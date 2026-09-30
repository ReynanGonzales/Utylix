using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IdmClone;

/// <summary>
/// A plain window inside the player where the video is drawn (libvlc is given its handle). It also reports the mouse:
/// click, double click, right click, movement and the wheel.
/// </summary>
public sealed class VlcHost : HwndHost
{
    private const string ClassName = "UtylixVideoSurface";
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    private const uint CS_DBLCLKS = 0x0008;
    private const int WM_ERASEBKGND = 0x0014, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_MOUSEMOVE = 0x0200, WM_MOUSEWHEEL = 0x020A, WM_SETCURSOR = 0x0020;

    private delegate IntPtr SurfaceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public SurfaceWndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private static readonly SurfaceWndProc Proc = SurfaceProc;                 // kept alive for as long as the program runs
    private static readonly Dictionary<IntPtr, VlcHost> Hosts = new();
    private static bool _registered;
    private IntPtr _hwnd;
    private bool _cursorHidden;

    public event Action? Clicked, DoubleClicked, RightClicked, Moved;
    public event Action<int>? Wheel;                                   // + up, - down

    /// <summary>The handle libvlc draws into.</summary>
    public IntPtr Surface => _hwnd;

    /// <summary>Hide the mouse pointer over the picture (full screen, while nothing moves).</summary>
    public bool CursorHidden
    {
        get => _cursorHidden;
        set { _cursorHidden = value; if (_hwnd != IntPtr.Zero) SetCursor(value ? IntPtr.Zero : LoadCursor(IntPtr.Zero, new IntPtr(32512))); }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        IntPtr instance = GetModuleHandle(null);
        if (!_registered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), style = CS_DBLCLKS, lpfnWndProc = Proc, hInstance = instance,
                hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)), hbrBackground = GetStockObject(4), lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
            _registered = true;
        }
        _hwnd = CreateWindowEx(0, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS, 0, 0, 16, 16, hwndParent.Handle, IntPtr.Zero, instance, IntPtr.Zero);
        lock (Hosts) Hosts[_hwnd] = this;
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        lock (Hosts) Hosts.Remove(hwnd.Handle);
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    private static IntPtr SurfaceProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        VlcHost? host;
        lock (Hosts) Hosts.TryGetValue(hwnd, out host);
        switch (msg)
        {
            case WM_ERASEBKGND:
                GetClientRect(hwnd, out var r);
                FillRect(wParam, ref r, GetStockObject(4));                       // black
                return new IntPtr(1);
            case WM_LBUTTONDOWN: host?.Clicked?.Invoke(); return IntPtr.Zero;
            case WM_LBUTTONDBLCLK: host?.DoubleClicked?.Invoke(); return IntPtr.Zero;
            case WM_RBUTTONUP: host?.RightClicked?.Invoke(); return IntPtr.Zero;
            case WM_MOUSEMOVE: host?.Moved?.Invoke(); return IntPtr.Zero;
            case WM_MOUSEWHEEL: host?.Wheel?.Invoke((short)((long)wParam >> 16) > 0 ? 1 : -1); return IntPtr.Zero;
            case WM_SETCURSOR when host is { _cursorHidden: true }:
                SetCursor(IntPtr.Zero);
                return new IntPtr(1);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
