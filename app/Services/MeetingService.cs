using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Transom.Native;

namespace Transom.Services;

public enum CallApp { Other, Teams, Zoom, Meet }

public sealed record CallInfo(string Name, CallApp App, bool CameraOn, string? ProcessName);

// Windows records which apps are using the microphone and camera (the data behind the taskbar's
// mic icon) in the CapabilityAccessManager consent store: a LastUsedTimeStop of 0 means "in use now".
public sealed class MeetingService
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase) { "msedge", "chrome", "firefox", "zen", "brave", "opera", "vivaldi", "arc" };

    private readonly DispatcherQueueTimer _timer;

    public event Action? Changed;

    public CallInfo? Current { get; private set; }

    public MeetingService(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += (_, _) => Check();
        _timer.Start();
        Check();
    }

    private void Check()
    {
        CallInfo? call = null;
        try
        {
            var microphone = UsersOf("microphone");
            var camera = UsersOf("webcam");
            if (microphone.FirstOrDefault() is { } key) call = Describe(key, camera.Contains(key));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        if (call == Current) return;
        Current = call;
        Changed?.Invoke();
    }

    private static List<string> UsersOf(string capability)
    {
        var users = new List<string>();
        using var root = Registry.CurrentUser.OpenSubKey(ConsentStore + capability);
        if (root is null) return users;
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key is null) continue;
            if (name == "NonPackaged")
            {
                foreach (var app in key.GetSubKeyNames())
                {
                    using var appKey = key.OpenSubKey(app);
                    if (appKey is not null && InUse(appKey)) users.Add(app);
                }
            }
            else if (InUse(key))
            {
                users.Add(name);
            }
        }
        users.RemoveAll(u => u.Contains("B2772555-534D-4C06-A7BB-8B2C45A266D1", StringComparison.OrdinalIgnoreCase));
        return users;
    }

    private static bool InUse(RegistryKey key) =>
        key.GetValue("LastUsedTimeStart") is long start && start != 0 && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    // Desktop apps are stored by path with '#' for '\'; Store apps by package family name.
    private static CallInfo Describe(string key, bool camera)
    {
        if (key.Contains('#'))
        {
            var path = key.Replace('#', '\\');
            var process = Path.GetFileNameWithoutExtension(path);
            var name = process;
            try
            {
                if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description) name = description.Trim();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            if (process.Equals("Zoom", StringComparison.OrdinalIgnoreCase)) return new("Zoom", CallApp.Zoom, camera, process);
            if (Browsers.Contains(process) && FindWindow(process, "Meet") != 0) return new("Google Meet", CallApp.Meet, camera, process);
            return new(name, CallApp.Other, camera, process);
        }

        if (key.StartsWith("MSTeams", StringComparison.OrdinalIgnoreCase)) return new("Microsoft Teams", CallApp.Teams, camera, "ms-teams");
        var family = key.Split('_')[0];
        var friendly = family.Split('.').Last();
        return new(friendly, CallApp.Other, camera, null);
    }

    public static nint FindWindow(string processName, string? titleContains = null)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                var handle = process.MainWindowHandle;
                if (handle == 0) continue;
                if (titleContains is null || process.MainWindowTitle.Contains(titleContains, StringComparison.OrdinalIgnoreCase)) return handle;
            }
        }
        return 0;
    }

    public void BringToFront()
    {
        if (Current?.ProcessName is { } process && FindWindow(process, Current.App == CallApp.Meet ? "Meet" : null) is not 0 and var window)
            Win32.SetForegroundWindow(window);
    }

    // Each app's own shortcuts, sent to its window: these change the app's state (and its UI)
    // rather than Windows', so camera and leave work too.
    public bool Send(CallAction action)
    {
        if (Current is not { ProcessName: { } process } call) return false;
        ushort[]? keys = (call.App, action) switch
        {
            (CallApp.Teams, CallAction.Camera) => [Win32.VK_CONTROL, Win32.VK_SHIFT, 0x4F],
            (CallApp.Teams, CallAction.Leave) => [Win32.VK_CONTROL, Win32.VK_SHIFT, 0x48],
            (CallApp.Zoom, CallAction.Camera) => [Win32.VK_MENU, 0x56],
            (CallApp.Zoom, CallAction.Leave) => [Win32.VK_MENU, 0x51],
            (CallApp.Meet, CallAction.Camera) => [Win32.VK_CONTROL, 0x45],
            _ => null,
        };
        if (keys is null) return false;
        var window = FindWindow(process, call.App == CallApp.Meet ? "Meet" : null);
        if (window == 0 || !Win32.SetForegroundWindow(window)) return false;
        Thread.Sleep(80);
        Win32.SendChord(keys);
        return true;
    }

    public static bool Supports(CallApp app, CallAction action) => (app, action) switch
    {
        (CallApp.Teams or CallApp.Zoom, _) => true,
        (CallApp.Meet, CallAction.Camera) => true,
        _ => false,
    };
}

public enum CallAction { Camera, Leave }
