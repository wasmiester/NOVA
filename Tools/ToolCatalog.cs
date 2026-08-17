using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Messages;

namespace Nova;

// Static tool schema catalog - what Claude can call, which of those calls
// are free, and which need a further Gate 2 review (see NovaAssistant for
// how FreeTools/Gate2Tools are actually enforced). This file only encodes
// the tier a tool belongs to (free/Gate 1/Gate 2), not when Gate 1 itself
// actually asks - that now depends on the task's origin, not the tool's
// tier, see NovaAssistant's _taskIsAmbientInitiated.
internal static class ToolCatalog
{
    // Standing policy: reading and opening never require Gate 1, period -
    // read_file/read_screen/browser_read (sensing), browser_navigate/
    // scroll_desktop/open_path (opening/navigating, no side effects),
    // search_email/list_calendar_events (reading Google data), plus memory
    // (local reversible bookkeeping) and browser_fill/browser_select/
    // browser_check/browser_upload (explicit user request to loosen these -
    // still just filling in form data for the user to review, not
    // clicking/submitting anything). Anything that actually changes state
    // outside Nova's own bookkeeping - editing a file, running a command,
    // clicking/typing in a desktop app, sending mail, adding a calendar
    // event - stays gated, since "reading/opening" is specifically about
    // consuming information, not causing effects.
    public static readonly HashSet<string> FreeTools =
        [
            "read_file", "list_files", "save_memory", "search_memory", "search_conversation_history", "read_screen", "browser_read",
            "browser_navigate", "browser_fill", "browser_select", "browser_check", "browser_upload",
            "scroll_desktop", "open_path", "search_email", "read_email", "list_calendar_events", "open_watched_terminal", "list_tools",
            "read_doc", "search_drive", "list_file_edits", "read_sheet", "read_slides",
            "get_settings", "update_setting", "read_recent_errors",
        ];

    // Tools that are irreversible, leave the machine, or replace existing
    // content with no OS-level undo (sending, submitting, posting,
    // overwriting a file that already existed) - these need a Gate 2
    // content review (see NovaAssistant) immediately before they execute,
    // every time, regardless of whether Gate 1 fired for this task at all.
    // send_email is the "leaves the machine" case: exactly the "fix the
    // typos and send it" example the roadmap describes - drafting it just
    // runs (a direct request needs no separate ask of its own anymore),
    // Gate 2 covers the actual send, with the real drafted content shown,
    // not just the task-level intent. edit_file (when overwriting - see
    // IsFree below) and revert_file_edit are the "no undo" case - both
    // replace whatever a file currently contains, and a misheard "yes"
    // clobbering an existing file is exactly the risk Gate 2's click-only
    // confirmation exists to close, the same reasoning already applied to
    // send_email. run_command is unconditionally Gate 2 too (every call,
    // not just ones CommandRiskClassifier flags as destructive) - once
    // Gate 1 stopped firing for a direct request, a command
    // CommandRiskClassifier's own text-matching heuristics missed would
    // otherwise run with no review of any kind at all; run_command's
    // surface (arbitrary shell text) is too open-ended to trust a
    // heuristic as the sole gate the way browser_click's structural
    // allowlist (see NavigationalClickGuard) is judged safe enough to
    // stay at Gate 1 - CommandRiskClassifier's own categorization is still
    // used (see ToolDescriptions.Gate2Parts), just to make the review text
    // itself say *why* a command looks risky, not to decide whether a
    // review happens.
    public static readonly HashSet<string> Gate2Tools = ["send_email", "revert_file_edit", "run_command", "delete_path"];

    // Two free tools have a per-call exception rather than being a plain
    // name lookup against FreeTools:
    //   - open_path: opening something normally has no side effects, but
    //     opening it *elevated* (run_as_admin) can genuinely change system
    //     state a normal process couldn't, so that specific case is Gate 2
    //     (see IsGate2 below) - Windows' own UAC prompt fires for it too,
    //     but UAC only asks "allow this app to make changes," never says
    //     *what* those changes are, so it's not a substitute for Nova's own
    //     accurate description of the action, the same reasoning that put
    //     run_command at unconditional Gate 2.
    //   - edit_file: creating a brand-new file is free (nothing existed to
    //     lose - see the classification rubric in the roadmap, tier 2) but
    //     overwriting one that already exists needs Gate 2, not just Gate
    //     1 (see Gate2Tools above and IsGate2 below) - there's no OS-level
    //     undo for File.WriteAllText, only FileEditHistory's own record of
    //     what was there before.
    public static bool IsFree(PendingToolCall call)
    {
        if (call.Name == "open_path")
        {
            return !(ToolInput.GetBool(call.Input, "run_as_admin") ?? false);
        }

        if (call.Name == "edit_file")
        {
            // Expanded the same way FileTools.EditFile itself now expands
            // its path before touching the filesystem - without this, a
            // path like "%USERPROFILE%\Desktop\notes.txt" would look
            // not-found here (literally, there's no folder named
            // "%USERPROFILE%") even when the real, expanded path already
            // exists, wrongly classifying a genuine overwrite as a free
            // new-file creation and skipping Gate 2 entirely.
            string? path = ToolInput.GetString(call.Input, "path");
            return path is not null && !File.Exists(Environment.ExpandEnvironmentVariables(path));
        }

        return FreeTools.Contains(call.Name);
    }

    // Gate2Tools above covers calls that are Gate 2 by *name* alone
    // (send_email, revert_file_edit, run_command - always). edit_file and
    // open_path are the two exceptions, each conditional on their own
    // input - can't live in a plain HashSet the same way; NovaAssistant's
    // own gate2Calls filter checks this method in addition to Gate2Tools.
    public static bool IsGate2(PendingToolCall call)
    {
        if (Gate2Tools.Contains(call.Name))
        {
            return true;
        }

        if (call.Name == "edit_file")
        {
            string? path = ToolInput.GetString(call.Input, "path");
            return path is not null && File.Exists(Environment.ExpandEnvironmentVariables(path));
        }

        if (call.Name == "open_path")
        {
            return ToolInput.GetBool(call.Input, "run_as_admin") ?? false;
        }

        return false;
    }

    public static readonly List<ToolUnion> Tools = BuildToolList();

    private static List<ToolUnion> BuildToolList()
    {
        Tool readFileTool = BuildTool(
            "read_file",
            "Reads and returns the contents of a local text file. Read-only and always allowed - use it freely, including to look at a file before editing it.",
            new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "Absolute or relative path to the file to read." } },
                required = new[] { "path" },
            });

        Tool listFilesTool = BuildTool(
            "list_files",
            "Lists files/folders in a local directory, most recently modified first. Free to use without " +
            "asking - pure reading via .NET's own file APIs, no shell involved, same trust level as " +
            "read_file. Use this instead of run_command with dir/ls/Get-ChildItem for finding a file (a " +
            "resume, a downloaded document, etc.) - it does the same job without needing an authorization " +
            "ask each time, which a shell command always would.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Folder to list, e.g. \"%USERPROFILE%\\\\Downloads\" or an absolute path." },
                    pattern = new { type = "string", description = "Wildcard filter, e.g. \"*resume*\" or \"*.pdf\". Defaults to everything." },
                    recursive = new { type = "boolean", description = "Search subfolders too. Defaults to false." },
                },
                required = new[] { "path" },
            });

        Tool editFileTool = BuildTool(
            "edit_file",
            "Writes a local file with new content. Creating a brand-new file (the path doesn't exist yet) is " +
            "free - nothing existed to lose. Overwriting a file that already exists requires user " +
            "authorization *and* a Gate 2 review before it actually writes, since there's no OS-level undo " +
            "for this - the previous content is saved automatically (see revert_file_edit/list_file_edits) " +
            "so a bad overwrite is recoverable, but the write itself still needs the same click-only " +
            "confirmation as an irreversible action, not just a spoken yes.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Absolute or relative path to the file to write." },
                    content = new { type = "string", description = "The full new contents of the file." },
                },
                required = new[] { "path", "content" },
            });

        Tool revertFileEditTool = BuildTool(
            "revert_file_edit",
            "Restores a file to how it looked before one of edit_file's own past overwrites, using the " +
            "history edit_file automatically records. steps_back=1 (the default) undoes the most recent " +
            "overwrite; a higher number reaches further back for a file with several edits in its history - " +
            "check list_file_edits first if you're not sure how many steps back the version the user wants " +
            "actually is. Always requires user authorization and a Gate 2 review, the same as edit_file's " +
            "own overwrites - restoring old content is still replacing whatever's there right now.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "The file to revert." },
                    steps_back = new { type = "number", description = "How many of edit_file's past overwrites to undo, counting back from the most recent. Defaults to 1." },
                },
                required = new[] { "path" },
            });

        Tool listFileEditsTool = BuildTool(
            "list_file_edits",
            "Lists edit_file's recorded overwrite history for a file (when each one happened) - check this " +
            "before revert_file_edit if you're not sure how many steps back covers what the user actually " +
            "wants undone. Free to use without asking - reads Nova's own local edit history, not the file " +
            "itself.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "The file whose edit history to list." },
                },
                required = new[] { "path" },
            });

        Tool deletePathTool = BuildTool(
            "delete_path",
            "Deletes a file or folder (works for either - no need to know which it is first). Always " +
            "requires user authorization and a Gate 2 review, same tier as sending an email, regardless " +
            "of the fact that it's sent to the Recycle Bin rather than permanently erased (a real safety " +
            "net, but not something to treat as making this casual - a folder's entire contents go with " +
            "it). Mention that it's recoverable from the Recycle Bin if it seems useful context, but " +
            "still ask like it matters.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "The file or folder to delete." },
                },
                required = new[] { "path" },
            });

        Tool runCommandTool = BuildTool(
            "run_command",
            "Runs a shell command on the user's machine and returns its output. Requires user authorization, " +
            "and every call - even a plain read-only one like \"dir\" - additionally goes through a Gate 2 " +
            "review of the exact command text immediately before it runs, the same as an overwriting " +
            "edit_file call. The authorization ask itself is spoken using `description`, never the raw " +
            "command - the user doesn't want to hear shell syntax read aloud, just what it accomplishes. " +
            "The actual command is still printed to the console for reference.",
            new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", description = "The command to run, exactly as typed into a terminal." },
                    description = new { type = "string", description = "A short, natural-language description of what this command does, e.g. \"list the files in this folder\" - this is what gets spoken to the user, never the raw command." },
                },
                required = new[] { "command", "description" },
            });

        Tool saveMemoryTool = BuildTool(
            "save_memory",
            "Saves a durable fact to persistent memory that carries across separate runs of this program - " +
            "unlike the rest of this conversation, which is forgotten on restart. Free to use without asking. " +
            "A save that looks like the same fact as an existing memory, just restated, automatically " +
            "strengthens/updates that memory instead of creating a duplicate - no need to check first.",
            new
            {
                type = "object",
                properties = new
                {
                    content = new { type = "string", description = "The fact or note to remember, written clearly enough to make sense on its own later." },
                    tags = new { type = "string", description = "Optional comma-separated tags to help find this later, e.g. \"preferences, project-nova\". Include a scope tag so later searches can tell durable facts from ones scoped to a single task: \"durable\" for anything that should apply broadly/forever (facts about the user's life/work - who they are, what they're building), \"style\" for how the user wants to be treated or collaborated with specifically (communication tone, workflow preferences, corrections about how you should behave - different from a fact about them, the same way a person tracks \"how to work with someone\" separately from \"what I know about them\"), or \"task:<short-name>\" (e.g. \"task:motorola-application\") for something only true within one specific task, like a number or choice made for that task alone - not something to reuse elsewhere." },
                },
                required = new[] { "content" },
            });

        Tool searchMemoryTool = BuildTool(
            "search_memory",
            "Searches previously saved persistent memories - matches exact keywords/tags as well as things " +
            "phrased differently but semantically related. Free to use without asking.",
            new
            {
                type = "object",
                properties = new { query = new { type = "string", description = "Keywords to search for in saved memories." } },
                required = new[] { "query" },
            });

        Tool searchConversationHistoryTool = BuildTool(
            "search_conversation_history",
            "Searches past *completed* tasks from earlier in this session (and previous runs - this persists " +
            "across restarts) - matches exact keywords as well as things phrased differently but semantically " +
            "related. The current, still-in-progress task is always already in context and doesn't need this. " +
            "Use it when the user references something earlier that isn't in the current conversation (\"what " +
            "was that email you sent this morning\", \"did I ask you to look into X\") - each completed task " +
            "carries forward as only a short summary, not its full detail, so this is how to actually recover " +
            "specifics from one. Free to use without asking.",
            new
            {
                type = "object",
                properties = new { query = new { type = "string", description = "Keywords to search for in past tasks." } },
                required = new[] { "query" },
            });

        Tool readScreenTool = BuildTool(
            "read_screen",
            "Reads a window via Windows UI Automation - its title plus the text of its visible controls " +
            "(buttons, labels, menus, fields). Read-only and always allowed. Best-effort: how much real " +
            "content comes through depends on how well the app exposes its UI to accessibility tools - " +
            "native controls read well, but custom-rendered content (e.g. a code editor's own text) may not. " +
            "With no window_title, reads the current foreground window - use this for whatever the user is " +
            "obviously looking at (\"summarize this document\"). With window_title, reads that window " +
            "instead, wherever it is - including minimized or behind other windows - without touching what's " +
            "currently on screen, e.g. checking a minimized window for a background lookup that isn't about " +
            "the user's own attention. An unmatched or ambiguous window_title returns the list of open window " +
            "titles instead of content, so a follow-up call can be more specific. Useful fallback for a Chrome " +
            "tab too, not just desktop apps - if browser_read comes back empty or missing the actual content " +
            "on a canvas-rendered page (Google Docs is the common one), this reads via a completely different " +
            "path (OS accessibility, not the DOM) and can succeed where browser_read can't.",
            new
            {
                type = "object",
                properties = new
                {
                    window_title = new { type = "string", description = "Optional substring to match against open window titles - omit to read the current foreground window instead." },
                },
                required = Array.Empty<string>(),
            });

        Tool scrollDesktopTool = BuildTool(
            "scroll_desktop",
            "Scrolls the current foreground window - a real, physical scroll wheel action on whatever desktop " +
            "app is focused. Has NO effect on a Chrome tab even when Chrome is the foreground window - a " +
            "browser tab's content is read/filled through the browser_* tools instead, which already see the " +
            "whole page regardless of scroll position, so there's never a reason to call this to \"see more\" " +
            "of a web page. If browser_read seems to be missing content, the actual fix is reading again or " +
            "checking embedded frames, not scrolling. Free to use without asking, since it's pure navigation " +
            "with no side effects.",
            new
            {
                type = "object",
                properties = new
                {
                    direction = new { type = "string", description = "\"up\", \"down\", \"left\", or \"right\"." },
                    amount = new { type = "number", description = "How much to scroll, in wheel-notch units. Defaults to 3." },
                },
                required = new[] { "direction" },
            });

        Tool interactDesktopTool = BuildTool(
            "interact_desktop",
            "Clicks or types into a control (by its visible label/name) in a window - not the browser, which " +
            "has its own separate browser_click tool. Clicking here is scoped exactly the same narrow way as " +
            "browser_click: only navigational clicks (pagination, expand/collapse, show more/load more, " +
            "close/dismiss, add another entry) are actually allowed through, enforced structurally, not just " +
            "by asking nicely - anything else (Send, Delete, Submit, Confirm, etc.) is refused outright " +
            "regardless of how it's asked for. Typing isn't restricted the same way. With no window_title, " +
            "acts on the current foreground window. With window_title, acts on that window instead, even if " +
            "it's minimized or behind other windows - if the control supports direct UI Automation (no visual " +
            "change happens at all), otherwise it has to come to the foreground first since simulated clicks/" +
            "keystrokes need real on-screen focus; only bring a window forward like that when doing so " +
            "actually fits what's being asked, not as a background side effect of an unrelated task.",
            new
            {
                type = "object",
                properties = new
                {
                    label = new { type = "string", description = "The visible name/label of the control to act on, e.g. \"Save\" or \"Search box\"." },
                    action = new { type = "string", description = "\"click\" or \"type\". Defaults to \"click\"." },
                    value = new { type = "string", description = "Text to type - only used when action is \"type\"." },
                    window_title = new { type = "string", description = "Optional substring to match against open window titles - omit to act on the current foreground window instead." },
                },
                required = new[] { "label" },
            });

        Tool openPathTool = BuildTool(
            "open_path",
            "Opens a file, folder, or application via the OS's default handler - the voice equivalent of " +
            "double-clicking it (e.g. opening File Explorer, a document, or launching a program). Free to " +
            "use without asking, since opening something has no side effects on its own - the one exception " +
            "is run_as_admin, which needs a Gate 2 review before it runs since an elevated process can " +
            "genuinely change system state a normal one couldn't, and Windows' own UAC prompt (which still " +
            "shows separately, in addition to this) never actually says what the elevated action will do.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File, folder, URL, or application path to open. Omit to just open File Explorer with no specific location." },
                    run_as_admin = new { type = "boolean", description = "Open elevated (as administrator). Defaults to false. Requires authorization when true." },
                },
                required = Array.Empty<string>(),
            });

        Tool openWatchedTerminalTool = BuildTool(
            "open_watched_terminal",
            "Opens a new, separate terminal window that streams commands to a real cmd.exe shell - the " +
            "user types into it normally. Plain text only: no colors, no interactive full-screen tools " +
            "(vim, an interactive rebase, etc.) - fine for build/test/git output, not a general-purpose " +
            "terminal replacement, so mention that if it's relevant. Free to use without asking. While " +
            "engaged (awake), its output is watched the same way file changes are - a build finishing or " +
            "a test failing there may prompt a brief proactive comment later; while asleep it's just a " +
            "plain terminal window.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() });

        Tool browserNavigateTool = BuildTool(
            "browser_navigate",
            "Navigates a browser tab to a URL. Acts on the user's real Chrome - if no debug-mode Chrome is " +
            "currently reachable, this launches one automatically (a separate, dedicated profile - never the " +
            "user's regular Chrome window), so there's no setup needed first. A new task's *first* call " +
            "automatically opens a fresh tab rather than navigating away from whatever tab is currently " +
            "selected (see browser_read) - it might still have something the user needs from an earlier, " +
            "unrelated task (a half-filled form, a page they're reading). Later calls within the same task " +
            "go back to reusing/switching tabs normally, since that's legitimate multi-step browsing within " +
            "one task, not a fresh unrelated one. If the URL is already open in another tab, switches to " +
            "that tab instead of opening a duplicate and says so. force_new always overrides this default " +
            "either way - pass it explicitly (true to always get a new tab even mid-task, false to " +
            "explicitly reuse the current tab even as a task's first call, e.g. if the user says \"check my " +
            "current tab\"). Free to use without asking - opening a page isn't consequential on its own.",
            new
            {
                type = "object",
                properties = new
                {
                    url = new { type = "string", description = "The URL to navigate to." },
                    force_new = new { type = "boolean", description = "Explicitly force (true) or suppress (false) opening a fresh tab, overriding the automatic new-task default. Omit to use that default." },
                },
                required = new[] { "url" },
            });

        Tool browserReadTool = BuildTool(
            "browser_read",
            "Reads a tab in the user's real Chrome: title, URL, a structural snapshot of visible text, fields " +
            "(with current values), and buttons, plus a separate \"Fillable fields\" list giving every field a " +
            "short ref (f1, f2, ...) - pass one of those to browser_fill's field_ref parameter whenever a label " +
            "appears more than once (e.g. every entry in a repeated form section has its own \"Month\"/\"Year\") " +
            "to target the exact one intended; a fresh browser_read invalidates all previous refs. If tab_hint " +
            "is omitted and more than one tab is open, returns a list of open tabs (title + URL) instead of " +
            "page content - call again with tab_hint (or ask the user which one they mean) to actually select " +
            "and read one. Once a tab is selected, it stays selected for later browser_read/browser_fill calls " +
            "until a different one is chosen. Already returns the whole page's content regardless of scroll " +
            "position - scroll_desktop has no effect on a browser tab and is never needed here, even for a " +
            "long page. Some pages (Google Docs is the common one) render their actual content as a canvas " +
            "rather than real DOM text, so this comes back empty or missing the document body even though " +
            "the page clearly has content - if that happens, try read_screen instead of assuming the " +
            "content just isn't accessible: it reads via Windows' own accessibility APIs rather than the " +
            "DOM, which can reach text a canvas-rendering page hides from this tool (confirmed working on a " +
            "real Google Doc). Read-only and always allowed.",
            new
            {
                type = "object",
                properties = new
                {
                    tab_hint = new { type = "string", description = "Text to match against open tabs' titles/URLs, e.g. \"stripe\" or \"job application\". Omit to use the already-selected tab, or to list all tabs if none is selected yet." },
                },
                required = Array.Empty<string>(),
            });

        Tool browserFillTool = BuildTool(
            "browser_fill",
            "Fills one form field on the currently selected tab (see browser_read) in the user's real Chrome. " +
            "Free to use without asking. When the field's label is unique on the page, label alone is enough. " +
            "When it isn't - e.g. a form with several entries, each with its own \"Month\"/\"Year\" fields - " +
            "label alone always targets the *first* match and will silently overwrite the wrong entry; pass " +
            "the ref for that exact field from browser_read's \"Fillable fields\" list instead. There is a " +
            "separate browser_click tool, but it's narrowly scoped to navigational clicks only (pagination, " +
            "expand/collapse, etc.) and structurally refuses anything submit-shaped - Nova can never submit " +
            "anything on the user's behalf, only fill fields in for the user to review and submit themselves.",
            new
            {
                type = "object",
                properties = new
                {
                    label = new { type = "string", description = "The visible label text of the field to fill, e.g. \"First Name\"." },
                    value = new { type = "string", description = "The value to type into the field." },
                    field_ref = new { type = "string", description = "The exact field from browser_read's \"Fillable fields\" list (e.g. \"f3\"). Required whenever the label repeats on the page - omitting it in that case risks overwriting the wrong occurrence. Optional when the label is unique." },
                },
                required = new[] { "label", "value" },
            });

        Tool browserSelectTool = BuildTool(
            "browser_select",
            "Chooses an option in a dropdown field on the currently selected tab (see browser_read) in the " +
            "user's real Chrome. Free to use without asking - same reasoning as browser_fill, this is still " +
            "just filling in form data, not clicking/submitting anything. Handles both real <select> elements " +
            "and custom combobox widgets automatically (common on Greenhouse and similar application forms) - " +
            "for the custom kind it types the value in and clicks the matching option from the popup that " +
            "appears, since typed text alone doesn't commit a value on those (confirmed the hard way: it can " +
            "visually look filled in a page read and still not actually be selected). No need to try " +
            "browser_fill as a workaround for a dropdown that rejects browser_select - this already tries " +
            "both interaction styles itself. Same label-vs-ref rule as browser_fill applies: when the label " +
            "repeats on the page (e.g. a \"Degree\" dropdown on each of several education entries), pass " +
            "field_ref from browser_read's \"Fillable fields\" list to target the exact one; label alone " +
            "always hits the first match. IMPORTANT: value must be the option's real text, copied exactly - " +
            "never a paraphrase, abbreviation, or a guess based on the field's label/question. For a real " +
            "<select>, browser_read's \"Fillable fields\" list already shows its actual options right there " +
            "(e.g. \"f5: Province [options: Alberta | British Columbia | ...]\") - use one of those verbatim. " +
            "If a field shows no [options: ...] (a custom combobox, whose choices usually aren't in the page " +
            "until opened), you haven't actually seen the real option text yet - don't invent one that sounds " +
            "plausible from the label (confirmed the hard way: guessing \"In Office\" for a field whose real " +
            "options were \"Open to commuting to closest hub office\"/\"Fully remote\" silently picked nothing " +
            "correct). Either ask the user what the exact options are, or try your best single guess and then " +
            "immediately browser_read again to confirm what actually got selected before moving on.",
            new
            {
                type = "object",
                properties = new
                {
                    label = new { type = "string", description = "The visible label text of the dropdown, e.g. \"Degree\"." },
                    value = new { type = "string", description = "The option's exact visible text, copied verbatim - see the options list in browser_read's \"Fillable fields\" output when there is one, e.g. \"Bachelor's\"." },
                    field_ref = new { type = "string", description = "The exact field from browser_read's \"Fillable fields\" list (e.g. \"f5\"). Required whenever the label repeats on the page. Optional when the label is unique." },
                },
                required = new[] { "label", "value" },
            });

        Tool browserCheckTool = BuildTool(
            "browser_check",
            "Checks or unchecks a checkbox on the currently selected tab (see browser_read) in the user's real " +
            "Chrome. Free to use without asking - same reasoning as browser_fill, this is still just filling " +
            "in form data, not clicking/submitting anything. Same label-vs-ref rule as browser_fill applies: " +
            "when the label repeats on the page, pass field_ref from browser_read's \"Fillable fields\" list to " +
            "target the exact one; label alone always hits the first match.",
            new
            {
                type = "object",
                properties = new
                {
                    label = new { type = "string", description = "The visible label text of the checkbox, e.g. \"I currently work here\"." },
                    @checked = new { type = "boolean", description = "true to check it, false to uncheck it. Defaults to true if omitted." },
                    field_ref = new { type = "string", description = "The exact field from browser_read's \"Fillable fields\" list (e.g. \"f8\"). Required whenever the label repeats on the page. Optional when the label is unique." },
                },
                required = new[] { "label" },
            });

        Tool browserUploadTool = BuildTool(
            "browser_upload",
            "Uploads a local file (a resume, a generated cover letter, etc.) to a file-picker field on the " +
            "currently selected tab (see browser_read) in the user's real Chrome. Free to use without asking - " +
            "same reasoning as browser_fill, this is still just filling in form data, not submitting anything. " +
            "File-picker fields are very often a native file input hidden behind a custom-styled widget, so " +
            "they'll frequently show up in the page snapshot as something else entirely (a button, a div) " +
            "rather than reading as an obvious upload field - if label alone doesn't find it, use field_ref " +
            "from browser_read's \"Fillable fields\" list, which does find it regardless of styling.",
            new
            {
                type = "object",
                properties = new
                {
                    label = new { type = "string", description = "The visible label/name of the upload field, e.g. \"Resume/CV\" or \"Cover Letter\"." },
                    file_path = new { type = "string", description = "Full local path to the file to upload, e.g. a resume or a cover letter just written with edit_file." },
                    field_ref = new { type = "string", description = "The exact field from browser_read's \"Fillable fields\" list (e.g. \"f9\"). Use this whenever label alone doesn't resolve the field." },
                },
                required = new[] { "label", "file_path" },
            });

        Tool browserClickTool = BuildTool(
            "browser_click",
            "Clicks a button or link, identified by its visible text, on the currently selected tab (see " +
            "browser_read) in the user's real Chrome. Deliberately narrow: only navigational clicks are " +
            "actually allowed to go through - pagination (next/previous/page), expand/collapse, show " +
            "more/load more, close/dismiss, add another entry (e.g. a bare \"Add\" button that reveals " +
            "another blank instance of a repeated form section, like another website-link row) - enforced " +
            "structurally, not just by asking nicely, so a call " +
            "for anything else (submit, buy, delete, confirm, post, follow, etc.) is refused outright " +
            "regardless of how it's asked for. Requires user authorization even for an allowed click, since " +
            "it's still a state change, not pure reading. Never call this hoping to sneak past the scope " +
            "check - if the user wants something submitted, posted, purchased, or otherwise sent, tell them " +
            "to click it themselves, the same as always.",
            new
            {
                type = "object",
                properties = new
                {
                    label = new { type = "string", description = "The visible text of the button or link to click, e.g. \"Next\" or \"Show more\"." },
                },
                required = new[] { "label" },
            });

        Tool searchEmailTool = BuildTool(
            "search_email",
            "Searches the user's Gmail using Gmail's own search syntax (e.g. \"is:unread\", " +
            "\"from:alice@example.com\", \"subject:invoice\") and returns matching messages (sender, date, " +
            "subject, snippet). Read-only and always allowed. Requires the user's Google account to be " +
            "connected (GOOGLE_CLIENT_ID/GOOGLE_CLIENT_SECRET in secrets/.env) - if it isn't, say so.",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Gmail search query syntax." },
                    max_results = new { type = "number", description = "Maximum number of emails to return. Defaults to 10." },
                },
                required = new[] { "query" },
            });

        Tool readEmailTool = BuildTool(
            "read_email",
            "Reads the full plain-text body of one specific email, by the id search_email already " +
            "includes in each of its results. The deliberate follow-up to a broad search_email scan for " +
            "the rare case something needed (an exact time, a reference number, detail past the first " +
            "couple hundred characters) is buried past the snippet - not a way to avoid search_email's " +
            "own broad-scan-first approach. Read-only and always allowed. Requires the user's Google " +
            "account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    message_id = new { type = "string", description = "The email's id, from a prior search_email result." },
                },
                required = new[] { "message_id" },
            });

        Tool sendEmailTool = BuildTool(
            "send_email",
            "Sends an email from the user's Gmail account. Irreversible and leaves the machine, so this " +
            "goes through Gate 2 - a content review of the actual drafted email happens automatically " +
            "immediately before it sends, separate from and in addition to Gate 1's task-level " +
            "authorization. Requires the user's Google account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    to = new { type = "string", description = "Recipient email address." },
                    subject = new { type = "string", description = "Email subject line." },
                    body = new { type = "string", description = "Plain-text email body." },
                },
                required = new[] { "to", "subject", "body" },
            });

        Tool listCalendarEventsTool = BuildTool(
            "list_calendar_events",
            "Lists the user's upcoming Google Calendar events (soonest first). Read-only and always " +
            "allowed. Requires the user's Google account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    max_results = new { type = "number", description = "Maximum number of events to return. Defaults to 10." },
                },
                required = Array.Empty<string>(),
            });

        Tool createCalendarEventTool = BuildTool(
            "create_calendar_event",
            "Creates an event on the user's primary Google Calendar. Requires user authorization - " +
            "adding something to the user's calendar is a real change, even though it's easy to undo " +
            "afterward (unlike send_email, which is why this is Gate 1 only, not Gate 2). Requires the " +
            "user's Google account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    summary = new { type = "string", description = "Event title." },
                    start = new { type = "string", description = "Start time, ISO 8601 (e.g. \"2026-08-05T14:00:00-07:00\")." },
                    end = new { type = "string", description = "End time, ISO 8601." },
                    description = new { type = "string", description = "Optional event description/notes." },
                },
                required = new[] { "summary", "start", "end" },
            });

        Tool readDocTool = BuildTool(
            "read_doc",
            "Reads the text content of a Google Doc. Read-only and always allowed. Requires the user's " +
            "Google account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    document_id = new { type = "string", description = "The document's ID (the long ID segment in its URL, e.g. the part after /d/ and before /edit)." },
                },
                required = new[] { "document_id" },
            });

        Tool createDocTool = BuildTool(
            "create_doc",
            "Creates a new Google Doc, optionally with initial text content. Requires user authorization - " +
            "a real change, though an easily-undoable one (unlike send_email, which is why this is Gate 1 " +
            "only, not Gate 2 - same reasoning as create_calendar_event). Requires the user's Google account " +
            "to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string", description = "The new document's title." },
                    content = new { type = "string", description = "Optional initial plain-text content." },
                },
                required = new[] { "title" },
            });

        Tool appendToDocTool = BuildTool(
            "append_to_doc",
            "Appends plain text to the end of an existing Google Doc. Requires user authorization - same " +
            "Gate 1 (not Gate 2) reasoning as create_doc/create_calendar_event. Requires the user's Google " +
            "account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    document_id = new { type = "string", description = "The document's ID (see read_doc)." },
                    text = new { type = "string", description = "The text to append." },
                },
                required = new[] { "document_id", "text" },
            });

        Tool replaceInDocTool = BuildTool(
            "replace_in_doc",
            "Finds and replaces text within an existing Google Doc - this is how to actually edit content " +
            "in place (fix a typo, swap out a word/phrase/line), not just add to the end. Matches on the " +
            "exact text itself (read the doc first to get it right), not a line number or position - " +
            "replaces every occurrence found. If nothing was found, the result says so explicitly rather " +
            "than silently doing nothing; re-read the doc and adjust find_text if that happens. Requires " +
            "user authorization - same Gate 1 (not Gate 2) reasoning as append_to_doc/create_calendar_event. " +
            "Requires the user's Google account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    document_id = new { type = "string", description = "The document's ID (see read_doc)." },
                    find_text = new { type = "string", description = "The exact existing text to find, e.g. a specific line or phrase." },
                    replace_text = new { type = "string", description = "The text to replace it with." },
                    match_case = new { type = "boolean", description = "Whether the search is case-sensitive. Defaults to false." },
                },
                required = new[] { "document_id", "find_text", "replace_text" },
            });

        Tool searchDriveTool = BuildTool(
            "search_drive",
            "Searches the user's Google Drive using Drive's own query syntax (e.g. \"name contains " +
            "'resume'\", \"mimeType = 'application/pdf'\") and returns matching files (name, type, last " +
            "modified date, id, link). Read-only and always allowed. Requires the user's Google account to " +
            "be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Drive search query syntax." },
                    max_results = new { type = "number", description = "Maximum number of files to return. Defaults to 10." },
                },
                required = new[] { "query" },
            });

        Tool uploadToDriveTool = BuildTool(
            "upload_to_drive",
            "Uploads a local file to the user's Google Drive as a new file. Requires user authorization - " +
            "same Gate 1 (not Gate 2) reasoning as create_doc/create_calendar_event; only ever creates a " +
            "new file, never overwrites something already in the user's Drive that Nova didn't upload " +
            "herself. Requires the user's Google account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    local_file_path = new { type = "string", description = "Full local path to the file to upload." },
                    drive_file_name = new { type = "string", description = "Name to give the file in Drive. Defaults to the local file's own name." },
                },
                required = new[] { "local_file_path" },
            });

        // rows/values below are always an array of arrays (one inner array
        // per spreadsheet row, one string per cell) - matches how Sheets'
        // own API takes values, and lets a multi-row write happen in one
        // call instead of one row at a time.
        Tool readSheetTool = BuildTool(
            "read_sheet",
            "Reads cell values from a Google Sheet, tab-separated, one line per row. Read-only and always " +
            "allowed. Requires the user's Google account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    spreadsheet_id = new { type = "string", description = "The spreadsheet's ID (the long ID segment in its URL, e.g. the part after /d/ and before /edit)." },
                    range = new { type = "string", description = "A1 notation range, e.g. \"Sheet1!A1:D10\". Omit to read the whole first sheet." },
                },
                required = new[] { "spreadsheet_id" },
            });

        Tool createSheetTool = BuildTool(
            "create_sheet",
            "Creates a new Google Sheet, optionally with initial row data written starting at A1. Requires " +
            "user authorization - a real change, though an easily-undoable one (same Gate 1, not Gate 2, " +
            "reasoning as create_doc/create_calendar_event). Requires the user's Google account to be " +
            "connected.",
            new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string", description = "The new spreadsheet's title." },
                    initial_rows = new
                    {
                        type = "array",
                        description = "Optional initial data: an array of rows, each row an array of cell values (strings/numbers), e.g. [[\"Name\", \"Total\"], [\"Alice\", \"12\"]].",
                        items = new { type = "array", items = new { } },
                    },
                },
                required = new[] { "title" },
            });

        Tool appendSheetRowsTool = BuildTool(
            "append_sheet_rows",
            "Appends rows to the end of a Google Sheet - finds the actual last row with data itself, no " +
            "need to know how many rows already exist. Requires user authorization - same Gate 1 (not Gate " +
            "2) reasoning as append_to_doc. Requires the user's Google account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    spreadsheet_id = new { type = "string", description = "The spreadsheet's ID (see read_sheet)." },
                    range = new { type = "string", description = "A1 notation range/sheet name to append within, e.g. \"Sheet1\". Omit to use the whole first sheet." },
                    rows = new
                    {
                        type = "array",
                        description = "Rows to append, each an array of cell values, e.g. [[\"Bob\", \"7\"]].",
                        items = new { type = "array", items = new { } },
                    },
                },
                required = new[] { "spreadsheet_id", "rows" },
            });

        Tool updateSheetRangeTool = BuildTool(
            "update_sheet_range",
            "Writes/overwrites values in a specific cell range of a Google Sheet - the Sheets analog of " +
            "replace_in_doc: cells are addressed positionally (A1 notation, e.g. \"Sheet1!B2:C3\"), the way " +
            "a spreadsheet's own UI addresses them, not by matching existing text. Requires user " +
            "authorization - same Gate 1 (not Gate 2) reasoning as replace_in_doc/append_to_doc: real but " +
            "easily undone by writing over it again or checking the sheet's own Google-side version " +
            "history. Requires the user's Google account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    spreadsheet_id = new { type = "string", description = "The spreadsheet's ID (see read_sheet)." },
                    range = new { type = "string", description = "A1 notation range to write, e.g. \"Sheet1!B2:C3\"." },
                    values = new
                    {
                        type = "array",
                        description = "Rows to write into the range, each an array of cell values.",
                        items = new { type = "array", items = new { } },
                    },
                },
                required = new[] { "spreadsheet_id", "range", "values" },
            });

        Tool readSlidesTool = BuildTool(
            "read_slides",
            "Reads a Google Slides presentation's text content, one line per slide. Read-only and always " +
            "allowed. Requires the user's Google account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    presentation_id = new { type = "string", description = "The presentation's ID (the long ID segment in its URL, e.g. the part after /d/ and before /edit)." },
                },
                required = new[] { "presentation_id" },
            });

        Tool createPresentationTool = BuildTool(
            "create_presentation",
            "Creates a new, empty Google Slides presentation. Creating only sets the title - use " +
            "append_slide afterward to actually add content, the same two-step shape as create_doc when " +
            "content is added separately. Requires user authorization - same Gate 1 (not Gate 2) reasoning " +
            "as create_doc/create_calendar_event. Requires the user's Google account to be connected.",
            new
            {
                type = "object",
                properties = new { title = new { type = "string", description = "The new presentation's title." } },
                required = new[] { "title" },
            });

        Tool appendSlideTool = BuildTool(
            "append_slide",
            "Adds one new slide (title + body text, using Slides' built-in \"title and body\" layout) to an " +
            "existing presentation. Requires user authorization - same Gate 1 (not Gate 2) reasoning as " +
            "append_to_doc. Requires the user's Google account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    presentation_id = new { type = "string", description = "The presentation's ID (see read_slides)." },
                    title = new { type = "string", description = "The new slide's title text." },
                    body = new { type = "string", description = "Optional body text for the new slide." },
                },
                required = new[] { "presentation_id", "title" },
            });

        Tool replaceTextInSlidesTool = BuildTool(
            "replace_text_in_slides",
            "Finds and replaces text across every slide in a Google Slides presentation - matches on the " +
            "exact text itself (read the presentation first to get it right), replaces every occurrence " +
            "found across all slides. If nothing was found, the result says so explicitly rather than " +
            "silently doing nothing. Requires user authorization - same Gate 1 (not Gate 2) reasoning as " +
            "replace_in_doc. Requires the user's Google account to be connected.",
            new
            {
                type = "object",
                properties = new
                {
                    presentation_id = new { type = "string", description = "The presentation's ID (see read_slides)." },
                    find_text = new { type = "string", description = "The exact existing text to find." },
                    replace_text = new { type = "string", description = "The text to replace it with." },
                    match_case = new { type = "boolean", description = "Whether the search is case-sensitive. Defaults to false." },
                },
                required = new[] { "presentation_id", "find_text", "replace_text" },
            });

        // Self-contained tools (see Tools/Dynamic/) - Nova building and using
        // her own small, isolated capabilities for things that aren't
        // already a built-in tool, preferring an existing free API/library
        // over writing everything from scratch. Loaded fresh and unloaded
        // again on every run_tool call (see DynamicToolRuntime) - never kept
        // resident between calls, so an unused one costs nothing once it's
        // done, not memory/resources sitting idle in the background.
        Tool buildToolTool = BuildTool(
            "build_tool",
            "Builds a new self-contained tool, or rebuilds/updates an existing one by reusing the same " +
            "name - a small, isolated piece of C# for a capability that isn't already a built-in tool. Not " +
            "limited to wrapping an external API - a NuGet library (e.g. a PDF-parsing library, to read a " +
            "file type nothing built-in handles) or a purely algorithmic tool with no external dependency " +
            "at all are just as valid a shape for this; API integration is one option here, not the " +
            "default assumption, and \"there's no API for that\" is not a reason this can't be built. When " +
            "it does call an external service, prefer a free API/library, and only reach for a " +
            "paid/metered one after actually exhausting free options - set uses_paid_api honestly either " +
            "way, since that's what determines whether " +
            "the user gets an explicit heads-up before it can ever run (see below). Compiled and " +
            "version-controlled automatically (a git commit per successful build); a build that fails to " +
            "compile registers nothing and leaves any previous working version untouched - the compiler " +
            "output comes back so it can be fixed and retried. Check list_tools first in case a suitable " +
            "tool already exists, rather than rebuilding one. Requires user authorization; its first real " +
            "run (via run_tool) additionally needs a separate review before it actually executes, the same " +
            "as any other irreversible-feeling first-time action. Set has_external_effects and " +
            "uses_paid_api honestly - both push a tool past \"approved once, trusted forever\": a plain " +
            "read-only tool (looks something up, computes something, both flags false) is trusted for " +
            "good once approved, the same as any other Gate-1-only action, but has_external_effects=true " +
            "(actually *does* something external on every call - sends a message, posts something, places " +
            "an order, modifies state somewhere else, the tool-equivalent of an HTTP POST/PUT rather than " +
            "a GET) or uses_paid_api=true (costs real money on every call, not just the build) each mean " +
            "the same Gate 2 review happens on every single run, not just the first - either flag alone is " +
            "enough to trigger it, and both can be true at once.\n\n" +
            "source_code must be one complete C# file with exactly one class implementing " +
            "Nova.ToolContract.INovaTool, shaped like:\n" +
            "using Nova.ToolContract;\n" +
            "using System.Collections.Generic;\n" +
            "using System.Text.Json;\n" +
            "using System.Threading;\n" +
            "using System.Threading.Tasks;\n\n" +
            "public sealed class Tool : INovaTool\n" +
            "{\n" +
            "    public async Task<string> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)\n" +
            "    {\n" +
            "        // read parameters from `input` (matches input_schema below), return a plain text result\n" +
            "    }\n" +
            "}",
            new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "Short identifier: letters, digits, underscores only, starting with a letter, e.g. \"weather_lookup\". This is what run_tool refers to it by later." },
                    description = new { type = "string", description = "What this tool does and when to use it - shown later via list_tools so a future task knows it exists without rebuilding it." },
                    input_schema = new { type = "object", description = "JSON Schema \"properties\" object describing what run_tool's `input` should contain for this tool, e.g. {\"city\": {\"type\": \"string\", \"description\": \"...\"}}." },
                    source_code = new { type = "string", description = "The full C# source file - see the shape described above." },
                    uses_paid_api = new { type = "boolean", description = "True only if this tool calls a metered/paid API (e.g. Google Maps Platform's Directions/Distance Matrix/Places, which need a billing-enabled account and charge per request past a small free tier) rather than a genuinely free one. Defaults to false - only set true after actually confirming no free alternative covers this." },
                    has_external_effects = new { type = "boolean", description = "True if this tool actually changes something outside Nova each time it runs - sends/posts/places/modifies anything external (the tool-equivalent of an HTTP POST/PUT). False (the default) for anything read-only/computational (the equivalent of a GET) - looking something up, calculating something, formatting something. Every run_tool call for a true tool needs a fresh Gate 2 review, not just the first." },
                },
                required = new[] { "name", "description", "input_schema", "source_code" },
            });

        Tool runToolTool = BuildTool(
            "run_tool",
            "Runs an already-built self-contained tool by name (see build_tool/list_tools). Loads it fresh, " +
            "executes it once, and unloads it immediately after - every call, not just the first. A tool " +
            "that's never been run before needs a review of what it does before this actually executes it " +
            "the first time; after that, reusing the same unchanged, read-only tool doesn't need re-approving. " +
            "A tool built with has_external_effects true is the one exception - it needs that same review " +
            "on every single run, not just the first, since it has a real effect outside Nova each time.",
            new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "The tool's name, exactly as given to build_tool." },
                    input = new { type = "object", description = "Arguments matching that tool's registered input_schema." },
                },
                required = new[] { "name", "input" },
            });

        Tool listToolsTool = BuildTool(
            "list_tools",
            "Lists every self-contained tool already built (name, description, input schema) - check this " +
            "before build_tool to see whether something suitable already exists rather than rebuilding it. " +
            "Free to use without asking.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() });

        Tool readRecentErrorsTool = BuildTool(
            "read_recent_errors",
            "Reads the most recent entries from Nova's own error log (data/errors.log) - use this when the " +
            "user asks what went wrong after Nova mentioned hitting an error, or asks to look into a problem " +
            "she flagged earlier. Free to use without asking.",
            new
            {
                type = "object",
                properties = new
                {
                    count = new { type = "integer", description = "How many recent error entries to return. Defaults to 5." },
                },
                required = Array.Empty<string>(),
            });

        Tool getSettingsTool = BuildTool(
            "get_settings",
            "Reads Nova's own adjustable settings (currently: calendar reminder lead time, and whether the " +
            "email/calendar watchers are on) - the standing preferences stored in data/settings.env, not " +
            "anything about the current conversation. Free to use without asking.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() });

        Tool updateSettingTool = BuildTool(
            "update_setting",
            "Changes one of Nova's own adjustable settings (see get_settings for the current values) - e.g. " +
            "the user saying \"remind me 15 minutes before instead\" or \"turn off email alerts\". Persisted " +
            "to data/settings.env, so it survives a restart. Free to use without asking.",
            new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "Which setting to change: \"calendar_reminder_lead_minutes\" (a positive whole number of minutes), \"calendar_watcher_enabled\" or \"email_watcher_enabled\" (true/false)." },
                    value = new { type = "string", description = "The new value, e.g. \"15\" or \"false\"." },
                },
                required = new[] { "name", "value" },
            });

        return
        [
            readFileTool, listFilesTool, editFileTool, revertFileEditTool, listFileEditsTool, deletePathTool, runCommandTool, saveMemoryTool, searchMemoryTool, searchConversationHistoryTool, readScreenTool,
            scrollDesktopTool, interactDesktopTool, openPathTool, openWatchedTerminalTool, browserNavigateTool,
            browserReadTool, browserFillTool, browserSelectTool, browserCheckTool, browserUploadTool, browserClickTool, searchEmailTool, readEmailTool, sendEmailTool,
            listCalendarEventsTool, createCalendarEventTool, readDocTool, createDocTool, appendToDocTool, replaceInDocTool,
            searchDriveTool, uploadToDriveTool,
            readSheetTool, createSheetTool, appendSheetRowsTool, updateSheetRangeTool,
            readSlidesTool, createPresentationTool, appendSlideTool, replaceTextInSlidesTool,
            buildToolTool, runToolTool, listToolsTool, getSettingsTool, updateSettingTool, readRecentErrorsTool,
        ];
    }

    private static Tool BuildTool(string name, string description, object inputSchema)
    {
        JsonElement schemaJson = JsonSerializer.SerializeToElement(inputSchema);
        var schemaRawData = schemaJson.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        var schema = new InputSchema(schemaRawData);

        JsonElement json = JsonSerializer.SerializeToElement(new { name, description, input_schema = inputSchema });
        var rawData = json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        return new Tool(rawData) { Name = name, InputSchema = schema };
    }
}
