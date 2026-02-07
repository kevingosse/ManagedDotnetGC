using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests basic object allocation and memory initialization
/// </summary>
public class BasicAllocationTest : TestBase
{
    public BasicAllocationTest()
        : base("Basic Allocation", "Verifies that objects can be allocated and memory is properly initialized")
    {
    }

    public override bool Run()
    {
        // Test simple object allocation
        var obj1 = new object();
        var obj2 = new object();

        if (obj1 == null || obj2 == null)
        {
            return false;
        }

        // Test array allocation with initialization
        var array = new byte[1024];
        Array.Fill(array, (byte)0xAA);

        for (int i = 0; i < array.Length; i++)
        {
            if (array[i] != 0xAA)
            {
                return false;
            }
        }

        // Test larger array
        var largeArray = new byte[32720];
        Array.Fill(largeArray, (byte)0xCC);

        for (int i = 0; i < largeArray.Length; i++)
        {
            if (largeArray[i] != 0xCC)
            {
                return false;
            }
        }

        return true;
    }
}
