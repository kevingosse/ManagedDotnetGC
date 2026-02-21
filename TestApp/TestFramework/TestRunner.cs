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

    private sealed record TestResult(string Name, bool Passed, string? Error);

    public void RegisterTest(TestBase test)
    {
        _tests.Add(test);
    }

    public bool RunAll()
    {
        AnsiConsole.Write(new Rule("[bold cyan]Test Execution[/]").RuleStyle("cyan").Centered());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine($"[dim]Running {_tests.Count} test(s)...[/]");
        AnsiConsole.WriteLine();

        var results = new List<TestResult>();

        AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(Spinner.Known.Dots)
            ])
            .Start(ctx =>
            {
                var task = ctx.AddTask("[cyan]Executing tests[/]", maxValue: _tests.Count);

                foreach (var test in _tests)
                {
                    results.Add(RunTest(test));
                    task.Increment(1);
                }
            });

        PrintResults(results);
        PrintSummary();

        return _failed == 0;
    }

    public bool RunSingle(string testName)
    {
        AnsiConsole.Write(new Rule("[bold cyan]Test Execution[/]").RuleStyle("cyan").Centered());
        AnsiConsole.WriteLine();

        var test = _tests.FirstOrDefault(t => t.Name.Equals(testName, StringComparison.OrdinalIgnoreCase));

        if (test == null)
        {
            AnsiConsole.MarkupLine($"[red]Test '{Markup.Escape(testName)}' not found.[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Available tests:[/]");
            foreach (var t in _tests)
            {
                AnsiConsole.MarkupLine($"  • {Markup.Escape(t.Name)}");
            }
            return false;
        }

        AnsiConsole.MarkupLine($"[dim]Running test: {Markup.Escape(test.Name)}...[/]");
        AnsiConsole.WriteLine();

        var result = RunTest(test);
        
        PrintResults([result]);
        PrintSummary();

        return _failed == 0;
    }

    private TestResult RunTest(TestBase test)
    {
        try
        {
            test.Setup();

            test.Run();

            test.Cleanup();

            _passed++;
            return new TestResult(test.Name, true, null);
        }
        catch (Exception ex)
        {
            _failed++;
            _failures.Add((test.Name, ex.Message));
            test.Cleanup();
            return new TestResult(test.Name, false, ex.Message);
        }
    }

    private static void PrintResults(IReadOnlyCollection<TestResult> results)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Results[/]").RuleStyle("grey"));

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[bold]Test[/]");
        table.AddColumn("[bold]Status[/]");
        table.AddColumn("[bold]Details[/]");

        foreach (var result in results)
        {
            var status = result.Passed ? "[green]Passed[/]" : "[red]Failed[/]";
            var details = result.Passed ? string.Empty : $"[red]{Markup.Escape(result.Error ?? "Failed")}[/]";
            table.AddRow(Markup.Escape(result.Name), status, details);
        }

        AnsiConsole.Write(table);
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
                AnsiConsole.MarkupLine($"[red]  • {Markup.Escape(testName)}[/]");
                AnsiConsole.MarkupLine($"[dim]    {Markup.Escape(error.Split('\n')[0])}[/]");
            }
        }

        AnsiConsole.MarkupLine("[bold cyan]═══════════════════════════════════════════════════[/]");
    }
}
