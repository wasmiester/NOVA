using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Nova;

// Structural narrow-scope enforcement for browser_click: an allowlist of
// navigational click intents (pagination, expand/collapse, show more,
// close/dismiss) - not a policy Claude is asked to follow, a hard check
// browser_click can't get past regardless of what it's asked to click.
// Deliberately the opposite of a blocklist (default-deny, not
// default-allow-except-blocked), since that's what "narrow scope" actually
// means - the whole reason browser clicking never existed before is too
// important to hand back on the strength of a prompt instruction alone.
//
// Text-matching is inherently imperfect - a button reading "Next: Confirm
// Payment" would still slip past the "next" match. The blocklist below is
// a second check specifically against that failure mode, but it's still a
// heuristic, not a guarantee. For an ambient-initiated task, Gate 1 (the
// user hears the exact button text before anything is clicked) is still a
// real backstop for whatever this doesn't catch - but Gate 1 no longer
// fires for a directly-requested click (see NovaAssistant's
// _taskIsAmbientInitiated), so for the common case this list itself is the
// only thing standing between a misclassified button and a real click. A
// deliberate tradeoff from that redesign, not an oversight - worth keeping
// in mind if this list ever needs revisiting.
internal static class NavigationalClickGuard
{
    private static readonly string[] AllowedPatterns =
    [
        "next", "previous", "prev", "back", "show more", "load more", "view more",
        "read more", "expand", "collapse", "close", "dismiss", "page",
    ];

    // Checked separately as a whole word, not folded into AllowedPatterns'
    // plain substring check above - "add" collides with ordinary words
    // like "address" ("Edit Address", "Confirm Address") if matched as a
    // bare substring. Covers "+ Add" / "Add" / "Add another <section>",
    // the common way a form grows a repeatable section (another work
    // history entry, another website field) - confirmed live: exactly
    // this shape of button (bare "Add", adding a blank website-link row)
    // is common on job application forms and was previously refused
    // outright, forcing a manual click for no real safety benefit - it
    // only ever reveals more empty fields for the user to review, the
    // same risk class as expand/collapse, never something transactional.
    // Genuinely risky "add" variants (Add to Cart, Add Payment Method,
    // Add to Order) are still caught by BlockedKeywords below
    // (cart/payment/order/checkout/billing), checked first.
    private static readonly Regex AddWholeWord = new(@"\badd\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] BlockedKeywords =
    [
        "submit", "send", "buy", "purchase", "pay", "payment", "checkout", "cart",
        "billing", "order", "subscribe", "delete", "remove", "confirm", "apply",
        "post", "publish", "donate", "sign up", "register", "book", "reserve",
        "follow", "unfollow", "vote", "accept", "agree", "unsubscribe",
    ];

    public static bool IsAllowed(string label, out string? refusalReason)
    {
        string lower = label.ToLowerInvariant();

        string? blockedHit = BlockedKeywords.FirstOrDefault(lower.Contains);
        if (blockedHit is not null)
        {
            refusalReason = $"\"{label}\" looks like it could {blockedHit} something - browser_click is " +
                             "scoped to navigational actions only (pagination, expand/collapse, show more, " +
                             "close/dismiss), never anything that could submit, purchase, post, or otherwise " +
                             "act on the user's behalf. Tell the user to click this one themselves.";
            return false;
        }

        if (!AllowedPatterns.Any(lower.Contains) && !AddWholeWord.IsMatch(label))
        {
            refusalReason = $"\"{label}\" doesn't match a recognized navigational click (next/previous/page, " +
                             "show more/load more, expand/collapse, close/dismiss, add another entry) - " +
                             "browser_click won't click anything outside that narrow scope. Tell the user to " +
                             "click this one themselves.";
            return false;
        }

        refusalReason = null;
        return true;
    }
}
