namespace ManagedDotnetGC.Api;

[NativeObject]
public interface IGc
{
    uint GetSyncBlockCacheCount();

    // Probes that route a test-provided address through the GC's own IGCHeap query
    // implementations (docs/missing-features.md item 6.3). The tri-state queries return
    // GcProbe.True/False, or a GcProbe sentinel when the underlying method is not implemented
    // or threw. GetContainingObject returns the containing object's address, 0 when the address
    // is not inside an object, or a GcProbe sentinel.
    nint GetContainingObject(nint address);
    int IsHeapPointer(nint address);
    int IsPromoted(nint address);

    // Number of times the GC issued the given EE bracket notification since startup
    // (docs/missing-features.md item 2.1).
    int GetGcCallbackCount(GcCallbackKind kind);

    // Field of the WriteBarrierParameters passed to the EE on the most recent
    // StompWriteBarrier call (docs/missing-features.md item 7.1).
    ulong GetWriteBarrierParameter(WriteBarrierParameterKind kind);
}

/// <summary>
/// Sentinel values returned by the <see cref="IGc"/> probe methods.
/// </summary>
public static class GcProbe
{
    /// <summary>The underlying IGCHeap method throws NotImplementedException.</summary>
    public const int NotImplemented = -1;

    /// <summary>The underlying IGCHeap method threw an unexpected exception (see the GC log).</summary>
    public const int Error = -2;

    public const int False = 0;
    public const int True = 1;
}

/// <summary>
/// The EE bracket notifications the GC must issue around a collection
/// (docs/missing-features.md item 2.1).
/// </summary>
public enum GcCallbackKind
{
    GcStartWork,
    BeforeGcScanRoots,
    AfterGcScanRoots,
    GcDone,

    /// <summary>Number of times a bracket notification was issued out of order.</summary>
    OrderViolation,
}

/// <summary>
/// Fields of the WriteBarrierParameters struct passed to the EE (docs/missing-features.md item 7.1).
/// </summary>
public enum WriteBarrierParameterKind
{
    CardTable,
    CardBundleTable,
    LowestAddress,
    HighestAddress,
    EphemeralLow,
    EphemeralHigh,
}
