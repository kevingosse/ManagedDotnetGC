using System.Runtime.InteropServices;

namespace TestApp;

internal unsafe class AppToGc
{
    public static void Initialize()
    {
        SetGetTypeCallback(&GetType);
    }

    [UnmanagedCallersOnly]
    public static unsafe void GetType(IntPtr address, char* buffer, int capacity, int* size)
    {
        var destination = new Span<char>(buffer, capacity);

        var obj = *(object*)&address;
        var type = obj.GetType().ToString();
        var length = Math.Min(type.Length, capacity);
        type[..length].CopyTo(destination);
        *size = length;
    }

    [DllImport("ManagedDotnetGC.dll")]
    private static extern void SetGetTypeCallback(delegate* unmanaged<IntPtr, char*, int, int*, void> callback);
}
