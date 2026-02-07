using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests various array types and sizes
/// </summary>
public class ArrayVariantTest : TestBase
{
    public ArrayVariantTest()
        : base("Array Variants", "Tests allocation of different array types and sizes")
    {
    }

    public override bool Run()
    {
        // Test byte array
        var byteArray = new byte[1000];
        Array.Fill(byteArray, (byte)42);
        if (byteArray[500] != 42) return false;

        // Test int array
        var intArray = new int[1000];
        Array.Fill(intArray, 12345);
        if (intArray[500] != 12345) return false;

        // Test long array
        var longArray = new long[1000];
        Array.Fill(longArray, 9876543210L);
        if (longArray[500] != 9876543210L) return false;

        // Test object array
        var objArray = new object[100];
        for (int i = 0; i < objArray.Length; i++)
        {
            objArray[i] = new object();
        }

        if (objArray[50] == null) return false;

        // Test string array
        var strArray = new string[100];
        for (int i = 0; i < strArray.Length; i++)
        {
            strArray[i] = $"String_{i}";
        }

        if (strArray[50] != "String_50") return false;

        // Trigger GC while arrays are still alive
        GC.Collect();

        // Verify arrays are still valid
        if (byteArray[500] != 42) return false;
        if (intArray[500] != 12345) return false;
        if (longArray[500] != 9876543210L) return false;
        if (objArray[50] == null) return false;
        if (strArray[50] != "String_50") return false;

        return true;
    }
}
