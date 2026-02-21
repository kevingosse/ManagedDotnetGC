using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests various array types and sizes
/// </summary>
public class ArrayVariantTest() : TestBase("Array Variants")
{
    public override void Run()
    {
        // Test byte array
        var byteArray = new byte[1000];
        Array.Fill(byteArray, (byte)42);
        if (byteArray[500] != 42) throw new Exception("byteArray[500] != 42 after Fill");

        // Test int array
        var intArray = new int[1000];
        Array.Fill(intArray, 12345);
        if (intArray[500] != 12345) throw new Exception("intArray[500] != 12345 after Fill");

        // Test long array
        var longArray = new long[1000];
        Array.Fill(longArray, 9876543210L);
        if (longArray[500] != 9876543210L) throw new Exception("longArray[500] != 9876543210 after Fill");

        // Test object array
        var objArray = new object[100];
        for (int i = 0; i < objArray.Length; i++)
        {
            objArray[i] = new object();
        }

        if (objArray[50] == null) throw new Exception("objArray[50] is null after allocation");

        // Test string array
        var strArray = new string[100];
        for (int i = 0; i < strArray.Length; i++)
        {
            strArray[i] = $"String_{i}";
        }

        if (strArray[50] != "String_50") throw new Exception($"strArray[50] = \"{strArray[50]}\", expected \"String_50\"");

        // Trigger GC while arrays are still alive
        GC.Collect();

        // Verify arrays are still valid
        if (byteArray[500] != 42) throw new Exception("byteArray[500] != 42 after GC");
        if (intArray[500] != 12345) throw new Exception("intArray[500] != 12345 after GC");
        if (longArray[500] != 9876543210L) throw new Exception("longArray[500] != 9876543210 after GC");
        if (objArray[50] == null) throw new Exception("objArray[50] is null after GC");
        if (strArray[50] != "String_50") throw new Exception($"strArray[50] = \"{strArray[50]}\" after GC, expected \"String_50\"");
    }
}
