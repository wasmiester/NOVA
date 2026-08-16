using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nova;

// The "hologram globe" - a core, tilted orbit rings, rim spokes, and dust
// particles. The mockup draws these additively (canvas "lighter" blend);
// Avalonia has no vector-shape equivalent, so overlapping low-alpha strokes
// approximate it instead. Brushes/pens are allocated once and their
// .Color mutated per frame rather than recreated, to avoid GC pressure at
// the animation timer's frame rate. Driven by a ~60fps DispatcherTimer
// rather than a per-compositor-frame hook - simpler and more predictable
// than chasing Avalonia's lower-level rendering APIs for one animated
// avatar.
internal sealed class ArcFace : Control
{
    private readonly record struct OrbitRing(double RadiusRatio, double AspectRatio, double RotationOffset, double RotationSpeed);
    private readonly record struct RimSpoke(double Angle, double LengthRatio, double JitterPhase, double JitterSpeed);
    private readonly record struct DustParticle(double X, double Y, double Size, double Phase, double Speed);

    private const double Radius = 92;
    // Cut from the mockup's 90/64/150 - actual rasterization cost scales
    // with element count (confirmed: batching draw calls only bought a
    // ~12% CPU reduction, well short of what halving the frame rate alone
    // should have if dispatch overhead were the bottleneck), so density is
    // the lever that actually matters here. Alpha values below are bumped
    // to compensate so the field still reads as full instead of sparse.
    private const int SpokeCount = 54;
    private const int RingCount = 36;
    private const int ParticleCount = 90;
    private const double IdleGlow = 4;

    private readonly OrbitRing[] _rings = new OrbitRing[RingCount];
    private readonly RimSpoke[] _spokes = new RimSpoke[SpokeCount];
    private readonly DustParticle[] _particles = new DustParticle[ParticleCount];

    // Same reasoning as the spoke pen below - all 64 ring pens are set to
    // the exact same color (see ApplyPaletteToPens), so one shared Pen
    // plus one batched geometry (each ring's own transform baked into its
    // EllipseGeometry.Transform) replaces 64 individual
    // PushTransform+DrawEllipse pairs with a single draw call.
    private readonly Pen _ringPen = new(new SolidColorBrush(), 1.2);
    // All 90 spokes share one alpha per frame (it only depends on the
    // shared `glow` value, not anything per-spoke), so they don't need 90
    // separate Pens/DrawLine calls - one shared Pen plus one batched
    // geometry per frame replaces 90 individual draw dispatches with a
    // single one. The geometry is rebuilt fresh (not reopened in place)
    // every frame - Avalonia's compositor renders on its own thread, and
    // mutating the same StreamGeometry instance a still-in-flight render
    // is reading from crashes Skia's native path code with an access
    // violation (confirmed the hard way).
    private readonly Pen _spokePen = new(new SolidColorBrush(), 1);
    private readonly SolidColorBrush[] _particleBrushes = new SolidColorBrush[ParticleCount];
    private readonly SolidColorBrush _hotspotBrush = new(Color.FromArgb(217, 255, 255, 255));
    private readonly RadialGradientBrush _coreBrush;

    // jumpChance/easing here only matter while speaking (Update()) - idle
    // ticks call Settle() instead, which ignores both and just eases toward
    // IdleGlow.
    private readonly SmoothedRandomWalk _amp = new(10, 4, 30, jumpChance: 0.18, easing: 0.22);

    // 30fps rather than 60 - every animated element here (ring drift, spoke
    // jitter, particle twinkle) is slow ambient motion where the extra
    // frames are imperceptible, and this alone roughly halves the CPU
    // cost of redrawing ~300 elements every tick under software
    // rendering (see AvaloniaOverlayHost's RenderingMode).
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private double _t;
    private DateTime _lastTick = DateTime.UtcNow;

    private ArcPalette _palette = ArcPalette.Gold;
    public bool IsAsleep { get; set; }

    // Drives the pulsing-equalizer look: the core/spokes/particles only
    // actually pulse while she's speaking. Otherwise _amp settles to a
    // stable, steady glow instead of continuing to wander.
    public bool IsSpeaking { get; set; }

    public ArcPalette Palette
    {
        get => _palette;
        set
        {
            _palette = value;
            ApplyPaletteToPens();
        }
    }

    public ArcFace()
    {
        Width = Radius * 2 + 20;
        Height = Radius * 2 + 20;

        var random = new Random();

        for (int i = 0; i < RingCount; i++)
        {
            _rings[i] = new OrbitRing(
                RadiusRatio: 0.18 + random.NextDouble() * 0.86,
                AspectRatio: 0.2 + random.NextDouble() * 0.5,
                RotationOffset: random.NextDouble() * Math.PI * 2,
                RotationSpeed: (random.NextDouble() - 0.5) * 0.12);
        }

        for (int i = 0; i < SpokeCount; i++)
        {
            _spokes[i] = new RimSpoke(
                Angle: i / (double)SpokeCount * Math.PI * 2,
                LengthRatio: 0.55 + random.NextDouble() * 0.4,
                JitterPhase: random.NextDouble() * Math.PI * 2,
                JitterSpeed: 0.6 + random.NextDouble() * 1.2);
        }

        for (int i = 0; i < ParticleCount; i++)
        {
            double pr = Math.Sqrt(random.NextDouble()) * Radius * 0.96;
            double pa = random.NextDouble() * Math.PI * 2;
            _particles[i] = new DustParticle(
                X: Math.Cos(pa) * pr,
                Y: Math.Sin(pa) * pr * 0.92,
                // Scaled up ~1.3x (sqrt(150/90), the old/new particle
                // count ratio) so total covered dust area stays roughly
                // the same as the denser original despite fewer particles.
                Size: 0.8 + random.NextDouble() * 2.0,
                Phase: random.NextDouble() * Math.PI * 2,
                Speed: 0.6 + random.NextDouble() * 1.4);
            _particleBrushes[i] = new SolidColorBrush();
        }

        _coreBrush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Transparent, 0.4),
                new GradientStop(Colors.Transparent, 1),
            },
        };

        ApplyPaletteToPens();
        _timer.Tick += OnTick;
    }

    private void ApplyPaletteToPens()
    {
        ((SolidColorBrush)_ringPen.Brush!).Color = WithAlpha(_palette.Ring, 0.075);
        ((SolidColorBrush)_spokePen.Brush!).Color = WithAlpha(_palette.Spoke, 0.16);

        foreach (SolidColorBrush brush in _particleBrushes)
        {
            brush.Color = WithAlpha(_palette.Dust, 0.4);
        }

        _coreBrush.GradientStops[0].Color = WithAlpha(_palette.CoreInner, 1.0);
        _coreBrush.GradientStops[1].Color = WithAlpha(_palette.CoreOuter, 0.55);
        _coreBrush.GradientStops[2].Color = WithAlpha(_palette.CoreOuter, 0);
    }

    public void Activate()
    {
        _lastTick = DateTime.UtcNow;
        _timer.Start();
    }

    public void Deactivate() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        _t += (now - _lastTick).TotalSeconds;
        _lastTick = now;

        if (IsSpeaking)
        {
            _amp.Update();
        }
        else
        {
            _amp.Settle(IdleGlow);
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

        if (IsAsleep)
        {
            var dimPen = new Pen(new SolidColorBrush(Color.FromArgb(56, 150, 150, 150)), 1);
            dc.DrawEllipse(null, dimPen, new Point(cx, cy), Radius * 0.62, Radius * 0.62);
            return;
        }

        double glow = _amp.Value / 24.0;

        double coreR = Radius * (0.09 + glow * 0.09);
        DrawCenteredEllipse(dc, _coreBrush, null, cx, cy, coreR * 3, coreR * 3);
        DrawCenteredEllipse(dc, _hotspotBrush, null, cx, cy, coreR * 0.55, coreR * 0.55);

        // Scale first, then rotate (matches the mockup's own ctx.rotate();
        // ctx.scale(1, ry); ctx.arc(...) composition order). All 64 rings
        // share one Pen (see _ringPen), so instead of 64 separate
        // PushTransform+DrawEllipse pairs, each ring's transform is baked
        // directly into its own EllipseGeometry and all 64 are combined
        // into one GeometryGroup drawn with a single DrawGeometry call -
        // same visual result (Geometry.Transform affects the stroke the
        // same way a DrawingContext-level PushTransform would), far fewer
        // draw dispatches under software rendering.
        var ringGroup = new GeometryGroup();
        Matrix AroundCenter(Matrix m) => Matrix.CreateTranslation(-cx, -cy) * m * Matrix.CreateTranslation(cx, cy);
        for (int i = 0; i < RingCount; i++)
        {
            OrbitRing ring = _rings[i];
            Matrix scale = AroundCenter(Matrix.CreateScale(1, ring.AspectRatio));
            Matrix rotate = AroundCenter(Matrix.CreateRotation(ring.RotationOffset + _t * ring.RotationSpeed));
            double r = Radius * ring.RadiusRatio;
            ringGroup.Children.Add(new EllipseGeometry(new Rect(cx - r, cy - r, r * 2, r * 2))
            {
                Transform = new MatrixTransform(scale * rotate),
            });
        }

        dc.DrawGeometry(null, _ringPen, ringGroup);

        // Profiling found this cost MORE than the 36 batched rings despite
        // being simpler shapes and fewer of them - the StreamGeometry
        // rebuilt via 54 separate BeginFigure/LineTo/EndFigure calls every
        // frame was the actual expense, not the line strokes themselves.
        // Same fix as rings: one GeometryGroup of pre-built LineGeometry
        // children (no per-figure path-construction API), one draw call.
        var spokeGroup = new GeometryGroup();
        for (int i = 0; i < SpokeCount; i++)
        {
            RimSpoke spoke = _spokes[i];
            double jitter = Math.Sin(_t * spoke.JitterSpeed * 2 + spoke.JitterPhase) * 0.15 * (0.4 + glow);
            double outer = Radius * (spoke.LengthRatio + jitter);
            double inner = outer - (Radius * 0.1 + glow * Radius * 0.13);
            double x1 = cx + Math.Cos(spoke.Angle) * inner, y1 = cy + Math.Sin(spoke.Angle) * inner * 0.92;
            double x2 = cx + Math.Cos(spoke.Angle) * outer, y2 = cy + Math.Sin(spoke.Angle) * outer * 0.92;
            spokeGroup.Children.Add(new LineGeometry(new Point(x1, y1), new Point(x2, y2)));
        }

        ((SolidColorBrush)_spokePen.Brush!).Color = WithAlpha(_palette.Spoke, 0.16 + glow * 0.16);
        dc.DrawGeometry(null, _spokePen, spokeGroup);

        for (int i = 0; i < ParticleCount; i++)
        {
            DustParticle particle = _particles[i];
            double twinkle = 0.4 + 0.3 * Math.Sin(_t * particle.Speed + particle.Phase) + glow * 0.2;
            double b = Math.Clamp(twinkle, 0.15, 1.0);
            _particleBrushes[i].Color = WithAlpha(_palette.Dust, 0.3 + b * 0.55);
            DrawCenteredEllipse(dc, _particleBrushes[i], null, cx + particle.X, cy + particle.Y, particle.Size, particle.Size);
        }
    }

    // Avalonia's DrawEllipse takes a bounding Rect, not WPF's
    // center-point + radiusX/radiusY - this converts once at every call
    // site instead of repeating the same Rect math inline everywhere.
    private static void DrawCenteredEllipse(DrawingContext dc, IBrush? fill, Pen? pen, double cx, double cy, double rx, double ry) =>
        dc.DrawEllipse(fill, pen, new Rect(cx - rx, cy - ry, rx * 2, ry * 2));

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);
}
