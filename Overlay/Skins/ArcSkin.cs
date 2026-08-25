using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nova;

// ARC chrome: dark near-black panel, warm gold (default) or cyan
// hologram-globe face, monospace-flavored HUD labels. Ground/panel colors
// stay constant across both themes (not overridden in the mockup's cyan
// block) - only accent/warn/text-hi/text-lo/hair actually change, so those
// live as mutable brush *instances* whose .Color gets mutated on toggle
// (every TextBlock/Border/effect bound to the same brush instance then
// repaints automatically - the same technique ArcFace already uses
// internally for its pens).
internal sealed class ArcSkin : IOverlaySkin
{
    // Exact hex values from the source mockup's gold/cyan themes.
    private static readonly Color GoldAccent = Color.FromRgb(0xf8, 0xd4, 0x82);
    private static readonly Color GoldTextHi = Color.FromRgb(0xfc, 0xe9, 0xd6);
    private static readonly Color GoldTextLo = Color.FromRgb(0x8a, 0x74, 0x54);
    private static readonly Color GoldWarn = Color.FromRgb(0xff, 0x7a, 0x45);
    private static readonly Color CyanAccent = Color.FromRgb(0x84, 0xe4, 0xff);
    private static readonly Color CyanTextHi = Color.FromRgb(0xdc, 0xee, 0xfc);
    private static readonly Color CyanTextLo = Color.FromRgb(0x5c, 0x86, 0xa0);
    private static readonly Color CyanWarn = Color.FromRgb(0xff, 0xb4, 0x54);
    private static readonly SolidColorBrush PanelBrush = new(Color.FromRgb(14, 23, 35));
    private static readonly SolidColorBrush GroundBrush = new(Color.FromRgb(0x0a, 0x0e, 0x14));

    private readonly SolidColorBrush _accentBrush = new(GoldAccent);
    private readonly SolidColorBrush _textHiBrush = new(GoldTextHi);
    private readonly SolidColorBrush _textLoBrush = new(GoldTextLo);
    private readonly SolidColorBrush _warnBrush = new(GoldWarn);
    private readonly SolidColorBrush _warnBgBrush = new(Color.FromArgb(20, GoldWarn.R, GoldWarn.G, GoldWarn.B));
    private readonly SolidColorBrush _warnBorderBrush = new(Color.FromArgb(115, GoldWarn.R, GoldWarn.G, GoldWarn.B));
    // The type-to-talk Send button's subtle fill - same "low-alpha tint of
    // the accent" shape as _warnBgBrush above, just tracking _accentBrush's
    // own gold/cyan toggle instead of _warnBrush's.
    private readonly SolidColorBrush _accentBgBrush = new(Color.FromArgb(31, GoldAccent.R, GoldAccent.G, GoldAccent.B));
    private static readonly Color GoldHair = Color.FromRgb(0x3a, 0x2e, 0x1c);
    private static readonly Color CyanHair = Color.FromRgb(0x23, 0x41, 0x58);
    private readonly SolidColorBrush _hairBrush = new(GoldHair);
    private readonly DropShadowEffect _wordmarkGlow;
    private readonly DropShadowEffect _panelGlow;
    private readonly DropShadowEffect _faceGlow;

    private readonly Border _root;
    private readonly ArcFace _face;
    private readonly TextBlock _wordmark;
    private readonly TextBlock _stateLabel;
    private readonly TextBlock _subLabel;
    private readonly ProgressBar _progressBar;
    private readonly Button _modeButton;
    private readonly Button _sleepButton;
    private readonly Button _themeButton;
    private readonly Button _maximizeButton;
    private readonly TranscriptPanel _transcript;
    private readonly ActivityLogPanel _activityLog;
    private readonly TextBlock _chromeChip;
    private readonly TextBlock _gmailChip;
    private readonly StackPanel _body;
    private readonly TextBlock _activityLabel;
    private readonly Border _activityLogBox;
    private readonly Border _transcriptBox;
    private readonly TextBlock _conversationLabel;
    private readonly Grid _typeRow;
    private readonly Border _pill;
    private readonly Border _pillDot;
    private readonly DropShadowEffect _pillDotGlow;
    private readonly SolidColorBrush _pillDotBrush;
    private readonly TextBlock _pillText;
    private readonly TextBlock _pillDragHandle;
    private OverlaySkinActions? _actions;
    private ActivationMode _mode = ActivationMode.Prompted;
    private bool _engaged = true;
    private bool _goldTheme = true;

    public Control Root => _root;

    public ArcSkin()
    {
        // box-shadow/drop-shadow(0 0 24px rgba(accent,.35)) on .arc .face in
        // the mockup - the avatar's own ambient glow, distinct from the
        // wordmark's and the whole panel's own glows below.
        _faceGlow = new DropShadowEffect { Color = _accentBrush.Color, BlurRadius = 20, OffsetX = 0, OffsetY = 0, Opacity = 0.35 };
        _face = new ArcFace { HorizontalAlignment = HorizontalAlignment.Center, Effect = _faceGlow };

        _wordmarkGlow = new DropShadowEffect { Color = _accentBrush.Color, BlurRadius = 12, OffsetX = 0, OffsetY = 0, Opacity = 0.55 };
        _wordmark = new TextBlock
        {
            Text = "NOVA",
            Foreground = _accentBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Effect = _wordmarkGlow,
        };

        _themeButton = MakeChromeButton("◐");
        _themeButton.Click += (_, _) => ToggleThemeClicked();
        _sleepButton = MakeChromeButton("☾");
        _sleepButton.Click += (_, _) => _actions?.SetEngaged(!_engaged);
        var cycleButton = MakeChromeButton("⟳");
        cycleButton.Click += (_, _) => _actions?.CycleSkin();
        var closeButton = MakeChromeButton("✕");
        closeButton.Click += (_, _) => _actions?.Close();

        // Widths/heights match the source design mockup's own .arc-log/
        // .arc-transcript exactly - see OverlayLayout.MaximizedPanelWidth's
        // own doc comment for the full ratio/gap reasoning.
        _transcript = new TranscriptPanel(BuildTranscriptRow, width: OverlayLayout.ConversationColumnWidth, thumbColor: Color.FromArgb(204, GoldAccent.R, GoldAccent.G, GoldAccent.B), height: OverlayLayout.TranscriptHeight);
        _activityLog = new ActivityLogPanel(BuildActivityRow, width: OverlayLayout.ActivityColumnWidth, thumbColor: Color.FromArgb(204, GoldAccent.R, GoldAccent.G, GoldAccent.B), height: OverlayLayout.ActivityLogHeight);
        _maximizeButton = MakeChromeButton("☰");
        // A single-line "minimize" glyph, distinct from the pill's own
        // down-chevron - sits right next to maximize since collapse/expand
        // is the same axis of control (how much of the panel shows), just
        // one more step past minimized.
        var collapseButton = MakeChromeButton("─");
        collapseButton.Click += (_, _) => SetCollapsed(true);

        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal };
        headerButtons.Children.Add(_themeButton);
        headerButtons.Children.Add(_sleepButton);
        headerButtons.Children.Add(_maximizeButton);
        headerButtons.Children.Add(collapseButton);
        headerButtons.Children.Add(cycleButton);
        headerButtons.Children.Add(closeButton);

        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(headerButtons, Dock.Right);
        header.Children.Add(_wordmark);
        header.Children.Add(headerButtons);

        // A long activity string ("SEARCHING MEMORY FOR \"...\"") used to
        // just get hard-truncated at a fixed character count - lost
        // information rather than showing it. A Viewbox (DownOnly so short
        // text like LISTENING never gets scaled *up*) shrinks the whole
        // line to fit the panel's width instead, so it stays fully legible
        // just smaller - ActivityTextTruncation still exists as a safety
        // net so a pathological wall of text doesn't shrink to an
        // illegible sliver, just raised well past the old 36-char cutoff.
        _stateLabel = new TextBlock
        {
            Text = "LISTENING",
            Foreground = _accentBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 15,
        };
        var stateLabelBox = new Viewbox
        {
            Child = _stateLabel,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 10, 0, 2),
        };
        _subLabel = new TextBlock
        {
            Text = "JUST START TALKING",
            Foreground = _textLoBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // Indeterminate - there's no real "% complete" for an agentic task
        // with an unknown number of tool-call rounds ahead of it, so this
        // is a sliding/pulsing "actively working" signal, not a progress
        // claim. Swaps in for _subLabel's idle hint while a task is
        // running (see ApplyState) rather than sitting alongside it.
        _progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            IsVisible = false,
            Height = 3,
            Width = 140,
            CornerRadius = new CornerRadius(0),
            Background = _hairBrush,
            Foreground = _accentBrush,
        };

        _modeButton = new Button
        {
            Content = "PROMPTED",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = _warnBrush,
            Background = _warnBgBrush,
            BorderBrush = _warnBorderBrush,
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        FlatButtonStyle.Apply(_modeButton, cornerRadius: 0);
        _modeButton.Click += (_, _) => _actions?.SwitchActivationMode(_mode == ActivationMode.Prompted ? ActivationMode.KeyBind : ActivationMode.Prompted);

        // .arc .status-bar { border-top: 1px solid var(--hair); ... } .arc
        // .chip { border: 1px solid var(--hair); color: var(--text-lo); }
        // .arc .chip b { color: var(--accent); } - bordered (not filled)
        // chips with the theme-tracking hair/text-lo/accent brushes, unlike
        // WEB's filled dark chips. Backed by real Chrome/Gmail connection
        // state, same as WEB's.
        // Confirmed live as a real gap: a 76px MaxWidth (matching AuraSkin's
        // own cap) was tight enough for AURA's proportional-font "Chrome
        // connected" text, but Consolas at 12px runs noticeably wider per
        // character - "CHROME · LINKED"/"GMAIL · OFFLINE" measured closer to
        // ~115px, so both chips were forced to truncate hard, right at the
        // "· LINKED"/"· OFFLINE" half that actually carries the state,
        // while sitting close enough together (6px gap) to read as one
        // squished cluster rather than two legible chips. Two changes: a
        // wide-enough MaxWidth (140, comfortably past the longest realistic
        // text) so nothing truncates, and a WrapPanel instead of a fixed
        // horizontal row - side by side when there's room (always true once
        // maximized), stacked with normal spacing instead of clipped/squished
        // when there isn't (minimized mode's narrower 272 DIP panel).
        _chromeChip = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, MaxWidth = 140 };
        _gmailChip = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, MaxWidth = 140 };
        var chromeChipBorder = new Border { BorderBrush = _hairBrush, BorderThickness = new Thickness(1), Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 0, 6, 6), Child = _chromeChip };
        var gmailChipBorder = new Border { BorderBrush = _hairBrush, BorderThickness = new Thickness(1), Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 0, 0, 6), Child = _gmailChip };
        var chipsRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        chipsRow.Children.Add(chromeChipBorder);
        chipsRow.Children.Add(gmailChipBorder);
        var statusBar = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        statusBar.Children.Add(new Border { Height = 1, Background = _hairBrush, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Stretch });
        statusBar.Children.Add(chipsRow);

        _body = new StackPanel { Margin = new Thickness(18) };
        _body.Children.Add(header);
        _body.Children.Add(_face);
        _body.Children.Add(stateLabelBox);
        _body.Children.Add(_subLabel);
        _body.Children.Add(_progressBar);
        _body.Children.Add(_modeButton);

        // Activity feed above the transcript, both revealed together by
        // the same maximize toggle - see _maximizeButton's click handler
        // above. Labels match TranscriptScrollBarStyle's own small-caps
        // convention (see _arc-log-label in the design mockup this is
        // built from).
        _activityLabel = new TextBlock { Text = "ACTIVITY", Foreground = _textLoBrush, FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 10, 0, 4), IsVisible = false };
        _conversationLabel = new TextBlock { Text = "CONVERSATION", Foreground = _textLoBrush, FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 10, 0, 4), IsVisible = false };

        var typeInput = new TextBox
        {
            PlaceholderText = "type instead of speak…",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = GroundBrush,
            Foreground = _textHiBrush,
            BorderBrush = _hairBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 7, 9, 7),
        };
        var sendButton = new Button
        {
            Content = "SEND",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = _accentBrush,
            Background = _accentBgBrush,
            BorderBrush = _hairBrush,
            BorderThickness = new Thickness(1),
            // Confirmed live: zero vertical padding with no explicit Height
            // left this wrapping tight around the bare text line-height,
            // reading as squished next to typeInput right beside it - match
            // that sibling's own vertical padding instead.
            Padding = new Thickness(12, 7, 12, 7),
            Cursor = new Cursor(StandardCursorType.Hand),
            // Confirmed live: at the narrower ~220px column width the
            // side-by-side maximize layout gives it, the Grid's arrange
            // pass was shrinking this Auto column below its own natural
            // size instead of letting the row simply run wider than the
            // column - a hard floor is the reliable fix regardless of the
            // exact arrange-time math.
            MinWidth = 56,
        };
        FlatButtonStyle.Apply(sendButton, cornerRadius: 4);
        TypeToTalkInput.WireUp(typeInput, sendButton, text => _actions?.SendTypedText(text));
        _typeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        Grid.SetColumn(typeInput, 0);
        Grid.SetColumn(sendButton, 1);
        typeInput.Margin = new Thickness(0, 0, 6, 0);
        _typeRow.Children.Add(typeInput);
        _typeRow.Children.Add(sendButton);

        // Side by side, not stacked - see OverlayLayout.MaximizedPanelWidth's
        // own doc comment for the exact width math this assumes. Each
        // panel gets the mockup's own thin hairline frame (.arc-log/
        // .arc-transcript) - confirmed live as a real gap: this was never
        // actually carried over into the real implementation, so both
        // areas rendered as bare content floating with no box around them.
        _activityLogBox = new Border { BorderBrush = _hairBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 8, 0, 8), Child = _activityLog.Root, IsVisible = false };
        _transcriptBox = new Border { BorderBrush = _hairBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Child = _transcript.Root, IsVisible = false };
        var activityColumn = new StackPanel();
        activityColumn.Children.Add(_activityLabel);
        activityColumn.Children.Add(_activityLogBox);
        var conversationColumn = new StackPanel();
        conversationColumn.Children.Add(_conversationLabel);
        conversationColumn.Children.Add(_transcriptBox);
        conversationColumn.Children.Add(_typeRow);
        // Fixed pixel columns, not star-weighted - the panel's own overall
        // width is already fixed (OverlayLayout.MaximizedPanelWidth), and
        // these match the two panels' own explicit widths above exactly,
        // so there's nothing left for "*" to actually apportion.
        var splitRow = new Grid { ColumnDefinitions = new ColumnDefinitions($"{OverlayLayout.ActivityColumnWidth},{OverlayLayout.ColumnGap},{OverlayLayout.ConversationColumnWidth}") };
        Grid.SetColumn(activityColumn, 0);
        Grid.SetColumn(conversationColumn, 2);
        splitRow.Children.Add(activityColumn);
        splitRow.Children.Add(conversationColumn);
        _body.Children.Add(splitRow);

        _maximizeButton.Click += (_, _) => SetMaximized(!_transcript.IsVisible);

        _body.Children.Add(statusBar);

        // The collapsed resting state - a slim pill sitting in front of the
        // real panel above, not a fourth invented look (see the design
        // mockup this is built from: click the bar and it expands directly
        // into the real header/avatar/state, never a separate preview).
        // Small dot instead of a second ArcFace instance - a live-animated
        // duplicate of the ~200px particle avatar would double its render/
        // timer cost for a decorative collapsed-state indicator; a plain
        // glow dot reads the same states (color/opacity carries them, per
        // the mockup's own reasoning) far more cheaply.
        _pillDotBrush = new SolidColorBrush(GoldAccent);
        _pillDotGlow = new DropShadowEffect { Color = GoldAccent, BlurRadius = 10, OffsetX = 0, OffsetY = 0, Opacity = 0.6 };
        _pillDot = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = _pillDotBrush,
            Effect = _pillDotGlow,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pillText = new TextBlock
        {
            Text = "LISTENING",
            Foreground = _accentBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var pillChevron = new TextBlock { Text = "▾", Foreground = _textLoBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        // A small, dedicated drag handle - see IOverlaySkin.PillDragHandle's
        // own doc comment for why the rest of the pill deliberately isn't
        // one too. "⋮⋮" is the same grip idiom drag-to-reorder lists already
        // use elsewhere (Trello/Notion/Slack) - immediately recognizable as
        // "grab here," distinct from the dot/text/chevron cluster that
        // reads as clickable content.
        _pillDragHandle = new TextBlock { Text = "⋮⋮", Foreground = _textLoBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Cursor = new Cursor(StandardCursorType.SizeAll) };
        var pillInner = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        Grid.SetColumn(_pillDragHandle, 0);
        Grid.SetColumn(_pillDot, 1);
        Grid.SetColumn(_pillText, 2);
        Grid.SetColumn(pillChevron, 3);
        pillInner.Children.Add(_pillDragHandle);
        pillInner.Children.Add(_pillDot);
        pillInner.Children.Add(_pillText);
        pillInner.Children.Add(pillChevron);
        // Border, not Button - see IOverlaySkin.Pill's own doc comment for
        // why: this needs to be both a drag handle and a click target,
        // which a single Button can't do at once. Click-to-expand is
        // resolved by AvaloniaOverlayWindow itself (see its
        // OnWindowPointerPressed), not wired here.
        _pill = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(18, 0, 18, 0),
            Height = 60,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = pillInner,
            IsVisible = false,
        };

        // Grid, not a second Border - _root already supplies the shared
        // background/border/corner-radius/glow for both states, this just
        // decides which content fills it. Both children can occupy the
        // same implicit cell since only one is ever IsVisible at a time.
        var rootContent = new Grid();
        rootContent.Children.Add(_body);
        rootContent.Children.Add(_pill);

        _panelGlow = new DropShadowEffect { Color = _accentBrush.Color, BlurRadius = 40, OffsetX = 0, OffsetY = 0, Opacity = 0.25 };
        _root = new Border
        {
            // Same fixed width as WEB/AURA - all 3 skins render at the same
            // size so cycling between them doesn't resize the window
            // sideways. See OverlayLayout.
            Width = OverlayLayout.PanelWidth,
            Background = GroundBrush,
            BorderBrush = _hairBrush,
            BorderThickness = new Thickness(1),
            // The mockup's octagon clip-path read as sharp/jagged even
            // with a small per-vertex fillet - dropped per direct request
            // in favor of matching AURA's plain rounded-rectangle corners
            // exactly (same CornerRadius value).
            CornerRadius = new CornerRadius(28),
            Child = rootContent,
            // box-shadow: ... 0 0 60px rgba(accent,0.1) from the mockup -
            // the soft accent-tinted glow around the whole panel.
            Effect = _panelGlow,
        };

        // CornerRadius alone only rounds the Border's own background/border
        // paint - it doesn't constrain the glow Effect, which otherwise
        // blurs outward from the full rectangular bounds and shows up as a
        // faint square accent poking past the rounded corners. An explicit
        // rounded-rect Clip (same radius, rebuilt on resize like the old
        // octagon Clip was) keeps everything, glow included, bounded to
        // the true rounded shape.
        _root.SizeChanged += (_, e) => _root.Clip = RoundedRectClip.Build(e.NewSize.Width, e.NewSize.Height, 28);
    }

    public void ApplyState(OverlayState state)
    {
        _engaged = !state.Asleep;
        _mode = state.Mode;
        _modeButton.Content = state.Mode == ActivationMode.Prompted ? "PROMPTED" : "KEY BIND";
        _face.IsAsleep = state.Asleep;

        // While she's silently working (reading a page, filling a field,
        // searching memory), the state label shows *that* instead of the
        // generic LISTENING - the same text already printed to the
        // console, so the overlay stops looking idle during a long task.
        // Working is also its own distinct pill-dot tint below (between
        // idle and speaking) - not just a text change, same as the
        // maximize-mode design mockup's tint-bar exploration.
        bool working = !state.Asleep && !state.IsSpeaking && state.CurrentActivity is not null;
        string stateText = state.Asleep
            ? "STANDBY"
            : state.IsSpeaking
                ? "SPEAKING"
                : working
                    ? ActivityTextTruncation.Truncate(state.CurrentActivity!).ToUpperInvariant()
                    : "LISTENING";
        _stateLabel.Text = stateText;
        _subLabel.Text = state.Asleep ? "CTRL+ALT+SPACE TO WAKE" : "JUST START TALKING";

        // The collapsed pill mirrors the same text live, plus its own dot
        // opacity carrying the same 4 states the mockup's tint-bar demo
        // cycled through (listening/speaking/working/asleep) - color/glow
        // alone should read as "what's happening" without needing to
        // expand back to the full panel.
        _pillText.Text = stateText;
        _pillDotBrush.Color = _accentBrush.Color;
        _pillDotGlow.Opacity = state.Asleep ? 0.15 : state.IsSpeaking ? 0.85 : working ? 0.6 : 0.45;
        _pillDot.Opacity = state.Asleep ? 0.5 : 1.0;
        _stateLabel.Opacity = state.Asleep ? 0.55 : 1.0;
        // A task in progress swaps the idle hint for the indeterminate bar
        // instead of showing both - "JUST START TALKING" doesn't apply
        // once she's already mid-task, and showing an inert hint next to
        // an active progress bar would read as contradictory.
        bool showProgress = state.IsBusy && !state.Asleep;
        _subLabel.IsVisible = !showProgress;
        _progressBar.IsVisible = showProgress;

        // .asleep .wordmark { opacity:.4; text-shadow:none } in the mockup -
        // reads as "powered off" rather than just quiet.
        _wordmark.Opacity = state.Asleep ? 0.4 : 1.0;
        _wordmarkGlow.Opacity = state.Asleep ? 0 : 0.55;

        // .sleep-toggle[aria-pressed="true"] in the mockup: the button
        // itself fills solid accent when asleep, rather than only the
        // face/label communicating the state change.
        _sleepButton.Background = state.Asleep ? _accentBrush : PanelBrush;
        _sleepButton.Foreground = state.Asleep ? GroundBrush : _accentBrush;

        _face.IsSpeaking = state.IsSpeaking;

        SetChip(_chromeChip, "CHROME", state.ChromeLinked);
        SetChip(_gmailChip, "GMAIL", state.GmailLinked);

        _transcript.Update(state.Transcript);
        _activityLog.Update(state.ActivityHistory);
    }

    // .arc .chip b { color: var(--accent); font-weight: 600; } in the
    // mockup - only the "· LINKED" suffix is accent-colored/bolder, the
    // label itself stays text-lo. Uses the theme-tracking brush instances
    // directly so a gold/cyan toggle repaints these too.
    private void SetChip(TextBlock chip, string label, bool linked)
    {
        chip.Inlines ??= [];
        chip.Inlines.Clear();
        chip.Inlines.Add(new Run($"{label} ") { Foreground = _textLoBrush });
        chip.Inlines.Add(new Run(linked ? "· LINKED" : "· OFFLINE")
        {
            Foreground = linked ? _accentBrush : _textLoBrush,
            FontWeight = linked ? FontWeight.SemiBold : FontWeight.Normal,
        });
    }

    // Confirm popup (see ConfirmPopup) styled to match ARC's own warn/
    // pending visual language - the same _warnBrush/_warnBgBrush/
    // _warnBorderBrush trio already used for a pending transcript row's
    // "AWAITING GO-AHEAD" treatment, so a Gate 2
    // review reads as a continuation of that existing convention rather
    // than a new color vocabulary. Built fresh on each access (cheap - a
    // handful of field reads) but referencing the same *mutable* brush
    // instances ARC mutates on a gold/cyan toggle, so OverlayWindow's
    // ThemeChanged-triggered refresh isn't strictly required for ARC (it
    // only matters for AURA, whose palette swap allocates new brushes) -
    // still called for both, since re-deriving from the current field
    // values is simplest kept uniform across all 3 skins.
    public ConfirmPopupStyle PopupStyle => new(
        HeaderText: "CONFIRM ACTION",
        CredentialsHeaderText: "CONNECT GOOGLE",
        Backdrop: new SolidColorBrush(Color.FromArgb(200, GroundBrush.Color.R, GroundBrush.Color.G, GroundBrush.Color.B)),
        CardBackground: PanelBrush,
        CardBorder: _warnBorderBrush,
        CardCornerRadius: 16,
        PanelCornerRadius: 28,
        HeaderBrush: _warnBrush,
        BodyBrush: _textHiBrush,
        MutedBrush: _textLoBrush,
        ErrorBrush: _warnBrush,
        Font: new FontFamily("Consolas"),
        ConfirmBackground: _warnBgBrush,
        ConfirmForeground: _warnBrush,
        ConfirmBorder: _warnBorderBrush,
        CancelBackground: PanelBrush,
        CancelForeground: _textLoBrush,
        CancelBorder: _hairBrush,
        InputBackground: GroundBrush,
        InputBorder: _hairBrush,
        InputForeground: _textHiBrush,
        InputCaret: _accentBrush,
        BorderThickness: 1,
        ButtonCornerRadius: 4,
        PixelShadowColor: null);

    public void ToggleTheme() => ToggleThemeClicked();

    public bool IsAlternateTheme => !_goldTheme;

    private void ToggleThemeClicked()
    {
        _goldTheme = !_goldTheme;
        _face.Palette = _goldTheme ? ArcPalette.Gold : ArcPalette.Cyan;

        Color accent = _goldTheme ? GoldAccent : CyanAccent;
        Color warn = _goldTheme ? GoldWarn : CyanWarn;
        _accentBrush.Color = accent;
        _textHiBrush.Color = _goldTheme ? GoldTextHi : CyanTextHi;
        _textLoBrush.Color = _goldTheme ? GoldTextLo : CyanTextLo;
        _warnBrush.Color = warn;
        _warnBgBrush.Color = Color.FromArgb(20, warn.R, warn.G, warn.B);
        _warnBorderBrush.Color = Color.FromArgb(115, warn.R, warn.G, warn.B);
        _wordmarkGlow.Color = accent;
        _panelGlow.Color = accent;
        _faceGlow.Color = accent;
        _hairBrush.Color = _goldTheme ? GoldHair : CyanHair;
        _accentBgBrush.Color = Color.FromArgb(31, accent.R, accent.G, accent.B);
        _pillDotGlow.Color = accent;
        _transcript.SetThumbColor(Color.FromArgb(204, accent.R, accent.G, accent.B));
        _activityLog.SetThumbColor(Color.FromArgb(204, accent.R, accent.G, accent.B));
        _actions?.ThemeChanged();
    }

    // Swaps between the full panel and the slim resting pill - see the
    // pill's own construction-site comment. Preserves whatever the
    // transcript/activity-log toggle was last set to (untouched here), so
    // re-expanding returns to maximize mode if that's where it was left.
    // Public (not just the button's own click handler) so OverlayWindow can
    // carry this state across a skin cycle - see its own CycleSkin comment.
    public void SetCollapsed(bool collapsed) => PanelCollapseHelper.SetCollapsed(collapsed, _body, _pill, SetMaximized);

    // Widens both this panel's own root and (via OverlayWindow's own
    // IsMaximized polling) the window together, in lockstep - explicit
    // assignment, not SizeToContent.Width, which previously desynced the
    // window's outer bounds from what actually rendered when content width
    // changed at runtime (see AvaloniaOverlayWindow's own constructor
    // comment on Width). Public for the same cross-skin-cycle reason as
    // SetCollapsed above.
    public void SetMaximized(bool maximized)
    {
        _transcript.IsVisible = maximized;
        _activityLog.IsVisible = maximized;
        _activityLabel.IsVisible = maximized;
        _conversationLabel.IsVisible = maximized;
        _typeRow.IsVisible = maximized;
        // The wrapping frame Border doesn't auto-collapse just because its
        // (now zero-height) child does - left always-visible, it would
        // still paint its own empty bordered box while minimized.
        _activityLogBox.IsVisible = maximized;
        _transcriptBox.IsVisible = maximized;
        _root.Width = maximized ? OverlayLayout.MaximizedPanelWidth : OverlayLayout.PanelWidth;
    }

    // _pill.IsVisible is already the single source of truth for this -
    // no separate bool to fall out of sync with it.
    public bool IsCollapsed => _pill.IsVisible;

    public Control Pill => _pill;

    public Control PillDragHandle => _pillDragHandle;

    public bool IsMaximized => _transcript.IsVisible;

    public void AttachActions(OverlaySkinActions actions) => _actions = actions;

    public void Activate() => _face.Activate();

    public void Deactivate() => _face.Deactivate();

    // ARC's log line from the source mockup: a thin left border (accent for
    // Nova, hairline for the user) with a small-caps tag above the text -
    // see .arc .line / .arc .line .tag in the mockup. An instance method
    // (not static) so rows built under one theme keep tracking it if the
    // user switches gold/cyan later - they reference the same mutable
    // brush instances as everything else in this skin.
    private Control BuildTranscriptRow(TranscriptEntry entry)
    {
        // .arc .line.pending in the mockup: warn-colored border/tag instead
        // of the usual hairline/accent, plus a " · AWAITING GO-AHEAD" tag
        // suffix - _warnBrush already tracks the gold/cyan theme toggle.
        IBrush tagBrush = entry.IsPending ? _warnBrush : entry.IsUser ? _textLoBrush : _accentBrush;
        IBrush borderBrush = entry.IsPending ? _warnBrush : entry.IsUser ? _textLoBrush : _accentBrush;

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = (entry.IsUser ? "YOU" : "NOVA") + (entry.IsPending ? " · AWAITING GO-AHEAD" : ""),
            Foreground = tagBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        });
        content.Children.Add(new TextBlock
        {
            Text = entry.Text,
            Foreground = _textHiBrush,
            FontSize = 18,
            TextWrapping = TextWrapping.Wrap,
        });

        return new Border
        {
            Background = entry.IsPending ? new SolidColorBrush(Color.FromArgb(20, _warnBrush.Color.R, _warnBrush.Color.G, _warnBrush.Color.B)) : null,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(8, 3, 0, 3),
            Margin = new Thickness(0, 0, 0, 6),
            Child = content,
        };
    }

    // Same thin-left-border shape as BuildTranscriptRow above, but a single
    // line per entry (no You/Nova tag - there's only ever one "speaker",
    // the tool loop itself) with an inline dots run for the in-progress
    // entry, matching the mockup's ARC activity-feed treatment.
    private (Control Row, TextBlock? Dots) BuildActivityRow(ActivityEntry entry)
    {
        IBrush borderBrush = entry.InProgress ? _accentBrush : _hairBrush;
        IBrush textBrush = entry.InProgress ? _textHiBrush : _textLoBrush;

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = entry.Text,
            Foreground = textBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        TextBlock? dots = null;
        if (entry.InProgress)
        {
            dots = new TextBlock { Foreground = textBrush, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
            content.Children.Add(dots);
        }

        var row = new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(8, 2, 0, 2),
            Margin = new Thickness(0, 0, 0, 4),
            Child = content,
        };
        return (row, dots);
    }

    private Button MakeChromeButton(string glyph)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 22,
            Height = 22,
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(0),
            Background = PanelBrush,
            Foreground = _accentBrush,
            BorderBrush = _textLoBrush,
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        FlatButtonStyle.Apply(button, cornerRadius: 11);
        return button;
    }

    // The narrow panel can't fit a long activity string ("searching memory
    // for \"...\"") on one line - cap it rather than let it wrap/overflow.
    // The state label now shrinks to fit via a Viewbox (see the
    // constructor) instead of relying on truncation for normal-length
    // activity text - this cutoff only exists as a floor so a truly
    // pathological string doesn't shrink to an illegible sliver.
}
