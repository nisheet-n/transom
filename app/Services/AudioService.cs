using System.Diagnostics;
using System.Runtime.InteropServices;
using Transom.Native;

using static Transom.Native.Com;

namespace Transom.Services;

public sealed record AudioApp(uint ProcessId, string Name, string? ExePath, float Volume, bool Muted, bool IsSystemSounds, bool Playing);

// Windows Core Audio through raw vtable calls (trimming/AOT friendly, no extra packages).
// Slot numbers are each method's position in mmdeviceapi.h / audiopolicy.h / endpointvolume.h, plus 3 for IUnknown.
public sealed unsafe class AudioService : IDisposable
{
    private const int eRender = 0, eCapture = 1, eConsole = 0, eCommunications = 2, AudioSessionStateActive = 1, AudioSessionStateExpired = 2;
    private const uint CLSCTX_ALL = 0x17;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IID_IAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    private static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private nint _enumerator;

    public AudioService()
    {
        if (Win32.CoCreateInstance(CLSID_MMDeviceEnumerator, 0, CLSCTX_ALL, IID_IMMDeviceEnumerator, out _enumerator) < 0) _enumerator = 0;
    }

    public float? MasterVolume
    {
        get => WithEndpoint(eRender, eConsole, e => { float v; return Call(e, 9, &v) >= 0 ? v : (float?)null; });
        set { if (value is { } v) WithEndpoint(eRender, eConsole, e => SetScalar(e, 7, Math.Clamp(v, 0, 1))); }
    }

    public bool MasterMuted
    {
        get => WithEndpoint(eRender, eConsole, e => { int m; return Call(e, 15, &m) >= 0 && m != 0; });
        set => WithEndpoint(eRender, eConsole, e => SetBool(e, 14, value));
    }

    public bool? MicrophoneMuted
    {
        get => WithEndpoint(eCapture, eCommunications, e => { int m; return Call(e, 15, &m) >= 0 ? m != 0 : (bool?)null; });
        set { if (value is { } muted) WithEndpoint(eCapture, eCommunications, e => SetBool(e, 14, muted)); }
    }

    // One row per process; a process with several sessions (common in browsers) shows the loudest.
    public IReadOnlyList<AudioApp> Apps()
    {
        var apps = new Dictionary<uint, AudioApp>();
        nint device = 0, manager = 0, sessions = 0;
        try
        {
            if (!DefaultDevice(eRender, eConsole, out device)) return [];
            var iid = IID_IAudioSessionManager2;
            var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Slot(device, 3);
            if (activate(device, &iid, CLSCTX_ALL, 0, &manager) < 0 || manager == 0) return [];
            if (Call(manager, 5, &sessions) < 0 || sessions == 0) return [];

            int count;
            if (Call(sessions, 3, &count) < 0) return [];
            var getSession = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(sessions, 4);
            for (var i = 0; i < count; i++)
            {
                nint control = 0;
                if (getSession(sessions, i, &control) < 0 || control == 0) continue;
                try
                {
                    if (ReadSession(control) is { } app && (!apps.TryGetValue(app.ProcessId, out var known) || (app.Playing && !known.Playing) || app.Volume > known.Volume))
                        apps[app.ProcessId] = app;
                }
                finally
                {
                    Release(control);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            Release(sessions);
            Release(manager);
            Release(device);
        }
        return [.. apps.Values.Where(a => a.Playing || a.Muted || a.Volume < 0.995f || a.IsSystemSounds).OrderBy(a => a.IsSystemSounds).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public void SetAppVolume(uint processId, float volume) => ForEachSessionOf(processId, v => SetScalar(v, 3, Math.Clamp(volume, 0, 1)));

    public void SetAppMuted(uint processId, bool muted) => ForEachSessionOf(processId, v => SetBool(v, 5, muted));

    private AudioApp? ReadSession(nint control)
    {
        int state;
        if (Call(control, 3, &state) < 0 || state == AudioSessionStateExpired) return null;

        nint control2 = 0, volume = 0;
        try
        {
            if (QueryInterface(control, IID_IAudioSessionControl2, out control2) < 0) return null;
            uint pid;
            if (Call(control2, 14, &pid) < 0) return null;
            var isSystem = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(control2, 15))(control2) == 0;
            if (pid == Environment.ProcessId) return null;

            if (QueryInterface(control, IID_ISimpleAudioVolume, out volume) < 0) return null;
            float level;
            int muted;
            Call(volume, 4, &level);
            Call(volume, 6, &muted);

            var (name, path) = isSystem ? ("System sounds", null) : Describe(pid);
            return new AudioApp(pid, name, path, level, muted != 0, isSystem, state == AudioSessionStateActive);
        }
        finally
        {
            Release(volume);
            Release(control2);
        }
    }

    private void ForEachSessionOf(uint processId, Action<nint> apply)
    {
        nint device = 0, manager = 0, sessions = 0;
        try
        {
            if (!DefaultDevice(eRender, eConsole, out device)) return;
            var iid = IID_IAudioSessionManager2;
            var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Slot(device, 3);
            if (activate(device, &iid, CLSCTX_ALL, 0, &manager) < 0 || Call(manager, 5, &sessions) < 0) return;
            int count;
            Call(sessions, 3, &count);
            var getSession = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(sessions, 4);
            for (var i = 0; i < count; i++)
            {
                nint control = 0, control2 = 0, volume = 0;
                try
                {
                    if (getSession(sessions, i, &control) < 0 || QueryInterface(control, IID_IAudioSessionControl2, out control2) < 0) continue;
                    uint pid;
                    if (Call(control2, 14, &pid) < 0 || pid != processId) continue;
                    if (QueryInterface(control, IID_ISimpleAudioVolume, out volume) >= 0) apply(volume);
                }
                finally
                {
                    Release(volume);
                    Release(control2);
                    Release(control);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            Release(sessions);
            Release(manager);
            Release(device);
        }
    }

    private static readonly Dictionary<uint, (string, string?)> Names = [];

    private static (string Name, string? Path) Describe(uint pid)
    {
        if (Names.TryGetValue(pid, out var known)) return known;
        var result = ("App", (string?)null);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            result = (process.ProcessName, null);
            var path = process.MainModule?.FileName;
            var description = path is null ? null : FileVersionInfo.GetVersionInfo(path).FileDescription;
            result = (string.IsNullOrWhiteSpace(description) ? process.ProcessName : description.Trim(), path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        Names[pid] = result;
        return result;
    }

    private T WithEndpoint<T>(int flow, int role, Func<nint, T> read)
    {
        nint device = 0, endpoint = 0;
        try
        {
            if (!DefaultDevice(flow, role, out device)) return default!;
            var iid = IID_IAudioEndpointVolume;
            var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Slot(device, 3);
            if (activate(device, &iid, CLSCTX_ALL, 0, &endpoint) < 0 || endpoint == 0) return default!;
            return read(endpoint);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return default!;
        }
        finally
        {
            Release(endpoint);
            Release(device);
        }
    }

    private void WithEndpoint(int flow, int role, Action<nint> write) => WithEndpoint<bool>(flow, role, e => { write(e); return true; });

    private bool DefaultDevice(int flow, int role, out nint device)
    {
        device = 0;
        if (_enumerator == 0) return false;
        nint result;
        var get = (delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Slot(_enumerator, 4);
        if (get(_enumerator, flow, role, &result) < 0 || result == 0) return false;
        device = result;
        return true;
    }

    private static void SetScalar(nint obj, int slot, float value)
    {
        var context = Guid.Empty;
        ((delegate* unmanaged[Stdcall]<nint, float, Guid*, int>)Slot(obj, slot))(obj, value, &context);
    }

    private static void SetBool(nint obj, int slot, bool value)
    {
        var context = Guid.Empty;
        ((delegate* unmanaged[Stdcall]<nint, int, Guid*, int>)Slot(obj, slot))(obj, value ? 1 : 0, &context);
    }

    public void Dispose()
    {
        Release(_enumerator);
        _enumerator = 0;
    }
}
