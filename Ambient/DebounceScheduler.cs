using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nova;

// Shared by AmbientFileWatcher and TerminalWatcher - both collapse a rapid
// burst of events (several file-save notifications, several chunks of
// terminal output) into a single check once things settle, cancelling
// whatever check was still pending whenever a newer event arrives. Used to
// be duplicated almost verbatim between the two (each with its own
// CancellationTokenSource field, lock, and cancel-then-replace dance).
internal sealed class DebounceScheduler
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    // Cancels any still-pending run from a previous call, then schedules a
    // fresh one after `delay`. `isEligible` is re-checked once the delay
    // elapses (not just at schedule time) since the ambient trigger this was
    // scheduled for may have stopped mattering - the task ended, or Nova
    // went dormant - while the debounce was still ticking.
    public void Schedule(TimeSpan delay, Func<bool> isEligible, Func<CancellationToken, Task> body)
    {
        lock (_lock)
        {
            _cts?.Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = RunAsync(delay, isEligible, body, cts.Token);
        }
    }

    private static async Task RunAsync(TimeSpan delay, Func<bool> isEligible, Func<CancellationToken, Task> body, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer event
        }

        if (!isEligible())
        {
            return;
        }

        await body(token);
    }
}
