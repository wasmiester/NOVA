namespace Nova;

// Shared panel width so all 3 skins render at the same size instead of the
// window visibly resizing sideways every time you cycle between them. 272
// is ARC's own natural width (~340) shaved by 20%, per direct request -
// ARC's header (the widest of the three) had to shrink its buttons/wordmark
// to fit, which WEB/AURA's narrower headers already clear comfortably.
internal static class OverlayLayout
{
    public const double PanelWidth = 272;

    // ARC's own natural (minimized, transcript-closed) height - the
    // tallest of the 3 skins, since its particle-field avatar is bigger
    // than WEB's smiley or AURA's orb. Applied as the window's MinHeight so
    // WEB/AURA's naturally shorter minimized states don't leave the window
    // visibly resizing every time you cycle skins - matches ARC's size
    // instead of each skin's own smaller natural content height.
    public const double PanelMinHeight = 405;
}
