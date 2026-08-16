using System;
using System.Collections.Generic;
using System.Text;

namespace Nova;

// Small dedicated settings store for Nova-adjustable tool preferences
// (calendar reminder lead time, watcher on/off) - deliberately separate
// from both secrets/.env (API keys/OAuth creds, not settings) and
// memory.db (learned facts/technical knowledge, not app configuration).
// Backed by the same KEY=VALUE format as EnvLoader, at data/settings.env,
// but read/written directly rather than through Environment variables -
// these are typed, defaulted values read on demand, not process-wide env
// state. Exposed to Nova herself via the get_settings/update_setting tools
// (see ToolCatalog) so she can read and change them by voice, not just a
// human hand-editing the file.
internal sealed class NovaSettings
{
    internal const string CalendarReminderLeadMinutesKey = "calendar_reminder_lead_minutes";
    internal const string CalendarWatcherEnabledKey = "calendar_watcher_enabled";
    internal const string EmailWatcherEnabledKey = "email_watcher_enabled";

    private const int DefaultCalendarReminderLeadMinutes = 30;

    // Every known setting, in display order - both DescribeAll and
    // update_setting's schema/validation are driven from this single list
    // so a new setting only needs adding here, not in three places.
    internal static readonly IReadOnlyList<string> KnownKeys =
        [CalendarReminderLeadMinutesKey, CalendarWatcherEnabledKey, EmailWatcherEnabledKey];

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string> _values;

    public NovaSettings(string path)
    {
        _path = path;
        _values = EnvLoader.ReadValues(path);
    }

    public int CalendarReminderLeadMinutes
    {
        get
        {
            lock (_lock)
            {
                return _values.TryGetValue(CalendarReminderLeadMinutesKey, out string? value) && int.TryParse(value, out int minutes) && minutes > 0
                    ? minutes
                    : DefaultCalendarReminderLeadMinutes;
            }
        }
    }

    public bool CalendarWatcherEnabled => GetBool(CalendarWatcherEnabledKey, defaultValue: true);

    public bool EmailWatcherEnabled => GetBool(EmailWatcherEnabledKey, defaultValue: true);

    // Returns an error message on failure (bad key/value), or null on
    // success - mirrors the plain string-return convention every other
    // tool implementation in this codebase uses instead of exceptions for
    // expected, user-facing failure modes.
    public string? TrySet(string key, string rawValue)
    {
        switch (key)
        {
            case CalendarReminderLeadMinutesKey:
                if (!int.TryParse(rawValue, out int minutes) || minutes <= 0)
                {
                    return $"\"{rawValue}\" isn't a positive whole number of minutes.";
                }

                Set(key, minutes.ToString());
                return null;

            case CalendarWatcherEnabledKey:
            case EmailWatcherEnabledKey:
                if (!TryParseBool(rawValue, out bool enabled))
                {
                    return $"\"{rawValue}\" isn't true/false.";
                }

                Set(key, enabled ? "true" : "false");
                return null;

            default:
                return $"Unknown setting \"{key}\" - known settings: {string.Join(", ", KnownKeys)}.";
        }
    }

    public string DescribeAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{CalendarReminderLeadMinutesKey} = {CalendarReminderLeadMinutes} (default {DefaultCalendarReminderLeadMinutes})");
        sb.AppendLine($"{CalendarWatcherEnabledKey} = {(CalendarWatcherEnabled ? "true" : "false")} (default true)");
        sb.Append($"{EmailWatcherEnabledKey} = {(EmailWatcherEnabled ? "true" : "false")} (default true)");
        return sb.ToString();
    }

    private bool GetBool(string key, bool defaultValue)
    {
        lock (_lock)
        {
            return _values.TryGetValue(key, out string? value) ? value.Equals("true", StringComparison.OrdinalIgnoreCase) : defaultValue;
        }
    }

    private static bool TryParseBool(string rawValue, out bool result)
    {
        string lower = rawValue.Trim().ToLowerInvariant();
        if (lower is "true" or "yes" or "on")
        {
            result = true;
            return true;
        }

        if (lower is "false" or "no" or "off")
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    private void Set(string key, string value)
    {
        lock (_lock)
        {
            _values[key] = value;
            EnvLoader.SetValues(_path, _values);
        }
    }
}
