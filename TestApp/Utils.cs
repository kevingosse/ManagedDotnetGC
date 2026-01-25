namespace TestApp;

internal class Utils
{
    public static unsafe nint GetAddress(object? obj)
    {
#pragma warning disable CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type
        return (nint)(*(object**)&obj);
#pragma warning restore CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type
    }
}
