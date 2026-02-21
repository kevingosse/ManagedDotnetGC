using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests allocation of objects with no fields
/// </summary>
public class EmptyObjectTest() : TestBase("Empty Objects")
{
    public override void Run()
    {
        // Allocate many empty objects
        var objects = new EmptyClass[1000];

        for (int i = 0; i < objects.Length; i++)
        {
            objects[i] = new EmptyClass();
        }

        // Verify all are non-null
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null)
                throw new Exception($"objects[{i}] is null after allocation");
        }

        // Trigger GC while objects are alive
        GC.Collect();

        // Verify still non-null
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null)
                throw new Exception($"objects[{i}] is null after GC");
        }
    }

    private class EmptyClass
    {
        // Intentionally empty
    }
}
