using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nova;

// AFK auto-sleep: after 3 hours of no system-wide mouse/keyboard input,
// puts Nova to sleep the same way the spoken sleep phrase or the overlay's
// sleep button do (NovaAssistant.SetEngaged(false)). Polls GetLastInputInfo
// on its own timer rather than hooking input events - simplest correct way
// to answer "how long has it been" without a global input hook, and cheap
// enough to check once a minute.
internal sealed class IdleTracker : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AfkThreshold = TimeSpan.FromHours(3);

    private readonly Timer _timer;

    public IdleTracker(Func<bool> isEngaged, Action goToSleep)
    {
        _timer = new Timer(
            _ =>
            {
                if (isEngaged() && GetIdleTime() >= AfkThreshold)
                {
                    goToSleep();
                }
            },
            null,
            PollInterval,
            PollInterval);
    }

    private static TimeSpan GetIdleTime()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Both tick counts wrap around at ~49.7 days; unchecked subtraction
        // of two uints still yields the correct elapsed duration across a
        // wraparound, it's only a literal "later minus earlier" comparison
        // that would break.
        uint idleTicks = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleTicks);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    public void Dispose() => _timer.Dispose();
}
