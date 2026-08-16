using Avalonia.Controls;

namespace Nova;

// Implemented by ArcSkin/WebSkin/AuraSkin (each a UserControl-style root).
// A behavioral contract, not a shared visual tree - WEB's chunky buttons
// and AURA's frosted-glass buttons aren't the same control restyled,
// they're genuinely different chrome per the design, so there's no value
// forcing a shared XAML/visual base - only the plumbing below is shared.
internal interface IOverlaySkin
{
    // The skin's own root visual - swapped into OverlayWindow.Content when
    // this skin becomes active, swapped out when the user cycles away.
    Control Root { get; }

    // Called once per DispatcherTimer tick (~130ms) while this skin is
    // active. Cheap work only (set fields, start/stop Storyboards) -
    // continuous per-frame drawing (ARC/WEB) happens on the skin's own
    // CompositionTarget.Rendering subscription, not here.
    void ApplyState(OverlayState state);

    // Cycles the skin's own local color theme (gold/cyan for ARC, light/
    // dark for AURA). WEB has no second theme in the source design - its
    // implementation is a documented no-op, not a missing feature.
    void ToggleTheme();

    // False = this skin's default theme (gold for ARC, light for AURA);
    // true = its alternate (cyan / dark). Always false for WEB. OverlayWindow
    // reads this to persist the theme across restarts, and calls
    // ToggleTheme() once on startup to restore a saved "alternate" state -
    // there's no separate SetTheme, reusing the existing toggle keeps this
    // to one code path per skin instead of two.
    bool IsAlternateTheme { get; }

    // Wires actions into this skin's own buttons. Called once, right after
    // construction, before the skin is ever shown.
    void AttachActions(OverlaySkinActions actions);

    // Per-skin visual parameters for the shared Gate 2 confirmation popup
    // (see ConfirmPopup/ConfirmPopupStyle). Read on skin cycle and on this
    // skin's own theme-toggle callback - a plain getter (not cached) so a
    // live theme change is picked up without a separate notification path.
    ConfirmPopupStyle PopupStyle { get; }

    // Called when this skin becomes the active one - (re)starts its own
    // CompositionTarget.Rendering subscription / Storyboards.
    void Activate();

    // Called when switched away from - stops this skin's
    // CompositionTarget.Rendering subscription and pauses any running
    // Storyboards. An inactive skin must not keep paying for a frame tick
    // in the background (see Phase 6 verification in the overlay plan).
    void Deactivate();
}
