using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Transom.Services;

namespace Transom.Controls;

public sealed partial class MeetingPanel : UserControl
{
    private const string MicGlyph = "", MicOffGlyph = "";

    private MeetingService? _meeting;
    private AudioService? _audio;

    public event Action? MuteChanged;

    public event Action? CloseRequested;

    public MeetingPanel()
    {
        InitializeComponent();
    }

    public void Attach(MeetingService meeting, AudioService audio)
    {
        _meeting = meeting;
        _audio = audio;
    }

    public void Opened()
    {
        if (_meeting?.Current is not { } call) return;
        Source.Text = call.CameraOn ? $"{call.Name} is using your microphone and camera" : $"{call.Name} is using your microphone";
        GoToText.Text = $"Go to {call.Name}";
        var camera = MeetingService.Supports(call.App, CallAction.Camera);
        var leave = MeetingService.Supports(call.App, CallAction.Leave);
        CameraButton.Visibility = camera ? Visibility.Visible : Visibility.Collapsed;
        LeaveButton.Visibility = leave ? Visibility.Visible : Visibility.Collapsed;
        CameraText.Text = call.CameraOn ? "Turn camera off" : "Turn camera on";
        Hint.Text = camera || leave
            ? $"Mute works for every app. Camera and leave use {call.Name}'s own shortcuts."
            : "Mute turns off your microphone for every app until you unmute.";
        ShowMute();
    }

    private void ShowMute()
    {
        var muted = _audio?.MicrophoneMuted == true;
        MuteIcon.Glyph = muted ? MicOffGlyph : MicGlyph;
        MuteText.Text = muted ? "Unmute microphone" : "Mute microphone";
        MuteButton.Style = muted ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_audio is null) return;
        _audio.MicrophoneMuted = _audio.MicrophoneMuted != true;
        ShowMute();
        MuteChanged?.Invoke();
    }

    private void Camera_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
        _meeting?.Send(CallAction.Camera);
    }

    private void Leave_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
        _meeting?.Send(CallAction.Leave);
    }

    private void GoTo_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
        _meeting?.BringToFront();
    }
}
