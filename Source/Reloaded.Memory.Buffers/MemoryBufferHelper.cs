﻿using Reloaded.Memory.Buffers.Structs;
using Reloaded.Memory.Buffers.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Vanara.PInvoke;

namespace Reloaded.Memory.Buffers
{
    /// <summary>
    /// Provides a a way to detect individual Reloaded buffers inside a process used for general small size memory storage,
    /// adding buffer information within certain proximity of an address as well as other various utilities partaining to
    /// buffers.
    /// </summary>
    public class MemoryBufferHelper
    {
        /// <summary> Attempts to mitigate synchronization issues by locking the thread creating a new buffer. </summary>
        private readonly object _threadLock = new object();

        /// <summary> Contains the default size of memory pages to be allocated. </summary>
        internal const int DefaultPageSize = 0x1000;

        /// <summary> Implementation of the Searcher that scans and finds existing <see cref="MemoryBuffer"/>s within the current process. </summary>
        private MemoryBufferSearcher _bufferSearcher;

        /// <summary> The process on which the MemoryBuffer acts upon. </summary>
        public Process Process { get; private set; }

        /// <summary>
        /// Creates a new <see cref="MemoryBufferHelper"/> for the specified process.
        /// </summary>
        /// <param name="process">The process.</param>
        public MemoryBufferHelper(Process process)
        {
            Process = process;
            _bufferSearcher = new MemoryBufferSearcher(process);
        }

        /*
            -----------------------
            Memory Buffer Factories
            -----------------------
        */

        /// <summary>
        /// Finds an appropriate location where a <see cref="MemoryBuffer"/>;
        /// or other memory allocation could be performed.
        /// </summary>
        /// <param name = "size" > The space in bytes that the specific <see cref="MemoryBuffer"/> would require to accomodate.</param>
        /// <param name="minimumAddress">The minimum absolute address to find a buffer in.</param>
        /// <param name="maximumAddress">The maximum absolute address to find a buffer in.</param>
        public BufferAllocationProperties FindBufferLocation(int size, IntPtr minimumAddress, IntPtr maximumAddress)
        {
            var memoryPages = MemoryPages.GetPages(Process);
            int bufferSize  = GetBufferSize(size);

            // Find a free page that satisfies the necessary properties to create a new buffer there.
            foreach (var page in memoryPages)
            {
                var pointer = GetBufferPointerInPageRange(page, bufferSize, minimumAddress, maximumAddress);
                if (pointer != IntPtr.Zero)
                    return new BufferAllocationProperties(pointer, bufferSize);
            }

            throw new Exception($"Unable to find memory location to fit MemoryBuffer of size {size} ({bufferSize}) between {minimumAddress.ToString("X")} and {maximumAddress.ToString("X")}.");
        }

        /// <summary>
        /// Creates a <see cref="MemoryBuffer"/> that satisfies a set size constraint
        /// and proximity to a set address.
        /// </summary>
        /// <param name="size">The minimum size the <see cref="MemoryBuffer"/> will have to accomodate.</param>
        /// <param name="minimumAddress">The minimum absolute address to create a buffer in.</param>
        /// <param name="maximumAddress">The maximum absolute address to create a buffer in.</param>
        /// <param name="retryCount">In the case the memory allocation fails; the amount of times memory allocation is to be retried.</param>
        /// <exception cref="System.Exception">Memory allocation failure due to possible race condition with other process/process itself/Windows scheduling.</exception>
        public MemoryBuffer CreateMemoryBuffer(int size, long minimumAddress = 0, long maximumAddress = long.MaxValue, int retryCount = 3)
        {
            // Keep retrying memory allocation.
            lock (_threadLock)
            {
                Exception caughtException = new Exception("Temp Exception in CreateMemoryBuffer(). This should not throw.");
                for (int retries = 0; retries < retryCount; retries++)
                {
                    try
                    {
                        var bufferProperties = FindBufferLocation(size, (IntPtr) minimumAddress, (IntPtr) maximumAddress);
                        return new MemoryBuffer(Process, bufferProperties.MemoryAddress, bufferProperties.Size);
                    }
                    catch (Exception ex) { caughtException = ex; }
                }

                throw caughtException;
            }
        }        

        /*
            -------------------
            Core Helper Methods
            -------------------
        */

        /// <summary>
        /// Returns a list of already known buffers that satisfy size requirements.
        /// </summary>
        /// <param name="size">The amount of bytes a buffer must have minimum.</param>
        /// <param name="useCache">See <see cref="MemoryBufferSearcher.GetBuffers"/></param>
        /// <returns></returns>
        public MemoryBuffer[] GetBuffers(int size, bool useCache = true)
        {
            // Get buffers already existing in process.
            return _bufferSearcher.GetBuffers(size, useCache);
        }

        /// <summary>
        /// Returns a list of already known buffers that satisfy a given size requirement 
        /// within a given range of memory addresses.
        /// </summary>
        /// <param name="size">The amount of bytes a buffer must have minimum.</param>
        /// <param name="minimumAddress">The maximum pointer a <see cref="MemoryBuffer"/> can occupy.</param>
        /// <param name="maximumAddress">The minimum pointer a <see cref="MemoryBuffer"/> can occupy.</param>
        /// <param name="useCache">See <see cref="MemoryBufferSearcher.GetBuffers"/></param>
        /// <returns></returns>
        public MemoryBuffer[] GetBuffers(int size, IntPtr minimumAddress, IntPtr maximumAddress, bool useCache = true)
        {
            // Get buffers already existing in process.
            var buffers = _bufferSearcher.GetBuffers(size, useCache);

            // Get all MemoryBuffers where their raw data range fits into the given minimum and maximum address.
            AddressRange allowedRange = new AddressRange((long) minimumAddress, (long) maximumAddress);
            var memoryBuffers = new List<MemoryBuffer>(buffers.Length);

            foreach (var buffer in buffers)
            {
                var bufferHeader = buffer.BufferHeader;
                var bufferAddressRange = new AddressRange((long)bufferHeader.DataPointer, (long)(bufferHeader.DataPointer + bufferHeader.Size));
                if (allowedRange.Contains(ref bufferAddressRange))
                    memoryBuffers.Add(buffer);
            }

            return memoryBuffers.ToArray();
        }

        /*
            -----------------------
            Internal Helper Methods
            -----------------------
        */

        /// <summary>
        /// Calculates the size of a <see cref="MemoryBuffer"/> to be created for a given requested size,
        /// accounting of overhead, that is a rounded multiple of the minimum allocation granularity.
        /// </summary>
        /// <param name="size">The size of the buffer to be allocated.</param>
        /// <returns>A calculated buffer size based off of the requested capacity in bytes.</returns>
        public int GetBufferSize(int size)
        {
            // Get size of buffer; allocation granularity or larger if greater than the granularity.
            Kernel32.GetSystemInfo(out var systemInfo);

            // Guard to ensure that page size is at least the minimum supported by the processor
            // While Reloaded is only intended for X86/64; this may be useful in the future.
            // The second guard ensured the default page size is aligned with the system info.
            int pageSize = DefaultPageSize;
            if (systemInfo.dwPageSize > pageSize || (pageSize % systemInfo.dwPageSize != 0))
                pageSize = (int)systemInfo.dwPageSize;

            return Mathematics.RoundUp(size + MemoryBuffer.BufferOverhead, pageSize);
        }

        /// <summary>
        /// Checks if a buffer can be created within a given set of pages described by pageInfo
        /// satisfying the given size, minimum and maximum memory location.
        /// </summary>
        /// <param name="pageInfo">Contains the information about a singular memory page.</param>
        /// <param name="bufferSize">The size that a <see cref="MemoryBuffer"/> would occupy. Pre-aligned to page-size.</param>
        /// <param name="minimumPtr">The maximum pointer a <see cref="MemoryBuffer"/> can occupy.</param>
        /// <param name="maximumPtr">The minimum pointer a <see cref="MemoryBuffer"/> can occupy.</param>
        /// <returns>Zero if the operation fails; otherwise positive value.</returns>
        private IntPtr GetBufferPointerInPageRange(Kernel32.MEMORY_BASIC_INFORMATION pageInfo, int bufferSize, IntPtr minimumPtr, IntPtr maximumPtr)
        {
            // Fast return if page is not free.
            if (pageInfo.State != (uint)Kernel32.MEM_ALLOCATION_TYPE.MEM_FREE)
                return IntPtr.Zero;

            // This is valid in both 32bit and 64bit Windows.
            // We can call GetSystemInfo to get this but that's a waste; these are constant for x86 and x64.
            int allocationGranularity = 65536;

            // Do not align page start/end to allocation granularity yet.
            // Align it when we map the possible buffer ranges in the pages.
            long pageStart = (long)pageInfo.BaseAddress;
            long pageEnd   = (long)pageInfo.BaseAddress + (long)pageInfo.RegionSize.Value;

            // Get range for page and min-max region.
            var minMaxRange  = new AddressRange((long)minimumPtr, (long)maximumPtr);
            var pageRange    = new AddressRange(pageStart, pageEnd);

            if (! pageRange.Overlaps(ref minMaxRange))
                return IntPtr.Zero;

            /* Three possible cases here:
               1. Page fits entirely inside min-max range and is smaller.
               2. Min-max range is inside page (i.e. page is bigger than the range)
               3. Page and min-max intersect, e.g. first half of pages in end of min-max
                  or second half of pages in start of min-max.

               Below we will build a set of possible buffer allocation ranges
               and check if they satisfy our conditions.
            */

            // Order:
            // End of page range.
            // Start of page range.
            // Max ptr
            // Min ptr

            /* Try placing range at start and end of page boundaries.
               Since we are allocating in page boundaries, we must compare against Min-Max. */

            // Note: We are rounding page boundary addresses up/down, possibly beyond the original ends/starts of page.
            //       We need to validate that we are still in the bounds of the actual page itself.

            var allocPtrPageMaxAligned = Mathematics.RoundDown(pageRange.EndPointer - bufferSize, allocationGranularity);
            var allocRangePageMaxStart = new AddressRange(allocPtrPageMaxAligned, allocPtrPageMaxAligned + bufferSize);

            if (pageRange.Contains(ref allocRangePageMaxStart) && minMaxRange.Contains(ref allocRangePageMaxStart))
                return (IntPtr)allocRangePageMaxStart.StartPointer;

            var allocPtrPageMinAligned = Mathematics.RoundUp(pageRange.StartPointer, allocationGranularity);
            var allocRangePageMinStart = new AddressRange(allocPtrPageMinAligned, allocPtrPageMinAligned + bufferSize);

            if (pageRange.Contains(ref allocRangePageMinStart) && minMaxRange.Contains(ref allocRangePageMinStart))
                return (IntPtr)allocRangePageMinStart.StartPointer;

            /* Try placing range at start and end of given minimum-maximum.
               Since we are allocating in Min-Max, we must compare against Page Boundaries. */

            // Note: Remember that rounding is dangerous and could potentially cause max and min to cross as usual,
            //       must check proposed page range against both given min-max and page memory range.

            var allocPtrMaxAligned = Mathematics.RoundDown((long)maximumPtr - bufferSize, allocationGranularity);
            var allocRangeMaxStart = new AddressRange(allocPtrMaxAligned, allocPtrMaxAligned + bufferSize);

            if (pageRange.Contains(ref allocRangeMaxStart) && minMaxRange.Contains(ref allocRangeMaxStart))
                return (IntPtr)allocRangeMaxStart.StartPointer;

            var allocPtrMinAligned = Mathematics.RoundUp((long)minimumPtr, allocationGranularity);
            var allocRangeMinStart = new AddressRange(allocPtrMinAligned, allocPtrMinAligned + bufferSize);

            if (pageRange.Contains(ref allocRangeMinStart) && minMaxRange.Contains(ref allocRangeMinStart))
                return (IntPtr)allocRangeMinStart.StartPointer;

            return IntPtr.Zero;
        }
    }
}
