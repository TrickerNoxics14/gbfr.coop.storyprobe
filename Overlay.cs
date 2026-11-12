using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace gbfr.coop.storyprobe;

/// <summary>Where the status lines are drawn. The game itself is never touched.</summary>
public interface IStatusSurface
{
    void Show(string[] lines);
    void Hide();
}

/// <summary>
/// A small text box at the top of the game window, for the few messages a
/// player needs while the game itself shows only a black screen ("waiting for
/// player 2 to pick the same part", "your saves differ"). It is a separate
/// operating-system window, not part of the game's drawing: it never takes
/// focus, never catches clicks or keys, is hidden whenever the game window is
/// not the active window. In exclusive full screen other windows cannot be
/// drawn over the game, so it may not show there (use borderless or windowed).
/// </summary>
public sealed class Win32StatusSurface : IStatusSurface
{
    // Window styles. Every one of the "never get in the way" ones is required.
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOOLWINDOW = 0x00000080,
        WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000;
    public const uint ExStyle = WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const int WM_APP_SHOW = 0x8001, WM_APP_HIDE = 0x8002;
    const int WM_PAINT = 0x000F, WM_TIMER = 0x0113, WM_DESTROY = 0x0002, WM_ERASEBKGND = 0x0014;
    const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    const uint LWA_ALPHA = 2;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800, DT_WORDBREAK = 0x10, DT_LEFT = 0;

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    static readonly WndProc _proc = StaticProc;   // one delegate for the one window class, kept alive
    static Win32StatusSurface _current;
    static bool _registered;
    readonly object _lock = new();
    string[] _lines = Array.Empty<string>();
    IntPtr _hwnd; Thread _thread; volatile bool _failed;
    string _lastKey;                      // what is on screen now (text, place, size): unchanged means no redraw

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct PAINTSTRUCT { public IntPtr hdc; public bool fErase; public RECT rcPaint; public bool fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WNDCLASSEX { public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawText(IntPtr dc, string text, int len, ref RECT r, uint fmt);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFont(int h, int w, int esc, int ori, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    public void Show(string[] lines)
    {
        if (_failed) return;
        lock (_lock) { _lines = lines ?? Array.Empty<string>(); }
        EnsureThread();
        var h = _hwnd; if (h != IntPtr.Zero) PostMessage(h, WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
    }

    public void Hide()
    {
        lock (_lock) { _lines = Array.Empty<string>(); }
        var h = _hwnd; if (h != IntPtr.Zero) PostMessage(h, WM_APP_HIDE, IntPtr.Zero, IntPtr.Zero);
    }

    void EnsureThread()
    {
        if (_thread != null) return;
        lock (_lock)
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "storyprobe-status" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    void Run()
    {
        try
        {
            _current = this;
            var inst = GetModuleHandle(null);
            var cls = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = inst, lpszClassName = "storyprobe_status" };
            if (!_registered) { RegisterClassEx(ref cls); _registered = true; }
            var hwnd = CreateWindowEx(ExStyle, cls.lpszClassName, "", WS_POPUP, -32000, -32000, 10, 10, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { _failed = true; return; }
            SetLayeredWindowAttributes(hwnd, 0, 225, LWA_ALPHA);
            _hwnd = hwnd;
            SetTimer(hwnd, (IntPtr)1, 500, IntPtr.Zero);   // follows the game window, hides when it is not active
            PostMessage(hwnd, WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
        }
        catch { _failed = true; }
    }

    static IntPtr StaticProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l) =>
        _current != null ? _current.Proc(hwnd, msg, w, l) : DefWindowProc(hwnd, msg, w, l);

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            switch (msg)
            {
                case WM_APP_SHOW: case WM_TIMER: Layout(hwnd); return IntPtr.Zero;
                case WM_APP_HIDE: _lastKey = null; ShowWindow(hwnd, SW_HIDE); return IntPtr.Zero;
                case WM_ERASEBKGND: return (IntPtr)1;
                case WM_PAINT: Paint(hwnd); return IntPtr.Zero;
            }
        }
        catch { }
        return DefWindowProc(hwnd, msg, w, l);
    }

    static IntPtr MakeFont(IntPtr dc)
    {
        var dpi = GetDeviceCaps(dc, 90);   // LOGPIXELSY
        return CreateFont(-(int)Math.Round(19 * Math.Max(96, dpi) / 96.0), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
    }

    void Layout(IntPtr hwnd)
    {
        string[] lines; lock (_lock) lines = _lines;
        if (lines.Length == 0) { _lastKey = null; ShowWindow(hwnd, SW_HIDE); return; }
        // Only while a window of THIS process (the game) is the active one; never the overlay itself.
        var fg = GetForegroundWindow();
        uint pid = 0;
        if (fg == IntPtr.Zero || fg == hwnd || GetWindowThreadProcessId(fg, out pid) == 0 || pid != (uint)Environment.ProcessId || !GetWindowRect(fg, out var gr))
        { _lastKey = null; ShowWindow(hwnd, SW_HIDE); return; }
        var gw = gr.right - gr.left; if (gw < 200) { _lastKey = null; ShowWindow(hwnd, SW_HIDE); return; }
        var dc = GetDC(hwnd); var font = MakeFont(dc); var old = SelectObject(dc, font);
        var pad = 14; var maxW = Math.Max(240, (int)(gw * 0.6));
        var r = new RECT { left = 0, top = 0, right = maxW, bottom = 0 };
        DrawText(dc, string.Join("\n", lines), -1, ref r, DT_CALCRECT | DT_NOPREFIX | DT_WORDBREAK | DT_LEFT);
        SelectObject(dc, old); DeleteObject(font); ReleaseDC(hwnd, dc);
        var w = r.right - r.left + pad * 2; var h = r.bottom - r.top + pad * 2;
        var x = gr.left + (gw - w) / 2; var y = gr.top + 36;
        var key = string.Join("\n", lines) + "|" + x + "|" + y + "|" + w + "|" + h;
        if (key == _lastKey && IsWindowVisible(hwnd)) return;   // nothing changed: no redraw, no flicker
        _lastKey = key;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(hwnd, IntPtr.Zero, true);
    }

    void Paint(IntPtr hwnd)
    {
        var dc = BeginPaint(hwnd, out var ps);
        try
        {
            GetClientRect(hwnd, out var rc);
            var brush = CreateSolidBrush(0x00241B1A);   // dark
            FillRect(dc, ref rc, brush); DeleteObject(brush);
            string[] lines; lock (_lock) lines = _lines;
            var font = MakeFont(dc); var old = SelectObject(dc, font);
            SetBkMode(dc, 1); SetTextColor(dc, 0x00FFFFFF);
            var pad = 14; var t = new RECT { left = pad, top = pad, right = rc.right - pad, bottom = rc.bottom - pad };
            DrawText(dc, string.Join("\n", lines), -1, ref t, DT_NOPREFIX | DT_WORDBREAK | DT_LEFT);
            SelectObject(dc, old); DeleteObject(font);
        }
        finally { EndPaint(hwnd, ref ps); }
    }
}

public partial class Mod
{
    /// <summary>Creates the surface on first use. The audit replaces it so no window is ever made while testing.</summary>
    internal static Func<IStatusSurface> StatusSurfaceFactory = () => new Win32StatusSurface();

    internal const int StatusMaxLines = 3, StatusDefaultTtlMs = 65_000;

    private sealed class StatusEntry { public string Text; public long Until; }
    private readonly SortedDictionary<string, StatusEntry> _status = new(StringComparer.Ordinal);
    private readonly object _statusLock = new();
    private IStatusSurface _surface;
    private string[] _shownStatus = Array.Empty<string>();
    private volatile bool _statusOverlay = true;

    /// <summary>Puts (or replaces) one message on screen for up to <paramref name="ttlMs"/>; <paramref name="key"/> names its slot.</summary>
    internal void SetStatus(string key, string text, long ttlMs = StatusDefaultTtlMs)
    {
        lock (_statusLock) _status[key] = new StatusEntry { Text = text, Until = _clock.ElapsedMilliseconds + ttlMs };
    }

    internal void ClearStatus(params string[] keys)
    {
        lock (_statusLock) foreach (var k in keys) _status.Remove(k);
    }

    /// <summary>Pump: show what is current, hide when nothing is.</summary>
    private void PumpStatus(long now)
    {
        string[] lines;
        lock (_statusLock)
        {
            if (_status.Count == 0 && _shownStatus.Length == 0) return;   // nothing on screen, nothing wanted: no work
            foreach (var k in _status.Where(p => p.Value.Until <= now).Select(p => p.Key).ToList()) _status.Remove(k);
            lines = _statusOverlay && _relayActive ? _status.Values.Select(v => v.Text).Take(StatusMaxLines).ToArray() : Array.Empty<string>();
        }
        if (lines.SequenceEqual(_shownStatus)) return;
        _shownStatus = lines;
        if (lines.Length == 0) { _surface?.Hide(); return; }
        try { _surface ??= StatusSurfaceFactory(); _surface.Show(lines); }
        catch (Exception ex) { RelayError("status overlay", ex); _statusOverlay = false; }
    }
}
