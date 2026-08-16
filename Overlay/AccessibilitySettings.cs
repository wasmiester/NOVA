using System.Runtime.InteropServices;

namespace Nova;

// Windows' nearest equivalent to CSS's prefers-reduced-motion is the "Show
// animations in Windows" setting (Settings > Accessibility > Visual
// effects). Read directly via SystemParametersInfo rather than a UI
// framework's own wrapper (WPF's SystemParameters.ClientAreaAnimation, which
// this used before) - keeps this file framework-agnostic across the
// WPF -> Avalonia move.
internal static class AccessibilitySettings
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    public static bool PrefersReducedMotion
    {
        get
        {
            bool enabled = true;
            SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0);
            return !enabled;
        }
    }
}
