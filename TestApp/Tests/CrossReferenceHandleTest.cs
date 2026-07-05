using System.Reflection;
using System.Runtime.InteropServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that the handle table can store HNDTYPE_CROSSREFERENCE (handle type 11). No public API
/// creates one on win-x64 (it's the FEATURE_JAVAMARSHAL Java-bridge handle), so the test calls
/// GCHandle.InternalAlloc through reflection: on a release runtime the FCall's type check is a
/// compiled-out assert (runtime-vm/marshalnative.cpp:342 — a debug/checked runtime would trip it),
/// and the type lands unvalidated in IGCHandleStore::CreateHandleOfType. Without the Java bridge
/// the GC excludes type 11 from every scan list — it is not a root, is never cleared, and is not
/// even updated when the target moves (runtime-gc/objecthandle.cpp:1724) — so the target is kept
/// alive by a normal strong reference and *pinned* (POH) so the raw slot cannot go stale when the
/// stock GC compacts. The handle only has to store, read back and free cleanly — which is exactly
/// the storage contract of item 4.1. (docs/missing-features.md item 4.1)
/// </summary>
public class CrossReferenceHandleTest() : TestBase("Cross-Reference Handles", GcFeature.NewHandleTypes)
{
    private const GCHandleType CrossReference = (GCHandleType)11; // HNDTYPE_CROSSREFERENCE

    public override void Run()
    {
        var internalAlloc = typeof(GCHandle).GetMethod("InternalAlloc", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("GCHandle.InternalAlloc not found - runtime internals changed");

        // Pinned so the address is stable across collections: nothing updates a type-11 slot when
        // the (compacting) stock GC moves the target
        var target = GC.AllocateArray<byte>(64, pinned: true);
        var rawHandle = (IntPtr)internalAlloc.Invoke(null, [target, CrossReference])!;

        if (rawHandle == IntPtr.Zero)
        {
            throw new Exception("InternalAlloc returned a null handle for HNDTYPE_CROSSREFERENCE");
        }

        var handle = GCHandle.FromIntPtr(rawHandle);

        try
        {
            if (!ReferenceEquals(handle.Target, target))
            {
                throw new Exception("Cross-reference handle did not round-trip its target");
            }

            // The handle must survive a collection in place (the target stays alive through the
            // local strong reference, not through the handle - type 11 is never scanned here)
            GC.Collect();

            if (!ReferenceEquals(handle.Target, target))
            {
                throw new Exception("Cross-reference handle lost its target across a collection");
            }
        }
        finally
        {
            handle.Free();
        }

        GC.KeepAlive(target);
    }
}
