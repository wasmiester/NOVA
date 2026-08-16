using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Nova;

// Opens a file, folder, URL, or application via the OS's default handler -
// the voice equivalent of double-clicking it. Free by default (see
// ToolCatalog.IsFree) since opening something has no side effects on its
// own; run_as_admin is the one exception, since an elevated process can
// genuinely change system state a normal one couldn't.
internal static class ShellOpener
{
    public static string Open(IReadOnlyDictionary<string, JsonElement> input)
    {
        string? path = ToolInput.GetString(input, "path");
        bool asAdmin = ToolInput.GetBool(input, "run_as_admin") ?? false;

        var psi = new ProcessStartInfo
        {
            // No path - "open File Explorer" with nothing more specific in
            // mind - matches explorer.exe's own default (no-args) behavior.
            FileName = string.IsNullOrWhiteSpace(path) ? "explorer.exe" : path,
            UseShellExecute = true, // required for shell verbs (open/runas) rather than launching path as a literal executable
        };

        if (asAdmin)
        {
            // Triggers Windows' own UAC prompt - a second, OS-level confirmation
            // beyond Nova's own Gate 1, which Nova has no way to skip or see past.
            psi.Verb = "runas";
        }

        // Opened from Nova's own background process, an Explorer window would
        // otherwise just flash in the taskbar instead of actually showing -
        // bring it to the front and maximize it. Scoped to Explorer
        // specifically (no path, or a folder - both open via explorer.exe),
        // not every arbitrary app/file open_path can launch. Must start
        // watching *before* Process.Start - a new folder window usually
        // opens inside the already-running shell explorer.exe process
        // rather than a new one, so this snapshots existing windows first
        // and looks for whichever new one appears, not a new process.
        bool opensExplorer = string.IsNullOrWhiteSpace(path) || Directory.Exists(path);
        if (opensExplorer)
        {
            WindowActivator.WatchAndActivateNewWindow("explorer");
        }

        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            return $"Couldn't open \"{path ?? "File Explorer"}\": {ex.Message}";
        }

        return string.IsNullOrWhiteSpace(path) ? "Opened File Explorer." : $"Opened \"{path}\".";
    }
}
