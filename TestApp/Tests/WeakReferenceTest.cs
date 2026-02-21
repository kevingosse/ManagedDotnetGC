using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests weak reference behavior
/// </summary>
public class WeakReferenceTest : TestBase
{
    public WeakReferenceTest()
        : base("Weak References")
    {
    }

    public override void Run()
    {
        // Test WeakReference
        var weakRef = GetWeakReference();

        if (!weakRef.IsAlive)
            throw new Exception("WeakReference not alive immediately after creation");

        GC.Collect();

        if (weakRef.IsAlive)
            throw new Exception("WeakReference still alive after GC with no strong roots");

        // Test WeakReference<T>
        var typedWeakRef = GetTypedWeakReference();

        if (!IsAlive(typedWeakRef))
            throw new Exception("WeakReference<T> not alive immediately after creation");

        GC.Collect();

        if (IsAlive(typedWeakRef))
            throw new Exception("WeakReference<T> still alive after GC with no strong roots");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference GetWeakReference()
    {
        var target = new[] { 0, 1, 2 };
        return new WeakReference(target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<int[]> GetTypedWeakReference()
    {
        var target = new[] { 0, 1, 2 };
        return new WeakReference<int[]>(target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> weakRef) where T : class
    {
        return weakRef.TryGetTarget(out _);
    }
}
