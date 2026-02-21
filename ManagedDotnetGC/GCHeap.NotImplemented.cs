using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

unsafe partial class GCHeap
{
    public void Destructor()
    {
        Write("IGCHeap Destructor");
        throw new NotImplementedException();
    }

    public bool IsValidSegmentSize(nint size)
    {
        Write("IsValidSegmentSize");
        throw new NotImplementedException();
    }

    public bool IsValidGen0MaxSize(nint size)
    {
        Write("IsValidGen0MaxSize");
        throw new NotImplementedException();
    }

    public nint GetValidSegmentSize(bool large_seg = false)
    {
        Write("GetValidSegmentSize");
        throw new NotImplementedException();
    }

    public void SetReservedVMLimit(nint vmlimit)
    {
        Write("SetReservedVMLimit");
        throw new NotImplementedException();
    }

    public void WaitUntilConcurrentGCComplete()
    {
        Write("WaitUntilConcurrentGCComplete");
        throw new NotImplementedException();
    }

    public void TemporaryEnableConcurrentGC()
    {
        Write("TemporaryEnableConcurrentGC");
        throw new NotImplementedException();
    }

    public void TemporaryDisableConcurrentGC()
    {
        Write("TemporaryDisableConcurrentGC");
        throw new NotImplementedException();
    }

    public HResult WaitUntilConcurrentGCCompleteAsync(int millisecondsTimeout)
    {
        Write("WaitUntilConcurrentGCCompleteAsync");
        throw new NotImplementedException();
    }

    public int GetGcLatencyMode()
    {
        Write("GetGcLatencyMode");
        throw new NotImplementedException();
    }

    public int SetGcLatencyMode(int newLatencyMode)
    {
        Write("SetGcLatencyMode");
        throw new NotImplementedException();
    }

    public bool RegisterForFullGCNotification(uint gen2Percentage, uint lohPercentage)
    {
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
        Write("WhichGeneration");
        throw new NotImplementedException();
    }

    public int StartNoGCRegion(ulong totalSize, bool lohSizeKnown, ulong lohSize, bool disallowFullBlockingGC)
    {
        Write("StartNoGCRegion");
        throw new NotImplementedException();
    }

    public int EndNoGCRegion()
    {
        Write("EndNoGCRegion");
        throw new NotImplementedException();
    }

    public bool IsPromoted(GCObject* obj)
    {
        Write("IsPromoted");
        throw new NotImplementedException();
    }

    public bool IsHeapPointer(IntPtr obj, bool small_heap_only)
    {
        Write("IsHeapPointer");
        throw new NotImplementedException();
    }

    public uint GetCondemnedGeneration() => 2;

    public bool IsEphemeral(GCObject* obj)
    {
        Write("IsEphemeral");
        throw new NotImplementedException();
    }

    public bool RuntimeStructuresValid()
    {
        Write("RuntimeStructuresValid");
        throw new NotImplementedException();
    }

    public void SetSuspensionPending(bool fSuspensionPending)
    {
        Write($"SetSuspensionPending({fSuspensionPending})");
    }

    public void SetYieldProcessorScalingFactor(float yieldProcessorScalingFactor)
    {
        Write("SetYieldProcessorScalingFactor");
        throw new NotImplementedException();
    }

    public void PublishObject(IntPtr obj)
    {
    }

    public bool IsLargeObject(GCObject* pObj)
    {
        Write("IsLargeObject");
        throw new NotImplementedException();
    }

    public void ValidateObjectMember(GCObject* obj)
    {
        Write("ValidateObjectMember");
        throw new NotImplementedException();
    }

    public GCObject* NextObj(GCObject* obj)
    {
        Write("NextObj");
        throw new NotImplementedException();
    }

    public GCObject* GetContainingObject(IntPtr pInteriorPtr, bool fCollectedGenOnly)
    {
        Write("GetContainingObject");
        throw new NotImplementedException();
    }

    public void DiagWalkObject(GCObject* obj, void* fn, void* context)
    {
        Write("DiagWalkObject");
        throw new NotImplementedException();
    }

    public void DiagWalkObject2(GCObject* obj, void* fn, void* context)
    {
        Write("DiagWalkObject2");
        throw new NotImplementedException();
    }

    public void DiagWalkHeap(void* fn, void* context, int gen_number, bool walk_large_object_heap_p)
    {
        Write("DiagWalkHeap");
        throw new NotImplementedException();
    }

    public void DiagWalkSurvivorsWithType(void* gc_context, void* fn, void* diag_context, walk_surv_type type, int gen_number = -1)
    {
        Write("DiagWalkSurvivorsWithType");
        throw new NotImplementedException();
    }

    public void DiagWalkFinalizeQueue(void* gc_context, void* fn)
    {
        Write("DiagWalkFinalizeQueue");
        throw new NotImplementedException();
    }

    public void DiagScanFinalizeQueue(void* fn, void* context)
    {
        Write("DiagScanFinalizeQueue");
        throw new NotImplementedException();
    }

    public void DiagScanHandles(void* fn, int gen_number, void* context)
    {
        Write("DiagScanHandles");
        throw new NotImplementedException();
    }

    public void DiagScanDependentHandles(void* fn, int gen_number, void* context)
    {
        Write("DiagScanDependentHandles");
        throw new NotImplementedException();
    }

    public void DiagDescrGenerations(void* fn, void* context)
    {
        Write("DiagDescrGenerations");
        throw new NotImplementedException();
    }

    public void DiagTraceGCSegments()
    {
        Write("DiagTraceGCSegments");
        throw new NotImplementedException();
    }

    public void DiagGetGCSettings(void* settings)
    {
        Write("DiagGetGCSettings");
        throw new NotImplementedException();
    }

    public bool StressHeap(gc_alloc_context* acontext)
    {
        Write("StressHeap");
        throw new NotImplementedException();
    }

    public void ControlEvents(GCEventKeyword keyword, GCEventLevel level)
    {
    }

    public void ControlPrivateEvents(GCEventKeyword keyword, GCEventLevel level)
    {
    }

    public uint GetGenerationWithRange(GCObject* obj, byte** ppStart, byte** ppAllocated, byte** ppReserved)
    {
        Write("GetGenerationWithRange");
        throw new NotImplementedException();
    }

    public int RefreshMemoryLimit()
    {
        Write("RefreshMemoryLimit");
        throw new NotImplementedException();
    }

    public enable_no_gc_region_callback_status EnableNoGCRegionCallback(nint callback, ulong callback_threshold)
    {
        Write("EnableNoGCRegionCallback");
        throw new NotImplementedException();
    }

    public ulong GetGenerationBudget(int generation)
    {
        Write("GetGenerationBudget");
        throw new NotImplementedException();
    }

    public void DiagWalkHeapWithACHandling(nint fn, void* context, int gen_number, bool walk_large_object_heap_p)
    {
        Write("DiagWalkHeapWithACHandling");
        throw new NotImplementedException();
    }

    public void GetMemoryInfo(out ulong highMemLoadThresholdBytes, out ulong totalAvailableMemoryBytes, out ulong lastRecordedMemLoadBytes, out ulong lastRecordedHeapSizeBytes, out ulong lastRecordedFragmentationBytes, out ulong totalCommittedBytes, out ulong promotedBytes, out ulong pinnedObjectCount, out ulong finalizationPendingCount, out ulong index, out uint generation, out uint pauseTimePct, out bool isCompaction, out bool isConcurrent, out ulong genInfoRaw, out ulong pauseInfoRaw, int kind)
    {
        Write("GetMemoryInfo");
        highMemLoadThresholdBytes = 0;
        totalAvailableMemoryBytes = 0;
        lastRecordedMemLoadBytes = 0;
        lastRecordedHeapSizeBytes = 0;
        lastRecordedFragmentationBytes = 0;
        totalCommittedBytes = 0;
        promotedBytes = 0;
        pinnedObjectCount = 0;
        finalizationPendingCount = 0;
        index = 0;
        generation = 0;
        pauseTimePct = 0;
        isCompaction = false;
        isConcurrent = false;
        genInfoRaw = 0;
        pauseInfoRaw = 0;
    }
}
