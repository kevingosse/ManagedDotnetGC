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

    private sealed record TestResult(string Name, string Description, bool Passed, string? Error);

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

    private TestResult RunTest(TestBase test)
    {
        try
        {
            test.Setup();

            var result = test.Run();

            test.Cleanup();

            if (result)
            {
                _passed++;
                return new TestResult(test.Name, test.Description, true, null);
            }

            _failed++;
            _failures.Add((test.Name, "Test returned false"));
            return new TestResult(test.Name, test.Description, false, "Test returned false");
        }
        catch (Exception ex)
        {
            _failed++;
            _failures.Add((test.Name, ex.ToString()));
            test.Cleanup();
            return new TestResult(test.Name, test.Description, false, ex.Message);
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
            var details = result.Passed
                ? Markup.Escape(result.Description)
                : $"[red]{Markup.Escape(result.Error ?? "Failed")}[/]";
            table.AddRow(Markup.Escape(result.Name), status, details);
        }

        AnsiConsole.Render(table);
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
