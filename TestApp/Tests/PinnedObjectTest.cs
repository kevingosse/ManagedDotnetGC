using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests GCHandle.Alloc with pinned objects - verifies that pinned objects don't move
/// </summary>
public class PinnedObjectTest() : TestBase("Pinned Objects")
{
    public override void Run()
    {
        var array = new byte[1024];
        for (int i = 0; i < array.Length; i++)
        {
            array[i] = (byte)i;
        }

        // Pin the array
        GCHandle handle = GCHandle.Alloc(array, GCHandleType.Pinned);

        try
        {
            // Get the address
            IntPtr address = handle.AddrOfPinnedObject();

            if (address == IntPtr.Zero)
                throw new Exception("AddrOfPinnedObject returned zero");

            // Force a GC
            GC.Collect();

            // The address should still be valid
            IntPtr addressAfterGC = handle.AddrOfPinnedObject();

            if (addressAfterGC != address)
                throw new Exception($"Pinned object moved during GC: 0x{address:X} -> 0x{addressAfterGC:X}");

            // Verify the data is still intact
            unsafe
            {
                byte* ptr = (byte*)address.ToPointer();
                for (int i = 0; i < array.Length; i++)
                {
                    if (ptr[i] != (byte)i)
                        throw new Exception($"Pinned data corrupted at ptr[{i}]: got {ptr[i]}, expected {(byte)i}");
                }
            }

            // Verify via the managed reference too
            for (int i = 0; i < array.Length; i++)
            {
                if (array[i] != (byte)i)
                    throw new Exception($"Managed array corrupted at [{i}]: got {array[i]}, expected {(byte)i}");
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
