using System.Text.RegularExpressions;

namespace Nova;

// Structural (code-level, not prompt-based) escalation check for run_command -
// same shape as NavigationalClickGuard, but a blocklist rather than an
// allowlist: run_command is deliberately general-purpose (unlike
// browser_click's narrow scope), so there's nothing to "allow" here, only a
// further Gate 2 review for calls that look like they could edit, delete,
// or download something, per the standing policy in ToolCatalog (read/
// lookup/create is free, edit/delete/download/run-something-that-can-do-
// those needs Gate 2, no exceptions for "but the implementation is actually
// safe" - see ToolCatalog.IsGate2).
//
// Text-matching a shell command is inherently imperfect - there's no way to
// know what an arbitrary .exe/.ps1/.py actually does short of running it,
// and a cleverly obfuscated command could still slip past. That's why the
// last category below (running an arbitrary script/executable) is
// deliberately broad rather than trying to enumerate every destructive
// interpreter flag. Note this classifier is now Gate 2's *only* backstop
// for a directly-requested command (a false negative here no longer also
// gets caught by a spoken Gate 1 description first, since Gate 1 only
// fires for ambient-initiated tasks now - see NovaAssistant's
// _taskIsAmbientInitiated) - a real, deliberate tradeoff, not an oversight,
// so keeping this list actually current matters more than it used to.
//
// Deliberately does NOT flag routine package-manager/build commands (npm
// install, pip install, dotnet build/restore, git pull/clone) even though
// they do fetch things over the network - none of them contain a literal
// "download a URL to a file" keyword (curl/wget/Invoke-WebRequest/-outfile/
// etc.), and treating "install"/"restore"/"pull" as download-shaped would
// make Nova's core coding-assistant workflow hit Gate 2 on nearly every
// build. This is a deliberate interpretive boundary, not an oversight -
// flagged to the user rather than assumed silently.
internal static class CommandRiskClassifier
{
    private static readonly Regex[] DeletePatterns =
    [
        new(@"\bdel\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\berase\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\brm\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\brd\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\brmdir\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bshred\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"remove-item", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"clear-content", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bformat\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"drop\s+(table|database)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"git\s+clean", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"git\s+reset\s+--hard", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"git\s+push\s+.*(--force|-f\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static readonly Regex[] WritePatterns =
    [
        // A bare '>' truncates/overwrites its target file; '>>' appends -
        // still a write, just less destructive, flagged the same way since
        // both replace file content the user didn't review first. Only the
        // lookbehind matters for excluding a stream-to-stream redirect like
        // `2>&1`/`1>&2` (preceded by the fd number) - an earlier version
        // also had a lookahead excluding '>' followed by a digit, meant for
        // the same purpose, but that actually excluded any real write whose
        // *target filename* happened to start with a digit (e.g.
        // `type x.txt>2024_report.docx`), which was never the intent.
        new(@"(?<!\d)>{1,2}", RegexOptions.Compiled),
        new(@"sed\s+-i", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"set-content", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"add-content", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"out-file", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"copy\s+/y", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"robocopy\b.*\s/mir\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bmv\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bmove\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static readonly Regex[] DownloadPatterns =
    [
        new(@"\bcurl\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bwget\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"invoke-webrequest", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\biwr\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"invoke-restmethod", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bbitsadmin\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"certutil\b.*-urlcache", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"-outfile\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"start-bitstransfer", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bftp\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bscp\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    // Running an arbitrary script/executable directly (not a shell builtin)
    // is "anything that can do edit, delete, download" by definition - Nova
    // has no way to know what's actually inside it, so any direct
    // invocation of one of these extensions is treated the same as if it
    // were already known to be destructive. Also covers the well-known "run
    // this arbitrary string as code" primitives - including handing inline
    // code straight to an interpreter's eval-a-string flag (python -c,
    // node -e, powershell -Command, etc.), which is exactly as unknowable
    // as a script file (e.g. `python -c "import shutil; shutil.rmtree(...)"`
    // is just as destructive as `rm -rf` but doesn't match any Delete
    // pattern, since those only look for actual delete *commands*, not
    // arbitrary code that happens to delete something).
    private static readonly Regex[] RunUnknownPatterns =
    [
        new(@"\S+\.(exe|bat|cmd|ps1|sh|py)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"invoke-expression", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\biex\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"-encodedcommand", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(python3?|node|ruby|perl)\b[^|;&\n]*\s-{1,2}(c|e|eval)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(powershell|pwsh)\b[^|;&\n]*-command\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public static bool IsDestructive(string command) =>
        Matches(DeletePatterns, command) || Matches(WritePatterns, command) ||
        Matches(DownloadPatterns, command) || Matches(RunUnknownPatterns, command);

    private static bool Matches(Regex[] patterns, string command)
    {
        foreach (Regex pattern in patterns)
        {
            if (pattern.IsMatch(command))
            {
                return true;
            }
        }

        return false;
    }
}
