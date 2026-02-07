using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests multi-dimensional array allocation
/// </summary>
public class MultiDimensionalArrayTest : TestBase
{
    public MultiDimensionalArrayTest()
        : base("Multi-Dimensional Arrays", "Verifies that multi-dimensional arrays can be allocated and used")
    {
    }

    public override bool Run()
    {
        // 2D array
        var array2D = new int[10, 20];

        for (int i = 0; i < 10; i++)
        {
            for (int j = 0; j < 20; j++)
            {
                array2D[i, j] = i * 100 + j;
            }
        }

        // Verify values
        for (int i = 0; i < 10; i++)
        {
            for (int j = 0; j < 20; j++)
            {
                if (array2D[i, j] != i * 100 + j)
                {
                    return false;
                }
            }
        }

        // 3D array
        var array3D = new int[5, 5, 5];

        for (int i = 0; i < 5; i++)
        {
            for (int j = 0; j < 5; j++)
            {
                for (int k = 0; k < 5; k++)
                {
                    array3D[i, j, k] = i * 10000 + j * 100 + k;
                }
            }
        }

        GC.Collect();

        // Verify 3D array still valid
        if (array3D[2, 3, 4] != 20304)
        {
            return false;
        }

        // 2D array of objects
        var objectArray = new object[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                objectArray[i, j] = new object();
            }
        }

        Console.WriteLine("Second GC collection...");
        GC.Collect();

        // Verify objects still there
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (objectArray[i, j] == null)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
