using System.Reflection;
using System.Runtime.InteropServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests frozen segments by registering a frozen segment via GC._RegisterFrozenSegment,
/// allocating objects in it, running a GC, and verifying the objects and their method tables remain intact.
/// </summary>
public class FrozenSegmentTest : TestBase
{
    public FrozenSegmentTest()
        : base("Frozen Segments")
    {
    }

    public override unsafe void Run()
    {
        var bufferSize = 1024 * 1024; // 1 MB
        var address = (byte*)NativeMemory.AlignedAlloc((nuint)bufferSize, (nuint)IntPtr.Size);
        NativeMemory.Clear(address, (nuint)bufferSize);

        try
        {
            var cursor = address;

            // Write all objects into the buffer as raw bytes (no managed references yet)
            var strAddr = WriteString(ref cursor, ['H', 'e', 'l', 'l', 'o']);
            var arrayAddr = WriteArray(ref cursor, 10, 20, 30);
            var str2Addr = WriteString(ref cursor, ['W', 'o', 'r', 'l', 'd']);

            var segment = RegisterFrozenSegment((IntPtr)address, (nint)(cursor - address));

            try
            {
                // Now that the segment is registered, materialize managed references
                var str = *(string*)&strAddr;
                var array = *(int[]*)&arrayAddr;
                var str2 = *(string*)&str2Addr;

                // Run multiple GC collections
                GC.Collect();
                GC.Collect();
                GC.Collect();

                // Verify that the objects are intact
                if (str != "Hello")
                    throw new Exception($"str = \"{str}\" after GC, expected \"Hello\"");

                if (array is not [10, 20, 30])
                    throw new Exception($"array = [{string.Join(", ", array)}] after GC, expected [10, 20, 30]");

                if (str2 != "World")
                    throw new Exception($"str2 = \"{str2}\" after GC, expected \"World\"");

                // Verify that the method table pointers are unchanged after GC
                if (GetMethodTablePointer(str) != typeof(string).TypeHandle.Value)
                    throw new Exception("Method table pointer of str changed after GC");

                if (GetMethodTablePointer(array) != typeof(int[]).TypeHandle.Value)
                    throw new Exception("Method table pointer of array changed after GC");

                if (GetMethodTablePointer(str2) != typeof(string).TypeHandle.Value)
                    throw new Exception("Method table pointer of str2 changed after GC");
            }
            finally
            {
                UnregisterFrozenSegment(segment);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(address);
        }
    }

    private static unsafe nint GetMethodTablePointer(object obj)
    {
#pragma warning disable CS8500
        var objRef = *(nint**)&obj;
#pragma warning restore CS8500
        return *objRef;
    }

    /// <summary>
    /// Writes a string object into the buffer and advances the cursor.
    /// Returns a pointer to the object start (the method table pointer).
    /// </summary>
    private static unsafe nint WriteString(ref byte* cursor, ReadOnlySpan<char> data)
    {
        var ptr = cursor;

        // Write the object header (sync block)
        *(nint*)ptr = 0;
        ptr += sizeof(nint);

        // The object reference points here (at the method table pointer)
        var objStart = ptr;

        // Write the method table pointer
        *(nint*)ptr = typeof(string).TypeHandle.Value;
        ptr += sizeof(nint);

        // Write the string length
        *(int*)ptr = data.Length;
        ptr += sizeof(int);

        // Write the characters + null terminator
        var destination = new Span<char>(ptr, data.Length + 1);
        data.CopyTo(destination);
        destination[data.Length] = '\0';
        ptr += (data.Length + 1) * sizeof(char);

        ptr = AlignPointer(ptr);
        cursor = ptr;

        return (nint)objStart;
    }

    /// <summary>
    /// Writes an int array object header into the buffer, fills its elements, and advances the cursor.
    /// Returns a pointer to the object start (the method table pointer).
    /// </summary>
    private static unsafe nint WriteArray(ref byte* cursor, params int[] elements)
    {
        var ptr = cursor;

        // Write the object header (sync block)
        *(nint*)ptr = 0;
        ptr += sizeof(nint);

        // The object reference points here (at the method table pointer)
        var objStart = ptr;

        // Write the method table pointer
        *(nint*)ptr = typeof(int[]).TypeHandle.Value;
        ptr += sizeof(nint);

        // Write the array length
        *(int*)ptr = elements.Length;
        ptr += sizeof(int);

        // Align after length field
        ptr = AlignPointer(ptr);

        // Write elements
        var elementPtr = (int*)ptr;
        for (var i = 0; i < elements.Length; i++)
        {
            elementPtr[i] = elements[i];
        }

        ptr += elements.Length * sizeof(int);

        ptr = AlignPointer(ptr);
        cursor = ptr;

        return (nint)objStart;
    }

    private static unsafe byte* AlignPointer(byte* ptr) => (byte*)(((nint)ptr + IntPtr.Size - 1) & ~(IntPtr.Size - 1));

    private static IntPtr RegisterFrozenSegment(IntPtr sectionAddress, nint sectionSize)
    {
        return (IntPtr)typeof(GC).GetMethod("_RegisterFrozenSegment", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [sectionAddress, sectionSize])!;
    }

    private static void UnregisterFrozenSegment(IntPtr segment)
    {
        typeof(GC).GetMethod("_UnregisterFrozenSegment", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [segment]);
    }
}
