using Spectre.Console;

namespace TestApp.TestFramework;

/// <summary>
/// Test runner that executes all registered tests and reports results
/// </summary>
public class TestRunner
{
    private readonly List<TestBase> _tests = new();
    private int _passed = 0;
    private int _failed = 0;
    private readonly List<(string testName, string error)> _failures = new();

    public void RegisterTest(TestBase test)
    {
        _tests.Add(test);
    }

    public bool RunAll()
    {
        AnsiConsole.MarkupLine("[bold cyan]╔══════════════════════════════════════════════════╗[/]");
        AnsiConsole.MarkupLine("[bold cyan]║    ManagedDotnetGC Tests                         ║[/]");
        AnsiConsole.MarkupLine("[bold cyan]╚══════════════════════════════════════════════════╝[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine($"[dim]Running {_tests.Count} test(s)...[/]");
        AnsiConsole.WriteLine();

        foreach (var test in _tests)
        {
            RunTest(test);
        }

        PrintSummary();

        return _failed == 0;
    }

    private void RunTest(TestBase test)
    {
        AnsiConsole.MarkupLine($"[bold]Test:[/] {test.Name}");
        AnsiConsole.MarkupLine($"[dim]{test.Description}[/]");

        try
        {
            test.Setup();

            var result = test.Run();

            test.Cleanup();

            if (result)
            {
                _passed++;
                AnsiConsole.MarkupLine("[green]✓ PASSED[/]");
            }
            else
            {
                _failed++;
                _failures.Add((test.Name, "Test returned false"));
                AnsiConsole.MarkupLine("[red]✗ FAILED[/]");
            }
        }
        catch (Exception ex)
        {
            _failed++;
            _failures.Add((test.Name, ex.ToString()));
            AnsiConsole.MarkupLine($"[red]✗ FAILED with exception: {ex.Message}[/]");
            test.Cleanup();
        }

        AnsiConsole.WriteLine();
    }

    private void PrintSummary()
    {
        AnsiConsole.MarkupLine("[bold cyan]═══════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[bold]Test Summary[/]");
        AnsiConsole.WriteLine();

        var totalTests = _passed + _failed;
        AnsiConsole.MarkupLine($"Total:  {totalTests}");

        if (_passed != 0)
        {
            AnsiConsole.MarkupLine($"[green]Passed: {_passed}[/]");
        }

        if (_failed != 0)
        {
            AnsiConsole.MarkupLine($"[red]Failed: {_failed}[/]");
        }

        if (_failures.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold red]Failed Tests:[/]");
            foreach (var (testName, error) in _failures)
            {
                AnsiConsole.MarkupLine($"[red]  • {testName}[/]");
                if (error != "Test returned false")
                {
                    AnsiConsole.MarkupLine($"[dim]    {error.Split('\n')[0]}[/]");
                }
            }
        }

        AnsiConsole.MarkupLine("[bold cyan]═══════════════════════════════════════════════════[/]");
    }
}
