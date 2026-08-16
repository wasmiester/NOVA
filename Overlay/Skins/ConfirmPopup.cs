using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nova;

// The Gate 2 confirmation popup, shared by all 3 skins: a dimmed backdrop
// plus a card with the pending action's plain-language description and
// Confirm/Cancel buttons. Added alongside the existing spoken Gate 2 ask
// (see NovaAssistant's Gate 2 block and ToolDescriptions.DescribeGate2Review)
// so an irreversible action (currently just send_email; more will land in
// ToolCatalog.Gate2Tools as delete/git tooling is added) can only actually
// be authorized by a real click - a misheard "yes" or a stray
// affirmative-sounding word picked up by STT can no longer send an email
// or delete a file on its own (see NovaAssistant.ConfirmGate2Review and the
// Gate 2 resolution block in ProcessTextInputAsync, which no longer accepts
// voice as a valid confirmation while this is up).
//
// Same "shared plumbing, per-skin look" split as TranscriptPanel's
// rowFactory - only the generic backdrop/card/button assembly lives here,
// every color/font/radius comes from whichever skin is currently active
// (see ConfirmPopupStyle, IOverlaySkin.PopupStyle).
internal sealed class ConfirmPopup
{
    private readonly Grid _root;
    private readonly Border _backdrop;
    private readonly Border _card;
    private readonly TextBlock _header;
    private readonly TextBlock _body;
    private readonly Button _confirmButton;
    private readonly Button _cancelButton;
    private readonly Action<bool> _onDecision;
    private ConfirmPopupStyle _style;

    public Control Root => _root;

    public bool IsShown => _root.IsVisible;

    public ConfirmPopup(ConfirmPopupStyle style, Action<bool> onDecision)
    {
        _style = style;
        _onDecision = onDecision;

        _header = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeight.Bold, FontSize = 12 };
        _body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 16),
        };

        _cancelButton = new Button { Content = "Cancel", Cursor = new Cursor(StandardCursorType.Hand), Padding = new Thickness(16, 7, 16, 7) };
        _cancelButton.Click += (_, _) => Resolve(approved: false);
        _confirmButton = new Button { Content = "Confirm", Cursor = new Cursor(StandardCursorType.Hand), Padding = new Thickness(16, 7, 16, 7) };
        _confirmButton.Click += (_, _) => Resolve(approved: true);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 10 };
        buttonRow.Children.Add(_cancelButton);
        buttonRow.Children.Add(_confirmButton);

        var cardContent = new StackPanel();
        cardContent.Children.Add(_header);
        cardContent.Children.Add(_body);
        cardContent.Children.Add(buttonRow);

        _card = new Border
        {
            Padding = new Thickness(18),
            MaxWidth = OverlayLayout.PanelWidth - 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = cardContent,
        };

        // Swallows every click anywhere over the panel while a review is
        // pending - nothing behind the popup (comfort button, sleep
        // toggle, drag-to-move) is reachable until it's resolved one way
        // or the other. The window's own drag handler additionally checks
        // ConfirmPopup.IsShown directly (pointer-pressed bubbles up to it
        // regardless of Handled, same reason ArcSkin's own chrome buttons
        // need an explicit ancestor check rather than relying on Handled).
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

    public void Show(string prompt)
    {
        _header.Text = _style.HeaderText;
        _body.Text = prompt;
        _root.IsVisible = true;
    }

    public void HideImmediate() => _root.IsVisible = false;

    private void Resolve(bool approved)
    {
        HideImmediate();
        _onDecision(approved);
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

        _header.Text = style.HeaderText;
        _header.Foreground = style.HeaderBrush;
        _header.FontFamily = style.Font;
        _body.Foreground = style.BodyBrush;
        _body.FontFamily = style.Font;

        _confirmButton.FontFamily = style.Font;
        _confirmButton.Background = style.ConfirmBackground;
        _confirmButton.Foreground = style.ConfirmForeground;
        _confirmButton.BorderBrush = style.ConfirmBorder;
        _confirmButton.BorderThickness = new Thickness(style.BorderThickness);
        FlatButtonStyle.Apply(_confirmButton, cornerRadius: style.ButtonCornerRadius, pixelShadowColor: style.PixelShadowColor);

        _cancelButton.FontFamily = style.Font;
        _cancelButton.Background = style.CancelBackground;
        _cancelButton.Foreground = style.CancelForeground;
        _cancelButton.BorderBrush = style.CancelBorder;
        _cancelButton.BorderThickness = new Thickness(style.BorderThickness);
        FlatButtonStyle.Apply(_cancelButton, cornerRadius: style.ButtonCornerRadius, pixelShadowColor: style.PixelShadowColor);
    }
}
