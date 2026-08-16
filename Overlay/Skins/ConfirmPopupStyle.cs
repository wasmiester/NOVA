using Avalonia.Media;

namespace Nova;

// Per-skin visual parameters for the overlay's two modal popups - the Gate
// 2 confirmation (see ConfirmPopup) and the Google-credentials prompt (see
// CredentialsPopup). One record covers both: they're the same "dimmed
// backdrop + card + a couple of buttons" shape, just with the credentials
// popup adding text-input fields - so both pull from the same per-skin
// palette rather than each skin exposing two near-identical style objects.
// Lets one shared popup implementation still look like it belongs to
// whichever skin is active, the same "shared plumbing, per-skin look"
// split TranscriptPanel already uses via its rowFactory delegate. Each
// skin's PopupStyle property builds one of these from its own existing
// brushes/fonts/corner radii rather than introducing a second set of
// colors just for this.
internal sealed record ConfirmPopupStyle(
    string HeaderText,
    // ConfirmPopup and CredentialsPopup show different headers on the same
    // card chrome ("CONFIRM ACTION" vs "CONNECT GOOGLE") - a separate
    // field rather than one popup reusing/mutating the other's text.
    string CredentialsHeaderText,
    IBrush Backdrop,
    IBrush CardBackground,
    IBrush CardBorder,
    double CardCornerRadius,
    // The dimmed backdrop is clipped to match the active skin's own panel
    // shape (see RoundedRectClip) - without this a rounded skin (ARC/AURA)
    // shows a jarring square backdrop poking past its rounded corners.
    double PanelCornerRadius,
    IBrush HeaderBrush,
    IBrush BodyBrush,
    // Secondary/quieter copy - CredentialsPopup's "get these from Google
    // Cloud Console..." help text and its idle-state status line.
    IBrush MutedBrush,
    // A connect failure (bad Client ID/Secret, browser consent closed
    // without approving) - reuses each skin's existing "needs your
    // attention" warn/pending color rather than introducing a dedicated
    // red, keeping one consistent "amber/warn = attention" language across
    // Gate 2 pending rows, this popup's header, and connect errors alike.
    IBrush ErrorBrush,
    FontFamily Font,
    IBrush ConfirmBackground,
    IBrush ConfirmForeground,
    IBrush ConfirmBorder,
    IBrush CancelBackground,
    IBrush CancelForeground,
    IBrush CancelBorder,
    IBrush InputBackground,
    IBrush InputBorder,
    IBrush InputForeground,
    IBrush InputCaret,
    double BorderThickness,
    double ButtonCornerRadius,
    // WEB's ".pixel-btn" hard offset-shadow bevel (see FlatButtonStyle) -
    // null for ARC/AURA's flat look.
    Color? PixelShadowColor);
