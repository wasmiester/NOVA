using Avalonia.Media;

namespace Nova;

// Two color themes for ARC, matching the finished mockup: warm gold
// (default) and a lighter cyan. Swapped live via ArcSkin.ToggleTheme.
internal sealed record ArcPalette(Color CoreInner, Color CoreOuter, Color Ring, Color Spoke, Color Dust)
{
    public static readonly ArcPalette Gold = new(
        CoreInner: Color.FromRgb(255, 236, 180),
        CoreOuter: Color.FromRgb(255, 193, 100),
        Ring: Color.FromRgb(255, 197, 105),
        Spoke: Color.FromRgb(255, 203, 115),
        Dust: Color.FromRgb(255, 218, 150));

    public static readonly ArcPalette Cyan = new(
        CoreInner: Color.FromRgb(214, 247, 255),
        CoreOuter: Color.FromRgb(120, 224, 255),
        Ring: Color.FromRgb(110, 220, 255),
        Spoke: Color.FromRgb(125, 226, 255),
        Dust: Color.FromRgb(150, 232, 255));
}
