using System.Runtime.InteropServices;
using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

unsafe partial class GCHeap
{
    private void MarkPhase()
    {
        // TODO: Check what need to be set on ScanContext
        ScanContext scanContext = default;
        scanContext.promotion = true;
        scanContext._unused1 = GCHandle.ToIntPtr(_handle);

        Write("Scan roots");
        var scanRootsCallback = (delegate* unmanaged<GCObject**, ScanContext*, uint, void>)&ScanRootsCallback;
        _gcToClr.GcScanRoots((IntPtr)scanRootsCallback, 2, 2, &scanContext);

        // Objects queued for the finalizer thread by earlier collections are strong roots
        // from the start of the mark phase — before handle scanning and weak clearing, like
        // the stock order (mark_phase.cpp:3176; missing-features 3.3)
        MarkFReachableQueues();

        ScanHandles();
        ScanDependentHandles();
        ClearHandles([HandleType.HNDTYPE_WEAK_SHORT]);
        ScanForFinalization();
        ScanDependentHandles();
        ClearHandles([HandleType.HNDTYPE_WEAK_LONG, HandleType.HNDTYPE_DEPENDENT, HandleType.HNDTYPE_WEAK_INTERIOR_POINTER]);

        var weakPtrScanCallback = (delegate* unmanaged<GCObject**, nint, nint, nint, void>)&WeakPtrScanCallback;
        _gcToClr.SyncBlockCacheWeakPtrScan(weakPtrScanCallback, GCHandle.ToIntPtr(_handle), 0);
    }

    [UnmanagedCallersOnly]
    private static void WeakPtrScanCallback(GCObject** obj, nint extraInfo, nint lp1, nint lp2)
    {
        var gcHeap = (GCHeap)GCHandle.FromIntPtr(lp1).Target!;

        var o = *obj;

        if (!gcHeap._nativeAllocator.IsInRange((nint)o)) // Ignore objects in frozen segments as they don't get marked
        {
            return;
        }

        if (!o->IsMarked())
        {
            *obj = null;
        }
    }

    [UnmanagedCallersOnly]
    private static void ScanRootsCallback(GCObject** obj, ScanContext* context, uint flags)
    {
        var handle = GCHandle.FromIntPtr(context->_unused1);
        var gcHeap = (GCHeap)handle.Target!;
        gcHeap.ScanRoots(*obj, context, (GcCallFlags)flags);
    }

    private void ScanForFinalization()
    {
        PrepareForFinalization();

        // Mark the newly dead objects just moved to the f-reachable queues (resurrection);
        // the leftovers from previous collections were already marked at the top of the
        // mark phase, so re-walking them here is a cheap no-op
        MarkFReachableQueues();
    }

    private void MarkFReachableQueues()
    {
        ScanContext scanContext = default;

        foreach (GCObject* obj in _freachableQueue)
        {
            ScanRoots(obj, &scanContext, default);
        }

        foreach (GCObject* obj in _criticalFreachableQueue)
        {
            ScanRoots(obj, &scanContext, default);
        }
    }

    private void ScanHandles()
    {
        ScanContext scanContext = default;

        foreach (var handle in _gcHandleManager.Store.EnumerateHandlesOfType([HandleType.HNDTYPE_STRONG, HandleType.HNDTYPE_PINNED]))
        {
            var obj = handle->Object;
            if (obj != null)
            {
                ScanRoots(obj, &scanContext, default);
            }
        }
    }

    private void ScanDependentHandles()
    {
        bool markedObjects;
        ScanContext scanContext = default;

        do
        {
            markedObjects = false;

            foreach (var handle in _gcHandleManager.Store.EnumerateHandlesOfType([HandleType.HNDTYPE_DEPENDENT]))
            {
                // Target: primary
                // Dependent: secondary
                var primary = handle->Object;
                var secondary = (GCObject*)handle->ExtraInfo;

                if (primary == null || secondary == null)
                {
                    continue;
                }

                // Anything outside the GC heap (frozen segments) counts as always-alive, and
                // an out-of-range secondary must never count as "needs promotion" or the
                // fixpoint spins forever (missing-features 3.4; stock interface.cpp:783)
                var primaryAlive = primary->IsMarked() || !_nativeAllocator.IsInRange((nint)primary);
                var secondaryNeedsMark = _nativeAllocator.IsInRange((nint)secondary) && !secondary->IsMarked();

                if (primaryAlive && secondaryNeedsMark)
                {
                    ScanRoots(secondary, &scanContext, default);
                    markedObjects = true;
                }
            }
        }
        while (markedObjects);
    }

    private void ScanRoots(GCObject* root, ScanContext* context, GcCallFlags flags)
    {
        if ((IntPtr)root == 0)
        {
            return;
        }

        if (!_nativeAllocator.IsInRange((IntPtr)root))
        {
            return;
        }
        
        if (flags.HasFlag(GcCallFlags.GC_CALL_INTERIOR))
        {
            root = ResolveInteriorPointer((nint)root);

            if (root == null)
            {
                return;
            }
        }

        _markStack.Push((nint)root);

        while (!_markStack.IsEmpty)
        {
            var ptr = _markStack.Pop();
            var o = (GCObject*)ptr;

            if (o->IsMarked())
            {
                continue;
            }

            // Reject anything that isn't inside a live region (frozen-segment references
            // land here too: they are outside the reservation and are deliberately skipped)
            if (!_regionAllocator.TryGetIndex(ptr, out var regionIndex))
            {
                continue;
            }

            var entry = _regionAllocator.GetEntry(regionIndex);

            if (entry->Kind == RegionKind.Free)
            {
                continue;
            }

            if (entry->Kind == RegionKind.SpanExtension)
            {
                entry = _regionAllocator.GetEntry(entry->SpanStartIndex);
            }

            o->EnumerateObjectReferences(_markStack);
            o->Mark();

            entry->LiveBytes = (int)Math.Min(int.MaxValue, entry->LiveBytes + (long)Align((nint)o->ComputeSize()));
        }
    }

    /// <summary>
    /// Maps an interior pointer to its containing object (contract: obj ≤ ptr < obj + size),
    /// or null when it doesn't point inside a live heap object (SPEC-M2 §6).
    /// </summary>
    private GCObject* ResolveInteriorPointer(nint addr)
    {
        if (!_regionAllocator.TryGetIndex(addr, out var index))
        {
            return null;
        }

        var entry = _regionAllocator.GetEntry(index);

        if (entry->Kind == RegionKind.SpanExtension)
        {
            index = entry->SpanStartIndex;
            entry = _regionAllocator.GetEntry(index);
        }

        GCObject* result = null;

        switch (entry->Kind)
        {
            case RegionKind.SpanStart:
            {
                var obj = (GCObject*)(_regionAllocator.RegionBase(index) + IntPtr.Size);

                if (addr >= (nint)obj && addr < (nint)obj + (nint)obj->ComputeSize())
                {
                    result = obj;
                }

                break;
            }

            case RegionKind.SizeClass:
            {
                // O(1): block index arithmetic + allocated-bit check (SPEC-M2 §6)
                var classSize = Region.ClassSizes[entry->SizeClass];
                var offset = addr - _regionAllocator.RegionBase(index);
                var block = (int)(offset / classSize);

                if (block < RegionAllocator.BlockCount(entry->SizeClass)
                    && (entry->AllocatedBlocks & (1ul << block)) != 0)
                {
                    var obj = (GCObject*)(_regionAllocator.RegionBase(index) + (nint)block * classSize + IntPtr.Size);

                    if (addr >= (nint)obj && addr < (nint)obj + (nint)obj->ComputeSize())
                    {
                        result = obj;
                    }
                }

                break;
            }

            case RegionKind.Bump:
            {
                // Linear walk from the region base; bounded by the 2 MB region size.
                // No brick table by design — see SPEC-M2 §6 for why bricks go stale.
                var ptr = _regionAllocator.RegionBase(index) + IntPtr.Size;
                var end = entry->Cursor;

                while (ptr < end && ptr <= addr)
                {
                    var obj = (GCObject*)ptr;
                    var size = (nint)obj->ComputeSize();

                    if (addr < ptr + size)
                    {
                        result = obj;
                        break;
                    }

                    ptr = Align(ptr + size);
                }

                break;
            }
        }

        // A pointer into dead space resolves to a free-object plug: not a live object
        // (conservative-mode hardening, missing-features 9.1)
        if (result != null && result->MethodTable == _freeObjectMethodTable)
        {
            return null;
        }

        return result;
    }
}
