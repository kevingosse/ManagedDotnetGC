namespace TestApp.TestFramework;

/// <summary>
/// The functional area a test exercises. Every test declares one.
///
/// Tests whose feature is listed in <c>Program.PendingFeatures</c> (not implemented yet in
/// ManagedDotnetGC) are skipped by default and only run when explicitly requested with
/// <c>--feature &lt;name&gt;</c> or <c>--all-features</c>. To start working on a feature, remove it
/// from that list and watch its tests fail.
/// </summary>
public enum GcFeature
{
    // ── Implemented (existing coverage) ─────────────────────────────────────────────
    Allocation,
    Marking,
    Stress,
    InteriorPointers,
    Handles,
    WeakReferences,
    DependentHandles,
    Finalization,
    FrozenSegments,
    SyncBlocks,
    GenerationApis,          // 6.2: GC.GetGeneration / GC.MaxGeneration (WhichGeneration, GetMaxGeneration)

    // ── Pending (see docs/missing-features.md for the corresponding items) ──────────
    CollectibleAssemblies,   // 3.1 + 4.1/4.2: LoaderAllocator edge, weak interior pointer handles
    RefCountedHandles,       // 3.2: HNDTYPE_REFCOUNTED scanning/clearing + RefCountedHandleCallbacks
    FinalizationQueueRoots,  // 3.3: f-reachable queue promoted early in the mark phase
    FrozenDependentHandles,  // 3.4: frozen-segment objects as dependent-handle primary/secondary
    SuppressFinalizeDrop,    // 5.1: suppressed dead objects dropped at scan, not resurrected
    ApiSurface,              // 6.1: misc GC APIs (RefreshMemoryLimit, ...)
    LatencyMode,             // 6.1: GCSettings.LatencyMode get/set
    NoGCRegion,              // 6.1: GC.TryStartNoGCRegion / EndNoGCRegion
    EventCounters,           // 6.1: GetGenerationBudget etc. polled by RuntimeEventSource
    AllocationAccounting,    // 8.1: alloc_bytes bookkeeping (GetAllocatedBytesForCurrentThread)
    MemoryInfo,              // 6.1/10: GetGCMemoryInfo / GetTotalMemory
    GcTriggering,            // 1.1: allocation-triggered collections
    MemoryReuse,             // 1.2: swept memory is reused / decommitted
    HardLimitOom,            // 1.3: GCHeapHardLimit honored + null-on-OOM
    GcEvents,                // 10: EventSink GC events (GCStart/GCEnd)
    GcInternals,             // Category B probes via ManagedDotnetGC.Api (custom GC only)
}
