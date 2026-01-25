using Spectre.Console;

namespace TestApp;

internal class InteriorPointerTest
{
    public static bool Run()
    {
        AnsiConsole.MarkupLine("[bold yellow]Interior pointer Test[/]");

        ref var interiorPointerSmall = ref GetInteriorPointer(10, out var weakRefSmall);
        ref var interiorPointerLarge = ref GetInteriorPointer(10 * 1024 * 1024, out var weakRefLarge);

        GC.Collect();

        if (!weakRefSmall.IsAlive)
        {
            AnsiConsole.MarkupLine("[bold red]Error: Weak reference should be alive after GC (small)[/]");
            return false;
        }

        if (!weakRefLarge.IsAlive)
        {
            AnsiConsole.MarkupLine("[bold red]Error: Weak reference should be alive after GC (large)[/]");
            return false;
        }

        GC.KeepAlive(interiorPointerSmall);
        GC.KeepAlive(interiorPointerLarge);

        return true;
    }

    private static ref int GetInteriorPointer(int size, out WeakReference weakRef)
    {
        var array = new int[size];
        AnsiConsole.WriteLine($"Array address: {Utils.GetAddress(array):x2}");
        weakRef = new WeakReference(array);
        return ref array[5];
    }
}
