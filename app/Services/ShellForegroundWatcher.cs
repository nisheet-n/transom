using System.Diagnostics;
using System.Runtime.InteropServices;
using static Transom.Native.Win32;

namespace Transom.Services;

internal sealed unsafe class ShellForegroundWatcher : IDisposable
{
    private static readonly HashSet<string> ShellProcesses =
        ["StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost", "ShellHost", "TextInputHost"];

    private static ShellForegroundWatcher? s_current;
    private nint _hook;

    public event Action<nint>? ForegroundChanged;

    public ShellForegroundWatcher()
    {
        s_current = this;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, &OnForegroundChanged, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
            _hook = 0;
        }
        if (s_current == this) s_current = null;
    }

    public static bool IsShellWindow(nint hwnd)
    {
        var buffer = stackalloc char[64];
        var length = GetClassName(hwnd, buffer, 64);
        var className = new string(buffer, 0, length);
        if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return true;

        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return ShellProcesses.Contains(process.ProcessName);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool CoversWholeMonitor(nint hwnd)
    {
        if (hwnd == 0 || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var window)) return false;
        var cloaked = 0;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, ref cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var monitor = MonitorFromRect(ref window, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        var m = info.rcMonitor;
        return window.Left <= m.Left && window.Top <= m.Top && window.Right >= m.Right && window.Bottom >= m.Bottom;
    }

    [UnmanagedCallersOnly]
    private static void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            s_current?.ForegroundChanged?.Invoke(hwnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
