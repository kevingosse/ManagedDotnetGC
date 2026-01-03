using Spectre.Console;
using System.Runtime.CompilerServices;

namespace TestApp;

internal class WeakReferenceTest
{
    public static bool Run()
    {
        AnsiConsole.MarkupLine("[bold yellow]WeakReference Test[/]");

        AnsiConsole.MarkupLine("[bold yellow]WeakReference[/]");
        var weakRef = GetWeakReference();

        if (!weakRef.IsAlive)
        {
            AnsiConsole.MarkupLine("[bold red]Error: Weak reference should be alive initially[/]");
            return false;
        }

        GC.Collect();

        if (weakRef.IsAlive)
        {
            AnsiConsole.MarkupLine("[bold red]Error: Weak reference should not be alive after GC[/]");
            return false;
        }

        AnsiConsole.MarkupLine("[bold yellow]WeakReference<T>[/]");
        var typedWeakRef = GetTypedWeakReference();

        if (!IsAlive(typedWeakRef))
        {
            AnsiConsole.MarkupLine("[bold red]Error: Typed weak reference should be alive initially[/]");
            return false;
        }

        GC.Collect();        

        if (IsAlive(typedWeakRef))
        {
            AnsiConsole.MarkupLine("[bold red]Error: Typed weak reference should not be alive after GC[/]");
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference GetWeakReference()
    {
        var target = new[] { 0, 1, 2 };
        return new WeakReference(target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<int[]> GetTypedWeakReference()
    {
        var target = new[] { 0, 1, 2 };
        return new WeakReference<int[]>(target);
    }

    // IsAlive is moved to a separate helper, because the out parameter creates a root that keeps the target alive
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> weakRef) where T : class
    {
        return weakRef.TryGetTarget(out _);
    }
}
