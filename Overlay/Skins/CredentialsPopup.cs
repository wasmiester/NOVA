using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nova;

// The Google-credentials popup, shared by all 3 skins: shown the first
// time a Google tool (search_email, send_email, list/create_calendar_event)
// is actually attempted with no account connected (see
// NovaAssistant.RequestGoogleCredentials) - rather than requiring the user
// to already know to go hand-edit secrets/.env, Nova surfaces the prompt
// right on the overlay, in the moment it's actually needed. Same "shared
// plumbing, per-skin look" split as ConfirmPopup/TranscriptPanel - colors/
// fonts come from IOverlaySkin.PopupStyle, the same record ConfirmPopup
// already uses.
//
// Security notes (this handles real OAuth credentials, however low-stakes
// an "installed app" Client Secret technically is under Google's own
// threat model - see GoogleAuth's doc comment):
//   - The secret field is masked (PasswordChar) - never shown in plaintext
//     once typed.
//   - Neither value is ever written to Console/ErrorLog by this class or
//     by NovaAssistant.ConnectGoogleAccountAsync - only to secrets/.env
//     (see EnvLoader.SetValues, gitignored, the same file
//     ANTHROPIC_API_KEY already lives in) and to Google's own OAuth
//     endpoints via GoogleAuth.AuthorizeAsync, the exact call path startup
//     already uses for a config-file-provided Client ID/Secret.
//   - The backdrop swallows clicks the same way ConfirmPopup's does -
//     nothing else on the panel is reachable while this is up.
internal sealed class CredentialsPopup
{
    private readonly Grid _root;
    private readonly Border _backdrop;
    private readonly Border _card;
    private readonly TextBlock _header;
    private readonly TextBlock _helpText;
    private readonly TextBox _clientIdBox;
    private readonly TextBox _clientSecretBox;
    private readonly TextBlock _statusText;
    private readonly Button _connectButton;
    private readonly Button _cancelButton;
    private readonly Action<string, string> _onConnect;
    private readonly Action _onCancel;
    private ConfirmPopupStyle _style;

    public Control Root => _root;

    public bool IsShown => _root.IsVisible;

    public CredentialsPopup(ConfirmPopupStyle style, Action<string, string> onConnect, Action onCancel)
    {
        _style = style;
        _onConnect = onConnect;
        _onCancel = onCancel;

        _header = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6), FontWeight = FontWeight.Bold, FontSize = 12 };
        _helpText = new TextBlock
        {
            Text = "Get these from Google Cloud Console → APIs & Services → Credentials (see secrets/.env.example).",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 12),
        };

        _clientIdBox = new TextBox { PlaceholderText = "Client ID", Margin = new Thickness(0, 0, 0, 8) };
        // PasswordChar masks every keystroke immediately - the secret is
        // never rendered in plaintext on screen once typed, only ever held
        // as the TextBox's own Text value in memory.
        _clientSecretBox = new TextBox { PlaceholderText = "Client Secret", PasswordChar = '●' };

        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
            IsVisible = false,
        };

        _cancelButton = new Button { Content = "Cancel", Cursor = new Cursor(StandardCursorType.Hand), Padding = new Thickness(16, 7, 16, 7) };
        _cancelButton.Click += (_, _) =>
        {
            HideImmediate();
            _onCancel();
        };
        _connectButton = new Button { Content = "Connect", Cursor = new Cursor(StandardCursorType.Hand), Padding = new Thickness(16, 7, 16, 7) };
        _connectButton.Click += (_, _) => _onConnect(_clientIdBox.Text ?? "", _clientSecretBox.Text ?? "");

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 10, Margin = new Thickness(0, 14, 0, 0) };
        buttonRow.Children.Add(_cancelButton);
        buttonRow.Children.Add(_connectButton);

        var cardContent = new StackPanel();
        cardContent.Children.Add(_header);
        cardContent.Children.Add(_helpText);
        cardContent.Children.Add(_clientIdBox);
        cardContent.Children.Add(_clientSecretBox);
        cardContent.Children.Add(_statusText);
        cardContent.Children.Add(buttonRow);

        _card = new Border
        {
            Padding = new Thickness(18),
            Width = OverlayLayout.PanelWidth - 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = cardContent,
        };

        // Swallows every click anywhere over the panel while this is up -
        // same reasoning as ConfirmPopup's own backdrop (see its comment);
        // the window's drag handler additionally checks
        // CredentialsPopup.IsShown directly, same pattern.
        _backdrop = new Border { IsHitTestVisible = true };
        _backdrop.SizeChanged += (_, e) => _backdrop.Clip = RoundedRectClip.Build(e.NewSize.Width, e.NewSize.Height, _style.PanelCornerRadius);

        _root = new Grid
        {
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _root.Children.Add(_backdrop);
        _root.Children.Add(_card);

        ApplyStyle(style);
    }

    public void Show() => _root.IsVisible = true;

    public void HideImmediate()
    {
        _root.IsVisible = false;
        // Cleared on hide (both a successful connect and an explicit
        // Cancel go through this) rather than left sitting in the fields -
        // a half-typed secret shouldn't linger once the popup's gone.
        _clientIdBox.Text = string.Empty;
        _clientSecretBox.Text = string.Empty;
    }

    // Reflects NovaAssistant.GoogleConnecting/GoogleConnectError - polled
    // on the overlay's own ~130ms tick, same as everything else it shows.
    public void ApplyConnectState(bool connecting, string? error)
    {
        _clientIdBox.IsEnabled = !connecting;
        _clientSecretBox.IsEnabled = !connecting;
        _connectButton.IsEnabled = !connecting;

        if (connecting)
        {
            _statusText.Text = "Connecting - complete the sign-in in your browser...";
            _statusText.Foreground = _style.MutedBrush;
            _statusText.IsVisible = true;
        }
        else if (error is not null)
        {
            _statusText.Text = error;
            _statusText.Foreground = _style.ErrorBrush;
            _statusText.IsVisible = true;
        }
        else
        {
            _statusText.IsVisible = false;
        }
    }

    public void ApplyStyle(ConfirmPopupStyle style)
    {
        _style = style;

        _backdrop.Background = style.Backdrop;
        _backdrop.Clip = RoundedRectClip.Build(_backdrop.Bounds.Width, _backdrop.Bounds.Height, style.PanelCornerRadius);

        _card.Background = style.CardBackground;
        _card.BorderBrush = style.CardBorder;
        _card.BorderThickness = new Thickness(style.BorderThickness);
        _card.CornerRadius = new CornerRadius(style.CardCornerRadius);

        _header.Text = style.CredentialsHeaderText;
        _header.Foreground = style.HeaderBrush;
        _header.FontFamily = style.Font;
        _helpText.Foreground = style.MutedBrush;
        _helpText.FontFamily = style.Font;
        _statusText.FontFamily = style.Font;

        foreach (TextBox box in new[] { _clientIdBox, _clientSecretBox })
        {
            box.Background = style.InputBackground;
            box.Foreground = style.InputForeground;
            box.BorderBrush = style.InputBorder;
            box.BorderThickness = new Thickness(style.BorderThickness);
            box.CaretBrush = style.InputCaret;
            box.FontFamily = style.Font;
            box.CornerRadius = new CornerRadius(style.ButtonCornerRadius);
            box.Padding = new Thickness(10, 7, 10, 7);
        }

        _connectButton.FontFamily = style.Font;
        _connectButton.Background = style.ConfirmBackground;
        _connectButton.Foreground = style.ConfirmForeground;
        _connectButton.BorderBrush = style.ConfirmBorder;
        _connectButton.BorderThickness = new Thickness(style.BorderThickness);
        FlatButtonStyle.Apply(_connectButton, cornerRadius: style.ButtonCornerRadius, pixelShadowColor: style.PixelShadowColor);

        _cancelButton.FontFamily = style.Font;
        _cancelButton.Background = style.CancelBackground;
        _cancelButton.Foreground = style.CancelForeground;
        _cancelButton.BorderBrush = style.CancelBorder;
        _cancelButton.BorderThickness = new Thickness(style.BorderThickness);
        FlatButtonStyle.Apply(_cancelButton, cornerRadius: style.ButtonCornerRadius, pixelShadowColor: style.PixelShadowColor);
    }
}
