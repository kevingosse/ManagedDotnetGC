using System.Runtime;
using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests dependent handles whose primary or secondary lives outside the normal GC heap (frozen
/// segments — string literals since .NET 8). The rule, validated against the stock GC: anything
/// outside the GC heap counts as always-alive for dependent-handle purposes. A frozen primary must
/// keep its secondary alive forever; a frozen secondary must neither hang the collection nor
/// corrupt the handle. (docs/missing-features.md item 3.4 — note the livelock failure mode: on a
/// broken GC this test hangs *inside* the GC, which only an external timeout can catch.)
/// </summary>
public class FrozenDependentHandleTest() : TestBase("Frozen Dependent Handles", GcFeature.FrozenDependentHandles)
{
    private const string FrozenPrimary = "FrozenDependentHandleTest primary literal";
    private const string FrozenSecondary = "FrozenDependentHandleTest secondary literal";
    private const string FrozenCwtKey = "FrozenDependentHandleTest cwt key literal";

    public override void Run()
    {
        TestFrozenPrimaryKeepsSecondaryAlive();
        TestFrozenSecondaryWithLivePrimary();
        TestFrozenSecondaryWithDeadPrimary();
        TestConditionalWeakTableWithLiteralKey();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestFrozenPrimaryKeepsSecondaryAlive()
    {
        var (handle, weakSecondary) = CreateWithFrozenPrimary();

        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // The literal primary is immortal, so the secondary must be too
            if (!weakSecondary.IsAlive)
            {
                throw new Exception("Secondary of a frozen-primary dependent handle was collected");
            }

            if (!ReferenceEquals(handle.Dependent, weakSecondary.Target))
            {
                throw new Exception("Dependent handle no longer returns the stored secondary");
            }
        }
        finally
        {
            handle.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DependentHandle Handle, WeakReference WeakSecondary) CreateWithFrozenPrimary()
    {
        var secondary = new object();
        return (new DependentHandle(FrozenPrimary, secondary), new WeakReference(secondary));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestFrozenSecondaryWithLivePrimary()
    {
        var primary = new object();
        var handle = new DependentHandle(primary, FrozenSecondary);

        try
        {
            // On a GC that treats "not marked" as "needs promotion" without a heap-range check,
            // a frozen secondary makes the dependent-handle fixpoint spin forever: this call hangs.
            GC.Collect();

            if (!ReferenceEquals(handle.Dependent, FrozenSecondary))
            {
                throw new Exception("Frozen secondary was dropped from the dependent handle while its primary is alive");
            }

            GC.KeepAlive(primary);
        }
        finally
        {
            handle.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestFrozenSecondaryWithDeadPrimary()
    {
        var handle = CreateWithDeadPrimary();

        try
        {
            GC.Collect();

            // The primary is dead: the handle must be cleared, frozen secondary notwithstanding
            if (handle.Target != null)
            {
                throw new Exception("Dependent handle with a dead primary still reports a target");
            }
        }
        finally
        {
            handle.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DependentHandle CreateWithDeadPrimary()
    {
        return new DependentHandle(new object(), FrozenSecondary);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestConditionalWeakTableWithLiteralKey()
    {
        var table = new ConditionalWeakTable<string, object>();

        var weakValue = AddValue(table);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        if (!weakValue.IsAlive)
        {
            throw new Exception("ConditionalWeakTable value keyed on a string literal was collected");
        }

        if (!table.TryGetValue(FrozenCwtKey, out var value) || !ReferenceEquals(value, weakValue.Target))
        {
            throw new Exception("ConditionalWeakTable lost the entry keyed on a string literal");
        }

        GC.KeepAlive(table);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddValue(ConditionalWeakTable<string, object> table)
    {
        var value = new object();
        table.Add(FrozenCwtKey, value);
        return new WeakReference(value);
    }
}
