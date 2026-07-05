using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests lightweight code generation (DynamicMethod): the delegate must keep the underlying code
/// alive across collections after the DynamicMethod object itself is dropped (collectible-code
/// liveness), and everything must become collectible once the delegate is gone.
/// (docs/missing-features.md item 3.1)
/// </summary>
public class DynamicMethodTest() : TestBase("Dynamic Methods", GcFeature.CollectibleAssemblies)
{
    public override void Run()
    {
        // All strong references to the delegate stay inside the helper so tier-0 stack slots in
        // this frame can't extend its lifetime.
        var weakDelegate = CreateUseAndDrop();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        if (weakDelegate.IsAlive)
        {
            throw new Exception("Dynamic method delegate was not collected after being dropped");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUseAndDrop()
    {
        var del = BuildDelegate();

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            int result = del(2, 3);

            if (result != 5)
            {
                throw new Exception($"Dynamic method returned {result}, expected 5");
            }
        }

        return new WeakReference(del);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Func<int, int, int> BuildDelegate()
    {
        var dynamicMethod = new DynamicMethod("Add", typeof(int), [typeof(int), typeof(int)]);

        var il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);

        // The DynamicMethod object is dropped when this method returns;
        // only the delegate keeps the code alive
        return dynamicMethod.CreateDelegate<Func<int, int, int>>();
    }
}
