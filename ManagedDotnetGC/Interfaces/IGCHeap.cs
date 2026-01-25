namespace ManagedDotnetGC.Interfaces;

[NativeObject]
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

    /// <summary>
    /// Returns whether or not the given size is a valid segment size.
    /// </summary>
    bool IsValidSegmentSize(nint size);

    /// <summary>
    /// Returns whether or not the given size is a valid gen 0 max size.
    /// </summary>
    bool IsValidGen0MaxSize(nint size);

    /// <summary>
    /// Gets a valid segment size.
    /// </summary>
    nint GetValidSegmentSize(bool large_seg = false);

    /// <summary>
    /// Sets the limit for reserved memory.
    /// </summary>
    void SetReservedVMLimit(nint vmlimit);

    /*
    ===========================================================================
    Concurrent GC routines. These are used in various places in the VM
    to synchronize with the GC, when the VM wants to update something that
    the GC is potentially using, if it's doing a background GC.

    Concrete examples of this are profiling/ETW scenarios.
    ===========================================================================
    */

    /// <summary>
    /// Blocks until any running concurrent GCs complete.
    /// </summary>
    void WaitUntilConcurrentGCComplete();

    /// <summary>
    /// Returns true if a concurrent GC is in progress, false otherwise.
    /// </summary>
    bool IsConcurrentGCInProgress();

    /// <summary>
    /// Temporarily enables concurrent GC, used during profiling.
    /// </summary>
    void TemporaryEnableConcurrentGC();

    /// <summary>
    /// Temporarily disables concurrent GC, used during profiling.
    /// </summary>
    void TemporaryDisableConcurrentGC();

    /// <summary>
    /// Returns whether or not Concurrent GC is enabled.
    /// </summary>
    bool IsConcurrentGCEnabled();

    /// <summary>
    /// Wait for a concurrent GC to complete if one is in progress, with the given timeout.
    /// </summary>
    HResult WaitUntilConcurrentGCCompleteAsync(int millisecondsTimeout);    // Use in native threads. TRUE if succeed. FALSE if failed or timeout


    /*
    ===========================================================================
    Finalization routines. These are used by the finalizer thread to communicate
    with the GC.
    ===========================================================================
    */

    /// <summary>
    /// Gets the number of finalizable objects.
    /// </summary>
    nint GetNumberOfFinalizable();

    /// <summary>
    /// Gets the next finalizable object.
    /// </summary>
    GCObject* GetNextFinalizable();

    /*
    ===========================================================================
    BCL routines. These are routines that are directly exposed by CoreLib
    as a part of the `System.GC` class. These routines behave in the same
    manner as the functions on `System.GC`.
    ===========================================================================
    */

    /// <summary>
    /// Gets memory related information the last GC observed. Depending on the last arg, this could
    /// be any last GC that got recorded, or of the kind specified by this arg. All info below is
    /// what was observed by that last GC.    /// </summary>
    /// <param name="highMemLoadThresholdBytes">physical memory load (in percentage) when GC will start to
    ///   react aggressively to reclaim memory.</param>
    /// <param name="totalAvailableMemoryBytes">the total amount of phyiscal memory available on the machine and the memory
    ///   limit set on the container if running in a container.</param>
    /// <param name="lastRecordedMemLoadBytes">physical memory load in percentage.</param>
    /// <param name="lastRecordedHeapSizeBytes">total managed heap size.</param>
    /// <param name="lastRecordedFragmentationBytes">total fragmentation in the managed heap.</param>
    /// <param name="totalCommittedBytes">total committed bytes by the managed heap.</param>
    /// <param name="promotedBytes">promoted bytes.</param>
    /// <param name="pinnedObjectCount"># of pinned objects observed.</param>
    /// <param name="finalizationPendingCount"># of objects ready for finalization.</param>
    /// <param name="index">the index of the GC.</param>
    /// <param name="generation">the generation the GC collected.</param>
    /// <param name="pauseTimePct">the % pause time in GC so far since process started.</param>
    /// <param name="isCompaction">compacted or not.</param>
    /// <param name="isConcurrent">concurrent or not.</param>
    /// <param name="genInfoRaw">info about each generation.</param>
    /// <param name="pauseInfoRaw">pause info.</param>
    /// <param name="kind"></param>
    void GetMemoryInfo(out ulong highMemLoadThresholdBytes,
                       out ulong totalAvailableMemoryBytes,
                       out ulong lastRecordedMemLoadBytes,
                       out ulong lastRecordedHeapSizeBytes,
                       out ulong lastRecordedFragmentationBytes,
                       out ulong totalCommittedBytes,
                       out ulong promotedBytes,
                       out ulong pinnedObjectCount,
                       out ulong finalizationPendingCount,
                       out ulong index,
                       out uint generation,
                       out uint pauseTimePct,
                       out bool isCompaction,
                       out bool isConcurrent,
                       out ulong genInfoRaw,
                       out ulong pauseInfoRaw,
                       int kind);

    /// <summary>
    /// Get the last memory load in percentage observed by the last GC.
    /// </summary>
    uint GetMemoryLoad();

    /// <summary>
    /// Gets the current GC latency mode.
    /// </summary>
    int GetGcLatencyMode();

    /// <summary>
    /// Sets the current GC latency mode. newLatencyMode has already been
    /// verified by CoreLib to be valid.
    /// </summary>
    int SetGcLatencyMode(int newLatencyMode);

    /// <summary>
    /// Gets the current LOH compaction mode.
    /// </summary>
    int GetLOHCompactionMode();

    /// <summary>
    /// Sets the current LOH compaction mode. newLOHCompactionMode has
    /// already been verified by CoreLib to be valid.
    /// </summary>
    void SetLOHCompactionMode(int newLOHCompactionMode);

    /// <summary>
    /// Registers for a full GC notification, raising a notification if the gen 2 or
    /// LOH object heap thresholds are exceeded.
    /// </summary>
    bool RegisterForFullGCNotification(uint gen2Percentage, uint lohPercentage);

    /// <summary>
    /// Cancels a full GC notification that was requested by `RegisterForFullGCNotification`.
    /// </summary>
    bool CancelFullGCNotification();

    /// <summary>
    /// Returns the status of a registered notification for determining whether a blocking
    /// Gen 2 collection is about to be initiated, with the given timeout.
    /// </summary>
    int WaitForFullGCApproach(int millisecondsTimeout);

    /// <summary>
    /// Returns the status of a registered notification for determining whether a blocking
    /// Gen 2 collection has completed, with the given timeout.
    /// </summary>
    int WaitForFullGCComplete(int millisecondsTimeout);

    /// <summary>
    /// Returns the generation in which obj is found. Also used by the VM
    /// in some places, in particular syncblk code.
    /// </summary>
    uint WhichGeneration(GCObject* obj);

    /// <summary>
    /// Returns the number of GCs that have transpired in the given generation
    /// since the beginning of the life of the process. Also used by the VM
    /// for debug code.
    /// </summary>
    int CollectionCount(int generation, int get_bgc_fgc_coutn);

    /// <summary>
    /// Begins a no-GC region, returning a code indicating whether entering the no-GC
    /// region was successful.
    /// </summary>
    int StartNoGCRegion(ulong totalSize, bool lohSizeKnown, ulong lohSize, bool disallowFullBlockingGC);

    /// <summary>
    /// Exits a no-GC region.
    /// </summary>
    int EndNoGCRegion();

    /// <summary>
    /// Gets the total number of bytes in use.
    /// </summary>
    nint GetTotalBytesInUse();

    ulong GetTotalAllocatedBytes();

    /// <summary>
    /// Forces a garbage collection of the given generation. Also used extensively
    /// throughout the VM.
    /// </summary>
    HResult GarbageCollect(int generation, bool low_memory_p, int mode);

    /// <summary>
    /// Gets the largest GC generation. Also used extensively throughout the VM.
    /// </summary>
    uint GetMaxGeneration();

    /// <summary>
    /// Indicates that an object's finalizer should not be run upon the object's collection.
    /// </summary>
    void SetFinalizationRun(GCObject* obj);

    /// <summary>
    /// Indicates that an object's finalizer should be run upon the object's collection.
    /// </summary>
    bool RegisterForFinalization(int gen, GCObject* obj);

    int GetLastGCPercentTimeInGC();

    nint GetLastGCGenerationSize(int gen);

    /*
    ===========================================================================
    Miscellaneous routines used by the VM.
    ===========================================================================
    */

    /// <summary>
    /// Initializes the GC heap, returning whether or not the initialization
    /// was successful.
    /// </summary>
    HResult Initialize();

    /// <summary>
    /// Returns whether nor this GC was promoted by the last GC.
    /// </summary>
    bool IsPromoted(GCObject* obj);

    /// <summary>
    /// Returns true if this pointer points into a GC heap, false otherwise.
    /// </summary>
    bool IsHeapPointer(IntPtr obj, bool small_heap_only);

    /// <summary>
    /// Return the generation that has been condemned by the current GC.
    /// </summary>
    uint GetCondemnedGeneration();

    /// <summary>
    /// Returns whether or not a GC is in progress.
    /// </summary>
    bool IsGCInProgressHelper(bool bConsiderGCStart = false);

    /// <summary>
    /// Returns the number of GCs that have occured. Mainly used for
    /// sanity checks asserting that a GC has not occured.
    /// </summary>
    uint GetGcCount();

    /// <summary>
    /// Gets whether or not the home heap of this alloc context matches the heap
    /// associated with this thread.
    /// </summary>
    bool IsThreadUsingAllocationContextHeap(gc_alloc_context* acontext, int thread_number);

    /// <summary>
    /// Returns whether or not this object resides in an ephemeral generation.
    /// </summary>
    bool IsEphemeral(GCObject* obj);

    /// <summary>
    /// Blocks until a GC is complete, returning a code indicating the wait was successful.
    /// </summary>
    uint WaitUntilGCComplete(bool bConsiderGCStart = false);

    /// <summary>
    /// "Fixes" an allocation context by binding its allocation pointer to a
    /// location on the heap.
    /// </summary>
    void FixAllocContext(gc_alloc_context* acontext, void* arg, void* heap);

    /// <summary>
    /// Gets the total survived size plus the total allocated bytes on the heap.
    /// </summary>
    nint GetCurrentObjSize();

    /// <summary>
    /// Sets whether or not a GC is in progress.
    /// </summary>
    void SetGCInProgress(bool fInProgress);

    /// <summary>
    /// Gets whether or not the GC runtime structures are in a valid state for heap traversal.
    /// </summary>
    bool RuntimeStructuresValid();

    /// <summary>
    /// Tells the GC when the VM is suspending threads.
    /// </summary>
    void SetSuspensionPending(bool fSuspensionPending);

    /// <summary>
    /// Tells the GC how many YieldProcessor calls are equal to one scaled yield processor call.
    /// </summary>
    void SetYieldProcessorScalingFactor(float yieldProcessorScalingFactor);

    /// <summary>
    /// Flush the log and close the file if GCLog is turned on.
    /// </summary>
    void Shutdown();

    /*
    ============================================================================
    Add/RemoveMemoryPressure support routines. These are on the interface
    for now, but we should move Add/RemoveMemoryPressure from the VM to the GC.
    When that occurs, these three routines can be removed from the interface.
    ============================================================================
    */

    /// <summary>
    /// Get the timestamp corresponding to the last GC that occured for the
    /// given generation.
    /// </summary>
    nint GetLastGCStartTime(int generation);

    /// <summary>
    /// Gets the duration of the last GC that occured for the given generation.
    /// </summary>
    nint GetLastGCDuration(int generation);

    /// <summary>
    /// Gets a timestamp for the current moment in time.
    /// </summary>
    nint GetNow();

    /*
    ===========================================================================
    Allocation routines. These all call into the GC's allocator and may trigger a garbage
    collection. All allocation routines return NULL when the allocation request
    couldn't be serviced due to being out of memory.
    ===========================================================================
    */

    /// <summary>
    /// Allocates an object on the given allocation context with the given size and flags.
    /// It is the responsibility of the caller to ensure that the passed-in alloc context is
    /// owned by the thread that is calling this function. If using per-thread alloc contexts,
    /// no lock is needed; callers not using per-thread alloc contexts will need to acquire
    /// a lock to ensure that the calling thread has unique ownership over this alloc context;
    /// </summary>
    GCObject* Alloc(ref gc_alloc_context acontext, nint size, GC_ALLOC_FLAGS flags);

    /// <summary>
    /// This is for the allocator to indicate it's done allocating a large object during a
    /// background GC as the BGC threads also need to walk UOH.
    /// </summary>
    void PublishObject(IntPtr obj);

    /// <summary>
    /// Signals the WaitForGCEvent event, indicating that a GC has completed.
    /// </summary>
    void SetWaitForGCEvent();

    /// <summary>
    /// Resets the state of the WaitForGCEvent back to an unsignalled state.
    /// </summary>
    void ResetWaitForGCEvent();

    /*
    ===========================================================================
    Heap verification routines. These are used during heap verification only.
    ===========================================================================
    */
    /// <summary>
    /// Returns whether or not this object is too large for SOH.
    /// </summary>
    bool IsLargeObject(GCObject* pObj);

    /// <summary>
    /// Walks an object and validates its members.
    /// </summary>
    void ValidateObjectMember(GCObject* obj);

    /// <summary>
    /// Retrieves the next object after the given object. When the EE
    /// is not suspended, the result is not accurate - if the input argument
    /// is in Gen0, the function could return zeroed out memory as the next object.
    /// </summary>
    GCObject* NextObj(GCObject* obj);

    /// <summary>
    /// Given an interior pointer, return a pointer to the object
    /// containing that pointer. This is safe to call only when the EE is suspended.
    /// When fCollectedGenOnly is true, it only returns the object if it's found in
    /// the generation(s) that are being collected.
    /// </summary>
    GCObject* GetContainingObject(IntPtr pInteriorPtr, bool fCollectedGenOnly);

    /*
    ===========================================================================
    Profiling routines. Used for event tracing and profiling to broadcast
    information regarding the heap.
    ===========================================================================
    */

    /// <summary>
    /// Walks an object, invoking a callback on each member.
    /// </summary>
    void DiagWalkObject(GCObject* obj, void* fn, void* context);
    //void DiagWalkObject(GCObject* obj, delegate* unmanaged[Stdcall]<GCObject*, void*, bool> fn, void* context);

    /// <summary>
    /// Walks an object, invoking a callback on each member.
    /// </summary>
    void DiagWalkObject2(GCObject* obj, void* fn, void* context);
    //void DiagWalkObject2(GCObject* obj, delegate* unmanaged[Stdcall]<GCObject*, byte**, void*, bool> fn, void* context);

    /// <summary>
    /// Walk the heap object by object.
    /// </summary>
    void DiagWalkHeap(void* fn, void* context, int gen_number, bool walk_large_object_heap_p);
    //void DiagWalkHeap(delegate* unmanaged[Stdcall]<GCObject*, void*, bool> fn, void* context, int gen_number, bool walk_large_object_heap_p);

    /// <summary>
    /// Walks the survivors and get the relocation information if objects have moved.
    /// gen_number is used when type == walk_for_uoh, otherwise ignored
    /// </summary>
    void DiagWalkSurvivorsWithType(void* gc_context, void* fn, void* diag_context, walk_surv_type type, int gen_number = -1);

    /// <summary>
    /// Walks the finalization queue.
    /// </summary>
    void DiagWalkFinalizeQueue(void* gc_context, void* fn);

    /// <summary>
    /// Scan roots on finalizer queue. This is a generic function.
    /// </summary>
    void DiagScanFinalizeQueue(void* fn, void* context);

    /// <summary>
    /// Scan handles for profiling or ETW.
    /// </summary>
    void DiagScanHandles(void* fn, int gen_number, void* context);

    /// <summary>
    /// Scan dependent handles for profiling or ETW.
    /// </summary>
    void DiagScanDependentHandles(void* fn, int gen_number, void* context);

    /// <summary>
    /// Describes all generations to the profiler, invoking a callback on each generation.
    /// </summary>
    void DiagDescrGenerations(void* fn, void* context);

    /// <summary>
    /// Traces all GC segments and fires ETW events with information on them.
    /// </summary>
    void DiagTraceGCSegments();

    /// <summary>
    /// Get GC settings for tracing purposes. These are settings not obvious from a trace.
    /// </summary>
    void DiagGetGCSettings(void* settings);

    /*
    ===========================================================================
    GC Stress routines. Used only when running under GC Stress.
    ===========================================================================
    */

    /// <summary>
    /// Returns TRUE if GC actually happens, otherwise FALSE. The passed alloc context
    /// must not be null.
    /// </summary>
    bool StressHeap(gc_alloc_context* acontext);

    /*
    ===========================================================================
    Routines to register read only segments for frozen objects.
    Only valid if FEATURE_BASICFREEZE is defined.
    ===========================================================================
    */

    /// <summary>
    /// Registers a frozen segment with the GC.
    /// </summary>
    nint RegisterFrozenSegment(segment_info* pseginfo);

    /// <summary>
    /// Unregisters a frozen segment.
    /// </summary>
    void UnregisterFrozenSegment(nint seg);

    /// <summary>
    /// Indicates whether an object is in a frozen segment.
    /// </summary>
    bool IsInFrozenSegment(GCObject* obj);

    /*
    ===========================================================================
    Routines for informing the GC about which events are enabled.
    ===========================================================================
    */

    /// <summary>
    /// Enables or disables the given keyword or level on the default event provider.
    /// </summary>
    void ControlEvents(GCEventKeyword keyword, GCEventLevel level);

    /// <summary>
    /// Enables or disables the given keyword or level on the private event provider.
    /// </summary>
    void ControlPrivateEvents(GCEventKeyword keyword, GCEventLevel level);

    uint GetGenerationWithRange(GCObject* obj, byte** ppStart, byte** ppAllocated, byte** ppReserved);

    /// <summary>
    /// Get the total paused duration.
    /// </summary>
    long GetTotalPauseDuration();

    /// <summary>
    /// Gets all the names and values of the GC configurations.
    /// </summary>
    void EnumerateConfigurationValues(void* context, IntPtr configurationValueFunc);

    /// <summary>
    /// Updates given frozen segment
    /// </summary>
    void UpdateFrozenSegment(IntPtr seg, IntPtr allocated, IntPtr committed);

    /// <summary>
    /// Refresh the memory limit
    /// </summary>
    int RefreshMemoryLimit();

    /// <summary>
    /// Enable NoGCRegionCallback
    /// </summary>
    enable_no_gc_region_callback_status EnableNoGCRegionCallback(IntPtr callback, ulong callback_threshold);

    /// <summary>
    /// Get extra work for the finalizer
    /// </summary>
    IntPtr GetExtraWorkForFinalization();

    ulong GetGenerationBudget(int generation);

    nint GetLOHThreshold();

    /// <summary>
    /// Walk the heap object by object outside of a GC.
    /// </summary>
    void DiagWalkHeapWithACHandling(IntPtr fn, void* context, int gen_number, bool walk_large_object_heap_p);
}