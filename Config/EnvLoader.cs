using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nova;

// Minimal KEY=VALUE .env loader. Only sets a variable if it isn't already
// present in the environment, so a real env var always wins over the file.
internal static class EnvLoader
{
    // Persists key/value pairs into the .env file at `path`, updating any
    // line whose key already matches in place and appending everything
    // else - every other line (comments, other secrets already there)
    // passes through untouched. Used by CredentialsPopup/
    // NovaAssistant.ConnectGoogleAccountAsync to save GOOGLE_CLIENT_ID/
    // GOOGLE_CLIENT_SECRET without clobbering ANTHROPIC_API_KEY or
    // anything else already in secrets/.env. Values are never logged -
    // this is the only place a caller should ever write them to disk.
    public static void SetValues(string path, IReadOnlyDictionary<string, string> values)
    {
        var remaining = new Dictionary<string, string>(values);
        List<string> lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            int separator = trimmed.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            string key = trimmed[..separator].Trim();
            if (remaining.TryGetValue(key, out string? value))
            {
                lines[i] = $"{key}={value}";
                remaining.Remove(key);
            }
        }

        foreach ((string key, string value) in remaining)
        {
            lines.Add($"{key}={value}");
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllLines(path, lines);
    }

    public static void Load(string path)
    {
        foreach ((string key, string value) in ReadValues(path))
        {
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    // Plain KEY=VALUE parse with no side effects - unlike Load, doesn't
    // touch Environment variables, since this is also used for typed
    // settings (see NovaSettings) that are read on demand rather than
    // pushed into process-wide env state.
    public static Dictionary<string, string> ReadValues(string path)
    {
        var values = new Dictionary<string, string>();
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            int separator = trimmed.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            string key = trimmed[..separator].Trim();
            string value = trimmed[(separator + 1)..].Trim().Trim('"');
            if (key.Length > 0 && !string.IsNullOrEmpty(value))
            {
                values[key] = value;
            }
        }

        return values;
    }
}
