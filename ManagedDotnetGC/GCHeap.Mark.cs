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

        // TODO: handles are roots too
        // TODO: Weak references (+ short/long weak refs)
        // TODO: ScanForFinalization
        // TODO: SyncBlockCache

        // Order in real GC:
        // Dependent handles
        // Short weak refs
        // ScanForFinalization
        // Long weak refs

        ScanHandles();
        ScanDependentHandles();
    }

    [UnmanagedCallersOnly]
    private static void ScanRootsCallback(GCObject** obj, ScanContext* context, uint flags)
    {
        var handle = GCHandle.FromIntPtr(context->_unused1);
        var gcHeap = (GCHeap)handle.Target!;
        gcHeap.ScanRoots(*obj, context, (GcCallFlags)flags);
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

    private void ScanRoots(GCObject* obj, ScanContext* context, GcCallFlags flags)
    {
        if ((IntPtr)obj == 0)
        {
            return;
        }

        if (flags.HasFlag(GcCallFlags.GC_CALL_INTERIOR))
        {
            // Find the segment containing the interior pointer
            var segment = FindSegmentContaining((IntPtr)obj);

            if (segment == null)
            {
                Write($"  No segment found for interior pointer {(IntPtr)obj:x2}");
                return;
            }

            var objectStartPtr = segment.FindClosestObjectBelow((IntPtr)obj);

            foreach (var ptr in WalkHeapObjects(objectStartPtr - IntPtr.Size, (IntPtr)obj))
            {
                var o = (GCObject*)ptr;
                var size = o->ComputeSize();

                if ((IntPtr)o <= (IntPtr)obj && (IntPtr)obj < (IntPtr)o + (nint)size)
                {
                    obj = o;
                    goto found;
                }
            }

            Write($"  No object found for interior pointer {(IntPtr)obj:x2}");
            return;

        found:
            ;
        }

        _markStack.Push((IntPtr)obj);

        while (_markStack.Count > 0)
        {
            var ptr = _markStack.Pop();
            var o = (GCObject*)ptr;

            if (o->IsMarked())
            {
                continue;
            }

            o->EnumerateObjectReferences(_markStack.Push);
            o->Mark();
        }
    }
}
