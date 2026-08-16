using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nova;

// The smiley face itself - circle/eyes/mouth swap discretely per state
// (closed-smile idle vs. a 😄-style grin while speaking vs. closed-eyes/
// flat-mouth asleep), plus a continuous slow vertical bob. Driven by direct
// sine-wave math on a DispatcherTimer tick (bob = amplitude * sin(t*rate),
// exactly matching the mockup's own wt-based formula) rather than WPF's
// AutoReverse DoubleAnimation+SineEase, which only approximated it - this
// is both simpler in Avalonia (no BeginAnimation/EasingFunction machinery
// needed) and more faithful to the source.
internal sealed class WebFace : Control
{
    private const double FaceRadius = 42; // faceR in the mockup
    private static readonly Color InkColor = Color.FromRgb(12, 27, 48);

    private readonly TranslateTransform _bobTransform = new();
    private readonly DispatcherTimer _blinkTimer = new();
    private readonly DispatcherTimer _bobTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Random _random = new();
    private bool _blinking;

    private bool _isSpeaking;
    private bool _isAsleep;
    private double _averageBarHeight;
    private double _wt;
    private DateTime _lastBobTick = DateTime.UtcNow;

    public WebFace()
    {
        double size = FaceRadius * 2 + 20;
        Width = size;
        Height = size;
        RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        RenderTransform = _bobTransform;

        _blinkTimer.Tick += OnBlinkTick;
        _bobTimer.Tick += OnBobTick;
    }

    public void ApplyState(bool isSpeaking, bool isAsleep, double averageBarHeight)
    {
        _isSpeaking = isSpeaking;
        _isAsleep = isAsleep;
        _averageBarHeight = averageBarHeight;
        InvalidateVisual();
    }

    public void Activate()
    {
        _lastBobTick = DateTime.UtcNow;
        _bobTimer.Start();

        _blinkTimer.Interval = TimeSpan.FromMilliseconds(3200 + _random.NextDouble() * 1400);
        _blinkTimer.Start();
    }

    public void Deactivate()
    {
        _bobTimer.Stop();
        _blinkTimer.Stop();
    }

    // bob = webAsleep ? sin(wt*0.6)*1.5 : sin(wt*1.3)*4 in the mockup -
    // smaller and slower while asleep, reading as calmer "breathing"
    // instead of the same energetic bob continuing regardless of state.
    private void OnBobTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        _wt += (now - _lastBobTick).TotalSeconds;
        _lastBobTick = now;

        double amplitude = _isAsleep ? 1.5 : 4;
        double rate = _isAsleep ? 0.6 : 1.3;
        _bobTransform.Y = amplitude * Math.Sin(_wt * rate);
    }

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        if (_blinking)
        {
            _blinking = false;
            _blinkTimer.Interval = TimeSpan.FromMilliseconds(3200 + _random.NextDouble() * 1400);
        }
        else
        {
            _blinking = true;
            _blinkTimer.Interval = TimeSpan.FromMilliseconds(110);
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        double cx = Bounds.Width / 2;
        double cy = Bounds.Height / 2;
        if (cx <= 0 || cy <= 0)
        {
            return;
        }

        DrawCenteredEllipse(dc, new SolidColorBrush(Color.FromRgb(255, 217, 61)), new Pen(new SolidColorBrush(InkColor), 4), cx, cy, FaceRadius, FaceRadius);
        DrawCenteredEllipse(dc, new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), null, cx - 14, cy - 15, 9, 9);

        bool closedEyes = _isAsleep || _blinking;
        if (_isSpeaking && !_isAsleep)
        {
            DrawHappyEye(dc, cx - 15, cy - 7);
            DrawHappyEye(dc, cx + 15, cy - 7);
        }
        else
        {
            DrawEye(dc, cx - 15, cy - 7, closedEyes);
            DrawEye(dc, cx + 15, cy - 7, closedEyes);
        }

        var mouthPen = new Pen(new SolidColorBrush(InkColor), 3) { LineCap = PenLineCap.Round };
        if (_isAsleep)
        {
            dc.DrawLine(mouthPen, new Point(cx - 6, cy + 10), new Point(cx + 6, cy + 10));
        }
        else if (_isSpeaking)
        {
            // mouthR = 15 + avgBar*0.22 against faceR=42 in the mockup.
            double mouthR = 15 + _averageBarHeight * 0.22;
            double mouthY = cy + 2;
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(cx - mouthR, mouthY), isFilled: true);
                ctx.ArcTo(new Point(cx + mouthR, mouthY), new Size(mouthR, mouthR), 0, isLargeArc: false, SweepDirection.CounterClockwise);
                ctx.EndFigure(true);
            }

            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(122, 16, 48)), new Pen(new SolidColorBrush(InkColor), 3), geometry);

            // A white "teeth" strip inside the open mouth, drawn after the
            // mouth fill so it sits on top. roundRectPath(fx - mouthR + 3,
            // mouthY, (mouthR-3)*2, 6, 2) in the mockup - top edge flush
            // with mouthY, corner radius 2, fixed 6px tall.
            double teethWidth = (mouthR - 3) * 2;
            const double teethHeight = 6;
            dc.DrawRectangle(
                Brushes.White,
                null,
                new RoundedRect(new Rect(cx - teethWidth / 2, mouthY, teethWidth, teethHeight), 2, 2));
        }
        else
        {
            // ctx.arc(fx, fy+3, 15, PI*0.15, PI*0.85) in the mockup - a
            // true partial arc of a 15-radius circle centered 3px below
            // face center. Sampled as explicit points (not a single ArcTo)
            // since ArcTo's sweep-direction flag reads backwards for this
            // particular start/end/radius combination.
            const double smileRadius = 15;
            double smileCenterY = cy + 3;
            const double startAngle = Math.PI * 0.15;
            const double endAngle = Math.PI * 0.85;
            const int smileSegments = 16;
            var smilePen = new Pen(new SolidColorBrush(InkColor), 4) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var smile = new StreamGeometry();
            using (StreamGeometryContext ctx = smile.Open())
            {
                ctx.BeginFigure(new Point(cx + smileRadius * Math.Cos(startAngle), smileCenterY + smileRadius * Math.Sin(startAngle)), isFilled: false);
                for (int i = 1; i <= smileSegments; i++)
                {
                    double angle = startAngle + (endAngle - startAngle) * i / smileSegments;
                    ctx.LineTo(new Point(cx + smileRadius * Math.Cos(angle), smileCenterY + smileRadius * Math.Sin(angle)));
                }

                ctx.EndFigure(false);
            }

            dc.DrawGeometry(null, smilePen, smile);
        }
    }

    // drawSmileyEye in the mockup: closed line spans x-5..x+5 (10 wide);
    // open eye is roundRectPath(x-4, y-6, 8, 12, 3).
    private static void DrawEye(DrawingContext dc, double x, double y, bool closed)
    {
        var pen = new Pen(new SolidColorBrush(InkColor), 3) { LineCap = PenLineCap.Round };
        if (closed)
        {
            dc.DrawLine(pen, new Point(x - 5, y), new Point(x + 5, y));
            return;
        }

        dc.DrawRectangle(new SolidColorBrush(InkColor), null, new RoundedRect(new Rect(x - 4, y - 6, 8, 12), 3, 3));
    }

    // drawHappyEye in the mockup: lineWidth 3.5, points at x-+7,y+3 to
    // x,y-5 (span 14, apex height 8).
    private static void DrawHappyEye(DrawingContext dc, double x, double y)
    {
        var pen = new Pen(new SolidColorBrush(InkColor), 3.5) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x - 7, y + 3), isFilled: false);
            ctx.LineTo(new Point(x, y - 5));
            ctx.LineTo(new Point(x + 7, y + 3));
            ctx.EndFigure(false);
        }

        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawCenteredEllipse(DrawingContext dc, IBrush? fill, Pen? pen, double cx, double cy, double rx, double ry) =>
        dc.DrawEllipse(fill, pen, new Rect(cx - rx, cy - ry, rx * 2, ry * 2));
}
