using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace Nova;

// Scrolling and clicking/typing in a desktop app (not the browser, which
// has its own separate tools). Clicking shares the same narrow-scope guard
// as browser_click (see NavigationalClickGuard) - only navigational clicks
// (pagination, expand/collapse, show more, close/dismiss) are structurally
// allowed through, everything else is refused outright regardless of how
// it's asked for. Typing isn't restricted the same way - filling in a
// field's contents isn't the same risk as clicking an arbitrary button in
// an arbitrary app.
//
// Interact() prefers real UI Automation patterns (Invoke/Value) when a
// control supports them - no mouse movement, works on a background or
// minimized window exactly as well as the foreground one, more reliable
// than simulated input generally. Falls back to synthetic mouse clicks/
// keystrokes (FlaUI's Mouse/Keyboard, real OS-level input) for controls
// that don't expose them, which is common in Electron/web-rendered apps -
// that fallback needs real on-screen focus, so it's the only path that
// ever brings a target window forward (see WindowFinder.BringToForeground).
// Scroll() stays foreground-only - it moves the real cursor to a screen
// position, which only makes sense for whatever's actually on screen.
internal static class DesktopInteraction
{
    public static string Scroll(IReadOnlyDictionary<string, JsonElement> input)
    {
        string direction = ToolInput.GetString(input, "direction") ?? "down";
        double lines = input.TryGetValue("amount", out var amountElement) && amountElement.ValueKind == JsonValueKind.Number
            ? amountElement.GetDouble()
            : 3;

        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return "No foreground window found.";
        }

        using var automation = new UIA3Automation();
        AutomationElement window = automation.FromHandle(hwnd);

        // Scrolling is a real synthetic input event at the current cursor
        // position, not UI-Automation-targeted - move the cursor into the
        // foreground window first so it scrolls that window regardless of
        // where the mouse physically happens to be sitting.
        Point target = window.TryGetClickablePoint(out Point clickable) ? clickable : CenterOf(window.BoundingRectangle);
        Mouse.MoveTo(target);

        switch (direction.ToLowerInvariant())
        {
            case "up":
                Mouse.Scroll(lines);
                break;
            case "down":
                Mouse.Scroll(-lines);
                break;
            case "left":
                Mouse.HorizontalScroll(-lines);
                break;
            case "right":
                Mouse.HorizontalScroll(lines);
                break;
            default:
                return $"Unknown scroll direction \"{direction}\" - use up, down, left, or right.";
        }

        return $"Scrolled {direction}.";
    }

    public static string Interact(IReadOnlyDictionary<string, JsonElement> input)
    {
        string label = input["label"].GetString()!;
        string action = (ToolInput.GetString(input, "action") ?? "click").ToLowerInvariant();
        string? value = ToolInput.GetString(input, "value");
        string? windowTitle = ToolInput.GetString(input, "window_title");

        if (action == "click" && !NavigationalClickGuard.IsAllowed(label, out string? refusalReason))
        {
            return refusalReason!;
        }

        IntPtr hwnd;
        if (windowTitle is null)
        {
            hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return "No foreground window found.";
            }
        }
        else if (!WindowFinder.TryResolve(windowTitle, out hwnd, out string? resolveError))
        {
            return resolveError!;
        }

        using var automation = new UIA3Automation();
        AutomationElement window = automation.FromHandle(hwnd);

        AutomationElement? target = window.FindAllDescendants()
            .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Name) && e.Name.Contains(label, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return $"No control labeled \"{label}\" found in that window.";
        }

        return action switch
        {
            "type" => TypeInto(hwnd, target, label, value),
            "click" => Click(hwnd, target, label),
            _ => $"Unknown action \"{action}\" - use click or type.",
        };
    }

    private static string Click(IntPtr hwnd, AutomationElement target, string label)
    {
        if (target.Patterns.Invoke.IsSupported)
        {
            target.Patterns.Invoke.Pattern.Invoke();
            return $"Clicked \"{label}\".";
        }

        // No Invoke support - falling back to a real simulated mouse click,
        // which needs actual on-screen coordinates, so a minimized/
        // background window has to come forward first. Only this fallback
        // path ever touches the user's foreground window - the
        // pattern-based path above never does.
        WindowFinder.BringToForeground(hwnd);
        target.Click(moveMouse: true);
        return $"Clicked \"{label}\" (simulated mouse click - no direct invoke support).";
    }

    private static string TypeInto(IntPtr hwnd, AutomationElement target, string label, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "No value provided to type.";
        }

        if (target.Patterns.Value.IsSupported)
        {
            target.Patterns.Value.Pattern.SetValue(value);
            return $"Typed into \"{label}\".";
        }

        // No Value-pattern support - falling back to real simulated
        // keystrokes, which need actual OS keyboard focus, same reasoning
        // as Click's fallback above.
        WindowFinder.BringToForeground(hwnd);
        target.Focus();
        Keyboard.Type(value);
        return $"Typed into \"{label}\" (simulated keystrokes - no direct text pattern support).";
    }

    private static Point CenterOf(Rectangle rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
