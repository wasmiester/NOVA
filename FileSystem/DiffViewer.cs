using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Nova;

// Shows a real diff for a pending edit_file call before it's authorized -
// this is the Gate 1 review (what will change); FileEditHistory is the
// separate safety net for after the fact (what it can be reverted back
// to, via revert_file_edit) - the two aren't redundant, one is prevention,
// the other is recovery. Prefers VS Code's native diff view (`code
// --diff`, same side-by-side +/- view as git) over a plain-text console
// dump, falling back to the console only if the `code` CLI isn't
// available.
internal static class DiffViewer
{
    public static bool ShowEditDiff(IReadOnlyDictionary<string, JsonElement> input)
    {
        // Expanded the same way FileTools.EditFile/ToolCatalog.IsFree now
        // both expand it - otherwise a %VAR%-style path would look
        // not-found here even when the real, expanded path already
        // exists, showing a "new file" diff for what's actually an
        // overwrite.
        string path = Environment.ExpandEnvironmentVariables(ToolInput.GetString(input, "path") ?? "(unknown path)");
        string newContent = ToolInput.GetString(input, "content") ?? "";
        string oldContent = File.Exists(path) ? File.ReadAllText(path) : "";

        // Left/right sides of the diff live as temp files that VS Code reads
        // asynchronously after `code` returns, so they're deliberately not
        // cleaned up here - a couple of leftover temp files is harmless.
        string oldFileForDiff = path;
        if (!File.Exists(path))
        {
            oldFileForDiff = Path.Combine(Path.GetTempPath(), $"nova-diff-new-file{Path.GetExtension(path)}");
            File.WriteAllText(oldFileForDiff, "");
        }

        string proposedFile = Path.Combine(Path.GetTempPath(), $"nova-diff-proposed-{Path.GetFileName(path)}");
        File.WriteAllText(proposedFile, newContent);

        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c code --diff \"{oldFileForDiff}\" \"{proposedFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            Console.WriteLine($"\n--- proposed changes to {path} (couldn't open VS Code's diff view - is 'code' on PATH?) ---");
            Console.Write(BuildLineDiff(oldContent, newContent));
            Console.WriteLine("--- end of changes ---");
            return false;
        }
    }

    private static string BuildLineDiff(string oldContent, string newContent)
    {
        string[] oldLines = oldContent.Length == 0 ? [] : oldContent.Replace("\r\n", "\n").Split('\n');
        string[] newLines = newContent.Length == 0 ? [] : newContent.Replace("\r\n", "\n").Split('\n');

        int n = oldLines.Length;
        int m = newLines.Length;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = oldLines[i] == newLines[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var sb = new StringBuilder();
        int a = 0;
        int b = 0;
        while (a < n && b < m)
        {
            if (oldLines[a] == newLines[b])
            {
                sb.Append("  ").AppendLine(oldLines[a]);
                a++;
                b++;
            }
            else if (lcs[a + 1, b] >= lcs[a, b + 1])
            {
                sb.Append("- ").AppendLine(oldLines[a]);
                a++;
            }
            else
            {
                sb.Append("+ ").AppendLine(newLines[b]);
                b++;
            }
        }

        while (a < n)
        {
            sb.Append("- ").AppendLine(oldLines[a]);
            a++;
        }

        while (b < m)
        {
            sb.Append("+ ").AppendLine(newLines[b]);
            b++;
        }

        return sb.ToString();
    }
}
