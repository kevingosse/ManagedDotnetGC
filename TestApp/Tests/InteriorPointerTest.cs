using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests interior pointer handling during GC
/// </summary>
public class InteriorPointerTest() : TestBase("Interior Pointers")
{
    public override void Run()
    {
        ref var interiorPointerSmall = ref GetInteriorPointer(10, out var weakRefSmall);
        ref var interiorPointerLarge = ref GetInteriorPointer(10 * 1024 * 1024, out var weakRefLarge);

        GC.Collect();

        if (!weakRefSmall.IsAlive)
            throw new Exception("Small array not alive when interior pointer is held on stack");

        if (!weakRefLarge.IsAlive)
            throw new Exception("Large array not alive when interior pointer is held on stack");

        GC.KeepAlive(interiorPointerSmall);
        GC.KeepAlive(interiorPointerLarge);
    }

    private static ref int GetInteriorPointer(int size, out WeakReference weakRef)
    {
        var array = new int[size];
        weakRef = new WeakReference(array);
        return ref array[5];
    }
}
