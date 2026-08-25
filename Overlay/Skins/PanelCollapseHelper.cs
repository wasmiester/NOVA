using System;
using Avalonia.Controls;

namespace Nova;

// Swaps between a skin's full panel and its slim resting pill - shared
// across ArcSkin/WebSkin/AuraSkin since this was previously triplicated
// verbatim in each one, including an identical bugfix comment copy-pasted
// three times. Only this plumbing is shared, not the visual pill/panel
// themselves (see IOverlaySkin's own doc comment on why the 3 skins stay
// "total strangers" visually) - same "small static helper for shared
// behavior" shape already used by FlatButtonStyle/TypeToTalkInput.
internal static class PanelCollapseHelper
{
    // setMaximized is called with false before collapsing, never after
    // expanding - un-maximizing first resets both the panel's width and
    // its content visibility together, rather than leaving stale wide
    // content (and a stale wide _root.Width) behind a pill that's meant to
    // always render at the normal resting width. Confirmed live as a real
    // bug when this was still three separate copies: collapsing while
    // maximized left the pill rendering at the wide maximized width, since
    // nothing reset it.
    public static void SetCollapsed(bool collapsed, Control body, Control pill, Action<bool> setMaximized)
    {
        if (collapsed)
        {
            setMaximized(false);
        }

        body.IsVisible = !collapsed;
        pill.IsVisible = collapsed;
    }
}
