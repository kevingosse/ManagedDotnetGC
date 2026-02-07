using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests GCHandle.Alloc with pinned objects
/// </summary>
public class PinnedObjectTest : TestBase
{
    public PinnedObjectTest()
        : base("Pinned Objects", "Verifies that pinned objects remain accessible and don't move")
    {
    }

    public override bool Run()
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
            {
                return false;
            }

            // Force a GC
            GC.Collect();

            // The address should still be valid
            IntPtr addressAfterGC = handle.AddrOfPinnedObject();

            if (addressAfterGC != address)
            {
                return false; // Object moved even though it was pinned!
            }

            // Verify the data is still intact
            unsafe
            {
                byte* ptr = (byte*)address.ToPointer();
                for (int i = 0; i < array.Length; i++)
                {
                    if (ptr[i] != (byte)i)
                    {
                        return false;
                    }
                }
            }

            // Verify via the managed reference too
            for (int i = 0; i < array.Length; i++)
            {
                if (array[i] != (byte)i)
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            handle.Free();
        }
    }
}
