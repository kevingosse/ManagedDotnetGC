using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests allocation of zero-length arrays
/// </summary>
public class ZeroLengthArrayTest : TestBase
{
    public ZeroLengthArrayTest()
        : base("Zero-Length Arrays")
    {
    }

    public override void Run()
    {
        // Test various zero-length array types
        var byteArray = new byte[0];
        var intArray = new int[0];
        var longArray = new long[0];
        var objectArray = new object[0];
        var stringArray = new string[0];

        if (byteArray == null || byteArray.Length != 0)
            throw new Exception($"byte[0]: null={byteArray == null}, Length={byteArray?.Length}");

        if (intArray == null || intArray.Length != 0)
            throw new Exception($"int[0]: null={intArray == null}, Length={intArray?.Length}");

        if (longArray == null || longArray.Length != 0)
            throw new Exception($"long[0]: null={longArray == null}, Length={longArray?.Length}");

        if (objectArray == null || objectArray.Length != 0)
            throw new Exception($"object[0]: null={objectArray == null}, Length={objectArray?.Length}");

        if (stringArray == null || stringArray.Length != 0)
            throw new Exception($"string[0]: null={stringArray == null}, Length={stringArray?.Length}");

        // Trigger GC
        GC.Collect();

        // Verify arrays still valid
        if (byteArray.Length != 0 || intArray.Length != 0)
            throw new Exception("Zero-length arrays have non-zero length after GC");
    }
}
