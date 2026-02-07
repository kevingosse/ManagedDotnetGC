using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests allocation of zero-length arrays
/// </summary>
public class ZeroLengthArrayTest : TestBase
{
    public ZeroLengthArrayTest()
        : base("Zero-Length Arrays", "Verifies that zero-length arrays can be allocated")
    {
    }

    public override bool Run()
    {
        // Test various zero-length array types
        var byteArray = new byte[0];
        var intArray = new int[0];
        var longArray = new long[0];
        var objectArray = new object[0];
        var stringArray = new string[0];

        if (byteArray == null || byteArray.Length != 0)
        {
            return false;
        }

        if (intArray == null || intArray.Length != 0)
        {
            return false;
        }

        if (longArray == null || longArray.Length != 0)
        {
            return false;
        }

        if (objectArray == null || objectArray.Length != 0)
        {
            return false;
        }

        if (stringArray == null || stringArray.Length != 0)
        {
            return false;
        }

        // Trigger GC
        GC.Collect();

        // Verify arrays still valid
        if (byteArray.Length != 0 || intArray.Length != 0)
        {
            return false;
        }

        return true;
    }
}
