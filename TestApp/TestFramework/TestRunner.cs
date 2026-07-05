using Spectre.Console;

namespace TestApp.TestFramework;

/// <summary>
/// Test runner that executes registered tests and reports results.
/// Tests can be skipped for two reasons: they require the custom GC API and the stock GC is
/// running, or their feature is still pending (see Program.PendingFeatures) and wasn't explicitly
/// requested.
/// </summary>
public class TestRunner
{
    private readonly List<TestBase> _tests = new();
    private readonly IReadOnlySet<GcFeature> _pendingFeatures;
    private readonly bool _isCustomGcApiAvailable;
    private int _passed;
    private int _failed;
    private int _skipped;
    private readonly List<(string testName, string error)> _failures = new();

    private enum TestStatus { Passed, Failed, Skipped }

    private sealed record TestResult(string Name, TestStatus Status, Exception? Error, string? SkipReason);

    public TestRunner(IReadOnlySet<GcFeature> pendingFeatures, bool isCustomGcApiAvailable)
    {
        _pendingFeatures = pendingFeatures;
        _isCustomGcApiAvailable = isCustomGcApiAvailable;
    }

    public void RegisterTest(TestBase test)
    {
        _tests.Add(test);
    }

    public bool RunAll(bool includePendingFeatures)
    {
        return Execute(_tests, bypassFeatureGate: includePendingFeatures);
    }

    public bool RunFeatures(IReadOnlyCollection<GcFeature> features)
    {
        var selected = _tests.Where(t => features.Contains(t.Feature)).ToList();

        if (selected.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No tests found for feature(s): {Markup.Escape(string.Join(", ", features))}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Features with tests:[/]");
            foreach (var group in _tests.GroupBy(t => t.Feature).OrderBy(g => g.Key.ToString()))
            {
                AnsiConsole.MarkupLine($"  • {group.Key} ({group.Count()} test(s))");
            }
            return false;
        }

        return Execute(selected, bypassFeatureGate: true);
    }

    public bool RunSingle(string testName)
    {
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

        // Running a test by name bypasses the pending-feature gate (that's the point of asking for
        // it explicitly), but a test that needs the custom GC API still can't run on the stock GC.
        return Execute([test], bypassFeatureGate: true);
    }

    private bool Execute(IReadOnlyList<TestBase> tests, bool bypassFeatureGate)
    {
        AnsiConsole.Write(new Rule("[bold cyan]Test Execution[/]").RuleStyle("cyan").Centered());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine($"[dim]Running {tests.Count} test(s)...[/]");
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
                var task = ctx.AddTask("[cyan]Executing tests[/]", maxValue: tests.Count);

                foreach (var test in tests)
                {
                    var skipReason = GetSkipReason(test, bypassFeatureGate);

                    if (skipReason != null)
                    {
                        _skipped++;
                        results.Add(new TestResult(test.Name, TestStatus.Skipped, null, skipReason));
                    }
                    else
                    {
                        results.Add(RunTest(test));
                    }

                    task.Increment(1);
                }
            });

        PrintResults(results);
        PrintSummary();

        return _failed == 0;
    }

    private string? GetSkipReason(TestBase test, bool bypassFeatureGate)
    {
        if (test.RequiresCustomGcApi && !_isCustomGcApiAvailable)
        {
            return "requires the ManagedDotnetGC API (custom GC only)";
        }

        if (!bypassFeatureGate && _pendingFeatures.Contains(test.Feature))
        {
            return $"feature {test.Feature} not implemented yet (--feature {test.Feature} to run)";
        }

        return null;
    }

    private TestResult RunTest(TestBase test)
    {
        try
        {
            Watchdog.Arm(test.Name, test.TimeoutSeconds);

            test.Setup();
            test.Run();

            _passed++;
            return new TestResult(test.Name, TestStatus.Passed, null, null);
        }
        catch (Exception ex)
        {
            _failed++;
            _failures.Add((test.Name, ex.ToString()));
            return new TestResult(test.Name, TestStatus.Failed, ex, null);
        }
        finally
        {
            Watchdog.Disarm();
            test.Cleanup();
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
            var (status, details) = result.Status switch
            {
                TestStatus.Passed => ("[green]Passed[/]", string.Empty),
                TestStatus.Skipped => ("[yellow]Skipped[/]", $"[dim]{Markup.Escape(result.SkipReason ?? "")}[/]"),
                _ => ("[red]Failed[/]", $"[red]{Markup.Escape(result.Error?.Message ?? "Failed")}[/]")
            };

            table.AddRow(Markup.Escape(result.Name), status, details);
        }

        AnsiConsole.Write(table);
    }

    private void PrintSummary()
    {
        AnsiConsole.MarkupLine("[bold cyan]═══════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[bold]Test Summary[/]");
        AnsiConsole.WriteLine();

        var totalTests = _passed + _failed + _skipped;
        AnsiConsole.MarkupLine($"Total:  {totalTests}");

        if (_passed != 0)
        {
            AnsiConsole.MarkupLine($"[green]Passed: {_passed}[/]");
        }

        if (_skipped != 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Skipped: {_skipped}[/]");
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
                AnsiConsole.MarkupLine($"[dim]    {Markup.Escape(error)}[/]");
            }
        }

        AnsiConsole.MarkupLine("[bold cyan]═══════════════════════════════════════════════════[/]");
    }

    /// <summary>
    /// Fails fast with the offending test's name when a test exceeds its timeout, so CI shows which
    /// test hung instead of a silent zombie process. Limitation: this is a managed thread — a hang
    /// inside the GC with the EE suspended also suspends the watchdog. Those hangs can only be
    /// caught by an external (out-of-process) timeout.
    /// </summary>
    private static class Watchdog
    {
        private static Thread? _thread;
        private static string? _testName;
        private static long _deadline; // Environment.TickCount64-based; 0 = disarmed

        public static void Arm(string testName, int timeoutSeconds)
        {
            _testName = testName;
            Volatile.Write(ref _deadline, Environment.TickCount64 + timeoutSeconds * 1000L);

            if (_thread == null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "TestWatchdog" };
                _thread.Start();
            }
        }

        public static void Disarm() => Volatile.Write(ref _deadline, 0);

        private static void Loop()
        {
            while (true)
            {
                Thread.Sleep(1000);

                var deadline = Volatile.Read(ref _deadline);

                if (deadline != 0 && Environment.TickCount64 > deadline)
                {
                    var message = $"WATCHDOG: test '{_testName}' exceeded its timeout";
                    Console.Error.WriteLine(message);
                    Environment.FailFast(message);
                }
            }
        }
    }
}
