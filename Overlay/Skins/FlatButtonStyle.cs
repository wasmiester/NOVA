using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nova;

// Avalonia's default (Fluent-theme) Button template bakes its own hover/
// pressed chrome on top of Background, so a button never renders as the
// flat, exact color it's given. This swaps in a flat Border+ContentPresenter
// template instead, and restores hover/pressed/focus feedback with direct
// pointer-event handlers on a transparent overlay Border rather than WPF-
// style triggers (which Avalonia doesn't have in the same form) - simpler,
// and avoids fighting Avalonia's own pseudo-class styling for a one-off,
// per-instance color-agnostic wash.
internal static class FlatButtonStyle
{
    // pixelShadowColor opts into WEB's ".pixel-btn" 3D bevel: box-shadow:
    // 3px 3px 0 var(--edge-lo), inset 0 0 0 2px rgba(255,255,255,.1) - a
    // hard offset copy plus a faint inner highlight, which a plain
    // BorderBrush can't reproduce. Left null for ARC/AURA's flat look.
    public static void Apply(Button button, double cornerRadius = 4, Color? pixelShadowColor = null)
    {
        var overlay = new Border
        {
            CornerRadius = new CornerRadius(cornerRadius),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsHitTestVisible = false,
        };

        button.Template = new FuncControlTemplate<Button>((b, scope) =>
        {
            var grid = new Grid();

            if (pixelShadowColor is { } shadowColor)
            {
                // A RenderTransform offset (not negative Margin - that
                // shrinks the element's own layout size instead of just
                // shifting where it paints). The mockup's raw "3px 3px"
                // offset is in its 2x-scaled coordinate space - halved to
                // 1.5px here, or every WEB pixel button reads roughly twice
                // as chunky as intended.
                var shadow = new Border
                {
                    Background = new SolidColorBrush(shadowColor),
                    CornerRadius = new CornerRadius(cornerRadius),
                    RenderTransform = new TranslateTransform(1.5, 1.5),
                };
                grid.Children.Add(shadow);
            }

            var border = new Border { CornerRadius = new CornerRadius(cornerRadius) };
            border.Bind(Border.BackgroundProperty, b.GetObservable(TemplatedControl.BackgroundProperty));
            border.Bind(Border.BorderBrushProperty, b.GetObservable(TemplatedControl.BorderBrushProperty));
            border.Bind(Border.BorderThicknessProperty, b.GetObservable(TemplatedControl.BorderThicknessProperty));
            grid.Children.Add(border);

            if (pixelShadowColor is not null)
            {
                var inset = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(cornerRadius),
                    Margin = new Thickness(1),
                    IsHitTestVisible = false,
                };
                grid.Children.Add(inset);
            }

            grid.Children.Add(overlay);

            var presenter = new ContentPresenter
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            presenter.Bind(ContentPresenter.ContentProperty, b.GetObservable(ContentControl.ContentProperty));
            presenter.Bind(ContentPresenter.MarginProperty, b.GetObservable(TemplatedControl.PaddingProperty));
            grid.Children.Add(presenter);

            return grid;
        });

        button.PointerEntered += (_, _) => overlay.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        button.PointerExited += (_, _) => overlay.Background = Brushes.Transparent;
        button.PointerPressed += (_, _) => overlay.Background = new SolidColorBrush(Color.FromArgb(46, 0, 0, 0));
        button.PointerReleased += (_, _) =>
            overlay.Background = button.IsPointerOver
                ? new SolidColorBrush(Color.FromArgb(28, 255, 255, 255))
                : Brushes.Transparent;
    }
}
