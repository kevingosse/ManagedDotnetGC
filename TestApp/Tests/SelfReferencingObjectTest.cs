using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests objects that reference themselves
/// </summary>
public class SelfReferencingObjectTest() : TestBase("Self-Referencing Objects")
{
    public override void Run()
    {
        // Create object that references itself
        var weakRef = CreateSelfReferencingObject();

        // Should be alive initially
        if (!weakRef.IsAlive)
            throw new Exception("Self-referencing object not alive before GC");

        // After GC, should be collected (no external root)
        GC.Collect();

        if (weakRef.IsAlive)
            throw new Exception("Self-referencing object still alive after GC with no external root");

        // Create self-referencing object with external root
        var node = new Node { Value = 42 };
        node.Self = node;

        GC.Collect();

        // Should survive because we have a root
        if (node.Self != node || node.Value != 42)
            throw new Exception($"Self-referencing node corrupted after GC: Value={node.Value}, Self=={(node.Self == node ? "self" : "other")}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateSelfReferencingObject()
    {
        var node = new Node { Value = 99 };
        node.Self = node;
        return new WeakReference(node);
    }

    private class Node
    {
        public int Value;
        public Node? Self;
    }
}
