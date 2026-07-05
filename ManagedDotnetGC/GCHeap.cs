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
    private long _lastLiveBytes;
    private long _budget = Region.MinGCBudget;

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
        _regionAllocator.SetFreeObjectMethodTable(_freeObjectMethodTable);
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

        // Honor DOTNET_GCHeapHardLimit as a cap on committed region bytes (SPEC-M2 §9)
        fixed (byte* privateKey = "GCHeapHardLimit"u8)
        fixed (byte* publicKey = "System.GC.HeapHardLimit"u8)
        {
            if (_gcToClr.GetIntConfigValue(privateKey, publicKey, out var hardLimit) && hardLimit > 0)
            {
                Write($"Heap hard limit: {hardLimit}");
                _regionAllocator.SetHardLimit(hardLimit);
            }
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

            // SPEC-M2 §8.3: the heap converges to ≈ 2× live
            _allocatedSinceGC = 0;
            _budget = Region.ComputeBudget(_lastLiveBytes);

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
        var obj = AllocWithRetry(ref acontext, size);

        if (obj != null && flags.HasFlag(GC_ALLOC_FLAGS.GC_ALLOC_FINALIZE))
        {
            if (!RegisterForFinalization(0, obj))
            {
                // Couldn't grow the finalization queue: surface as OOM (SPEC-M2 §9.1).
                // The object has no MethodTable yet (the EE writes it after we return), so
                // plug its extent from the requested size to keep the heap walkable.
                AllocateFreeObject((nint)obj, (uint)(Align(size) - SizeOfObject));
                return null;
            }
        }

        return obj;
    }

    /// <summary>
    /// The collect-and-retry protocol (SPEC-M2 §8.3 + §9.1): one budget-triggered collection
    /// per call at most, then one forced collection between the first genuine allocation
    /// failure and surrendering with null (the EE turns null into OutOfMemoryException).
    /// </summary>
    private GCObject* AllocWithRetry(ref gc_alloc_context acontext, nint size)
    {
        var budgetChecked = false;
        var collectedForOom = false;

        while (true)
        {
            var snapshot = Volatile.Read(ref _gcCount);

            if (!budgetChecked)
            {
                budgetChecked = true;

                // Reads outside the alloc lock are approximate; the trigger doesn't care
                if (_allocatedSinceGC > _budget)
                {
                    Collect(snapshot);
                    continue;
                }
            }

            // Three tiers per SPEC-M2 §4; the direct tiers leave the caller's context
            // alone (it may still serve small allocations from its remaining window)
            var obj = size <= Region.BumpMaxSize
                ? AllocFromWindow(ref acontext, size)
                : size + IntPtr.Size <= Region.SizeClassMaxSize
                    ? AllocBlock(ref acontext, size)
                    : AllocSpan(ref acontext, size);

            if (obj != null)
            {
                return obj;
            }

            if (collectedForOom)
            {
                return null;
            }

            collectedForOom = true;
            Collect(snapshot);
        }
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

    private GCObject* AllocBlock(ref gc_alloc_context acontext, nint size)
    {
        var sizeClass = Region.SelectClass(size);

        _allocLock.Acquire();

        try
        {
            if (!_regionAllocator.TryAllocBlock(sizeClass, out var block))
            {
                return null;
            }

            var classSize = Region.ClassSizes[sizeClass];
            acontext.alloc_bytes_uoh += classSize;
            _allocatedSinceGC += classSize;

            return (GCObject*)(block + IntPtr.Size);
        }
        finally
        {
            _allocLock.Release();
        }
    }

    private GCObject* AllocSpan(ref gc_alloc_context acontext, nint size)
    {
        // The caller's context is left untouched: it may still serve small allocations
        var regionCount = Region.SpanRegionCount(size);

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
        var next = GCObject.NextEpoch(_currentEpoch);

        if (next < _currentEpoch)
        {
            // Wrapped: clear every stale stamp so it cannot alias the restarted sequence
            foreach (var ptr in WalkHeapObjects())
            {
                ((GCObject*)ptr)->Epoch = 0;
            }
        }

        _currentEpoch = next;
        GCObject.CurrentEpoch = next;
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

                case RegionKind.SizeClass:
                {
                    var (bitmap, classSize) = _regionAllocator.GetSizeClassInfo(i);
                    var regionBase = _regionAllocator.RegionBase(i);

                    while (bitmap != 0)
                    {
                        var bit = System.Numerics.BitOperations.TrailingZeroCount(bitmap);
                        bitmap &= bitmap - 1;
                        yield return regionBase + (nint)bit * classSize + IntPtr.Size;
                    }

                    break;
                }
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
            // A span holds exactly one object; size-class regions are walked via their bitmap
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
