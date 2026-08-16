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
    private readonly Button _sleepButton;
    private readonly Button _themeButton;
    private readonly Button[] _chromeButtons;
    private readonly TranscriptPanel _transcript;
    private readonly Border _chromeChipBorder;
    private readonly Border _gmailChipBorder;
    private readonly Ellipse _chromeDot;
    private readonly Ellipse _gmailDot;
    private readonly TextBlock _chromeChipText;
    private readonly TextBlock _gmailChipText;
    private OverlaySkinActions? _actions;
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

        _transcript = new TranscriptPanel(BuildTranscriptRow, width: 220, thumbColor: Color.FromArgb(179, 122, 108, 190));
        var maximizeButton = MakeChromeButton("☰");
        maximizeButton.Click += (_, _) => _transcript.IsVisible = !_transcript.IsVisible;
        _chromeButtons = [_themeButton, _sleepButton, maximizeButton, cycleButton, closeButton];

        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal };
        headerButtons.Children.Add(_themeButton);
        headerButtons.Children.Add(_sleepButton);
        headerButtons.Children.Add(maximizeButton);
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
        _stateLabel = new TextBlock { Text = "listening…", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 2) };
        _subLabel = new TextBlock { Text = "just start talking", FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center };

        // .aura .status-bar { justify-content: space-between; } .aura .chip
        // { background: var(--glass-bg-hi); border: 1px solid
        // var(--glass-border); border-radius: 999px; } .aura .chip i {
        // width:7px; height:7px; border-radius:50%; background:
        // linear-gradient(accent,accent2); } - pill chips with a small
        // gradient status dot. Backed by real Chrome/Gmail connection
        // state; the dot dims to a flat grey when not actually connected.
        _chromeDot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0) };
        _gmailDot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0) };
        _chromeChipText = new TextBlock { FontSize = 13 };
        _gmailChipText = new TextBlock { FontSize = 13 };
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

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(faceCanvas);
        stack.Children.Add(_stateLabel);
        stack.Children.Add(_subLabel);
        stack.Children.Add(_transcript.Root);

        // WEB's edge-to-edge chrome doesn't apply here - AURA's status row
        // is just centered pill chips, matching the mockup's own design -
        // but it shares WEB's other bug: wrapping everything (chipsRow
        // included) in one Margin(18) StackPanel lets any extra height from
        // OverlayLayout.PanelMinHeight land as a gap *below* the chips
        // instead of pinning them to the true bottom edge. A 2-row Grid
        // fixes it the same way: the Star row absorbs the extra space, the
        // Auto row (chipsRow) stays flush against the bottom.
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(stack, 0);
        Grid.SetRow(chipsRow, 1);
        layout.Children.Add(stack);
        layout.Children.Add(chipsRow);

        // Same fix as ARC/WEB - the 5-button header row is wider than the
        // orb, which otherwise drives the window's natural size, clipping
        // the rightmost chrome buttons outside it.
        _root = new Border { Width = OverlayLayout.PanelWidth, CornerRadius = new CornerRadius(28), Child = layout };
        ApplyPalette(palette);
    }

    public void ApplyState(OverlayState state)
    {
        _engaged = !state.Asleep;

        _face.ApplyState(state.Asleep);
        _badge.ApplyState(state.IsSpeaking && !state.Asleep);

        // While she's silently working (reading a page, filling a field,
        // searching memory), the state label shows *that* instead of the
        // generic listening… - the same text already printed to the
        // console, so the overlay stops looking idle during a long task.
        _stateLabel.Text = state.Asleep
            ? "sleeping…"
            : state.IsSpeaking
                ? "speaking…"
                : state.CurrentActivity is { } activity
                    ? TruncateActivity(activity).ToLowerInvariant()
                    : "listening…";
        _subLabel.Text = state.Asleep ? "press ctrl+alt+space to wake" : "just start talking";

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

        _face.ApplyPalette(palette);
        _transcript.SetRowFactory(BuildTranscriptRow);
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
    private static string TruncateActivity(string text) =>
        text.Length > 36 ? string.Concat(text.AsSpan(0, 36), "…") : text;
}
