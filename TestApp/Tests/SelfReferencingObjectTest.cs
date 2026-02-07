using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests objects that reference themselves
/// </summary>
public class SelfReferencingObjectTest : TestBase
{
    public SelfReferencingObjectTest()
        : base("Self-Referencing Objects", "Verifies objects that reference themselves are handled correctly")
    {
    }

    public override bool Run()
    {
        // Create object that references itself
        var weakRef = CreateSelfReferencingObject();

        // Should be alive initially
        if (!weakRef.IsAlive)
        {
            return false;
        }

        // After GC, should be collected (no external root)
        GC.Collect();

        if (weakRef.IsAlive)
        {
            return false;
        }

        // Create self-referencing object with external root
        var node = new Node { Value = 42 };
        node.Self = node;

        GC.Collect();

        // Should survive because we have a root
        if (node.Self != node || node.Value != 42)
        {
            return false;
        }

        return true;
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
