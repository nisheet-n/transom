using System.Runtime.InteropServices;

namespace Transom.Native;

internal static partial class Win32
{
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETTASKBARPOS = 5;
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    public const nint ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    public const uint WM_DESTROY = 0x0002, WM_SETTINGCHANGE = 0x001A, WM_GETMINMAXINFO = 0x0024, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0;
    public const int GWLP_WNDPROC = -4;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWA_CLOAKED = 14;
    public const int DWMWCP_DONOTROUND = 1, DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    public const int GWL_STYLE = -16;
    public const int WS_THICKFRAME = 0x00040000, WS_DLGFRAME = 0x00400000, WS_BORDER = 0x00800000;
    public const uint WM_CLIPBOARDUPDATE = 0x031D, WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    public const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_V = 0x56;
    public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;
    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001, ES_DISPLAY_REQUIRED = 0x00000002;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("shell32.dll")]
    public static partial nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string lpString);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromRect(ref RECT lprc, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);


    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    public static partial nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static partial int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    public static unsafe partial nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hWinEventHook);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    public static unsafe partial int GetClassName(nint hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("kernel32.dll")]
    public static partial uint SetThreadExecutionState(uint esFlags);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll")]
    public static unsafe partial uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    public static unsafe void SendCtrlV()
    {
        var keys = new[] { (VK_CONTROL, 0u), (VK_V, 0u), (VK_V, KEYEVENTF_KEYUP), (VK_CONTROL, KEYEVENTF_KEYUP) };
        var inputs = stackalloc INPUT[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = keys[i].Item1, dwFlags = keys[i].Item2 } } };
        }
        SendInput((uint)keys.Length, inputs, sizeof(INPUT));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, ref int pvAttribute, int cbAttribute);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref int pvAttribute, int cbAttribute);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    public static unsafe partial nint SetWindowsHookEx(int idHook, delegate* unmanaged<int, nint, nint, nint> lpfn, nint hmod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static unsafe partial int GetMessage(void* lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? lpModuleName);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [LibraryImport("oleaut32.dll")]
    public static partial void SysFreeString(nint bstrString);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    public static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll")]
    public static partial nint GetClipboardData(uint uFormat);

    [LibraryImport("user32.dll")]
    public static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport("user32.dll")]
    public static partial uint GetDoubleClickTime();

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll")]
    public static partial nuint GlobalSize(nint hMem);

    public static unsafe void SendCtrlC()
    {
        var keys = new[] { (VK_CONTROL, 0u), ((ushort)0x43, 0u), ((ushort)0x43, KEYEVENTF_KEYUP), (VK_CONTROL, KEYEVENTF_KEYUP) };
        var inputs = stackalloc INPUT[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = keys[i].Item1, dwFlags = keys[i].Item2 } } };
        }
        SendInput((uint)keys.Length, inputs, sizeof(INPUT));
    }

    public static nint SetWindowProc(nint hWnd, nint proc) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, GWLP_WNDPROC, proc) : SetWindowLong(hWnd, GWLP_WNDPROC, (int)proc);

    public static void RemoveFrame(nint hWnd)
    {
        var style = GetWindowLong(hWnd, GWL_STYLE) & ~(WS_THICKFRAME | WS_DLGFRAME | WS_BORDER);
        SetWindowLong(hWnd, GWL_STYLE, style);
        SetWindowPos(hWnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        var corners = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        var border = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }
}
