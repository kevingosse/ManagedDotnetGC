using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that null references are handled correctly
/// </summary>
public class NullReferenceTest() : TestBase("Null Reference Handling")
{
    public override void Run()
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
                throw new Exception($"objectArray[{i}] is null after GC, expected non-null");
        }

        // Verify nulls are still null
        for (int i = 50; i < 100; i++)
        {
            if (objectArray[i] != null)
                throw new Exception($"objectArray[{i}] is non-null after GC, expected null");
        }

        // Object with null fields
        var node = new Node
        {
            Value = 42,
            Child = null // Explicitly null
        };

        GC.Collect();

        if (node.Value != 42 || node.Child != null)
            throw new Exception($"Node fields corrupted after GC: Value={node.Value}, Child={node.Child}");
    }

    private class Node
    {
        public int Value;
        public Node? Child;
    }
}
