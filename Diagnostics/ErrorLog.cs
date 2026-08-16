using System;
using System.IO;
using System.Linq;

namespace Nova;

// Baseline error logging - normal engineering practice, not a designed
// feature. Appends timestamped exception details to a local log file so
// there's a record to look at after the fact. See ReadRecent below for the
// "want to look" half of the self-healing behavior - this file only ever
// writes/reads plain text, it doesn't reason about severity itself.
internal static class ErrorLog
{
    private const string EntrySeparator = "\n\n";

    private static readonly object WriteLock = new();
    private static string _path = "errors.log";

    public static void Initialize(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public static void Log(string context, Exception ex)
    {
        string entry = $"[{DateTime.UtcNow:o}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
        try
        {
            lock (WriteLock)
            {
                File.AppendAllText(_path, entry);
            }
        }
        catch
        {
            // A logging failure shouldn't cascade into more errors.
        }
    }

    // The "want to look into it" half of the self-healing surfacing (see
    // NovaAssistant.ProcessTextInputAsync's outer catch) - lets Claude
    // actually read what was logged instead of the user having to open the
    // file by hand. Reads the most recent `count` entries (each one a full
    // timestamp+context+exception block, split on the same blank-line
    // separator Log writes between entries) rather than a raw line/char
    // tail, so a multi-line stack trace never gets cut mid-entry.
    public static string ReadRecent(int count)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return "No errors logged.";
            }

            string[] entries = File.ReadAllText(_path)
                .Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            if (entries.Length == 0)
            {
                return "No errors logged.";
            }

            return string.Join(EntrySeparator, entries.TakeLast(count));
        }
        catch (Exception ex)
        {
            return $"Couldn't read the error log: {ex.Message}";
        }
    }
}
