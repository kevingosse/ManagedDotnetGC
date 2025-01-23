namespace ManagedDotnetGC.Interfaces;

[NativeObject]
public unsafe interface IGCToCLR
{
    // Suspends the EE for the given reason.
    void SuspendEE(SUSPEND_REASON reason);

    // Resumes all paused threads, with a boolean indicating
    // if the EE is being restarted because a GC is complete.
    void RestartEE(bool bFinishedGC);

    // Performs a stack walk of all managed threads and invokes the given promote_func
    // on all GC roots encountered on the stack. Depending on the condemned generation,
    // this function may also enumerate all static GC refs if necessary.
    void GcScanRoots(void* fn, int condemned, int max_gen, void* sc);

    // Callback from the GC informing the EE that it is preparing to start working.
    void GcStartWork(int condemned, int max_gen);

    // Callback from the GC informing the EE that the scanning of roots is about
    // to begin.
    void BeforeGcScanRoots(int condemned, bool is_bgc, bool is_concurrent);

    // Callback from the GC informing the EE that it has completed the managed stack
    // scan. User threads are still suspended at this point.
    void AfterGcScanRoots(int condemned, int max_gen, void* sc);

    // Callback from the GC informing the EE that a GC has completed.
    void GcDone(int condemned);

    // Predicate for the GC to query whether or not a given refcounted handle should
    // be promoted.
    bool RefCountedHandleCallbacks(GCObject* pObject);

    // Performs a weak pointer scan of the sync block cache.
    void SyncBlockCacheWeakPtrScan(void* scanProc, nint lp1, nint lp2);

    // Indicates to the EE that the GC intends to demote objects in the sync block cache.

    void SyncBlockCacheDemote(int max_gen);

    // Indicates to the EE that the GC has granted promotion to objects in the sync block cache.
    void SyncBlockCachePromotionsGranted(int max_gen);

    uint GetActiveSyncBlockCount();

    // Queries whether or not the current thread has preemptive GC disabled.
    bool IsPreemptiveGCDisabled();

    // Enables preemptive GC on the current thread. Returns true if the thread mode
    // was changed and false if the thread mode wasn't changed or the thread is not
    // a managed thread.
    bool EnablePreemptiveGC();

    // Disables preemptive GC on the current thread.
    void DisablePreemptiveGC();

    // Gets the Thread instance for the current thread, or null if no thread
    // instance is associated with this thread.
    //
    // If the GC created the current thread, GetThread returns null for threads
    // that were not created as suspendable (see `IGCHeap::CreateThread`).

    void* GetThread();

    // Retrieves the alloc context associated with the current thread.

    gc_alloc_context* GetAllocContext();

    // Calls the given enum_alloc_context_func with every active alloc context.

    void GcEnumAllocContexts(void* fn, void* param);

    // Get the Allocator for objects from collectible assemblies

    byte* GetLoaderAllocatorObjectForGC(GCObject* pObject);

    // Creates and returns a new thread.
    // Parameters:
    //  threadStart - The function that will serve as the thread stub for the
    //                new thread. It will be invoked immediately upon the
    //                new thread upon creation.
    //  arg - The argument that will be passed verbatim to threadStart.
    //  is_suspendable - Whether or not the thread that is created should be suspendable
    //                   from a runtime perspective. Threads that are suspendable have
    //                   a VM Thread object associated with them that can be accessed
    //                   using `IGCHeap::GetThread`.
    //  name - The name of this thread, optionally used for diagnostic purposes.
    // Returns:
    //  true if the thread was started successfully, false if not.
    bool CreateThread(void* threadStart, void* arg, bool is_suspendable, char* name);

    // When a GC starts, gives the diagnostics code a chance to run.
    void DiagGCStart(int gen, bool isInduced);

    // When GC heap segments change, gives the diagnostics code a chance to run.
    void DiagUpdateGenerationBounds();

    // When a GC ends, gives the diagnostics code a chance to run.
    void DiagGCEnd(nint index, int gen, int reason, bool fConcurrent);

    // During a GC after we discover what objects' finalizers should run, gives the diagnostics code a chance to run.
    void DiagWalkFReachableObjects(void* gcContext);

    // During a GC after we discover the survivors and the relocation info,
    // gives the diagnostics code a chance to run. This includes LOH if we are
    // compacting LOH.
    void DiagWalkSurvivors(void* gcContext, bool fCompacting);

    // During a full GC after we discover what objects to survive on UOH,
    // gives the diagnostics code a chance to run.
    void DiagWalkUOHSurvivors(void* gcContext, int gen);

    // At the end of a background GC, gives the diagnostics code a chance to run.
    void DiagWalkBGCSurvivors(void* gcContext);

    // Informs the EE of changes to the location of the card table, potentially updating the write
    // barrier if it needs to be updated.
    void StompWriteBarrier(WriteBarrierParameters* args);

    // Signals to the finalizer thread that there are objects ready to
    // be finalized.
    void EnableFinalization(bool foundFinalizers);

    // Signals to the EE that the GC encountered a fatal error and can't recover.
    void HandleFatalError(uint exitCode);

    // Offers the EE the option to finalize the given object eagerly, i.e.
    // not on the finalizer thread but on the current thread. The
    // EE returns true if it finalized the object eagerly and the GC does not
    // need to do so, and false if it chose not to eagerly finalize the object
    // and it's up to the GC to finalize it later.
    bool EagerFinalized(GCObject* obj);

    // Retrieves the method table for the free object, a special kind of object used by the GC
    // to keep the heap traversable. Conceptually, the free object is similar to a managed array
    // of bytes: it consists of an object header (like all objects) and a "numComponents" field,
    // followed by some number of bytes of space that's free on the heap.
    //
    // The free object allows the GC to traverse the heap because it can inspect the numComponents
    // field to see how many bytes to skip before the next object on a heap segment begins.
    void* GetFreeObjectMethodTable();

    // Asks the EE for the value of a given configuration key. If the EE does not know or does not
    // have a value for the requeested config key, false is returned and the value of the passed-in
    // pointer is undefined. Otherwise, true is returned and the config key's value is written to
    // the passed-in pointer.
    bool GetBooleanConfigValue(char* privateKey, char* publicKey, bool* value);

    bool GetIntConfigValue(char* privateKey, char* publicKey, long* value);

    bool GetStringConfigValue(char* privateKey, char* publicKey, char** value);

    void FreeStringConfigValue(char* value);

    // Returns true if this thread is a "GC thread", or a thread capable of
    // doing GC work. Threads are either /always/ GC threads
    // (if they were created for this purpose - background GC threads
    // and server GC threads) or they became GC threads by suspending the EE
    // and initiating a collection.
    bool IsGCThread();

    // Returns true if the current thread is either a background GC thread
    // or a server GC thread.
    bool WasCurrentThreadCreatedByGC();

    // Given an object, if this object is an instance of `System.Threading.OverlappedData`,
    // and the runtime treats instances of this class specially, traverses the objects that
    // are directly or (once) indirectly pinned by this object and reports them to the GC for
    // the purposes of relocation and promotion.
    //
    // Overlapped objects are very special and as such the objects they wrap can't be promoted in
    // the same manner as normal objects. This callback gives the EE the opportunity to hide these
    // details, if they are implemented at all.
    //
    // This function is a no-op if "object" is not an OverlappedData object.
    void WalkAsyncPinnedForPromotion(GCObject* obj, void* sc, void* callback);

    // Given an object, if this object is an instance of `System.Threading.OverlappedData` and the
    // runtime treats instances of this class specially, traverses the objects that are directly
    // or once indirectly pinned by this object and invokes the given callback on them. The callback
    // is passed the following arguments:
    //     Object* "from" - The object that "caused" the "to" object to be pinned. If a single object
    //                      is pinned directly by this OverlappedData, this object will be the
    //                      OverlappedData object itself. If an array is pinned by this OverlappedData,
    //                      this object will be the pinned array.
    //     Object* "to"   - The object that is pinned by the "from" object. If a single object is pinned
    //                      by an OverlappedData, "to" will be that single object. If an array is pinned
    //                      by an OverlappedData, the callback will be invoked on all elements of that
    //                      array and each element will be a "to" object.
    //     void* "context" - Passed verbatim from "WalkOverlappedObject" to the callback function.
    // The "context" argument will be passed directly to the callback without modification or inspection.
    //
    // This function is a no-op if "object" is not an OverlappedData object.
    void WalkAsyncPinned(GCObject* obj, void* context, void* func);

    // Returns an IGCToCLREventSink instance that can be used to fire events.
    void* EventSink();

    uint GetTotalNumSizedRefHandles();

    bool AnalyzeSurvivorsRequested(int condemnedGeneration);

    void AnalyzeSurvivorsFinished(nint gcIndex, int condemnedGeneration, ulong promoted_bytes, void* reportGenerationBounds);

    void VerifySyncTableEntry();

    void UpdateGCEventStatus(int publicLevel, int publicKeywords, int privateLEvel, int privateKeywords);

    void LogStressMsg(uint level, uint facility, nint msg);

    uint GetCurrentProcessCpuCount();

    void DiagAddNewRegion(int generation, byte* rangeStart, byte* rangeEnd, byte* rangeEndReserved);

    // The following method is available only with EE_INTERFACE_MAJOR_VERSION >= 1
    void LogErrorToHost(byte* message);

    ulong GetThreadOSThreadId(IntPtr thread);
}