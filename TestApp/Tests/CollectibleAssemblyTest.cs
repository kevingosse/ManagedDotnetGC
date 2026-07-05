using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests collectible assemblies (AssemblyBuilderAccess.RunAndCollect): instances of collectible
/// types must keep their LoaderAllocator (and thus their MethodTable and code) alive — this is the
/// mark-phase LoaderAllocator edge. Statics of collectible types must work (they are tracked with
/// HNDTYPE_WEAK_INTERIOR_POINTER handles), and the assembly must become collectible once nothing
/// references it anymore. (docs/missing-features.md items 3.1, 4.1, 4.2)
/// </summary>
public class CollectibleAssemblyTest() : TestBase("Collectible Assemblies", GcFeature.CollectibleAssemblies)
{
    public override void Run()
    {
        TestInstanceKeepsTypeAlive();
        TestCollectibleStatics();
        TestAssemblyUnloads();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestInstanceKeepsTypeAlive()
    {
        object instance = CreateCollectibleInstance("KeepAliveAsm");

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // Both calls dereference the (collectible) MethodTable: if the LoaderAllocator was
            // collected while the instance is alive, this reads freed memory.
            var type = instance.GetType();

            if (type.Name != "CollectibleType")
            {
                throw new Exception($"Unexpected type name: {type.Name}");
            }

            int value = (int)type.GetMethod("GetValue")!.Invoke(instance, null)!;

            if (value != 42)
            {
                throw new Exception($"Method on collectible type returned {value}, expected 42");
            }
        }

        GC.KeepAlive(instance);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestCollectibleStatics()
    {
        // The FieldInfo keeps the collectible assembly alive for the duration of this sub-test.
        // Any strong reference to the stored value is scoped to NoInlining helpers so tier-0 stack
        // slots in this frame can't extend its lifetime.
        var field = CreateCollectibleInstance("StaticsAsm").GetType().GetField("StaticField")!;

        var weakValue = SetStatic(field);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The static must root its value across collections
        if (!weakValue.IsAlive)
        {
            throw new Exception("Value stored in a collectible static was collected");
        }

        VerifyReadBack(field, weakValue);

        // Clearing the static must release the value
        field.SetValue(null, null);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        if (weakValue.IsAlive)
        {
            throw new Exception("Value was not collected after the collectible static was cleared");
        }

        GC.KeepAlive(field);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SetStatic(FieldInfo field)
    {
        var value = new object();
        field.SetValue(null, value);
        return new WeakReference(value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyReadBack(FieldInfo field, WeakReference weakValue)
    {
        var readBack = field.GetValue(null);

        if (!ReferenceEquals(readBack, weakValue.Target))
        {
            throw new Exception("Collectible static did not round-trip the stored reference");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestAssemblyUnloads()
    {
        var weakType = CreateAndDrop();

        // Unloading a collectible assembly can take several collections
        for (int i = 0; i < 10 && weakType.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        if (weakType.IsAlive)
        {
            throw new Exception("Collectible assembly did not unload after 10 collections");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDrop()
    {
        var instance = CreateCollectibleInstance("UnloadAsm");
        return new WeakReference(instance.GetType());
    }

    private static object CreateCollectibleInstance(string assemblyName)
    {
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName(assemblyName), AssemblyBuilderAccess.RunAndCollect);

        var moduleBuilder = assemblyBuilder.DefineDynamicModule("M");
        var typeBuilder = moduleBuilder.DefineType("CollectibleType", TypeAttributes.Public);

        var method = typeBuilder.DefineMethod("GetValue", MethodAttributes.Public, typeof(int), Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, 42);
        il.Emit(OpCodes.Ret);

        typeBuilder.DefineField("StaticField", typeof(object), FieldAttributes.Public | FieldAttributes.Static);

        var type = typeBuilder.CreateType();

        return Activator.CreateInstance(type)!;
    }
}
