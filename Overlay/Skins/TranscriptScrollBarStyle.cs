using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Nova;

// The mockup's transcript panels use a thin, accent-colored scroll thumb
// (--scroll-thumb) instead of the OS's default grey scrollbar. Avalonia's
// ScrollBar exposes its Thumb as a themed template part reachable via a
// /template/ selector, so this only needs to override the Thumb's
// Background/CornerRadius/Width rather than rebuilding the whole
// ScrollBar template from scratch (the WPF version had to, since
// FrameworkElementFactory couldn't populate Track's CLR-only Thumb
// property any other way - Avalonia's style selectors don't have that
// limitation).
internal static class TranscriptScrollBarStyle
{
    public static void Apply(ScrollViewer scrollViewer, Color thumbColor)
    {
        var thumbStyle = new Style(x => x.OfType<ScrollBar>().Template().OfType<Thumb>());
        thumbStyle.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(thumbColor)));
        thumbStyle.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(3)));
        thumbStyle.Setters.Add(new Setter(Layoutable.WidthProperty, 5.0));

        var barStyle = new Style(x => x.OfType<ScrollBar>());
        barStyle.Setters.Add(new Setter(Layoutable.WidthProperty, 6.0));

        // Indexer-free Add is fine here (unlike the old WPF Resources
        // dictionary) - Styles is a list, re-applying with a new color
        // (ARC's theme toggle) just appends another override on top, which
        // is harmless since the last-added Setter for the same property
        // wins.
        scrollViewer.Styles.Add(barStyle);
        scrollViewer.Styles.Add(thumbStyle);
    }
}
