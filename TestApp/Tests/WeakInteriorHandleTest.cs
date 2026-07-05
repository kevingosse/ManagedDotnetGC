using System.Reflection;
using System.Reflection.Emit;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that the handle table can store HNDTYPE_WEAK_INTERIOR_POINTER (handle type 10): the EE
/// creates one per statics kind the first time a collectible type's statics are allocated
/// (GC statics: runtime-vm/loaderallocator.cpp:2465, non-GC statics: :2385). This isolates item
/// 4.1 from the rest of the collectible-assembly machinery: no instance of the collectible type
/// is ever created (so the 3.1 LoaderAllocator mark edge is not needed) and the assembly stays
/// rooted throughout (so no unload). The statics storage is kept alive through the managed
/// LoaderAllocator object, not through the weak interior handle, so plain marking is enough for
/// the values to survive. (docs/missing-features.md item 4.1)
/// </summary>
public class WeakInteriorHandleTest() : TestBase("Weak Interior Handles", GcFeature.NewHandleTypes)
{
    public override void Run()
    {
        var type = CreateCollectibleType();

        var objectField = type.GetField("ObjectField")!;
        var intField = type.GetField("IntField")!;

        // The first static access allocates the statics storage; for a collectible type this
        // creates one HNDTYPE_WEAK_INTERIOR_POINTER handle per statics kind (GC and non-GC)
        var value = new object();
        objectField.SetValue(null, value);
        intField.SetValue(null, 1234);

        if (!ReferenceEquals(objectField.GetValue(null), value))
        {
            throw new Exception("Collectible GC static did not round-trip the stored reference");
        }

        // The statics must survive collections while the assembly is rooted (through the
        // FieldInfo -> Type -> LoaderAllocator chain, not through the weak interior handle)
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        if (!ReferenceEquals(objectField.GetValue(null), value))
        {
            throw new Exception("Collectible GC static lost its value across collections");
        }

        if ((int)intField.GetValue(null)! != 1234)
        {
            throw new Exception($"Collectible non-GC static lost its value across collections: {intField.GetValue(null)}");
        }

        GC.KeepAlive(value);
        GC.KeepAlive(type);
    }

    private static Type CreateCollectibleType()
    {
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("WeakInteriorHandleAsm"), AssemblyBuilderAccess.RunAndCollect);

        var typeBuilder = assemblyBuilder.DefineDynamicModule("M").DefineType("TypeWithStatics", TypeAttributes.Public);

        typeBuilder.DefineField("ObjectField", typeof(object), FieldAttributes.Public | FieldAttributes.Static);
        typeBuilder.DefineField("IntField", typeof(int), FieldAttributes.Public | FieldAttributes.Static);

        return typeBuilder.CreateType();
    }
}
