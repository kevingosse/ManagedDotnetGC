using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that the GC correctly traces reference graphs
/// </summary>
public class ReferenceGraphTest : TestBase
{
    public ReferenceGraphTest()
        : base("Reference Graph")
    {
    }

    public override void Run()
    {
        // Create a reference chain: root -> child1 -> child2
        var (weakRoot, weakChild1, weakChild2) = CreateReferenceChain(out var root);

        // All should be alive
        if (!weakRoot.IsAlive || !weakChild1.IsAlive || !weakChild2.IsAlive)
            throw new Exception("One or more nodes not alive before first GC");

        // Collect - all should survive because root is still alive
        GC.Collect();

        if (!weakRoot.IsAlive)
            throw new Exception("Root not alive after GC while root is still referenced");
        if (!weakChild1.IsAlive)
            throw new Exception("child1 not alive after GC while root is still referenced");
        if (!weakChild2.IsAlive)
            throw new Exception("child2 not alive after GC while root is still referenced");

        // Keep root alive
        GC.KeepAlive(root);

        // Now let everything go out of scope
        root = null;
        GC.Collect();

        // All should be collected now
        if (weakRoot.IsAlive) throw new Exception("Root still alive after GC with no roots");
        if (weakChild1.IsAlive) throw new Exception("child1 still alive after GC with no roots");
        if (weakChild2.IsAlive) throw new Exception("child2 still alive after GC with no roots");
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
