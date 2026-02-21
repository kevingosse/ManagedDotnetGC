using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that circular references are collected when no external root exists
/// </summary>
public class CircularReferenceTest : TestBase
{
    public CircularReferenceTest()
        : base("Circular References")
    {
    }

    public override void Run()
    {
        // Create a simple circular reference: A -> B -> A
        var weakRefs = CreateSimpleCircle();

        // Both should be alive initially
        if (!weakRefs.Item1.IsAlive || !weakRefs.Item2.IsAlive)
            throw new Exception("Simple circle nodes not alive before GC");

        // After GC, both should be collected since there's no external root
        GC.Collect();

        if (weakRefs.Item1.IsAlive)
            throw new Exception("nodeA of simple circle still alive after GC");
        if (weakRefs.Item2.IsAlive)
            throw new Exception("nodeB of simple circle still alive after GC");

        // Test a more complex circle: A -> B -> C -> D -> A
        var complexWeakRefs = CreateComplexCircle();

        for (int i = 0; i < complexWeakRefs.Length; i++)
        {
            if (!complexWeakRefs[i].IsAlive)
                throw new Exception($"Complex circle node[{i}] not alive before GC");
        }

        GC.Collect();

        for (int i = 0; i < complexWeakRefs.Length; i++)
        {
            if (complexWeakRefs[i].IsAlive)
                throw new Exception($"Complex circle node[{i}] still alive after GC");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference, WeakReference) CreateSimpleCircle()
    {
        var nodeA = new Node { Value = 1 };
        var nodeB = new Node { Value = 2 };

        nodeA.Child = nodeB;
        nodeB.Child = nodeA;

        return (new WeakReference(nodeA), new WeakReference(nodeB));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateComplexCircle()
    {
        var nodes = new Node[4];
        var weakRefs = new WeakReference[4];

        for (int i = 0; i < 4; i++)
        {
            nodes[i] = new Node { Value = i };
            weakRefs[i] = new WeakReference(nodes[i]);
        }

        // Create circular chain: 0->1->2->3->0
        for (int i = 0; i < 4; i++)
        {
            nodes[i].Child = nodes[(i + 1) % 4];
        }

        return weakRefs;
    }

    private class Node
    {
        public int Value;
        public Node? Child;
    }
}
