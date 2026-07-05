using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests ref-counted handle semantics through ComWrappers: a managed object exposed to native code
/// (CCW) must stay alive while the native side holds a reference — even with no managed roots —
/// and become collectable once the native reference is released. This exercises
/// HNDTYPE_REFCOUNTED scanning (RefCountedHandleCallbacks) and its long-weak clearing.
/// (docs/missing-features.md item 3.2)
/// </summary>
public class ComWrappersTest() : TestBase("ComWrappers Lifetime", GcFeature.RefCountedHandles)
{
    public override void Run()
    {
        var wrappers = new TestComWrappers();

        var (ccw, weakObject) = CreateObjectWithCcw(wrappers);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // No managed reference exists, but the native side still holds the CCW:
        // the ref-counted handle must keep the object alive.
        if (!weakObject.IsAlive)
        {
            throw new Exception("Managed object with a native-referenced CCW was collected");
        }

        VerifyUnwrap(wrappers, ccw, weakObject);

        // Release the native reference: the object must now be collectable
        Marshal.Release(ccw);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        if (weakObject.IsAlive)
        {
            throw new Exception("Managed object survived after its last native CCW reference was released");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (nint Ccw, WeakReference WeakObject) CreateObjectWithCcw(TestComWrappers wrappers)
    {
        var obj = new object();

        nint ccw = wrappers.GetOrCreateComInterfaceForObject(obj, CreateComInterfaceFlags.None);

        return (ccw, new WeakReference(obj));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyUnwrap(TestComWrappers wrappers, nint ccw, WeakReference weakObject)
    {
        // While rooted, unwrapping the CCW must return the original instance
        var roundtrip = wrappers.GetOrCreateObjectForComInstance(ccw, CreateObjectFlags.Unwrap);

        if (!ReferenceEquals(roundtrip, weakObject.Target))
        {
            throw new Exception("Unwrapping the CCW did not return the original managed object");
        }
    }

    private sealed class TestComWrappers : ComWrappers
    {
        protected override unsafe ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
        {
            // No custom interfaces: the CCW only exposes IUnknown, which is all lifetime tests need
            count = 0;
            return null;
        }

        protected override object CreateObject(nint externalComObject, CreateObjectFlags flags)
            => throw new NotSupportedException();

        protected override void ReleaseObjects(System.Collections.IEnumerable objects)
            => throw new NotSupportedException();
    }
}
