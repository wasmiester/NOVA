using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nova;

// Best-effort "bring this window to the front and maximize it." A
// background process like Nova can't just call SetForegroundWindow and
// have Windows honor it - foreground-lock prevention makes a newly opened
// window (Explorer, Chrome) just flash in the taskbar instead. The
// AttachThreadInput trick below is the standard workaround: temporarily
// share input state with the foreground thread so the activation request
// looks like it's coming from an already-focused context.
internal static class WindowActivator
{
    private const int SwRestore = 9;
    private const int SwMaximize = 3;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public static void BringToFrontMaximized(IntPtr hWnd) => BringToFront(hWnd, maximize: true);

    // Restores a minimized window and brings it to the foreground without
    // forcibly maximizing it - for callers (like WindowFinder's simulated-
    // input click/keystroke fallback) that just need real OS focus at the
    // window's current size/position, not a layout change. Shares the same
    // AttachThreadInput workaround as BringToFrontMaximized - confirmed
    // live as a real gap: a separate, weaker implementation elsewhere
    // (plain SetForegroundWindow, no thread-attach) silently did nothing on
    // a background process, exactly the failure this file's own doc
    // comment already explains.
    public static void BringToFront(IntPtr hWnd) => BringToFront(hWnd, maximize: false);

    private static void BringToFront(IntPtr hWnd, bool maximize)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        // GetWindowThreadProcessId's *return value* is the thread ID (what
        // AttachThreadInput needs) - the out-param is the process ID, a
        // different thing, only used for the window-ownership matching in
        // SnapshotTopLevelWindows below.
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint targetThread = GetWindowThreadProcessId(hWnd, out _);
        uint currentThread = GetCurrentThreadId();

        bool attached = foregroundThread != targetThread && foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, SwRestore);
            }

            if (maximize)
            {
                ShowWindow(hWnd, SwMaximize);
            }

            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    // Explorer and Chrome both defeat "track the launched process's
    // MainWindowHandle": Chrome's launcher process exits within ~3s having
    // never owned a window (re-execs into a separate browser process,
    // different PID), and Explorer usually opens a new folder window
    // *inside* the already-running shell process instead of a new one.
    // This snapshots existing top-level windows for the process name
    // before launch, then polls for whichever new one appears - agnostic
    // to whether it's a new PID or a new window on an existing one. Call
    // immediately before starting the process expected to open the window.
    public static void WatchAndActivateNewWindow(string processName, Action<IntPtr>? onFound = null, int timeoutMs = 6000)
    {
        HashSet<IntPtr> before = SnapshotTopLevelWindows(processName);
        var thread = new Thread(() =>
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                foreach (IntPtr hWnd in SnapshotTopLevelWindows(processName))
                {
                    if (!before.Contains(hWnd))
                    {
                        BringToFrontMaximized(hWnd);
                        onFound?.Invoke(hWnd);
                        return;
                    }
                }

                Thread.Sleep(200);
            }
        })
        { IsBackground = true };
        thread.Start();
    }

    private static HashSet<IntPtr> SnapshotTopLevelWindows(string processName)
    {
        var handles = new HashSet<IntPtr>();
        var pids = new HashSet<int>(Process.GetProcessesByName(processName).Select(p => p.Id));
        if (pids.Count == 0)
        {
            return handles;
        }

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out int pid);
            if (pids.Contains(pid) && IsWindowVisible(hWnd))
            {
                handles.Add(hWnd);
            }

            return true;
        }, IntPtr.Zero);
        return handles;
    }
}
