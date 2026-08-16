using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using ElBruno.LocalEmbeddings;
using Google.Apis.Auth.OAuth2;
using Whisper.net;

namespace Nova;

// The core conversation/turn-taking orchestrator: owns the Claude
// conversation history, Gate 1 authorization state, and tool dispatch.
// AudioCapturePipeline feeds it finished utterances via DispatchUtterance
// and reads back IsBusy/IsSpeaking to decide when to listen vs. treat new
// audio as a barge-in (via Interrupt()).
internal sealed class NovaAssistant
{
    private const int MaxToolRounds = 6; // safety cap against a runaway tool-call loop
    private const int SpeechCooldownMs = 300;

    private readonly AnthropicClient _client;
    private readonly ITtsEngine _tts;
    private readonly ISttEngine _stt;
    private readonly WhisperVadProcessor _vadProcessor;
    private readonly string _memoryDbPath;
    // Separate file from _memoryDbPath (see ConversationArchive's own doc
    // comment for why) - completed-task transcripts, not durable facts.
    private readonly string _conversationArchiveDbPath;
    // Separate again (see FileEditHistory's own doc comment) - pre-edit
    // file content for revert_file_edit, not a task summary or a fact.
    private readonly string _fileEditHistoryDbPath;
    private readonly LocalEmbeddingGenerator _embeddingGenerator;
    private readonly BrowserController _browser;

    // Null when GOOGLE_CLIENT_ID/GOOGLE_CLIENT_SECRET aren't set in
    // secrets/.env - Nova still runs fine, the Gmail/Calendar tools just
    // report themselves as unavailable rather than crashing anything. Not
    // readonly (unlike _browser above) - ConnectGoogleAccountAsync can
    // populate these live, mid-run, once the overlay's credentials popup
    // finishes a successful OAuth connect. A plain reference reassignment
    // is safe to read unsynchronized from another thread (same tolerance
    // as every other overlay-polled field here) - worst case a caller sees
    // null a moment longer than necessary, never a torn object.
    private GmailClient? _gmail;
    private CalendarClient? _calendar;
    private DocsClient? _docs;
    private DriveClient? _drive;
    private SheetsClient? _sheets;
    private SlidesClient? _slides;

    // Owned here rather than by Program.cs (unlike AmbientFileWatcher/
    // TerminalWatcher, which stay composition-root-owned since nothing
    // about them changes mid-run) - GmailWatcher can only start once a
    // GmailClient exists, and that can now happen either at startup or
    // live via ConnectGoogleAccountAsync, so whichever path gets there
    // first needs to be able to start it without Program.cs's involvement.
    // See EnsureGmailWatcherStarted.
    private GmailWatcher? _gmailWatcher;

    // Same ownership reasoning as _gmailWatcher above - see EnsureCalendarWatcherStarted.
    private CalendarWatcher? _calendarWatcher;

    // Where ConnectGoogleAccountAsync persists a newly-entered Client ID/
    // Secret and caches the resulting OAuth refresh token - the same two
    // paths Program.cs already uses for the startup-time connect attempt,
    // just handed in so a live, popup-triggered connect can reuse the
    // exact same GoogleAuth.AuthorizeAsync call path.
    private readonly string _secretsEnvPath;
    private readonly string _googleTokenStoreDir;

    // Nova-adjustable settings (calendar reminder lead time, watcher on/
    // off) - see NovaSettings' own doc comment for why this is separate
    // from both secrets/.env and memory.db.
    private readonly NovaSettings _settings;

    // The overlay's Google-credentials popup state (see CredentialsPopup) -
    // same next-tick-polling shape as _currentActivity/_pendingGate2Prompt
    // above, not an event/callback, so the overlay's own DispatcherTimer
    // just reads it like everything else.
    private bool _needsGoogleCredentials;
    private bool _googleConnecting;
    private string? _googleConnectError;

    // Backs the in-flight OAuth wait inside ConnectGoogleAccountAsync so
    // Cancel can actually abort it instead of just hiding the popup while
    // the browser-consent wait keeps running forever underneath - see
    // CancelGoogleCredentials/ConnectGoogleAccountAsync.
    private CancellationTokenSource? _googleConnectCts;

    // Self-contained tools (see Tools/Dynamic/) - _selfContainedToolsDir is
    // the git-versioned source root (SelfContainedTools/), _toolRegistryDbPath
    // is the SQLite metadata store (name/description/schema/git commit/
    // approval/failure count), _toolContractDllPath is the built
    // Nova.ToolContract.dll every generated tool project references.
    private readonly string _selfContainedToolsDir;
    private readonly string _toolRegistryDbPath;
    private readonly string _toolContractDllPath;

    // Per-task consecutive-failure streak, one entry per tool name,
    // *not* persisted - "3 failures in a row for the same task" (the
    // agreed auto-revert threshold) is scoped to the current task, reset
    // whenever a new one starts (see the else branch below), not
    // accumulated across unrelated tasks days apart the way
    // ToolRegistry's own consecutive_failures column would if used alone.
    private readonly Dictionary<string, int> _toolFailuresThisTask = [];

    // Whether browser_navigate has already run once during the current task -
    // reset alongside _toolFailuresThisTask at each task boundary (see
    // ArchiveCompletedTaskAsync). A task's first navigation defaults to a
    // fresh tab rather than reusing whatever's currently selected (which
    // could be mid-use from an earlier, unrelated task) - see
    // BrowserController.NavigateAsync's defaultToNewTab parameter. Later
    // navigations within the same task go back to reusing/switching tabs
    // normally, since that's legitimate multi-step browsing within one task.
    private bool _taskHasNavigatedBrowser;

    // Whether the *current* task originated from an ambient trigger
    // (TriggerAmbientFileSuggestion/TriggerEmailAlert/TriggerTerminalSuggestion -
    // Nova noticing something and proactively wanting to say/do something
    // unprompted) rather than something the user actually asked for. This is
    // what Gate 1 is actually gated on now (see needsAuthorization in
    // ProcessTextInputAsync) - a direct request already carries its own
    // authorization in the asking, so it never needs a separate formal
    // "should I go ahead?" the way an unprompted suggestion genuinely does.
    // Set true only by the three ambient trigger methods, right before they
    // call ProcessTextInputAsync; false is the default for every other path
    // (a real utterance, an interjection, a Gate 1/Gate 2 resumption) and
    // reset per task alongside _toolFailuresThisTask/_taskHasNavigatedBrowser.
    private bool _taskIsAmbientInitiated;

    // Snapshots ToolCatalog.IsGate2/IsUnapprovedToolRun/HasExternalEffectsToolRun/
    // UsesPaidApiToolRun's verdict for each call the *moment* it comes out of RunAssistantTurnAsync - i.e. before
    // Gate 1 is ever asked, let alone waited on - keyed by call.Id (fresh
    // per Claude response, so never stale across rounds/tasks). Consulted
    // instead of re-deriving the same check later, at the actual Gate 2
    // decision point: edit_file's classification depends on live
    // File.Exists(path), and re-checking that *after* however long the
    // user took to answer Gate 1 means a file that existed at dispatch
    // time but got deleted by something else in the meantime would
    // silently downgrade from "needs a Gate 2 click" to "already covered
    // by the earlier spoken yes" - exactly the risk Gate 2 exists to
    // guard against. Reset per task alongside _toolFailuresThisTask.
    private readonly Dictionary<string, bool> _gate2NeededAtDispatch = [];

    // Launches TerminalRelay/ as a child process - takes repoRoot, which is
    // composition-root territory, so Program.cs supplies this rather than
    // NovaAssistant needing to know about repo layout.
    private readonly Func<string> _openWatchedTerminal;

    private const string GoogleNotConnectedMessage =
        "The user's Google account isn't connected. A credentials popup was just shown on the overlay (see " +
        "CredentialsPopup) - tell the user to enter their Google OAuth Client ID/Secret there. If they don't " +
        "have those yet, secrets/.env.example has the Google Cloud Console setup steps.";

    // isBusy gates the mic: 1 from the moment an utterance is dispatched for
    // transcription through the end of its spoken reply (+ cooldown).
    private int _isBusy;

    // novaSpeaking is true for the Claude-call-through-spoken-reply phase; while
    // it's true, new speech picked up by the mic is treated as a barge-in
    // instead of a normal turn.
    private int _novaSpeaking;
    private CancellationTokenSource? _turnCts;

    // What to show the overlay in place of the usual idle hint during a
    // silent tool-execution stretch - see OverlayState.CurrentActivity's
    // doc comment. Reference type, so Volatile.Read/Write (not an int
    // flag) is what keeps this safe to poll from the overlay's own thread.
    private string? _currentActivity;

    // The plain-language action description for the overlay's confirm
    // popup (see ConfirmPopup) - non-null exactly while _pendingGate2Review
    // is set. A separate field rather than deriving it from
    // _pendingGate2Review itself: that list is only ever touched from
    // inside ProcessTextInputAsync's own turn, with no synchronization,
    // same reasoning as _currentActivity above.
    private string? _pendingGate2Prompt;

    // Whisper.net's processor (like most whisper.cpp bindings) isn't safe
    // for concurrent overlapping calls on the same instance - before
    // "dynamic talking" this was never an issue, since the mic was fully
    // gated shut while busy, so only one transcription could ever be in
    // flight at a time. Now an interjection can be transcribing while
    // another utterance (a second interjection, or a full barge-in) starts
    // a second transcription - without serializing them, two overlapping
    // calls into the same native processor can hang indefinitely rather
    // than throwing, which is exactly what "stuck, nothing in the error
    // log" looks like. Every VAD/Whisper call goes through this now.
    private readonly SemaphoreSlim _transcriptionLock = new(1, 1);

    // "Dynamic talking": speech captured while a task is busy but Nova isn't
    // actively speaking (the long silent stretches of tool execution - a
    // browser read, a fill, a memory search) doesn't start a competing task
    // the way a fresh utterance would, and doesn't hard-cancel the one
    // already running the way a spoken barge-in does (see Interrupt()) -
    // it queues here and gets surfaced to the in-flight task at its own
    // next safe checkpoint (between tool-call rounds, never mid-tool-call),
    // as extra context alongside whatever it was already doing. Claude
    // decides for herself whether that means stop, change scope, answer a
    // quick question and continue, or just keep going - no separate
    // classification step, same reasoning-over-conversation-history
    // pattern the whole tool loop already uses.
    private readonly ConcurrentQueue<string> _pendingInterjections = new();

    // The *current task's* conversation history, replayed back to Claude on
    // every request (Claude only remembers what's actually resent). Used to
    // grow across the whole session and never shrink, which meant every
    // task - forever, including ones from hours earlier and totally
    // unrelated - got re-sent (and re-billed for) on every single turn.
    // Now reset at each clean task boundary (see ArchiveCompletedTaskAsync):
    // the finished task's exchange is archived to ConversationArchive and
    // replaced here with, at most, a short rolling summary folded into the
    // next task's first message (see _lastTaskSummary) - not carried
    // forward as raw history at all.
    private readonly List<MessageParam> _conversation = [];

    // A bounded, display-only log of real conversational turns (not control-
    // plane confirmations like mode switches) - the overlay's maximized
    // transcript view polls a snapshot of this. Locked (unlike the bool/enum
    // state elsewhere in this class that tolerates unsynchronized polling)
    // since a torn read of actual text content would be visibly wrong, not
    // just stale by a tick.
    private const int MaxTranscriptEntries = 100;
    private readonly List<TranscriptEntry> _transcript = [];
    private readonly Lock _transcriptLock = new();

    // The current task's own transcript, in plain "User:"/"Nova:" lines -
    // appended to by RecordTranscript alongside _transcript above (same
    // lock), but scoped to just this task and cleared at each boundary
    // (see ArchiveCompletedTaskAsync), rather than _transcript's own
    // rolling 100-entry window across every task. This is what actually
    // gets archived/summarized - a plain StringBuilder rather than trying
    // to serialize _conversation's raw SDK message types, which would be
    // both riskier to get right and less readable if Claude ever pulls it
    // back via search_conversation_history.
    private readonly StringBuilder _currentTaskLog = new();
    private DateTime _taskStartedAtUtc = DateTime.UtcNow;

    // A short Haiku-written recap of the task that just finished (see
    // ArchiveCompletedTaskAsync/SummarizeTaskAsync) - folded into the
    // *next* task's first message, then cleared, so back-to-back tasks
    // still have light continuity without carrying the full transcript
    // forward. Null once consumed or when there's nothing to carry (the
    // very first task, or one that archived with nothing worth summarizing).
    private string? _lastTaskSummary;

    // Gate 1: a task Nova has asked about but not yet been told to go ahead
    // with. Set when a turn needs an authorization; checked at the start of the
    // *next* turn instead of via a separate listening mode, so it reuses the
    // normal turn-taking loop rather than needing a new mic-routing path.
    private List<PendingToolCall>? _pendingAuthorization;

    // Gate 2: a content review for an irreversible/machine-leaving action
    // (see ToolCatalog.Gate2Tools), shown right before it actually executes.
    // Distinct from _pendingAuthorization - being Gate-1-authorized never
    // lets a Gate 2 call skip this, it's a fresh check every time, not a
    // blanket task-level yes. Same next-turn-checks-it resume pattern as
    // Gate 1, for the same reason (reuses normal turn-taking, no new
    // mic-routing mode needed).
    private List<PendingToolCall>? _pendingGate2Review;

    // Dormant (false) means Nova doesn't transcribe or react to speech at
    // all - the hotkey (TriggerReadyAcknowledgment) is the only way back to
    // engaged (true), normal respond-when-spoken-to behavior, until a sleep
    // phrase or the AFK idle timeout (see IdleTracker) puts her back to
    // sleep. A spoken sleep phrase and the overlay's sleep button both just
    // flip this one flag directly; switching ActivationMode also resets it
    // to that mode's own default (see SwitchActivationModeAsync).
    private bool _engaged = true;

    // Which of the two listening behaviors is active - see ActivationMode's
    // own doc comment. Defaults to Prompted (today's plain
    // respond-when-spoken-to baseline) so nothing changes for anyone who
    // never touches this.
    private ActivationMode _activationMode = ActivationMode.Prompted;

    public NovaAssistant(
        AnthropicClient client,
        ITtsEngine tts,
        ISttEngine stt,
        WhisperVadProcessor vadProcessor,
        string memoryDbPath,
        string conversationArchiveDbPath,
        string fileEditHistoryDbPath,
        LocalEmbeddingGenerator embeddingGenerator,
        BrowserController browser,
        GmailClient? gmail,
        CalendarClient? calendar,
        DocsClient? docs,
        DriveClient? drive,
        SheetsClient? sheets,
        SlidesClient? slides,
        string secretsEnvPath,
        string googleTokenStoreDir,
        string settingsPath,
        string selfContainedToolsDir,
        string toolRegistryDbPath,
        string toolContractDllPath,
        Func<string> openWatchedTerminal)
    {
        _client = client;
        _tts = tts;
        _stt = stt;
        _vadProcessor = vadProcessor;
        _memoryDbPath = memoryDbPath;
        _conversationArchiveDbPath = conversationArchiveDbPath;
        _fileEditHistoryDbPath = fileEditHistoryDbPath;
        _embeddingGenerator = embeddingGenerator;
        _browser = browser;
        _gmail = gmail;
        _calendar = calendar;
        _docs = docs;
        _drive = drive;
        _sheets = sheets;
        _slides = slides;
        _secretsEnvPath = secretsEnvPath;
        _googleTokenStoreDir = googleTokenStoreDir;
        _settings = new NovaSettings(settingsPath);
        _selfContainedToolsDir = selfContainedToolsDir;
        _toolRegistryDbPath = toolRegistryDbPath;
        _toolContractDllPath = toolContractDllPath;
        _openWatchedTerminal = openWatchedTerminal;

        // IsSpeaking (and so the overlay's talking animations) should
        // reflect actual audible playback, not the moment a chunk is handed
        // to the engine - synthesis has real latency, so setting this any
        // earlier would make the avatar start "talking" before Nova is
        // actually audible.
        _tts.PlaybackStarted += () => Volatile.Write(ref _novaSpeaking, 1);

        EnsureGmailWatcherStarted();
        EnsureCalendarWatcherStarted();
    }

    // Starts the inbox-watch poller the moment a GmailClient actually
    // exists - called both here (constructor, when secrets/.env already had
    // credentials at startup) and from ConnectGoogleAccountAsync's success
    // path (a live, popup-triggered connect), so a mid-run connect no
    // longer needs a restart to get proactive new-mail alerts, only
    // read/send tools used to update live. Idempotent (checked via
    // _gmailWatcher is null) since both call sites could in principle run
    // against an already-started watcher.
    private void EnsureGmailWatcherStarted()
    {
        if (_gmailWatcher is not null || _gmail is null)
        {
            return;
        }

        _gmailWatcher = new GmailWatcher(_gmail, () => !IsBusy && _settings.EmailWatcherEnabled, TriggerEmailAlert);
    }

    // Same idempotent, dual-call-site shape as EnsureGmailWatcherStarted
    // above (constructor + a live ConnectGoogleAccountAsync connect) - see
    // its own doc comment.
    private void EnsureCalendarWatcherStarted()
    {
        if (_calendarWatcher is not null || _calendar is null)
        {
            return;
        }

        _calendarWatcher = new CalendarWatcher(
            _calendar,
            () => !IsBusy && _settings.CalendarWatcherEnabled,
            () => _settings.CalendarReminderLeadMinutes,
            TriggerCalendarReminder);
    }

    public bool IsBusy => Volatile.Read(ref _isBusy) != 0;

    public bool IsSpeaking => Volatile.Read(ref _novaSpeaking) != 0;

    internal string? CurrentActivity => Volatile.Read(ref _currentActivity);

    // See _pendingGate2Prompt's own doc comment. Deliberately not folded
    // into IsBusy - a pending Gate 2 review is a real wait-on-the-user
    // state (same as Gate 1's _pendingAuthorization), not "busy" in the
    // sense IsBusy means elsewhere (see ProcessTextInputAsync's finally
    // block, which clears IsBusy the moment the Gate 2 ask is spoken).
    internal string? PendingGate2Prompt => Volatile.Read(ref _pendingGate2Prompt);

    // See _engaged's own doc comment above - the overlay uses this to
    // show/hide its asleep state.
    internal bool Engaged => _engaged;

    // See ActivationMode's own doc comment - the overlay's mode button
    // reads this to show which mode is current.
    internal ActivationMode Mode => _activationMode;

    // For the overlay's "CHROME LINKED"/"GMAIL LINKED" status chips.
    // ChromeLinked reflects a real, live connection state (flips back off
    // if the user closes the debug Chrome window); GmailLinked can now
    // also flip from false to true mid-run (see ConnectGoogleAccountAsync/
    // CredentialsPopup), though never back to false again - there's no
    // in-app disconnect, only ever a live connect.
    internal bool ChromeLinked => _browser.IsConnected;

    internal bool GmailLinked => _gmail is not null;

    // See CredentialsPopup/RequestGoogleCredentials's doc comments -
    // mirrors PendingGate2Prompt's shape (a plain field the overlay polls,
    // not an event).
    internal bool NeedsGoogleCredentials => Volatile.Read(ref _needsGoogleCredentials);

    internal bool GoogleConnecting => Volatile.Read(ref _googleConnecting);

    internal string? GoogleConnectError => Volatile.Read(ref _googleConnectError);

    // A snapshot copy (not a live reference) so the overlay's polling
    // thread never sees the list mutate mid-read.
    internal IReadOnlyList<TranscriptEntry> SnapshotTranscript()
    {
        lock (_transcriptLock)
        {
            return _transcript.ToArray();
        }
    }

    private void RecordTranscript(bool isUser, string text, bool isPending = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_transcriptLock)
        {
            // Same lock as _transcript below, not a new one - _currentTaskLog
            // is appended to from the same call sites (including the
            // background interjection-transcription path), so it needs the
            // same protection against a torn/interleaved concurrent append.
            _currentTaskLog.AppendLine($"{(isUser ? "User" : "Nova")}: {text}");

            _transcript.Add(new TranscriptEntry(isUser, text, isPending));
            if (_transcript.Count > MaxTranscriptEntries)
            {
                _transcript.RemoveAt(0);
            }
        }
    }

    // Called at the one point a task genuinely, cleanly finishes (see the
    // round loop in ProcessTextInputAsync) - archives the whole exchange
    // to ConversationArchive, then resets both the live _conversation and
    // _currentTaskLog so the next task starts from a small, cheap baseline
    // instead of carrying today's entire history forward forever. A short
    // Haiku-written summary (not the full transcript) is what actually
    // carries into the *next* task - see _lastTaskSummary; full detail
    // only comes back if Claude later calls search_conversation_history.
    //
    // Deliberately fail-safe throughout (mirrors HaikuGate's "fail closed"
    // reasoning): a summarize or archive-write hiccup logs and falls back
    // rather than throwing, since the live conversation has *already* been
    // reset by the time either of those runs - the worst case is a task
    // isn't searchable later, never a stuck/growing conversation because
    // the network or disk had a bad moment.
    private async Task ArchiveCompletedTaskAsync()
    {
        string transcriptText;
        lock (_transcriptLock)
        {
            transcriptText = _currentTaskLog.ToString().Trim();
            _currentTaskLog.Clear();
        }

        DateTime startedAt = _taskStartedAtUtc;
        _taskStartedAtUtc = DateTime.UtcNow;
        _conversation.Clear();
        // "3 failures in a row" is scoped to one task, not accumulated
        // across separate unrelated tasks - see _toolFailuresThisTask's
        // own doc comment.
        _toolFailuresThisTask.Clear();
        _taskHasNavigatedBrowser = false;
        _gate2NeededAtDispatch.Clear();
        _taskIsAmbientInitiated = false;

        if (transcriptText.Length == 0)
        {
            // Nothing actually happened worth remembering - e.g. a pending
            // review that got silently declined with no further exchange.
            _lastTaskSummary = null;
            return;
        }

        string summary;
        try
        {
            summary = await SummarizeTaskAsync(transcriptText);
        }
        catch (Exception ex)
        {
            ErrorLog.Log("ArchiveCompletedTaskAsync (summarize)", ex);
            summary = "A task ran; summary unavailable.";
        }

        _lastTaskSummary = summary;

        try
        {
            await ConversationArchive.Save(_conversationArchiveDbPath, _embeddingGenerator, startedAt, summary, transcriptText);
        }
        catch (Exception ex)
        {
            ErrorLog.Log("ArchiveCompletedTaskAsync (save)", ex);
        }
    }

    // A cheap Haiku call (same cost tier as HaikuGate) that compresses a
    // just-finished task down to one or two sentences - not cancellable by
    // the turn's own cts, since this runs *after* that task is already
    // considered done and shouldn't be cut short by an unrelated barge-in
    // racing the very end of it.
    private async Task<string> SummarizeTaskAsync(string transcriptText)
    {
        const string prompt =
            "Summarize the following just-finished task in one or two short sentences, written so they can " +
            "be silently prepended as brief context to the start of a different, later task - state what " +
            "was done or decided, not a blow-by-blow transcript. No preamble, just the summary itself.\n\n";

        MessageCreateParams parameters = new()
        {
            MaxTokens = 120,
            Model = "claude-haiku-4-5-20251001",
            Messages = [new MessageParam { Role = Role.User, Content = prompt + transcriptText }],
        };

        // UseWindowsForms=true adds a project-wide implicit `using
        // System.Windows.Forms;`, which collides with this SDK's own
        // Message type - fully qualified to disambiguate (same reasoning
        // as GmailClient.cs/HaikuGate.cs).
        Anthropic.Models.Messages.Message response = await _client.Messages.Create(parameters, CancellationToken.None);
        string text = string.Concat(response.Content.Select(block => block.TryPickText(out var textBlock) ? textBlock.Text : ""));
        return string.IsNullOrWhiteSpace(text) ? "A task ran; no summary available." : text.Trim();
    }

    // Called by AudioCapturePipeline the instant a sustained barge-in is
    // detected (talking over Nova's active speech) - silences her voice
    // immediately, nothing else. Deliberately does NOT touch _isBusy,
    // _turnCts, _currentActivity, or _pendingInterjections: cutting her off
    // mid-sentence doesn't by itself mean "stop the task" - it's very often
    // just the user adding more context or a follow-up while she's still
    // talking. The task keeps running exactly as if this had happened
    // during a silent tool-execution stretch (see _pendingInterjections'
    // doc comment); the interrupting utterance itself is captured and
    // classified once transcribed (see TranscribeAndQueueInterjectionAsync)
    // - only an utterance that actually reads as a stop request calls
    // Interrupt() below to really cancel something.
    //
    // ITtsEngine.SpeakAsync is built for exactly this split: StopPlayback
    // here (not cancelling the token) resolves its internal completion
    // signal without the token itself being cancelled, so the awaited
    // SpeakAsync call in SpeakAndWaitAsync returns normally - as if the
    // speech had simply finished - rather than throwing
    // OperationCanceledException and unwinding the rest of the turn.
    public void StopSpeaking()
    {
        _tts.StopPlayback();
        Volatile.Write(ref _novaSpeaking, 0);
    }

    // The actual hard stop - cancels whatever task is currently running.
    // Called from TranscribeAndQueueInterjectionAsync once a barge-in
    // utterance is transcribed and StopIntentClassifier confirms it's
    // genuinely asking Nova to stop, not just talking over her mid-task.
    public void Interrupt()
    {
        _tts.StopPlayback();
        _turnCts?.Cancel();
        Volatile.Write(ref _novaSpeaking, 0);
        Volatile.Write(ref _isBusy, 0);
        Volatile.Write(ref _currentActivity, null);
        // A full interrupt starts a brand new turn - any interjection still
        // sitting in the queue was meant for the task that's now being cut
        // off, not whatever comes next, so it's superseded rather than
        // carried forward.
        while (_pendingInterjections.TryDequeue(out _))
        {
        }

        Console.WriteLine("\n[interrupted]");
    }

    // Called by AudioCapturePipeline once a finished utterance clears the
    // silence/min-length checks. Real speech always wins - but the
    // ambient triggers below (GmailWatcher/AmbientFileWatcher/
    // TerminalWatcher, each on their own thread) claim _isBusy via their
    // own atomic check-and-claim, so it's possible one of them won that
    // race and started a turn in the gap between AudioCapturePipeline's own
    // last read of IsBusy and this call actually landing. Interlocked.Exchange
    // both claims busy unconditionally *and* atomically reports whether
    // something else already held it, so that case can be torn down
    // properly (same cleanup Interrupt() does) instead of letting two turns
    // run concurrently and race on _conversation.
    public void DispatchUtterance(float[] samples)
    {
        // A stray interjection queued for a just-finished task shouldn't
        // bleed into this new, unrelated one.
        while (_pendingInterjections.TryDequeue(out _))
        {
        }

        if (Interlocked.Exchange(ref _isBusy, 1) != 0)
        {
            _tts.StopPlayback();
            _turnCts?.Cancel();
            Volatile.Write(ref _novaSpeaking, 0);
            Volatile.Write(ref _currentActivity, null);
        }

        _ = ProcessUtteranceAsync(samples);
    }

    // Called by AudioCapturePipeline when speech is captured while a task is
    // busy but Nova isn't actively speaking (a silent tool-execution
    // stretch) - see _pendingInterjections' doc comment for the full
    // reasoning - and now also for a barge-in over her active speech (see
    // StopSpeaking): both land here as of the same classification, since
    // neither should hard-cancel anything by default. Doesn't touch
    // _isBusy at all; the task already holding it is what eventually
    // surfaces this.
    public void DispatchInterjection(float[] samples) => _ = TranscribeAndQueueInterjectionAsync(samples);

    private async Task TranscribeAndQueueInterjectionAsync(float[] samples)
    {
        try
        {
            string? text = await TranscribeAsync(samples);
            if (text is null)
            {
                return;
            }

            Console.WriteLine($"[interjection] You: {text}");
            RecordTranscript(isUser: true, text);

            if (!IsBusy)
            {
                return; // the task this was meant for already finished on its own
            }

            // The one case that still hard-cancels: the utterance itself
            // reads as an explicit stop request, not just added context or
            // a follow-up (see StopIntentClassifier). Checked here, after
            // transcription, rather than at the moment a barge-in is first
            // detected - by then the words are actually known.
            if (StopIntentClassifier.IsStopCommand(text))
            {
                Interrupt();
                return;
            }

            _pendingInterjections.Enqueue(text);
        }
        catch (Exception ex)
        {
            ErrorLog.Log("TranscribeAndQueueInterjectionAsync", ex);
        }
    }

    // Shared by ProcessUtteranceAsync and TranscribeAndQueueInterjectionAsync -
    // see _transcriptionLock's doc comment for why every call goes through
    // the same lock. Returns null if the RMS-triggered clip wasn't actually
    // speech (VAD rejected it) or transcribed to nothing.
    private async Task<string?> TranscribeAsync(float[] samples)
    {
        await _transcriptionLock.WaitAsync();
        try
        {
            IReadOnlyList<VadSegmentData> speechSegments = await _vadProcessor.DetectSpeechAsync(samples);
            if (speechSegments.Count == 0)
            {
                return null;
            }

            return await _stt.TranscribeAsync(samples);
        }
        finally
        {
            _transcriptionLock.Release();
        }
    }

    // Collects everything queued since the last check into one string - if
    // more than one interjection landed before the task reached a
    // checkpoint, Claude sees all of it at once rather than only the last.
    private string? DrainPendingInterjections()
    {
        if (_pendingInterjections.IsEmpty)
        {
            return null;
        }

        var parts = new List<string>();
        while (_pendingInterjections.TryDequeue(out string? text))
        {
            parts.Add(text);
        }

        return string.Join(" ", parts);
    }

    private static readonly string[] ReadyPhrases = ["I'm ready.", "Yes?", "Go ahead.", "At your service.", "What can I do?"];
    private readonly Random _random = new();

    // Triggered by HotkeyListener instead of speech - this is now the *only*
    // way to wake Nova from dormant (see _engaged's doc comment): while
    // asleep she doesn't transcribe or react to any spoken wake phrase at
    // all, so the hotkey is the sole path back to engaged. Deliberately
    // just a short local acknowledgement (like a butler answering a call),
    // not a screen-glance-and-suggest - whatever the user actually wants
    // comes from what they say next, through the normal always-listening
    // voice path, not from Nova guessing unprompted. A pending Gate 1
    // question takes priority - this isn't a valid reply to it, so this
    // no-ops rather than confusing that flow.
    public void TriggerReadyAcknowledgment()
    {
        if (IsBusy || _pendingAuthorization is not null)
        {
            Console.WriteLine("\n[hotkey: busy right now - ignored]\n");
            return;
        }

        bool wasAsleep = !_engaged;
        _engaged = true;
        string phrase = ReadyPhrases[_random.Next(ReadyPhrases.Length)];
        Console.WriteLine(wasAsleep ? $"\n[hotkey: woke up - {phrase}]" : $"\n[hotkey: {phrase}]");
        _ = SpeakLocalReplyAsync(phrase);
    }

    // Triggered by AmbientFileWatcher after its own cheap Haiku gate decided
    // a file change might be worth surfacing. Gated on _engaged - ambient
    // proactivity is off while asleep, same as everything else except email/
    // calendar (see TriggerEmailAlert). Re-checks eligibility since state
    // may have moved on since the watcher's own check.
    public void TriggerAmbientFileSuggestion(string filePath, string fileSnippet)
    {
        if (!_engaged || _pendingAuthorization is not null)
        {
            return;
        }

        // Atomic claim-if-free, not a separate IsBusy check followed by a
        // write - this runs on AmbientFileWatcher's own thread, racing
        // against the mic thread's DispatchUtterance and the other ambient
        // triggers below, all of which can fire at any moment. A plain
        // check-then-write here let two of them both see "not busy" and
        // both start a turn, corrupting the shared _conversation list.
        if (Interlocked.CompareExchange(ref _isBusy, 1, 0) != 0)
        {
            return;
        }

        _taskIsAmbientInitiated = true;
        Console.WriteLine($"\n[ambient: {filePath} flagged as possibly worth mentioning...]");
        string prompt =
            $"[Ambient trigger, not a user message - the file \"{filePath}\" was just modified, and a " +
            "quick automated check flagged it as possibly worth proactively mentioning. Current " +
            $"contents:\n\n{fileSnippet}\n\nIf there's a genuinely useful, brief thing to offer (e.g. " +
            "it looks like a finished class/function and testing it would help, or something stands " +
            "out), say it in one short sentence. Don't force it if there's nothing real to say.]";
        _ = ProcessTextInputAsync(prompt);
    }

    // Proactive inbox-watching, triggered by GmailWatcher. Not gated on
    // _engaged (unlike the ambient file trigger above) - email/calendar are
    // the deliberate exceptions to the sleep-state suppression, always
    // active regardless of awake/asleep, so this only checks IsBusy/pending
    // gates to avoid interrupting an active conversation or stepping on an
    // unrelated authorization already in flight.
    public void TriggerEmailAlert(string summary)
    {
        if (_pendingAuthorization is not null || _pendingGate2Review is not null)
        {
            return;
        }

        // Atomic claim-if-free - see TriggerAmbientFileSuggestion's comment.
        if (Interlocked.CompareExchange(ref _isBusy, 1, 0) != 0)
        {
            return;
        }

        _taskIsAmbientInitiated = true;
        Console.WriteLine($"\n[email alert: {summary}]");
        string prompt = $"[Ambient trigger, not a user message - {summary} Mention it to the user briefly in one short sentence.]";
        _ = ProcessTextInputAsync(prompt);
    }

    // Proactive calendar-reminder alert, triggered by CalendarWatcher - same
    // "email/calendar are exceptions to the sleep-state suppression"
    // reasoning as TriggerEmailAlert above, so this only checks IsBusy/
    // pending gates too (CalendarWatcher's own eligibility check already
    // covers the NovaSettings.CalendarWatcherEnabled toggle).
    public void TriggerCalendarReminder(string summary)
    {
        if (_pendingAuthorization is not null || _pendingGate2Review is not null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _isBusy, 1, 0) != 0)
        {
            return;
        }

        _taskIsAmbientInitiated = true;
        Console.WriteLine($"\n[calendar reminder: {summary}]");
        string prompt = $"[Ambient trigger, not a user message - upcoming calendar event: {summary} Mention it to the user briefly in one short sentence.]";
        _ = ProcessTextInputAsync(prompt);
    }

    // Triggered by TerminalWatcher after its own Haiku gate decided a chunk
    // of watched-terminal output might be worth surfacing - same grouping
    // (and same _engaged gating) as the ambient file trigger above, since
    // the roadmap treats file watching and terminal watching as the same
    // "ambient triggers" mechanism.
    public void TriggerTerminalSuggestion(string outputSnippet)
    {
        if (!_engaged || _pendingAuthorization is not null || _pendingGate2Review is not null)
        {
            return;
        }

        // Atomic claim-if-free - see TriggerAmbientFileSuggestion's comment.
        if (Interlocked.CompareExchange(ref _isBusy, 1, 0) != 0)
        {
            return;
        }

        _taskIsAmbientInitiated = true;
        Console.WriteLine("\n[ambient: terminal output flagged as possibly worth mentioning...]");
        string prompt =
            "[Ambient trigger, not a user message - recent output from the watched terminal was flagged " +
            $"as possibly worth proactively mentioning:\n\n{outputSnippet}\n\nIf there's a genuinely " +
            "useful, brief thing to offer (e.g. a build or test run finished, especially if it failed), " +
            "say it in one short sentence. Don't force it if there's nothing real to say.]";
        _ = ProcessTextInputAsync(prompt);
    }

    private async Task ProcessUtteranceAsync(float[] samples)
    {
        // Asleep: the hotkey is the only way back to engaged (see
        // TriggerReadyAcknowledgment) - no spoken wake phrase anymore, so
        // there's no reason to pay for a Whisper transcription just to
        // throw it away. VAD/dispatch upstream still runs (cheap, local),
        // this is just where the actual "reacting to audio" stops.
        if (!_engaged)
        {
            Volatile.Write(ref _isBusy, 0);
            return;
        }

        // Immediate feedback that something is happening - otherwise there's
        // total silence for the entire VAD-confirm + transcription pipeline
        // before any text shows up at all.
        Console.WriteLine("[transcribing...]");
        Volatile.Write(ref _currentActivity, "transcribing");

        string? input;
        try
        {
            input = await TranscribeAsync(samples);
        }
        catch (Exception ex)
        {
            // A transcription-engine failure (a bad STT backend, a native
            // exception) must never leave _isBusy stuck - without this,
            // Nova silently stops responding to *everything* from this
            // point on, with no error spoken and no way back short of a
            // restart. Real, not hypothetical - confirmed live while
            // testing an experimental STT engine swap.
            ErrorLog.Log("ProcessUtteranceAsync (transcription)", ex);
            Volatile.Write(ref _isBusy, 0);
            Volatile.Write(ref _currentActivity, null);
            await TryAnnounceUnhandledErrorAsync();
            return;
        }

        if (input is null)
        {
            // Distinct from the garbled-but-non-null case (a nonsense
            // transcript still reaches the LLM, which naturally asks for
            // clarification on its own) - this is the STT engine or its VAD
            // confirmation rejecting the clip outright, most often a short
            // or quiet utterance (confirmed live via AudioCapturePipeline's
            // onset-profile diagnostic: a rejected utterance's real speech
            // energy was a brief, quiet burst well below other utterances'
            // peak levels). Previously silent here - no spoken feedback at
            // all, so there was no way to tell "she heard nothing" apart
            // from "she's ignoring me."
            Volatile.Write(ref _isBusy, 0);
            Volatile.Write(ref _currentActivity, null);
            await SpeakLocalReplyAsync("Sorry, I didn't catch that - could you say it again?");
            return;
        }

        Console.WriteLine($"You: {input}");
        RecordTranscript(isUser: true, input);
        Volatile.Write(ref _currentActivity, null); // transcribing's done - the tool loop sets its own activity from here

        // Mode-switch and the sleep phrase are both checked locally, before
        // anything reaches Claude - keeps them free and instant rather than
        // a real conversational turn. Mode-switch first: "switch to key
        // bind mode" shouldn't also get evaluated as a sleep phrase (it
        // isn't one, but checking order is what guarantees that stays true
        // as phrasing evolves).
        if (VoiceControlPhrases.TryDetectModeSwitch(input) is { } newMode)
        {
            await SwitchActivationModeAsync(newMode);
            Volatile.Write(ref _isBusy, 0);
            return;
        }

        if (VoiceControlPhrases.ContainsSleepPhrase(input))
        {
            _engaged = false;
            Console.WriteLine("[going dormant]");
            await SpeakLocalReplyAsync("Okay, taking a break.");
            Volatile.Write(ref _isBusy, 0);
            return;
        }

        await ProcessTextInputAsync(input);
    }

    // internal (not private) so the overlay's mode button can call the exact
    // same state-mutation + TTS-confirmation logic a voice-triggered switch
    // uses, rather than duplicating it. Sets IsBusy for its own duration
    // (unlike its only other caller, ProcessUtteranceAsync, which already
    // runs under DispatchUtterance's IsBusy=1) so the overlay button path -
    // which has no such wrapper - can't fire a second concurrent switch
    // while the first is still speaking its confirmation; without this, two
    // clicks (or a click landing mid-speech) would race on _turnCts and
    // cause overlapping/doubled TTS playback.
    internal async Task SwitchActivationModeAsync(ActivationMode mode)
    {
        Volatile.Write(ref _isBusy, 1);
        try
        {
            if (mode == _activationMode)
            {
                await SpeakLocalReplyAsync($"Already in {DescribeMode(mode)} mode.");
                return;
            }

            _activationMode = mode;
            // Switching mode resets to that mode's own default - Prompted
            // starts engaged, KeyBind starts asleep (see ActivationMode's
            // own doc comment). This is itself an active interaction either
            // way, so the confirmation always gets spoken regardless of
            // which direction _engaged just moved.
            _engaged = mode == ActivationMode.Prompted;
            Console.WriteLine($"\n[activation mode -> {mode}]\n");
            await SpeakLocalReplyAsync($"Switched to {DescribeMode(mode)} mode.");
        }
        finally
        {
            Volatile.Write(ref _isBusy, 0);
        }
    }

    private static string DescribeMode(ActivationMode mode) => mode switch
    {
        ActivationMode.Prompted => "prompted",
        ActivationMode.KeyBind => "key bind",
        _ => mode.ToString(),
    };

    // The overlay's sleep/wake button - the manual equivalent of the sleep
    // phrase handling in ProcessUtteranceAsync above (waking is handled
    // separately - see TriggerReadyAcknowledgment, the hotkey being the only
    // path back from asleep). Deliberately synchronous and silent (no TTS
    // confirmation): a button click already gives the user immediate visual
    // feedback, so speaking "Okay, taking a break." here would just be
    // redundant noise the user didn't ask for - a real divergence from the
    // voice path's behavior, not an oversight.
    internal void SetEngaged(bool engaged)
    {
        if (engaged == _engaged)
        {
            return;
        }

        _engaged = engaged;
        Console.WriteLine(engaged ? "\n[woke up (overlay)]\n" : "\n[going dormant (overlay)]\n");
    }

    // The overlay's Gate 2 confirm popup (see ConfirmPopup) - the *only*
    // way a Gate 2 review can actually be approved. See the Gate 2 block in
    // ProcessTextInputAsync: voice arriving while a review is pending no
    // longer resolves it either way, even if it sounds affirmative, so a
    // misheard word can't authorize an irreversible action on its own. The
    // null-check guards a stale click racing a review that already
    // resolved some other way (e.g. Interrupt firing right before the
    // click lands) - _pendingGate2Review is read unsynchronized here, same
    // tolerance as every other cross-thread overlay-polled field.
    public void ConfirmGate2Review(bool approved)
    {
        if (_pendingGate2Review is null)
        {
            return;
        }

        _ = ProcessTextInputAsync(string.Empty, approved);
    }

    // Surfaces the overlay's Google-credentials popup (see
    // CredentialsPopup) the first time a Google tool (search_email,
    // send_email, list/create_calendar_event) is actually attempted with
    // no account connected - called from the 4 "_gmail/_calendar is null"
    // branches in ExecuteToolAsync below. Guarded so a task that retries
    // or calls more than one Google tool in the same turn doesn't
    // re-trigger the popup while it's already up or mid-connect.
    private void RequestGoogleCredentials()
    {
        if (Volatile.Read(ref _needsGoogleCredentials) || Volatile.Read(ref _googleConnecting))
        {
            return;
        }

        Volatile.Write(ref _googleConnectError, null);
        Volatile.Write(ref _needsGoogleCredentials, true);
    }

    // Shared by every Google-tool dispatch case in ExecuteToolAsync below -
    // previously each of the 12 cases repeated the same 5-line
    // "if (_x is null) { RequestGoogleCredentials(); return (..., true); }"
    // shape by hand (doubled again by this session's own Sheets/Slides
    // additions), so a change to the not-connected behavior needed
    // touching all 12 instead of one. `is not { } x` reads as "null ->
    // request credentials and fall through to the not-connected message;
    // non-null -> bind it to x and keep going" at each call site.
    private T? RequireGoogleClient<T>(T? client) where T : class
    {
        if (client is null)
        {
            RequestGoogleCredentials();
        }

        return client;
    }

    // The credentials popup's Cancel button - dismisses the prompt and, if
    // a Connect click already kicked off the OAuth wait, actually aborts
    // it too (see ConnectGoogleAccountAsync) - without this, an abandoned
    // browser-consent wait would keep _googleConnecting stuck true forever,
    // and RequestGoogleCredentials refuses to show the popup again while
    // that's the case, permanently locking out every Google tool. A later
    // Google tool attempt can trigger the popup again once this settles.
    public void CancelGoogleCredentials()
    {
        Volatile.Write(ref _needsGoogleCredentials, false);
        _googleConnectCts?.Cancel();
    }

    // The credentials popup's Connect button. Runs on the overlay's UI
    // thread - kicks off the actual OAuth handshake in the background so
    // the click handler itself returns immediately (the handshake opens
    // the system browser and waits on the user, which can take anywhere
    // from a few seconds to however long they take to sign in).
    public void ConnectGoogleAccount(string clientId, string clientSecret)
    {
        if (Volatile.Read(ref _googleConnecting))
        {
            return;
        }

        _ = ConnectGoogleAccountAsync((clientId ?? "").Trim(), (clientSecret ?? "").Trim());
    }

    // Runs the exact same OAuth "installed app" flow GoogleAuth.AuthorizeAsync
    // already used at startup for a config-file-provided Client ID/Secret -
    // just triggered live from the popup instead. Progress/failure is
    // reported back purely through NeedsGoogleCredentials/GoogleConnecting/
    // GoogleConnectError (the same state-polling shape CredentialsPopup
    // already reads every ~130ms tick), not a return value or an event,
    // since the caller (a Button.Click handler) can't usefully await this.
    //
    // Security: clientSecret is never written to Console/ErrorLog anywhere
    // in this method or anything it calls - only to secrets/.env (see
    // EnvLoader.SetValues, which preserves every other line already in
    // that file, e.g. ANTHROPIC_API_KEY) and to Google's own OAuth
    // endpoints via GoogleAuth.AuthorizeAsync. That's the same trust level
    // ANTHROPIC_API_KEY already gets in the same file - no stricter model
    // invented for just this one secret.
    private async Task ConnectGoogleAccountAsync(string clientId, string clientSecret)
    {
        if (clientId.Length == 0 || clientSecret.Length == 0)
        {
            Volatile.Write(ref _googleConnectError, "Both fields are required.");
            return;
        }

        Volatile.Write(ref _googleConnecting, true);
        Volatile.Write(ref _googleConnectError, null);
        var connectCts = new CancellationTokenSource();
        _googleConnectCts = connectCts;
        try
        {
            UserCredential credential = await GoogleAuth.AuthorizeAsync(clientId, clientSecret, _googleTokenStoreDir, connectCts.Token);
            _gmail = new GmailClient(credential);
            _calendar = new CalendarClient(credential);
            _docs = new DocsClient(credential);
            _drive = new DriveClient(credential);
            _sheets = new SheetsClient(credential);
            _slides = new SlidesClient(credential);
            EnsureGmailWatcherStarted();
            EnsureCalendarWatcherStarted();

            EnvLoader.SetValues(_secretsEnvPath, new Dictionary<string, string>
            {
                ["GOOGLE_CLIENT_ID"] = clientId,
                ["GOOGLE_CLIENT_SECRET"] = clientSecret,
            });

            Volatile.Write(ref _needsGoogleCredentials, false);
            Console.WriteLine("\n[Google account connected via overlay]\n");

            // This can land while Nova is otherwise idle, and
            // wrapping just the spoken confirmation (not the OAuth wait
            // itself) keeps a stray second click from racing it.
            Volatile.Write(ref _isBusy, 1);
            try
            {
                await SpeakLocalReplyAsync("Your Google account is connected now.");
            }
            finally
            {
                Volatile.Write(ref _isBusy, 0);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancel button (see CancelGoogleCredentials) - the user
            // already dismissed the popup, so there's nothing to report
            // back; just let the wait actually stop instead of leaving
            // _googleConnecting stuck true underneath a closed popup.
            Console.WriteLine("\n[Google connect cancelled]\n");
        }
        catch (Exception ex)
        {
            // Broad on purpose - this can fail for reasons ranging from a
            // typo'd Client ID to the user closing the consent browser tab
            // without approving; none of those should crash anything, they
            // should just let the popup report an error and stay open for
            // another try.
            ErrorLog.Log("ConnectGoogleAccountAsync", ex);
            Volatile.Write(ref _googleConnectError, "Couldn't connect - check the Client ID/Secret and try again.");
        }
        finally
        {
            Volatile.Write(ref _googleConnecting, false);
            if (ReferenceEquals(_googleConnectCts, connectCts))
            {
                _googleConnectCts = null;
            }

            connectCts.Dispose();
        }
    }

    // A short local acknowledgement (wake/sleep/mode-switch confirmations) -
    // deliberately not added to `_conversation`, since these are control-plane
    // interactions Claude never needs to see, not real conversational
    // content. Still goes through a real (cancellable) turnCts so a barge-in
    // works on these the same as anything else Nova says.
    // General error self-healing surfacing - a turn-level failure with no
    // safe fallback to revert to (a Claude API hiccup, a permission error)
    // used to fail completely silently from the user's perspective: logged,
    // but never spoken, so the console was the only way to notice anything
    // went wrong. This turns it into something the user can actually act
    // on - a brief local notice now, with read_recent_errors available on
    // a later turn if they ask to look into it. Deliberately not an
    // interactive "look or ignore" menu right here - chaining more logic
    // onto a pipeline that just failed risks a second failure compounding
    // the first; "ignore" is simply the default if nothing follows up.
    // Wrapped in its own try/catch since SpeakLocalReplyAsync only
    // swallows OperationCanceledException - if TTS itself is what's
    // broken, this shouldn't throw a second unhandled exception out of the
    // catch block that called it.
    private async Task TryAnnounceUnhandledErrorAsync()
    {
        try
        {
            await SpeakLocalReplyAsync("Sorry, something went wrong there - I've logged it. Let me know if you'd like me to look into it.");
        }
        catch
        {
            // Best-effort - see this method's own doc comment.
        }
    }

    private async Task SpeakLocalReplyAsync(string text)
    {
        var cts = new CancellationTokenSource();
        _turnCts = cts;
        try
        {
            await SpeakAndWaitAsync(text, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Barge-in - fine, this was just a short local acknowledgement.
        }
        finally
        {
            if (ReferenceEquals(_turnCts, cts))
            {
                _turnCts = null;
            }
        }
    }

    // Shared turn-processing core for a transcribed utterance and an
    // ambient-trigger synthetic prompt (see TriggerAmbientFileSuggestion) -
    // the tool-call loop and conversation history don't care where `input`
    // came from. Gate 1 is the one exception: it's gated on
    // _taskIsAmbientInitiated (set by the three ambient trigger methods
    // right before they call this), since that's specifically what
    // distinguishes "Nova wants to do something unprompted" (needs a real
    // ask) from "the user already asked for this" (the ask itself already
    // is the authorization - see needsAuthorization below). Gate 2 is
    // unaffected by origin either way. forcedGate2Decision is set only
    // by ConfirmGate2Review (the overlay popup's Confirm/Cancel click) -
    // input is unused in that case, see the Gate 2 branch below.
    private async Task ProcessTextInputAsync(string input, bool? forcedGate2Decision = null)
    {
        var cts = new CancellationTokenSource();
        _turnCts = cts;

        try
        {
            // Not a clear yes - the pending tool_use call(s) still need a
            // tool_result, or the conversation becomes permanently invalid
            // (the API rejects every subsequent request) since Claude is
            // left waiting on a call that never got resolved. Decline them
            // in the same message as the new input, since Claude/Anthropic
            // requires the tool_result to come immediately after the
            // tool_use turn - it can't be a separate later message. Shared
            // between Gate 1 and Gate 2 resolution below - identical shape,
            // just which pending field it clears differs.
            (bool Authorized, List<PendingToolCall>? Calls) ResolvePending(List<PendingToolCall> pendingCalls)
            {
                if (TextHeuristics.LooksAffirmative(input))
                {
                    return (true, pendingCalls); // conversation already ends with the
                                                  // assistant's tool_use turn from before
                }

                List<ContentBlockParam> declineContent = pendingCalls
                    .Select(call => (ContentBlockParam)new ToolResultBlockParam(call.Id) { Content = "User did not authorize this action.", IsError = true })
                    .ToList();
                declineContent.Add(new TextBlockParam(input));
                _conversation.Add(new MessageParam { Role = Role.User, Content = declineContent });
                return (false, null);
            }

            List<PendingToolCall>? callsToExecuteNow = null;
            bool taskAuthorized = false;
            bool gate2JustConfirmed = false;

            if (_pendingGate2Review is { } pendingGate2Calls)
            {
                _pendingGate2Review = null;
                Volatile.Write(ref _pendingGate2Prompt, null);

                if (forcedGate2Decision is { } decision)
                {
                    // The popup's Confirm/Cancel click - the only way this
                    // ever resolves as approved. See ConfirmGate2Review.
                    if (decision)
                    {
                        taskAuthorized = true;
                        callsToExecuteNow = pendingGate2Calls;
                        gate2JustConfirmed = true;

                        // Approval is tied to the click itself, not to
                        // whether the run that follows actually succeeds -
                        // a first run that fails is still an approved
                        // tool (it just failed), so a retry within the
                        // same task doesn't re-trigger this review.
                        foreach (PendingToolCall call in pendingGate2Calls.Where(c => c.Name == "run_tool"))
                        {
                            string? toolName = ToolInput.GetString(call.Input, "name");
                            if (toolName is not null)
                            {
                                ToolRegistry.MarkApproved(_toolRegistryDbPath, toolName);
                            }
                        }
                    }
                    else
                    {
                        List<ContentBlockParam> declineContent = pendingGate2Calls
                            .Select(call => (ContentBlockParam)new ToolResultBlockParam(call.Id) { Content = "User declined this action via the confirmation popup.", IsError = true })
                            .ToList();
                        _conversation.Add(new MessageParam { Role = Role.User, Content = declineContent });
                    }
                }
                else
                {
                    // Real voice/text arrived instead of a popup click -
                    // never treated as a confirmation here, even if it
                    // sounds affirmative (e.g. a misheard "yes"): Gate 2 is
                    // deliberately click-only (see ConfirmPopup) so an STT
                    // slip can't authorize an irreversible action. Same
                    // decline-and-carry-forward shape as ResolvePending's
                    // non-affirmative branch below, just unconditional here.
                    List<ContentBlockParam> declineContent = pendingGate2Calls
                        .Select(call => (ContentBlockParam)new ToolResultBlockParam(call.Id) { Content = "User did not authorize this action.", IsError = true })
                        .ToList();
                    declineContent.Add(new TextBlockParam(input));
                    _conversation.Add(new MessageParam { Role = Role.User, Content = declineContent });
                }
            }
            else if (_pendingAuthorization is { } pendingCalls)
            {
                _pendingAuthorization = null;
                (taskAuthorized, callsToExecuteNow) = ResolvePending(pendingCalls);
            }
            else
            {
                // _lastTaskSummary carries a short Haiku-written recap of
                // the previous task forward (see ArchiveCompletedTaskAsync)
                // instead of that task's full transcript, which is what
                // keeps _conversation from growing without bound across a
                // long-running session - the full detail is still there if
                // ever needed, just in ConversationArchive via
                // search_conversation_history, not resent on every turn.
                string content = _lastTaskSummary is { } summary
                    ? $"[Context carried over from the task you just finished: {summary}]\n\n{input}"
                    : input;
                _conversation.Add(new MessageParam { Role = Role.User, Content = content });
                _lastTaskSummary = null;
            }

            for (int round = 0; round < MaxToolRounds; round++)
            {
                List<PendingToolCall> pendingToolCalls;
                bool skipGate2Review = false;
                if (callsToExecuteNow is not null)
                {
                    pendingToolCalls = callsToExecuteNow;
                    callsToExecuteNow = null;
                    skipGate2Review = gate2JustConfirmed;
                    gate2JustConfirmed = false;
                }
                else
                {
                    pendingToolCalls = await RunAssistantTurnAsync(cts.Token);
                    // Snapshot Gate 2 status right now, before Gate 1 is
                    // even asked - see _gate2NeededAtDispatch's own doc
                    // comment for why this can't wait until the actual
                    // Gate 2 check further down.
                    foreach (PendingToolCall call in pendingToolCalls)
                    {
                        _gate2NeededAtDispatch[call.Id] = ToolCatalog.IsGate2(call) || IsUnapprovedToolRun(call) || HasExternalEffectsToolRun(call) || UsesPaidApiToolRun(call);
                    }
                }

                if (pendingToolCalls.Count == 0)
                {
                    // She was about to give her final reply for this task -
                    // but if something arrived while she was working on it,
                    // give her a chance to actually address it (the task
                    // might not really be done) instead of ending on a
                    // reply that's now stale.
                    string? closingInterjection = DrainPendingInterjections();
                    if (closingInterjection is null)
                    {
                        // The one point a task genuinely, cleanly finishes -
                        // archive the whole exchange and reset the live
                        // conversation here rather than letting it grow
                        // forever (see ArchiveCompletedTaskAsync). Not
                        // cancelled by cts - a stray barge-in racing the
                        // very end of a reply shouldn't be able to cut this
                        // off and leave the archive/reset half-done.
                        await ArchiveCompletedTaskAsync();
                        break; // final text-only reply, already spoken
                    }

                    _conversation.Add(new MessageParam
                    {
                        Role = Role.User,
                        Content = $"[The user just said, while you were working: \"{closingInterjection}\"]",
                    });
                    continue;
                }

                List<PendingToolCall> gatedCalls = pendingToolCalls.Where(call => !ToolCatalog.IsFree(call)).ToList();
                // Gate 1 only fires for a task Nova started on her own
                // initiative (_taskIsAmbientInitiated) - a direct request
                // already carries its own authorization in the asking, so
                // stopping to ask "should I go ahead?" right after being
                // told to do exactly that is the redundant, robotic-sounding
                // friction this was redesigned to remove. Gate 2 below is
                // completely unaffected either way - it still independently
                // reviews the genuinely irreversible step regardless of how
                // (or whether) Gate 1 fired for this task.
                bool needsAuthorization = !taskAuthorized && gatedCalls.Count > 0 && _taskIsAmbientInitiated;
                if (needsAuthorization)
                {
                    // Set before any cancellable await below (SpeakAndWaitAsync in
                    // particular) - a barge-in mid-question would otherwise cancel
                    // out before this assignment ran, leaving the tool_use blocks
                    // that RunAssistantTurnAsync already committed to `_conversation`
                    // permanently unresolved (pendingAuthorization null next turn
                    // means nothing sends their tool_result, and the API rejects
                    // every request afterward).
                    _pendingAuthorization = pendingToolCalls;

                    // edit_file's own diff view isn't shown here anymore - an
                    // existing-file overwrite is always *also* Gate 2 (see
                    // ToolCatalog.IsGate2/IsFree: for edit_file specifically,
                    // "free" and "Gate 2" are exact complements - a call
                    // never reaches gatedCalls for this tool without also
                    // being in gate2Calls below), so showing it once there,
                    // right before the actual click, covers every case this
                    // used to cover here, without depending on Gate 1 having
                    // fired at all for this task.
                    string ask = ToolDescriptions.DescribePendingTask(gatedCalls);

                    // Field/typed values aren't spoken aloud (could be anything) - print
                    // them so there's still something concrete to review.
                    List<PendingToolCall> fillCalls = gatedCalls
                        .Where(call => call.Name == "browser_fill" || (call.Name == "interact_desktop" && ToolInput.GetString(call.Input, "action") == "type"))
                        .ToList();
                    if (fillCalls.Count > 0)
                    {
                        Console.WriteLine("\n--- proposed field values ---");
                        foreach (PendingToolCall call in fillCalls)
                        {
                            Console.WriteLine($"{ToolInput.GetString(call.Input, "label")}: {ToolInput.GetString(call.Input, "value")}");
                        }

                        Console.WriteLine("--- end of fields ---");
                        ask += " I've printed the field values to the console.";
                    }

                    // The spoken ask uses run_command's natural-language description, never the
                    // raw command (see ToolCatalog) - keep the actual command in the logs anyway.
                    foreach (PendingToolCall call in gatedCalls.Where(call => call.Name == "run_command"))
                    {
                        Console.WriteLine($"[command: {ToolInput.GetString(call.Input, "command")}]");
                    }

                    await SpeakAndWaitAsync(ask, cts.Token);
                    Console.WriteLine($"\n[awaiting authorization: {ask}]\n");
                    RecordTranscript(isUser: false, ask, isPending: true);
                    break;
                }

                // Gate 2: a content review for irreversible/machine-leaving calls
                // (see ToolCatalog.Gate2Tools), required every time regardless of
                // taskAuthorized - Gate 1's blanket yes covers getting the task
                // done, but never covers this specific final step on its own.
                // Skipped only for the one round replaying calls that were *just*
                // confirmed at Gate 2 (see skipGate2Review above).
                // run_tool joins Gate 2 only for a tool that's never been
                // approved before - reusing an already-approved tool again
                // doesn't need re-review (same "advancing to something new
                // needs approval, retreating/repeating doesn't" asymmetry
                // the roadmap already applies to skill reverts).
                // Uses the snapshot taken the moment these calls were first
                // dispatched (see _gate2NeededAtDispatch), not a fresh
                // re-check here - the fresh check is what used to create a
                // TOCTOU window across however long Gate 1's own wait took.
                List<PendingToolCall> gate2Calls = pendingToolCalls
                    .Where(call => _gate2NeededAtDispatch.GetValueOrDefault(call.Id))
                    .ToList();
                if (!skipGate2Review && gate2Calls.Count > 0)
                {
                    // Same reasoning as Gate 1's assignment above - set before the
                    // cancellable SpeakAndWaitAsync so a barge-in mid-question can't
                    // leave these tool_use blocks permanently unresolved.
                    _pendingGate2Review = pendingToolCalls;
                    // Paid-API status folded straight into the description
                    // text handed to DescribeGate2Review/Action - this is
                    // the point real cost actually starts (build_tool
                    // itself just compiles code), so the first-run review
                    // is where that needs to be disclosed, not a separate
                    // gate of its own.
                    (string? Description, bool AlreadyApproved) ToolDescriptionLookup(string toolName)
                    {
                        ToolRecord? record = ToolRegistry.Find(_toolRegistryDbPath, toolName);
                        if (record is null)
                        {
                            return (null, false);
                        }

                        string description = record.UsesPaidApi ? $"{record.Description} (uses a paid API - each use may cost money)" : record.Description;
                        return (description, record.Approved);
                    }
                    string gate2Ask = ToolDescriptions.DescribeGate2Review(gate2Calls, ToolDescriptionLookup);
                    // Popup body text - same plain-language action
                    // description as the spoken ask, minus the spoken-only
                    // "confirm on the popup" framing (the popup's own
                    // Confirm/Cancel buttons already ask that visually).
                    string gate2PopupText = ToolDescriptions.DescribeGate2Action(gate2Calls, ToolDescriptionLookup);

                    // edit_file has no undo/backup for the *original* content
                    // beyond FileEditHistory's own record, so an overwrite is
                    // effectively irreversible without it - show a real diff
                    // so there's something to actually review before the
                    // click, not just a text description. Was previously
                    // shown during Gate 1's own ask; moved here since that no
                    // longer unconditionally fires, and every edit_file call
                    // that needs review reaches gate2Calls regardless (see
                    // the comment at the Gate 1 block above).
                    List<PendingToolCall> editCalls = gate2Calls.Where(call => call.Name == "edit_file").ToList();
                    if (editCalls.Count > 0)
                    {
                        bool openedDiffView = false;
                        foreach (PendingToolCall call in editCalls)
                        {
                            openedDiffView |= DiffViewer.ShowEditDiff(call.Input);
                        }

                        string diffNote = openedDiffView
                            ? " I've opened the changes in a diff view."
                            : " I've printed the proposed changes to the console.";
                        gate2Ask += diffNote;
                        gate2PopupText += diffNote;
                    }

                    // The full email body isn't spoken aloud (could be long or
                    // sensitive) - printed to console so there's still something
                    // concrete to review, same pattern as browser_fill/run_command above.
                    List<PendingToolCall> emailCalls = gate2Calls.Where(call => call.Name == "send_email").ToList();
                    if (emailCalls.Count > 0)
                    {
                        Console.WriteLine("\n--- proposed email ---");
                        foreach (PendingToolCall call in emailCalls)
                        {
                            Console.WriteLine($"To: {ToolInput.GetString(call.Input, "to")}");
                            Console.WriteLine($"Subject: {ToolInput.GetString(call.Input, "subject")}");
                            Console.WriteLine($"Body:\n{ToolInput.GetString(call.Input, "body")}");
                        }

                        Console.WriteLine("--- end of email ---");
                        gate2Ask += " I've printed the full email to the console.";
                        gate2PopupText += " (Full email printed to console.)";
                    }

                    Volatile.Write(ref _pendingGate2Prompt, gate2PopupText);
                    await SpeakAndWaitAsync(gate2Ask, cts.Token);
                    Console.WriteLine($"\n[awaiting Gate 2 review: {gate2Ask}]\n");
                    RecordTranscript(isUser: false, gate2Ask, isPending: true);
                    break;
                }

                // taskAuthorized is NOT set here - reaching this point can mean either a
                // genuinely authorized gated call, or a round that only had free read_file
                // calls, and re-setting it in the latter case would wrongly pre-authorize
                // a *later* round's gated call in the same task without ever asking.
                var results = new List<ContentBlockParam>();
                foreach (PendingToolCall call in pendingToolCalls)
                {
                    // Checked explicitly rather than only relying on
                    // ExecuteToolAsync's own awaited calls to notice
                    // cancellation - several tool methods (ReadFile,
                    // ListFiles, DesktopInteraction.*, etc.) are plain
                    // synchronous code with nothing to observe a token
                    // against, so a fast one finishing "successfully" right
                    // after an explicit stop request landed would otherwise
                    // let the loop start yet another call before the
                    // cancellation ever surfaces as an exception.
                    cts.Token.ThrowIfCancellationRequested();

                    string status = ToolDescriptions.DescribeToolStatus(call);
                    Console.WriteLine($"[{status}...]");
                    // Same text as the console line above, surfaced to the
                    // overlay too - see OverlayState.CurrentActivity. Left
                    // in place (not cleared) once this round's tools finish -
                    // several tool-only rounds in a row (retries, multi-step
                    // reads) should keep showing the last real activity
                    // instead of flickering back to the idle hint between
                    // every single round; it only actually goes away once
                    // she starts speaking (the overlay already hides it
                    // then) or the task ends.
                    Volatile.Write(ref _currentActivity, status);
                    (string content, bool isError) = await ExecuteToolAsync(call.Name, call.Input, cts.Token);
                    results.Add(new ToolResultBlockParam(call.Id) { Content = content, IsError = isError });
                }

                // Safe checkpoint - between rounds, never mid-tool-call. If
                // something was said while these tools were running, fold it
                // in alongside the results so the next round's reasoning
                // sees both at once, the same way ResolvePending above
                // combines a tool_result with fresh user text.
                string? interjection = DrainPendingInterjections();
                if (interjection is not null)
                {
                    results.Add(new TextBlockParam($"[The user just said, while you were working: \"{interjection}\"]"));
                }

                _conversation.Add(new MessageParam { Role = Role.User, Content = results });
            }
        }
        catch (OperationCanceledException)
        {
            // Barge-in - a newer utterance already took over.
        }
        catch (AnthropicApiException ex)
        {
            Console.WriteLine($"\n[API error: {ex.Message}]\n");
            await TryAnnounceUnhandledErrorAsync();
        }
        catch (Exception ex)
        {
            // Broader than the API/cancellation cases above on purpose - the
            // TTS engine can now fail in new ways (sidecar not running, HTTP
            // errors) that would otherwise leave isBusy stuck forever since
            // the isBusy-reset below never runs if this method exits via an
            // uncaught exception from its fire-and-forget caller.
            ErrorLog.Log("ProcessTextInputAsync", ex);
            Console.WriteLine($"\n[error: {ex.Message}]\n");
            await TryAnnounceUnhandledErrorAsync();
        }
        finally
        {
            if (ReferenceEquals(_turnCts, cts))
            {
                _turnCts = null;
            }
        }

        if (cts.IsCancellationRequested)
        {
            return; // the newer turn already owns isBusy - don't clobber it
        }

        await Task.Delay(SpeechCooldownMs);
        Volatile.Write(ref _isBusy, 0);
        Volatile.Write(ref _currentActivity, null);
    }

    // Runs one Claude turn (streamed, tools enabled), speaking any text as it
    // arrives and appending the assistant's reply to `_conversation`. Returns the
    // tool calls (if any) the turn ended with, so the caller can decide whether
    // to execute them immediately or pause for Gate 1 authorization first.
    //
    // A barge-in cancels this mid-flight, so the reply-recording is wrapped in a
    // try/finally: whatever was said so far - including a text block that never
    // got a formal content_block_stop because the stream was cut off - still
    // gets saved to `_conversation`. Otherwise an interrupted reply vanishes from
    // history entirely and the next turn lands on a broken, gappy conversation.
    private async Task<List<PendingToolCall>> RunAssistantTurnAsync(CancellationToken cancellationToken)
    {
        MessageCreateParams parameters = new()
        {
            MaxTokens = 2048,
            Model = "claude-sonnet-5",
            System = SystemPrompt.Text,
            Tools = ToolCatalog.Tools,
            Messages = _conversation,
            // Marks the last cacheable block (system prompt + tool schemas,
            // and implicitly the growing conversation prefix before it) so
            // repeated turns read that unchanged prefix from cache instead
            // of paying full input-token price for it every single time.
            CacheControl = new CacheControlEphemeral { Ttl = "1h" },
        };

        var blockKinds = new Dictionary<long, string>();
        var toolIds = new Dictionary<long, string>();
        var toolNames = new Dictionary<long, string>();
        var toolJson = new Dictionary<long, StringBuilder>();
        var textAccum = new Dictionary<long, StringBuilder>();
        var assistantBlocks = new List<ContentBlockParam>();
        var toolCalls = new List<PendingToolCall>();
        string pendingSpeech = "";
        bool novaPrefixWritten = false;
        long? openTextIndex = null;
        var novaReply = new StringBuilder();

        try
        {
            await foreach (RawMessageStreamEvent evt in _client.Messages.CreateStreaming(parameters, cancellationToken))
            {
                if (evt.TryPickContentBlockStart(out var startEvt))
                {
                    long idx = startEvt.Index;
                    if (startEvt.ContentBlock.TryPickToolUse(out var toolUseStart))
                    {
                        blockKinds[idx] = "tool_use";
                        toolIds[idx] = toolUseStart.ID;
                        toolNames[idx] = toolUseStart.Name;
                        toolJson[idx] = new StringBuilder();
                    }
                    else
                    {
                        blockKinds[idx] = "text";
                        textAccum[idx] = new StringBuilder();
                        openTextIndex = idx;
                    }

                    continue;
                }

                if (evt.TryPickContentBlockDelta(out var deltaEvt))
                {
                    long idx = deltaEvt.Index;
                    if (blockKinds.GetValueOrDefault(idx) == "tool_use")
                    {
                        if (deltaEvt.Delta.TryPickInputJson(out var inputJsonDelta))
                        {
                            toolJson[idx].Append(inputJsonDelta.PartialJson);
                        }

                        continue;
                    }

                    if (deltaEvt.Delta.TryPickText(out var textDelta))
                    {
                        if (!novaPrefixWritten)
                        {
                            Console.Write("Nova: ");
                            novaPrefixWritten = true;
                        }

                        Console.Write(textDelta.Text);
                        textAccum[idx].Append(textDelta.Text);
                        pendingSpeech += textDelta.Text;
                        novaReply.Append(textDelta.Text);

                        int boundary = TextHeuristics.LastSentenceBoundaryIndex(pendingSpeech);
                        if (boundary >= 0)
                        {
                            string toSpeak = pendingSpeech[..(boundary + 1)].Trim();
                            pendingSpeech = pendingSpeech[(boundary + 1)..];
                            if (toSpeak.Length > 0)
                            {
                                await SpeakAndWaitAsync(toSpeak, cancellationToken);
                            }
                        }
                    }

                    continue;
                }

                if (evt.TryPickContentBlockStop(out var stopEvt))
                {
                    long idx = stopEvt.Index;
                    if (blockKinds.GetValueOrDefault(idx) == "tool_use")
                    {
                        string fullJson = toolJson[idx].ToString();
                        Dictionary<string, JsonElement> inputDict = string.IsNullOrWhiteSpace(fullJson)
                            ? []
                            : JsonDocument.Parse(fullJson).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

                        assistantBlocks.Add(new ToolUseBlockParam { ID = toolIds[idx], Name = toolNames[idx], Input = inputDict });
                        toolCalls.Add(new PendingToolCall(toolIds[idx], toolNames[idx], inputDict));
                    }
                    else
                    {
                        // Claude can emit an empty text block immediately before a tool_use
                        // block - the API rejects empty text content blocks if replayed.
                        string text = textAccum[idx].ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            assistantBlocks.Add(new TextBlockParam(text));
                        }

                        if (openTextIndex == idx)
                        {
                            openTextIndex = null;
                        }
                    }

                    continue;
                }
            }

            string finalChunk = pendingSpeech.Trim();
            if (finalChunk.Length > 0)
            {
                await SpeakAndWaitAsync(finalChunk, cancellationToken);
            }
        }
        finally
        {
            if (novaPrefixWritten)
            {
                Console.WriteLine("\n");
            }

            // The stream was cut off before this block's content_block_stop fired -
            // flush whatever was accumulated (already spoken or not) so it isn't lost.
            if (openTextIndex is { } stillOpenIdx && textAccum.TryGetValue(stillOpenIdx, out var openText))
            {
                string text = openText.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    assistantBlocks.Add(new TextBlockParam(text));
                }
            }

            if (assistantBlocks.Count > 0)
            {
                _conversation.Add(new MessageParam { Role = Role.Assistant, Content = assistantBlocks });
            }

            RecordTranscript(isUser: false, novaReply.ToString().Trim());
        }

        return toolCalls;
    }

    // Speaks one chunk of text and waits for it to actually finish playing -
    // engine-specific completion/timeout/cancellation handling lives in the
    // ITtsEngine implementation itself (see KokoroTtsEngine/ChatterboxTtsClient).
    // IsSpeaking flips true via the engine's PlaybackStarted event (wired in
    // the constructor), not here - synthesis has real latency between this
    // call and audio actually starting, so setting it here would make
    // IsSpeaking (and the overlay's talking animations) lie ahead of the
    // actual audio.
    private async Task SpeakAndWaitAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await _tts.SpeakAsync(text, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _novaSpeaking, 0);
        }
    }

    private async Task<(string Content, bool IsError)> ExecuteToolAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        try
        {
            switch (name)
            {
                case "read_file":
                    return (FileTools.ReadFile(input), false);
                case "list_files":
                    return (FileTools.ListFiles(input), false);
                case "edit_file":
                    return (FileTools.EditFile(_fileEditHistoryDbPath, input), false);
                case "revert_file_edit":
                    return (FileTools.RevertFileEdit(_fileEditHistoryDbPath, input), false);
                case "list_file_edits":
                    return (FileTools.ListFileEdits(_fileEditHistoryDbPath, input), false);
                case "delete_path":
                    return (FileTools.DeletePath(input), false);
                case "run_command":
                    return (await CommandRunner.RunCommandAsync(input, cancellationToken), false);
                case "save_memory":
                    return (await MemoryStore.Save(_memoryDbPath, _embeddingGenerator, input), false);
                case "search_memory":
                    return (await MemoryStore.Search(_memoryDbPath, _embeddingGenerator, input), false);
                case "search_conversation_history":
                    return (await ConversationArchive.Search(_conversationArchiveDbPath, _embeddingGenerator, input), false);
                case "read_screen":
                    return (ScreenReader.ReadScreen(ToolInput.GetString(input, "window_title")), false);
                case "scroll_desktop":
                    return (DesktopInteraction.Scroll(input), false);
                case "interact_desktop":
                    return (DesktopInteraction.Interact(input), false);
                case "open_path":
                    return (ShellOpener.Open(input), false);
                case "open_watched_terminal":
                    return (_openWatchedTerminal(), false);
                case "browser_navigate":
                    {
                        bool defaultToNewTab = !_taskHasNavigatedBrowser;
                        _taskHasNavigatedBrowser = true;
                        return (await _browser.NavigateAsync(input, defaultToNewTab), false);
                    }
                case "browser_read":
                    return (await _browser.ReadAsync(input), false);
                case "browser_fill":
                    return (await _browser.FillAsync(input), false);
                case "browser_select":
                    return (await _browser.SelectAsync(input), false);
                case "browser_check":
                    return (await _browser.CheckAsync(input), false);
                case "browser_upload":
                    return (await _browser.UploadAsync(input), false);
                case "browser_click":
                    return (await _browser.ClickAsync(input), false);
                case "search_email":
                    if (RequireGoogleClient(_gmail) is not { } gmailForSearch)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await gmailForSearch.SearchAsync(input["query"].GetString()!, ToolInput.GetInt(input, "max_results") ?? 10, cancellationToken), false);
                case "send_email":
                    if (RequireGoogleClient(_gmail) is not { } gmailForSend)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await gmailForSend.SendAsync(input["to"].GetString()!, input["subject"].GetString()!, input["body"].GetString()!, cancellationToken), false);
                case "list_calendar_events":
                    if (RequireGoogleClient(_calendar) is not { } calendarForList)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await calendarForList.ListUpcomingAsync(ToolInput.GetInt(input, "max_results") ?? 10, cancellationToken), false);
                case "create_calendar_event":
                    if (RequireGoogleClient(_calendar) is not { } calendarForCreate)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    if (!DateTimeOffset.TryParse(input["start"].GetString(), out DateTimeOffset start) ||
                        !DateTimeOffset.TryParse(input["end"].GetString(), out DateTimeOffset end))
                    {
                        return ("start/end must be valid ISO 8601 date-times.", true);
                    }

                    return (await calendarForCreate.CreateEventAsync(input["summary"].GetString()!, start, end, ToolInput.GetString(input, "description"), cancellationToken), false);
                case "read_doc":
                    if (RequireGoogleClient(_docs) is not { } docsForRead)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await docsForRead.ReadAsync(input["document_id"].GetString()!, cancellationToken), false);
                case "create_doc":
                    if (RequireGoogleClient(_docs) is not { } docsForCreate)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await docsForCreate.CreateAsync(input["title"].GetString()!, ToolInput.GetString(input, "content"), cancellationToken), false);
                case "append_to_doc":
                    if (RequireGoogleClient(_docs) is not { } docsForAppend)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await docsForAppend.AppendAsync(input["document_id"].GetString()!, input["text"].GetString()!, cancellationToken), false);
                case "replace_in_doc":
                    if (RequireGoogleClient(_docs) is not { } docsForReplace)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await docsForReplace.ReplaceTextAsync(
                        input["document_id"].GetString()!,
                        input["find_text"].GetString()!,
                        input["replace_text"].GetString()!,
                        ToolInput.GetBool(input, "match_case") ?? false,
                        cancellationToken), false);
                case "search_drive":
                    if (RequireGoogleClient(_drive) is not { } driveForSearch)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await driveForSearch.SearchAsync(input["query"].GetString()!, ToolInput.GetInt(input, "max_results") ?? 10, cancellationToken), false);
                case "upload_to_drive":
                    if (RequireGoogleClient(_drive) is not { } driveForUpload)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await driveForUpload.UploadAsync(input["local_file_path"].GetString()!, ToolInput.GetString(input, "drive_file_name"), cancellationToken), false);
                case "read_sheet":
                    if (RequireGoogleClient(_sheets) is not { } sheetsForRead)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await sheetsForRead.ReadAsync(input["spreadsheet_id"].GetString()!, ToolInput.GetString(input, "range"), cancellationToken), false);
                case "create_sheet":
                    if (RequireGoogleClient(_sheets) is not { } sheetsForCreate)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await sheetsForCreate.CreateAsync(input["title"].GetString()!, ToolInput.GetRows(input, "initial_rows"), cancellationToken), false);
                case "append_sheet_rows":
                    if (RequireGoogleClient(_sheets) is not { } sheetsForAppend)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await sheetsForAppend.AppendRowsAsync(input["spreadsheet_id"].GetString()!, ToolInput.GetString(input, "range"), ToolInput.GetRows(input, "rows") ?? [], cancellationToken), false);
                case "update_sheet_range":
                    if (RequireGoogleClient(_sheets) is not { } sheetsForUpdate)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await sheetsForUpdate.UpdateRangeAsync(input["spreadsheet_id"].GetString()!, input["range"].GetString()!, ToolInput.GetRows(input, "values") ?? [], cancellationToken), false);
                case "read_slides":
                    if (RequireGoogleClient(_slides) is not { } slidesForRead)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await slidesForRead.ReadAsync(input["presentation_id"].GetString()!, cancellationToken), false);
                case "create_presentation":
                    if (RequireGoogleClient(_slides) is not { } slidesForCreate)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await slidesForCreate.CreateAsync(input["title"].GetString()!, cancellationToken), false);
                case "append_slide":
                    if (RequireGoogleClient(_slides) is not { } slidesForAppend)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await slidesForAppend.AppendSlideAsync(input["presentation_id"].GetString()!, input["title"].GetString()!, ToolInput.GetString(input, "body"), cancellationToken), false);
                case "replace_text_in_slides":
                    if (RequireGoogleClient(_slides) is not { } slidesForReplace)
                    {
                        return (GoogleNotConnectedMessage, true);
                    }

                    return (await slidesForReplace.ReplaceTextAsync(
                        input["presentation_id"].GetString()!,
                        input["find_text"].GetString()!,
                        input["replace_text"].GetString()!,
                        ToolInput.GetBool(input, "match_case") ?? false,
                        cancellationToken), false);
                case "build_tool":
                    {
                        (string message, bool success) = await ToolBuilder.BuildAsync(
                            _selfContainedToolsDir, _toolRegistryDbPath, _toolContractDllPath,
                            input["name"].GetString()!,
                            input["description"].GetString()!,
                            input["input_schema"].GetRawText(),
                            input["source_code"].GetString()!,
                            ToolInput.GetBool(input, "uses_paid_api") ?? false,
                            ToolInput.GetBool(input, "has_external_effects") ?? false,
                            cancellationToken);
                        return (message, !success);
                    }
                case "run_tool":
                    return await RunDynamicToolAsync(input, cancellationToken);
                case "list_tools":
                    return (DescribeAvailableTools(), false);
                case "get_settings":
                    return (_settings.DescribeAll(), false);
                case "read_recent_errors":
                    return (ErrorLog.ReadRecent(ToolInput.GetInt(input, "count") ?? 5), false);
                case "update_setting":
                    string settingName = input["name"].GetString()!;
                    string settingValue = input["value"].GetString()!;
                    string? settingError = _settings.TrySet(settingName, settingValue);
                    return settingError is null
                        ? ($"Updated {settingName} to {settingValue}.", false)
                        : (settingError, true);
                default:
                    return ($"Unknown tool: {name}", true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A real cancellation (the turn's own cts, cancelled by
            // Interrupt() - now only reachable via an explicit stop
            // request, see StopIntentClassifier), not a tool's own
            // internal timeout (those are caught and converted to a
            // friendly message *inside* the tool itself - CommandRunner/
            // DynamicToolRuntime never let their own timeouts reach here
            // as an exception at all). Previously this was caught by the
            // blanket catch below and turned into a normal, non-throwing
            // ("Tool error: ...", true) result - which meant the tool-call
            // loop in ProcessTextInputAsync just moved on to the *next*
            // queued call instead of stopping, so an explicit "stop" mid-
            // round didn't actually stop the round. Re-throwing lets it
            // unwind through that loop into ProcessTextInputAsync's own
            // catch (OperationCanceledException), the same path a
            // cancellation between rounds already took.
            throw;
        }
        catch (Exception ex)
        {
            ErrorLog.Log($"ExecuteToolAsync: {name}", ex);

            // A Google client field, once assigned (startup or a live
            // popup connect), stayed non-null forever - if the underlying
            // token is later revoked by the user, expires from months of
            // inactivity, or the token cache gets corrupted, every Google
            // tool call would otherwise keep hitting this same generic
            // "Tool error" message forever with no way back to the
            // credentials popup short of restarting Nova. All six clients
            // share one UserCredential from one AuthorizeAsync call, so an
            // auth failure on any of them means the same thing for all of
            // them - reset every one together and let the next Google tool
            // attempt naturally re-trigger RequestGoogleCredentials via
            // RequireGoogleClient above.
            if (IsGoogleAuthFailure(ex))
            {
                _gmail = null;
                _calendar = null;
                _docs = null;
                _drive = null;
                _sheets = null;
                _slides = null;
                return ("The Google account's connection stopped working (it may have been revoked or expired) - " +
                        "a credentials popup was just shown on the overlay so it can be reconnected.", true);
            }

            return ($"Tool error: {ex.Message}", true);
        }
    }

    // TokenResponseException is what Google.Apis.Auth throws when a
    // refresh token itself is rejected (revoked access, "invalid_grant") -
    // the clear, unambiguous signal. GoogleApiException with a 401 is the
    // broader "this specific request wasn't authorized" signal, covering
    // cases a token-refresh failure wouldn't (e.g. the token still refreshes
    // fine but access to this particular resource was separately revoked).
    private static bool IsGoogleAuthFailure(Exception ex) =>
        ex is Google.Apis.Auth.OAuth2.Responses.TokenResponseException ||
        (ex is Google.GoogleApiException apiEx && apiEx.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized);

    // run_tool's actual dispatch - looks the tool up, runs it (load,
    // invoke, unload - see DynamicToolRuntime), and tracks the same-task
    // consecutive-failure streak that triggers self-repair. A caught
    // failure here is deliberately *not* rethrown to ExecuteToolAsync's
    // own catch above - this needs to update the failure count/trigger a
    // revert before turning it into an error tool_result, which a generic
    // catch further up can't do.
    private async Task<(string Content, bool IsError)> RunDynamicToolAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        string name = input["name"].GetString()!;
        ToolRecord? record = ToolRegistry.Find(_toolRegistryDbPath, name);
        if (record is null)
        {
            return ($"No tool named \"{name}\" exists - check list_tools, or build it first with build_tool.", true);
        }

        IReadOnlyDictionary<string, JsonElement> toolInput = input.TryGetValue("input", out JsonElement inputElement) && inputElement.ValueKind == JsonValueKind.Object
            ? inputElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)
            : new Dictionary<string, JsonElement>();

        try
        {
            string result = await DynamicToolRuntime.RunAsync(record.DllPath, toolInput, cancellationToken);
            ToolRegistry.RecordSuccess(_toolRegistryDbPath, name);
            _toolFailuresThisTask.Remove(name);
            return (result, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A real cancellation (an explicit stop request while this
            // tool happened to be mid-run - see StopIntentClassifier) is
            // not evidence the tool itself is broken, so it shouldn't
            // count toward the 3-in-a-row self-repair threshold below, nor
            // get logged as a failure in the registry. Re-thrown, not
            // returned as a normal tuple, so it still unwinds all the way
            // out to ProcessTextInputAsync's own cancellation handling,
            // the same as any other tool's genuine cancellation now does
            // (see ExecuteToolAsync's own OperationCanceledException catch).
            throw;
        }
        catch (Exception ex)
        {
            ErrorLog.Log($"run_tool ({name})", ex);
            ToolRegistry.RecordFailure(_toolRegistryDbPath, name);
            int failuresThisTask = _toolFailuresThisTask.GetValueOrDefault(name) + 1;
            _toolFailuresThisTask[name] = failuresThisTask;

            if (failuresThisTask < 3)
            {
                return ($"The \"{name}\" tool failed: {ex.Message}", true);
            }

            // 3 in a row within this task - self-repair kicks in. Not
            // spoken directly here (that would fight the outer, already-
            // active turn for _turnCts) - folded into the tool_result instead, so
            // Claude's own next reply (already correctly wired into this
            // turn's normal speech) is what actually announces it. Never
            // silent, per the roadmap's self-repair commitment - just
            // said in Claude's own voice instead of a separate canned one.
            _toolFailuresThisTask[name] = 0;
            string revertMessage = await ToolBuilder.RevertAsync(_selfContainedToolsDir, _toolRegistryDbPath, name, CancellationToken.None);
            Console.WriteLine($"\n[self-repair] {revertMessage}\n");
            return ($"The \"{name}\" tool failed 3 times in a row: {ex.Message}\n{revertMessage} Tell the user this happened.", true);
        }
    }

    // True only for a run_tool call targeting a tool that's never been
    // through Gate 2 review before - see the gate2Calls filter above and
    // the MarkApproved call in the Gate 2 confirmation branch.
    private bool IsUnapprovedToolRun(PendingToolCall call)
    {
        if (call.Name != "run_tool")
        {
            return false;
        }

        string? toolName = ToolInput.GetString(call.Input, "name");
        return toolName is not null && ToolRegistry.Find(_toolRegistryDbPath, toolName) is { Approved: false };
    }

    // The GET-vs-POST/PUT distinction: a tool built with has_external_effects
    // true (self-declared, see ToolCatalog's build_tool schema) needs a
    // fresh Gate 2 review on *every* run, not just its first, since unlike
    // a read-only tool it actually does something outside Nova each time -
    // "approved once, trusted forever" was reasoned for the risk of running
    // unreviewed code, never for a tool whose steady-state behavior is
    // itself Gate-2-shaped (send/post/order/modify). Independent of
    // IsUnapprovedToolRun above - a tool can be both, neither, or just one.
    private bool HasExternalEffectsToolRun(PendingToolCall call)
    {
        if (call.Name != "run_tool")
        {
            return false;
        }

        string? toolName = ToolInput.GetString(call.Input, "name");
        return toolName is not null && ToolRegistry.Find(_toolRegistryDbPath, toolName) is { HasExternalEffects: true };
    }

    // A tool that costs real money per call needs the same per-call review
    // as one with external effects - "approved once" was reasoned for the
    // risk of running unreviewed code, never for "and also I'll keep
    // quietly spending your money forever after the first time." The
    // review text itself already says so (see NovaAssistant's
    // ToolDescriptionLookup local function, which appends "uses a paid
    // API - each use may cost money" to the description whenever
    // UsesPaidApi is true) - this is what makes sure that text is actually
    // seen on every call, not just the first.
    private bool UsesPaidApiToolRun(PendingToolCall call)
    {
        if (call.Name != "run_tool")
        {
            return false;
        }

        string? toolName = ToolInput.GetString(call.Input, "name");
        return toolName is not null && ToolRegistry.Find(_toolRegistryDbPath, toolName) is { UsesPaidApi: true };
    }

    private string DescribeAvailableTools()
    {
        List<ToolRecord> tools = ToolRegistry.List(_toolRegistryDbPath);
        return tools.Count == 0
            ? "No self-contained tools have been built yet."
            : string.Join("\n", tools.Select(t =>
                $"{t.Name}{(t.Approved ? "" : " (not yet approved/run)")}{(t.HasExternalEffects ? " (has external effects - needs a Gate 2 review every run, not just the first)" : "")}{(t.UsesPaidApi ? " (uses a paid API - needs a Gate 2 review every run, not just the first)" : "")}: {t.Description}\n  input: {t.InputSchemaJson}"));
    }
}
