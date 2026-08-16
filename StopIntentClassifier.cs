using System.Text.RegularExpressions;

namespace Nova;

// Structural (code-level, not LLM-based) check for whether a barge-in
// utterance is explicitly asking Nova to stop the task she's currently
// working on - same reasoning as NavigationalClickGuard/CommandRiskClassifier:
// deterministic and instant, not an LLM round-trip, for something that needs
// to feel immediate. Talking over Nova's active speech only silences her
// voice (see NovaAssistant.StopSpeaking) - it does NOT cancel the underlying
// task by itself anymore, since a barge-in is very often just adding context
// or a follow-up task building on the current one, not a request to stop.
// This is the one thing that still does cancel it (see
// NovaAssistant.TranscribeAndQueueInterjectionAsync).
//
// Deliberately conservative/narrow (bare "stop" alone would false-positive
// on completely unrelated speech like "stop by the store on your way back") -
// missing a genuine stop request here isn't catastrophic, since it just
// falls through to being queued as a normal interjection, and Claude still
// sees it as context and can choose to stop on her own reasoning at the next
// checkpoint - this is a fast path for the obvious, unambiguous case, not
// the only way to actually stop something.
internal static class StopIntentClassifier
{
    private static readonly Regex[] StopPatterns =
    [
        new(@"^\s*stop[.!]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled), // a bare "stop"/"stop."/"stop!"
        new(@"\bstop\s+(that|it|right there|doing that)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(please|just)\s+stop\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bcancel\s+(that|it|this)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\babort\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"never\s*mind", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bforget\s+(it|that)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"don'?t\s+do\s+(that|this|it)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public static bool IsStopCommand(string utterance)
    {
        foreach (Regex pattern in StopPatterns)
        {
            if (pattern.IsMatch(utterance))
            {
                return true;
            }
        }

        return false;
    }
}
