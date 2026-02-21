using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests multi-dimensional array allocation
/// </summary>
public class MultiDimensionalArrayTest() : TestBase("Multi-Dimensional Arrays")
{
    public override void Run()
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
                    throw new Exception($"array2D[{i},{j}] = {array2D[i, j]}, expected {i * 100 + j}");
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
            throw new Exception($"array3D[2,3,4] = {array3D[2, 3, 4]} after GC, expected 20304");

        // 2D array of objects
        var objectArray = new object[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                objectArray[i, j] = new object();
            }
        }

        GC.Collect();

        // Verify objects still there
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (objectArray[i, j] == null)
                    throw new Exception($"objectArray[{i},{j}] is null after GC");
            }
        }
    }
}
