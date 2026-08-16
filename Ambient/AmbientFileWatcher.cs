using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;

namespace Nova;

// Comfort level 1 (autonomous): watches a directory for file changes and
// uses a cheap Haiku call to decide whether a change is worth proactively
// surfacing, before ever escalating to a real (Sonnet, full-conversation)
// turn. Per the roadmap, "the Haiku ambient gate sees only the current
// trigger" - not the conversation history - which is what keeps this cheap
// enough to run continuously. Fails closed: any error (including a locked
// file mid-write) is treated as "not worth it," never as a reason to
// escalate.
//
// v1 simplification: watches wherever the process was launched from
// (Directory.GetCurrentDirectory()) rather than a configured "current
// project" - there's no such setting yet.
internal sealed class AmbientFileWatcher : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);
    private const int MaxSnippetChars = 4000;

    // Without this, build output, intermediates, and this app's own SQLite
    // journal churn would fire on every build/turn - pure noise, and a
    // wasted Haiku call each time.
    private static readonly string[] ExcludedPathSegments =
    [
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}data{Path.DirectorySeparatorChar}",
    ];

    private static readonly string[] IncludedExtensions =
    [
        ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".go", ".rs", ".cpp", ".c", ".h",
        ".md", ".json", ".yaml", ".yml",
    ];

    private readonly FileSystemWatcher _watcher;
    private readonly AnthropicClient _client;
    private readonly Func<bool> _isEligible;
    private readonly Action<string, string> _onWorthSurfacing;

    private readonly object _debounceLock = new();
    private CancellationTokenSource? _debounceCts;

    public AmbientFileWatcher(string watchPath, AnthropicClient client, Func<bool> isEligible, Action<string, string> onWorthSurfacing)
    {
        _client = client;
        _isEligible = isEligible;
        _onWorthSurfacing = onWorthSurfacing;

        _watcher = new FileSystemWatcher(watchPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!_isEligible() || Directory.Exists(e.FullPath))
        {
            return;
        }

        if (ExcludedPathSegments.Any(segment => e.FullPath.Contains(segment, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (!IncludedExtensions.Contains(Path.GetExtension(e.FullPath), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        // A single save can fire several rapid change events - debounce so
        // the gate only runs once things settle, not mid-write.
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            var cts = new CancellationTokenSource();
            _debounceCts = cts;
            _ = DebounceAndCheckAsync(e.FullPath, cts.Token);
        }
    }

    private async Task DebounceAndCheckAsync(string path, CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceDelay, token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer change
        }

        if (!_isEligible())
        {
            return;
        }

        string? snippet = TryReadSnippet(path);
        if (snippet is null)
        {
            return;
        }

        bool worthSurfacing = await HaikuGate.IsWorthSurfacingAsync(_client, GateSystemPrompt, $"File: {path}\n\n{snippet}", token);
        if (worthSurfacing && _isEligible())
        {
            _onWorthSurfacing(path, snippet);
        }
    }

    private const string GateSystemPrompt =
        "You gate proactive interruptions for a voice coding assistant watching the user's files. " +
        "Given one changed file's path and current contents, answer with exactly one word: YES if this " +
        "looks like the kind of change a helpful collaborator would proactively comment on or offer to " +
        "act on (e.g. a class or function that looks freshly finished, a config change), NO if it's " +
        "routine, clearly mid-edit, generated, or otherwise not worth interrupting for. Default to NO " +
        "when unsure - being wrong by staying quiet is far cheaper than a bad interruption.";

    private static string? TryReadSnippet(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string content = File.ReadAllText(path);
            return content.Length > MaxSnippetChars ? content[..MaxSnippetChars] : content;
        }
        catch (IOException)
        {
            return null; // still locked/mid-write - skip this trigger rather than block
        }
    }

    public void Dispose() => _watcher.Dispose();
}
