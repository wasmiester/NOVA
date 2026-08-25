using System.Diagnostics;
using System.IO;

namespace Nova;

// Launches TerminalRelay/ as a separate process - a real, separate console
// window the user can type into, whose output also streams back to
// TerminalWatcher over a named pipe. Kept as a standalone launcher (rather
// than folded into NovaAssistant) since it needs repoRoot, which is
// composition-root/Program.cs territory, not something NovaAssistant
// should need to know about.
internal static class WatchedTerminalLauncher
{
    public static string Open(string repoRoot)
    {
        // Matches whichever configuration Nova.Core itself was built in
        // (checked at compile time, not runtime) - a hardcoded "Debug" here
        // would silently fail to find the relay the moment Nova is ever
        // built/run as Release, even with a Release build of TerminalRelay
        // sitting right there.
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        string relayExePath = Path.Combine(repoRoot, "TerminalRelay", "bin", Configuration, "net10.0", "NovaTerminalRelay.exe");
        if (!File.Exists(relayExePath))
        {
            return "Couldn't find the terminal relay executable - it needs building first " +
                   "(dotnet build in the TerminalRelay/ folder).";
        }

        var psi = new ProcessStartInfo(relayExePath, TerminalWatcher.PipeName)
        {
            UseShellExecute = false,
        };
        Process.Start(psi);
        return "Opened a new terminal window that I'm watching (plain text only - no colors or " +
               "interactive tools like vim). Build, test, or git output there may prompt me to say " +
               "something if it looks worth mentioning, while I'm engaged.";
    }
}
