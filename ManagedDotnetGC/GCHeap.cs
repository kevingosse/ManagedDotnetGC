using ManagedDotnetGC.Dac;
using NativeObjects;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

internal unsafe class GCHeap : Interfaces.IGCHeap
{
    private readonly IGCToCLRInvoker _gcToClr;
    private readonly GCHandleManager _gcHandleManager;

    private readonly IGCHeap _nativeObject;
    private DacManager? _dacManager;

    public GCHeap(IGCToCLRInvoker gcToClr)
    {
        _gcToClr = gcToClr;
        _gcHandleManager = new GCHandleManager(gcToClr);

        _nativeObject = IGCHeap.Wrap(this);
    }

    public IntPtr IGCHeapObject => _nativeObject;
    public IntPtr IGCHandleManagerObject => _gcHandleManager.IGCHandleManagerObject;

    public void Destructor()
    {
        Write("IGCHeap Destructor");
    }

    public bool IsValidSegmentSize(nint size)
    {
        Write("IsValidSegmentSize");
        return false;
    }

    public bool IsValidGen0MaxSize(nint size)
    {
        Write("IsValidGen0MaxSize");
        return false;
    }

    public nint GetValidSegmentSize(bool large_seg = false)
    {
        Write("GetValidSegmentSize");
        return 0;
    }

    public void SetReservedVMLimit(nint vmlimit)
    {
        Write("SetReservedVMLimit");
    }

    public void WaitUntilConcurrentGCComplete()
    {
        Write("WaitUntilConcurrentGCComplete");
    }

    public bool IsConcurrentGCInProgress()
    {
        Write("IsConcurrentGCInProgress");
        return false;
    }

    public void TemporaryEnableConcurrentGC()
    {
        Write("TemporaryEnableConcurrentGC");
    }

    public void TemporaryDisableConcurrentGC()
    {
        Write("TemporaryDisableConcurrentGC");
    }

    public bool IsConcurrentGCEnabled()
    {
        Write("IsConcurrentGCEnabled");
        return false;
    }

    public HResult WaitUntilConcurrentGCCompleteAsync(int millisecondsTimeout)
    {
        Write("WaitUntilConcurrentGCCompleteAsync");
        return default;
    }

    public nint GetNumberOfFinalizable()
    {
        Write("GetNumberOfFinalizable");
        return 0;
    }

    public unsafe GCObject* GetNextFinalizable()
    {
        Write("GetNextFinalizable");
        return null;
    }

    public uint GetMemoryLoad()
    {
        Write("GetMemoryLoad");
        return 0;
    }

    public int GetGcLatencyMode()
    {
        Write("GetGcLatencyMode");
        return 0;
    }

    public int SetGcLatencyMode(int newLatencyMode)
    {
        Write("SetGcLatencyMode");
        return 0;
    }

    public int GetLOHCompactionMode()
    {
        Write("GetLOHCompactionMode");
        return 0;
    }

    public void SetLOHCompactionMode(int newLOHCompactionMode)
    {
        Write("SetLOHCompactionMode");
    }

    public bool RegisterForFullGCNotification(uint gen2Percentage, uint lohPercentage)
    {
        Write("RegisterForFullGCNotification");
        return false;
    }

    public bool CancelFullGCNotification()
    {
        Write("CancelFullGCNotification");
        return false;
    }

    public int WaitForFullGCApproach(int millisecondsTimeout)
    {
        Write("WaitForFullGCApproach");
        return 0;
    }

    public int WaitForFullGCComplete(int millisecondsTimeout)
    {
        Write("WaitForFullGCComplete");
        return 0;
    }

    public unsafe uint WhichGeneration(GCObject* obj)
    {
        Write("WhichGeneration");
        return 0;
    }

    public int CollectionCount(int generation, int get_bgc_fgc_coutn)
    {
        Write("CollectionCount");
        return 0;
    }

    public int StartNoGCRegion(ulong totalSize, bool lohSizeKnown, ulong lohSize, bool disallowFullBlockingGC)
    {
        Write("StartNoGCRegion");
        return 0;
    }

    public int EndNoGCRegion()
    {
        Write("EndNoGCRegion");
        return 0;
    }

    public nint GetTotalBytesInUse()
    {
        Write("GetTotalBytesInUse");
        return 0;
    }

    public ulong GetTotalAllocatedBytes()
    {
        Write("GetTotalAllocatedBytes");
        return 0;
    }

    public HResult GarbageCollect(int generation, bool low_memory_p, int mode)
    {
        Write("GarbageCollect");

        _gcHandleManager.Store.DumpHandles(_dacManager);

        return HResult.S_OK;
    }

    public uint GetMaxGeneration()
    {
        Write("GetMaxGeneration");
        return 2;
    }

    public unsafe void SetFinalizationRun(GCObject* obj)
    {
        Write("SetFinalizationRun");
    }

    public unsafe bool RegisterForFinalization(int gen, GCObject* obj)
    {
        Write("RegisterForFinalization");
        return false;
    }

    public int GetLastGCPercentTimeInGC()
    {
        Write("GetLastGCPercentTimeInGC");
        return 0;
    }

    public nint GetLastGCGenerationSize(int gen)
    {
        Write("GetLastGCGenerationSize");
        return 0;
    }

    public HResult Initialize()
    {
        Write("Initialize GCHeap");

        if (DacManager.TryLoad(out var dacManager))
        {
            _dacManager = dacManager;
        }

        var parameters = new WriteBarrierParameters();

        parameters.operation = WriteBarrierOp.Initialize;
        parameters.is_runtime_suspended = true;
        parameters.requires_upper_bounds_check = false; // Actually ignored because Initialize
        //parameters.card_table = (uint*)Marshal.AllocHGlobal(sizeof(nint) * 2);
        parameters.ephemeral_low = (byte*)(~0);
        //parameters.ephemeral_high = (byte*)1;

        _gcToClr.StompWriteBarrier(&parameters);

        //parameters.operation = WriteBarrierOp.StompResize;

        //_gcToClr.StompWriteBarrier(Unsafe.AsPointer(ref parameters));

        return HResult.S_OK;
    }

    public unsafe bool IsPromoted(GCObject* obj)
    {
        Write("IsPromoted");
        return false;
    }

    public unsafe bool IsHeapPointer(IntPtr obj, bool small_heap_only)
    {
        Write("IsHeapPointer");
        return false;
    }

    public uint GetCondemnedGeneration()
    {
        Write("GetCondemnedGeneration");
        return 0;
    }

    public bool IsGCInProgressHelper(bool bConsiderGCStart = false)
    {
        Write("IsGCInProgressHelper");
        return false;
    }

    public uint GetGcCount()
    {
        Write("GetGcCount");
        return 0;
    }

    public unsafe bool IsThreadUsingAllocationContextHeap(gc_alloc_context* acontext, int thread_number)
    {
        Write("IsThreadUsingAllocationContextHeap");
        return false;
    }

    public unsafe bool IsEphemeral(GCObject* obj)
    {
        Write("IsEphemeral");
        return false;
    }

    public uint WaitUntilGCComplete(bool bConsiderGCStart = false)
    {
        Write("WaitUntilGCComplete");
        return 0;
    }

    public unsafe void FixAllocContext(gc_alloc_context* acontext, void* arg, void* heap)
    {
        Write("FixAllocContext");
    }

    public nint GetCurrentObjSize()
    {
        Write("GetCurrentObjSize");
        return 0;
    }

    public void SetGCInProgress(bool fInProgress)
    {
        Write("SetGCInProgress");
    }

    public bool RuntimeStructuresValid()
    {
        Write("RuntimeStructuresValid");
        return false;
    }

    public void SetSuspensionPending(bool fSuspensionPending)
    {
        Write("SetSuspensionPending");
    }

    public void SetYieldProcessorScalingFactor(float yieldProcessorScalingFactor)
    {
        Write("SetYieldProcessorScalingFactor");
    }

    public void Shutdown()
    {
        Write("Shutdown");
    }

    public nint GetLastGCStartTime(int generation)
    {
        Write("GetLastGCStartTime");
        return 0;
    }

    public nint GetLastGCDuration(int generation)
    {
        Write("GetLastGCDuration");
        return 0;
    }

    public nint GetNow()
    {
        Write("GetNow");
        return 0;
    }

    public GCObject* Alloc(gc_alloc_context* acontext, nint size, uint flags)
    {
        var threadId = GetCurrentThreadId();

        Write($"{threadId} Alloc: {size} (alloc context: {(IntPtr)acontext:x2}, start: {acontext->alloc_ptr:x2}, size: {size:x2}, limit: {acontext->alloc_limit:x2})");

        // _gcHandleManager.Store.DumpHandles();

        var result = acontext->alloc_ptr;
        var advance = result + size;

        if (advance <= acontext->alloc_limit)
        {
            acontext->alloc_ptr = advance;
            return (GCObject*)result;
        }

        Write($"{threadId} Allocating new segment");

        int beginGap = 24;
        int growthSize = 16 * 1024 * 1024;

        var newPages = Marshal.AllocHGlobal(growthSize);

        Write($"{threadId} Allocated {growthSize} bytes at {newPages:x2}");

        // Zero the memory
        for (int i = 0; i < growthSize / sizeof(nint); i++)
        {
            *((nint*)newPages + i) = 0;
        }

        Write($"{threadId} Zeroed the memory");

        var allocationStart = newPages + beginGap;
        acontext->alloc_ptr = allocationStart + size;
        acontext->alloc_limit = newPages + growthSize;

        Write($"{threadId} Returning new alloc context: {(IntPtr)acontext:x2}, start: {acontext->alloc_ptr:x2}, limit: {acontext->alloc_limit:x2}");

        return (GCObject*)allocationStart;
    }

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public unsafe void PublishObject(IntPtr obj)
    {
        Write($"PublishObject: {obj:x2}");
    }

    public void SetWaitForGCEvent()
    {
        Write("SetWaitForGCEvent");
    }

    public void ResetWaitForGCEvent()
    {
        Write("ResetWaitForGCEvent");
    }

    public unsafe bool IsLargeObject(GCObject* pObj)
    {
        Write("IsLargeObject");
        return false;
    }

    public unsafe void ValidateObjectMember(GCObject* obj)
    {
        Write("ValidateObjectMember");
    }

    public unsafe GCObject* NextObj(GCObject* obj)
    {
        Write("NextObj");
        return null;
    }

    public unsafe GCObject* GetContainingObject(IntPtr pInteriorPtr, bool fCollectedGenOnly)
    {
        Write("GetContainingObject");
        return null;
    }

    public unsafe void DiagWalkObject(GCObject* obj, void* fn, void* context)
    {
        Write("DiagWalkObject");
    }

    public unsafe void DiagWalkObject2(GCObject* obj, void* fn, void* context)
    {
        Write("DiagWalkObject2");
    }

    public unsafe void DiagWalkHeap(void* fn, void* context, int gen_number, bool walk_large_object_heap_p)
    {
        Write("DiagWalkHeap");
    }

    public unsafe void DiagWalkSurvivorsWithType(void* gc_context, void* fn, void* diag_context, walk_surv_type type, int gen_number = -1)
    {
        Write("DiagWalkSurvivorsWithType");
    }

    public unsafe void DiagWalkFinalizeQueue(void* gc_context, void* fn)
    {
        Write("DiagWalkFinalizeQueue");
    }

    public unsafe void DiagScanFinalizeQueue(void* fn, void* context)
    {
        Write("DiagScanFinalizeQueue");
    }

    public unsafe void DiagScanHandles(void* fn, int gen_number, void* context)
    {
        Write("DiagScanHandles");
    }

    public unsafe void DiagScanDependentHandles(void* fn, int gen_number, void* context)
    {
        Write("DiagScanDependentHandles");
    }

    public unsafe void DiagDescrGenerations(void* fn, void* context)
    {
        Write("DiagDescrGenerations");
    }

    public void DiagTraceGCSegments()
    {
        Write("DiagTraceGCSegments");
    }

    public unsafe void DiagGetGCSettings(void* settings)
    {
        Write("DiagGetGCSettings");
    }

    public unsafe bool StressHeap(gc_alloc_context* acontext)
    {
        Write("StressHeap");
        return false;
    }

    public unsafe void* RegisterFrozenSegment(segment_info* pseginfo)
    {
        Write("RegisterFrozenSegment");
        return pseginfo;
    }

    public unsafe void UnregisterFrozenSegment(void* seg)
    {
        Write("UnregisterFrozenSegment");
    }

    public unsafe bool IsInFrozenSegment(GCObject* obj)
    {
        Write("IsInFrozenSegment");
        return false;
    }

    public void ControlEvents(GCEventKeyword keyword, GCEventLevel level)
    {
        Write("ControlEvents");
    }

    public void ControlPrivateEvents(GCEventKeyword keyword, GCEventLevel level)
    {
        Write("ControlPrivateEvents");
    }

    public unsafe uint GetGenerationWithRange(GCObject* obj, byte** ppStart, byte** ppAllocated, byte** ppReserved)
    {
        Write("GetGenerationWithRange");
        return 0;
    }

    public long GetTotalPauseDuration()
    {
        Write("GetTotalPauseDuration");
        return 0;
    }

    public void EnumerateConfigurationValues(void* context, nint configurationValueFunc)
    {
        Write("EnumerateConfigurationValues");
    }

    public void UpdateFrozenSegment(nint seg, nint allocated, nint committed)
    {
        Write($"UpdateFrozenSegment {seg:x2} {allocated:x2} {committed:x2}");
    }

    public int RefreshMemoryLimit()
    {
        Write("RefreshMemoryLimit");
        return 0;
    }

    public enable_no_gc_region_callback_status EnableNoGCRegionCallback(nint callback, ulong callback_threshold)
    {
        Write("EnableNoGCRegionCallback");
        return default;
    }

    public nint GetExtraWorkForFinalization()
    {
        Write("GetExtraWorkForFinalization");
        return 0;
    }

    public ulong GetGenerationBudget(int generation)
    {
        Write("GetGenerationBudget");
        return 0;
    }

    public nint GetLOHThreshold()
    {
        Write("GetLOHThreshold");
        return 0;
    }

    public void DiagWalkHeapWithACHandling(nint fn, void* context, int gen_number, bool walk_large_object_heap_p)
    {
        Write("DiagWalkHeapWithACHandling");
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
