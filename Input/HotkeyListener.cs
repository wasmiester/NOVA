using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Nova;

// A global hotkey that, when pressed, has Nova announce she's listening
// (see NovaAssistant.TriggerReadyAcknowledgment) so a request can start on
// demand instead of needing an existing conversation - and, since there's
// no spoken wake phrase anymore, this is also the only way to wake her from
// asleep. RegisterHotKey needs a real window message pump, which a console
// app doesn't have by default, so this runs its own dedicated STA thread
// with a hidden message-only window just to receive WM_HOTKEY.
internal sealed class HotkeyListener : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 1;

    // Ctrl+Alt+Space.
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkSpace = 0x20;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed class HotkeyWindow : NativeWindow
    {
        public event Action? Pressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
            {
                Pressed?.Invoke();
            }

            base.WndProc(ref m);
        }
    }

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private HotkeyWindow? _window;

    // False if Ctrl+Alt+Space is already claimed by another app (Discord,
    // an NVIDIA overlay, etc. commonly grab combos like this) -
    // RegisterHotKey fails silently otherwise, so this is the only signal.
    public bool Registered { get; private set; }

    public HotkeyListener(Action onPressed)
    {
        _thread = new Thread(() => RunMessageLoop(onPressed)) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    private void RunMessageLoop(Action onPressed)
    {
        _window = new HotkeyWindow();
        _window.Pressed += onPressed;
        Registered = RegisterHotKey(_window.Handle, HotkeyId, ModControl | ModAlt, VkSpace);
        _ready.Set();
        Application.Run();
    }

    public void Dispose()
    {
        if (_window is not null)
        {
            UnregisterHotKey(_window.Handle, HotkeyId);
        }
    }
}
