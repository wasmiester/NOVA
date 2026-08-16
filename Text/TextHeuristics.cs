using System.Linq;

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
        if (noWords.Any(lower.Contains))
        {
            return false;
        }

        string[] yesWords = ["yes", "yeah", "yep", "sure", "go ahead", "do it", "okay", "ok", "please do", "affirmative"];
        return yesWords.Any(lower.Contains);
    }

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
