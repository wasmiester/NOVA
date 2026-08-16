using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nova;

// Proactive inbox-watching: polls for unread mail and alerts on anything
// new since the last check - distinct from search_email, which is
// on-demand reading. Not gated on Engaged (unlike AmbientFileWatcher) -
// email/calendar are the deliberate exceptions to the sleep-state
// suppression (see NovaAssistant.TriggerEmailAlert); this only checks
// IsBusy and the email-watcher setting, so it never interrupts an active
// conversation.
internal sealed class GmailWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private const int MaxChecked = 10;

    // The baseline-establishing poll (first one ever) fetches more than a
    // regular poll does - if more than MaxChecked emails are unread at
    // startup, a plain 10-message baseline would only ever remember the
    // newest 10, and an older unread one that later resurfaces into a
    // regular poll's top-10 window (because newer mail got read/archived
    // in between) would wrongly look brand new. A larger one-time baseline
    // fetch closes most of that window without paying the cost on every
    // regular 2-minute poll.
    private const int BaselineMaxChecked = 50;

    private readonly GmailClient _gmail;
    private readonly Func<bool> _isEligible;
    private readonly Action<string> _onNewEmail;
    private readonly HashSet<string> _seenIds = [];
    private readonly CancellationTokenSource _cts = new();

    // First poll only establishes the baseline (today's unread inbox) -
    // without this, every already-unread email would get announced the
    // moment Nova starts, which is exactly the noisy behavior this is
    // supposed to avoid.
    private bool _baselineEstablished;

    public GmailWatcher(GmailClient gmail, Func<bool> isEligible, Action<string> onNewEmail)
    {
        _gmail = gmail;
        _isEligible = isEligible;
        _onNewEmail = onNewEmail;
        _ = PollLoopAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(token);
            }
            catch
            {
                // A transient API failure shouldn't kill the poller - just try again next interval.
            }

            try
            {
                await Task.Delay(PollInterval, token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken token)
    {
        if (!_baselineEstablished)
        {
            List<EmailSummary> baseline = await _gmail.FetchAsync("is:unread", BaselineMaxChecked, token);
            foreach (EmailSummary email in baseline)
            {
                _seenIds.Add(email.Id);
            }

            _baselineEstablished = true;
            return;
        }

        List<EmailSummary> unread = await _gmail.FetchAsync("is:unread", MaxChecked, token);
        foreach (EmailSummary email in unread)
        {
            if (_seenIds.Contains(email.Id))
            {
                continue;
            }

            // Deliberately does NOT mark this seen when not eligible (e.g.
            // Nova is mid-task) - a previous version added it to _seenIds
            // unconditionally here, which meant an email arriving while
            // busy was permanently forgotten instead of just deferred:
            // once "seen," it would never be retried on a later poll even
            // after Nova became free again. Leaving it unseen lets the
            // very next poll (or the one after that) catch it once
            // _isEligible() is true.
            if (!_isEligible())
            {
                continue;
            }

            _seenIds.Add(email.Id);
            _onNewEmail($"New email from {email.From}: \"{email.Subject}\" - {email.Snippet}");
        }
    }

    public void Dispose() => _cts.Cancel();
}
