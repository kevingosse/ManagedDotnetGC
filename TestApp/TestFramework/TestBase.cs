namespace TestApp.TestFramework;

/// <summary>
/// Base class for all GC tests
/// </summary>
public abstract class TestBase
{
    public string Name { get; }
    public string Description { get; }

    protected TestBase(string name, string description)
    {
        Name = name;
        Description = description;
    }

    /// <summary>
    /// Run the test and return true if it passes
    /// </summary>
    public abstract bool Run();

    /// <summary>
    /// Optional setup before the test runs
    /// </summary>
    public virtual void Setup() { }

    /// <summary>
    /// Optional cleanup after the test runs
    /// </summary>
    public virtual void Cleanup() { }
}
