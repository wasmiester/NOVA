namespace Nova;

// Which listening behavior the user has chosen - orthogonal to engaged/
// asleep (NovaAssistant._engaged), which tracks the current moment-to-moment
// state within whichever mode is active. Switching modes resets _engaged to
// that mode's own default (see NovaAssistant.SwitchActivationModeAsync) -
// after that, both modes behave identically (full listening once engaged,
// asleep via a sleep phrase/the overlay button/AFK, the hotkey the only way
// back in) - the only real difference is what you get by default.
internal enum ActivationMode
{
    // Starts engaged - the default, today's plain respond-when-spoken-to
    // behavior with no hotkey required to begin.
    Prompted,

    // Starts asleep - nothing is transcribed or reacted to until the
    // hotkey is pressed, specifically so ambient conversation near the
    // machine can never accidentally prompt her.
    KeyBind,
}
