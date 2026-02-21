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

        // TODO: SyncBlockCache

        ScanHandles();
        ScanDependentHandles();
        ClearHandles([HandleType.HNDTYPE_WEAK_SHORT]);
        ScanForFinalization();
        ScanDependentHandles();
        ClearHandles([HandleType.HNDTYPE_WEAK_LONG, HandleType.HNDTYPE_DEPENDENT]);
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

        ScanContext scanContext = default;

        foreach (GCObject* obj in _freachableQueue)
        {
            ScanRoots(obj, &scanContext, default);
        }

        foreach (GCObject* obj in _critialFreachableQueue)
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

                if (primary->IsMarked() && !secondary->IsMarked())
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
            // Find the segment containing the interior pointer
            var segment = _segmentManager.FindSegmentContaining((nint)root);

            if (segment.IsNull)
            {
                Write($"  No segment found for interior pointer {(IntPtr)root:x2}");
                return;
            }

            var objectStartPtr = segment.FindClosestObjectBelow((IntPtr)root);

            bool found = false;

            foreach (var ptr in WalkHeapObjects(objectStartPtr, (IntPtr)root))
            {
                var o = (GCObject*)ptr;
                var size = o->ComputeSize();

                if ((IntPtr)o <= (IntPtr)root && (IntPtr)root < (IntPtr)o + (nint)size)
                {
                    root = o;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                Write($"  No object found for interior pointer {(IntPtr)root:x2}");
                return;
            }
        }

        _markStack.Push((IntPtr)root);

        while (_markStack.Count > 0)
        {
            var ptr = _markStack.Pop();
            var o = (GCObject*)ptr;

            if (o->IsMarked())
            {
                continue;
            }

            var segment = _segmentManager.FindSegmentContaining((nint)o);

            if (segment.IsNull)
            {
                continue;
            }

            o->EnumerateObjectReferences(_markStack.Push);
            o->Mark();
            segment.MarkObject((IntPtr)o);
        }
    }
}
