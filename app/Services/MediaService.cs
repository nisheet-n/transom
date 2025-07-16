using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Transom.Services;

public sealed class MediaService
{
    private readonly DispatcherQueue _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _version;
    private volatile bool _timelineQueued;

    public event Action? Changed;

    public bool HasSession => _session is not null;
    public bool IsPlaying { get; private set; }
    public bool IsVideo { get; private set; }
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Source { get; private set; } = "";
    public ImageSource? Artwork { get; private set; }
    public TimeSpan Duration { get; private set; }
    public TimeSpan Position { get; private set; }
    private DateTimeOffset _positionStamp = DateTimeOffset.Now;

    public TimeSpan CurrentPosition
    {
        get
        {
            if (!IsPlaying || Duration <= TimeSpan.Zero) return Position;
            var now = Position + (DateTimeOffset.Now - _positionStamp);
            return now > Duration ? Duration : now;
        }
    }
    public double ArtworkAspect { get; private set; } = 1;

    public MediaService(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _dispatcher.TryEnqueue(() => _ = AttachAsync());
            await AttachAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public Task TogglePlayPauseAsync() => Send(s => s.TryTogglePlayPauseAsync().AsTask());

    public Task NextAsync() => Send(s => s.TrySkipNextAsync().AsTask());

    public Task PreviousAsync() => Send(s => s.TrySkipPreviousAsync().AsTask());

    // A player can close between the click and the call; that shouldn't take Transom down with it.
    private async Task Send(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
    {
        if (_session is not { } session) return;
        try
        {
            await command(session);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task AttachAsync()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = _manager?.GetCurrentSession();

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
            Source = FriendlySource(_session.SourceAppUserModelId);
        }

        UpdatePlayback();
        UpdateTimeline();
        await RefreshPropertiesAsync();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _dispatcher.TryEnqueue(() => _ = RefreshPropertiesAsync());

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        _dispatcher.TryEnqueue(() =>
        {
            UpdatePlayback();
            UpdateTimeline();
            Changed?.Invoke();
        });

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        if (_timelineQueued) return;
        _timelineQueued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _timelineQueued = false;
            UpdateTimeline();
        });
    }

    private void UpdatePlayback()
    {
        var info = _session?.GetPlaybackInfo();
        IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        IsVideo = info?.PlaybackType == Windows.Media.MediaPlaybackType.Video;
    }

    private async Task RefreshPropertiesAsync()
    {
        var version = ++_version;
        var session = _session;

        if (session is null)
        {
            Title = Artist = Source = "";
            Artwork = null;
            Changed?.Invoke();
            return;
        }

        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            if (version != _version) return;

            Title = properties.Title ?? "";
            Artist = string.IsNullOrEmpty(properties.Artist) ? properties.AlbumArtist ?? "" : properties.Artist;
            Changed?.Invoke();

            var (artwork, aspect) = await LoadArtworkAsync(properties.Thumbnail);
            if (version != _version) return;

            Artwork = artwork;
            ArtworkAspect = aspect;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static async Task<(ImageSource?, double)> LoadArtworkAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return (null, 1);

        using var stream = await thumbnail.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var aspect = decoder.PixelHeight == 0 ? 1 : decoder.PixelWidth / (double)decoder.PixelHeight;

        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return (image, aspect);
    }

    private void UpdateTimeline()
    {
        var timeline = _session?.GetTimelineProperties();
        if (timeline is null)
        {
            Position = Duration = TimeSpan.Zero;
            return;
        }
        Duration = timeline.EndTime - timeline.StartTime;
        Position = timeline.Position - timeline.StartTime;
        _positionStamp = timeline.LastUpdatedTime.Year > 2000 ? timeline.LastUpdatedTime : DateTimeOffset.Now;
    }

    private static string FriendlySource(string appId)
    {
        try
        {
            return AppInfo.GetFromAppUserModelId(appId).DisplayInfo.DisplayName;
        }
        catch (Exception)
        {
            var startName = Native.ShellItem.StartMenuName(appId);
            if (!string.IsNullOrEmpty(startName)) return startName;

            var name = appId.Split('!')[0];
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : "";
        }
    }
}
