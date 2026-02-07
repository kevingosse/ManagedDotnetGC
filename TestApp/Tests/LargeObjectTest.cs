using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests large object allocation (LOH)
/// </summary>
public class LargeObjectTest : TestBase
{
    public LargeObjectTest()
        : base("Large Object Allocation", "Verifies that large objects (>85KB) are properly allocated and initialized")
    {
    }

    public override bool Run()
    {
        // Allocate a 100KB object
        var mediumObj = new byte[100_000];
        Array.Fill(mediumObj, (byte)0xFF);

        for (int i = 0; i < mediumObj.Length; i++)
        {
            if (mediumObj[i] != 0xFF)
            {
                return false;
            }
        }

        // Allocate an 8MB object
        var hugeObj = new byte[8 * 1024 * 1024];
        Array.Fill(hugeObj, (byte)0xEE);

        for (int i = 0; i < hugeObj.Length; i++)
        {
            if (hugeObj[i] != 0xEE)
            {
                return false;
            }
        }

        return true;
    }
}
