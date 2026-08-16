using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

namespace Nova;

internal sealed class CalendarClient
{
    private const string PrimaryCalendarId = "primary";
    private readonly CalendarService _service;

    public CalendarClient(UserCredential credential)
    {
        _service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Nova",
        });
    }

    public async Task<string> ListUpcomingAsync(int maxResults, CancellationToken cancellationToken)
    {
        IList<Event> items = await FetchUpcomingAsync(maxResults, cancellationToken);
        if (items.Count == 0)
        {
            return "No upcoming events.";
        }

        var sb = new StringBuilder();
        foreach (Event e in items)
        {
            string when = e.Start?.DateTimeDateTimeOffset?.ToString("ddd MMM d, h:mm tt") ?? e.Start?.Date ?? "unknown time";
            sb.AppendLine($"{when}: {e.Summary}");
        }

        return sb.ToString();
    }

    // Structured counterpart to ListUpcomingAsync, for CalendarWatcher's
    // reminder polling rather than human-facing display. Skips all-day
    // events (Start.Date, no DateTimeDateTimeOffset) - there's no specific
    // time to count down to, so a lead-time reminder doesn't apply to them.
    public async Task<List<(string Id, string Summary, DateTimeOffset Start)>> GetUpcomingEventsAsync(int maxResults, CancellationToken cancellationToken)
    {
        IList<Event> items = await FetchUpcomingAsync(maxResults, cancellationToken);
        var results = new List<(string, string, DateTimeOffset)>();
        foreach (Event e in items)
        {
            if (e.Start?.DateTimeDateTimeOffset is not { } start || e.Id is null)
            {
                continue;
            }

            results.Add((e.Id, e.Summary ?? "(untitled event)", start));
        }

        return results;
    }

    private async Task<IList<Event>> FetchUpcomingAsync(int maxResults, CancellationToken cancellationToken)
    {
        EventsResource.ListRequest request = _service.Events.List(PrimaryCalendarId);
        request.TimeMinDateTimeOffset = DateTimeOffset.Now;
        request.SingleEvents = true; // expand recurring events into individual instances
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
        request.MaxResults = maxResults;

        Events events = await request.ExecuteAsync(cancellationToken);
        return events.Items ?? [];
    }

    public async Task<string> CreateEventAsync(string summary, DateTimeOffset start, DateTimeOffset end, string? description, CancellationToken cancellationToken)
    {
        var newEvent = new Event
        {
            Summary = summary,
            Description = description,
            Start = new EventDateTime { DateTimeDateTimeOffset = start },
            End = new EventDateTime { DateTimeDateTimeOffset = end },
        };

        Event created = await _service.Events.Insert(newEvent, PrimaryCalendarId).ExecuteAsync(cancellationToken);
        return $"Created \"{summary}\" on your calendar for {start:ddd MMM d, h:mm tt}.";
    }
}
