using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Nova;

// Shared "spawn a process, capture output, enforce an internal timeout
// independent of the caller's own cancellation token" mechanics - previously
// reimplemented identically three times (CommandRunner, DynamicToolRuntime,
// ToolGitVersioning), including a verbatim-copied Truncate helper. That
// triplication was also how the same barge-in-leaves-a-zombie-process bug
// ended up needing the same fix in three separate places (see RunAsync's own
// comment below) - now fixed once, here. Each caller still owns its own
// timeout duration and how to report a TimedOut outcome (a plain return
// value for CommandRunner/DynamicToolRuntime, a thrown exception for
// ToolGitVersioning) - only the process lifecycle mechanics are shared.
internal static class ProcessRunner
{
    internal enum RunOutcome { Completed, TimedOut }

    internal readonly record struct ProcessResult(RunOutcome Outcome, int ExitCode, string Stdout, string Stderr);

    public static async Task<ProcessResult> RunAsync(string fileName, string arguments, string? workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        using Process process = Process.Start(psi)!;
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Kill the process regardless of which token actually fired -
            // the previous per-call-site version of this only killed it on
            // the internal timeout (a `when (!cancellationToken.
            // IsCancellationRequested)` guard on the catch), which meant a
            // real barge-in cancelling cancellationToken left the process
            // running detached: that guard made the catch not apply, so the
            // exception just propagated straight past the kill call.
            // Killing first, then re-throwing for a genuine outer
            // cancellation (instead of swallowing it into a return value
            // the way a timeout is), keeps the exact same
            // propagate-to-the-caller behavior every caller already relies
            // on - just without leaking the process anymore either way.
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(RunOutcome.TimedOut, -1, "", "");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        return new ProcessResult(RunOutcome.Completed, process.ExitCode, stdout, stderr);
    }

    public static string Truncate(string text, int maxChars) =>
        text.Length > maxChars ? text[..maxChars] + "... [truncated]" : text;
}
