using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Win32;

namespace Nova;

// Hosts the Avalonia overlay on its own dedicated STA thread, same shape as
// HotkeyListener/the old WPF OverlayHost - Avalonia's own event loop
// (StartWithClassicDesktopLifetime) blocks the calling thread the same way
// Dispatcher.Run()/Application.Run() do, so it needs its own thread rather
// than running on Main's. Assistant/repoRoot are handed to the App class via
// static fields (set before Configure<T>() runs) rather than constructor
// injection, since AppBuilder.Configure<TApp>() requires a parameterless
// constructor.
internal sealed class AvaloniaOverlayHost : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private Dispatcher? _dispatcher;

    public AvaloniaOverlayHost(NovaAssistant assistant, string repoRoot)
    {
        AvaloniaOverlayApp.PendingAssistant = assistant;
        AvaloniaOverlayApp.PendingRepoRoot = repoRoot;
        AvaloniaOverlayApp.OnReady = () =>
        {
            _dispatcher = Dispatcher.UIThread;
            _ready.Set();
        };

        _thread = new Thread(RunMessageLoop) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Name = "Nova Avalonia Overlay UI";
        _thread.Start();
        _ready.Wait();
    }

    private static void RunMessageLoop()
    {
        // Avalonia's default GPU-accelerated Skia backend probes for Vulkan
        // on this machine, which pulls Intel's graphics compiler
        // (igc64.dll, ~83MB) directly into the process - the same class of
        // cost as Whisper's Vulkan backend above, for an overlay whose
        // heaviest draw is ARC's ~300-element particle field in a 204px
        // canvas, nowhere near demanding enough to need a GPU. Software
        // rendering trades a bit of compositor headroom for dropping that
        // driver dependency entirely.
        AppBuilder.Configure<AvaloniaOverlayApp>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new List<Win32RenderingMode> { Win32RenderingMode.Software } })
            .LogToTrace()
            .StartWithClassicDesktopLifetime([]);
    }

    // Best-effort - Main() blocks forever and Ctrl+C typically terminates
    // the process without unwinding `using` statements anyway (same caveat
    // as the old OverlayHost/audioPipeline/hotkey listeners).
    public void Dispose() => _dispatcher?.Post(() => Environment.Exit(0));
}
