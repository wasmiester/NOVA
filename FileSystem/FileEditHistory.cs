using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Nova;

// One row per file overwrite edit_file has ever made - the content the
// file had *before* that specific edit, keyed by its path. Exists so an
// overwrite (which File.WriteAllText makes with no OS-level undo of its
// own) is actually recoverable: revert_file_edit reads a row back out and
// restores it, rather than "there's no backup" being the permanent state
// of things. A brand new file (nothing existed to lose) never gets a row -
// see FileTools.EditFile.
//
// Separate file from memory.db/conversation-archive.db/tools-registry.db,
// same reasoning as those staying apart from each other - a different
// shape (raw file content, not curated facts or task summaries) and a
// different, much higher potential row size (a whole file's text), no
// reason to share a table or slow any of them down.
internal sealed record FileEditRecord(int Id, string FilePath, string PreviousContent, string EditedAt);

internal static class FileEditHistory
{
    public static void Initialize(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS edits (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path TEXT NOT NULL,
                previous_content TEXT NOT NULL,
                edited_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public static void RecordEdit(string dbPath, string filePath, string previousContent)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO edits (file_path, previous_content, edited_at) VALUES ($path, $content, $now)";
        cmd.Parameters.AddWithValue("$path", NormalizePath(filePath));
        cmd.Parameters.AddWithValue("$content", previousContent);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    // Newest first - entry 0 is what "revert the last edit" reverts to.
    public static List<FileEditRecord> GetHistory(string dbPath, string filePath, int maxResults)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        // COLLATE NOCASE - Windows paths are case-insensitive, but SQLite's
        // default TEXT comparison isn't, so without this the same file
        // referenced with different casing across calls (or one call using
        // a relative path resolved from a different CWD - NormalizePath
        // below handles that half) would silently look like a different,
        // historyless file.
        cmd.CommandText = "SELECT id, file_path, previous_content, edited_at FROM edits WHERE file_path = $path COLLATE NOCASE ORDER BY id DESC LIMIT $max";
        cmd.Parameters.AddWithValue("$path", NormalizePath(filePath));
        cmd.Parameters.AddWithValue("$max", maxResults);
        using var reader = cmd.ExecuteReader();
        var results = new List<FileEditRecord>();
        while (reader.Read())
        {
            results.Add(new FileEditRecord(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return results;
    }

    // stepsBack=1 means "undo the most recent edit" (restore what the file
    // looked like right before it) - 2 means the edit before that, and so
    // on, covering "reverse part of the changes" for a file with several
    // edits in its history, not just the single most recent one. stepsBack
    // must be at least 1 - the caller (FileTools.RevertFileEdit) doesn't
    // validate its own steps_back input, so this guards directly rather
    // than trusting a positive value: stepsBack=0 would ask SQL for a
    // 0-row history and then still try to index into it (history[-1]),
    // and a negative stepsBack means "no LIMIT" to SQLite, returning every
    // row and then indexing negative anyway - both throw ArgumentOutOfRangeException
    // without this check.
    public static FileEditRecord? GetEditToRevertTo(string dbPath, string filePath, int stepsBack)
    {
        if (stepsBack < 1)
        {
            return null;
        }

        List<FileEditRecord> history = GetHistory(dbPath, filePath, stepsBack);
        return history.Count >= stepsBack ? history[stepsBack - 1] : null;
    }

    // Resolves relative paths (and different-but-equivalent forms like a
    // trailing separator) to one canonical absolute form before it's ever
    // used as the SQL key - paired with COLLATE NOCASE above, this is what
    // makes "the same file, referenced two different ways" actually match.
    private static string NormalizePath(string path) => Path.GetFullPath(path);
}
