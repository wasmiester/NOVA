using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nova;

internal static class ToolDescriptions
{
    // Groups same-tool calls together so a bulk operation (many edit_file
    // calls authorized in one round, say) reads as "edit fileA, fileB, and
    // 45 more like that (47 total)" instead of 47 "and"-joined clauses in a
    // row - the true scope either way, but a 47-item run-on sentence is
    // both unspeakable and, in practice, unlistenable, which defeats the
    // point of stating scope honestly (see the roadmap's bulk-action open
    // question). Small groups stay fully spelled out, same as before -
    // only a group past BulkThreshold gets summarized, and even then the
    // real count is always said, never hidden behind a vague "a few".
    // Shared by both Gate 1 (DescribePendingTask) and Gate 2 (Gate2Parts) -
    // the same honesty concern applies to either one bundling many calls
    // into a single ask.
    private const int BulkThreshold = 3;

    private static IEnumerable<string> DescribeWithScope(IEnumerable<PendingToolCall> calls, Func<PendingToolCall, string> describeOne)
    {
        foreach (IGrouping<string, PendingToolCall> group in calls.GroupBy(c => c.Name))
        {
            List<PendingToolCall> items = group.ToList();
            if (items.Count < BulkThreshold)
            {
                foreach (PendingToolCall call in items)
                {
                    yield return describeOne(call);
                }

                continue;
            }

            yield return $"{describeOne(items[0])}, {describeOne(items[1])}, and {items.Count - 2} more like that ({items.Count} total)";
        }
    }

    public static string DescribePendingTask(IEnumerable<PendingToolCall> calls)
    {
        IEnumerable<string> parts = DescribeWithScope(calls, call => call.Name switch
        {
            "edit_file" => DescribeEditFile(call),
            "revert_file_edit" => $"revert {ToolInput.GetString(call.Input, "path") ?? "a file"} to an earlier version",
            // spoken form deliberately uses the natural-language description, never the raw
            // command - the console status line (DescribeToolStatus) keeps the raw command for reference
            "run_command" => ToolInput.GetString(call.Input, "description") ?? "run a command",
            "browser_navigate" => $"go to {ToolInput.GetString(call.Input, "url") ?? "a page"}",
            // deliberately not reading the field value aloud (could be anything, including
            // something sensitive) - it's printed to the console for visual review instead
            "browser_fill" => $"fill in the \"{ToolInput.GetString(call.Input, "label") ?? "a field"}\" field",
            "interact_desktop" => (ToolInput.GetString(call.Input, "action") ?? "click") == "type"
                ? $"type into \"{ToolInput.GetString(call.Input, "label") ?? "a control"}\""
                : $"click \"{ToolInput.GetString(call.Input, "label") ?? "a control"}\"",
            // only reaches here when run_as_admin is true - a plain open_path is free, see ToolCatalog.IsFree
            "open_path" => $"open \"{ToolInput.GetString(call.Input, "path") ?? "File Explorer"}\" as administrator",
            "browser_click" => $"click \"{ToolInput.GetString(call.Input, "label") ?? "a button"}\"",
            "create_calendar_event" => $"add \"{ToolInput.GetString(call.Input, "summary") ?? "an event"}\" to your calendar",
            "create_doc" => $"create a doc called \"{ToolInput.GetString(call.Input, "title") ?? "Untitled"}\"",
            "append_to_doc" => "add text to that doc",
            "replace_in_doc" => $"replace \"{ToolInput.GetString(call.Input, "find_text") ?? "some text"}\" in that doc",
            "upload_to_drive" => $"upload \"{ToolInput.GetString(call.Input, "local_file_path") ?? "a file"}\" to your Drive",
            "create_sheet" => $"create a spreadsheet called \"{ToolInput.GetString(call.Input, "title") ?? "Untitled"}\"",
            "append_sheet_rows" => "add rows to that spreadsheet",
            "update_sheet_range" => $"update the {ToolInput.GetString(call.Input, "range") ?? "given"} range in that spreadsheet",
            "create_presentation" => $"create a presentation called \"{ToolInput.GetString(call.Input, "title") ?? "Untitled"}\"",
            "append_slide" => $"add a slide titled \"{ToolInput.GetString(call.Input, "title") ?? "Untitled"}\" to that presentation",
            "replace_text_in_slides" => $"replace \"{ToolInput.GetString(call.Input, "find_text") ?? "some text"}\" in that presentation",
            "build_tool" => $"build a \"{ToolInput.GetString(call.Input, "name") ?? "new"}\" tool - {ToolInput.GetString(call.Input, "description") ?? "a new capability"}" +
                ((ToolInput.GetBool(call.Input, "uses_paid_api") ?? false) ? " (this one uses a paid API)" : ""),
            "run_tool" => $"run the \"{ToolInput.GetString(call.Input, "name") ?? ""}\" tool",
            _ => $"use {call.Name}",
        });

        return $"I'd like to {string.Join(" and ", parts)}. Should I go ahead?";
    }

    // "create" vs "edit" mirrors ToolCatalog.IsFree/IsGate2's own split
    // exactly (a brand new path is free, an existing one is Gate 1 *and*
    // Gate 2) - said out loud too, so the user hears which kind of change
    // this actually is, not a generic "edit" for something that's really
    // a fresh file with nothing to lose.
    private static string DescribeEditFile(PendingToolCall call)
    {
        string path = ToolInput.GetString(call.Input, "path") ?? "a file";
        return File.Exists(path) ? $"edit {path}" : $"create {path}";
    }

    // Shared by DescribeGate2Review (spoken) and DescribeGate2Action (the
    // overlay's confirm-popup body, see ConfirmPopup) - both describe the
    // *concrete content* about to go out, not just restate the task the
    // way DescribePendingTask (Gate 1) does, they just frame it
    // differently for voice vs. a popup card. send_email deliberately
    // omits the body here - it's long/could be anything, so it's printed
    // to the console instead (see the Gate 2 block in NovaAssistant), same
    // pattern as browser_fill's field values.
    // toolDescriptionLookup is only ever needed for run_tool (see below) -
    // ToolDescriptions has no DB access of its own (matches ToolCatalog's
    // own pure-schema style), so NovaAssistant hands in a lookup backed by
    // ToolRegistry when it already has one on hand, and every other Gate 2
    // tool ignores it entirely. AlreadyApproved is what distinguishes the
    // two different reasons a run_tool call can land here - a genuinely
    // new tool's first-ever run, vs. an already-trusted tool that still
    // needs a review on *every* call because it has real external effects
    // (see HasExternalEffectsToolRun) - the two need different phrasing,
    // not "run for the first time" every single time.
    private static IEnumerable<string> Gate2Parts(IEnumerable<PendingToolCall> calls, Func<string, (string? Description, bool AlreadyApproved)>? toolDescriptionLookup) => DescribeWithScope(calls, call => call.Name switch
    {
        "send_email" => $"send an email to {ToolInput.GetString(call.Input, "to")} with the subject \"{ToolInput.GetString(call.Input, "subject")}\"",
        // Reached only when the target already exists (see ToolCatalog.IsGate2) -
        // a brand new file never gets here at all, it's free.
        "edit_file" => $"overwrite {ToolInput.GetString(call.Input, "path")} - its previous content is saved first, so this can be undone",
        "revert_file_edit" => $"revert {ToolInput.GetString(call.Input, "path")} to an earlier version - what's there now is saved first too, so this can be undone as well",
        "delete_path" => $"delete {ToolInput.GetString(call.Input, "path")} - it goes to the Recycle Bin, not permanently erased, but still worth a real look before confirming",
        // Every run_command call reaches Gate 2 now (see ToolCatalog.
        // Gate2Tools) - CommandRiskClassifier no longer decides *whether*
        // this review happens, only what it says: a specific reason when
        // the command matches one of its categories, a plain "review this"
        // otherwise (a command it doesn't recognize as risky still gets
        // looked at, just without a false claim about *why*).
        "run_command" => DescribeRunCommandReview(call),
        // Reached either for a tool's *first-ever* run, or for an
        // already-approved tool that has real external effects on every
        // call (see the conditional Gate 2 check in NovaAssistant's round
        // loop) - an already-approved, read-only tool being reused never
        // lands here.
        "run_tool" => DescribeToolRunReview(call, toolDescriptionLookup),
        // Reached only when run_as_admin is true (see ToolCatalog.IsGate2) -
        // a plain open_path is free and never gets here. Windows' own UAC
        // prompt still fires independently on top of this - it just never
        // says *what* the elevated action actually is, which is what this
        // review closes the gap on.
        "open_path" => $"open \"{ToolInput.GetString(call.Input, "path") ?? "File Explorer"}\" as administrator",
        _ => $"use {call.Name}",
    });

    private static string DescribeRunCommandReview(PendingToolCall call)
    {
        string command = ToolInput.GetString(call.Input, "command") ?? "a command";
        return CommandRiskClassifier.IsDestructive(command)
            ? $"run this exact command: {command} - it looks like it could edit, delete, or download something, so it needs a closer look before it actually runs"
            : $"run this exact command: {command}";
    }

    private static string DescribeToolRunReview(PendingToolCall call, Func<string, (string? Description, bool AlreadyApproved)>? toolDescriptionLookup)
    {
        string name = ToolInput.GetString(call.Input, "name") ?? "a new tool";
        (string? description, bool alreadyApproved) = toolDescriptionLookup?.Invoke(name) ?? (null, false);

        // alreadyApproved here means this run only landed at Gate 2 because
        // it has external effects on every call, not because it's unreviewed -
        // "run for the first time" would be actively misleading for a tool
        // that's been run and approved many times before.
        string framing = alreadyApproved ? "run" : "run for the first time";
        return description is null
            ? $"{framing} the \"{name}\" tool"
            : $"{framing} the \"{name}\" tool - {description}";
    }

    // Gate 2 review text - spoken right before an irreversible action
    // actually executes. Points at the overlay's confirm popup rather than
    // asking "should I go ahead?" - Gate 2 is deliberately click-only (see
    // ConfirmGate2Review), so the spoken ask shouldn't imply a spoken yes
    // would work.
    public static string DescribeGate2Review(IEnumerable<PendingToolCall> calls, Func<string, (string? Description, bool AlreadyApproved)>? toolDescriptionLookup = null) =>
        $"Here's what I'm about to do: {string.Join(" and ", Gate2Parts(calls, toolDescriptionLookup))}. Please confirm on the popup before I continue.";

    // The overlay confirm-popup's body text (see ConfirmPopup) - same
    // action description as DescribeGate2Review, without the spoken-only
    // "confirm on the popup" framing, since the popup's own Confirm/Cancel
    // buttons already ask that visually.
    public static string DescribeGate2Action(IEnumerable<PendingToolCall> calls, Func<string, (string? Description, bool AlreadyApproved)>? toolDescriptionLookup = null)
    {
        string joined = string.Join(" and ", Gate2Parts(calls, toolDescriptionLookup));
        return char.ToUpperInvariant(joined[0]) + joined[1..] + "?";
    }

    public static string DescribeToolStatus(PendingToolCall call) => call.Name switch
    {
        "read_file" => $"reading {ToolInput.GetString(call.Input, "path") ?? "a file"}",
        "list_files" => $"listing files in {ToolInput.GetString(call.Input, "path") ?? "a folder"}",
        "edit_file" => $"writing {ToolInput.GetString(call.Input, "path") ?? "a file"}",
        "revert_file_edit" => $"reverting {ToolInput.GetString(call.Input, "path") ?? "a file"}",
        "list_file_edits" => $"checking edit history for {ToolInput.GetString(call.Input, "path") ?? "a file"}",
        "delete_path" => $"deleting {ToolInput.GetString(call.Input, "path") ?? "a file or folder"}",
        "run_command" => $"running: {ToolInput.GetString(call.Input, "command") ?? "a command"}",
        "save_memory" => "saving to memory",
        "search_memory" => $"searching memory for \"{ToolInput.GetString(call.Input, "query") ?? ""}\"",
        "search_conversation_history" => $"searching past tasks for \"{ToolInput.GetString(call.Input, "query") ?? ""}\"",
        "read_screen" => "reading the screen",
        "scroll_desktop" => $"scrolling {ToolInput.GetString(call.Input, "direction") ?? ""}",
        "open_path" => string.IsNullOrEmpty(ToolInput.GetString(call.Input, "path")) ? "opening File Explorer" : $"opening \"{ToolInput.GetString(call.Input, "path")}\"",
        "open_watched_terminal" => "opening a watched terminal",
        "interact_desktop" => (ToolInput.GetString(call.Input, "action") ?? "click") == "type"
            ? $"typing into \"{ToolInput.GetString(call.Input, "label") ?? "a control"}\""
            : $"clicking \"{ToolInput.GetString(call.Input, "label") ?? "a control"}\"",
        "browser_navigate" => $"opening {ToolInput.GetString(call.Input, "url") ?? "a page"}",
        "browser_read" => "reading the browser page",
        "browser_fill" => $"filling in \"{ToolInput.GetString(call.Input, "label") ?? "a field"}\"",
        "browser_select" => $"selecting \"{ToolInput.GetString(call.Input, "value") ?? "an option"}\" for \"{ToolInput.GetString(call.Input, "label") ?? "a field"}\"",
        "browser_check" => $"{(ToolInput.GetBool(call.Input, "checked") ?? true ? "checking" : "unchecking")} \"{ToolInput.GetString(call.Input, "label") ?? "a checkbox"}\"",
        "browser_upload" => $"uploading to \"{ToolInput.GetString(call.Input, "label") ?? "a field"}\"",
        "browser_click" => $"clicking \"{ToolInput.GetString(call.Input, "label") ?? "a button"}\"",
        "search_email" => $"searching your email for \"{ToolInput.GetString(call.Input, "query") ?? ""}\"",
        "read_email" => "reading the email",
        "send_email" => $"sending an email to {ToolInput.GetString(call.Input, "to") ?? "someone"}",
        "list_calendar_events" => "checking your calendar",
        "create_calendar_event" => $"adding \"{ToolInput.GetString(call.Input, "summary") ?? "an event"}\" to your calendar",
        "read_doc" => "reading the doc",
        "create_doc" => $"creating \"{ToolInput.GetString(call.Input, "title") ?? "a doc"}\"",
        "append_to_doc" => "adding text to the doc",
        "replace_in_doc" => $"replacing \"{ToolInput.GetString(call.Input, "find_text") ?? "text"}\" in the doc",
        "search_drive" => $"searching your Drive for \"{ToolInput.GetString(call.Input, "query") ?? ""}\"",
        "upload_to_drive" => $"uploading \"{ToolInput.GetString(call.Input, "local_file_path") ?? "a file"}\" to Drive",
        // Previously always the same fixed string regardless of which
        // spreadsheet/range - confirmed live as a real gap: two
        // back-to-back "reading the spreadsheet" console lines gave no way
        // to tell whether that was a genuine duplicate read or two
        // legitimately different reads.
        "read_sheet" => ToolInput.GetString(call.Input, "range") is { } sheetRange
            ? $"reading the spreadsheet ({sheetRange})"
            : "reading the spreadsheet",
        "create_sheet" => $"creating \"{ToolInput.GetString(call.Input, "title") ?? "a spreadsheet"}\"",
        "append_sheet_rows" => "adding rows to the spreadsheet",
        "update_sheet_range" => $"updating {ToolInput.GetString(call.Input, "range") ?? "a range"} in the spreadsheet",
        "read_slides" => "reading the presentation",
        "create_presentation" => $"creating \"{ToolInput.GetString(call.Input, "title") ?? "a presentation"}\"",
        "append_slide" => $"adding a slide to the presentation",
        "replace_text_in_slides" => $"replacing \"{ToolInput.GetString(call.Input, "find_text") ?? "text"}\" in the presentation",
        "build_tool" => $"building the \"{ToolInput.GetString(call.Input, "name") ?? "new"}\" tool",
        "run_tool" => $"running the \"{ToolInput.GetString(call.Input, "name") ?? ""}\" tool",
        "list_tools" => "checking which tools already exist",
        _ => $"using {call.Name}",
    };

    // Short, verb-only form for the overlay's maximized activity feed (see
    // NovaAssistant's activity-history recording) - deliberately drops the
    // dynamic argument (a search query, a file path, a field label)
    // DescribeToolStatus above includes, since that's exactly what a
    // terminal-style scrolling log doesn't have room for and doesn't need:
    // the full detail is still one click away in the actual console log,
    // this is just "what kind of thing is happening right now." A separate
    // explicit switch rather than deriving this from DescribeToolStatus's
    // own string (e.g. stripping everything after "for") - the formats
    // vary too much across tools (quoted-suffix, parenthetical, colon-
    // prefixed) for one generic trim rule to hold up cleanly.
    public static string DescribeToolActivity(PendingToolCall call) => call.Name switch
    {
        "read_file" => "reading a file",
        "list_files" => "listing files",
        "edit_file" => "writing a file",
        "revert_file_edit" => "reverting a file",
        "list_file_edits" => "checking edit history",
        "delete_path" => "deleting a file",
        "run_command" => "running a command",
        "save_memory" => "saving to memory",
        "search_memory" => "searching memory",
        "search_conversation_history" => "searching past tasks",
        "read_screen" => "reading the screen",
        "scroll_desktop" => "scrolling",
        "open_path" => "opening a file",
        "open_watched_terminal" => "opening a terminal",
        "interact_desktop" => (ToolInput.GetString(call.Input, "action") ?? "click") == "type" ? "typing" : "clicking",
        "browser_navigate" => "opening a page",
        "browser_read" => "reading the browser page",
        "browser_fill" => "filling in a field",
        "browser_select" => "selecting an option",
        "browser_check" => (ToolInput.GetBool(call.Input, "checked") ?? true) ? "checking a box" : "unchecking a box",
        "browser_upload" => "uploading a file",
        "browser_click" => "clicking a button",
        "search_email" => "searching your email",
        "read_email" => "reading the email",
        "send_email" => "sending an email",
        "list_calendar_events" => "checking your calendar",
        "create_calendar_event" => "adding a calendar event",
        "read_doc" => "reading the doc",
        "create_doc" => "creating a doc",
        "append_to_doc" => "adding text to the doc",
        "replace_in_doc" => "editing the doc",
        "search_drive" => "searching your Drive",
        "upload_to_drive" => "uploading to Drive",
        "read_sheet" => "reading the spreadsheet",
        "create_sheet" => "creating a spreadsheet",
        "append_sheet_rows" => "adding rows to the spreadsheet",
        "update_sheet_range" => "updating the spreadsheet",
        "read_slides" => "reading the presentation",
        "create_presentation" => "creating a presentation",
        "append_slide" => "adding a slide",
        "replace_text_in_slides" => "editing the presentation",
        "build_tool" => "building a tool",
        "run_tool" => "running a tool",
        "list_tools" => "checking existing tools",
        _ => $"using {call.Name}",
    };
}
