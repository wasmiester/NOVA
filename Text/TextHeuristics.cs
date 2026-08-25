using System.Linq;
using System.Text.RegularExpressions;

namespace Nova;

internal static class TextHeuristics
{
    // A simple keyword check, not real NLU - good enough for MVP but easy to
    // fool with an indirect answer ("I guess so") or trip up on negation buried
    // in a longer sentence.
    public static bool LooksAffirmative(string text)
    {
        string lower = text.ToLowerInvariant();
        string[] noWords = ["no", "don't", "do not", "stop", "cancel", "nevermind", "never mind"];
        // Word-boundary matching, not a raw substring search - confirmed live
        // as a real bug: "no".Contains via lower.Contains also matches inside
        // "nova" ("nova".Contains("no")), so "yes Nova, send it" - about the
        // most natural way to answer while addressing her by name - read as a
        // decline before any yes-word was ever checked.
        if (noWords.Any(word => ContainsWord(lower, word)))
        {
            return false;
        }

        string[] yesWords = ["yes", "yeah", "yep", "sure", "go ahead", "do it", "okay", "ok", "please do", "affirmative"];
        return yesWords.Any(word => ContainsWord(lower, word));
    }

    private static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b");

    // Finds the last sentence-ending punctuation in the buffer so far, so a
    // completed sentence (or run of sentences) can be flushed to TTS immediately
    // instead of waiting for Claude's full reply.
    public static int LastSentenceBoundaryIndex(string text)
    {
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] is '.' or '!' or '?' or '\n')
            {
                return i;
            }
        }

        return -1;
    }
}
