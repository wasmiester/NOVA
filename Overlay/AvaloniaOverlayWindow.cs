using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Nova;

// The overlay's real window: transparent/topmost/no-titlebar chrome
// hosting whichever IOverlaySkin is currently active as its Content, and
// driving the ~130ms DispatcherTimer that polls NovaAssistant and pushes
// an OverlayState into it. All 3 skins are constructed eagerly (see _skins
// below) so switching back to a previously-viewed one never re-triggers
// per-skin setup (ARC's generated rings, etc.).
//
// Rendered entirely with native Avalonia controls (Skia, no embedded
// browser) - the WebView2 version this replaced had a fundamental
// "airspace" bug where WPF's AllowsTransparency hit-tests off its own
// rendered bitmap, which had nothing drawn where the WebView2 child HWND
// painted itself, so every click fell through to whatever was behind the
// window on the desktop. A single Avalonia visual tree has no separate
// child-window surface to disagree with - confirmed via a spike before
// committing to this rewrite (see chat history).
internal sealed class AvaloniaOverlayWindow : Window
{
    private readonly NovaAssistant _assistant;
    private readonly IOverlaySkin[] _skins;
    private readonly ConfirmPopup _popup;
    private readonly CredentialsPopup _credentialsPopup;
    private readonly Grid _contentHost = new();
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromMilliseconds(130) };
    private readonly string _positionFilePath;
    private int _activeIndex;

    // Tracks the window's "resting" (minimized, transcript-closed) Top so
    // that opening the transcript panel - which can grow the window enough
    // to trigger the clip-avoidance shift below - doesn't leave it stranded
    // higher up once the panel closes again.
    private const double ExpansionHeightThreshold = 100;
    private double _restingTop;
    private double _lastHeight;
    private bool _sizeInitialized;

    // The physical-pixel screen X of the window's right edge - the anchor
    // maximize-mode's width change grows/shrinks around (see PushState's
    // own width-adjustment), same "remember where it should snap back to"
    // idea as _restingTop above, just for the other axis. Updated on
    // startup/state-load and again after every deliberate drag, so a
    // moved window still maximizes toward its own current position
    // instead of the original startup corner.
    private double _anchoredRightPhysical;
    private double _scaling = 1.0;

    // A mode-switch click that lands while Nova is genuinely busy can't
    // switch immediately - remembered here and applied on the next tick
    // once she's free.
    private ActivationMode? _pendingModeSwitch;

    public AvaloniaOverlayWindow(NovaAssistant assistant, string repoRoot)
    {
        _assistant = assistant;
        _positionFilePath = Path.Combine(repoRoot, "data", "overlay-position.txt");

        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        CanResize = false;
        ShowInTaskbar = false;
        // Width set directly on the Window rather than inferred from
        // content via SizeToContent.WidthAndHeight - the two fought each
        // other in practice (confirmed via screenshot: widening the
        // skins' own root Border grew the window's outer bounds but left
        // the actual rendered content floating at its old size in the
        // top-left corner, the two visibly out of sync). Height still
        // genuinely needs to track content (minimized vs. maximized
        // transcript), so only that axis is left to SizeToContent.
        Width = OverlayLayout.PanelWidth;
        MinHeight = OverlayLayout.PanelMinHeight;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // PixelPoint.Position is physical pixels, but OverlayLayout.PanelWidth
        // (and the "40" margin) are DIPs - on this machine's 1.5x display
        // scaling, a hardcoded "300" physical-pixel margin was *narrower*
        // than the window's actual physical width (272 DIP = 408 physical),
        // so the window's right edge landed ~108px past the real screen
        // boundary. Confirmed via GetSystemMetrics(SM_CXVIRTUALSCREEN) vs.
        // GetWindowRect: the window itself was correctly laid out the whole
        // time (verified via UIA - every button sat at the right position),
        // it was just partially rendered off-screen, which every screenshot
        // during that investigation mistook for a real clipping bug.
        // Multiplying by the screen's own Scaling converts the DIP margin
        // to the correct physical-pixel one.
        Screen? primary = Screens.Primary;
        PixelRect workArea = primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        _scaling = primary?.Scaling ?? 1.0;
        int marginPhysical = (int)(40 * _scaling);
        int panelWidthPhysical = (int)(OverlayLayout.PanelWidth * _scaling);
        Position = new PixelPoint(workArea.Right - panelWidthPhysical - marginPhysical, workArea.Y + marginPhysical);

        bool stateLoaded = TryLoadState(out PixelPoint savedPosition, out int savedIndex, out bool savedArcAlt, out bool savedAuraAlt);
        if (stateLoaded)
        {
            Position = savedPosition;
        }

        _restingTop = Position.Y;
        _anchoredRightPhysical = Position.X + (int)(Width * _scaling);

        SizeChanged += (_, e) => OnSizeChanged(e.NewSize);

        PointerPressed += OnWindowPointerPressed;

        // The X button quits Nova entirely (Environment.Exit), not just
        // this window - there's no other way to stop the voice pipeline
        // once the overlay is the only visible UI.
        var actions = new OverlaySkinActions(
            SwitchActivationMode: SwitchActivationModeGuarded,
            SetEngaged: engaged => _assistant.SetEngaged(engaged),
            CycleSkin: CycleSkin,
            Close: () => Environment.Exit(0),
            // A theme toggle (ARC gold/cyan, AURA light/dark) needs both
            // popups re-styled too, not just persisted - AURA's toggle in
            // particular allocates new brush instances rather than
            // mutating existing ones, so a popup built before the toggle
            // would otherwise keep showing the old palette.
            ThemeChanged: () =>
            {
                _popup.ApplyStyle(_skins[_activeIndex].PopupStyle);
                _credentialsPopup.ApplyStyle(_skins[_activeIndex].PopupStyle);
                SaveState();
            },
            SendTypedText: text => _assistant.DispatchTypedText(text));

        _skins = [new ArcSkin(), new WebSkin(), new AuraSkin()];
        foreach (IOverlaySkin skin in _skins)
        {
            skin.AttachActions(actions);
        }

        if (stateLoaded && savedIndex >= 0 && savedIndex < _skins.Length)
        {
            _activeIndex = savedIndex;
        }

        // Both popups (see ConfirmPopup/CredentialsPopup) are siblings
        // layered on top of whichever skin is active in this same Grid,
        // rather than owned by each skin's own visual tree - lets them
        // show/hide/restyle without any of the 3 skins needing to know
        // they exist. Gate 2 review and a Google-credentials prompt are
        // sequenced, never simultaneous in practice (Gate 2 always
        // resolves before ExecuteToolAsync ever discovers Gmail/Calendar
        // isn't connected), so no extra logic to keep them mutually
        // exclusive - the credentials popup is just added last so it wins
        // if that assumption is ever wrong.
        //
        // Constructed here - *before* the saved-theme restore below -
        // deliberately: ToggleTheme() fires the actions.ThemeChanged
        // callback (see above), which reads _popup/_credentialsPopup.
        // Restoring a saved "alternate" theme used to run first, which
        // called ToggleTheme() while both fields were still null,
        // throwing a NullReferenceException the instant the overlay tried
        // to start with a saved cyan ARC/dark AURA theme - only ever
        // silent before because no run had actually saved "alternate" yet
        // (caught live: restoring a saved cyan ARC theme crashed the
        // overlay thread on startup).
        _popup = new ConfirmPopup(_skins[_activeIndex].PopupStyle, approved => _assistant.ConfirmGate2Review(approved));
        _credentialsPopup = new CredentialsPopup(
            _skins[_activeIndex].PopupStyle,
            onConnect: (id, secret) => _assistant.ConnectGoogleAccount(id, secret),
            onCancel: () => _assistant.CancelGoogleCredentials());

        // Restore each skin's own theme by toggling it exactly once if the
        // saved state says "alternate" - skins construct in their default
        // theme, so there's nothing to do for a saved "default". Must run
        // after both popups above are constructed - see their own comment.
        if (stateLoaded)
        {
            if (savedArcAlt)
            {
                _skins[0].ToggleTheme();
            }

            if (savedAuraAlt)
            {
                _skins[2].ToggleTheme();
            }
        }

        _contentHost.Children.Add(_skins[_activeIndex].Root);
        _contentHost.Children.Add(_popup.Root);
        _contentHost.Children.Add(_credentialsPopup.Root);
        Content = _contentHost;
        _skins[_activeIndex].Activate();

        _stateTimer.Tick += (_, _) => PushState();
        _stateTimer.Start();
        PushState();
    }

    private void OnSizeChanged(Size newSize)
    {
        if (!_sizeInitialized)
        {
            _sizeInitialized = true;
            _lastHeight = newSize.Height;
            return;
        }

        double delta = newSize.Height - _lastHeight;
        PixelRect workArea = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

        if (delta > ExpansionHeightThreshold)
        {
            // Growing a lot (transcript panel opening) - remember where we
            // were *before* the clip-avoidance shift below runs, so closing
            // the panel can snap back to it.
            _restingTop = Position.Y;
        }
        else if (delta < -ExpansionHeightThreshold)
        {
            // Shrinking back down (transcript panel closing) - return to
            // the position from before it opened.
            Position = Position.WithY((int)_restingTop);
        }

        _lastHeight = newSize.Height;

        // Opening the transcript panel can make the window taller than the
        // screen has room for below its current Top - rather than let it
        // run off the bottom edge, slide it up just enough to stay fully
        // visible. newSize is in DIPs, Position/workArea are physical
        // pixels (same unit mismatch already fixed in the constructor's
        // initial placement) - convert via RenderScaling before comparing.
        double heightPhysical = newSize.Height * RenderScaling;
        double overflow = Position.Y + heightPhysical - workArea.Bottom;
        if (overflow > 0)
        {
            Position = Position.WithY((int)Math.Max(workArea.Y, Position.Y - overflow));
        }

        // Defensive horizontal clamp - Width is fixed post-construction so
        // this never needs to *react* to anything, it just guarantees the
        // window can't end up straddling the screen's right edge (which is
        // what the initial-position DPI/physical-pixel bug above did)
        // regardless of whatever else touches Position over the window's
        // lifetime.
        int maxX = workArea.Right - (int)(Width * RenderScaling);
        if (Position.X > maxX)
        {
            Position = Position.WithX(maxX);
        }
        else if (Position.X < workArea.X)
        {
            Position = Position.WithX(workArea.X);
        }
    }

    private void PushState()
    {
        if (_pendingModeSwitch is { } pending && !_assistant.IsBusy)
        {
            _pendingModeSwitch = null;
            _ = _assistant.SwitchActivationModeAsync(pending);
        }

        var state = new OverlayState(
            IsBusy: _assistant.IsBusy,
            IsSpeaking: _assistant.IsSpeaking,
            Asleep: !_assistant.Engaged,
            Mode: _assistant.Mode,
            Transcript: _assistant.SnapshotTranscript(),
            ActivityHistory: _assistant.SnapshotActivityHistory(),
            ChromeLinked: _assistant.ChromeLinked,
            GmailLinked: _assistant.GmailLinked,
            CurrentActivity: _assistant.CurrentActivity,
            PendingGate2Prompt: _assistant.PendingGate2Prompt,
            NeedsGoogleCredentials: _assistant.NeedsGoogleCredentials,
            GoogleConnecting: _assistant.GoogleConnecting,
            GoogleConnectError: _assistant.GoogleConnectError);
        _skins[_activeIndex].ApplyState(state);

        // See IOverlaySkin.IsCollapsed's own doc comment - the floor only
        // makes sense while the full panel is actually showing.
        MinHeight = _skins[_activeIndex].IsCollapsed ? 0 : OverlayLayout.PanelMinHeight;

        // See IOverlaySkin.IsMaximized's own doc comment. Direct Width
        // assignment, not SizeToContent - grows/shrinks around
        // _anchoredRightPhysical (the right edge, not the left) so the
        // panel widens toward the screen's center instead of running off
        // whichever edge it's docked near.
        double targetWidth = _skins[_activeIndex].IsMaximized ? OverlayLayout.MaximizedPanelWidth : OverlayLayout.PanelWidth;
        if (Math.Abs(Width - targetWidth) > 0.5)
        {
            Width = targetWidth;
            Position = Position.WithX((int)(_anchoredRightPhysical - targetWidth * _scaling));
        }

        if (state.PendingGate2Prompt is { } gate2Prompt)
        {
            _popup.Show(gate2Prompt);
        }
        else
        {
            _popup.HideImmediate();
        }

        if (state.NeedsGoogleCredentials)
        {
            _credentialsPopup.Show();
        }
        else
        {
            _credentialsPopup.HideImmediate();
        }

        _credentialsPopup.ApplyConnectState(state.GoogleConnecting, state.GoogleConnectError);
    }

    private System.Threading.Tasks.Task SwitchActivationModeGuarded(ActivationMode mode)
    {
        if (_assistant.IsBusy)
        {
            _pendingModeSwitch = mode;
            return System.Threading.Tasks.Task.CompletedTask;
        }

        return _assistant.SwitchActivationModeAsync(mode);
    }

    private void CycleSkin()
    {
        // Confirmed live as a real gap: each skin's collapsed/maximized
        // state was entirely private, so cycling away from a maximized (or
        // collapsed) skin and back silently reset to the plain minimized
        // panel instead of carrying the resting state the user actually
        // left it in.
        bool wasCollapsed = _skins[_activeIndex].IsCollapsed;
        bool wasMaximized = _skins[_activeIndex].IsMaximized;

        _skins[_activeIndex].Deactivate();
        _activeIndex = (_activeIndex + 1) % _skins.Length;
        _skins[_activeIndex].SetMaximized(wasMaximized);
        _skins[_activeIndex].SetCollapsed(wasCollapsed);
        _contentHost.Children.RemoveAt(0);
        _contentHost.Children.Insert(0, _skins[_activeIndex].Root);
        _popup.ApplyStyle(_skins[_activeIndex].PopupStyle);
        _credentialsPopup.ApplyStyle(_skins[_activeIndex].PopupStyle);
        _skins[_activeIndex].Activate();
        PushState();
        SaveState();
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // While either popup is up (Gate 2 review or a Google-credentials
        // prompt), its card/buttons/text fields are still the only thing
        // meant to be reachable - but a click that lands on the popup's
        // own backdrop (its empty surrounding area, not the card) can't
        // possibly have been aimed at a control there, so it's safe to
        // let that reposition the whole window, popup included. Confirmed
        // live as a real bug: any click anywhere while a popup was shown
        // used to block dragging unconditionally, making the window
        // immovable for as long as either popup stayed up - including the
        // credentials prompt, which can land somewhere inconvenient with
        // no way to move it out of the way.
        bool clickedPopupBackdrop = (_popup.IsShown && ReferenceEquals(e.Source, _popup.Backdrop))
            || (_credentialsPopup.IsShown && ReferenceEquals(e.Source, _credentialsPopup.Backdrop));

        if ((_popup.IsShown || _credentialsPopup.IsShown) && !clickedPopupBackdrop)
        {
            return;
        }

        if (e.Source is Visual source && FindAncestorButton(source) is not null)
        {
            return;
        }

        BeginMoveDrag(e);
        _restingTop = Position.Y; // a deliberate drag always becomes the new resting spot
        _anchoredRightPhysical = Position.X + (int)(Width * _scaling);
        SaveState();
    }

    private static Button? FindAncestorButton(Visual source)
    {
        Visual? current = source;
        while (current is not null)
        {
            if (current is Button button)
            {
                return button;
            }

            current = current.GetVisualParent();
        }

        return null;
    }

    // File format: left,top,activeSkinIndex,arcAlternateTheme,auraAlternateTheme.
    private bool TryLoadState(out PixelPoint position, out int activeIndex, out bool arcAlternateTheme, out bool auraAlternateTheme)
    {
        position = default;
        activeIndex = 0;
        arcAlternateTheme = false;
        auraAlternateTheme = false;
        try
        {
            if (!File.Exists(_positionFilePath))
            {
                return false;
            }

            string[] parts = File.ReadAllText(_positionFilePath).Split(',');
            if (parts.Length < 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double left) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double top))
            {
                return false;
            }

            position = new PixelPoint((int)left, (int)top);

            // parts[2] used to be a numeric skin index in this same format
            // (unchanged across the WebView2 detour and back) - "arc"/
            // "web"/"aura" strings from that experiment would land here too
            // if a save from that version is still on disk; fall back to
            // index 0 for anything that doesn't parse as an integer.
            if (parts.Length >= 5)
            {
                if (int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                {
                    activeIndex = idx;
                }

                arcAlternateTheme = parts[3] == "1";
                auraAlternateTheme = parts[4] == "1";
            }

            // Ignore a saved position that's no longer on any screen (e.g. a
            // monitor was unplugged since) rather than opening off-screen.
            PixelPoint loadedPosition = position;
            return Screens.All.Any(s => s.Bounds.Contains(loadedPosition));
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_positionFilePath)!);
            string arcAlt = _skins.Length > 0 && _skins[0].IsAlternateTheme ? "1" : "0";
            string auraAlt = _skins.Length > 2 && _skins[2].IsAlternateTheme ? "1" : "0";
            string content = string.Join(',',
                Position.X.ToString(CultureInfo.InvariantCulture),
                Position.Y.ToString(CultureInfo.InvariantCulture),
                _activeIndex.ToString(CultureInfo.InvariantCulture),
                arcAlt,
                auraAlt);
            File.WriteAllText(_positionFilePath, content);
        }
        catch (IOException)
        {
            // Not critical - the window just won't remember its state this
            // time.
        }
    }
}
