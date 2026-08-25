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
    // active. Cheap work only (set fields, update timer-driven animation
    // state) - continuous per-frame drawing (ARC/WEB's own avatars) happens
    // on each skin's own DispatcherTimer tick loop, not here.
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

    // True while this skin has collapsed itself down to the slim resting
    // pill. Read once per tick (same cadence as ApplyState) so
    // OverlayWindow can drop its own Window.MinHeight floor while
    // collapsed - that floor exists to keep the full panel from ever
    // looking cramped, but applied unconditionally it also stopped the
    // window from ever actually shrinking to the pill's real height,
    // confirmed live: the pill rendered floating inside a window still
    // sized for the full panel instead of collapsing around it.
    bool IsCollapsed { get; }

    // True while this skin's activity feed and conversation transcript are
    // both showing (the maximize toggle) - read once per tick, same as
    // IsCollapsed, so OverlayWindow can widen itself to
    // OverlayLayout.MaximizedPanelWidth for the side-by-side layout and
    // shrink back to PanelWidth otherwise. The skin's own root Width
    // changes in lockstep, in its own maximize-button click handler - see
    // that handler's own comment for why this isn't done through
    // SizeToContent instead.
    bool IsMaximized { get; }

    // Imposes collapsed/maximized state from outside, rather than only
    // ever toggling in response to this skin's own button click - lets
    // OverlayWindow carry a resting/collapsed/maximized state across a
    // skin cycle (see its own CycleSkin) instead of every skin quietly
    // resetting to its own independent default the moment it becomes
    // active. Confirmed live as a real gap: cycling skins while maximized
    // (or collapsed) used to silently drop back to the plain minimized
    // panel, since each skin's IsVisible flags were entirely private state
    // nothing else ever touched.
    void SetCollapsed(bool collapsed);

    void SetMaximized(bool maximized);

    // Per-skin visual parameters for the shared Gate 2 confirmation popup
    // (see ConfirmPopup/ConfirmPopupStyle). Read on skin cycle and on this
    // skin's own theme-toggle callback - a plain getter (not cached) so a
    // live theme change is picked up without a separate notification path.
    ConfirmPopupStyle PopupStyle { get; }

    // Called when this skin becomes the active one - (re)starts its own
    // avatar's DispatcherTimer tick loop (see ArcFace/WebFace/AuraFace).
    void Activate();

    // Called when switched away from - stops this skin's own avatar timer.
    // An inactive skin must not keep paying for a tick in the background.
    void Deactivate();
}
