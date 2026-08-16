using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Nova;

// Best-effort: only reads what the OS accessibility tree actually exposes.
// Native controls (buttons, menus, labels, simple text fields) come through
// well; custom-rendered content (a code editor's own text, canvas UIs) often
// doesn't, since it's not really "UI" from Windows' point of view.
internal static class ScreenReader
{
    // Chrome (and browsers generally) don't necessarily have a tab's full
    // accessibility tree ready the instant it becomes active - Chromium
    // builds it somewhat lazily, and switching tabs/navigating doesn't
    // change the foreground HWND at all (still the same Chrome window),
    // so there's no OS-level signal to wait on. Confirmed directly: right
    // after switching to a different tab, a read can come back showing
    // the *previous* tab's content even though the window title already
    // reflects the new one. Reading twice and comparing, rather than a
    // single blind read, is what actually catches this - a single fixed
    // delay would either be wasted most of the time or still not be long
    // enough for a heavy page like Google Docs.
    private const int SettleAttempts = 4;
    private const int SettleDelayMs = 300;

    // windowTitle is null for the common case (read whatever's in the
    // foreground - what the user is obviously looking at). Given, it's
    // matched against every open top-level window's title (not just the
    // foreground one, and not skipped for minimized windows - IsWindowVisible
    // stays true for a minimized window, only actually-hidden/destroyed ones
    // return false) so a background/minimized window can be read without
    // ever touching what's currently on screen.
    public static string ReadScreen(string? windowTitle = null)
    {
        IntPtr hwnd;
        if (windowTitle is null)
        {
            hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return "No foreground window found.";
            }
        }
        else if (!WindowFinder.TryResolve(windowTitle, out hwnd, out string? error))
        {
            return error!;
        }

        string current = ReadScreenOnce(hwnd);
        for (int attempt = 1; attempt < SettleAttempts; attempt++)
        {
            Thread.Sleep(SettleDelayMs);
            string next = ReadScreenOnce(hwnd);
            if (next == current)
            {
                return next; // two consecutive reads agree - settled
            }

            current = next;
        }

        // Never stabilized within the budget - still return the most
        // recent read rather than giving up, since it's the freshest
        // information available even if the tree might still be catching up.
        return current;
    }

    private static string ReadScreenOnce(IntPtr hwnd)
    {
        using var automation = new UIA3Automation();
        AutomationElement window = automation.FromHandle(hwnd);

        var sb = new StringBuilder();
        sb.AppendLine($"Window: {TryGetName(window) ?? "(untitled)"}");

        const int MaxElements = 200;
        const int MaxChars = 8000;
        int count = 0;
        foreach (AutomationElement element in window.FindAllDescendants())
        {
            // Individual elements in a UIA tree - especially in modern
            // WinUI/Chromium-based apps like the current Notepad - can
            // throw PropertyNotSupportedException on properties as basic
            // as Name, even though FindAllDescendants() itself succeeded
            // (confirmed directly: this is what "reading Notepad keeps
            // failing" actually was - one such element threw and took the
            // *entire* scan down with it, not just a gap in what came
            // through). Every property read for this element is now
            // inside one try/catch - a bad element gets skipped instead of
            // failing the whole read.
            try
            {
                // An edit control's typed content lives in ValuePattern
                // (or, for richer document-style controls, TextPattern) -
                // not Name, which is usually empty or just a generic label
                // like "Text Editor". Reading Name alone means the actual
                // content of any text box/document (Notepad's text, a
                // search field's query, etc.) was invisible - only chrome
                // like menu/button labels ever came through.
                string? name = TryGetName(element);
                string? value = TryGetValue(element);

                bool hasName = !string.IsNullOrWhiteSpace(name);
                bool hasValue = !string.IsNullOrWhiteSpace(value);
                if (!hasName && !hasValue)
                {
                    continue;
                }

                sb.AppendLine(hasValue && value != name
                    ? $"[{element.ControlType}] {name}: {value}"
                    : $"[{element.ControlType}] {name}");
                count++;
                if (count >= MaxElements)
                {
                    break;
                }
            }
            catch
            {
                // Unreadable element - skip it, not the whole scan.
            }
        }

        string result = sb.ToString();
        return result.Length > MaxChars ? result[..MaxChars] + "\n... [truncated]" : result;
    }

    private static string? TryGetName(AutomationElement element)
    {
        try
        {
            return element.Name;
        }
        catch
        {
            return null;
        }
    }

    // ValuePattern covers most plain text boxes/edit controls (including
    // classic Win32 Notepad); TextPattern is the fallback for
    // document-style controls that expose content that way instead (the
    // modernized WinUI Notepad among them). Either can be advertised as
    // supported but still throw on access for a given control, so both are
    // best-effort - a failure here just means this one element contributes
    // no extra content, not that the whole read fails.
    private static string? TryGetValue(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                return element.Patterns.Value.Pattern.Value.Value;
            }
        }
        catch
        {
        }

        try
        {
            if (element.Patterns.Text.IsSupported)
            {
                return element.Patterns.Text.Pattern.DocumentRange.GetText(2000);
            }
        }
        catch
        {
        }

        return null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
