namespace TestApp.TestFramework;

/// <summary>
/// Base class for all GC tests
/// </summary>
public abstract class TestBase
{
    public string Name { get; }

    /// <summary>
    /// The functional area this test exercises. Used for feature-gating (pending features are
    /// skipped unless explicitly requested) and for running a feature's tests with --feature.
    /// </summary>
    public GcFeature Feature { get; }

    protected TestBase(string name, GcFeature feature)
    {
        Name = name;
        Feature = feature;
    }

    /// <summary>
    /// True for tests that need the ManagedDotnetGC.Api channel and therefore can only run on the
    /// custom GC. They are reported as skipped on the stock GC.
    /// </summary>
    public virtual bool RequiresCustomGcApi => false;

    /// <summary>
    /// Watchdog timeout for this test. Note: the in-process watchdog only catches hangs that leave
    /// the EE running (deadlocked WaitForPendingFinalizers, runaway loops). A hang *inside* the GC
    /// with the EE suspended also suspends the watchdog thread — only an external timeout (CI-level)
    /// can catch those.
    /// </summary>
    public virtual int TimeoutSeconds => 120;

    /// <summary>
    /// Run the test. Throws an exception with a descriptive message if the test fails.
    /// </summary>
    public abstract void Run();

    /// <summary>
    /// Optional setup before the test runs
    /// </summary>
    public virtual void Setup() { }

    /// <summary>
    /// Optional cleanup after the test runs
    /// </summary>
    public virtual void Cleanup() { }
}
