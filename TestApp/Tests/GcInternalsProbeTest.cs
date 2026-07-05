using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ManagedDotnetGC.Api;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Routes test-provided addresses through the GC's own IGCHeap query implementations
/// (GetContainingObject, IsHeapPointer, IsPromoted) via the managed API — a unit test of the
/// brick-table interior-pointer resolution, which becomes correctness-critical once memory reuse
/// lands and bricks can go stale.
/// (docs/missing-features.md item 6.3)
/// </summary>
public class GcInternalsProbeTest() : TestBase("GC Internals Probes", GcFeature.GcInternals)
{
    public override bool RequiresCustomGcApi => true;

    public override void Run()
    {
        var gc = GcApi.TryCreate() ?? throw new Exception("Failed to initialize GC API");

        // Pin the array so its address stays valid for the duration of the probes
        var array = new byte[4096];
        var handle = GCHandle.Alloc(array, GCHandleType.Pinned);

        try
        {
            nint objectStart = Utils.GetAddress(array);
            nint dataStart = handle.AddrOfPinnedObject();

            TestGetContainingObject(gc, objectStart, dataStart, array.Length);
            TestIsHeapPointer(gc, objectStart);
            TestIsPromoted(gc, objectStart);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void TestGetContainingObject(GcApi gc, nint objectStart, nint dataStart, int length)
    {
        // An interior pointer into the middle of the array must resolve to the object start
        AssertContainingObject(gc, dataStart + length / 2, objectStart, "the middle of the array");

        // Same for the first data byte...
        AssertContainingObject(gc, dataStart, objectStart, "the first data byte");

        // ...and for the very first byte of the object (the method table slot): stock find_object
        // resolves obj <= ptr < obj + size, so a pointer at the object start belongs to that object
        AssertContainingObject(gc, objectStart, objectStart, "the first byte of the object");

        // One past the end of the object (byte[] on x64: 16-byte header + data, already aligned)
        // is NOT inside it — it resolves to the next object or to nothing, never back to this one
        var onePastEnd = CheckProbeSentinels(gc.GetContainingObject(dataStart + length), "one past the end of the object");

        if (onePastEnd == objectStart)
        {
            throw new Exception("GetContainingObject(one past the end of the object) resolved back into the object");
        }

        // A stack address is not in the GC heap
        AssertContainingObject(gc, GetStackAddress(), 0, "a stack address");

        // Neither is null
        AssertContainingObject(gc, 0, 0, "null");
    }

    private static void TestIsHeapPointer(GcApi gc, nint objectStart)
    {
        AssertTriState(gc.IsHeapPointer(objectStart), true, "IsHeapPointer", "a heap object address");
        AssertTriState(gc.IsHeapPointer(GetStackAddress()), false, "IsHeapPointer", "a stack address");
        AssertTriState(gc.IsHeapPointer(0), false, "IsHeapPointer", "null");
    }

    private static void TestIsPromoted(GcApi gc, nint objectStart)
    {
        // The EE only consults IsPromoted mid-collection (ComWrappers/RCW detach during the
        // AfterGcScanRoots callback) and treats false as "dead, safe to detach" — so outside of a
        // collection the only safe answer is true. Stock's own answer outside a GC is stale mark
        // state; this asserts the safe contract, not observed stock behavior.
        AssertTriState(gc.IsPromoted(objectStart), true, "IsPromoted", "a heap object address outside a GC");
    }

    private static void AssertContainingObject(GcApi gc, nint address, nint expected, string description)
    {
        var actual = CheckProbeSentinels(gc.GetContainingObject(address), $"GetContainingObject({description})");

        if (actual != expected)
        {
            throw new Exception($"GetContainingObject({description}) returned {actual:x}, expected {expected:x}");
        }
    }

    private static void AssertTriState(int actual, bool expected, string method, string description)
    {
        CheckProbeSentinels(actual, $"{method}({description})");

        var expectedValue = expected ? GcProbe.True : GcProbe.False;

        if (actual != expectedValue)
        {
            throw new Exception($"{method}({description}) returned {actual}, expected {expectedValue}");
        }
    }

    private static nint CheckProbeSentinels(nint result, string description)
    {
        if (result == GcProbe.NotImplemented)
        {
            throw new Exception($"{description}: the underlying IGCHeap method is not implemented");
        }

        if (result == GcProbe.Error)
        {
            throw new Exception($"{description}: the underlying IGCHeap method threw (see the GC log)");
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe nint GetStackAddress()
    {
        int local = 0;
        return (nint)(&local);
    }
}
