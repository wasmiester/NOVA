using System.Collections.Generic;

namespace Nova;

// One frame of everything a skin needs to render itself - computed once
// per DispatcherTimer tick by OverlayWindow, pushed into whichever skin is
// currently active. Skins never read NovaAssistant directly; they're pure
// "given this state, look right" renderers.
internal readonly record struct OverlayState(
    bool IsBusy,
    bool IsSpeaking,
    bool Asleep,      // !Engaged - the hotkey is the only way back from this
    ActivationMode Mode,
    IReadOnlyList<TranscriptEntry> Transcript,
    // The maximized activity feed's own rolling log - see
    // NovaAssistant.RecordActivity/SnapshotActivityHistory. Separate from
    // Transcript above since it's a different kind of history (tool-call
    // steps, not conversational turns) with its own append-only terminal-
    // style rendering, not the You/Nova bubble/line treatment.
    IReadOnlyList<ActivityEntry> ActivityHistory,
    bool ChromeLinked,
    bool GmailLinked,
    // What Nova's actually doing right now during a silent tool-execution
    // stretch (e.g. "reading the browser page") - the same text already
    // printed to the console via ToolDescriptions.DescribeToolStatus, now
    // also surfaced visually instead of the overlay only ever showing
    // LISTENING/SPEAKING regardless of what's actually happening. Null
    // when idle or between activities - skins fall back to their normal
    // engage hint then.
    string? CurrentActivity,
    // Non-null exactly when a Gate 2 review is awaiting the confirm popup's
    // Confirm/Cancel click (see ConfirmPopup, NovaAssistant.ConfirmGate2Review)
    // - the plain-language action description to show in the popup body,
    // same text ToolDescriptions.DescribeGate2Action already computed for
    // it. Unlike CurrentActivity this is never derived from IsBusy - Gate 2
    // deliberately isn't "busy" while waiting on the user (see
    // NovaAssistant.PendingGate2Prompt's own doc comment).
    string? PendingGate2Prompt,
    // Google-credentials popup (see CredentialsPopup,
    // NovaAssistant.RequestGoogleCredentials) - NeedsGoogleCredentials
    // shows/hides the popup itself; GoogleConnecting/GoogleConnectError
    // drive its in-progress/error status line while it's up.
    bool NeedsGoogleCredentials,
    bool GoogleConnecting,
    string? GoogleConnectError);
