using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace Nova;

// Wires the send behavior for the maximized panel's type-to-talk field -
// see NovaAssistant.DispatchTypedText, reached via OverlaySkinActions.
// SendTypedText rather than a direct NovaAssistant reference (skins stay
// "total strangers to NovaAssistant itself" - see OverlaySkinActions' own
// doc comment). Deliberately just a wiring helper, not a full component
// class like TranscriptPanel/ActivityLogPanel: there's no shared visual
// chrome or ongoing animation state to own, each skin already builds its
// own styled TextBox/Button (matching its own visual language, same as
// everywhere else in this project - see FlatButtonStyle/
// TranscriptScrollBarStyle for the same "small static helper" shape used
// for shared behavior that isn't a real component).
internal static class TypeToTalkInput
{
    public static void WireUp(TextBox input, Button sendButton, Action<string> sendTypedText)
    {
        void Send()
        {
            string text = input.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                return;
            }

            sendTypedText(text);
            input.Text = "";
        }

        sendButton.Click += (_, _) => Send();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Send();
            }
        };
    }
}
