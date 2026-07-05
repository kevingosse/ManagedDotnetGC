using System.Diagnostics;

namespace TestApp.TestFramework;

/// <summary>
/// Relaunches the test executable as a child process. Used by tests that need environment
/// variables set before runtime startup (e.g. DOTNET_GCHeapHardLimit) or that would take the whole
/// process down on failure. The child inherits the current environment (including DOTNET_GCName,
/// so it runs on the same GC as the parent) plus the provided overrides.
/// </summary>
public static class ChildProcess
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr, bool TimedOut)
    {
        public string Describe() =>
            TimedOut
                ? $"timed out. Stdout: {StdOut} Stderr: {StdErr}"
                : $"exited with code {ExitCode}. Stdout: {StdOut} Stderr: {StdErr}";
    }

    public static Result Run(string arguments, IReadOnlyDictionary<string, string> extraEnvironment, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var (key, value) in extraEnvironment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new Exception($"Failed to start child process {startInfo.FileName}");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited in the meantime
            }

            process.WaitForExit();
            return new Result(-1, SafeResult(stdout), SafeResult(stderr), TimedOut: true);
        }

        // WaitForExit(milliseconds) can return before the output streams are drained;
        // the parameterless overload flushes them.
        process.WaitForExit();

        return new Result(process.ExitCode, SafeResult(stdout), SafeResult(stderr), TimedOut: false);

        static string SafeResult(Task<string> task)
        {
            try
            {
                return task.GetAwaiter().GetResult().Trim();
            }
            catch (Exception ex)
            {
                return $"<failed to read: {ex.Message}>";
            }
        }
    }
}
