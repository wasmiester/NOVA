using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nova;

// The orb + wide-set blinking eyes with a slow independent look-around
// drift, plus the soft (non-pixelated - see WebZzz for the pixel-art
// version) floating "Zzz" shown while asleep.
//
// Driven by a single ~60fps DispatcherTimer that evaluates small piecewise-
// linear keyframe tables (EvaluateKeyframes below) rather than WPF's
// DoubleAnimationUsingKeyFrames/BeginAnimation - Avalonia's declarative
// Animation/Transition system doesn't have a direct equivalent for
// "animate this arbitrary Transform property on a free-floating object
// forever with per-instance start delays", and manually evaluating the
// same (time, value) pairs the WPF version used keyframe-for-keyframe
// keeps the exact timing/values from the mockup's own @keyframes.
internal sealed class AuraFace
{
    private const double OrbSize = 140;
    private const double EyeSize = 16;
    private const double EyeGap = 38;

    private readonly Grid _root;
    private readonly Ellipse _orb;
    private readonly Border _leftEye;
    private readonly Border _rightEye;
    private readonly Ellipse _leftHighlight;
    private readonly Ellipse _rightHighlight;
    private readonly TranslateTransform _lookTransform = new();
    private readonly ScaleTransform _leftBlink = new(1, 1);
    private readonly ScaleTransform _rightBlink = new(1, 1);
    private readonly TranslateTransform[] _zzzTransforms;
    private readonly TextBlock[] _zzzGlyphs;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _t;
    private DateTime _lastTick = DateTime.UtcNow;
    private bool _isAsleep;
    private bool _animating;

    public Control Root => _root;

    // (time, value) pairs, matching the mockup's @keyframes lookAround /
    // blink / zzz timings exactly - see the WPF version's key-frame
    // construction this replaced for the original per-frame breakdown.
    private static readonly (double Time, double Value)[] LookXFrames =
    [
        (0, 0), (0.25, 0), (0.55, -11), (0.8, -11), (1.15, 0),
        (1.4, 0), (1.7, 11), (1.95, 11), (2.2, 0), (6.5, 0),
    ];

    private static readonly (double Time, double Value)[] LookYFrames =
    [
        (0, 0), (0.25, 0), (0.55, -1), (1.4, -1), (1.7, -1),
        (1.95, -1), (2.2, 1.5), (2.35, 1.5), (2.5, 0), (6.5, 0),
    ];

    private static readonly (double Time, double Value)[] BlinkFrames =
    [
        (0, 1), (4.05, 1), (4.2, 0.12), (4.35, 1), (4.4, 1),
    ];

    private static readonly (double Time, double Value)[] ZzzOpacityFrames =
    [
        (0, 0), (0.65, 0.9), (2.6, 0),
    ];

    private static readonly (double Time, double Value)[] ZzzRiseFrames =
    [
        (0, 6), (2.6, -12),
    ];

    private static readonly double[] ZzzDelays = [0, 0.35, 0.7];

    public AuraFace(AuraPalette palette)
    {
        _orb = new Ellipse { Width = OrbSize, Height = OrbSize };

        _leftEye = BuildEye(_leftBlink, out _leftHighlight);
        _rightEye = BuildEye(_rightBlink, out _rightHighlight);
        var spacer = new Control { Width = EyeGap };

        var eyesRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -6, 0, 0),
            RenderTransform = _lookTransform,
        };
        eyesRow.Children.Add(_leftEye);
        eyesRow.Children.Add(spacer);
        eyesRow.Children.Add(_rightEye);

        _zzzGlyphs = [BuildZzzGlyph("Z", 20), BuildZzzGlyph("Z", 15), BuildZzzGlyph("z", 11)];
        _zzzTransforms = new TranslateTransform[_zzzGlyphs.Length];
        for (int i = 0; i < _zzzGlyphs.Length; i++)
        {
            var t = new TranslateTransform();
            _zzzTransforms[i] = t;
            _zzzGlyphs[i].RenderTransform = t;
        }

        var zzzPanel = new Canvas { Width = 60, Height = 46, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -10, -10, 0), IsHitTestVisible = false };
        Canvas.SetRight(_zzzGlyphs[0], 4); Canvas.SetTop(_zzzGlyphs[0], 14);
        Canvas.SetRight(_zzzGlyphs[1], 16); Canvas.SetTop(_zzzGlyphs[1], 4);
        Canvas.SetRight(_zzzGlyphs[2], 28); Canvas.SetTop(_zzzGlyphs[2], -4);
        foreach (TextBlock glyph in _zzzGlyphs)
        {
            zzzPanel.Children.Add(glyph);
        }

        _root = new Grid { Width = OrbSize, Height = OrbSize };
        _root.Children.Add(_orb);
        _root.Children.Add(eyesRow);
        _root.Children.Add(zzzPanel);

        _timer.Tick += OnTick;

        ApplyPalette(palette);
    }

    public void ApplyPalette(AuraPalette palette)
    {
        _orb.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(AuraPalette.Accent, 0.45),
                new GradientStop(AuraPalette.Accent2, 1.0),
            },
            Center = new RelativePoint(0.34, 0.3, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.34, 0.3, RelativeUnit.Relative),
            // The mockup is `radial-gradient(circle at 34% 30%, ...)` with
            // no explicit size, which CSS defaults to "farthest-corner" -
            // the 100% stop lands at the box's farthest corner, not the
            // visible circle's edge. Avalonia's own default radius (0.5)
            // is much smaller, so the same stop percentages would finish
            // the pink->blue transition well inside the visible circle -
            // reading as far more blue than the source. Setting Radius to
            // the actual farthest-corner distance (sqrt(0.66^2+0.70^2)
            // from (0.34,0.3) to (1,1)) reproduces CSS's sizing.
            RadiusX = new RelativeScalar(0.962, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.962, RelativeUnit.Relative),
        };
        ((SolidColorBrush)_leftEye.Background!).Color = palette.Ink;
        ((SolidColorBrush)_rightEye.Background!).Color = palette.Ink;
    }

    public void ApplyState(bool isAsleep)
    {
        if (isAsleep == _isAsleep)
        {
            return;
        }

        _isAsleep = isAsleep;
        if (isAsleep)
        {
            _leftEye.Height = 4;
            _rightEye.Height = 4;
            _leftEye.CornerRadius = new CornerRadius(2);
            _rightEye.CornerRadius = new CornerRadius(2);
            _leftHighlight.IsVisible = false;
            _rightHighlight.IsVisible = false;
        }
        else
        {
            foreach (TextBlock glyph in _zzzGlyphs)
            {
                glyph.Opacity = 0;
            }

            _leftEye.Height = EyeSize;
            _rightEye.Height = EyeSize;
            _leftEye.CornerRadius = new CornerRadius(EyeSize / 2);
            _rightEye.CornerRadius = new CornerRadius(EyeSize / 2);
            _leftHighlight.IsVisible = true;
            _rightHighlight.IsVisible = true;
        }
    }

    public void Activate()
    {
        _animating = true;
        _lastTick = DateTime.UtcNow;
        _timer.Start();
    }

    public void Deactivate()
    {
        _animating = false;
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        _t += (now - _lastTick).TotalSeconds;
        _lastTick = now;

        if (!_animating || AccessibilitySettings.PrefersReducedMotion)
        {
            return;
        }

        if (_isAsleep)
        {
            for (int i = 0; i < _zzzGlyphs.Length; i++)
            {
                double localT = _t - ZzzDelays[i];
                if (localT < 0)
                {
                    continue;
                }

                _zzzGlyphs[i].Opacity = EvaluateKeyframes(ZzzOpacityFrames, localT);
                _zzzTransforms[i].Y = EvaluateKeyframes(ZzzRiseFrames, localT);
            }
        }
        else
        {
            _lookTransform.X = EvaluateKeyframes(LookXFrames, _t);
            _lookTransform.Y = EvaluateKeyframes(LookYFrames, _t);
            double blink = EvaluateKeyframes(BlinkFrames, _t);
            _leftBlink.ScaleY = blink;
            _rightBlink.ScaleY = blink;
        }
    }

    // Linear interpolation between the surrounding (time, value) pairs,
    // wrapping at the last keyframe's time - the same semantics as WPF's
    // LinearDoubleKeyFrame + RepeatBehavior.Forever.
    private static double EvaluateKeyframes((double Time, double Value)[] frames, double t)
    {
        double cycle = frames[^1].Time;
        double tm = cycle > 0 ? t % cycle : 0;
        for (int i = 1; i < frames.Length; i++)
        {
            if (tm <= frames[i].Time)
            {
                double t0 = frames[i - 1].Time, t1 = frames[i].Time;
                double v0 = frames[i - 1].Value, v1 = frames[i].Value;
                double frac = t1 > t0 ? (tm - t0) / (t1 - t0) : 0;
                return v0 + (v1 - v0) * frac;
            }
        }

        return frames[^1].Value;
    }

    // A small white glint in the upper-left of each eye - gives the flat
    // dark circle some life instead of reading as a dead dot.
    private static Border BuildEye(ScaleTransform blink, out Ellipse highlight)
    {
        highlight = new Ellipse
        {
            Width = EyeSize * 0.3,
            Height = EyeSize * 0.3,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(EyeSize * 0.2, EyeSize * 0.18, 0, 0),
        };

        return new Border
        {
            Width = EyeSize,
            Height = EyeSize,
            CornerRadius = new CornerRadius(EyeSize / 2),
            Background = new SolidColorBrush(Colors.Black),
            RenderTransform = blink,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Child = highlight,
        };
    }

    private static TextBlock BuildZzzGlyph(string text, double fontSize) => new()
    {
        Text = text,
        FontSize = fontSize,
        FontWeight = FontWeight.Bold,
        Foreground = new SolidColorBrush(AuraPalette.Accent2),
        Opacity = 0,
    };
}
