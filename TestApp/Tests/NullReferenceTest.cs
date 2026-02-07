using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that null references are handled correctly
/// </summary>
public class NullReferenceTest : TestBase
{
    public NullReferenceTest()
        : base("Null Reference Handling", "Verifies GC correctly handles null references in objects and arrays")
    {
    }

    public override bool Run()
    {
        // Array with null references
        var objectArray = new object?[100];

        // Fill half with objects, leave half null
        for (int i = 0; i < 50; i++)
        {
            objectArray[i] = new object();
        }

        GC.Collect();

        // Verify non-null objects survived
        for (int i = 0; i < 50; i++)
        {
            if (objectArray[i] == null)
            {
                return false;
            }
        }

        // Verify nulls are still null
        for (int i = 50; i < 100; i++)
        {
            if (objectArray[i] != null)
            {
                return false;
            }
        }

        // Object with null fields
        var node = new Node
        {
            Value = 42,
            Child = null // Explicitly null
        };

        GC.Collect();

        if (node.Value != 42 || node.Child != null)
        {
            return false;
        }

        return true;
    }

    private class Node
    {
        public int Value;
        public Node? Child;
    }
}
