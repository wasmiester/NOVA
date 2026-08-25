namespace Nova;

// Shared panel width so all 3 skins render at the same size instead of the
// window visibly resizing sideways every time you cycle between them. 272
// is ARC's own natural width (~340) shaved by 20%, per direct request -
// ARC's header (the widest of the three) had to shrink its buttons/wordmark
// to fit, which WEB/AURA's narrower headers already clear comfortably.
internal static class OverlayLayout
{
    public const double PanelWidth = 272;

    // Only used while maximize mode's activity feed and conversation sit
    // side by side instead of stacked. Matches the source design mockup's
    // own .split rule exactly (grid-template-columns: minmax(0, 1fr)
    // minmax(0, 1.5fr); gap: 18px) - the conversation column is
    // deliberately 1.5x the activity column's width, not an even split, so
    // ActivityColumnWidth/ConversationColumnWidth below hold that same
    // ratio: 180 and 270, plus the 18px gap = 468px the splitRow columns
    // actually need.
    //
    // MaximizedPanelWidth is NOT simply 468 + a uniform 36px margin -
    // confirmed live as a real bug: that assumption only holds for
    // AuraSkin (_root has no border, _layout margin is 18 per side = 36
    // total). WebSkin's _root has a 3px border AND innerBorder has a 2px
    // border AND its margin is 16 (not 18) per side - 42px total chrome,
    // 6px short of what the fixed columns need at 504. ArcSkin's _root has
    // a 1px border on top of its 18px margin - 38px total, 2px short. 510
    // is sized off WEB's 42px (the worst case, i.e. the most chrome around
    // the content), so every skin's splitRow gets at least the 468px it
    // needs - AURA and ARC end up with a few extra unused pixels of slack
    // at the row's trailing edge instead, which is harmless.
    //
    // The window and the active skin's own root both resize to this
    // together (see IOverlaySkin.IsMaximized) - explicit assignment, not
    // SizeToContent.Width, which previously desynced the window's outer
    // bounds from what actually rendered when content width changed at
    // runtime (see AvaloniaOverlayWindow's own constructor comment on
    // Width).
    public const double MaximizedPanelWidth = 510;
    public const double ActivityColumnWidth = 180;
    public const double ConversationColumnWidth = 270;
    public const double ColumnGap = 18;

    // TranscriptHeight matches the mockup's own .arc-transcript/
    // .web-transcript/.aura-transcript exactly. ActivityLogHeight is NOT
    // the mockup's own 229 - that number assumed a plain content height
    // with no extra frame around either box. The real implementation wraps
    // both panels in their own bordered/filled Border (see each skin's own
    // *Box fields) and the conversation column also carries the
    // type-to-talk row below the transcript, neither of which the mockup's
    // flat 229/200 pair accounted for. Confirmed live: at the literal 229,
    // the activity column's bottom edge sat visibly short of the
    // conversation column's (which ends at transcriptBox + the input row).
    // 240 accounts for that gap on ARC/WEB (whose transcript also sits in
    // its own bordered box); AURA's own transcript has no such box (see
    // AuraSkin's own comment on why), so its two columns land a bit less
    // exactly flush, but still far closer than the original 229.
    public const double ActivityLogHeight = 240;
    public const double TranscriptHeight = 200;

    // ARC's own natural (minimized, transcript-closed) height - the
    // tallest of the 3 skins, since its particle-field avatar is bigger
    // than WEB's smiley or AURA's orb. Applied as the window's MinHeight so
    // WEB/AURA's naturally shorter minimized states don't leave the window
    // visibly resizing every time you cycle skins - matches ARC's size
    // instead of each skin's own smaller natural content height.
    public const double PanelMinHeight = 405;
}
