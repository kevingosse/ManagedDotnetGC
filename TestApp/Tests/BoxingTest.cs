using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests boxing and unboxing of value types
/// </summary>
public class BoxingTest : TestBase
{
    public BoxingTest()
        : base("Boxing/Unboxing", "Verifies that value types can be boxed and unboxed correctly")
    {
    }

    public override bool Run()
    {
        // Box primitive types
        object boxedInt = 42;
        object boxedLong = 12345678901234L;
        object boxedDouble = 3.14159;
        object boxedBool = true;

        // Verify boxed values
        if ((int)boxedInt != 42)
        {
            return false;
        }

        if ((long)boxedLong != 12345678901234L)
        {
            return false;
        }

        if ((double)boxedDouble != 3.14159)
        {
            return false;
        }

        if ((bool)boxedBool != true)
        {
            return false;
        }

        // Box custom struct
        var myStruct = new MyStruct { X = 100, Y = 200, Name = "Test" };
        object boxedStruct = myStruct;

        // Trigger GC
        GC.Collect();

        // Unbox and verify
        var unboxedStruct = (MyStruct)boxedStruct;
        if (unboxedStruct.X != 100 || unboxedStruct.Y != 200 || unboxedStruct.Name != "Test")
        {
            return false;
        }

        // Create weak reference to boxed value
        var weakRef = CreateBoxedValue();

        GC.Collect();

        // Boxed value should be collected
        if (weakRef.IsAlive)
        {
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateBoxedValue()
    {
        object boxed = 999;
        return new WeakReference(boxed);
    }

    private struct MyStruct
    {
        public int X;
        public int Y;
        public string Name;
    }
}
