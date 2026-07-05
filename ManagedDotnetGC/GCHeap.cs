using ManagedDotnetGC.Dac;
using NativeObjects;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

internal unsafe partial class GCHeap : Interfaces.IGCHeap
{
    internal static readonly int SizeOfObject = sizeof(nint) * 3;

    private readonly ManualResetEventSlim _gcEvent = new(false);

    private IGCToCLRInvoker _gcToClr;
    private readonly GCHandleManager _gcHandleManager;

    private readonly IGCHeap _nativeObject;
    private DacManager? _dacManager;

    private MethodTable* _freeObjectMethodTable;

    private readonly RegionAllocator _regionAllocator;
    private readonly GcAwareLock _allocLock;
    private readonly GcAwareLock _gcLock;
    private long _allocatedSinceGC;

    private GCHandle _handle;
    private readonly MarkStack _markStack = new();
    private uint _currentEpoch = 1;

    private readonly NativeAllocator _nativeAllocator;

    public GCHeap(IGCToCLRInvoker gcToClr)
    {
        _handle = GCHandle.Alloc(this);
        _gcToClr = gcToClr;
        _gcLock = new GcAwareLock(gcToClr);
        _allocLock = new GcAwareLock(gcToClr);
        _gcHandleManager = new GCHandleManager();
        _nativeAllocator = new(Region.HeapReserveSize);
        _regionAllocator = new RegionAllocator(_nativeAllocator);

        _nativeObject = IGCHeap.Wrap(this);
        _freeObjectMethodTable = (MethodTable*)gcToClr.GetFreeObjectMethodTable();
        Write($"Free Object Method Table: {(nint)_freeObjectMethodTable:x2}");

        InitializeManagedApi();
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

        var parameters = new WriteBarrierParameters
        {
            operation = WriteBarrierOp.Initialize,
            is_runtime_suspended = true,
            ephemeral_low = -1
        };

        StompWriteBarrier(parameters);

        return HResult.S_OK;
    }

    public void Shutdown() => Write("Shutdown");

    public HResult GarbageCollect(int generation, bool low_memory_p, int mode)
    {
        Write($"GarbageCollect({generation}, {low_memory_p}, {mode})");

        // Explicit collections always collect (the caller is entitled to a collection that
        // starts after its call), so the snapshot is ignored via force
        Collect(_gcCount, force: true);

        return HResult.S_OK;
    }

    /// <summary>
    /// The single entry point for collections (SPEC-M2 §8.2). Safe to call from cooperative
    /// mode (allocation-triggered GCs): the preemptive switch happens before SuspendEE.
    /// When not forced, the collection is skipped if another thread completed one since the
    /// caller read <paramref name="gcCountSnapshot"/> — that is what stops racing allocators
    /// from running back-to-back collections.
    /// </summary>
    private void Collect(uint gcCountSnapshot, bool force = false)
    {
        // Switch before acquiring so the lock's own restore leaves us preemptive: calling
        // SuspendEE from cooperative mode would self-deadlock
        var wasCooperative = _gcToClr.EnablePreemptiveGC();

        _gcLock.Acquire();

        if (force || Volatile.Read(ref _gcCount) == gcCountSnapshot)
        {
            _gcToClr.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);

            AdvanceEpoch();

            FixAllocContexts();

            Write("Mark phase");
            MarkPhase();

            Write("Sweep phase");
            SweepPhase();

            // DumpHeap();

            Interlocked.Increment(ref _gcCount);

            _gcToClr.RestartEE(finishedGC: true);
            _gcToClr.EnableFinalization(GetNumberOfFinalizable() > 0);
        }

        _gcLock.Release();

        if (wasCooperative)
        {
            _gcToClr.DisablePreemptiveGC();
        }
    }

    public void SetWaitForGCEvent() => _gcEvent.Set();

    public void ResetWaitForGCEvent() => _gcEvent.Reset();

    public uint WaitUntilGCComplete(bool considerGCStart = false)
    {
        _gcEvent.Wait();
        return 0;
    }

    public int GetLOHCompactionMode() => 0;

    public void SetLOHCompactionMode(int newLOHCompactionMode)
    {
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
        // The EE only calls this when [alloc_ptr, alloc_limit) can't fit the request.
        // Dispatch per SPEC-M2 §4; note the size-class tier arrives with M2 step S4 —
        // until then, 32 KB < size ≤ 1 MB is served by oversized bump windows.
        var obj = size + IntPtr.Size <= Region.SizeClassMaxSize
            ? AllocFromWindow(ref acontext, size)
            : AllocSpan(ref acontext, size);

        if (obj != null && flags.HasFlag(GC_ALLOC_FLAGS.GC_ALLOC_FINALIZE))
        {
            RegisterForFinalization(0, obj);
        }

        return obj;
    }

    private GCObject* AllocFromWindow(ref gc_alloc_context acontext, nint size)
    {
        // Plug the context's remainder to keep the heap walkable before replacing it
        FixAllocContext(ref acontext);

        _allocLock.Acquire();

        try
        {
            if (!_regionAllocator.TryGetWindow(size, out var window, out var length))
            {
                return null;
            }

            // SPEC-M2 §3: first object ref at window + 8; the -16 pairs with the plug
            // formula in FixAllocContext to keep the region walkable end-to-end
            var result = window + IntPtr.Size;

            acontext.alloc_ptr = Align(result + size);
            acontext.alloc_limit = window + length - 2 * IntPtr.Size;
            acontext.alloc_bytes += length;

            _allocatedSinceGC += length;

            return (GCObject*)result;
        }
        finally
        {
            _allocLock.Release();
        }
    }

    private GCObject* AllocSpan(ref gc_alloc_context acontext, nint size)
    {
        // The caller's context is left untouched: it may still serve small allocations
        var regionCount = (int)((size + IntPtr.Size + Region.Size - 1) >> Region.Shift);

        _allocLock.Acquire();

        try
        {
            if (!_regionAllocator.TryAllocSpan(regionCount, out var spanBase))
            {
                return null;
            }

            var allocated = (long)regionCount << Region.Shift;
            acontext.alloc_bytes_uoh += allocated;
            _allocatedSinceGC += allocated;

            return (GCObject*)(spanBase + IntPtr.Size);
        }
        finally
        {
            _allocLock.Release();
        }
    }

    /// <summary>
    /// Starts a new mark epoch (SPEC-M2 §5). Runs under STW. On uint wrap, stale stamps
    /// from 2³² collections ago could alias the new epoch, so all stamps are cleared first.
    /// </summary>
    private void AdvanceEpoch()
    {
        _currentEpoch++;

        if (_currentEpoch == 0)
        {
            foreach (var ptr in WalkHeapObjects())
            {
                ((GCObject*)ptr)->Epoch = 0;
            }

            _currentEpoch = 1;
        }

        GCObject.CurrentEpoch = _currentEpoch;
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

        // Un-account the window remainder the context never consumed (SPEC-M2 §4.1)
        acontext.alloc_bytes -= acontext.alloc_limit - acontext.alloc_ptr;

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
        var count = _regionAllocator.CarvedCount;

        for (int i = 0; i < count; i++)
        {
            var (kind, start, end) = GetWalkRange(i);

            switch (kind)
            {
                case RegionKind.Bump:
                    foreach (var obj in WalkHeapObjects(start, end))
                    {
                        yield return obj;
                    }
                    break;

                case RegionKind.SpanStart:
                    yield return start;
                    break;
            }
        }
    }

    private (RegionKind kind, nint start, nint end) GetWalkRange(int index)
    {
        var entry = _regionAllocator.GetEntry(index);

        return entry->Kind switch
        {
            // Objects live in [base + 8, Cursor); the tail past Cursor is virgin zero
            RegionKind.Bump => (RegionKind.Bump, _regionAllocator.RegionBase(index) + IntPtr.Size, entry->Cursor),
            // A span holds exactly one object
            RegionKind.SpanStart => (RegionKind.SpanStart, _regionAllocator.RegionBase(index) + IntPtr.Size, 0),
            _ => (entry->Kind, 0, 0),
        };
    }

    private static IEnumerable<IntPtr> WalkHeapObjects(nint objectStart, nint end)
    {
        var ptr = objectStart;

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
}
