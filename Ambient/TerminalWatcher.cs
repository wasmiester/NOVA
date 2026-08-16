using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;

namespace Nova;

// The other ambient source, alongside AmbientFileWatcher (same
// only-fires-while-engaged gating - see NovaAssistant.TriggerTerminalSuggestion):
// watches output from a plain-text pass-through terminal (see TerminalRelay/,
// launched via the open_watched_terminal tool) over a named pipe,
// buffers/debounces it, and asks the same Haiku gate whether it's worth
// surfacing.
//
// v1 simplification: one watched terminal at a time - the pipe server only
// accepts a single connection; a second relay process would just wait
// until the first disconnects (closing that window) before it could connect.
internal sealed class TerminalWatcher : IDisposable
{
    public const string PipeName = "nova-terminal-relay";

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);
    private const int MaxBufferedChars = 4000;

    private readonly AnthropicClient _client;
    private readonly Func<bool> _isEligible;
    private readonly Action<string> _onWorthSurfacing;
    private readonly CancellationTokenSource _cts = new();

    private readonly StringBuilder _buffer = new();
    private readonly object _bufferLock = new();
    private CancellationTokenSource? _debounceCts;

    public TerminalWatcher(AnthropicClient client, Func<bool> isEligible, Action<string> onWorthSurfacing)
    {
        _client = client;
        _isEligible = isEligible;
        _onWorthSurfacing = onWorthSurfacing;
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await ReadUntilDisconnectedAsync(server, token);
        }
    }

    private async Task ReadUntilDisconnectedAsync(NamedPipeServerStream server, CancellationToken token)
    {
        using var reader = new StreamReader(server, leaveOpen: true);
        var chunk = new char[1024];
        while (!token.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(chunk.AsMemory(), token);
            }
            catch
            {
                return;
            }

            if (read == 0)
            {
                return; // relay process disconnected - the terminal window closed
            }

            OnOutputReceived(new string(chunk, 0, read));
        }
    }

    private void OnOutputReceived(string text)
    {
        if (!_isEligible())
        {
            return;
        }

        lock (_bufferLock)
        {
            _buffer.Append(text);
            if (_buffer.Length > MaxBufferedChars)
            {
                _buffer.Remove(0, _buffer.Length - MaxBufferedChars);
            }

            // A command can print output over several rapid chunks - debounce
            // so the gate only runs once things settle, not mid-stream.
            _debounceCts?.Cancel();
            var cts = new CancellationTokenSource();
            _debounceCts = cts;
            _ = DebounceAndCheckAsync(cts.Token);
        }
    }

    private async Task DebounceAndCheckAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceDelay, token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by newer output
        }

        if (!_isEligible())
        {
            return;
        }

        string snapshot;
        lock (_bufferLock)
        {
            snapshot = _buffer.ToString();
            _buffer.Clear();
        }

        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return;
        }

        bool worthSurfacing = await HaikuGate.IsWorthSurfacingAsync(_client, GateSystemPrompt, snapshot, token);
        if (worthSurfacing && _isEligible())
        {
            _onWorthSurfacing(snapshot);
        }
    }

    private const string GateSystemPrompt =
        "You gate proactive interruptions for a voice coding assistant watching a terminal session. " +
        "Given a chunk of recent terminal output, answer with exactly one word: YES if it looks like " +
        "something a helpful collaborator would proactively comment on (e.g. a build or test run just " +
        "finished, especially if it failed; a clear error), NO if it's routine, mid-command, or not worth " +
        "interrupting for. Default to NO when unsure - being wrong by staying quiet is far cheaper than a " +
        "bad interruption.";

    public void Dispose() => _cts.Cancel();
}
