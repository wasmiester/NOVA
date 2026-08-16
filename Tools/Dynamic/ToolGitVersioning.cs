using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nova;

// Git-backed version history for self-contained tools - a plain local repo
// rooted at SelfContainedTools/ (separate from the main Nova app, which
// isn't a git repo at all), so every successful build of a tool becomes a
// real, revertible commit. Deliberately never rewrites history (no reset
// --hard, no force anything) - a "revert" is always a *new* commit that
// restores an older version's content, the same shape `git revert` uses,
// so the commit log stays a straight, honest record of what actually
// happened rather than something that could look like it never did.
internal static class ToolGitVersioning
{
    public static void EnsureRepo(string toolsRootDir)
    {
        Directory.CreateDirectory(toolsRootDir);
        if (Directory.Exists(Path.Combine(toolsRootDir, ".git")))
        {
            return;
        }

        RunGit(toolsRootDir, "init");
        // Repo-local identity (not --global) - this must never touch the
        // user's own global git config, and a fresh machine/environment
        // may not have one set at all, which would otherwise make every
        // commit below fail outright ("Please tell me who you are").
        RunGit(toolsRootDir, "config user.email \"nova@local\"");
        RunGit(toolsRootDir, "config user.name \"Nova\"");
    }

    // Commits whatever's currently on disk under toolsRootDir/toolName -
    // returns the resulting commit hash, or the existing HEAD hash
    // unchanged if a rebuild produced byte-identical content (git reports
    // "nothing to commit" in that case, which isn't an error here, just
    // means no new version actually exists).
    public static async Task<string> CommitAsync(string toolsRootDir, string toolName, string message, CancellationToken cancellationToken)
    {
        await RunGitAsync(toolsRootDir, $"add \"{toolName}\"", cancellationToken);
        await RunGitAsync(toolsRootDir, $"commit -m \"{EscapeForCommitMessage(message)}\" -- \"{toolName}\"", cancellationToken, allowNonZeroExit: true);
        (int _, string headHash) = await RunGitAsync(toolsRootDir, "rev-parse HEAD", cancellationToken);
        return headHash.Trim();
    }

    // Reverts one tool to the version immediately before its current one -
    // never further back than that in one call, matching "3 failures ->
    // step back once" rather than trying to guess how far back is safe.
    // Returns null if there's no earlier version to revert to (a
    // brand-new tool that's never been rebuilt).
    public static async Task<string?> RevertOneVersionAsync(string toolsRootDir, string toolName, CancellationToken cancellationToken)
    {
        (int _, string logOutput) = await RunGitAsync(toolsRootDir, $"log --format=%H -- \"{toolName}\"", cancellationToken);
        string[] commits = logOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (commits.Length < 2)
        {
            return null; // nothing earlier to go back to
        }

        string previousCommit = commits[1];
        await RunGitAsync(toolsRootDir, $"checkout {previousCommit} -- \"{toolName}\"", cancellationToken);
        await RunGitAsync(toolsRootDir, $"commit -m \"Revert {toolName} to previous version ({previousCommit[..8]})\" -- \"{toolName}\"", cancellationToken);
        (int _, string headHash) = await RunGitAsync(toolsRootDir, "rev-parse HEAD", cancellationToken);
        return headHash.Trim();
    }

    private static string EscapeForCommitMessage(string message) => message.Replace("\"", "'");

    private static void RunGit(string workingDir, string arguments) =>
        RunGitAsync(workingDir, arguments, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<(int ExitCode, string Stdout)> RunGitAsync(string workingDir, string arguments, CancellationToken cancellationToken, bool allowNonZeroExit = false)
    {
        ProcessRunner.ProcessResult result = await ProcessRunner.RunAsync("git", arguments, workingDir, TimeSpan.FromSeconds(30), cancellationToken);
        if (result.Outcome == ProcessRunner.RunOutcome.TimedOut)
        {
            throw new InvalidOperationException($"git {arguments} timed out after 30 seconds.");
        }

        if (result.ExitCode != 0 && !allowNonZeroExit)
        {
            throw new InvalidOperationException($"git {arguments} failed (exit {result.ExitCode}): {result.Stderr}");
        }

        return (result.ExitCode, result.Stdout);
    }
}
