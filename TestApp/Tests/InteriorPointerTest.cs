using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests interior pointer handling during GC
/// </summary>
public class InteriorPointerTest : TestBase
{
    public InteriorPointerTest()
        : base("Interior Pointers", "Verifies that objects remain alive when only interior pointers exist")
    {
    }

    public override bool Run()
    {
        ref var interiorPointerSmall = ref GetInteriorPointer(10, out var weakRefSmall);
        ref var interiorPointerLarge = ref GetInteriorPointer(10 * 1024 * 1024, out var weakRefLarge);

        GC.Collect();

        if (!weakRefSmall.IsAlive)
        {
            return false;
        }

        if (!weakRefLarge.IsAlive)
        {
            return false;
        }

        GC.KeepAlive(interiorPointerSmall);
        GC.KeepAlive(interiorPointerLarge);

        return true;
    }

    private static ref int GetInteriorPointer(int size, out WeakReference weakRef)
    {
        var array = new int[size];
        weakRef = new WeakReference(array);
        return ref array[5];
    }
}
