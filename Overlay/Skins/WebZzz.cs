using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nova;

// Three pixel-art "Z" glyphs (drawn as literal filled-square grids, not
// text - text would anti-alias and lose the pixel-art look), fixed in
// place at full opacity while WEB is asleep - the mockup's drawPixelZ
// draws them at fixed coordinates every frame, no fade or float. Each
// glyph is drawn twice - a dark offset copy behind a bright one - for the
// same hard "outline" look used elsewhere in this panel, instead of a
// blur-based drop shadow.
internal sealed class WebZzz : Control
{
    private static readonly int[,] ZGrid =
    {
        { 1, 1, 1, 1, 1 },
        { 0, 0, 0, 0, 1 },
        { 0, 0, 0, 1, 0 },
        { 0, 0, 1, 0, 0 },
        { 0, 1, 0, 0, 0 },
        { 1, 0, 0, 0, 0 },
        { 1, 1, 1, 1, 1 },
    };

    private readonly record struct Glyph(double PixelSize, Color Color, double OffsetX, double OffsetY);

    private static readonly Glyph[] Glyphs =
    [
        new(5, Colors.White, 0, 0),
        new(4, Color.FromRgb(255, 203, 71), 20, -16),
        new(3, Color.FromRgb(0, 229, 255), 36, -28),
    ];

    private bool _isAsleep;

    public bool IsAsleep
    {
        get => _isAsleep;
        set
        {
            if (_isAsleep == value)
            {
                return;
            }

            _isAsleep = value;
            InvalidateVisual();
        }
    }

    public WebZzz()
    {
        Width = 90;
        Height = 70;
    }

    public void Activate()
    {
    }

    public void Deactivate()
    {
    }

    public override void Render(DrawingContext dc)
    {
        if (!IsAsleep)
        {
            return;
        }

        foreach (Glyph glyph in Glyphs)
        {
            DrawGlyph(dc, glyph.OffsetX, glyph.OffsetY, glyph.PixelSize, glyph.Color);
        }
    }

    private static void DrawGlyph(DrawingContext dc, double x, double y, double pixelSize, Color color)
    {
        DrawGrid(dc, x - pixelSize * 0.4, y + pixelSize * 0.4, pixelSize, Color.FromRgb(5, 10, 20));
        DrawGrid(dc, x, y, pixelSize, color);
    }

    private static void DrawGrid(DrawingContext dc, double x, double y, double pixelSize, Color color)
    {
        var brush = new SolidColorBrush(color);
        for (int row = 0; row < ZGrid.GetLength(0); row++)
        {
            for (int col = 0; col < ZGrid.GetLength(1); col++)
            {
                if (ZGrid[row, col] == 1)
                {
                    dc.DrawRectangle(brush, null, new RoundedRect(new Rect(x + col * pixelSize, y + row * pixelSize, pixelSize, pixelSize)));
                }
            }
        }
    }
}
