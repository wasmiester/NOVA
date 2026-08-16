using System;

namespace Nova;

// The backend-touching actions every skin's chrome must wire to real
// buttons. Built once by OverlayWindow, handed to whichever skin is active
// via AttachActions - keeps all 3 skin implementations total strangers to
// NovaAssistant itself.
internal sealed record OverlaySkinActions(
    Action<bool> SetEngaged,
    Action CycleSkin,
    Action Close,
    // Called by a skin's own theme-toggle click (ARC/AURA only) so
    // OverlayWindow can persist the change - see IOverlaySkin.IsAlternateTheme.
    Action ThemeChanged);
