﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using Reloaded.Memory.Buffers.Structs;
using Reloaded.Memory.Buffers.Utilities;
using Reloaded.Memory.Sources;
using Vanara.PInvoke;
using static Reloaded.Memory.Buffers.Utilities.VirtualQueryUtility;

namespace Reloaded.Memory.Buffers
{
    /// <summary>
    /// Provides a buffer for permanent (until the process dies) general small size memory storage, reusable 
    /// concurrently between different DLLs within the same process.
    /// </summary>
    public unsafe class MemoryBuffer
    {
        /* === Internal & Testing Use Only === */

        /// <summary> Calculates the overhead of each individual buffer.</summary>
        internal static int BufferOverhead => sizeof(MemoryBufferHeader) + sizeof(MemoryBufferMagic);

        /// <summary> The address where the individual buffer has been allocated. </summary>
        internal IntPtr AllocationAddress  => _headerAddress - sizeof(MemoryBufferMagic);

        /// <summary> Used to ensure only one thread in the current application can access buffer at once. </summary>
        private readonly object _threadLock = new object();

        /// <summary> Pregenerated byte sequence used to identify a Reloaded buffer. </summary>
        private static MemoryBufferMagic _bufferMagic = new MemoryBufferMagic(true); 

        /* === Rest of class. === */

        /// <summary> Defines where Memory will be read in or written to. </summary>
        public IMemory MemorySource   { get; private set; }

        /// <summary> Gets/Sets the header of the buffer which defines various properties of the current buffer. </summary>
        public MemoryBufferHeader BufferHeader
        {
            get { MemorySource.SafeRead(_headerAddress, out MemoryBufferHeader bufferHeader);
                  return bufferHeader; }
            set => MemorySource.Write(_headerAddress, ref value);
        }

        /// <summary> Stores the location of the <see cref="MemoryBufferHeader"/> structure. </summary>
        private readonly IntPtr _headerAddress;

        /*
            --------------
            Constructor(s)
            --------------
        */

        /// <summary>
        /// Creates a new buffer in a specified location in memory with a specified size.
        /// </summary>
        /// <param name="process">The process inside which the <see cref="MemoryBuffer"/> will be allocated.</param>
        /// <param name="bufferAddress">Specifies the base address of the new buffer to be created. The "magic" will be written there and buffer right after.</param>
        /// <param name="bufferSize">The size of the buffer to be created. Note that the buffer size includes the buffer header!</param>
        /// <param name="isBufferPreallocated">Set this to true if the position where the buffer is being created has already been allocated.</param>
        /// <remarks>
        /// This constructor will override any existing buffer! Please use <see cref="FromAddress"/> instead
        /// if you wish to get a hold of an already existing buffer at the given <see cref="bufferAddress"/>.
        /// </remarks>
        public unsafe MemoryBuffer(Process process, IntPtr bufferAddress, int bufferSize, bool isBufferPreallocated = false)
        {
            if (!isBufferPreallocated)
            {
                // Get the function, commit the pages and check.
                var virtualAllocFunction = VirtualAllocUtility.GetVirtualAllocFunction(process);
                var address = virtualAllocFunction(process.Handle, bufferAddress, (uint)bufferSize);

                if (address == IntPtr.Zero)
                    throw new Exception($"Failed to allocate MemoryBuffer of size {bufferSize} at address {bufferAddress.ToString("X")}. Last Win32 Error: {Marshal.GetLastWin32Error()}");
            }

            // Obtain the Memory Source for the given process and write the magic.
            MemorySource = GetMemorySource(process);
            MemorySource.Write(bufferAddress, ref _bufferMagic);

            // Setup buffer after Magic.
            _headerAddress  = bufferAddress + sizeof(MemoryBufferMagic);
            var dataPtr     = bufferAddress + BufferOverhead;
            var realBufSize = bufferSize - BufferOverhead; 

            BufferHeader   = new MemoryBufferHeader(dataPtr, realBufSize);
        }

        /// <summary>
        /// Creates a new <see cref="MemoryBuffer"/> given the address of the buffer and the header details.
        /// </summary>
        /// <param name="memorySource">Contains the source used for read/write of memory.</param>
        /// <param name="headerAddress">The address of the memory buffer; this follows the memory buffer "magic".</param>
        private MemoryBuffer(IMemory memorySource, IntPtr headerAddress)
        {
            _headerAddress = headerAddress;
            MemorySource   = memorySource;
        }

        /*
            -----------------
            Factory Method(s)
            -----------------
        */

        /// <summary>
        /// Attempts to find an existing <see cref="MemoryBuffer"/> at the specified address and returns an instance of it.
        /// If the operation fails; the function returns null.
        /// </summary>
        public static unsafe MemoryBuffer FromAddress(Process process, IntPtr bufferMagicAddress)
        {
            // Query the region we are going to create a buffer in.
            var virtualQueryFunction = GetVirtualQueryFunction(process);
            var memoryInformation = virtualQueryFunction(process.Handle, bufferMagicAddress);

            if (memoryInformation.State != (uint)Kernel32.MEM_ALLOCATION_TYPE.MEM_FREE)
            {
                if (IsBuffer(process, bufferMagicAddress))
                    return new MemoryBuffer(GetMemorySource(process), bufferMagicAddress + sizeof(MemoryBufferMagic));
            }

            return null;
        }

        /*
            -----------------
            Factory Helper(s)
            -----------------
        */

        /// <summary>
        /// Checks if a <see cref="MemoryBuffer"/> exists at this location by comparing the bytes available here
        /// against the MemoryBuffer "Magic".
        /// </summary>
        public static bool IsBuffer(Process process, IntPtr bufferMagicAddress)
        {
            try
            {
                GetMemorySource(process).SafeRead(bufferMagicAddress, out MemoryBufferMagic bufferMagic);
                
                if (_bufferMagic.MagicEquals(ref bufferMagic))
                    return true;
            }
            catch {  /* Suppress */ }

            return false;
        }

        /*
            --------------
            Core Functions
            --------------
        */

        /// <summary>
        /// Writes your own memory bytes into process' memory and gives you the address
        /// for the memory location of the written bytes.
        /// </summary>
        /// <param name="bytesToWrite">Individual bytes to be written onto the buffer.</param>
        /// <returns>Pointer to the passed in bytes written to memory. Null pointer, if it cannot fit into the buffer.</returns>
        public IntPtr Add(byte[] bytesToWrite)
        {
            /* A lock for the threads of the current application (DLL); just in case as extra backup. */
            lock (_threadLock)
            {
                /* The following is application (DLL) lock to ensure that various different modules
                   do not try reading/writing to the same buffer at once.
                */
                while (BufferHeader.State == MemoryBufferHeader.BufferState.Locked)
                    Thread.Sleep(1);

                // Lock the buffer and locally store header for modification.
                var bufferHeader = BufferHeader;

                /* Below we lock the buffer. Note that we cannot access the buffer by pointer as it
                   may be in another process (remember we are using arbitrary memory sources) */
                bufferHeader.Lock();
                BufferHeader = bufferHeader;

                // Check if item can fit in buffer and buffer address is valid.
                if (!CanItemFit(bytesToWrite.Length) || _headerAddress == IntPtr.Zero)
                {
                    bufferHeader.Unlock();
                    BufferHeader = bufferHeader;
                    return IntPtr.Zero;
                }

                // Append the item to the buffer.
                IntPtr appendAddress = bufferHeader.WritePointer;
                MemorySource.WriteRaw(appendAddress, bytesToWrite);
                bufferHeader.Offset += bytesToWrite.Length;

                // Re-align, unlock and write back to memory.
                bufferHeader.Align();
                bufferHeader.Unlock();
                BufferHeader = bufferHeader;

                return appendAddress;
            }
        }

        /// <summary>
        /// Writes your own structure address into process' memory and gives you the address 
        /// to which the structure has been directly written to.
        /// </summary>
        /// <param name="bytesToWrite">A structure to be converted into individual bytes to be written onto the buffer.</param>
        /// <param name="marshalElement">Set this to true to marshal the given parameter before writing it to the buffer, else false.</param>
        /// <returns>Pointer to the newly written structure in memory. Null pointer, if it cannot fit into the buffer.</returns>
        public IntPtr Add<TStructure>(ref TStructure bytesToWrite, bool marshalElement = false)
        {
            return Add(Struct.GetBytes(ref bytesToWrite, marshalElement));
        }

        /// <summary>
        /// Returns true if the object can fit into the buffer, else false.
        /// </summary>
        /// <param name="objectSize">The size of the object to be appended to the buffer.</param>
        /// <returns>Returns true if the object can fit into the buffer, else false.</returns>
        public bool CanItemFit(int objectSize)
        {
            // Check if base buffer uninitialized or if object size too big.
            return BufferHeader.Remaining >= objectSize;
        }

        /*
            --------------
            Misc Functions
            --------------
        */

        /// <summary/>
        public override bool Equals(object obj)
        {
            // The two <see cref="MemoryBuffer"/>s are equal if their base address is the same.
            var buffer = obj as MemoryBuffer;
            return buffer != null && _headerAddress == buffer._headerAddress;
        }

        /// <summary/>
        [ExcludeFromCodeCoverage]
        public override int GetHashCode()
        {
            return (int)_headerAddress;
        }

        /// <summary>
        /// Assigns a Memory Source to a buffer.
        /// </summary>
        /// <param name="process">The process where memory will be read/written to.</param>
        private static IMemory GetMemorySource(Process process)
        {
            if (process.Id == Process.GetCurrentProcess().Id)
                return new Sources.Memory();
            else
                return new ExternalMemory(process);
        }
    }
}
