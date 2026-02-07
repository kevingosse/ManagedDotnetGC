using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests arrays of value types vs reference types
/// </summary>
public class ArrayOfStructsTest : TestBase
{
    public ArrayOfStructsTest()
        : base("Arrays of Structs", "Verifies arrays containing value types work correctly")
    {
    }

    public override bool Run()
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
            {
                return false;
            }
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
            {
                return false;
            }

            if (complexStructArray[i].Data == null ||
                complexStructArray[i].Data.Length != 100)
            {
                return false;
            }
        }

        return true;
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
