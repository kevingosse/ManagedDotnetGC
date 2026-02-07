using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests string allocation and GC behavior
/// </summary>
public class StringTest : TestBase
{
    public StringTest()
        : base("String Handling", "Verifies string allocation, comparison, and GC behavior")
    {
    }

    public override bool Run()
    {
        // Test string allocation
        var str1 = "Hello, World!";
        var str2 = new string('x', 1000);

        if (str1.Length != 13 || str2.Length != 1000)
        {
            return false;
        }

        // Test string concatenation creates new objects
        var str3 = str1 + str2;
        if (str3.Length != 1013)
        {
            return false;
        }

        // Weak reference to temporary string
        var weakRef = CreateTemporaryString();

        GC.Collect();

        // Temporary string should be collected
        if (weakRef.IsAlive)
        {
            return false;
        }

        // Test empty string
        var emptyStr = string.Empty;
        if (emptyStr == null || emptyStr.Length != 0)
        {
            return false;
        }

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
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateTemporaryString()
    {
        var temp = new string('z', 500);
        return new WeakReference(temp);
    }
}
