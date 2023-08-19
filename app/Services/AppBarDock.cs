using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using static Transom.Native.Win32;

namespace Transom.Services;

internal sealed unsafe class AppBarDock : IDisposable
{
    private static AppBarDock? s_current;

    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DispatcherQueue _dispatcher;
    private readonly uint _callbackMessage;
    private nint _oldWndProc;
    private bool _registered;
    private bool _layoutQueued;

    public event Action<double>? ThicknessChanged;
    public event Action<bool>? FullScreenChanged;
    public event Action? SettingChanged;
    public event Action? ClipboardChanged;
    public event Action<int>? HotKeyPressed;

    public AppBarDock(nint hwnd, AppWindow appWindow)
    {
        _hwnd = hwnd;
        _appWindow = appWindow;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _callbackMessage = RegisterWindowMessage("Transom.AppBar");

        s_current = this;
        _oldWndProc = SetWindowProc(hwnd, (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc);

        var data = NewData();
        data.uCallbackMessage = _callbackMessage;
        _registered = SHAppBarMessage(ABM_NEW, ref data) != 0;
    }

    // The rail matches the taskbar's real thickness (it changes with display scaling and the
    // small-taskbar setting) and stops where the taskbar starts. Windows then shrinks the work
    // area so maximized windows sit next to the rail instead of underneath it.
    public void Layout()
    {
        if (!_registered) return;

        var taskbar = NewData();
        RECT area;
        int thickness;
        double scale;

        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref taskbar) != 0)
        {
            var tb = taskbar.rc;
            thickness = taskbar.uEdge is ABE_TOP or ABE_BOTTOM ? tb.Height : tb.Width;
            (area, scale) = MonitorOf(tb);
            switch (taskbar.uEdge)
            {
                case ABE_BOTTOM: area.Bottom = tb.Top; break;
                case ABE_TOP: area.Top = tb.Bottom; break;
                case ABE_LEFT: area.Left = tb.Right; break;
            }
        }
        else
        {
            (area, scale) = MonitorOf(default);
            thickness = (int)Math.Round(48 * scale);
        }

        var data = NewData();
        data.uEdge = ABE_LEFT;
        data.rc = new RECT { Left = area.Left, Top = area.Top, Right = area.Left + thickness, Bottom = area.Bottom };
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        data.rc.Right = data.rc.Left + thickness;
        SHAppBarMessage(ABM_SETPOS, ref data);

        _appWindow.MoveAndResize(new RectInt32(data.rc.Left, data.rc.Top, data.rc.Width, data.rc.Height));
        ThicknessChanged?.Invoke(thickness / scale);
    }

    public void Dispose()
    {
        if (_registered)
        {
            var data = NewData();
            SHAppBarMessage(ABM_REMOVE, ref data);
            _registered = false;
        }
        if (_oldWndProc != 0)
        {
            SetWindowProc(_hwnd, _oldWndProc);
            _oldWndProc = 0;
        }
        if (s_current == this) s_current = null;
    }

    private void QueueLayout()
    {
        if (_layoutQueued) return;
        _layoutQueued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _layoutQueued = false;
            Layout();
        });
    }

    private APPBARDATA NewData() => new() { cbSize = (uint)sizeof(APPBARDATA), hWnd = _hwnd };

    private static (RECT Bounds, double Scale) MonitorOf(RECT rect)
    {
        var monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        return (info.rcMonitor, scale);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var self = s_current;
        if (self is null) return 0;
        var oldProc = self._oldWndProc;

        try
        {
            if (msg == self._callbackMessage)
            {
                if (wParam == ABN_POSCHANGED) self.QueueLayout();
                else if (wParam == ABN_FULLSCREENAPP) self.FullScreenChanged?.Invoke(lParam != 0);
                return 0;
            }
            if (msg is WM_SETTINGCHANGE or WM_DISPLAYCHANGE or WM_DPICHANGED) self.QueueLayout();
            if (msg == WM_SETTINGCHANGE) self._dispatcher.TryEnqueue(() => self.SettingChanged?.Invoke());
            if (msg == WM_CLIPBOARDUPDATE) self._dispatcher.TryEnqueue(() => self.ClipboardChanged?.Invoke());
            if (msg == WM_HOTKEY)
            {
                var id = (int)wParam;
                self._dispatcher.TryEnqueue(() => self.HotKeyPressed?.Invoke(id));
                return 0;
            }
            if (msg == WM_DESTROY) self.Dispose();
            if (msg == WM_GETMINMAXINFO)
            {
                var result = CallWindowProc(oldProc, hwnd, msg, wParam, lParam);
                ((MINMAXINFO*)lParam)->ptMinTrackSize = new POINT { X = 1, Y = 1 };
                return result;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return CallWindowProc(oldProc, hwnd, msg, wParam, lParam);
    }
}
