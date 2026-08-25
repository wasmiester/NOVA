using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Nova;

internal static class FileTools
{
    // Deliberately not run_command + dir/ls: this only ever enumerates
    // filesystem entries via .NET's own APIs, with no shell involved at
    // all - no injection surface, no chaining, no way to accidentally
    // delete or modify anything, so it's safe to make free the same way
    // read_file already is. The recurring friction this fixes: "find my
    // resume in Downloads" kept needing a fresh Gate 1 authorization every
    // single time via run_command, even though listing files is pure
    // reading - the same reasoning that already makes read_file/browser_read
    // free, just previously unavailable without going through the shell.
    public static string ListFiles(IReadOnlyDictionary<string, JsonElement> input)
    {
        // %USERPROFILE%-style references only expand under an actual shell
        // (cmd/PowerShell) - Directory.Exists takes the string completely
        // literally, so without this a path like "%USERPROFILE%\Downloads"
        // would look for a folder literally named "%USERPROFILE%" and
        // always come back not-found.
        string path = ExpandPath(input["path"].GetString()!);
        string pattern = ToolInput.GetString(input, "pattern") ?? "*";
        bool recursive = ToolInput.GetBool(input, "recursive") ?? false;

        if (!Directory.Exists(path))
        {
            return $"Folder not found: {path}";
        }

        const int MaxResults = 100;
        List<string> results;
        try
        {
            results = Directory
                .EnumerateFileSystemEntries(path, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(MaxResults)
                .Select(p => Directory.Exists(p) ? $"{p}\\ (folder)" : $"{p} - {File.GetLastWriteTime(p):yyyy-MM-dd HH:mm}")
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // UnauthorizedAccessException doesn't inherit from IOException -
            // a recursive listing commonly hits at least one permission-
            // denied subfolder (System Volume Information and the like),
            // and that shouldn't fail the whole listing.
            return $"Couldn't list \"{path}\": {ex.Message}";
        }

        return results.Count == 0
            ? $"No files matching \"{pattern}\" found in {path}."
            : $"{results.Count} result(s) in {path}, most recently modified first:\n" + string.Join("\n", results);
    }

    public static string ReadFile(IReadOnlyDictionary<string, JsonElement> input)
    {
        string path = ExpandPath(input["path"].GetString()!);
        if (!File.Exists(path))
        {
            return $"File not found: {path}";
        }

        const int MaxChars = 8000;
        string content = File.ReadAllText(path);
        return content.Length > MaxChars
            ? content[..MaxChars] + $"\n... [truncated, {content.Length - MaxChars} more characters]"
            : content;
    }

    // Creating a brand-new file is free (see ToolCatalog.IsFree) - nothing
    // existed to lose, so there's nothing to record. Overwriting an
    // *existing* one is Gate 2 and always records what the file looked
    // like immediately before, via FileEditHistory - see revert_file_edit
    // for how that gets used to actually undo it.
    public static string EditFile(string editHistoryDbPath, IReadOnlyDictionary<string, JsonElement> input)
    {
        string path = ExpandPath(input["path"].GetString()!);
        string content = input["content"].GetString()!;
        // WriteAllText creates the file itself but not missing parent
        // folders - without this, creating a brand new file (a generated
        // cover letter, say) in a folder that doesn't exist yet would
        // throw instead of just creating it, the way a person saving a
        // new file would expect.
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            FileEditHistory.RecordEdit(editHistoryDbPath, path, File.ReadAllText(path));
        }

        File.WriteAllText(path, content);
        return $"Wrote {content.Length} characters to {path}.";
    }

    // Gate 2, same as the overwrite it's undoing - restoring old content
    // is still replacing whatever's there *right now* with something
    // else, which could clobber a more recent legitimate change (the user
    // editing the file themselves after Nova's edit) just as easily as a
    // fresh edit could. Recorded as its own history entry too (the
    // content being replaced - i.e. the version being reverted *away*
    // from - gets saved the same way any other overwrite's previous
    // content would), so reverting is itself undoable, not a one-way trip
    // out of history.
    public static string RevertFileEdit(string editHistoryDbPath, IReadOnlyDictionary<string, JsonElement> input)
    {
        string path = ExpandPath(input["path"].GetString()!);
        int stepsBack = ToolInput.GetInt(input, "steps_back") ?? 1;

        FileEditRecord? target = FileEditHistory.GetEditToRevertTo(editHistoryDbPath, path, stepsBack);
        if (target is null)
        {
            return $"No edit history found for {path} going back {stepsBack} step(s) - use list_file_edits to see what's actually available.";
        }

        if (File.Exists(path))
        {
            FileEditHistory.RecordEdit(editHistoryDbPath, path, File.ReadAllText(path));
        }

        File.WriteAllText(path, target.PreviousContent);
        return $"Reverted {path} to how it looked before the edit from {target.EditedAt} ({stepsBack} step(s) back).";
    }

    public static string ListFileEdits(string editHistoryDbPath, IReadOnlyDictionary<string, JsonElement> input)
    {
        string path = ExpandPath(input["path"].GetString()!);
        List<FileEditRecord> history = FileEditHistory.GetHistory(editHistoryDbPath, path, 10);
        if (history.Count == 0)
        {
            return $"No recorded edit history for {path}.";
        }

        var lines = history.Select((entry, index) => $"{index + 1} step(s) back: edited at {entry.EditedAt}, {entry.PreviousContent.Length} characters before that edit");
        return $"Edit history for {path} (most recent first):\n" + string.Join("\n", lines);
    }

    // Gate 2 (see ToolCatalog.Gate2Tools) regardless of the Recycle-Bin
    // routing below - the roadmap's own standing policy is that delete
    // requires Gate 2 "regardless of implementation," since a
    // theoretically-restorable delete is still a much weaker claim than
    // "nothing happened," and mixing that judgment call into the tier
    // itself would make the review depend on implementation details the
    // user can't see. Routed through the Recycle Bin anyway (not
    // File.Delete/Directory.Delete) purely for real safety, independent of
    // gating - genuinely undoable via the OS's own UI is a strictly better
    // outcome than permanent, and costs nothing extra to provide. Handles
    // both a file and a folder path under one name since "delete this" is
    // one request either way - the caller doesn't need to know or say
    // which kind of thing they're pointing at.
    public static string DeletePath(IReadOnlyDictionary<string, JsonElement> input)
    {
        string path = ExpandPath(input["path"].GetString()!);

        if (File.Exists(path))
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Couldn't delete \"{path}\": {ex.Message}";
            }

            return $"Deleted {path} (sent to the Recycle Bin - it can still be restored from there).";
        }

        if (Directory.Exists(path))
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Couldn't delete \"{path}\": {ex.Message}";
            }

            return $"Deleted folder {path} and everything in it (sent to the Recycle Bin - it can still be restored from there).";
        }

        return $"Not found: {path}";
    }

    // %VAR%-style references only expand under an actual shell (cmd/
    // PowerShell) - .NET's own File/Directory APIs take a path completely
    // literally, so without this a model-supplied path like
    // "%USERPROFILE%\Desktop\notes.txt" would be treated as a folder
    // literally named "%USERPROFILE%" rather than the user's real Desktop.
    // Previously only ListFiles did this expansion - every other file tool
    // took its path literally, which could silently read/write/revert the
    // wrong location (or report "not found" for a file that does exist)
    // whenever a %VAR%-style path was used anywhere but list_files.
    // Internal, not private - shared with ToolDescriptions.DescribeEditFile,
    // which needs the exact same expansion before its own File.Exists check
    // (see that method's own comment for the bug this fixes).
    internal static string ExpandPath(string path) => Environment.ExpandEnvironmentVariables(path);
}
