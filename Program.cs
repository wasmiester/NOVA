using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using ElBruno.LocalEmbeddings;
using KokoroSharp;
using KokoroSharp.Core;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace Nova;

// Setup:
//   1. dotnet add package Anthropic
//   2. dotnet add package KokoroSharp.CPU
//   3. dotnet add package Whisper.net / Whisper.net.Runtime / NAudio
//   4. dotnet add package Microsoft.Data.Sqlite
//   5. Copy secrets/.env.example to secrets/.env and fill in
//      ANTHROPIC_API_KEY (secrets/.env is gitignored - never commit it).
//   6. dotnet run
//
// Build order so far (rationale for each lives next to the relevant code):
//   7  - persistent memory                    -> Memory/MemoryStore.cs
//   8  - screen reading (FlaUI/UI Automation)  -> Screen/ScreenReader.cs
//   9  - browser form-filling, no click/submit -> Browser/BrowserController.cs
//   10 - semantic memory search                -> Memory/MemoryStore.cs
//   11 - browser tools attach to real Chrome    -> Browser/BrowserController.cs
//   12 - real acoustic echo cancellation        -> Audio/AudioCapturePipeline.cs
//   13 - activation modes + narrow browser/desktop clicking -> Comfort/, Browser/NavigationalClickGuard.cs
//   14 - Gmail send/read + inbox watch + Calendar -> Google/
//   15 - watched terminal (plain-text pass-through) -> TerminalRelay/, Ambient/TerminalWatcher.cs
//   16 - floating overlay HUD (Avalonia, own STA thread) -> Overlay/
internal static class Program
{
    // Experiment: evaluating whether Chatterbox (via a local Python sidecar,
    // see tts-sidecar/) actually sounds more natural than Kokoro before
    // committing to it. Flip to false to go back to Kokoro - see
    // Speech/ITtsEngine.cs for how the two engines are kept swappable.
    private const bool UseChatterboxTts = false;

    private static async Task Main()
    {
        string repoRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
        EnvLoader.Load(Path.Combine(repoRoot, "secrets", ".env"));

        ErrorLog.Initialize(Path.Combine(repoRoot, "data", "errors.log"));
        // Catches anything that isn't already handled closer to where it
        // happened (e.g. on a background thread) - can't stop the process
        // from dying, but at least the failure gets recorded first.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ErrorLog.Log("Unhandled exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

        // Reads ANTHROPIC_API_KEY (and ANTHROPIC_BASE_URL, if set) from the
        // environment automatically.
        AnthropicClient client = new();

        string memoryDbPath = Path.Combine(repoRoot, "data", "memory.db");
        Directory.CreateDirectory(Path.GetDirectoryName(memoryDbPath)!);
        MemoryStore.Initialize(memoryDbPath);

        // Separate file from memory.db (see ConversationArchive's own doc
        // comment) - completed-task transcripts, not durable facts.
        string conversationArchiveDbPath = Path.Combine(repoRoot, "data", "conversation-archive.db");
        ConversationArchive.Initialize(conversationArchiveDbPath);

        // Separate again (see FileEditHistory's own doc comment) - lets
        // edit_file overwrites actually be undone via revert_file_edit.
        string fileEditHistoryDbPath = Path.Combine(repoRoot, "data", "file-edit-history.db");
        FileEditHistory.Initialize(fileEditHistoryDbPath);

        // Self-contained tools (see Tools/Dynamic/) - SelfContainedTools/ is
        // the git-versioned source root, tools-registry.db is the metadata
        // store, and toolContractDllPath points at the shared interface
        // every generated tool project references (see
        // ToolContract/INovaTool.cs) - built as part of this same solution
        // via the ProjectReference in Nova.Core.csproj, so it's already on
        // disk by the time this runs.
        string selfContainedToolsDir = Path.Combine(repoRoot, "SelfContainedTools");
        string toolRegistryDbPath = Path.Combine(repoRoot, "data", "tools-registry.db");
        string toolContractDllPath = Path.Combine(repoRoot, "ToolContract", "bin", "Debug", "net10.0", "Nova.ToolContract.dll");
        ToolRegistry.Initialize(toolRegistryDbPath);

        Console.WriteLine("Loading local embedding model for semantic memory search (first run downloads it)...");
        await using LocalEmbeddingGenerator embeddingGenerator = await LocalEmbeddingGenerator.CreateAsync();
        Console.WriteLine("Embedding model ready.");

        var browser = new BrowserController();

        // Optional: Gmail send/read + Calendar read/create + Docs read/
        // create/append + Drive search/upload. Nova runs fine without these
        // set - the tools just report themselves unavailable, which now
        // also surfaces the overlay's credentials popup (see
        // CredentialsPopup/NovaAssistant.RequestGoogleCredentials) so
        // connecting doesn't require hand-editing this file. secretsEnvPath/
        // googleTokenStoreDir are handed to NovaAssistant either way (not
        // just inside the if-below) since a live popup-triggered connect
        // needs them regardless of whether startup already had credentials.
        // See secrets/.env.example for the Google Cloud project setup steps.
        string secretsEnvPath = Path.Combine(repoRoot, "secrets", ".env");
        string googleTokenStoreDir = Path.Combine(repoRoot, "data", "google-token");

        // Nova-adjustable settings (calendar reminder lead time, watcher
        // on/off) - deliberately separate from secrets/.env (credentials,
        // not settings) and memory.db (learned facts, not app config). See
        // Config/NovaSettings.cs.
        string settingsPath = Path.Combine(repoRoot, "data", "settings.env");

        GmailClient? gmailClient = null;
        CalendarClient? calendarClient = null;
        DocsClient? docsClient = null;
        DriveClient? driveClient = null;
        SheetsClient? sheetsClient = null;
        SlidesClient? slidesClient = null;
        string? googleClientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID");
        string? googleClientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET");
        if (!string.IsNullOrEmpty(googleClientId) && !string.IsNullOrEmpty(googleClientSecret))
        {
            Console.WriteLine("Connecting Google account (Gmail + Calendar + Docs + Drive + Sheets + Slides) - first run opens a browser window for consent...");
            try
            {
                Google.Apis.Auth.OAuth2.UserCredential credential =
                    await GoogleAuth.AuthorizeAsync(googleClientId, googleClientSecret, googleTokenStoreDir, CancellationToken.None);
                gmailClient = new GmailClient(credential);
                calendarClient = new CalendarClient(credential);
                docsClient = new DocsClient(credential);
                driveClient = new DriveClient(credential);
                sheetsClient = new SheetsClient(credential);
                slidesClient = new SlidesClient(credential);
                Console.WriteLine("Google account connected.");
            }
            catch (Exception ex)
            {
                // Same broad-catch reasoning as NovaAssistant.ConnectGoogleAccountAsync's
                // own live-connect attempt - a revoked token, a corrupted
                // data/google-token cache, or a transient network error here
                // shouldn't take down the entire app before the overlay/TTS/mic
                // pipeline ever get a chance to start. Nova just runs without
                // Google connected, the same as if the env vars were never set -
                // reconnecting via the overlay's credentials popup still works
                // since it goes through this same GoogleAuth.AuthorizeAsync call
                // with its own independent try/catch.
                ErrorLog.Log("Startup Google connect", ex);
                Console.WriteLine($"Couldn't connect the Google account at startup ({ex.Message}) - continuing without it. Try reconnecting via the overlay's credentials popup, or check secrets/.env / data/google-token.");
            }
        }
        else
        {
            Console.WriteLine("GOOGLE_CLIENT_ID/GOOGLE_CLIENT_SECRET not set - Gmail/Calendar/Docs/Drive/Sheets/Slides tools will report as unavailable until connected via the overlay's credentials popup (see secrets/.env.example).");
        }

        ITtsEngine tts;
        if (UseChatterboxTts)
        {
            Process sidecarProcess = StartChatterboxSidecar(repoRoot);
            void KillSidecar()
            {
                try
                {
                    if (!sidecarProcess.HasExited)
                    {
                        sidecarProcess.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best-effort - the process may already be gone.
                }
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => KillSidecar();
            Console.CancelKeyPress += (_, _) => KillSidecar();

            var chatterboxClient = new ChatterboxTtsClient(new Uri("http://127.0.0.1:8765/"));
            Console.WriteLine("Waiting for the Chatterbox sidecar to load its model (first run downloads several GB of weights)...");
            await chatterboxClient.WaitUntilReadyAsync(CancellationToken.None);
            Console.WriteLine("Chatterbox ready.");
            tts = chatterboxClient;
        }
        else
        {
            Console.WriteLine("Loading voice model (first run downloads ~320MB)...");
            KokoroTTS kokoroTts = KokoroTTS.LoadModel();
            KokoroVoice kokoroVoice = KokoroVoiceManager.GetVoice("af_heart");
            Console.WriteLine("Voice ready.");
            tts = new KokoroTtsEngine(kokoroTts, kokoroVoice);
        }

        // Downgraded from small.en to base.en (still quantized) - small was
        // the single biggest contributor to perceived response latency,
        // and became more noticeable once transcriptions started being
        // serialized through one lock (see NovaAssistant's
        // _transcriptionLock) for "dynamic talking" - a queued-up
        // interjection now has to wait out however long the one ahead of
        // it takes. base.en is roughly 2-3x faster on CPU; still solid
        // accuracy for clear, short spoken commands, which is the actual
        // workload here, not dictating long-form prose.
        string whisperModelPath = Path.Combine(repoRoot, "models", "ggml-base.en-q5_1.bin");
        if (!File.Exists(whisperModelPath))
        {
            Console.WriteLine("Downloading speech-to-text model (first run only)...");
            Directory.CreateDirectory(Path.GetDirectoryName(whisperModelPath)!);
            using Stream modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.BaseEn, QuantizationType.Q5_1);
            using FileStream fileStream = File.Create(whisperModelPath);
            await modelStream.CopyToAsync(fileStream);
        }

        // Whisper.net.AllRuntimes bundles CPU/CUDA/Vulkan/CoreML/OpenVINO
        // native backends and auto-picks the "best" one available - on this
        // machine that silently meant Vulkan, which pulls the full Intel
        // *and* NVIDIA GPU driver/shader-compiler stack (~300MB) directly
        // into this process just to transcribe short VAD-gated utterances,
        // nowhere near demanding enough to need GPU acceleration. Forcing
        // CPU keeps the same model/accuracy/functionality, just without
        // that driver stack ever loading.
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
        using WhisperFactory whisperFactory = WhisperFactory.FromPath(whisperModelPath);
        using WhisperProcessor whisperProcessor = whisperFactory.CreateBuilder()
            .WithLanguage("en")
            .WithNoContext() // this WhisperProcessor is reused across turns - don't let one
                              // turn's transcription bias decoding on the next
            .WithThreads(Environment.ProcessorCount)
            .Build();
        Console.WriteLine("Speech-to-text ready.");

        string vadModelPath = Path.Combine(repoRoot, "models", "silero-vad.bin");
        if (!File.Exists(vadModelPath))
        {
            Console.WriteLine("Downloading voice-activity-detection model (first run only)...");
            Directory.CreateDirectory(Path.GetDirectoryName(vadModelPath)!);
            using Stream vadModelStream = await WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(SileroVadType.V6_2_0);
            using FileStream vadFileStream = File.Create(vadModelPath);
            await vadModelStream.CopyToAsync(vadFileStream);
        }

        using WhisperVadFactory vadFactory = WhisperVadFactory.FromPath(vadModelPath);
        using WhisperVadProcessor vadProcessor = vadFactory.CreateBuilder()
            .WithThreads(Environment.ProcessorCount)
            .Build();
        Console.WriteLine("Voice-activity detection ready.\n");

        var assistant = new NovaAssistant(
            client, tts, whisperProcessor, vadProcessor, memoryDbPath, conversationArchiveDbPath, fileEditHistoryDbPath, embeddingGenerator, browser,
            gmailClient, calendarClient, docsClient, driveClient, sheetsClient, slidesClient, secretsEnvPath, googleTokenStoreDir, settingsPath,
            selfContainedToolsDir, toolRegistryDbPath, toolContractDllPath, () => WatchedTerminalLauncher.Open(repoRoot));

        using var audioPipeline = new AudioCapturePipeline(assistant);
        audioPipeline.Start();

        // Floating overlay HUD (ARC/WEB/AURA skins, ⟳ to switch) - runs on
        // its own dedicated STA thread with its own Avalonia event loop, the
        // same shape as HotkeyListener's own STA thread below. See
        // Overlay/AvaloniaOverlayHost.cs.
        using var overlay = new AvaloniaOverlayHost(assistant, repoRoot);

        // Ctrl+Alt+Space has Nova announce she's listening (like a butler
        // answering a call) - and, per TriggerReadyAcknowledgment, is now
        // the *only* way to wake her from asleep, so it always works
        // regardless of engaged/asleep state.
        using var hotkey = new HotkeyListener(assistant.TriggerReadyAcknowledgment);

        // Watches wherever Nova was launched from for file changes worth
        // proactively surfacing. Only actually fires while engaged (awake) -
        // see AmbientFileWatcher/TriggerAmbientFileSuggestion.
        using var ambientWatcher = new AmbientFileWatcher(
            Directory.GetCurrentDirectory(),
            client,
            () => assistant.Engaged,
            assistant.TriggerAmbientFileSuggestion);

        // Proactive inbox-watching (GmailWatcher) is now owned by
        // NovaAssistant itself, not constructed here - it starts the moment
        // a GmailClient exists, whether that's at this point (credentials
        // already in secrets/.env) or later via a live overlay connect (see
        // NovaAssistant.EnsureGmailWatcherStarted/ConnectGoogleAccountAsync),
        // so a mid-run connect no longer needs a restart to get it running.
        // Deliberately NOT gated on Engaged - email/calendar are the
        // exceptions to the sleep-state suppression (see TriggerEmailAlert).

        // The other ambient source, alongside the file watcher above -
        // watches output from any terminal opened via open_watched_terminal.
        // Always listening (cheap - just a pipe server); only actually
        // escalates anything while engaged, same gating as the file watcher.
        using var terminalWatcher = new TerminalWatcher(
            client,
            () => assistant.Engaged,
            assistant.TriggerTerminalSuggestion);

        // Auto-sleep after 3 hours of no mouse/keyboard input, system-wide -
        // same SetEngaged(false) path as the overlay's sleep button and the
        // spoken sleep phrase. See Input/IdleTracker.cs.
        using var idleTracker = new IdleTracker(() => assistant.Engaged, () => assistant.SetEngaged(false));

        Console.WriteLine("Nova - step 17 (Prompted/Key Bind activation modes, hotkey-only wake, AFK auto-sleep)");
        Console.WriteLine("Prompted mode (default): just start talking - talk over her any time to interrupt.");
        if (hotkey.Registered)
        {
            Console.WriteLine("Press Ctrl+Alt+Space any time to get Nova's attention, including waking her from asleep.");
        }
        else
        {
            Console.WriteLine("Couldn't register Ctrl+Alt+Space - it's likely already claimed by another app on this machine.");
        }

        Console.WriteLine("Say \"take a break\"/\"go to sleep\" to put her to sleep - Ctrl+Alt+Space is the only way to wake her back up.");
        Console.WriteLine("Say \"switch to key bind mode\" to require the hotkey before she'll listen at all - good for avoiding accidental triggers from ambient conversation.");
        Console.WriteLine("Ctrl+C to quit.\n");

        await Task.Delay(Timeout.Infinite);
    }

    // Chatterbox has no .NET bindings - tts-sidecar/server.py is a small
    // Flask wrapper around it, run inside its own venv (see
    // tts-sidecar/.venv, created with --system-site-packages to reuse the
    // machine's existing CUDA-enabled torch install rather than
    // re-downloading it). Not redirecting stdout/stderr lets its startup
    // output (model loading, download progress) share Nova's own console.
    private static Process StartChatterboxSidecar(string repoRoot)
    {
        string sidecarDir = Path.Combine(repoRoot, "tts-sidecar");
        string pythonExe = Path.Combine(sidecarDir, ".venv", "Scripts", "python.exe");
        var psi = new ProcessStartInfo(pythonExe, "server.py")
        {
            WorkingDirectory = sidecarDir,
            UseShellExecute = false,
        };
        return Process.Start(psi)!;
    }
}
