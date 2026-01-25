using ManagedDotnetGC.Dac;
using NativeObjects;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

internal unsafe partial class GCHeap : Interfaces.IGCHeap
{
    internal const int AllocationContextSize = 32 * 1024;
    internal const int SegmentSize = AllocationContextSize * 128;
    internal static readonly int SizeOfObject = sizeof(nint) * 3;

    private IGCToCLRInvoker _gcToClr;
    private readonly GCHandleManager _gcHandleManager;

    private readonly IGCHeap _nativeObject;
    private DacManager? _dacManager;

    private MethodTable* _freeObjectMethodTable;

    private List<Segment> _segments = new();
    private Segment _activeSegment;

    private GCHandle _handle;
    private Stack<IntPtr> _markStack = new();

    public GCHeap(IGCToCLRInvoker gcToClr)
    {
        _handle = GCHandle.Alloc(this);
        _gcToClr = gcToClr;
        _gcHandleManager = new GCHandleManager();

        _nativeObject = IGCHeap.Wrap(this);
        _freeObjectMethodTable = (MethodTable*)gcToClr.GetFreeObjectMethodTable();
        Write($"Free Object Method Table: {(nint)_freeObjectMethodTable:x2}");
    }

    public IntPtr IGCHeapObject => _nativeObject;
    public IntPtr IGCHandleManagerObject => _gcHandleManager.IGCHandleManagerObject;

    public HResult Initialize()
    {
        Write("Initialize GCHeap");

        if (DacManager.TryLoad(out var dacManager))
        {
            _dacManager = dacManager;
        }

        _activeSegment = new(SegmentSize);
        _segments.Add(_activeSegment);

        var parameters = new WriteBarrierParameters
        {
            operation = WriteBarrierOp.Initialize,
            is_runtime_suspended = true,
            ephemeral_low = -1
        };

        _gcToClr.StompWriteBarrier(&parameters);

        return HResult.S_OK;
    }

    public HResult GarbageCollect(int generation, bool low_memory_p, int mode)
    {
        Write($"GarbageCollect({generation}, {low_memory_p}, {mode})");

        _gcToClr.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);

        FixAllocContexts();

        Write("Mark phase");
        MarkPhase();

        Write("Sweep phase");
        SweepPhase();

        // DumpHeap();

        // TODO: when to call?
        // _gcToClr.EnableFinalization(true);

        _gcToClr.RestartEE(finishedGC: true);

        return HResult.S_OK;
    }

    private void SweepPhase()
    {
        Write("Updating weak references");
        UpdateWeakReferences();
        Sweep();
    }

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
        // TODO: dependent handles
        // TODO: Weak references (+ short/long weak refs)
        // TODO: ScanForFinalization
        // TODO: SyncBlockCache

        // Order in real GC:
        // Dependent handles
        // Short weak refs
        // ScanForFinalization
        // Long weak refs

        ScanHandles();
    }

    public void FixAllocContext(gc_alloc_context* acontext, void* arg, void* heap)
    {
        FixAllocContext(ref Unsafe.AsRef<gc_alloc_context>(acontext));
    }

    public bool IsThreadUsingAllocationContextHeap(gc_alloc_context* acontext, int thread_number)
    {
        return true;
    }

    public GCObject* Alloc(ref gc_alloc_context acontext, nint size, GC_ALLOC_FLAGS flags)
    {
        var result = acontext.alloc_ptr;
        var advance = result + size;

        // TODO: Add object to finalization queue if needed
        // TODO: How to recognize critical finalizers?

        if (advance <= acontext.alloc_limit)
        {
            // There is enough room left in the allocation context
            acontext.alloc_ptr = advance;
            return (GCObject*)result;
        }

        // We need to allocate a new allocation context
        FixAllocContext(ref acontext);

        var minimumSize = size + SizeOfObject;

        if (minimumSize > SegmentSize)
        {
            // We need a dedicated segment for this allocation
            var segment = new Segment(size);
            segment.Current = segment.End;

            lock (_segments)
            {
                _segments.Add(segment);
            }

            acontext.alloc_ptr = 0;
            acontext.alloc_limit = 0;

            result = Align(segment.Start + IntPtr.Size);

            segment.MarkObject(result);

            return (GCObject*)result;
        }

        lock (_segments)
        {
            if (_activeSegment.Current + minimumSize >= _activeSegment.End)
            {
                // The active segment is full, allocate a new one
                _activeSegment = new Segment(SegmentSize);
                _segments.Add(_activeSegment);
            }

            var desiredSize = Math.Min(Math.Max(minimumSize, AllocationContextSize), _activeSegment.End - _activeSegment.Current);

            result = _activeSegment.Current + IntPtr.Size;
            _activeSegment.Current += desiredSize;

            acontext.alloc_ptr = Align(result + size);
            acontext.alloc_limit = _activeSegment.Current - IntPtr.Size * 2;

            _activeSegment.MarkObject(result);

            return (GCObject*)result;
        }
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
        foreach (var handle in _gcHandleManager.Store.AsSpan())
        {
            if (handle.Type < HandleType.HNDTYPE_STRONG)
            {
                continue;
            }

            var obj = (GCObject*)handle.Object;
            if (obj != null)
            {
                ScanRoots(obj, null, default);
            }
        }
    }

    private Segment? FindSegmentContaining(IntPtr addr)
    {
        for (int i = 0; i < _segments.Count; i++)
        {
            var segment = _segments[i];

            if (addr >= segment.Start && addr < segment.End)
            {
                return segment;
            }
        }

        return null;
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

    private void UpdateWeakReferences()
    {
        // TODO: Handle long weak references

        var span = _gcHandleManager.Store.AsSpan();

        for (int i = 0; i < span.Length; i++)
        {
            ref var handle = ref span[i];

            if (handle.Type >= HandleType.HNDTYPE_STRONG)
            {
                continue;
            }

            var obj = (GCObject*)handle.Object;
            if (obj != null && !obj->IsMarked())
            {
                handle.Object = IntPtr.Zero;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Align(nint address) => (address + (IntPtr.Size - 1)) & ~(IntPtr.Size - 1);

    [UnmanagedCallersOnly]
    private static void FixAllocContextCallback(gc_alloc_context* acontext, IntPtr arg)
    {
        var handle = GCHandle.FromIntPtr(arg);
        var gcHeap = (GCHeap)handle.Target!;
        gcHeap.FixAllocContext(ref Unsafe.AsRef<gc_alloc_context>(acontext));
    }

    private void FixAllocContexts()
    {
        var callback = (delegate* unmanaged<gc_alloc_context*, IntPtr, void>)&FixAllocContextCallback;
        _gcToClr.GcEnumAllocContexts((IntPtr)callback, GCHandle.ToIntPtr(_handle));
    }

    private void FixAllocContext(ref gc_alloc_context acontext)
    {
        if (acontext.alloc_ptr == 0)
        {
            return;
        }

        AllocateFreeObject(acontext.alloc_ptr, (uint)(acontext.alloc_limit - acontext.alloc_ptr));

        // Invalidate the allocation context so threads get a fresh one
        acontext.alloc_ptr = 0;
        acontext.alloc_limit = 0;
    }

    private void AllocateFreeObject(nint address, uint length)
    {
        var freeObject = (GCObject*)address;
        freeObject->RawMethodTable = _freeObjectMethodTable;
        freeObject->Length = length;
    }

    private IEnumerable<IntPtr> WalkHeapObjects()
    {
        foreach (var segment in _segments)
        {
            foreach (var obj in WalkHeapObjects(segment.Start, segment.Current))
            {
                yield return obj;
            }
        }
    }

    private IEnumerable<IntPtr> WalkHeapObjects(nint start, nint end)
    {
        var ptr = start + IntPtr.Size;

        while (ptr < end)
        {
            yield return ptr;
            ptr = FindNextObject(ptr);
        }

        static unsafe nint FindNextObject(nint current)
        {
            var obj = (GCObject*)current;
            return Align(current + (nint)obj->ComputeSize());
        }
    }

    private void DumpHeap()
    {
        foreach (var ptr in WalkHeapObjects())
        {
            var obj = (GCObject*)ptr;
            bool isFreeObject = obj->MethodTable == _freeObjectMethodTable;

            var name = isFreeObject ? "Free" : _dacManager?.GetObjectName(new(ptr));

            Write($"{ptr:x2} - {name?.PadRight(50)}");
        }
    }

    private void Sweep()
    {
        foreach (var ptr in WalkHeapObjects())
        {
            var obj = (GCObject*)ptr;

            bool marked = obj->IsMarked();
            obj->Unmark();

            bool isFreeObject = obj->MethodTable == _freeObjectMethodTable;

            if (!marked && !isFreeObject)
            {
                var startPtr = ptr - IntPtr.Size; // Include the header
                var endPtr = Align(startPtr + (nint)obj->ComputeSize());

                // Clear the memory
                new Span<byte>((void*)startPtr, (int)(endPtr - startPtr)).Clear();

                // Allocate a free object to keep the heap walkable
                AllocateFreeObject(ptr, (uint)(endPtr - startPtr - SizeOfObject));
            }
        }
    }
}
