using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests boxing and unboxing of value types
/// </summary>
public class BoxingTest : TestBase
{
    public BoxingTest()
        : base("Boxing/Unboxing")
    {
    }

    public override void Run()
    {
        // Box primitive types
        object boxedInt = 42;
        object boxedLong = 12345678901234L;
        object boxedDouble = 3.14159;
        object boxedBool = true;

        // Verify boxed values
        if ((int)boxedInt != 42)
            throw new Exception($"Unboxed int = {(int)boxedInt}, expected 42");

        if ((long)boxedLong != 12345678901234L)
            throw new Exception($"Unboxed long = {(long)boxedLong}, expected 12345678901234");

        if ((double)boxedDouble != 3.14159)
            throw new Exception($"Unboxed double = {(double)boxedDouble}, expected 3.14159");

        if ((bool)boxedBool != true)
            throw new Exception("Unboxed bool was not true");

        // Box custom struct
        var myStruct = new MyStruct { X = 100, Y = 200, Name = "Test" };
        object boxedStruct = myStruct;

        // Trigger GC
        GC.Collect();

        // Unbox and verify
        var unboxedStruct = (MyStruct)boxedStruct;
        if (unboxedStruct.X != 100 || unboxedStruct.Y != 200 || unboxedStruct.Name != "Test")
            throw new Exception($"Unboxed struct fields: X={unboxedStruct.X}, Y={unboxedStruct.Y}, Name={unboxedStruct.Name}; expected X=100, Y=200, Name=Test");

        // Create weak reference to boxed value
        var weakRef = CreateBoxedValue();

        GC.Collect();

        // Boxed value should be collected
        if (weakRef.IsAlive)
            throw new Exception("Boxed value still alive after GC with no roots");
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
