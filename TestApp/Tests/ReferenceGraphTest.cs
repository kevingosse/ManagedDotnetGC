using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that the GC correctly traces reference graphs
/// </summary>
public class ReferenceGraphTest : TestBase
{
    public ReferenceGraphTest()
        : base("Reference Graph", "Verifies that the GC correctly traces object references")
    {
    }

    public override bool Run()
    {
        // Create a reference chain: root -> child1 -> child2
        var (weakRoot, weakChild1, weakChild2) = CreateReferenceChain(out var root);

        // All should be alive
        if (!weakRoot.IsAlive || !weakChild1.IsAlive || !weakChild2.IsAlive)
        {
            return false;
        }

        // Collect - all should survive because root is still alive
        GC.Collect();

        if (!weakRoot.IsAlive || !weakChild1.IsAlive || !weakChild2.IsAlive)
        {
            return false;
        }

        // Keep root alive
        GC.KeepAlive(root);

        // Now let everything go out of scope
        root = null;
        GC.Collect();

        // All should be collected now
        if (weakRoot.IsAlive || weakChild1.IsAlive || weakChild2.IsAlive)
        {
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference, WeakReference, WeakReference) CreateReferenceChain(out Node root)
    {
        root = new Node { Value = 1 };
        var child1 = new Node { Value = 2 };
        var child2 = new Node { Value = 3 };

        root.Child = child1;
        child1.Child = child2;

        return (new WeakReference(root), new WeakReference(child1), new WeakReference(child2));
    }

    private class Node
    {
        public int Value;
        public Node? Child;
    }
}
