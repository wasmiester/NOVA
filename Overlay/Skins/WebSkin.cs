using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Nova;

// WEB chrome: vivid saturated-blue panel, thick black pixel-style borders,
// the smiley + equalizer ring + Zzz layered in a Grid. No second color
// theme in the source design - ToggleTheme is a documented no-op here.
internal sealed class WebSkin : IOverlaySkin
{
    private readonly Border _root;
    private readonly WebFace _face;
    private readonly WebEqualizerRing _ring;
    private readonly WebZzz _zzz;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _stateLabel;
    private readonly TextBlock _subLabel;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _logHead;
    private readonly TextBlock _conversationHead;
    private readonly Border _activityLogBox;
    private readonly Border _transcriptBox;
    private readonly Grid _typeRow;
    private readonly TextBlock _chromeChip;
    private readonly TextBlock _gmailChip;
    private readonly Button _modeButton;
    private readonly Button _sleepButton;
    private readonly TranscriptPanel _transcript;
    private readonly ActivityLogPanel _activityLog;
    private readonly Grid _layout;
    private readonly Button _pill;
    private readonly Border _pillDot;
    private readonly TextBlock _pillText;
    private OverlaySkinActions? _actions;
    private ActivationMode _mode = ActivationMode.Prompted;
    private bool _engaged = true;

    // Exact hex values from the source mockup (--panel, --panel-2, --edge-lo,
    // --text-hi, --text-lo, --amber, --red) - not approximated.
    private static readonly SolidColorBrush PanelBrush = new(Color.FromRgb(0x2f, 0x6f, 0xff));
    private static readonly SolidColorBrush Panel2Brush = new(Color.FromRgb(0x1a, 0x3f, 0xc4));
    private static readonly SolidColorBrush EdgeBrush = new(Color.FromRgb(0x05, 0x0a, 0x14));
    private static readonly SolidColorBrush EdgeHiBrush = new(Color.FromRgb(0xba, 0xf0, 0xff));
    private static readonly SolidColorBrush TextBrush = new(Color.FromRgb(0xf4, 0xf9, 0xff));
    private static readonly SolidColorBrush TextLoBrush = new(Color.FromRgb(0x9c, 0xc0, 0xec));
    private static readonly SolidColorBrush AmberBrush = new(Color.FromRgb(0xff, 0xcb, 0x47));
    private static readonly Color GreenColor = Color.FromRgb(0x29, 0xff, 0x8f);
    private static readonly Color AsleepDotColor = Color.FromRgb(0x6b, 0x72, 0x80);

    // font-family: "Courier New", ui-monospace, "Cascadia Mono", monospace
    // in the mockup - Courier New leads, not Consolas.
    private static readonly FontFamily WebFont = new("Courier New, Consolas");

    public Control Root => _root;

    public WebSkin()
    {
        _face = new WebFace();
        _ring = new WebEqualizerRing();
        _zzz = new WebZzz { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(60, -70, 0, 0) };

        // filter: drop-shadow(0 0 18px rgba(0,229,255,.3)) on .web-face in
        // the mockup - the avatar's own cyan ambient glow.
        var faceGrid = new Grid
        {
            Effect = new DropShadowEffect { Color = Color.FromRgb(0x00, 0xe5, 0xff), BlurRadius = 16, OffsetX = 0, OffsetY = 0, Opacity = 0.3 },
        };
        faceGrid.Children.Add(_ring);
        faceGrid.Children.Add(_face);
        faceGrid.Children.Add(_zzz);

        // Reverted back to a plain circle - the mockup's flat-square +
        // offset-shadow pixel dot read as broken/wrong next to the
        // wordmark in practice, even though it matched the CSS literally.
        _statusDot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(GreenColor), Margin = new Thickness(6, 0, 0, 0) };
        // .web .logo-badge: font-weight 800 (ExtraBold, not Black/900),
        // font-size 42.5px in the mockup's 2x space -> 21px.
        var wordmarkRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        wordmarkRow.Children.Add(new TextBlock { Text = "NOVA", Foreground = TextBrush, FontFamily = WebFont, FontWeight = FontWeight.ExtraBold, FontSize = 21 });
        wordmarkRow.Children.Add(_statusDot);

        _sleepButton = MakePixelButton("Z");
        _sleepButton.Click += (_, _) => _actions?.SetEngaged(!_engaged);
        // Widths/heights match the source design mockup's own .web-log/
        // .web-transcript exactly - see OverlayLayout.MaximizedPanelWidth's
        // own doc comment for the full ratio/gap reasoning.
        _transcript = new TranscriptPanel(BuildTranscriptRow, width: OverlayLayout.ConversationColumnWidth, thumbColor: Color.FromArgb(230, 0xff, 0xcb, 0x47), height: OverlayLayout.TranscriptHeight);
        _activityLog = new ActivityLogPanel(BuildActivityRow, width: OverlayLayout.ActivityColumnWidth, thumbColor: Color.FromArgb(230, 0xff, 0xcb, 0x47), height: OverlayLayout.ActivityLogHeight);
        // .web .log-head "ACTIVITY LOG" in the mockup, above the transcript -
        // only actually meaningful (and only shown) while the transcript
        // itself is expanded, same as the mockup's log only existing at all
        // in the maximized view.
        _logHead = new TextBlock { Text = "ACTIVITY LOG", Foreground = TextLoBrush, FontFamily = WebFont, FontWeight = FontWeight.Bold, FontSize = 13, Margin = new Thickness(0, 12, 0, 4), IsVisible = false };
        _conversationHead = new TextBlock { Text = "CONVERSATION", Foreground = TextLoBrush, FontFamily = WebFont, FontWeight = FontWeight.Bold, FontSize = 13, Margin = new Thickness(0, 12, 0, 4), IsVisible = false };
        var maximizeButton = MakePixelButton("☰");
        maximizeButton.Click += (_, _) => SetMaximized(!_transcript.IsVisible);
        var cycleButton = MakePixelButton("⟳");
        cycleButton.Click += (_, _) => _actions?.CycleSkin();
        var closeButton = MakePixelButton("X");
        closeButton.Background = AmberBrush;
        closeButton.Foreground = new SolidColorBrush(Color.FromRgb(12, 34, 102));
        // A single-line "minimize" glyph, distinct from the pill's own
        // down-chevron - sits right next to maximize since collapse/expand
        // is the same axis of control (how much of the panel shows), just
        // one more step past minimized.
        var collapseButton = MakePixelButton("─");
        collapseButton.Click += (_, _) => SetCollapsed(true);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal };
        buttonRow.Children.Add(_sleepButton);
        buttonRow.Children.Add(maximizeButton);
        buttonRow.Children.Add(collapseButton);
        buttonRow.Children.Add(cycleButton);
        buttonRow.Children.Add(closeButton);
        closeButton.Click += (_, _) => _actions?.Close();

        var header = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(buttonRow, Dock.Right);
        header.Children.Add(wordmarkRow);
        header.Children.Add(buttonRow);

        // .web .drag-bar in the mockup: background: var(--panel-2);
        // border-bottom: 4px solid var(--edge-lo); box-shadow: inset 0 -3px
        // 0 var(--edge-hi) - a distinct brighter chrome bar with a hard dark
        // border and a thin bright highlight line just above it. Those raw
        // 3px/4px values are in the mockup's 2x-scaled coordinate space -
        // halved here too.
        // HorizontalAlignment.Stretch set explicitly on all three - Border/
        // Rectangle don't stretch to fill a parent StackPanel's cross-axis
        // width by default in Avalonia the way they did in WPF, so without
        // this the drag-bar's background and both hi/lo stripe lines only
        // spanned their own narrow content width instead of the full panel.
        var headerBar = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        headerBar.Children.Add(new Border { Background = Panel2Brush, Padding = new Thickness(8), Child = header, HorizontalAlignment = HorizontalAlignment.Stretch });
        headerBar.Children.Add(new Rectangle { Height = 1.5, Fill = EdgeHiBrush, HorizontalAlignment = HorizontalAlignment.Stretch });
        headerBar.Children.Add(new Rectangle { Height = 2, Fill = EdgeBrush, HorizontalAlignment = HorizontalAlignment.Stretch });

        // A long activity string ("SEARCHING MEMORY FOR \"...\"") used to
        // just get hard-truncated at a fixed character count - lost
        // information rather than showing it. A Viewbox (DownOnly so short
        // text like LISTENING never gets scaled *up*) shrinks the whole
        // line to fit the panel's width instead, so it stays fully legible
        // just smaller - TruncateActivity below still exists as a safety
        // net so a pathological wall of text doesn't shrink to an
        // illegible sliver, just raised well past the old 36-char cutoff.
        _stateLabel = new TextBlock { Text = "LISTENING", Foreground = TextBrush, FontFamily = WebFont, FontWeight = FontWeight.Bold, FontSize = 16 };
        var stateLabelBox = new Viewbox
        {
            Child = _stateLabel,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 2),
        };

        // The mockup's blinking cursor (.web .cursor) next to the sub-label
        // was dropped per direct request - read as visual noise in practice.
        _subLabel = new TextBlock { Text = "JUST START TALKING", Foreground = TextLoBrush, FontFamily = WebFont, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
        // Indeterminate - there's no real "% complete" for an agentic task
        // with an unknown number of tool-call rounds ahead of it, so this
        // is a sliding/pulsing "actively working" signal, not a progress
        // claim. Swaps in for _subLabel's idle hint while a task is
        // running (see ApplyState) rather than sitting alongside it. Square
        // corners and the EdgeHi/Edge brush pair, matching this skin's
        // blocky pixel-art chrome rather than a soft rounded bar.
        _progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            IsVisible = false,
            Height = 4,
            Width = 140,
            CornerRadius = new CornerRadius(0),
            Background = EdgeBrush,
            Foreground = EdgeHiBrush,
        };

        _modeButton = new Button
        {
            Content = "PROMPTED",
            FontFamily = WebFont,
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Foreground = TextBrush,
            Background = PanelBrush,
            BorderBrush = EdgeBrush,
            BorderThickness = new Thickness(2),
            Padding = new Thickness(7.2, 4.8, 7.2, 4.8),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        FlatButtonStyle.Apply(_modeButton, cornerRadius: 0, pixelShadowColor: EdgeBrush.Color);
        _modeButton.Click += (_, _) => _actions?.SwitchActivationMode(_mode == ActivationMode.Prompted ? ActivationMode.KeyBind : ActivationMode.Prompted);

        // .web .status-bar in the mockup: border-top: 4px solid var(--edge-lo);
        // background: var(--panel-2); box-shadow: inset 0 3px 0 var(--edge-hi) -
        // the drag-bar's chrome treatment mirrored at the bottom, holding two
        // integration-status chips. Backed by real state (BrowserController's
        // live connection, whether a Google account was configured at
        // startup), not just static mockup copy.
        _chromeChip = new TextBlock { FontFamily = WebFont, FontWeight = FontWeight.Bold, FontSize = 10, Foreground = TextBrush, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _gmailChip = new TextBlock { FontFamily = WebFont, FontWeight = FontWeight.Bold, FontSize = 10, Foreground = TextBrush, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var chipsRow = new Grid();
        chipsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        chipsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var chromeChipBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x20, 0x2e)), Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 4, 0), Child = _chromeChip };
        var gmailChipBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x20, 0x2e)), Padding = new Thickness(6, 3, 6, 3), Child = _gmailChip };
        Grid.SetColumn(chromeChipBorder, 0);
        Grid.SetColumn(gmailChipBorder, 1);
        chipsRow.Children.Add(chromeChipBorder);
        chipsRow.Children.Add(gmailChipBorder);

        var statusBar = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        statusBar.Children.Add(new Rectangle { Height = 1.5, Fill = EdgeHiBrush, HorizontalAlignment = HorizontalAlignment.Stretch });
        statusBar.Children.Add(new Rectangle { Height = 2, Fill = EdgeBrush, HorizontalAlignment = HorizontalAlignment.Stretch });
        statusBar.Children.Add(new Border { Background = Panel2Brush, Padding = new Thickness(8), Child = chipsRow, HorizontalAlignment = HorizontalAlignment.Stretch });

        // .web .drag-bar/.status-bar sit flush against panel-inner's own
        // top/bottom edges in the mockup, with only the *content between*
        // them padded - wrapping everything (headerBar/statusBar included)
        // in one Margin(16) StackPanel, like the very first pass at this
        // layout did, insets the header/status-bar backgrounds and stripe
        // lines from the panel's true edges even though they stretch
        // correctly within that shrunken container. A 3-row Grid instead:
        // header and status-bar go directly in the Auto rows (no side
        // margin, so they reach the real edges), only the middle content
        // gets padding, and the Star row is what actually absorbs any
        // extra height from OverlayLayout.PanelMinHeight - keeping the
        // status bar pinned to the true bottom edge regardless of which
        // skin's natural content is shorter than ARC's.
        var typeInput = new TextBox
        {
            PlaceholderText = "TYPE INSTEAD OF SPEAK…",
            FontFamily = WebFont,
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x20, 0x2e)),
            Foreground = TextBrush,
            BorderBrush = EdgeBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(8, 6, 8, 6),
        };
        var sendButton = MakePixelButton("SEND");
        sendButton.Width = double.NaN;
        sendButton.Padding = new Thickness(10, 0, 10, 0);
        sendButton.Background = AmberBrush;
        sendButton.Foreground = new SolidColorBrush(Color.FromRgb(12, 34, 102));
        // Confirmed live: at the narrower ~220px column width the
        // side-by-side maximize layout gives it, the Grid's arrange pass
        // was shrinking this Auto column below its own natural size
        // instead of letting the row simply run wider than the column - a
        // hard floor is the reliable fix regardless of the exact
        // arrange-time math.
        sendButton.MinWidth = 60;
        TypeToTalkInput.WireUp(typeInput, sendButton, text => _actions?.SendTypedText(text));
        _typeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        Grid.SetColumn(typeInput, 0);
        Grid.SetColumn(sendButton, 1);
        typeInput.Margin = new Thickness(0, 0, 4, 0);
        _typeRow.Children.Add(typeInput);
        _typeRow.Children.Add(sendButton);

        var innerContent = new StackPanel { Margin = new Thickness(16, 0, 16, 0) };
        innerContent.Children.Add(faceGrid);
        innerContent.Children.Add(stateLabelBox);
        innerContent.Children.Add(_subLabel);
        innerContent.Children.Add(_progressBar);
        innerContent.Children.Add(_modeButton);
        // Side by side, not stacked - see OverlayLayout.MaximizedPanelWidth's
        // own doc comment for the exact width math this assumes. Each panel
        // gets the mockup's own solid pixel-console box (.web-log/
        // .web-transcript: background AND border both EdgeBrush) -
        // confirmed live as a real gap: this was never actually carried
        // over into the real implementation, so both areas rendered as
        // bare content floating with no box around them at all, unlike the
        // type-to-talk input right below (which already had its own
        // recessed-field background).
        _activityLogBox = new Border { Background = EdgeBrush, BorderBrush = EdgeBrush, BorderThickness = new Thickness(2), Padding = new Thickness(0, 6, 0, 6), IsVisible = false, Child = _activityLog.Root };
        _transcriptBox = new Border { Background = EdgeBrush, BorderBrush = EdgeBrush, BorderThickness = new Thickness(2), Padding = new Thickness(6), IsVisible = false, Child = _transcript.Root };
        var activityColumn = new StackPanel();
        activityColumn.Children.Add(_logHead);
        activityColumn.Children.Add(_activityLogBox);
        var conversationColumn = new StackPanel();
        conversationColumn.Children.Add(_conversationHead);
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
        innerContent.Children.Add(splitRow);

        _layout = new Grid();
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(headerBar, 0);
        Grid.SetRow(innerContent, 1);
        Grid.SetRow(statusBar, 2);
        _layout.Children.Add(headerBar);
        _layout.Children.Add(innerContent);
        _layout.Children.Add(statusBar);

        // .web .scanrows in the mockup: a full-panel repeating horizontal
        // stripe texture, non-interactive, layered on top of everything.
        var scanlines = new Rectangle
        {
            IsHitTestVisible = false,
            Opacity = 0.5,
            Fill = new DrawingBrush
            {
                TileMode = TileMode.Tile,
                DestinationRect = new RelativeRect(new Rect(0, 0, 4, 4), RelativeUnit.Absolute),
                Drawing = new GeometryDrawing
                {
                    Brush = new ImmutableSolidColorBrush(Color.FromArgb(46, 0, 0, 0)),
                    Geometry = new RectangleGeometry(new Rect(0, 0, 4, 1)),
                },
            },
        };

        // The collapsed resting state - see ArcSkin's own pill for the full
        // reasoning (a plain dot instead of a live WebFace instance, same
        // cost tradeoff). Pixel-bordered circle instead of a glow, matching
        // WEB's own blocky chrome rather than ARC's soft ambient light.
        _pillDot = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = AmberBrush,
            BorderBrush = EdgeBrush,
            BorderThickness = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pillText = new TextBlock
        {
            Text = "LISTENING",
            Foreground = TextBrush,
            FontFamily = WebFont,
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var pillChevron = new TextBlock { Text = "▾", Foreground = TextBrush, FontFamily = WebFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var pillInner = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(_pillDot, 0);
        Grid.SetColumn(_pillText, 1);
        Grid.SetColumn(pillChevron, 2);
        pillInner.Children.Add(_pillDot);
        pillInner.Children.Add(_pillText);
        pillInner.Children.Add(pillChevron);
        _pill = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 0, 16, 0),
            Height = 56,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = pillInner,
            IsVisible = false,
        };
        FlatButtonStyle.Apply(_pill, cornerRadius: 0, stretchContent: true);
        _pill.Click += (_, _) => SetCollapsed(false);

        var bodyOrPill = new Grid();
        bodyOrPill.Children.Add(_layout);
        bodyOrPill.Children.Add(_pill);

        var contentGrid = new Grid();
        contentGrid.Children.Add(bodyOrPill);
        contentGrid.Children.Add(scanlines);

        // box-shadow: 0 0 0 3px var(--edge-hi) on the outer window, plus a
        // separate dark 4px border on .panel-inner - the classic two-tone
        // pixel bevel.
        var innerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(12, 34, 102)),
            BorderBrush = EdgeBrush,
            // .panel-inner { border: 4px solid var(--edge-lo); } in the
            // mockup's 2x-scaled coordinate space -> 2px at this scale.
            BorderThickness = new Thickness(2),
            Child = contentGrid,
        };

        _root = new Border
        {
            // Same fixed width as ARC/AURA - all 3 skins render at the same
            // size so cycling between them doesn't resize the window
            // sideways. See OverlayLayout.
            Width = OverlayLayout.PanelWidth,
            BorderBrush = EdgeHiBrush,
            BorderThickness = new Thickness(3),
            Child = innerBorder,
        };
    }

    public void ApplyState(OverlayState state)
    {
        _engaged = !state.Asleep;
        _mode = state.Mode;
        _modeButton.Content = state.Mode == ActivationMode.Prompted ? "PROMPTED" : "KEY BIND";

        _ring.IsSpeaking = state.IsSpeaking;
        _ring.IsAsleep = state.Asleep;
        _face.ApplyState(state.IsSpeaking, state.Asleep, _ring.AverageHeight);
        _zzz.IsAsleep = state.Asleep;

        _statusDot.Fill = new SolidColorBrush(state.Asleep ? AsleepDotColor : GreenColor);
        // While she's silently working (reading a page, filling a field,
        // searching memory), the state label shows *that* instead of the
        // generic LISTENING - the same text already printed to the
        // console, so the overlay stops looking idle during a long task.
        bool working = !state.Asleep && !state.IsSpeaking && state.CurrentActivity is not null;
        string stateText = state.Asleep
            ? "SLEEPING"
            : state.IsSpeaking
                ? "SPEAKING"
                : working
                    ? TruncateActivity(state.CurrentActivity!).ToUpperInvariant()
                    : "LISTENING";
        _stateLabel.Text = stateText;
        _subLabel.Text = state.Asleep ? "PRESS CTRL+ALT+SPACE TO WAKE" : "JUST START TALKING";

        // The collapsed pill mirrors the same text/state live - see
        // ArcSkin's own pill for the full reasoning.
        _pillText.Text = stateText;
        _pillDot.Opacity = state.Asleep ? 0.5 : state.IsSpeaking ? 1.0 : working ? 0.85 : 0.65;
        // A task in progress swaps the idle hint for the indeterminate bar
        // instead of showing both - "JUST START TALKING" doesn't apply
        // once she's already mid-task, and showing an inert hint next to
        // an active progress bar would read as contradictory.
        bool showProgress = state.IsBusy && !state.Asleep;
        _subLabel.IsVisible = !showProgress;
        _progressBar.IsVisible = showProgress;

        // .pixel-btn[aria-pressed="true"] in the mockup: cyan fill + dark
        // text while asleep, instead of only the face communicating it.
        _sleepButton.Background = state.Asleep ? new SolidColorBrush(Color.FromRgb(0x00, 0xe5, 0xff)) : PanelBrush;
        _sleepButton.Foreground = state.Asleep ? new SolidColorBrush(Color.FromRgb(12, 34, 102)) : TextBrush;

        SetChip(_chromeChip, "CHROME", state.ChromeLinked);
        SetChip(_gmailChip, "GMAIL", state.GmailLinked);

        _transcript.Update(state.Transcript);
        _activityLog.Update(state.ActivityHistory);
    }

    // .web .status-chip b { color: var(--green); font-weight: 800; } in the
    // mockup - only the "LINKED"/status word is green/bolder, not the whole
    // chip.
    private static void SetChip(TextBlock chip, string label, bool linked)
    {
        chip.Inlines ??= [];
        chip.Inlines.Clear();
        chip.Inlines.Add(new Run($"{label} "));
        chip.Inlines.Add(new Run(linked ? "LINKED" : "OFFLINE")
        {
            Foreground = linked ? new SolidColorBrush(GreenColor) : TextLoBrush,
            FontWeight = FontWeight.ExtraBold,
        });
    }

    // Confirm popup (see ConfirmPopup) styled to match WEB's own pixel
    // chrome: hard 2px dark borders, the amber "X" close-button color for
    // the primary Confirm action (WEB's existing accent-for-a-notable-
    // action color, already used on closeButton), flat panel-2 for Cancel.
    public ConfirmPopupStyle PopupStyle => new(
        HeaderText: "CONFIRM ACTION",
        CredentialsHeaderText: "CONNECT GOOGLE",
        Backdrop: new SolidColorBrush(Color.FromArgb(210, EdgeBrush.Color.R, EdgeBrush.Color.G, EdgeBrush.Color.B)),
        CardBackground: Panel2Brush,
        CardBorder: EdgeBrush,
        CardCornerRadius: 0,
        PanelCornerRadius: 0,
        HeaderBrush: AmberBrush,
        BodyBrush: TextBrush,
        MutedBrush: TextLoBrush,
        ErrorBrush: AmberBrush,
        Font: WebFont,
        ConfirmBackground: AmberBrush,
        ConfirmForeground: new SolidColorBrush(Color.FromRgb(12, 34, 102)),
        ConfirmBorder: EdgeBrush,
        CancelBackground: PanelBrush,
        CancelForeground: TextBrush,
        CancelBorder: EdgeBrush,
        // Same dark plate already used behind the CHROME/GMAIL status
        // chips (see chromeChipBorder/gmailChipBorder above) - one
        // consistent "recessed field" color across the skin.
        InputBackground: new SolidColorBrush(Color.FromRgb(0x12, 0x20, 0x2e)),
        InputBorder: EdgeBrush,
        InputForeground: TextBrush,
        InputCaret: AmberBrush,
        BorderThickness: 2,
        ButtonCornerRadius: 0,
        PixelShadowColor: EdgeBrush.Color);

    public void ToggleTheme()
    {
        // No second theme in the source design - intentional no-op.
    }

    public bool IsAlternateTheme => false;

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
        _logHead.IsVisible = maximized;
        _conversationHead.IsVisible = maximized;
        _typeRow.IsVisible = maximized;
        // The wrapping frame Border doesn't auto-collapse just because its
        // (now zero-height) child does - left always-visible, it would
        // still paint its own empty bordered box while minimized.
        _activityLogBox.IsVisible = maximized;
        _transcriptBox.IsVisible = maximized;
        _root.Width = maximized ? OverlayLayout.MaximizedPanelWidth : OverlayLayout.PanelWidth;
    }

    public bool IsCollapsed => _pill.IsVisible;

    public bool IsMaximized => _transcript.IsVisible;

    public void AttachActions(OverlaySkinActions actions) => _actions = actions;

    public void Activate()
    {
        _face.Activate();
        _ring.Activate();
        _zzz.Activate();
    }

    public void Deactivate()
    {
        _face.Deactivate();
        _ring.Deactivate();
        _zzz.Deactivate();
    }

    // WEB's speech bubble from the source mockup: You on the right in pale
    // cyan, Nova on the left in green, both with a thick dark pixel-style
    // border and a small triangular tail - see .web .bubble in the mockup.
    private static Control BuildTranscriptRow(TranscriptEntry entry)
    {
        // .web .bubble.nova.pending in the mockup: amber instead of green,
        // tail included, for a Gate 1/Gate 2 "should I go ahead?" ask.
        Color fill = entry.IsUser ? Color.FromRgb(0xba, 0xf0, 0xff)
            : entry.IsPending ? Color.FromRgb(0xff, 0xcb, 0x47)
            : Color.FromRgb(0x29, 0xff, 0x8f);
        var ink = new SolidColorBrush(Color.FromRgb(12, 34, 102));

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = entry.IsUser ? "YOU" : "NOVA",
            Foreground = ink,
            Opacity = 0.7,
            FontFamily = WebFont,
            FontWeight = FontWeight.Black,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 2),
        });
        content.Children.Add(new TextBlock
        {
            Text = entry.Text,
            Foreground = ink,
            FontFamily = WebFont,
            FontWeight = FontWeight.Bold,
            FontSize = 19.5,
            TextWrapping = TextWrapping.Wrap,
        });

        // .web .bubble-note in the mockup: a separate block-level line
        // below the message body, not merged into the tag line.
        if (entry.IsPending)
        {
            content.Children.Add(new TextBlock
            {
                Text = "· AWAITING GO-AHEAD",
                Foreground = ink,
                Opacity = 0.7,
                FontFamily = WebFont,
                FontWeight = FontWeight.Bold,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        var bubble = new Border
        {
            Background = new SolidColorBrush(fill),
            BorderBrush = EdgeBrush,
            BorderThickness = new Thickness(2),
            Padding = new Thickness(8, 6, 8, 6),
            MaxWidth = 190,
            HorizontalAlignment = entry.IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = content,
        };

        var tail = new Polygon
        {
            Points = entry.IsUser
                ? [new Point(0, 0), new Point(10, 0), new Point(10, 10)]
                : [new Point(0, 0), new Point(10, 0), new Point(0, 10)],
            Fill = new SolidColorBrush(fill),
            Stroke = EdgeBrush,
            StrokeThickness = 2,
            HorizontalAlignment = entry.IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = entry.IsUser ? new Thickness(0, -2, 10, 0) : new Thickness(10, -2, 0, 0),
        };

        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(bubble);
        row.Children.Add(tail);
        return row;
    }

    // WEB's activity feed: a terminal ">" prompt per line instead of a
    // speech bubble - matches the pixel-terminal treatment from the source
    // mockup rather than reusing the bubble look, which only makes sense
    // for an actual back-and-forth conversation.
    private static (Control Row, TextBlock? Dots) BuildActivityRow(ActivityEntry entry)
    {
        var promptBrush = new SolidColorBrush(entry.InProgress ? GreenColor : TextLoBrush.Color);
        IBrush textBrush = entry.InProgress ? TextBrush : TextLoBrush;

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = "> ", Foreground = promptBrush, FontFamily = WebFont, FontWeight = FontWeight.Bold, FontSize = 12 });
        line.Children.Add(new TextBlock { Text = entry.Text.ToUpperInvariant(), Foreground = textBrush, FontFamily = WebFont, FontWeight = FontWeight.Bold, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        TextBlock? dots = null;
        if (entry.InProgress)
        {
            dots = new TextBlock { Foreground = promptBrush, FontFamily = WebFont, FontWeight = FontWeight.Bold, FontSize = 12 };
            line.Children.Add(dots);
        }

        return (new Border { Padding = new Thickness(0, 2, 0, 2), Child = line }, dots);
    }

    // .web .pixel-btn: font-size 30px, border 3px in the mockup's 2x space
    // -> 15px/1.5px here.
    private static Button MakePixelButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Width = 26,
            Height = 24,
            FontSize = 14,
            Margin = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(0),
            FontFamily = WebFont,
            FontWeight = FontWeight.Bold,
            Background = PanelBrush,
            Foreground = TextBrush,
            BorderBrush = EdgeBrush,
            BorderThickness = new Thickness(1.5),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        FlatButtonStyle.Apply(button, cornerRadius: 0, pixelShadowColor: EdgeBrush.Color);
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
