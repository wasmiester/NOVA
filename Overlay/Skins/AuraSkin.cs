using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nova;

// AURA chrome: soft pastel gradient background, frosted-glass-style
// translucent panel, rounded corners throughout, orb + badge composed
// from AuraFace/AuraBadge. Two lightness themes (light default, dark).
internal sealed class AuraSkin : IOverlaySkin
{
    private readonly Border _root;
    private readonly AuraFace _face;
    private readonly AuraBadge _badge;
    private readonly TextBlock _wordmark;
    private readonly TextBlock _stateLabel;
    private readonly TextBlock _subLabel;
    private readonly ProgressBar _progressBar;
    private readonly Button _modeButton;
    private readonly Button _sleepButton;
    private readonly Button _themeButton;
    private readonly Button[] _chromeButtons;
    private readonly TranscriptPanel _transcript;
    private readonly ActivityLogPanel _activityLog;
    private readonly TextBlock _activityLabel;
    private readonly TextBlock _conversationLabel;
    private readonly Border _activityLogBox;
    private readonly TextBox _typeInput;
    private readonly Button _sendButton;
    private readonly Grid _typeRow;
    private readonly Border _chromeChipBorder;
    private readonly Border _gmailChipBorder;
    private readonly Ellipse _chromeDot;
    private readonly Ellipse _gmailDot;
    private readonly TextBlock _chromeChipText;
    private readonly TextBlock _gmailChipText;
    private readonly Grid _layout;
    private readonly Button _pill;
    private readonly Border _pillDot;
    private readonly TextBlock _pillText;
    private readonly TextBlock _pillChevron;
    private OverlaySkinActions? _actions;
    private ActivationMode _mode = ActivationMode.Prompted;
    private bool _engaged = true;
    private bool _lightTheme = true;
    private AuraPalette _palette = AuraPalette.Light;

    public Control Root => _root;

    public AuraSkin()
    {
        AuraPalette palette = AuraPalette.Light;
        _face = new AuraFace(palette);
        _badge = new AuraBadge(AuraPalette.Accent2);

        // A Canvas (not a Grid+margin hack) for absolute top-right badge
        // placement - matches .aura .wifi-badge's position:absolute;
        // right/top in the source mockup.
        // box-shadow: 0 20px 60px rgba(accent,.45), 0 0 0 14px rgba(255,255,
        // 255,.5) on .aura .orb in the mockup - the orb's own ambient pink
        // glow plus the white "soap bubble" edge ring.
        var faceCanvas = new Canvas
        {
            Width = 140,
            Height = 140,
            Effect = new DropShadowEffect { Color = AuraPalette.Accent, BlurRadius = 30, OffsetX = 0, OffsetY = 0, Opacity = 0.4 },
        };
        var haloRing = new Ellipse
        {
            Width = 154,
            Height = 154,
            Fill = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)),
        };
        Canvas.SetLeft(haloRing, -7);
        Canvas.SetTop(haloRing, -7);
        faceCanvas.Children.Add(haloRing);
        Canvas.SetLeft(_face.Root, 0);
        Canvas.SetTop(_face.Root, 0);
        faceCanvas.Children.Add(_face.Root);
        Canvas.SetRight(_badge.Root, -6);
        Canvas.SetTop(_badge.Root, -6);
        faceCanvas.Children.Add(_badge.Root);

        _wordmark = new TextBlock { Text = "Nova", FontSize = 24, FontWeight = FontWeight.Bold };

        _themeButton = MakeChromeButton("◐");
        _themeButton.Click += (_, _) => ToggleThemeClicked();
        _sleepButton = MakeChromeButton("☾");
        _sleepButton.Click += (_, _) => _actions?.SetEngaged(!_engaged);
        var cycleButton = MakeChromeButton("⟳");
        cycleButton.Click += (_, _) => _actions?.CycleSkin();
        var closeButton = MakeChromeButton("✕");
        closeButton.Click += (_, _) => _actions?.Close();

        // Widths/heights match the source design mockup's own .aura-log/
        // .aura-transcript exactly - see OverlayLayout.MaximizedPanelWidth's
        // own doc comment for the full ratio/gap reasoning.
        _transcript = new TranscriptPanel(BuildTranscriptRow, width: OverlayLayout.ConversationColumnWidth, thumbColor: Color.FromArgb(179, 122, 108, 190), height: OverlayLayout.TranscriptHeight);
        _activityLog = new ActivityLogPanel(BuildActivityRow, width: OverlayLayout.ActivityColumnWidth, thumbColor: Color.FromArgb(179, 122, 108, 190), height: OverlayLayout.ActivityLogHeight);
        _activityLabel = new TextBlock { Text = "activity", FontSize = 12, Margin = new Thickness(2, 10, 0, 4), IsVisible = false };
        _conversationLabel = new TextBlock { Text = "conversation", FontSize = 12, Margin = new Thickness(2, 10, 0, 4), IsVisible = false };
        var maximizeButton = MakeChromeButton("☰");
        maximizeButton.Click += (_, _) => SetMaximized(!_transcript.IsVisible);
        // A single-line "minimize" glyph, distinct from the pill's own
        // down-chevron - sits right next to maximize since collapse/expand
        // is the same axis of control (how much of the panel shows), just
        // one more step past minimized.
        var collapseButton = MakeChromeButton("─");
        collapseButton.Click += (_, _) => SetCollapsed(true);
        _chromeButtons = [_themeButton, _sleepButton, maximizeButton, collapseButton, cycleButton, closeButton];

        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal };
        headerButtons.Children.Add(_themeButton);
        headerButtons.Children.Add(_sleepButton);
        headerButtons.Children.Add(maximizeButton);
        headerButtons.Children.Add(collapseButton);
        headerButtons.Children.Add(cycleButton);
        headerButtons.Children.Add(closeButton);

        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(headerButtons, Dock.Right);
        header.Children.Add(_wordmark);
        header.Children.Add(headerButtons);

        // border-radius: 999px in the mockup relies on CSS auto-clamping the
        // radius to half the element's height for a true capsule/stadium
        // shape - Avalonia's Border.CornerRadius does no such clamping, so
        // a known fixed Height with CornerRadius = Height / 2 is the actual
        // capsule.
        const double ModeButtonHeight = 30;
        _modeButton = new Button
        {
            Content = "Prompted",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            // linear-gradient(120deg, accent, accent-2) in the mockup.
            Background = MakeAngledGradient(AuraPalette.Accent, AuraPalette.Accent2, 30),
            BorderThickness = new Thickness(0),
            Height = ModeButtonHeight,
            Padding = new Thickness(16, 0, 16, 0),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        FlatButtonStyle.Apply(_modeButton, cornerRadius: ModeButtonHeight / 2);
        _modeButton.Click += (_, _) => _actions?.SwitchActivationMode(_mode == ActivationMode.Prompted ? ActivationMode.KeyBind : ActivationMode.Prompted);

        // A long activity string ("searching memory for \"...\"") used to
        // just get hard-truncated at a fixed character count - lost
        // information rather than showing it. A Viewbox (DownOnly so short
        // text like "listening…" never gets scaled *up*) shrinks the whole
        // line to fit the panel's width instead, so it stays fully legible
        // just smaller - TruncateActivity below still exists as a safety
        // net so a pathological wall of text doesn't shrink to an
        // illegible sliver, just raised well past the old 36-char cutoff.
        _stateLabel = new TextBlock { Text = "listening…", FontSize = 18 };
        var stateLabelBox = new Viewbox
        {
            Child = _stateLabel,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 2),
        };
        _subLabel = new TextBlock { Text = "just start talking", FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center };
        // Indeterminate - there's no real "% complete" for an agentic task
        // with an unknown number of tool-call rounds ahead of it, so this
        // is a sliding/pulsing "actively working" signal, not a progress
        // claim. Swaps in for _subLabel's idle hint while a task is
        // running (see ApplyState) rather than sitting alongside it.
        _progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            IsVisible = false,
            Height = 6,
            Width = 140,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            Foreground = MakeAngledGradient(AuraPalette.Accent, AuraPalette.Accent2, 30),
        };

        // .aura .status-bar { justify-content: space-between; } .aura .chip
        // { background: var(--glass-bg-hi); border: 1px solid
        // var(--glass-border); border-radius: 999px; } .aura .chip i {
        // width:7px; height:7px; border-radius:50%; background:
        // linear-gradient(accent,accent2); } - pill chips with a small
        // gradient status dot. Backed by real Chrome/Gmail connection
        // state; the dot dims to a flat grey when not actually connected.
        _chromeDot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0) };
        _gmailDot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0) };
        // MaxWidth + ellipsis trimming, not an unconstrained auto-width pill -
        // "Chrome connected"/"Gmail connected" side by side can run wider
        // than the panel (272 DIP, minus the layout's own 18px margins),
        // which a plain StackPanel doesn't wrap or clip - it just renders
        // past the window's right edge. 76 leaves both chips (padding + dot
        // + text) comfortably inside the available ~236px even at their
        // longest, unlike WEB's fix (Grid star columns) which would stretch
        // AURA's snug capsule shape into a full-width block instead.
        _chromeChipText = new TextBlock { FontSize = 13, MaxWidth = 76, TextTrimming = TextTrimming.CharacterEllipsis };
        _gmailChipText = new TextBlock { FontSize = 13, MaxWidth = 76, TextTrimming = TextTrimming.CharacterEllipsis };
        var chromeChipContent = new StackPanel { Orientation = Orientation.Horizontal };
        chromeChipContent.Children.Add(_chromeDot);
        chromeChipContent.Children.Add(_chromeChipText);
        var gmailChipContent = new StackPanel { Orientation = Orientation.Horizontal };
        gmailChipContent.Children.Add(_gmailDot);
        gmailChipContent.Children.Add(_gmailChipText);
        const double ChipHeight = 26;
        _chromeChipBorder = new Border { BorderThickness = new Thickness(1), Height = ChipHeight, CornerRadius = new CornerRadius(ChipHeight / 2), Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 8, 0), Child = chromeChipContent };
        _gmailChipBorder = new Border { BorderThickness = new Thickness(1), Height = ChipHeight, CornerRadius = new CornerRadius(ChipHeight / 2), Padding = new Thickness(10, 0, 10, 0), Child = gmailChipContent };
        var chipsRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        chipsRow.Children.Add(_chromeChipBorder);
        chipsRow.Children.Add(_gmailChipBorder);

        // The avatar/state-tag/subtext/mode-button group centers vertically
        // in whatever space is left between the header and the transcript/
        // chips - a plain top-aligned StackPanel (the original approach)
        // left this group pinned right under the header with a growing gap
        // of dead space below it whenever AURA's own naturally shorter
        // content sat inside the taller shared PanelMinHeight (driven by
        // ARC, the tallest skin - see OverlayLayout). header stays its own
        // row rather than joining the centered group, since it's chrome
        // (wordmark + buttons), not part of the "look centered" content.
        var coreContent = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        coreContent.Children.Add(faceCanvas);
        coreContent.Children.Add(stateLabelBox);
        coreContent.Children.Add(_subLabel);
        coreContent.Children.Add(_progressBar);
        coreContent.Children.Add(_modeButton);

        // AURA's type-to-talk row: a pill-shaped glass field matching the
        // chip/card language elsewhere in this skin, rather than WEB/ARC's
        // squarer boxed input.
        _typeInput = new TextBox
        {
            PlaceholderText = "type instead of speak…",
            FontSize = 13,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(12, 6, 12, 6),
        };
        _sendButton = new Button
        {
            Content = "send",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 0, 14, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            // Confirmed live: at the narrower ~220px column width the
            // side-by-side maximize layout gives it, the Grid's arrange
            // pass was shrinking this Auto column below its own natural
            // size instead of letting the row simply run wider than the
            // column - a hard floor is the reliable fix regardless of the
            // exact arrange-time math.
            MinWidth = 58,
        };
        FlatButtonStyle.Apply(_sendButton, cornerRadius: 15);
        TypeToTalkInput.WireUp(_typeInput, _sendButton, text => _actions?.SendTypedText(text));
        _typeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        Grid.SetColumn(_typeInput, 0);
        Grid.SetColumn(_sendButton, 1);
        _typeInput.Margin = new Thickness(0, 0, 6, 0);
        _typeRow.Children.Add(_typeInput);
        _typeRow.Children.Add(_sendButton);

        // Side by side, not stacked - see OverlayLayout.MaximizedPanelWidth's
        // own doc comment for the exact width math this assumes. The
        // activity log gets the mockup's own frosted-glass box (.aura-log)
        // - confirmed live as a real gap: this was never actually carried
        // over, so it rendered as bare content with no box around it. The
        // transcript doesn't get one (per the mockup, .aura-transcript has
        // no box of its own) - each message already carries its own glass
        // card via BuildTranscriptRow, so an outer box would just double up.
        _activityLogBox = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(4, 8, 4, 8), IsVisible = false, Child = _activityLog.Root };
        var activityColumn = new StackPanel();
        activityColumn.Children.Add(_activityLabel);
        activityColumn.Children.Add(_activityLogBox);
        var conversationColumn = new StackPanel();
        conversationColumn.Children.Add(_conversationLabel);
        conversationColumn.Children.Add(_transcript.Root);
        conversationColumn.Children.Add(_typeRow);
        // Fixed pixel columns, not star-weighted - the panel's own overall
        // width is already fixed (OverlayLayout.MaximizedPanelWidth), and
        // these match the two panels' own explicit widths above exactly,
        // so there's nothing left for "*" to actually apportion.
        var bottomStack = new Grid { ColumnDefinitions = new ColumnDefinitions($"{OverlayLayout.ActivityColumnWidth},{OverlayLayout.ColumnGap},{OverlayLayout.ConversationColumnWidth}") };
        Grid.SetColumn(activityColumn, 0);
        Grid.SetColumn(conversationColumn, 2);
        bottomStack.Children.Add(activityColumn);
        bottomStack.Children.Add(conversationColumn);

        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(coreContent, 1);
        Grid.SetRow(bottomStack, 2);
        stack.Children.Add(header);
        stack.Children.Add(coreContent);
        stack.Children.Add(bottomStack);

        // WEB's edge-to-edge chrome doesn't apply here - AURA's status row
        // is just centered pill chips, matching the mockup's own design -
        // but it shares WEB's other bug: wrapping everything (chipsRow
        // included) in one Margin(18) StackPanel lets any extra height from
        // OverlayLayout.PanelMinHeight land as a gap *below* the chips
        // instead of pinning them to the true bottom edge. A 2-row Grid
        // fixes it the same way: the Star row absorbs the extra space, the
        // Auto row (chipsRow) stays flush against the bottom.
        _layout = new Grid { Margin = new Thickness(18) };
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(stack, 0);
        Grid.SetRow(chipsRow, 1);
        _layout.Children.Add(stack);
        _layout.Children.Add(chipsRow);

        // The collapsed resting state - see ArcSkin's own pill for the full
        // reasoning (a plain dot instead of a live AuraFace instance, same
        // cost tradeoff). Own margin, not inherited from _layout's - the
        // pill sits alongside _layout as a sibling now, not inside it.
        _pillDot = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Colors.White, 0),
                    new GradientStop(AuraPalette.Accent2, 0.45),
                    new GradientStop(AuraPalette.Accent, 1),
                },
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pillText = new TextBlock
        {
            Text = "listening…",
            FontSize = 13,
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _pillChevron = new TextBlock { Text = "▾", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var pillInner = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(4, 0, 4, 0) };
        Grid.SetColumn(_pillDot, 0);
        Grid.SetColumn(_pillText, 1);
        Grid.SetColumn(_pillChevron, 2);
        pillInner.Children.Add(_pillDot);
        pillInner.Children.Add(_pillText);
        pillInner.Children.Add(_pillChevron);
        _pill = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(18, 0, 18, 0),
            Height = 60,
            Margin = new Thickness(18),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = pillInner,
            IsVisible = false,
        };
        FlatButtonStyle.Apply(_pill, cornerRadius: 28, stretchContent: true);
        _pill.Click += (_, _) => SetCollapsed(false);

        var bodyOrPill = new Grid();
        bodyOrPill.Children.Add(_layout);
        bodyOrPill.Children.Add(_pill);

        // Same fix as ARC/WEB - the 5-button header row is wider than the
        // orb, which otherwise drives the window's natural size, clipping
        // the rightmost chrome buttons outside it.
        _root = new Border { Width = OverlayLayout.PanelWidth, CornerRadius = new CornerRadius(28), Child = bodyOrPill };
        ApplyPalette(palette);
    }

    public void ApplyState(OverlayState state)
    {
        _engaged = !state.Asleep;
        _mode = state.Mode;
        _modeButton.Content = state.Mode == ActivationMode.Prompted ? "Prompted" : "Key Bind";

        _face.ApplyState(state.Asleep);
        _badge.ApplyState(state.IsSpeaking && !state.Asleep);

        // While she's silently working (reading a page, filling a field,
        // searching memory), the state label shows *that* instead of the
        // generic listening… - the same text already printed to the
        // console, so the overlay stops looking idle during a long task.
        bool working = !state.Asleep && !state.IsSpeaking && state.CurrentActivity is not null;
        string stateText = state.Asleep
            ? "sleeping…"
            : state.IsSpeaking
                ? "speaking…"
                : working
                    ? TruncateActivity(state.CurrentActivity!).ToLowerInvariant()
                    : "listening…";
        _stateLabel.Text = stateText;
        _subLabel.Text = state.Asleep ? "press ctrl+alt+space to wake" : "just start talking";

        // The collapsed pill mirrors the same text/state live - see
        // ArcSkin's own pill for the full reasoning.
        _pillText.Text = stateText;
        _pillDot.Opacity = state.Asleep ? 0.45 : state.IsSpeaking ? 1.0 : working ? 0.8 : 0.6;
        // A task in progress swaps the idle hint for the indeterminate bar
        // instead of showing both - "just start talking" doesn't apply
        // once she's already mid-task, and showing an inert hint next to
        // an active progress bar would read as contradictory.
        bool showProgress = state.IsBusy && !state.Asleep;
        _subLabel.IsVisible = !showProgress;
        _progressBar.IsVisible = showProgress;

        // .sleep-toggle[aria-pressed="true"] in the mockup: solid
        // accent-gradient fill + glow while asleep, instead of only the
        // face/label communicating it.
        if (state.Asleep)
        {
            _sleepButton.Background = MakeAngledGradient(AuraPalette.Accent, AuraPalette.Accent2, 30);
            _sleepButton.Foreground = Brushes.White;
        }
        else
        {
            _sleepButton.Background = new SolidColorBrush(_palette.GlassHi);
            _sleepButton.Foreground = new SolidColorBrush(_palette.Ink);
        }

        SetChip(_chromeChipText, _chromeDot, "Chrome", state.ChromeLinked);
        SetChip(_gmailChipText, _gmailDot, "Gmail", state.GmailLinked);

        _transcript.Update(state.Transcript);
        _activityLog.Update(state.ActivityHistory);
    }

    private void SetChip(TextBlock text, Ellipse dot, string label, bool linked)
    {
        text.Text = $"{label} {(linked ? "connected" : "offline")}";
        text.Foreground = new SolidColorBrush(_palette.Ink);
        dot.Fill = linked
            ? MakeAngledGradient(AuraPalette.Accent, AuraPalette.Accent2, 30)
            : new SolidColorBrush(_palette.InkMute);
    }

    // Confirm popup (see ConfirmPopup) styled to match AURA's own frosted-
    // glass language: the same pink "pending" tag color already used for a
    // transcript row's "waiting on you" treatment (PendingTagColor, right
    // above BuildTranscriptRow) as the header/card-border accent, the
    // existing accent gradient for the primary Confirm action. Built fresh
    // from _palette on every access (not cached) since AuraSkin's own
    // theme toggle allocates *new* brush instances rather than mutating
    // existing ones (see ApplyPalette) - unlike ARC, a cached copy here
    // would go stale after a light/dark toggle.
    public ConfirmPopupStyle PopupStyle
    {
        get
        {
            var pendingBrush = new SolidColorBrush(PendingTagColor);
            return new ConfirmPopupStyle(
                HeaderText: "confirm action",
                CredentialsHeaderText: "connect google",
                Backdrop: new SolidColorBrush(Color.FromArgb(140, _palette.Ink.R, _palette.Ink.G, _palette.Ink.B)),
                CardBackground: new SolidColorBrush(_palette.GlassHi),
                CardBorder: pendingBrush,
                CardCornerRadius: 20,
                PanelCornerRadius: 28,
                HeaderBrush: pendingBrush,
                BodyBrush: new SolidColorBrush(_palette.Ink),
                MutedBrush: new SolidColorBrush(_palette.InkMute),
                ErrorBrush: pendingBrush,
                Font: FontFamily.Default,
                ConfirmBackground: MakeAngledGradient(AuraPalette.Accent, AuraPalette.Accent2, 30),
                ConfirmForeground: Brushes.White,
                ConfirmBorder: Brushes.Transparent,
                CancelBackground: new SolidColorBrush(_palette.GlassHi),
                CancelForeground: new SolidColorBrush(_palette.Ink),
                CancelBorder: new SolidColorBrush(_palette.GlassBorder),
                InputBackground: new SolidColorBrush(_palette.Glass),
                InputBorder: new SolidColorBrush(_palette.GlassBorder),
                InputForeground: new SolidColorBrush(_palette.Ink),
                InputCaret: new SolidColorBrush(AuraPalette.Accent),
                BorderThickness: 1,
                ButtonCornerRadius: 15,
                PixelShadowColor: null);
        }
    }

    public void ToggleTheme() => ToggleThemeClicked();

    public bool IsAlternateTheme => !_lightTheme;

    private void ToggleThemeClicked()
    {
        _lightTheme = !_lightTheme;
        ApplyPalette(_lightTheme ? AuraPalette.Light : AuraPalette.Dark);
        _actions?.ThemeChanged();
    }

    private void ApplyPalette(AuraPalette palette)
    {
        _palette = palette;
        _root.Background = BuildGroundBrush(palette.GroundStops);
        _wordmark.Foreground = new SolidColorBrush(palette.Ink);
        _stateLabel.Foreground = new SolidColorBrush(palette.Ink);
        _subLabel.Foreground = new SolidColorBrush(palette.InkMute);

        var buttonBackground = new SolidColorBrush(palette.GlassHi);
        var buttonForeground = new SolidColorBrush(palette.Ink);
        foreach (Button button in _chromeButtons)
        {
            button.Background = buttonBackground;
            button.Foreground = buttonForeground;
        }

        // .sleep-toggle[aria-pressed="true"] applies to the theme swatch too
        // in the mockup - dark mode is its "pressed" state.
        if (!_lightTheme)
        {
            _themeButton.Background = MakeAngledGradient(AuraPalette.Accent, AuraPalette.Accent2, 30);
            _themeButton.Foreground = Brushes.White;
        }

        // .aura .chip { border: 1px solid var(--glass-border); } in the
        // mockup - a distinct token from --glass-bg-hi, not the same brush
        // as the chip's own background.
        var chipBorderBrush = new SolidColorBrush(palette.GlassBorder);
        _chromeChipBorder.Background = buttonBackground;
        _chromeChipBorder.BorderBrush = chipBorderBrush;
        _gmailChipBorder.Background = buttonBackground;
        _gmailChipBorder.BorderBrush = chipBorderBrush;
        _chromeChipText.Foreground = buttonForeground;
        _gmailChipText.Foreground = buttonForeground;

        _activityLabel.Foreground = new SolidColorBrush(palette.InkMute);
        _conversationLabel.Foreground = new SolidColorBrush(palette.InkMute);
        _activityLogBox.Background = new SolidColorBrush(palette.Glass);
        _activityLogBox.BorderBrush = new SolidColorBrush(palette.GlassBorder);
        _pillText.Foreground = new SolidColorBrush(palette.Ink);
        _pillChevron.Foreground = new SolidColorBrush(palette.InkMute);
        _typeInput.Background = new SolidColorBrush(palette.Glass);
        _typeInput.BorderBrush = new SolidColorBrush(palette.GlassBorder);
        _typeInput.Foreground = new SolidColorBrush(palette.Ink);
        _typeInput.CaretBrush = new SolidColorBrush(AuraPalette.Accent);
        _sendButton.Background = MakeAngledGradient(AuraPalette.Accent, AuraPalette.Accent2, 30);

        _face.ApplyPalette(palette);
        _transcript.SetRowFactory(BuildTranscriptRow);
        _activityLog.SetRowFactory(BuildActivityRow);
    }

    // AURA's frosted card from the source mockup: a translucent, blurred-
    // glass rounded card with a small tag above the text - see .aura .line
    // in the mockup (no backdrop-filter: blur() equivalent available, so
    // this is a plain semi-transparent fill).
    // .aura .line.pending in the mockup: brighter glass fill, accent
    // border, pink tag with a " · waiting on you" suffix, for a Gate 1/
    // Gate 2 "should I go ahead?" ask.
    private static readonly Color PendingTagColor = Color.FromRgb(0xe8, 0x5f, 0xa4);

    private Control BuildTranscriptRow(TranscriptEntry entry)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = (entry.IsUser ? "You" : "Nova") + (entry.IsPending ? " · waiting on you" : ""),
            Foreground = entry.IsPending ? new SolidColorBrush(PendingTagColor) : new SolidColorBrush(_palette.InkMute),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        });
        content.Children.Add(new TextBlock
        {
            Text = entry.Text,
            Foreground = new SolidColorBrush(_palette.Ink),
            FontSize = 18,
            TextWrapping = TextWrapping.Wrap,
        });

        return new Border
        {
            Background = new SolidColorBrush(entry.IsPending ? _palette.GlassHi : _palette.Glass),
            BorderBrush = new SolidColorBrush(entry.IsPending ? AuraPalette.Accent : _palette.GlassHi),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = content,
        };
    }

    // A quieter cousin of BuildTranscriptRow's card - a single glass pill
    // per step instead of a two-line tag+text card, since a tool-call
    // trace doesn't carry the same "who said this" weight a conversation
    // turn does. Literal terminal styling (see WEB) doesn't fit AURA's
    // soft-glass identity.
    private (Control Row, TextBlock? Dots) BuildActivityRow(ActivityEntry entry)
    {
        var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.Fill = entry.InProgress
            ? MakeAngledGradient(AuraPalette.Accent, AuraPalette.Accent2, 30)
            : new SolidColorBrush(_palette.InkMute);

        var text = new TextBlock
        {
            Text = entry.Text,
            Foreground = new SolidColorBrush(entry.InProgress ? _palette.Ink : _palette.InkMute),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextBlock? dots = null;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(dot);
        content.Children.Add(text);
        if (entry.InProgress)
        {
            dots = new TextBlock { Foreground = new SolidColorBrush(_palette.Ink), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(dots);
        }

        var row = new Border
        {
            Background = new SolidColorBrush(_palette.Glass),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Child = content,
        };
        return (row, dots);
    }

    // Swaps between the full panel and the slim resting pill - see
    // ArcSkin's own SetCollapsed for the full reasoning.
    public void SetCollapsed(bool collapsed)
    {
        // Confirmed live as a real bug: collapsing while maximized left
        // _transcript/_activityLog still marked visible and _root.Width at
        // MaximizedPanelWidth, so the pill rendered at the wide maximized
        // width instead of the normal resting one. Un-maximizing first
        // resets both width and content visibility together, rather than
        // just patching the width and leaving stale wide content behind
        // the pill for whenever it next expands.
        if (collapsed)
        {
            SetMaximized(false);
        }

        _layout.IsVisible = !collapsed;
        _pill.IsVisible = collapsed;
    }

    // Widens both this panel's own root and (via OverlayWindow's own
    // IsMaximized polling) the window together, in lockstep - see
    // ArcSkin's own SetMaximized for the full reasoning.
    public void SetMaximized(bool maximized)
    {
        _transcript.IsVisible = maximized;
        _activityLog.IsVisible = maximized;
        _activityLabel.IsVisible = maximized;
        _conversationLabel.IsVisible = maximized;
        _typeRow.IsVisible = maximized;
        // The wrapping frame Border doesn't auto-collapse just because its
        // (now zero-height) child does - left always-visible, it would
        // still paint its own empty glass box while minimized.
        _activityLogBox.IsVisible = maximized;
        _root.Width = maximized ? OverlayLayout.MaximizedPanelWidth : OverlayLayout.PanelWidth;
    }

    public bool IsCollapsed => _pill.IsVisible;

    public bool IsMaximized => _transcript.IsVisible;

    public void AttachActions(OverlaySkinActions actions) => _actions = actions;

    public void Activate() => _face.Activate();

    public void Deactivate() => _face.Deactivate();

    // linear-gradient(160deg, stop 0%, stop 22%, stop 52%, stop 78%, stop
    // 100%) from the mockup - CSS's 0deg points up and increases clockwise,
    // converted to Avalonia's unit-square start/end points (160deg is
    // close to straight down with a slight rightward lean).
    private static LinearGradientBrush BuildGroundBrush(Color[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.329, 0.030, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.671, 0.970, RelativeUnit.Relative),
        };
        double[] offsets = [0, 0.22, 0.52, 0.78, 1.0];
        for (int i = 0; i < stops.Length && i < offsets.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop(stops[i], offsets[i]));
        }

        return brush;
    }

    // Avalonia's LinearGradientBrush has no WPF-style (Color, Color, angle)
    // convenience constructor - this builds the equivalent by hand: a
    // gradient line through the center of the unit square, in the
    // direction implied by a WPF-convention angle (0deg = east, clockwise).
    // CSS's own angle convention (0deg = north) is converted to this one at
    // each call site as (cssAngle - 90), same as the WPF version did.
    private static LinearGradientBrush MakeAngledGradient(Color a, Color b, double wpfAngleDegrees)
    {
        double radians = wpfAngleDegrees * System.Math.PI / 180;
        double dx = System.Math.Cos(radians) * 0.5;
        double dy = System.Math.Sin(radians) * 0.5;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5 - dx, 0.5 - dy, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5 + dx, 0.5 + dy, RelativeUnit.Relative),
            GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
        };
    }

    private static Button MakeChromeButton(string glyph)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 23,
            Height = 23,
            FontSize = 12,
            Margin = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        FlatButtonStyle.Apply(button, cornerRadius: 12);
        return button;
    }

    // The narrow panel can't fit a long activity string ("searching memory
    // for \"...\"") on one line - cap it rather than let it wrap/overflow.
    // The state label now shrinks to fit via a Viewbox (see the
    // constructor) instead of relying on truncation for normal-length
    // activity text - this cutoff only exists as a floor so a truly
    // pathological string doesn't shrink to an illegible sliver.
    private static string TruncateActivity(string text) =>
        text.Length > 90 ? string.Concat(text.AsSpan(0, 90), "…") : text;
}
