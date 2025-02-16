using ManagedDotnetGC.Dac;
using NativeObjects;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

internal unsafe class GCHeap : Interfaces.IGCHeap
{
    private const int AllocationContextSize = 32 * 1024;
    private const int SegmentSize = AllocationContextSize * 128;
    private static int SizeOfObject = sizeof(nint) * 3;

    private readonly IGCToCLRInvoker _gcToClr;
    private readonly GCHandleManager _gcHandleManager;

    private readonly IGCHeap _nativeObject;
    private DacManager? _dacManager;

    private MethodTable* _freeObjectMethodTable;

    private List<Segment> _segments = new();
    private Segment _activeSegment;

    private ConcurrentDictionary<nint, StrongBox<(nint start, nint end)>> _frozenSegments = new();
    private int _frozenSegmentIndex;

    private GCHandle _handle;

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

    public unsafe nint RegisterFrozenSegment(segment_info* pseginfo)
    {
        var handle = Interlocked.Increment(ref _frozenSegmentIndex);
        _frozenSegments.TryAdd(handle, new((pseginfo->ibFirstObject, pseginfo->ibCommit)));
        return handle;
    }

    public unsafe void UnregisterFrozenSegment(nint seg)
    {
        _frozenSegments.TryRemove(seg, out _);
    }

    public unsafe bool IsInFrozenSegment(GCObject* obj)
    {
        foreach (var segment in _frozenSegments.Values)
        {
            if ((nint)obj >= segment.Value.start && (nint)obj < segment.Value.end)
            {
                return true;
            }
        }
        
        return false;
    }

    public void UpdateFrozenSegment(nint seg, nint allocated, nint committed)
    {
        if (_frozenSegments.TryGetValue(seg, out var segment))
        {
            segment.Value.start = allocated;
            segment.Value.end = committed;
        }
    }

    public HResult GarbageCollect(int generation, bool low_memory_p, int mode)
    {
        Write("GarbageCollect");

        _gcToClr.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);

        _gcHandleManager.Store.DumpHandles(_dacManager);

        var callback = (delegate* unmanaged<gc_alloc_context*, IntPtr, void>)&EnumAllocContextCallback;
        _gcToClr.GcEnumAllocContexts((IntPtr)callback, GCHandle.ToIntPtr(_handle));

        TraverseHeap();

        _gcToClr.RestartEE(finishedGC: true);

        return HResult.S_OK;
    }

    public unsafe void FixAllocContext(gc_alloc_context* acontext, void* arg, void* heap)
    {
        FixAllocContext(ref Unsafe.AsRef<gc_alloc_context>(acontext));
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
    private static void EnumAllocContextCallback(gc_alloc_context* acontext, IntPtr arg)
    {
        var handle = GCHandle.FromIntPtr(arg);
        var gcHeap = (GCHeap)handle.Target!;
        gcHeap.FixAllocContext(ref Unsafe.AsRef<gc_alloc_context>(acontext));
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

            var name = obj->MethodTable == _freeObjectMethodTable
                ? "Free"
                : _dacManager?.GetObjectName(new(ptr));

            Write($"{ptr:x2} - {name}");

            var alignment = sizeof(nint) - 1;
            ptr += ((nint)ComputeSize(obj) + alignment) & ~alignment;
        }
    }

    private void TraverseAllocContext(nint start, nint end)
    {
        var ptr = start + IntPtr.Size;

        while (ptr < end)
        {
            var obj = (GCObject*)ptr;

            if (obj->MethodTable == null)
            {
                break;
            }

            var name = _dacManager?.GetObjectName(new(ptr));
            Write($"{ptr:x2} - {name}");

            var alignment = sizeof(nint) - 1;
            ptr += ((nint)ComputeSize(obj) + alignment) & ~alignment;
        }
    }

    private static unsafe uint ComputeSize(GCObject* obj)
    {
        var methodTable = obj->MethodTable;

        if (!methodTable->HasComponentSize)
        {
            // Fixed-size object
            return methodTable->BaseSize;
        }

        // Variable-size object
        return methodTable->BaseSize + obj->Length * methodTable->ComponentSize;
    }

    #region Not implemented

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
        //Write("WaitUntilGCComplete");
        return 0;
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

    #endregion
}
