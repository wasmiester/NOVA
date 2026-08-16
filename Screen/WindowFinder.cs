using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Nova;

// Shared top-level window enumeration/resolution - used by both
// ScreenReader (read_screen's window_title) and DesktopInteraction
// (interact_desktop's window_title) so "find the window whose title
// matches this text" has exactly one implementation.
internal static class WindowFinder
{
    public static List<(IntPtr Handle, string Title)> EnumerateTopLevelWindows()
    {
        var windows = new List<(IntPtr, string)>();
        EnumWindows(
            (hwnd, _) =>
            {
                // IsWindowVisible stays true for a minimized window - only
                // an actually hidden/destroyed one returns false - so
                // minimized windows are deliberately still offered as
                // targets here.
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                int length = GetWindowTextLength(hwnd);
                if (length == 0)
                {
                    return true; // untitled windows are almost always background/tool windows, not anything worth offering as a target
                }

                var sb = new StringBuilder(length + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                windows.Add((hwnd, sb.ToString()));
                return true;
            },
            IntPtr.Zero);

        return windows;
    }

    // Matched as a case-insensitive substring, same convention as
    // BrowserController's tab_hint. Unmatched or ambiguous returns false
    // with the full list of open titles in `error` instead of guessing, so
    // a follow-up call can be exact.
    public static bool TryResolve(string windowTitle, out IntPtr hwnd, out string? error)
    {
        List<(IntPtr Handle, string Title)> windows = EnumerateTopLevelWindows();
        List<(IntPtr Handle, string Title)> matches = windows.FindAll(w => w.Title.Contains(windowTitle, StringComparison.OrdinalIgnoreCase));

        if (matches.Count == 1)
        {
            hwnd = matches[0].Handle;
            error = null;
            return true;
        }

        hwnd = IntPtr.Zero;
        string available = windows.Count == 0 ? "(none found)" : string.Join(", ", windows.ConvertAll(w => $"\"{w.Title}\""));
        error = matches.Count == 0
            ? $"No open window matching \"{windowTitle}\" - open windows: {available}"
            : $"Multiple open windows match \"{windowTitle}\" - be more specific: {string.Join(", ", matches.ConvertAll(w => $"\"{w.Title}\""))}";
        return false;
    }

    // Restores (if minimized) and brings the window to the foreground -
    // only meant to be called from a simulated-input fallback path (real
    // mouse clicks/keystrokes need actual OS focus and on-screen
    // coordinates, unlike UI Automation's Invoke/Value patterns), not as a
    // default step for every interaction.
    public static void BringToForeground(IntPtr hwnd)
    {
        const int swRestore = 9;
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, swRestore);
        }

        SetForegroundWindow(hwnd);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
