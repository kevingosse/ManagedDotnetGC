using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests jagged arrays (arrays of arrays)
/// </summary>
public class JaggedArrayTest : TestBase
{
    public JaggedArrayTest()
        : base("Jagged Arrays")
    {
    }

    public override void Run()
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
                throw new Exception($"jaggedArray[{i}] is null after GC");

            if (jaggedArray[i].Length != i + 1)
                throw new Exception($"jaggedArray[{i}].Length = {jaggedArray[i].Length}, expected {i + 1}");

            for (int j = 0; j < jaggedArray[i].Length; j++)
            {
                if (jaggedArray[i][j] != i * 100 + j)
                    throw new Exception($"jaggedArray[{i}][{j}] = {jaggedArray[i][j]}, expected {i * 100 + j}");
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
                    throw new Exception($"objectJagged[{i}][{j}] is null after GC");
            }
        }
    }
}
