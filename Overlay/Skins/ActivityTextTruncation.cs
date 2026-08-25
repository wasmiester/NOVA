using System;

namespace Nova;

// The narrow panel can't fit a long activity string ("searching memory for
// \"...\"") on one line - cap it rather than let it wrap/overflow. Each
// skin's own state label shrinks to fit via a Viewbox instead of relying on
// truncation for normal-length activity text - this cutoff only exists as a
// floor so a truly pathological string doesn't shrink to an illegible
// sliver. Shared across ArcSkin/WebSkin/AuraSkin - was previously
// triplicated identically (same cutoff, same comment) in each one, with
// zero actual skin-specific behavior.
internal static class ActivityTextTruncation
{
    public static string Truncate(string text) =>
        text.Length > 90 ? string.Concat(text.AsSpan(0, 90), "…") : text;
}
