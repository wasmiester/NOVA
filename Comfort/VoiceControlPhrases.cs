using System.Linq;

namespace Nova;

// Local, always-checked-first phrase detection for controlling Nova's own
// operating state - deliberately NOT routed through Claude, since this is a
// meta command about Nova herself (go dormant) rather than real
// conversation. Handling it locally keeps it free and instant. Waking back
// up from dormant has no voice phrase anymore - the hotkey
// (NovaAssistant.TriggerReadyAcknowledgment) is the only way, so there's
// nothing to detect for that direction.
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
}
