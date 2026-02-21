using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests structs that contain reference type fields
/// </summary>
public class StructWithReferencesTest : TestBase
{
    public StructWithReferencesTest()
        : base("Structs with References")
    {
    }

    public override void Run()
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
                throw new Exception($"structArray[{i}].RefField is null after GC");

            if (structArray[i].Name != $"Item_{i}")
                throw new Exception($"structArray[{i}].Name = \"{structArray[i].Name}\" after GC, expected \"Item_{i}\"");

            if (structArray[i].Value != i)
                throw new Exception($"structArray[{i}].Value = {structArray[i].Value} after GC, expected {i}");
        }
    }

    private struct StructWithRef
    {
        public int Value;
        public object RefField;
        public string Name;
    }
}
