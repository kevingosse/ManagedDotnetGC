using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

// The long tail of the IGCHeap surface. Under NativeAOT a NotImplementedException escaping
// an UnmanagedCallersOnly frame is a process fail-fast, so everything the EE or BCL can
// reach must return a sane value instead of throwing (missing-features 6.1/10.x). The
// remaining throwing members are the ones nothing can reach on a release win-x64 runtime.
unsafe partial class GCHeap
{
    // GCSettings.LatencyMode: behaviorally ignored (every collection is blocking), but the
    // property must round-trip. 1 = GCLatencyMode.Interactive, the workstation default.
    private int _latencyMode = 1;

    // GCSettings.LargeObjectHeapCompactionMode: 1 = Default. There is no LOH, so the value
    // is bookkeeping only; stock resets CompactOnce to Default after the compacting GC.
    private int _lohCompactionMode = 1;

    public void Destructor()
    {
        // Process shutdown: nothing to tear down, the OS reclaims everything
        Write("IGCHeap Destructor");
    }

    public bool IsValidSegmentSize(nint size) => true;

    public bool IsValidGen0MaxSize(nint size) => true;

    public nint GetValidSegmentSize(bool large_seg = false) => Region.Size;

    public void SetReservedVMLimit(nint vmlimit)
    {
    }

    // There is no concurrent GC: "wait until complete" is always already true
    public void WaitUntilConcurrentGCComplete()
    {
    }

    public void TemporaryEnableConcurrentGC()
    {
    }

    public void TemporaryDisableConcurrentGC()
    {
    }

    public HResult WaitUntilConcurrentGCCompleteAsync(int millisecondsTimeout) => HResult.S_OK;

    public int GetGcLatencyMode() => _latencyMode;

    public int SetGcLatencyMode(int newLatencyMode)
    {
        _latencyMode = newLatencyMode;
        return 0; // set_pause_mode_success
    }

    public bool RegisterForFullGCNotification(uint gen2Percentage, uint lohPercentage)
    {
        // No notification support: the EE surfaces false as InvalidOperationException
        Write("RegisterForFullGCNotification");
        return false;
    }

    public bool CancelFullGCNotification()
    {
        Write("CancelFullGCNotification");
        return true;
    }

    public int WaitForFullGCApproach(int millisecondsTimeout) => 4;

    public int WaitForFullGCComplete(int millisecondsTimeout) => 4;

    public uint WhichGeneration(GCObject* obj)
    {
        // Non-generational: there is a single generation, 0. Objects outside the GC heap (frozen
        // segments) report int.MaxValue, matching the stock GC. The answer for heap objects must
        // never exceed GetMaxGeneration: the EE indexes arrays sized GetMaxGeneration + 1 with it
        // (e.g. the dead-thread GC trigger heuristic in threads.cpp).
        return _nativeAllocator.IsInRange((nint)obj) ? 0 : (uint)int.MaxValue;
    }

    public int StartNoGCRegion(ulong totalSize, bool lohSizeKnown, ulong lohSize, bool disallowFullBlockingGC)
    {
        // Declining is a documented outcome: start_no_gc_no_memory -> managed false
        // (missing-features 6.1). Implementing for real means promising no GC until End.
        Write("StartNoGCRegion (declined)");
        return 1; // start_no_gc_no_memory
    }

    public int EndNoGCRegion()
    {
        // Never in a region, since StartNoGCRegion always declines
        return 1; // end_no_gc_not_in_progress -> managed InvalidOperationException
    }

    public bool IsPromoted(GCObject* obj)
    {
        // During a GC: marked or outside the heap (frozen = immortal) means promoted.
        // Outside a GC everything is promoted, matching stock (interface.cpp:783-790).
        return obj == null
            || !_gcInProgress
            || !_nativeAllocator.IsInRange((nint)obj)
            || obj->IsMarked();
    }

    public bool IsHeapPointer(IntPtr obj, bool small_heap_only)
    {
        // Frozen segments are deliberately excluded, like the stock answer
        return _nativeAllocator.IsInRange(obj);
    }

    public uint GetCondemnedGeneration() => 2;

    public bool IsEphemeral(GCObject* obj) => false;

    public bool RuntimeStructuresValid() => true;

    public void SetSuspensionPending(bool fSuspensionPending)
    {
        Write($"SetSuspensionPending({fSuspensionPending})");
    }

    public void SetYieldProcessorScalingFactor(float yieldProcessorScalingFactor)
    {
        // The EE publishes its yield-processor normalization measurement here whenever
        // suspension activity makes it re-measure — reachable as soon as the GC triggers
        // collections on its own. Nothing to do for this GC (missing-features 6.x: no-op).
        Write("SetYieldProcessorScalingFactor");
    }

    public void PublishObject(IntPtr obj)
    {
    }

    public bool IsLargeObject(GCObject* pObj) => false;

    public void ValidateObjectMember(GCObject* obj)
    {
    }

    public GCObject* NextObj(GCObject* obj)
    {
        // Only used by stock-internal walks that never reach a standalone GC; null is the
        // documented "no next object" answer and can't fail-fast
        return null;
    }

    public GCObject* GetContainingObject(IntPtr pInteriorPtr, bool fCollectedGenOnly)
    {
        // Same machinery as interior-pointer marking; null for non-heap addresses and
        // pointers into dead space (missing-features 6.3)
        var obj = _nativeAllocator.IsInRange(pInteriorPtr) ? ResolveInteriorPointer(pInteriorPtr) : null;

        // The resolver's bound includes the trailing pre-header slot that ComputeSize counts
        // for the *next* object (fine for marking, matching stock find_object); the API answer
        // is strict: one past the object's own bytes is not inside it
        if (obj != null && pInteriorPtr >= (nint)obj + (nint)obj->ComputeSize() - IntPtr.Size)
        {
            return null;
        }

        return obj;
    }

    // Diag walks: a profiler attach, dotnet-gcdump or an EventPipe heap session reaches
    // these; silent no-ops mean "no data" instead of fail-fast (missing-features 10.x)
    public void DiagWalkObject(GCObject* obj, void* fn, void* context)
    {
    }

    public void DiagWalkObject2(GCObject* obj, void* fn, void* context)
    {
    }

    public void DiagWalkHeap(void* fn, void* context, int gen_number, bool walk_large_object_heap_p)
    {
    }

    public void DiagWalkSurvivorsWithType(void* gc_context, void* fn, void* diag_context, walk_surv_type type, int gen_number = -1)
    {
    }

    public void DiagWalkFinalizeQueue(void* gc_context, void* fn)
    {
    }

    public void DiagScanFinalizeQueue(void* fn, void* context)
    {
    }

    public void DiagScanHandles(void* fn, int gen_number, void* context)
    {
    }

    public void DiagScanDependentHandles(void* fn, int gen_number, void* context)
    {
    }

    public void DiagDescrGenerations(void* fn, void* context)
    {
    }

    public void DiagTraceGCSegments()
    {
    }

    public void DiagGetGCSettings(void* settings)
    {
    }

    public bool StressHeap(gc_alloc_context* acontext)
    {
        // GCStress is not supported; false = "no stress collection happened"
        return false;
    }

    public void ControlEvents(GCEventKeyword keyword, GCEventLevel level)
    {
    }

    public void ControlPrivateEvents(GCEventKeyword keyword, GCEventLevel level)
    {
    }

    public uint GetGenerationWithRange(GCObject* obj, byte** ppStart, byte** ppAllocated, byte** ppReserved)
    {
        // One pseudo-generation spanning the whole heap reservation
        *ppStart = (byte*)_nativeAllocator.LowestAddress;
        *ppAllocated = (byte*)_regionAllocator.RegionBase(_regionAllocator.CarvedCount);
        *ppReserved = (byte*)_nativeAllocator.HighestAddress;
        return 0;
    }

    public int RefreshMemoryLimit()
    {
        // No cached limits to refresh; DOTNET_GCHeapHardLimit is read once at startup
        return 0; // refresh_success
    }

    public enable_no_gc_region_callback_status EnableNoGCRegionCallback(nint callback, ulong callback_threshold)
    {
        // Never inside a NoGC region (StartNoGCRegion declines), so registration cannot start
        return enable_no_gc_region_callback_status.not_started;
    }

    public ulong GetGenerationBudget(int generation)
    {
        // Polled by the gen-0-gc-budget EventCounter the moment dotnet-counters attaches
        return (ulong)_budget;
    }

    public void DiagWalkHeapWithACHandling(nint fn, void* context, int gen_number, bool walk_large_object_heap_p)
    {
    }

    public void GetMemoryInfo(out ulong highMemLoadThresholdBytes, out ulong totalAvailableMemoryBytes, out ulong lastRecordedMemLoadBytes, out ulong lastRecordedHeapSizeBytes, out ulong lastRecordedFragmentationBytes, out ulong totalCommittedBytes, out ulong promotedBytes, out ulong pinnedObjectCount, out ulong finalizationPendingCount, out ulong index, out uint generation, out uint pauseTimePct, out bool isCompaction, out bool isConcurrent, out ulong genInfoRaw, out ulong pauseInfoRaw, int kind)
    {
        // Real numbers where the region heap has them, zeros elsewhere (missing-features 10.x).
        // All kinds report the last blocking collection — the only kind there is.
        var committed = (ulong)_regionAllocator.CommittedRegionBytes;
        var live = (ulong)_lastLiveBytes;

        highMemLoadThresholdBytes = 0;
        totalAvailableMemoryBytes = 0;
        lastRecordedMemLoadBytes = 0;
        lastRecordedHeapSizeBytes = live;
        lastRecordedFragmentationBytes = committed > live ? committed - live : 0;
        totalCommittedBytes = committed;
        promotedBytes = live;
        pinnedObjectCount = 0;
        finalizationPendingCount = (ulong)GetNumberOfFinalizable();
        index = _gcCount;
        generation = 0;
        pauseTimePct = 0;
        isCompaction = false;
        isConcurrent = false;
        genInfoRaw = 0;
        pauseInfoRaw = 0;
    }
}
