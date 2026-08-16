using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
// System.IO is implicitly global in this project (ImplicitUsings) and its
// Path clashes with Avalonia.Controls.Shapes.Path - alias to disambiguate.
using Path = Avalonia.Controls.Shapes.Path;

namespace Nova;

// Wifi-signal-style "talking" badge - three tilted concentric arcs, fully
// invisible except while Nova's actually speaking. Each arc's opacity
// independently random-walks (SmoothedRandomWalk, same technique as ARC's
// face) so it reads as a tiny graphic equalizer rather than one uniform
// pulse - updated on the same ~130ms state tick as everything else in this
// skin, not a separate per-frame render loop.
internal sealed class AuraBadge
{
    private readonly Grid _root;
    private readonly Path[] _arcs;
    private readonly SmoothedRandomWalk[] _levels;

    public Control Root => _root;

    public AuraBadge(Color accent2)
    {
        double[] radii = [6.4, 9.4, 13.4];
        _arcs = new Path[radii.Length];
        _levels = new SmoothedRandomWalk[radii.Length];

        _root = new Grid
        {
            Width = 24,
            Height = 24,
            RenderTransform = new RotateTransform(45),
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            IsHitTestVisible = false,
            Opacity = 0,
        };

        for (int i = 0; i < radii.Length; i++)
        {
            _levels[i] = new SmoothedRandomWalk(1, 0.25, 1.0, jumpChance: 0.5, easing: 0.5);
            _arcs[i] = BuildArc(radii[i], accent2);
            _root.Children.Add(_arcs[i]);
        }
    }

    // Called once per ~130ms state tick, not per frame - see the class
    // comment above for why that cadence is fine here.
    public void ApplyState(bool isSpeaking)
    {
        _root.Opacity = isSpeaking ? 1 : 0;
        if (!isSpeaking)
        {
            return;
        }

        for (int i = 0; i < _arcs.Length; i++)
        {
            _levels[i].Update();
            _arcs[i].Opacity = _levels[i].Value;
        }
    }

    private static Path BuildArc(double radius, Color color)
    {
        Point center = new(12, 18);
        Point start = PointOnCircle(center, radius, -45);
        Point end = PointOnCircle(center, radius, 45);

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments!.Add(new ArcSegment { Point = end, Size = new Size(radius, radius), RotationAngle = 0, IsLargeArc = false, SweepDirection = SweepDirection.Clockwise });
        var geometry = new PathGeometry();
        geometry.Figures!.Add(figure);

        return new Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2.6,
            StrokeLineCap = PenLineCap.Round,
        };
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Sin(radians), center.Y - radius * Math.Cos(radians));
    }
}
