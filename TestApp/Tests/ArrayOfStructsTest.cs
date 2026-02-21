using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests arrays of value types vs reference types
/// </summary>
public class ArrayOfStructsTest : TestBase
{
    public ArrayOfStructsTest()
        : base("Arrays of Structs")
    {
    }

    public override void Run()
    {
        // Array of value types
        var structArray = new MyStruct[100];

        for (int i = 0; i < structArray.Length; i++)
        {
            structArray[i] = new MyStruct
            {
                X = i,
                Y = i * 2,
                Z = i * 3
            };
        }

        GC.Collect();

        // Verify values
        for (int i = 0; i < structArray.Length; i++)
        {
            if (structArray[i].X != i ||
                structArray[i].Y != i * 2 ||
                structArray[i].Z != i * 3)
                throw new Exception($"structArray[{i}]: X={structArray[i].X}, Y={structArray[i].Y}, Z={structArray[i].Z}; expected X={i}, Y={i * 2}, Z={i * 3}");
        }

        // Array of structs with references
        var complexStructArray = new ComplexStruct[100];

        for (int i = 0; i < complexStructArray.Length; i++)
        {
            complexStructArray[i] = new ComplexStruct
            {
                Value = i,
                Data = new byte[100]
            };
        }

        GC.Collect();

        // Verify structs and their references
        for (int i = 0; i < complexStructArray.Length; i++)
        {
            if (complexStructArray[i].Value != i)
                throw new Exception($"complexStructArray[{i}].Value = {complexStructArray[i].Value}, expected {i}");

            if (complexStructArray[i].Data == null ||
                complexStructArray[i].Data.Length != 100)
                throw new Exception($"complexStructArray[{i}].Data is null or wrong length after GC");
        }
    }

    private struct MyStruct
    {
        public int X;
        public int Y;
        public int Z;
    }

    private struct ComplexStruct
    {
        public int Value;
        public byte[] Data;
    }
}
