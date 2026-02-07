using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests structs that contain reference type fields
/// </summary>
public class StructWithReferencesTest : TestBase
{
    public StructWithReferencesTest()
        : base("Structs with References", "Verifies that GC correctly traces references within value types")
    {
    }

    public override bool Run()
    {
        // Create an array of structs containing references
        var structArray = new StructWithRef[100];

        for (int i = 0; i < structArray.Length; i++)
        {
            structArray[i] = new StructWithRef
            {
                Value = i,
                RefField = new object(),
                Name = $"Item_{i}"
            };
        }

        // Trigger GC - objects referenced by structs should survive
        GC.Collect();

        // Verify all references still valid
        for (int i = 0; i < structArray.Length; i++)
        {
            if (structArray[i].RefField == null)
            {
                return false;
            }

            if (structArray[i].Name != $"Item_{i}")
            {
                return false;
            }

            if (structArray[i].Value != i)
            {
                return false;
            }
        }

        return true;
    }

    private struct StructWithRef
    {
        public int Value;
        public object RefField;
        public string Name;
    }
}
