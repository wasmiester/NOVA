using Avalonia.Media;

namespace Nova;

// AURA's two lightness modes - the pink/blue accent identity stays
// constant, only the ground and ink/glass tones flip. GroundStops is the
// exact 5-color background wash from the mockup's
// linear-gradient(160deg, ...) - was flattened to a 2-color blend before,
// which read noticeably duller/flatter than the source's soft rainbow.
internal sealed record AuraPalette(Color Ink, Color InkMute, Color[] GroundStops, Color Glass, Color GlassHi, Color GlassBorder)
{
    public static readonly AuraPalette Light = new(
        Ink: Color.FromRgb(44, 38, 69),
        InkMute: Color.FromRgb(125, 117, 153),
        // Confirmed live: the original stops read as near-white (each one
        // sat within a few points of 0xff on every channel), so the
        // translucent white Glass/GlassHi fills the components themselves
        // use had almost nothing to contrast against - everything blended
        // into one flat pale wash. Same pink-to-blue progression, same
        // gradient angle, just genuinely saturated pastels instead of
        // white with a tint.
        GroundStops:
        [
            Color.FromRgb(0xff, 0xd2, 0xea),
            Color.FromRgb(0xfa, 0xc2, 0xea),
            Color.FromRgb(0xe0, 0xcf, 0xfa),
            Color.FromRgb(0xc6, 0xdf, 0xfc),
            Color.FromRgb(0xd9, 0xea, 0xfc),
        ],
        Glass: Color.FromArgb(158, 255, 255, 255),
        GlassHi: Color.FromArgb(217, 255, 255, 255),
        // --glass-border: rgba(255,255,255,.8) in the mockup's light theme -
        // a distinct token from --glass-bg-hi (.85), not the same value.
        GlassBorder: Color.FromArgb(204, 255, 255, 255));

    public static readonly AuraPalette Dark = new(
        Ink: Color.FromRgb(243, 239, 250),
        InkMute: Color.FromRgb(182, 174, 209),
        GroundStops:
        [
            Color.FromRgb(0x1c, 0x17, 0x30),
            Color.FromRgb(0x20, 0x1a, 0x35),
            Color.FromRgb(0x24, 0x1d, 0x3d),
            Color.FromRgb(0x1a, 0x20, 0x36),
            Color.FromRgb(0x16, 0x1a, 0x2c),
        ],
        Glass: Color.FromArgb(18, 255, 255, 255),
        GlassHi: Color.FromArgb(31, 255, 255, 255),
        // --glass-border: rgba(255,255,255,.14) in the mockup's dark theme.
        GlassBorder: Color.FromArgb(36, 255, 255, 255));

    public static readonly Color Accent = Color.FromRgb(255, 143, 199);
    public static readonly Color Accent2 = Color.FromRgb(127, 178, 255);
}
