using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Transom.Native;

using static Transom.Native.Com;

namespace Transom.Services;

// Reads the text selected in whatever app has focus, through UI Automation (the same API screen
// readers use), after each mouse release or Shift / Ctrl+A key release. Hooks live on their own
// thread and reads run on the thread pool, so a slow or hung app never delays mouse or keyboard input.
public sealed unsafe class SelectionWatcher : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const nint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const uint WM_QUIT = 0x0012, COINIT_MULTITHREADED = 0, CLSCTX_INPROC_SERVER = 1;
    private const int UIA_TextPatternId = 10014, MaxText = 100_000;
    private const string HiddenTextEditor = "native-edit-context", TerminalInput = "xterm-helper-textarea";

    private static readonly HashSet<string> NoCopyWindows =
    [
        "CabinetWClass", "ExploreWClass", "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow",
        "mintty", "VirtualConsoleClass", "PuTTY", "XLMAIN", "TscShellContainerClass",
    ];

    private static readonly Guid CLSID_CUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid IID_IUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
    private static readonly Guid IID_IUIAutomationTextPattern = new("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A");

    private static SelectionWatcher? _instance;
    private static int _downX, _downY;
    private static uint _lastUpTime;
    private static bool _multiClick, _shiftSelecting;
    private static long _borrowUntil;
    private static (string Text, DateTimeOffset Since)? _borrowed;

    private static readonly string[] PasswordManagers = ["KeePass", "1Password", "Bitwarden", "LastPass", "Dashlane", "Enpass", "NordPass", "ProtonPass", "RoboForm", "Keeper"];
    private int _gesture;
    private bool _emptyPattern, _retried;

    private readonly DispatcherQueue _dispatcher;
    private readonly Timer _timer;
    private Thread? _thread;
    private uint _threadId;
    private nint _automation;
    private int _reading;
    private string? _last;

    public event Action<string?>? Changed;

    public SelectionWatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new Timer(_ => Read());
    }

    public bool Enabled
    {
        get => _thread is not null;
        set
        {
            if (value == Enabled) return;
            if (value) Start();
            else Stop();
        }
    }

    public string? Current => _last;

    public nint Source { get; private set; }

    public void Clear() => Publish(null);

    private void Start()
    {
        _instance = this;
        _thread = new Thread(HookLoop) { IsBackground = true, Name = "Transom selection hooks" };
        _thread.Start();
    }

    private void Stop()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        _thread = null;
        _threadId = 0;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        Publish(null);
    }

    private void HookLoop()
    {
        _threadId = Win32.GetCurrentThreadId();
        var module = Win32.GetModuleHandle(null);
        var mouse = Win32.SetWindowsHookEx(WH_MOUSE_LL, &MouseProc, module, 0);
        var keyboard = Win32.SetWindowsHookEx(WH_KEYBOARD_LL, &KeyboardProc, module, 0);
        var msg = stackalloc byte[64];
        while (Win32.GetMessage(msg, 0, 0, 0) > 0) { }
        Win32.UnhookWindowsHookEx(mouse);
        Win32.UnhookWindowsHookEx(keyboard);
    }

    // A drag, a double/triple click, Shift+arrows or Ctrl+A almost always selects something;
    // a plain click usually clears the selection instead.
    [UnmanagedCallersOnly]
    private static nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && (wParam == WM_LBUTTONDOWN || wParam == WM_LBUTTONUP))
        {
            var info = (int*)lParam;
            var time = (uint)info[4];
            if (wParam == WM_LBUTTONDOWN)
            {
                _downX = info[0];
                _downY = info[1];
                _multiClick = time - _lastUpTime <= Win32.GetDoubleClickTime();
            }
            else
            {
                _lastUpTime = time;
                _instance?.Schedule(_multiClick || Math.Abs(info[0] - _downX) > 4 || Math.Abs(info[1] - _downY) > 4);
            }
        }
        return Win32.CallNextHookEx(0, code, wParam, lParam);
    }

    // Shift on its own is just a capital letter; only Shift with arrows, Home/End or Page keys selects.
    [UnmanagedCallersOnly]
    private static nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var vk = *(uint*)lParam;
            if (wParam == WM_KEYDOWN && vk is >= 0x21 and <= 0x28 && IsDown(Win32.VK_SHIFT)) _shiftSelecting = true;
            if (wParam == WM_KEYUP)
            {
                if (vk is 0x10 or 0xA0 or 0xA1 && _shiftSelecting)
                {
                    _shiftSelecting = false;
                    _instance?.Schedule(true);
                }
                else if (vk == 0x41 && IsDown(Win32.VK_CONTROL))
                {
                    _instance?.Schedule(true);
                }
            }
        }
        return Win32.CallNextHookEx(0, code, wParam, lParam);
    }

    private static bool IsDown(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void Schedule(bool gesture)
    {
        _retried = false;
        Volatile.Write(ref _gesture, gesture ? 1 : 0);
        _timer.Change(180, Timeout.Infinite);
    }

    public static bool IsBorrowingClipboard => Environment.TickCount64 < Volatile.Read(ref _borrowUntil);

    private void Read()
    {
        if (Interlocked.Exchange(ref _reading, 1) == 1) return;
        try
        {
            var foreground = Win32.GetForegroundWindow();
            Win32.GetWindowThreadProcessId(foreground, out var pid);
            if (foreground == 0 || pid == Environment.ProcessId) return;
            var gesture = Interlocked.Exchange(ref _gesture, 0) == 1;
            _emptyPattern = false;
            var text = ReadSelection(foreground, gesture);

            // Chromium-based apps update their accessibility tree a moment after a double-click,
            // so an empty answer right after a selection gesture gets one more look.
            if (text is null && gesture && _emptyPattern && !_retried)
            {
                _retried = true;
                Volatile.Write(ref _gesture, 1);
                _timer.Change(400, Timeout.Infinite);
                return;
            }
            _retried = false;
            if (text is not null) Source = foreground;
            if (_borrowed is { } borrowed)
            {
                _borrowed = null;
                _dispatcher.TryEnqueue(() => _ = ClipboardHistoryCleanup.ForgetAsync(borrowed.Text, borrowed.Since));
            }
            Publish(text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    private void Publish(string? text)
    {
        if (text == _last) return;
        _last = text;
        _dispatcher.TryEnqueue(() => Changed?.Invoke(text));
    }

    // Raw vtable calls keep this trimming/AOT friendly. Slot numbers are the method's position
    // in the UIAutomationClient.h interface, plus 3 for IUnknown. Web views often focus a
    // container, so the walk goes up to the nearest element that actually holds text.
    private string? ReadSelection(nint foreground, bool gesture)
    {
        Win32.CoInitializeEx(0, COINIT_MULTITHREADED);
        if (_automation == 0 && Win32.CoCreateInstance(CLSID_CUIAutomation, 0, CLSCTX_INPROC_SERVER, IID_IUIAutomation, out _automation) < 0) return null;

        nint element = 0, walker = 0;
        try
        {
            if (Call(_automation, 8, &element) < 0 || element == 0) return null;

            int isPassword;
            if (Call(element, 35, &isPassword) >= 0 && isPassword != 0) return null;

            var focusedClass = ClassName(element);
            if (focusedClass == HiddenTextEditor) return gesture ? BorrowCopy(foreground) : null;
            if (focusedClass == TerminalInput) return null;

            Call(_automation, 16, &walker);
            var getParent = walker == 0 ? null : (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, 3);
            for (var depth = 0; depth < 12 && element != 0; depth++)
            {
                if (TrySelectedText(element, out var text))
                {
                    _emptyPattern = text is null;
                    return text;
                }
                if (getParent is null) break;
                nint parent = 0;
                getParent(walker, element, &parent);
                Release(element);
                element = parent;
            }
            return gesture && CanBorrow(foreground) ? BorrowCopy(foreground) : null;
        }
        finally
        {
            Release(element);
            Release(walker);
        }
    }

    // True when the element exposes text at all; text is its selection, or null when nothing is selected.
    private static bool TrySelectedText(nint element, out string? text)
    {
        text = null;
        nint pattern = 0, ranges = 0, range = 0, bstr = 0;
        try
        {
            var iid = IID_IUIAutomationTextPattern;
            var getPattern = (delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)Slot(element, 14);
            if (getPattern(element, UIA_TextPatternId, &iid, &pattern) < 0 || pattern == 0) return false;

            int count;
            if (Call(pattern, 5, &ranges) < 0 || ranges == 0 || Call(ranges, 3, &count) < 0 || count == 0) return true;
            var getElement = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(ranges, 4);
            if (getElement(ranges, 0, &range) < 0 || range == 0) return true;

            var getText = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(range, 12);
            if (getText(range, MaxText, &bstr) < 0 || bstr == 0) return true;
            var value = Marshal.PtrToStringBSTR(bstr).Trim();
            text = value.Length == 0 ? null : value;
            return true;
        }
        finally
        {
            if (bstr != 0) Win32.SysFreeString(bstr);
            Release(range);
            Release(ranges);
            Release(pattern);
        }
    }

    private static string? ClassName(nint element)
    {
        nint bstr = 0;
        if (Call(element, 30, &bstr) < 0 || bstr == 0) return null;
        try
        {
            return Marshal.PtrToStringBSTR(bstr);
        }
        finally
        {
            Win32.SysFreeString(bstr);
        }
    }

    // Ctrl+C isn't "copy" everywhere: it stops commands in terminals, copies files in Explorer,
    // leaves Excel's marching ants and goes to the remote PC in Remote Desktop.
    private static bool CanBorrow(nint window)
    {
        if (ShellForegroundWatcher.IsShellWindow(window) || ShellForegroundWatcher.CoversWholeMonitor(window)) return false;
        var buffer = stackalloc char[128];
        var length = Win32.GetClassName(window, buffer, 128);
        if (NoCopyWindows.Contains(new string(buffer, 0, length))) return false;
        Win32.GetWindowThreadProcessId(window, out var pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return !PasswordManagers.Any(p => process.ProcessName.Contains(p, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Applies when the app hides its text from UI Automation: VS Code's editor always does,
    // other apps when they don't support it. The selection is copied and the clipboard is put
    // back right after. Skipped while any modifier is held, since Ctrl+Shift+C means something else.
    private static string? BorrowCopy(nint foreground)
    {
        if (IsDown(Win32.VK_SHIFT) || IsDown(Win32.VK_CONTROL) || IsDown(Win32.VK_MENU) || IsDown(Win32.VK_LWIN) || IsDown(0x5C)) return null;
        if (Win32.GetForegroundWindow() != foreground) return null;

        Volatile.Write(ref _borrowUntil, Environment.TickCount64 + 2000);
        try
        {
            var snapshot = ClipboardSnapshot.Take();
            if (snapshot is null) return null;
            var before = Win32.GetClipboardSequenceNumber();
            Win32.SendCtrlC();

            for (var i = 0; i < 20 && Win32.GetClipboardSequenceNumber() == before; i++) Thread.Sleep(25);
            if (Win32.GetClipboardSequenceNumber() == before) return null;

            Thread.Sleep(30);
            var text = ClipboardSnapshot.ReadText()?.Trim();
            snapshot.Restore();
            if (!string.IsNullOrEmpty(text)) _borrowed = (text, DateTimeOffset.Now.AddSeconds(-3));
            return string.IsNullOrEmpty(text) ? null : text.Length > MaxText ? text[..MaxText] : text;
        }
        finally
        {
            Volatile.Write(ref _borrowUntil, Environment.TickCount64 + 600);
        }
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
        _instance = null;
        if (_automation != 0)
        {
            Release(_automation);
            _automation = 0;
        }
    }
}

// Windows' own clipboard history (Win+V) records a borrowed copy; it is removed again so a
// selection never lingers there or syncs to other devices.
internal static class ClipboardHistoryCleanup
{
    public static async Task ForgetAsync(string text, DateTimeOffset since)
    {
        try
        {
            await Task.Delay(500);
            var result = await Windows.ApplicationModel.DataTransfer.Clipboard.GetHistoryItemsAsync();
            if (result.Status != Windows.ApplicationModel.DataTransfer.ClipboardHistoryItemsResultStatus.Success) return;
            foreach (var item in result.Items)
            {
                if (item.Timestamp < since || !item.Content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) continue;
                if ((await item.Content.GetTextAsync()).Trim() == text) Windows.ApplicationModel.DataTransfer.Clipboard.DeleteItemFromHistory(item);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
