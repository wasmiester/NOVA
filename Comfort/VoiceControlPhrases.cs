using System.Linq;

namespace Nova;

// Local, always-checked-first phrase detection for controlling Nova's own
// operating state - deliberately NOT routed through Claude, since these are
// meta commands about Nova herself (go dormant, switch activation mode)
// rather than real conversation. Handling them locally keeps them free and
// instant. Waking back up from dormant has no voice phrase - the hotkey
// (NovaAssistant.TriggerReadyAcknowledgment) is the only way, so there's
// nothing to detect for that direction; both of these only ever get a
// chance to run while already engaged (see ProcessUtteranceAsync, which
// skips transcription entirely while asleep).
internal static class VoiceControlPhrases
{
    private static readonly string[] SleepPhrases =
    [
        "take a break", "call it a day", "let's call it", "that's all for now",
        "go to sleep", "stop listening",
    ];

    public static bool ContainsSleepPhrase(string text)
    {
        string lower = text.ToLowerInvariant();
        return SleepPhrases.Any(lower.Contains);
    }

    // Detects an explicit request to switch activation mode, e.g. "switch
    // to key bind mode" / "go back to prompted mode". Requires both a mode
    // keyword AND switching intent, so it doesn't fire on a sentence that
    // just happens to mention one of these words in passing.
    public static ActivationMode? TryDetectModeSwitch(string text)
    {
        string lower = text.ToLowerInvariant();
        bool hasSwitchIntent = new[] { "switch", "go to", "turn on", "activate", "set" }.Any(lower.Contains)
            && new[] { "mode", "level" }.Any(lower.Contains);

        if (!hasSwitchIntent)
        {
            return null;
        }

        if (new[] { "key bind", "key-bind", "keybind", "key bound" }.Any(lower.Contains))
        {
            return ActivationMode.KeyBind;
        }

        if (lower.Contains("prompted"))
        {
            return ActivationMode.Prompted;
        }

        return null;
    }
}
