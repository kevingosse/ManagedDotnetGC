using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests allocation of objects with no fields
/// </summary>
public class EmptyObjectTest : TestBase
{
    public EmptyObjectTest()
        : base("Empty Objects", "Verifies that objects with no fields can be allocated")
    {
    }

    public override bool Run()
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
            {
                return false;
            }
        }

        // Trigger GC while objects are alive
        GC.Collect();

        // Verify still non-null
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null)
            {
                return false;
            }
        }

        return true;
    }

    private class EmptyClass
    {
        // Intentionally empty
    }
}
