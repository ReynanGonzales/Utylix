using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace IdmClone;

/// <summary>
/// Global shortcuts that start a screen capture from anywhere:
///  - Ctrl+Alt+S: an ordinary registered hotkey.
///  - Win+S: Windows keeps this one for Search, so a normal hotkey can't be claimed. While this is on, a keyboard hook
///    watches for Win+S, swallows it (so Search doesn't open) and starts the capture instead. It only ever reacts to
///    that one combination and never records or stores keys. Programs running as administrator don't pass keys to it.
/// </summary>
public sealed class Shortcuts : IDisposable
{
    private const int WM_HOTKEY = 0x0312, HotkeyId = 0x5554, RecordHotkeyId = 0x5555, PauseHotkeyId = 0x5556;
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_F = 0x46, VK_P = 0x50, VK_R = 0x52, VK_S = 0x53, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }

    private readonly HwndSource _window;                 // hidden window that receives the hotkey message
    private readonly HookProc _hookProc;                 // kept in a field so it is not collected while hooked
    private IntPtr _hook;
    private bool _swallowedS, _swallowedF;
    private bool _winSOn, _winFOn;
    private bool _ctrlAltRegistered, _recordRegistered, _pauseRegistered;

    /// <summary>Raised on the UI thread when a capture shortcut was pressed.</summary>
    public event Action? Pressed;

    /// <summary>Raised on the UI thread when the recording shortcut (Ctrl + Alt + R) was pressed.</summary>
    public event Action? RecordPressed;

    /// <summary>Raised on the UI thread when the pause shortcut (Ctrl + Alt + P) was pressed.</summary>
    public event Action? PausePressed;

    /// <summary>Raised on the UI thread when Win + F was pressed (bring Utylix up).</summary>
    public event Action? OpenPressed;

    public Shortcuts()
    {
        _window = new HwndSource(new HwndSourceParameters("UtylixShortcuts") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });   // message-only
        _window.AddHook(WndProc);
        _hookProc = Hook;
    }

    /// <summary>Turn the shortcuts on or off. Returns false for a shortcut Windows did not let us have.</summary>
    public (bool CtrlAltS, bool WinS, bool CtrlAltR, bool CtrlAltP) Apply(bool ctrlAltS, bool winS, bool ctrlAltR, bool winF = false)
    {
        _winSOn = winS; _winFOn = winF;
        if (_pauseRegistered) { UnregisterHotKey(_window.Handle, PauseHotkeyId); _pauseRegistered = false; }
        bool ok4 = true;
        if (ctrlAltR)                                       // the pause key comes with the recording shortcut
        {
            _pauseRegistered = RegisterHotKey(_window.Handle, PauseHotkeyId, 0x0001 | 0x0002 | 0x4000, VK_P);
            ok4 = _pauseRegistered;
        }
        if (_recordRegistered) { UnregisterHotKey(_window.Handle, RecordHotkeyId); _recordRegistered = false; }
        bool ok3 = true;
        if (ctrlAltR)
        {
            _recordRegistered = RegisterHotKey(_window.Handle, RecordHotkeyId, 0x0001 | 0x0002 | 0x4000, VK_R);
            ok3 = _recordRegistered;
        }

        if (_ctrlAltRegistered) { UnregisterHotKey(_window.Handle, HotkeyId); _ctrlAltRegistered = false; }
        bool ok1 = true;
        if (ctrlAltS)
        {
            _ctrlAltRegistered = RegisterHotKey(_window.Handle, HotkeyId, 0x0001 | 0x0002 | 0x4000, VK_S);   // Alt + Ctrl, no auto-repeat
            ok1 = _ctrlAltRegistered;
        }

        if (_hook != IntPtr.Zero && !winS && !winF) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        bool ok2 = true;
        if ((winS || winF) && _hook == IntPtr.Zero)
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
            ok2 = _hook != IntPtr.Zero;
        }
        return (ok1, ok2, ok3, ok4);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == RecordHotkeyId)
        {
            handled = true;
            RecordPressed?.Invoke();
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == PauseHotkeyId)
        {
            handled = true;
            PausePressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (key.vkCode == VK_F && (_winFOn || _swallowedF))
            {
                if (_winFOn && (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && (Down(VK_LWIN) || Down(VK_RWIN)) && !Down(VK_SHIFT) && !Down(VK_CONTROL) && !Down(VK_MENU))
                {
                    _swallowedF = true;
                    MaskWindowsKey();
                    _window.Dispatcher.BeginInvoke(new Action(() => OpenPressed?.Invoke()));
                    return (IntPtr)1;                                                        // Windows' Feedback Hub never sees it
                }
                if ((msg == WM_KEYUP || msg == WM_SYSKEYUP) && _swallowedF)
                {
                    _swallowedF = false;
                    return (IntPtr)1;
                }
            }
            if (key.vkCode == VK_S && (_winSOn || _swallowedS))
            {
                if (_winSOn && (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && (Down(VK_LWIN) || Down(VK_RWIN)) && !Down(VK_SHIFT) && !Down(VK_CONTROL) && !Down(VK_MENU))
                {
                    _swallowedS = true;
                    MaskWindowsKey();
                    _window.Dispatcher.BeginInvoke(new Action(() => Pressed?.Invoke()));     // keep the hook fast: do the work later
                    return (IntPtr)1;                                                        // Windows Search never sees it
                }
                if ((msg == WM_KEYUP || msg == WM_SYSKEYUP) && _swallowedS)
                {
                    _swallowedS = false;
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>
    /// A Win key press with nothing else opens the Start menu when released. Since we swallowed the S, press an
    /// unassigned key so Windows sees "Win + something" and leaves the Start menu closed.
    /// </summary>
    private static void MaskWindowsKey()
    {
        var down = new INPUT { type = 1, ki = new KEYBDINPUT { vk = 0x07 } };
        var up = new INPUT { type = 1, ki = new KEYBDINPUT { vk = 0x07, flags = 0x0002 } };
        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_ctrlAltRegistered) UnregisterHotKey(_window.Handle, HotkeyId);
        if (_recordRegistered) UnregisterHotKey(_window.Handle, RecordHotkeyId);
        if (_pauseRegistered) UnregisterHotKey(_window.Handle, PauseHotkeyId);
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _window.Dispose();
    }
}
