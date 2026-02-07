using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests jagged arrays (arrays of arrays)
/// </summary>
public class JaggedArrayTest : TestBase
{
    public JaggedArrayTest()
        : base("Jagged Arrays", "Verifies jagged arrays (arrays of arrays) work correctly")
    {
    }

    public override bool Run()
    {
        // Create jagged array
        var jaggedArray = new int[10][];

        for (int i = 0; i < jaggedArray.Length; i++)
        {
            jaggedArray[i] = new int[i + 1];

            for (int j = 0; j < jaggedArray[i].Length; j++)
            {
                jaggedArray[i][j] = i * 100 + j;
            }
        }

        GC.Collect();

        // Verify structure
        for (int i = 0; i < jaggedArray.Length; i++)
        {
            if (jaggedArray[i] == null)
            {
                return false;
            }

            if (jaggedArray[i].Length != i + 1)
            {
                return false;
            }

            for (int j = 0; j < jaggedArray[i].Length; j++)
            {
                if (jaggedArray[i][j] != i * 100 + j)
                {
                    return false;
                }
            }
        }

        // Jagged array of objects
        var objectJagged = new object[5][];

        for (int i = 0; i < objectJagged.Length; i++)
        {
            objectJagged[i] = new object[i + 1];

            for (int j = 0; j < objectJagged[i].Length; j++)
            {
                objectJagged[i][j] = new object();
            }
        }

        GC.Collect();

        // Verify all objects still exist
        for (int i = 0; i < objectJagged.Length; i++)
        {
            for (int j = 0; j < objectJagged[i].Length; j++)
            {
                if (objectJagged[i][j] == null)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
