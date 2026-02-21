namespace TestApp.TestFramework;

/// <summary>
/// Base class for all GC tests
/// </summary>
public abstract class TestBase
{
    public string Name { get; }

    protected TestBase(string name)
    {
        Name = name;
    }

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
