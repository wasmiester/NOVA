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
    private readonly Button _sleepButton;
    private readonly Button _themeButton;
    private readonly Button _maximizeButton;
    private readonly TranscriptPanel _transcript;
    private readonly TextBlock _chromeChip;
    private readonly TextBlock _gmailChip;
    private OverlaySkinActions? _actions;
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

        _transcript = new TranscriptPanel(BuildTranscriptRow, width: 220, thumbColor: Color.FromArgb(204, GoldAccent.R, GoldAccent.G, GoldAccent.B));
        _maximizeButton = MakeChromeButton("☰");
        _maximizeButton.Click += (_, _) => _transcript.IsVisible = !_transcript.IsVisible;

        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal };
        headerButtons.Children.Add(_themeButton);
        headerButtons.Children.Add(_sleepButton);
        headerButtons.Children.Add(_maximizeButton);
        headerButtons.Children.Add(cycleButton);
        headerButtons.Children.Add(closeButton);

        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(headerButtons, Dock.Right);
        header.Children.Add(_wordmark);
        header.Children.Add(headerButtons);

        _stateLabel = new TextBlock
        {
            Text = "LISTENING",
            Foreground = _accentBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
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

        // .arc .status-bar { border-top: 1px solid var(--hair); ... } .arc
        // .chip { border: 1px solid var(--hair); color: var(--text-lo); }
        // .arc .chip b { color: var(--accent); } - bordered (not filled)
        // chips with the theme-tracking hair/text-lo/accent brushes, unlike
        // WEB's filled dark chips. Backed by real Chrome/Gmail connection
        // state, same as WEB's.
        _chromeChip = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        _gmailChip = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        var chromeChipBorder = new Border { BorderBrush = _hairBrush, BorderThickness = new Thickness(1), Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 0, 6, 0), Child = _chromeChip };
        var gmailChipBorder = new Border { BorderBrush = _hairBrush, BorderThickness = new Thickness(1), Padding = new Thickness(5, 3, 5, 3), Child = _gmailChip };
        var chipsRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        chipsRow.Children.Add(chromeChipBorder);
        chipsRow.Children.Add(gmailChipBorder);
        var statusBar = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        statusBar.Children.Add(new Border { Height = 1, Background = _hairBrush, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Stretch });
        statusBar.Children.Add(chipsRow);

        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(header);
        stack.Children.Add(_face);
        stack.Children.Add(_stateLabel);
        stack.Children.Add(_subLabel);
        stack.Children.Add(_transcript.Root);
        stack.Children.Add(statusBar);

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
            Child = stack,
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
        _face.IsAsleep = state.Asleep;

        // While she's silently working (reading a page, filling a field,
        // searching memory), the state label shows *that* instead of the
        // generic LISTENING - the same text already printed to the
        // console, so the overlay stops looking idle during a long task.
        _stateLabel.Text = state.Asleep
            ? "STANDBY"
            : state.IsSpeaking
                ? "SPEAKING"
                : state.CurrentActivity is { } activity
                    ? TruncateActivity(activity).ToUpperInvariant()
                    : "LISTENING";
        _subLabel.Text = state.Asleep ? "CTRL+ALT+SPACE TO WAKE" : "JUST START TALKING";
        _stateLabel.Opacity = state.Asleep ? 0.55 : 1.0;

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
        _transcript.SetThumbColor(Color.FromArgb(204, accent.R, accent.G, accent.B));
        _actions?.ThemeChanged();
    }

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
    private static string TruncateActivity(string text) =>
        text.Length > 36 ? string.Concat(text.AsSpan(0, 36), "…") : text;
}
