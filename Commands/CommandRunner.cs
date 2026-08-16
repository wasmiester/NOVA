using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nova;

internal static class CommandRunner
{
    public static async Task<string> RunCommandAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        string command = input["command"].GetString()!;

        ProcessRunner.ProcessResult result = await ProcessRunner.RunAsync("cmd.exe", "/c " + command, null, TimeSpan.FromSeconds(30), cancellationToken);
        if (result.Outcome == ProcessRunner.RunOutcome.TimedOut)
        {
            return "Command timed out after 30 seconds and was killed.";
        }

        const int MaxChars = 4000;
        string stdout = ProcessRunner.Truncate(result.Stdout, MaxChars);
        string stderr = ProcessRunner.Truncate(result.Stderr, MaxChars);
        return $"Exit code: {result.ExitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}";
    }
}
