using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Transom.Native;

namespace Transom.Services;

// Reads the text selected in whatever app has focus, through UI Automation (the same API screen
// readers use), after each mouse release or Shift / Ctrl+A key release. Hooks live on their own
// thread and reads run on the thread pool, so a slow or hung app never delays mouse or keyboard input.
public sealed unsafe class SelectionWatcher : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const nint WM_KEYUP = 0x0101, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const uint WM_QUIT = 0x0012, COINIT_MULTITHREADED = 0, CLSCTX_INPROC_SERVER = 1;
    private const int UIA_TextPatternId = 10014, MaxText = 100_000;
    private const string HiddenTextEditor = "native-edit-context";

    private static readonly Guid CLSID_CUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid IID_IUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
    private static readonly Guid IID_IUIAutomationTextPattern = new("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A");

    private static SelectionWatcher? _instance;
    private static int _downX, _downY;
    private static uint _lastUpTime;
    private static bool _multiClick;
    private static long _borrowUntil;
    private int _gesture;

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

    [UnmanagedCallersOnly]
    private static nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && wParam == WM_KEYUP)
        {
            var vk = *(uint*)lParam;
            if (vk is 0x10 or 0xA0 or 0xA1 || (vk == 0x41 && IsDown(Win32.VK_CONTROL))) _instance?.Schedule(true);
        }
        return Win32.CallNextHookEx(0, code, wParam, lParam);
    }

    private static bool IsDown(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void Schedule(bool gesture)
    {
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
            Publish(ReadSelection(foreground, Interlocked.Exchange(ref _gesture, 0) == 1));
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
    // in the UIAutomationClient.h interface, plus 3 for IUnknown.
    private string? ReadSelection(nint foreground, bool gesture)
    {
        Win32.CoInitializeEx(0, COINIT_MULTITHREADED);
        if (_automation == 0 && Win32.CoCreateInstance(CLSID_CUIAutomation, 0, CLSCTX_INPROC_SERVER, IID_IUIAutomation, out _automation) < 0) return null;

        nint element = 0, pattern = 0, ranges = 0, range = 0, text = 0, className = 0;
        try
        {
            if (Call(_automation, 8, &element) < 0 || element == 0) return null;

            int isPassword;
            if (Call(element, 35, &isPassword) >= 0 && isPassword != 0) return null;

            if (Call(element, 30, &className) >= 0 && className != 0 && Marshal.PtrToStringBSTR(className) == HiddenTextEditor)
                return gesture ? BorrowCopy(foreground) : null;

            var iid = IID_IUIAutomationTextPattern;
            var getPattern = (delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)Slot(element, 14);
            if (getPattern(element, UIA_TextPatternId, &iid, &pattern) < 0 || pattern == 0) return null;

            if (Call(pattern, 5, &ranges) < 0 || ranges == 0) return null;
            int count;
            if (Call(ranges, 3, &count) < 0 || count == 0) return null;
            var getElement = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(ranges, 4);
            if (getElement(ranges, 0, &range) < 0 || range == 0) return null;

            var getText = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(range, 12);
            if (getText(range, MaxText, &text) < 0 || text == 0) return null;
            var value = Marshal.PtrToStringBSTR(text).Trim();
            return value.Length == 0 ? null : value;
        }
        finally
        {
            if (text != 0) Win32.SysFreeString(text);
            if (className != 0) Win32.SysFreeString(className);
            Release(range);
            Release(ranges);
            Release(pattern);
            Release(element);
        }
    }

    // VS Code and other Monaco editors hide their text from UI Automation unless screen reader
    // mode is on, so for them the selection is copied and the clipboard is put back right after.
    // Skipped while any modifier is held, since Ctrl+Shift+C or Ctrl+Alt+C mean something else.
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
            return string.IsNullOrEmpty(text) ? null : text.Length > MaxText ? text[..MaxText] : text;
        }
        finally
        {
            Volatile.Write(ref _borrowUntil, Environment.TickCount64 + 600);
        }
    }

    private static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    private static int Call<T>(nint obj, int index, T* result) where T : unmanaged =>
        ((delegate* unmanaged[Stdcall]<nint, T*, int>)Slot(obj, index))(obj, result);

    private static void Release(nint obj)
    {
        if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj);
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
