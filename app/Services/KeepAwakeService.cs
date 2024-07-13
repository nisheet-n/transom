using Microsoft.UI.Dispatching;
using static Transom.Native.Win32;

namespace Transom.Services;

public sealed class KeepAwakeService
{
    private readonly DispatcherQueueTimer _timer;
    private bool _keepScreenOn;

    public event Action? Changed;

    public bool IsOn { get; private set; }
    public DateTimeOffset? Until { get; private set; }
    public TimeSpan? Remaining => Until - DateTimeOffset.Now;

    public bool KeepScreenOn
    {
        get => _keepScreenOn;
        set
        {
            _keepScreenOn = value;
            Apply();
        }
    }

    public KeepAwakeService(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(20);
        _timer.Tick += (_, _) => Check();
    }

    public void Start(TimeSpan? duration)
    {
        IsOn = true;
        Until = duration is { } d ? DateTimeOffset.Now + d : null;
        Apply();
        _timer.Start();
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (!IsOn) return;
        IsOn = false;
        Until = null;
        SetThreadExecutionState(ES_CONTINUOUS);
        _timer.Stop();
        Changed?.Invoke();
    }

    private void Apply()
    {
        if (!IsOn) return;
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | (_keepScreenOn ? ES_DISPLAY_REQUIRED : 0));
    }

    private void Check()
    {
        if (Remaining is { } left && left <= TimeSpan.Zero) Stop();
        else Changed?.Invoke();
    }
}
