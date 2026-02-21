using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests string allocation and GC behavior
/// </summary>
public class StringTest : TestBase
{
    public StringTest()
        : base("String Handling")
    {
    }

    public override void Run()
    {
        // Test string allocation
        var str1 = "Hello, World!";
        var str2 = new string('x', 1000);

        if (str1.Length != 13 || str2.Length != 1000)
            throw new Exception($"Unexpected lengths: str1={str1.Length} (expected 13), str2={str2.Length} (expected 1000)");

        // Test string concatenation creates new objects
        var str3 = str1 + str2;
        if (str3.Length != 1013)
            throw new Exception($"str3.Length = {str3.Length}, expected 1013");

        // Weak reference to temporary string
        var weakRef = CreateTemporaryString();

        GC.Collect();

        // Temporary string should be collected
        if (weakRef.IsAlive)
            throw new Exception("Temporary string still alive after GC with no roots");

        // Test empty string
        var emptyStr = string.Empty;
        if (emptyStr == null || emptyStr.Length != 0)
            throw new Exception($"string.Empty is null or non-empty: Length={emptyStr?.Length}");

        // Test array of strings
        var stringArray = new string[100];
        for (int i = 0; i < 100; i++)
        {
            stringArray[i] = $"String number {i}";
        }

        GC.Collect();

        // Verify strings survive
        for (int i = 0; i < 100; i++)
        {
            if (stringArray[i] != $"String number {i}")
                throw new Exception($"stringArray[{i}] = \"{stringArray[i]}\" after GC, expected \"String number {i}\"");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateTemporaryString()
    {
        var temp = new string('z', 500);
        return new WeakReference(temp);
    }
}
