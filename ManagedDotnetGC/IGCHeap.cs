namespace ManagedDotnetGC;

[GenerateNativeStub]
public unsafe interface IGCHeap
{
    /*
===========================================================================
Hosting APIs. These are used by GC hosting. The code that
calls these methods may possibly be moved behind the interface -
today, the VM handles the setting of segment size and max gen 0 size.
(See src/vm/corehost.cpp)
===========================================================================
*/

    // Returns whether or not the given size is a valid segment size.
    bool IsValidSegmentSize(nint size);

    // Returns whether or not the given size is a valid gen 0 max size.
    bool IsValidGen0MaxSize(nint size);

    // Gets a valid segment size.
    nint GetValidSegmentSize(bool large_seg = false);

    // Sets the limit for reserved memory.
    void SetReservedVMLimit(nint vmlimit);

    /*
    ===========================================================================
    Concurrent GC routines. These are used in various places in the VM
    to synchronize with the GC, when the VM wants to update something that
    the GC is potentially using, if it's doing a background GC.

    Concrete examples of this are profiling/ETW scenarios.
    ===========================================================================
    */

    // Blocks until any running concurrent GCs complete.
    void WaitUntilConcurrentGCComplete();

    // Returns true if a concurrent GC is in progress, false otherwise.
    bool IsConcurrentGCInProgress();

    // Temporarily enables concurrent GC, used during profiling.
    void TemporaryEnableConcurrentGC();

    // Temporarily disables concurrent GC, used during profiling.
    void TemporaryDisableConcurrentGC();

    // Returns whether or not Concurrent GC is enabled.
    bool IsConcurrentGCEnabled();

    // Wait for a concurrent GC to complete if one is in progress, with the given timeout.
    HResult WaitUntilConcurrentGCCompleteAsync(int millisecondsTimeout);    // Use in native threads. TRUE if succeed. FALSE if failed or timeout


    /*
    ===========================================================================
    Finalization routines. These are used by the finalizer thread to communicate
    with the GC.
    ===========================================================================
    */

    // Gets the number of finalizable objects.
    nint GetNumberOfFinalizable();

    // Gets the next finalizable object.
    GCObject* GetNextFinalizable();

    /*
    ===========================================================================
    BCL routines. These are routines that are directly exposed by CoreLib
    as a part of the `System.GC` class. These routines behave in the same
    manner as the functions on `System.GC`.
    ===========================================================================
    */

    // Gets memory related information the last GC observed. Depending on the last arg, this could
    // be any last GC that got recorded, or of the kind specified by this arg. All info below is
    // what was observed by that last GC.
    // 
    // highMemLoadThreshold - physical memory load (in percentage) when GC will start to
    //   react aggressively to reclaim memory.
    // totalPhysicalMem - the total amount of phyiscal memory available on the machine and the memory
    //   limit set on the container if running in a container.
    // lastRecordedMemLoad - physical memory load in percentage.
    // lastRecordedHeapSizeBytes - total managed heap size.
    // lastRecordedFragmentation - total fragmentation in the managed heap.
    // totalCommittedBytes - total committed bytes by the managed heap.
    // promotedBytes - promoted bytes. 
    // pinnedObjectCount - # of pinned objects observed.
    // finalizationPendingCount - # of objects ready for finalization.
    // index - the index of the GC.
    // generation - the generation the GC collected.
    // pauseTimePct - the % pause time in GC so far since process started.
    // isCompaction - compacted or not.
    // isConcurrent - concurrent or not.
    // genInfoRaw - info about each generation.
    // pauseInfoRaw - pause info.
    void GetMemoryInfo(ulong* highMemLoadThresholdBytes,
                               ulong* totalAvailableMemoryBytes,
                               ulong* lastRecordedMemLoadBytes,
                               ulong* lastRecordedHeapSizeBytes,
                               ulong* lastRecordedFragmentationBytes,
                               ulong* totalCommittedBytes,
                               ulong* promotedBytes,
                               ulong* pinnedObjectCount,
                               ulong* finalizationPendingCount,
                               ulong* index,
                               uint* generation,
                               uint* pauseTimePct,
                               bool* isCompaction,
                               bool* isConcurrent,
                               ulong* genInfoRaw,
                               ulong* pauseInfoRaw,
                               int kind);

    // Get the last memory load in percentage observed by the last GC.
    uint GetMemoryLoad();

    // Gets the current GC latency mode.
    int GetGcLatencyMode();

    // Sets the current GC latency mode. newLatencyMode has already been
    // verified by CoreLib to be valid.
    int SetGcLatencyMode(int newLatencyMode);

    // Gets the current LOH compaction mode.
    int GetLOHCompactionMode();

    // Sets the current LOH compaction mode. newLOHCompactionMode has
    // already been verified by CoreLib to be valid.
    void SetLOHCompactionMode(int newLOHCompactionMode);

    // Registers for a full GC notification, raising a notification if the gen 2 or
    // LOH object heap thresholds are exceeded.
    bool RegisterForFullGCNotification(uint gen2Percentage, uint lohPercentage);

    // Cancels a full GC notification that was requested by `RegisterForFullGCNotification`.
    bool CancelFullGCNotification();

    // Returns the status of a registered notification for determining whether a blocking
    // Gen 2 collection is about to be initiated, with the given timeout.
    int WaitForFullGCApproach(int millisecondsTimeout);

    // Returns the status of a registered notification for determining whether a blocking
    // Gen 2 collection has completed, with the given timeout.
    int WaitForFullGCComplete(int millisecondsTimeout);

    // Returns the generation in which obj is found. Also used by the VM
    // in some places, in particular syncblk code.
    uint WhichGeneration(GCObject* obj);

    // Returns the number of GCs that have transpired in the given generation
    // since the beginning of the life of the process. Also used by the VM
    // for debug code.
    int CollectionCount(int generation, int get_bgc_fgc_coutn);

    // Begins a no-GC region, returning a code indicating whether entering the no-GC
    // region was successful.
    int StartNoGCRegion(ulong totalSize, bool lohSizeKnown, ulong lohSize, bool disallowFullBlockingGC);

    // Exits a no-GC region.
    int EndNoGCRegion();

    // Gets the total number of bytes in use.
    nint GetTotalBytesInUse();

    ulong GetTotalAllocatedBytes();

    // Forces a garbage collection of the given generation. Also used extensively
    // throughout the VM.
    HResult GarbageCollect(int generation, bool low_memory_p, int mode);

    // Gets the largest GC generation. Also used extensively throughout the VM.
    uint GetMaxGeneration();

    // Indicates that an object's finalizer should not be run upon the object's collection.
    void SetFinalizationRun(GCObject* obj);

    // Indicates that an object's finalizer should be run upon the object's collection.
    bool RegisterForFinalization(int gen, GCObject* obj);

    int GetLastGCPercentTimeInGC();

    nint GetLastGCGenerationSize(int gen);

    /*
    ===========================================================================
    Miscellaneous routines used by the VM.
    ===========================================================================
    */

    // Initializes the GC heap, returning whether or not the initialization
    // was successful.
    HResult Initialize();

    // Returns whether nor this GC was promoted by the last GC.
    bool IsPromoted(GCObject* obj);

    // Returns true if this pointer points into a GC heap, false otherwise.
    bool IsHeapPointer(void* obj, bool small_heap_only);

    // Return the generation that has been condemned by the current GC.
    uint GetCondemnedGeneration();

    // Returns whether or not a GC is in progress.
    bool IsGCInProgressHelper(bool bConsiderGCStart = false);

    // Returns the number of GCs that have occured. Mainly used for
    // sanity checks asserting that a GC has not occured.
    uint GetGcCount();

    // Gets whether or not the home heap of this alloc context matches the heap
    // associated with this thread.
    bool IsThreadUsingAllocationContextHeap(gc_alloc_context* acontext, int thread_number);

    // Returns whether or not this object resides in an ephemeral generation.
    bool IsEphemeral(GCObject* obj);

    // Blocks until a GC is complete, returning a code indicating the wait was successful.
    uint WaitUntilGCComplete(bool bConsiderGCStart = false);

    // "Fixes" an allocation context by binding its allocation pointer to a
    // location on the heap.
    void FixAllocContext(gc_alloc_context* acontext, void* arg, void* heap);

    // Gets the total survived size plus the total allocated bytes on the heap.
    nint GetCurrentObjSize();

    // Sets whether or not a GC is in progress.
    void SetGCInProgress(bool fInProgress);

    // Gets whether or not the GC runtime structures are in a valid state for heap traversal.
    bool RuntimeStructuresValid();

    // Tells the GC when the VM is suspending threads.
    void SetSuspensionPending(bool fSuspensionPending);

    // Tells the GC how many YieldProcessor calls are equal to one scaled yield processor call.
    void SetYieldProcessorScalingFactor(float yieldProcessorScalingFactor);

    // Flush the log and close the file if GCLog is turned on.
    void Shutdown();

    /*
    ============================================================================
    Add/RemoveMemoryPressure support routines. These are on the interface
    for now, but we should move Add/RemoveMemoryPressure from the VM to the GC.
    When that occurs, these three routines can be removed from the interface.
    ============================================================================
    */

    // Get the timestamp corresponding to the last GC that occured for the
    // given generation.
    nint GetLastGCStartTime(int generation);

    // Gets the duration of the last GC that occured for the given generation.
    nint GetLastGCDuration(int generation);

    // Gets a timestamp for the current moment in time.
    nint GetNow();

    /*
    ===========================================================================
    Allocation routines. These all call into the GC's allocator and may trigger a garbage
    collection. All allocation routines return NULL when the allocation request
    couldn't be serviced due to being out of memory.
    ===========================================================================
    */

    // Allocates an object on the given allocation context with the given size and flags.
    // It is the responsibility of the caller to ensure that the passed-in alloc context is
    // owned by the thread that is calling this function. If using per-thread alloc contexts,
    // no lock is needed; callers not using per-thread alloc contexts will need to acquire
    // a lock to ensure that the calling thread has unique ownership over this alloc context;
    GCObject* Alloc(gc_alloc_context* acontext, nint size, uint flags);

    // This is for the allocator to indicate it's done allocating a large object during a
    // background GC as the BGC threads also need to walk UOH.
    void PublishObject(byte* obj);

    // Signals the WaitForGCEvent event, indicating that a GC has completed.
    void SetWaitForGCEvent();

    // Resets the state of the WaitForGCEvent back to an unsignalled state.
    void ResetWaitForGCEvent();

    /*
    ===========================================================================
    Heap verification routines. These are used during heap verification only.
    ===========================================================================
    */
    // Returns whether or not this object is too large for SOH.
    bool IsLargeObject(GCObject* pObj);

    // Walks an object and validates its members.
    void ValidateObjectMember(GCObject* obj);

    // Retrieves the next object after the given object. When the EE
    // is not suspended, the result is not accurate - if the input argument
    // is in Gen0, the function could return zeroed out memory as the next object.
    GCObject* NextObj(GCObject* obj);

    // Given an interior pointer, return a pointer to the object
    // containing that pointer. This is safe to call only when the EE is suspended.
    // When fCollectedGenOnly is true, it only returns the object if it's found in
    // the generation(s) that are being collected.
    GCObject* GetContainingObject(void* pInteriorPtr, bool fCollectedGenOnly);

    /*
    ===========================================================================
    Profiling routines. Used for event tracing and profiling to broadcast
    information regarding the heap.
    ===========================================================================
    */

    // Walks an object, invoking a callback on each member.
    void DiagWalkObject(GCObject* obj, void* fn, void* context);
    //void DiagWalkObject(GCObject* obj, delegate* unmanaged[Stdcall]<GCObject*, void*, bool> fn, void* context);

    // Walks an object, invoking a callback on each member.
    void DiagWalkObject2(GCObject* obj, void* fn, void* context);
    //void DiagWalkObject2(GCObject* obj, delegate* unmanaged[Stdcall]<GCObject*, byte**, void*, bool> fn, void* context);

    // Walk the heap object by object.
    void DiagWalkHeap(void* fn, void* context, int gen_number, bool walk_large_object_heap_p);
    //void DiagWalkHeap(delegate* unmanaged[Stdcall]<GCObject*, void*, bool> fn, void* context, int gen_number, bool walk_large_object_heap_p);

    // Walks the survivors and get the relocation information if objects have moved.
    // gen_number is used when type == walk_for_uoh, otherwise ignored
    void DiagWalkSurvivorsWithType(void* gc_context, void* fn, void* diag_context, walk_surv_type type, int gen_number = -1);

    // Walks the finalization queue.
    void DiagWalkFinalizeQueue(void* gc_context, void* fn);

    // Scan roots on finalizer queue. This is a generic function.
    void DiagScanFinalizeQueue(void* fn, void* context);

    // Scan handles for profiling or ETW.
    void DiagScanHandles(void* fn, int gen_number, void* context);

    // Scan dependent handles for profiling or ETW.
    void DiagScanDependentHandles(void* fn, int gen_number, void* context);

    // Describes all generations to the profiler, invoking a callback on each generation.
    void DiagDescrGenerations(void* fn, void* context);

    // Traces all GC segments and fires ETW events with information on them.
    void DiagTraceGCSegments();

    // Get GC settings for tracing purposes. These are settings not obvious from a trace.
    void DiagGetGCSettings(void* settings);

    /*
    ===========================================================================
    GC Stress routines. Used only when running under GC Stress.
    ===========================================================================
    */

    // Returns TRUE if GC actually happens, otherwise FALSE. The passed alloc context
    // must not be null.
    bool StressHeap(gc_alloc_context* acontext);

    /*
    ===========================================================================
    Routines to register read only segments for frozen objects.
    Only valid if FEATURE_BASICFREEZE is defined.
    ===========================================================================
    */

    // Registers a frozen segment with the GC.
    void* RegisterFrozenSegment(segment_info* pseginfo);

    // Unregisters a frozen segment.
    void UnregisterFrozenSegment(void* seg);

    // Indicates whether an object is in a frozen segment.
    bool IsInFrozenSegment(GCObject* obj);

    /*
    ===========================================================================
    Routines for informing the GC about which events are enabled.
    ===========================================================================
    */

    // Enables or disables the given keyword or level on the default event provider.
    void ControlEvents(GCEventKeyword keyword, GCEventLevel level);

    // Enables or disables the given keyword or level on the private event provider.
    void ControlPrivateEvents(GCEventKeyword keyword, GCEventLevel level);

    uint GetGenerationWithRange(GCObject* obj, byte** ppStart, byte** ppAllocated, byte** ppReserved);

}