using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nova;

// Proactive calendar-reminder watching: polls upcoming events and alerts
// once each crosses into the reminder lead window (NovaSettings.
// CalendarReminderLeadMinutes, 30 by default) before its start time -
// distinct from list_calendar_events, which is on-demand reading. Not
// gated on Engaged (unlike AmbientFileWatcher) - the user asked for
// email/calendar to be the deliberate exceptions to the sleep-state
// suppression (see NovaAssistant.TriggerEmailAlert's own doc comment);
// this only checks IsBusy/the settings toggle, so it never interrupts an
// active conversation and can still be turned off entirely.
internal sealed class CalendarWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private const int MaxChecked = 15;

    private readonly CalendarClient _calendar;
    private readonly Func<bool> _isEligible;
    private readonly Func<int> _leadMinutes;
    private readonly Action<string> _onReminder;

    // Which events have already been reminded about, by their stable
    // Google-assigned event ID - unlike GmailWatcher's _seenIds, this never
    // needs a "baseline" pass, since an event's start time (not "have I
    // seen this ID before") is what actually decides whether a reminder is
    // due, so there's nothing to wrongly announce on the very first poll.
    private readonly HashSet<string> _remindedIds = [];
    private readonly CancellationTokenSource _cts = new();

    public CalendarWatcher(CalendarClient calendar, Func<bool> isEligible, Func<int> leadMinutes, Action<string> onReminder)
    {
        _calendar = calendar;
        _isEligible = isEligible;
        _leadMinutes = leadMinutes;
        _onReminder = onReminder;
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
        List<(string Id, string Summary, DateTimeOffset Start)> events = await _calendar.GetUpcomingEventsAsync(MaxChecked, token);
        DateTimeOffset now = DateTimeOffset.Now;
        TimeSpan lead = TimeSpan.FromMinutes(_leadMinutes());

        foreach ((string id, string summary, DateTimeOffset start) in events)
        {
            if (_remindedIds.Contains(id))
            {
                continue;
            }

            TimeSpan until = start - now;
            if (until > lead || until < TimeSpan.Zero)
            {
                continue; // not yet in the reminder window, or already started/passed
            }

            // Deliberately does NOT mark this reminded when not eligible
            // (e.g. Nova is mid-task or the watcher is disabled) - same
            // "defer, don't forget" reasoning as GmailWatcher's identical
            // check, so the next poll (still inside the lead window) gets
            // another chance to actually surface it.
            if (!_isEligible())
            {
                continue;
            }

            _remindedIds.Add(id);
            int minutesUntil = Math.Max(0, (int)Math.Round(until.TotalMinutes));
            _onReminder($"\"{summary}\" starts at {start:h:mm tt} - about {minutesUntil} minute{(minutesUntil == 1 ? "" : "s")} from now.");
        }
    }

    public void Dispose() => _cts.Cancel();
}
