using Avalonia;
using Avalonia.Media;

namespace Nova;

// Shared rounded-rectangle clip geometry - a Border's own CornerRadius only
// rounds its background/border paint, not an Effect (glow) or a sibling
// element layered over it in a Grid, either of which otherwise paints past
// the true rounded shape and shows up as a faint square poking past the
// corners. Originally ArcSkin-only (its panel glow); now also shared by
// ConfirmPopup's backdrop, which needs to match whichever skin's own corner
// radius is currently active.
internal static class RoundedRectClip
{
    public static StreamGeometry Build(double width, double height, double radius)
    {
        double r = System.Math.Min(radius, System.Math.Min(width, height) / 2);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(r, 0), isFilled: true);
            ctx.LineTo(new Point(width - r, 0));
            ctx.ArcTo(new Point(width, r), new Size(r, r), 0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(width, height - r));
            ctx.ArcTo(new Point(width - r, height), new Size(r, r), 0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(r, height));
            ctx.ArcTo(new Point(0, height - r), new Size(r, r), 0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(0, r));
            ctx.ArcTo(new Point(r, 0), new Size(r, r), 0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }

        return geometry;
    }
}
