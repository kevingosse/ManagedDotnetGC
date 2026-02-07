using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests weak reference behavior
/// </summary>
public class WeakReferenceTest : TestBase
{
    public WeakReferenceTest()
        : base("Weak References", "Verifies that weak references work correctly with GC")
    {
    }

    public override bool Run()
    {
        // Test WeakReference
        var weakRef = GetWeakReference();

        if (!weakRef.IsAlive)
        {
            return false;
        }

        GC.Collect();

        if (weakRef.IsAlive)
        {
            return false;
        }

        // Test WeakReference<T>
        var typedWeakRef = GetTypedWeakReference();

        if (!IsAlive(typedWeakRef))
        {
            return false;
        }

        GC.Collect();

        if (IsAlive(typedWeakRef))
        {
            return false;
        }

        return true;
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
