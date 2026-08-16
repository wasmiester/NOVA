using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nova;

// A ring of thick radial bars wrapped around WebFace - only actually
// random-walks toward full height while Nova's speaking; otherwise eases
// down to a small resting nub (or fully to 0 while asleep). AverageHeight
// is read each tick by WebSkin to drive how wide-open the grin mouth looks.
// Driven by a ~60fps DispatcherTimer (see ArcFace for why, over Avalonia's
// lower-level per-compositor-frame hooks).
internal sealed class WebEqualizerRing : Control
{
    private const int BarCount = 10;
    private const double InnerRadius = 56; // ringInnerR in the mockup
    private const double MaxBarLength = 26;

    private static readonly Color[] BarColors =
    [
        Color.FromRgb(255, 59, 48),
        Color.FromRgb(255, 203, 71),
        Color.FromRgb(41, 255, 143),
        Color.FromRgb(0, 229, 255),
        Color.FromRgb(255, 47, 163),
    ];

    private readonly double[] _heights = new double[BarCount];
    private readonly double[] _targets = new double[BarCount];
    private readonly Pen[] _pens = new Pen[BarCount];
    private readonly Random _random = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public bool IsSpeaking { get; set; }
    public bool IsAsleep { get; set; }
    public double AverageHeight { get; private set; }

    public WebEqualizerRing()
    {
        double size = (InnerRadius + MaxBarLength) * 2 + 12;
        Width = size;
        Height = size;

        for (int i = 0; i < BarCount; i++)
        {
            _heights[i] = 4;
            _targets[i] = 4;
            _pens[i] = new Pen(new SolidColorBrush(BarColors[i % BarColors.Length]), 8) { LineCap = PenLineCap.Round };
        }

        _timer.Tick += OnTick;
    }

    public void Activate() => _timer.Start();

    public void Deactivate() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        double sum = 0;
        for (int i = 0; i < BarCount; i++)
        {
            if (IsAsleep)
            {
                _targets[i] = 0;
            }
            else if (IsSpeaking)
            {
                if (_random.NextDouble() < 0.09)
                {
                    _targets[i] = 4 + _random.NextDouble() * MaxBarLength;
                }
            }
            else
            {
                _targets[i] = 3;
            }

            _heights[i] += (_targets[i] - _heights[i]) * 0.2;
            sum += _heights[i];
        }

        AverageHeight = sum / BarCount;
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

        for (int i = 0; i < BarCount; i++)
        {
            double angle = i / (double)BarCount * Math.PI * 2 - Math.PI / 2;
            double outer = InnerRadius + _heights[i];
            double x1 = cx + Math.Cos(angle) * InnerRadius, y1 = cy + Math.Sin(angle) * InnerRadius;
            double x2 = cx + Math.Cos(angle) * outer, y2 = cy + Math.Sin(angle) * outer;
            dc.DrawLine(_pens[i], new Point(x1, y1), new Point(x2, y2));
        }
    }
}
