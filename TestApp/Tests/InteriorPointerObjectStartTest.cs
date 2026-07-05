using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// An interior pointer may land on any byte of an object, including the very first one (the
/// method-table word): the stock GC resolves GC_CALL_INTERIOR roots anywhere from the first byte
/// of the object up to (but excluding) one past the end. ScanRoots currently drops that edge:
/// FindClosestObjectBelow returns the object itself, and WalkHeapObjects(closestBelow, root) walks
/// an empty range because of its exclusive upper bound, so the object is never marked. This test
/// keeps an array alive through such a pointer alone.
/// </summary>
public class InteriorPointerObjectStartTest() : TestBase("Interior pointer at object start", GcFeature.InteriorPointers)
{
    // byte[] layout on x64: 8-byte method table pointer, 4-byte length, 4 bytes of padding
    private const int ArrayDataOffset = 16;

    public override void Run()
    {
        // Control: a pointer to the first data byte must keep the array alive — validates the
        // mechanics of the test itself
        RunScenario(offsetFromObjectStart: ArrayDataOffset, "the first data byte");

        // The edge case: a pointer at the very first byte of the object
        RunScenario(offsetFromObjectStart: 0, "the first byte of the object");
    }

    private static void RunScenario(int offsetFromObjectStart, string description)
    {
        ref byte interiorPointer = ref CreateArray(offsetFromObjectStart, out var weakRef);

        GC.Collect();

        if (!weakRef.IsAlive)
        {
            throw new Exception($"Array not alive when an interior pointer at {description} is held on stack");
        }

        GC.KeepAlive(interiorPointer);
    }

    // The array reference must not outlive this frame: the byref returned to the caller is its only root
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ref byte CreateArray(int offsetFromObjectStart, out WeakReference weakRef)
    {
        var array = new byte[1024];
        weakRef = new WeakReference(array);

        return ref Unsafe.SubtractByteOffset(
            ref MemoryMarshal.GetArrayDataReference(array),
            (nuint)(ArrayDataOffset - offsetFromObjectStart));
    }
}
