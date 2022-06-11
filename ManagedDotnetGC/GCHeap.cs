using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ManagedDotnetGC
{
    internal unsafe class GCHeap : IGCHeap
    {
        private readonly IGCToCLR _gcToClr;
        private readonly GCHandleManager _gcHandleManager;

        private readonly NativeStubs.IGCHeapStub _gcHeapStub;

        public GCHeap(IGCToCLR gcToClr)
        {
            _gcToClr = gcToClr;
            _gcHandleManager = new GCHandleManager(gcToClr);

            _gcHeapStub = NativeStubs.IGCHeapStub.Wrap(this);
        }

        public IntPtr IGCHeapObject => _gcHeapStub;
        public IntPtr IGCHandleManagerObject => _gcHandleManager.IGCHandleManagerObject;

        public void Destructor()
        {

        }

        public bool IsValidSegmentSize(nint size)
        {
            Console.WriteLine("IsValidSegmentSize");
            return false;
        }

        public bool IsValidGen0MaxSize(nint size)
        {
            Console.WriteLine("IsValidGen0MaxSize");
            return false;
        }

        public nint GetValidSegmentSize(bool large_seg = false)
        {
            Console.WriteLine("GetValidSegmentSize");

            return 0;
        }

        public void SetReservedVMLimit(nint vmlimit)
        {
            Console.WriteLine("SetReservedVMLimit");

        }

        public void WaitUntilConcurrentGCComplete()
        {
            Console.WriteLine("WaitUntilConcurrentGCComplete");

        }

        public bool IsConcurrentGCInProgress()
        {
            Console.WriteLine("IsConcurrentGCInProgress");

            return false;
        }

        public void TemporaryEnableConcurrentGC()
        {
            Console.WriteLine("TemporaryEnableConcurrentGC");

        }

        public void TemporaryDisableConcurrentGC()
        {
            Console.WriteLine("TemporaryDisableConcurrentGC");

        }

        public bool IsConcurrentGCEnabled()
        {
            Console.WriteLine("IsConcurrentGCEnabled");

            return false;
        }

        public HResult WaitUntilConcurrentGCCompleteAsync(int millisecondsTimeout)
        {
            Console.WriteLine("WaitUntilConcurrentGCCompleteAsync");

            return default;
        }

        public nint GetNumberOfFinalizable()
        {
            Console.WriteLine("GetNumberOfFinalizable");

            return 0;
        }

        public unsafe GCObject* GetNextFinalizable()
        {
            Console.WriteLine("[GC] GetNextFinalizable");

            return null;
        }

        public unsafe void GetMemoryInfo(ulong* highMemLoadThresholdBytes, ulong* totalAvailableMemoryBytes, ulong* lastRecordedMemLoadBytes, ulong* lastRecordedHeapSizeBytes, ulong* lastRecordedFragmentationBytes, ulong* totalCommittedBytes, ulong* promotedBytes, ulong* pinnedObjectCount, ulong* finalizationPendingCount, ulong* index, uint* generation, uint* pauseTimePct, bool* isCompaction, bool* isConcurrent, ulong* genInfoRaw, ulong* pauseInfoRaw, int kind)
        {
            Console.WriteLine("GetMemoryInfo");

        }

        public uint GetMemoryLoad()
        {
            Console.WriteLine("GetMemoryLoad");

            return 0;
        }

        public int GetGcLatencyMode()
        {
            Console.WriteLine("GetGcLatencyMode");

            return 0;
        }

        public int SetGcLatencyMode(int newLatencyMode)
        {
            Console.WriteLine("SetGcLatencyMode");

            return 0;
        }

        public int GetLOHCompactionMode()
        {
            Console.WriteLine("GetLOHCompactionMode");

            return 0;
        }

        public void SetLOHCompactionMode(int newLOHCompactionMode)
        {
            Console.WriteLine("SetLOHCompactionMode");

        }

        public bool RegisterForFullGCNotification(uint gen2Percentage, uint lohPercentage)
        {
            Console.WriteLine("RegisterForFullGCNotification");

            return false;
        }

        public bool CancelFullGCNotification()
        {
            Console.WriteLine("CancelFullGCNotification");

            return false;
        }

        public int WaitForFullGCApproach(int millisecondsTimeout)
        {
            Console.WriteLine("WaitForFullGCApproach");

            return 0;
        }

        public int WaitForFullGCComplete(int millisecondsTimeout)
        {
            Console.WriteLine("WaitForFullGCComplete");

            return 0;
        }

        public unsafe uint WhichGeneration(GCObject* obj)
        {
            Console.WriteLine("WhichGeneration");

            return 0;
        }

        public int CollectionCount(int generation, int get_bgc_fgc_coutn)
        {
            Console.WriteLine("CollectionCount");

            return 0;
        }

        public int StartNoGCRegion(ulong totalSize, bool lohSizeKnown, ulong lohSize, bool disallowFullBlockingGC)
        {
            Console.WriteLine("StartNoGCRegion");

            return 0;
        }

        public int EndNoGCRegion()
        {
            Console.WriteLine("EndNoGCRegion");

            return 0;
        }

        public nint GetTotalBytesInUse()
        {
            Console.WriteLine("GetTotalBytesInUse");

            return 0;
        }

        public ulong GetTotalAllocatedBytes()
        {
            Console.WriteLine("GetTotalAllocatedBytes");
            return 0;
        }

        public HResult GarbageCollect(int generation, bool low_memory_p, int mode)
        {
            Console.WriteLine("GarbageCollect");

            return HResult.S_OK;
        }

        public uint GetMaxGeneration()
        {
            Console.WriteLine("GetMaxGeneration");

            return 0;
        }

        public unsafe void SetFinalizationRun(GCObject* obj)
        {
            Console.WriteLine("SetFinalizationRun");

        }

        public unsafe bool RegisterForFinalization(int gen, GCObject* obj)
        {
            Console.WriteLine("RegisterForFinalization");

            return false;
        }

        public int GetLastGCPercentTimeInGC()
        {
            Console.WriteLine("GetLastGCPercentTimeInGC");

            return 0;
        }

        public nint GetLastGCGenerationSize(int gen)
        {
            Console.WriteLine("GetLastGCGenerationSize");

            return 0;
        }

        public HResult Initialize()
        {
            Console.WriteLine("[GC] Initialize GCHeap");



            var parameters = default(WriteBarrierParameters);

            parameters.operation = WriteBarrierOp.Initialize;
            parameters.is_runtime_suspended = true;
            parameters.requires_upper_bounds_check = false;
            parameters.card_table = (uint*)Marshal.AllocHGlobal(sizeof(nint) * 2);
            parameters.lowest_address = (byte*)(~0);
            parameters.highest_address = (byte*)1;
            parameters.ephemeral_low = (byte*)(~0);
            parameters.ephemeral_high = (byte*)1;

            _gcToClr.StompWriteBarrier(Unsafe.AsPointer(ref parameters));
            return HResult.S_OK;
        }

        public unsafe bool IsPromoted(GCObject* obj)
        {
            Console.WriteLine("IsPromoted");

            return false;
        }

        public unsafe bool IsHeapPointer(void* obj, bool small_heap_only)
        {
            Console.WriteLine("IsHeapPointer");

            return false;
        }

        public uint GetCondemnedGeneration()
        {
            Console.WriteLine("GetCondemnedGeneration");

            return 0;
        }

        public bool IsGCInProgressHelper(bool bConsiderGCStart = false)
        {
            Console.WriteLine("[GC] IsGCInProgressHelper");

            return false;
        }

        public uint GetGcCount()
        {
            Console.WriteLine("GetGcCount");

            return 0;
        }

        public unsafe bool IsThreadUsingAllocationContextHeap(gc_alloc_context* acontext, int thread_number)
        {
            Console.WriteLine("IsThreadUsingAllocationContextHeap");

            return false;
        }

        public unsafe bool IsEphemeral(GCObject* obj)
        {
            Console.WriteLine("IsEphemeral");

            return false;
        }

        public uint WaitUntilGCComplete(bool bConsiderGCStart = false)
        {
            Console.WriteLine("WaitUntilGCComplete");

            return 0;
        }

        public unsafe void FixAllocContext(gc_alloc_context* acontext, void* arg, void* heap)
        {
            Console.WriteLine("[GC] FixAllocContext");

        }

        public nint GetCurrentObjSize()
        {
            Console.WriteLine("GetCurrentObjSize");

            return 0;
        }

        public void SetGCInProgress(bool fInProgress)
        {
            Console.WriteLine("SetGCInProgress");

        }

        public bool RuntimeStructuresValid()
        {
            Console.WriteLine("RuntimeStructuresValid");

            return false;
        }

        public void SetSuspensionPending(bool fSuspensionPending)
        {
            Console.WriteLine("SetSuspensionPending");

        }

        public void SetYieldProcessorScalingFactor(float yieldProcessorScalingFactor)
        {
            Console.WriteLine("SetYieldProcessorScalingFactor");

        }

        public void Shutdown()
        {
            Console.WriteLine("Shutdown");

        }

        public nint GetLastGCStartTime(int generation)
        {
            Console.WriteLine("GetLastGCStartTime");

            return 0;
        }

        public nint GetLastGCDuration(int generation)
        {
            Console.WriteLine("GetLastGCDuration");

            return 0;
        }

        public nint GetNow()
        {
            Console.WriteLine("GetNow");

            return 0;
        }

        public GCObject* Alloc(gc_alloc_context* acontext, nint size, uint flags)
        {
            var result = acontext->alloc_ptr;
            var advance = result + size;

            if (advance <= acontext->alloc_limit)
            {
                acontext->alloc_ptr = advance;
                return (GCObject*)result;
            }

            Console.WriteLine("[GC] Allocating new segment");

            int beginGap = 24;
            int growthSize = 16 * 1024 * 1024;

            var newPages = Marshal.AllocHGlobal(growthSize);

            // Zero the memory
            for (int i = 0; i < growthSize / sizeof(nint); i++)
            {
                *((nint*)newPages + i) = 0;
            }

            var allocationStart = newPages + beginGap;
            acontext->alloc_ptr = allocationStart + size;
            acontext->alloc_limit = newPages + growthSize;
            return (GCObject*)allocationStart;
        }

        public unsafe void PublishObject(byte* obj)
        {
        }

        public void SetWaitForGCEvent()
        {
            Console.WriteLine("SetWaitForGCEvent");

        }

        public void ResetWaitForGCEvent()
        {
            Console.WriteLine("ResetWaitForGCEvent");

        }

        public unsafe bool IsLargeObject(GCObject* pObj)
        {
            Console.WriteLine("IsLargeObject");
            return false;
        }

        public unsafe void ValidateObjectMember(GCObject* obj)
        {
            Console.WriteLine("ValidateObjectMember");

        }

        public unsafe GCObject* NextObj(GCObject* obj)
        {
            Console.WriteLine("NextObj");

            return null;
        }

        public unsafe GCObject* GetContainingObject(void* pInteriorPtr, bool fCollectedGenOnly)
        {
            Console.WriteLine("GetContainingObject");

            return null;
        }

        public unsafe void DiagWalkObject(GCObject* obj, void* fn, void* context)
        {
            Console.WriteLine("DiagWalkObject");

        }

        public unsafe void DiagWalkObject2(GCObject* obj, void* fn, void* context)
        {
            Console.WriteLine("DiagWalkObject2");

        }

        public unsafe void DiagWalkHeap(void* fn, void* context, int gen_number, bool walk_large_object_heap_p)
        {
            Console.WriteLine("DiagWalkHeap");

        }

        public unsafe void DiagWalkSurvivorsWithType(void* gc_context, void* fn, void* diag_context, walk_surv_type type, int gen_number = -1)
        {
            Console.WriteLine("DiagWalkSurvivorsWithType");

        }

        public unsafe void DiagWalkFinalizeQueue(void* gc_context, void* fn)
        {
            Console.WriteLine("DiagWalkFinalizeQueue");

        }

        public unsafe void DiagScanFinalizeQueue(void* fn, void* context)
        {
            Console.WriteLine("DiagScanFinalizeQueue");

        }

        public unsafe void DiagScanHandles(void* fn, int gen_number, void* context)
        {
            Console.WriteLine("DiagScanHandles");

        }

        public unsafe void DiagScanDependentHandles(void* fn, int gen_number, void* context)
        {
            Console.WriteLine("DiagScanDependentHandles");

        }

        public unsafe void DiagDescrGenerations(void* fn, void* context)
        {
            Console.WriteLine("DiagDescrGenerations");

        }

        public void DiagTraceGCSegments()
        {
            Console.WriteLine("DiagTraceGCSegments");

        }

        public unsafe void DiagGetGCSettings(void* settings)
        {
            Console.WriteLine("DiagGetGCSettings");

        }

        public unsafe bool StressHeap(gc_alloc_context* acontext)
        {
            Console.WriteLine("StressHeap");


            return false;
        }

        public unsafe void* RegisterFrozenSegment(segment_info* pseginfo)
        {
            Console.WriteLine("RegisterFrozenSegment");

            return null;
        }

        public unsafe void UnregisterFrozenSegment(void* seg)
        {
            Console.WriteLine("UnregisterFrozenSegment");

        }

        public unsafe bool IsInFrozenSegment(GCObject* obj)
        {
            Console.WriteLine("IsInFrozenSegment");

            return false;
        }

        public void ControlEvents(GCEventKeyword keyword, GCEventLevel level)
        {
            Console.WriteLine("[GC] ControlEvents");
        }

        public void ControlPrivateEvents(GCEventKeyword keyword, GCEventLevel level)
        {
            Console.WriteLine("[GC] ControlPrivateEvents");
        }

        public unsafe uint GetGenerationWithRange(GCObject* obj, byte** ppStart, byte** ppAllocated, byte** ppReserved)
        {
            Console.WriteLine("GetGenerationWithRange");

            return 0;
        }
    }
}
