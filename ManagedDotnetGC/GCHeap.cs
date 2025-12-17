using ManagedDotnetGC.Dac;
using NativeObjects;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

internal unsafe partial class GCHeap : Interfaces.IGCHeap
{
    private const int AllocationContextSize = 32 * 1024;
    private const int SegmentSize = AllocationContextSize * 128;
    private static int SizeOfObject = sizeof(nint) * 3;

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
        Write("GarbageCollect");

        _gcToClr.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);

        _gcHandleManager.Store.DumpHandles(_dacManager);

        var callback = (delegate* unmanaged<gc_alloc_context*, IntPtr, void>)&EnumAllocContextCallback;
        _gcToClr.GcEnumAllocContexts((IntPtr)callback, GCHandle.ToIntPtr(_handle));

        ScanContext scanContext = default;
        scanContext._unused1 = GCHandle.ToIntPtr(_handle);

        var scanRootsCallback = (delegate* unmanaged<GCObject**, ScanContext*, uint, void>)&ScanRootsCallback;
        _gcToClr.GcScanRoots((IntPtr)scanRootsCallback, 2, 2, &scanContext);

        TraverseHeap();

        _gcToClr.RestartEE(finishedGC: true);

        return HResult.S_OK;
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

            return (GCObject*)(segment.Start + IntPtr.Size);
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

            acontext.alloc_ptr = result + size;
            acontext.alloc_limit = _activeSegment.Current - IntPtr.Size * 2;

            return (GCObject*)result;
        }
    }

    [UnmanagedCallersOnly]
    private static void ScanRootsCallback(GCObject** obj, ScanContext* context, uint flags)
    {
        var handle = GCHandle.FromIntPtr(context->_unused1);
        var gcHeap = (GCHeap)handle.Target!;
        gcHeap.ScanRoots(*obj, context, (GcCallFlags)flags);
        // TODO: handles are roots too
        // TODO: dependent handles
    }

    [UnmanagedCallersOnly]
    private static void EnumAllocContextCallback(gc_alloc_context* acontext, IntPtr arg)
    {
        var handle = GCHandle.FromIntPtr(arg);
        var gcHeap = (GCHeap)handle.Target!;
        gcHeap.FixAllocContext(ref Unsafe.AsRef<gc_alloc_context>(acontext));
    }

    private void ScanRoots(GCObject* obj, ScanContext* context, GcCallFlags flags)
    {
        if (flags.HasFlag(GcCallFlags.GC_CALL_INTERIOR))
        {
            // TODO
            Write($"Interior: {(IntPtr)obj:x2}");
            return;
        }

        _markStack.Push((IntPtr)obj);

        while (_markStack.Count > 0)
        {
            var ptr = _markStack.Pop();
            var o = (GCObject*)ptr;

            if (ptr == IntPtr.Zero || o->IsMarked())
            {
                continue;
            }

            o->EnumerateObjectReferences(_markStack.Push);

            Write($"Marked: {ptr:x2} - {_dacManager?.GetObjectName(new(ptr))}");
            o->Mark();
        }
    }

    private void FixAllocContext(ref gc_alloc_context acontext)
    {
        if (acontext.alloc_ptr == 0)
        {
            return;
        }

        AllocateFreeObject(acontext.alloc_ptr, (uint)(acontext.alloc_limit - acontext.alloc_ptr));
    }

    private void AllocateFreeObject(nint address, uint length)
    {
        var freeObject = (GCObject*)address;
        freeObject->MethodTable = _freeObjectMethodTable;
        freeObject->Length = length;
    }

    private void TraverseHeap()
    {
        foreach (var segment in _segments)
        {
            TraverseHeap(segment.Start, segment.Current);
        }
    }

    private void TraverseHeap(nint start, nint end)
    {
        var ptr = start + IntPtr.Size;

        while (ptr < end)
        {
            var obj = (GCObject*)ptr;

            bool marked = obj->IsMarked();
            obj->Unmark();

            var name = obj->MethodTable == _freeObjectMethodTable
                ? "Free"
                : _dacManager?.GetObjectName(new(ptr));

            Write($"{ptr:x2} - {name.PadRight(50)} {(marked ? "(*** marked ***)" : "")}");

            var alignment = sizeof(nint) - 1;
            ptr += ((nint)obj->ComputeSize() + alignment) & ~alignment;
        }
    }
}
