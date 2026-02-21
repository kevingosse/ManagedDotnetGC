using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests large object allocation (LOH)
/// </summary>
public class LargeObjectTest() : TestBase("Large Object Allocation")
{
    public override void Run()
    {
        // Allocate a 100KB object
        var mediumObj = new byte[100_000];
        Array.Fill(mediumObj, (byte)0xFF);

        for (int i = 0; i < mediumObj.Length; i++)
        {
            if (mediumObj[i] != 0xFF)
                throw new Exception($"mediumObj[{i}] = 0x{mediumObj[i]:X2}, expected 0xFF after Fill");
        }

        // Allocate an 8MB object
        var hugeObj = new byte[8 * 1024 * 1024];
        Array.Fill(hugeObj, (byte)0xEE);

        for (int i = 0; i < hugeObj.Length; i++)
        {
            if (hugeObj[i] != 0xEE)
                throw new Exception($"hugeObj[{i}] = 0x{hugeObj[i]:X2}, expected 0xEE after Fill");
        }
    }
}
