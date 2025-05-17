using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Transom.Controls;
using Transom.Native;
using Transom.Services;
using Windows.System.Power;

namespace Transom;

public sealed partial class RailWindow : Window
{
    private readonly AppBarDock _dock;
    private readonly ShellForegroundWatcher _shell = new();
    private readonly MediaService _media;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timelineTimer;
    private readonly KeepAwakeService _awake;
    private readonly ReminderStore _reminders;
    private readonly ClipboardService _clipboard = new();
    private readonly ConvertService _convert = new();
    private DetectedValue? _detected;
    private SelectionWatcher? _selection;
    private string? _selectedText;
    private nint _lastAppWindow;
    private const int ClipboardHotKey = 1;
    private const int NotesHotKey = 2;
    private const int NavHotKeyBase = 11;
    private string _appliedDock = "";
    private bool _navKeysOn;
    private readonly NoteStore _notes;
    private readonly ShelfService _shelf = new();
    private DateTimeOffset? _captureRequested;
    private bool _isLight;
    private SettingsWindow? _settingsWindow;
    private readonly nint _hwnd;

    public RailWindow()
    {
        InitializeComponent();

        _media = new MediaService(DispatcherQueue);
        _media.Changed += UpdateMedia;
        _timelineTimer = DispatcherQueue.CreateTimer();
        _timelineTimer.Interval = TimeSpan.FromSeconds(1);
        _timelineTimer.Tick += (_, _) => UpdateTimeline();
        _awake = new KeepAwakeService(DispatcherQueue);
        _awake.Changed += UpdateAwake;
        ConvertPanelView.Attach(_convert);
        _reminders = new ReminderStore(DispatcherQueue);
        _reminders.Changed += UpdateReminders;
        _reminders.Due += Notifications.ShowReminder;
        Notifications.ReminderAction += OnReminderAction;
        UpdateReminders();

        var hwnd = _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;
        Win32.RemoveFrame(hwnd);

        _dock = new AppBarDock(hwnd, AppWindow);
        _dock.ThicknessChanged += ApplyThickness;
        _dock.FullScreenChanged += _ => RecheckOnTop();
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) SetOnTop(true);
        };
        _shell.ForegroundChanged += foreground =>
        {
            if (foreground != _hwnd && !ShellForegroundWatcher.IsShellWindow(foreground)) _lastAppWindow = foreground;
            UpdateOnTop(foreground);
        };
        _dock.SettingChanged += ApplyLook;
        _dock.ClipboardChanged += async () =>
        {
            if (SelectionWatcher.IsBorrowingClipboard) return;
            var foreground = Win32.GetForegroundWindow();
            await _clipboard.CaptureAsync(foreground == _hwnd ? "" : AppName(foreground));
            DetectFromClipboard();
            await ShowCaptureIfRequestedAsync();
        };
        _dock.HotKeyPressed += id =>
        {
            if (id == ClipboardHotKey) OpenClipboard();
            if (id == NotesHotKey) OpenNotes();
            if (id is >= NavHotKeyBase and < NavHotKeyBase + 9) TriggerRailItem(id - NavHotKeyBase);
        };
        _clipboard.Changed += UpdateClipboard;
        Win32.AddClipboardFormatListener(hwnd);
        Win32.RegisterHotKey(hwnd, ClipboardHotKey, Win32.MOD_WIN | Win32.MOD_ALT | Win32.MOD_NOREPEAT, Win32.VK_V);
        Win32.RegisterHotKey(hwnd, NotesHotKey, Win32.MOD_WIN | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x4E);
        _notes = new NoteStore(DispatcherQueue);
        NotesPanelView.Attach(_notes);
        ShelfPanelView.Attach(_shelf, hwnd);
        CapturePanelView.Attach(_shelf);
        CapturePanelView.NewCaptureRequested += () =>
        {
            CaptureFlyout.Hide();
            _ = StartCaptureAsync();
        };
        _shelf.Changed += UpdateShelfBadge;
        UpdateShelfBadge();
        _selection = new SelectionWatcher(DispatcherQueue);
        _selection.Changed += OnSelectionChanged;
        ApplyRailSettings();

        if (SystemBackdrop is RailBackdrop backdrop) backdrop.Connected += ApplyLook;
        RailSettings.Current.Changed += ApplyLook;
        RailSettings.Current.Changed += ApplyRailSettings;
        PowerManager.EnergySaverStatusChanged += OnEnergySaverChanged;
        _ = _media.StartAsync();

        Closed += (_, _) =>
        {
            PowerManager.EnergySaverStatusChanged -= OnEnergySaverChanged;
            RailSettings.Current.Changed -= ApplyLook;
            RailSettings.Current.Changed -= ApplyRailSettings;
            for (var i = 0; i < 9; i++) Win32.UnregisterHotKey(_hwnd, NavHotKeyBase + i);
            if (RailSettings.Current.ClearClipboardOnQuit) _clipboard.ClearUnpinned();
            _settingsWindow?.Close();
            _awake.Stop();
            Notifications.ReminderAction -= OnReminderAction;
            Win32.RemoveClipboardFormatListener(_hwnd);
            Win32.UnregisterHotKey(_hwnd, ClipboardHotKey);
            Win32.UnregisterHotKey(_hwnd, NotesHotKey);
            _notes.SaveNow();
            Notifications.Unregister();
            _selection?.Dispose();
            _shell.Dispose();
            _dock.Dispose();
        };
    }

    private void ApplyRailSettings()
    {
        var settings = RailSettings.Current;
        ApplyTools(settings);
        ApplyNavigationKeys(settings.KeyboardNavigation);
        Win32.SetWindowDisplayAffinity(_hwnd, settings.HideFromCapture ? Win32.WDA_EXCLUDEFROMCAPTURE : 0);
        if (_selection is not null) _selection.Enabled = settings.WatchSelection;
        if (!_awake.IsOn) AwakeDuration.SelectedIndex = Math.Clamp(settings.AwakeDefault, 0, 3);

        if (settings.Dock == _appliedDock) return;
        _appliedDock = settings.Dock;
        ApplyDock(settings.Dock);
        _dock.Layout();
    }

    // Moving between edges turns the rail horizontal for top/bottom and points every panel
    // toward the middle of the screen, away from the edge the rail sits on.
    private void ApplyDock(string dock)
    {
        _dock.Edge = dock switch { "right" => Win32.ABE_RIGHT, "top" => Win32.ABE_TOP, "bottom" => Win32.ABE_BOTTOM, _ => Win32.ABE_LEFT };
        var horizontal = _dock.IsHorizontal;

        Root.RowDefinitions.Clear();
        Root.ColumnDefinitions.Clear();
        foreach (var size in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
        {
            if (horizontal) Root.ColumnDefinitions.Add(new ColumnDefinition { Width = size });
            else Root.RowDefinitions.Add(new RowDefinition { Height = size });
        }
        Grid.SetRow(TopSlots, 0);
        Grid.SetColumn(TopSlots, 0);
        Grid.SetRow(BottomSlots, horizontal ? 0 : 2);
        Grid.SetColumn(BottomSlots, horizontal ? 2 : 0);

        var orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        TopSlots.Orientation = BottomSlots.Orientation = orientation;
        TopSlots.Padding = horizontal ? new Thickness(6, 0, 0, 0) : new Thickness(0, 6, 0, 0);
        BottomSlots.Padding = horizontal ? new Thickness(0, 0, 6, 0) : new Thickness(0, 0, 0, 6);
        TopSlots.VerticalAlignment = BottomSlots.VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Stretch;

        Root.BorderThickness = _dock.Edge switch
        {
            Win32.ABE_RIGHT => new Thickness(1, 0, 0, 0),
            Win32.ABE_TOP => new Thickness(0, 0, 0, 1),
            Win32.ABE_BOTTOM => new Thickness(0, 1, 0, 0),
            _ => new Thickness(0, 0, 1, 0),
        };

        var (start, end) = _dock.Edge switch
        {
            Win32.ABE_RIGHT => (FlyoutPlacementMode.LeftEdgeAlignedTop, FlyoutPlacementMode.LeftEdgeAlignedBottom),
            Win32.ABE_TOP => (FlyoutPlacementMode.BottomEdgeAlignedLeft, FlyoutPlacementMode.BottomEdgeAlignedRight),
            Win32.ABE_BOTTOM => (FlyoutPlacementMode.TopEdgeAlignedLeft, FlyoutPlacementMode.TopEdgeAlignedRight),
            _ => (FlyoutPlacementMode.RightEdgeAlignedTop, FlyoutPlacementMode.RightEdgeAlignedBottom),
        };
        foreach (var flyout in new FlyoutBase[] { MediaFlyout, AiFlyout, ClipboardFlyout, ConvertFlyout, NotesFlyout, ShelfFlyout, CaptureFlyout }) flyout.Placement = start;
        foreach (var flyout in new FlyoutBase[] { AwakeFlyout, RemindersFlyout, TransomMenu }) flyout.Placement = end;

        var tipPlacement = _dock.Edge switch
        {
            Win32.ABE_RIGHT => PlacementMode.Left,
            Win32.ABE_TOP => PlacementMode.Bottom,
            Win32.ABE_BOTTOM => PlacementMode.Top,
            _ => PlacementMode.Right,
        };
        foreach (var button in TopSlots.Children.Concat(BottomSlots.Children).OfType<Button>()) ToolTipService.SetPlacement(button, tipPlacement);
    }

    private void ApplyTools(RailSettings settings)
    {
        bool Show(string id) => !settings.HiddenTools.Contains(id);
        static Visibility V(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

        ArtButton.Visibility = PlayButton.Visibility = V(Show("media"));
        AiButton.Visibility = V(Show("ai"));
        ClipboardButton.Visibility = V(Show("clipboard"));
        ConvertButton.Visibility = V(Show("convert"));
        NotesButton.Visibility = V(Show("notes"));
        ShelfButton.Visibility = V(Show("shelf"));
        CaptureButton.Visibility = V(Show("capture"));
        AwakeButton.Visibility = V(Show("awake"));
        RemindersButton.Visibility = V(Show("reminders"));

        var firstGroup = Show("ai") || Show("clipboard") || Show("convert");
        var secondGroup = Show("notes") || Show("shelf") || Show("capture");
        SepMedia.Visibility = V(Show("media") && (firstGroup || secondGroup));
        SepTools.Visibility = V(firstGroup && secondGroup);
        SepMenu.Visibility = V(Show("awake") || Show("reminders"));
    }

    private void ApplyNavigationKeys(bool on)
    {
        if (on == _navKeysOn) return;
        _navKeysOn = on;
        for (var i = 0; i < 9; i++)
        {
            if (on) Win32.RegisterHotKey(_hwnd, NavHotKeyBase + i, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, (uint)('1' + i));
            else Win32.UnregisterHotKey(_hwnd, NavHotKeyBase + i);
        }
    }

    private void TriggerRailItem(int index)
    {
        var items = TopSlots.Children.Concat(BottomSlots.Children)
            .OfType<Button>()
            .Where(b => b.Visibility == Visibility.Visible && b.Opacity > 0.9)
            .ToList();
        if (index >= items.Count) return;

        var button = items[index];
        Activate();
        if (button == CaptureButton) _ = StartCaptureAsync();
        else if (button == PlayButton) _ = _media.TogglePlayPauseAsync();
        else button.Flyout?.ShowAt(button);
    }

    // Windows announces full screen before the window has finished resizing, so measure
    // the front window again over the next two seconds instead of only once.
    private async void RecheckOnTop()
    {
        foreach (var delay in new[] { 0, 250, 750, 2000 })
        {
            await Task.Delay(delay);
            UpdateOnTop(Win32.GetForegroundWindow());
        }
    }

    private void UpdateOnTop(nint foreground)
    {
        var fullScreenApp = foreground != _hwnd
            && ShellForegroundWatcher.CoversWholeMonitor(foreground)
            && !ShellForegroundWatcher.IsShellWindow(foreground);
        SetOnTop(!fullScreenApp);
    }

    private void SetOnTop(bool onTop)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = onTop;
    }

    private void ApplyLook()
    {
        var settings = RailSettings.Current;
        var taskbar = TaskbarAppearance.Read();
        var isLight = settings.Theme switch
        {
            RailTheme.Light => true,
            RailTheme.Dark => false,
            _ => taskbar.IsLight,
        };
        var saver = settings.SolidOnBatterySaver && PowerManager.EnergySaverStatus == EnergySaverStatus.On;

        _isLight = isLight;
        Root.RequestedTheme = isLight ? ElementTheme.Light : ElementTheme.Dark;
        UpdateAwake();
        var solid = (SystemBackdrop as RailBackdrop)?.Apply(settings, taskbar, isLight, saver);
        Root.Background = new SolidColorBrush(solid ?? Microsoft.UI.Colors.Transparent);
    }

    private void OnEnergySaverChanged(object? sender, object e) => DispatcherQueue.TryEnqueue(ApplyLook);

    private void UpdateMedia()
    {
        var hasArt = _media.Artwork is not null;
        ArtImage.ImageSource = _media.Artwork;
        FlyArtImage.ImageSource = _media.Artwork;
        ArtFrame.Visibility = FlyPoster.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;
        ArtPlaceholder.Visibility = FlyNoArt.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;
        SizeArtwork();
        UpdateTimeline();

        var label = string.IsNullOrEmpty(_media.Artist) ? _media.Title : $"{_media.Title} · {_media.Artist}";
        ToolTipService.SetToolTip(ArtButton, _media.HasSession && label.Length > 0 ? label : "Nothing playing");
        FlyTitle.Text = _media.HasSession && _media.Title.Length > 0 ? _media.Title : "Nothing playing";
        FlyArtist.Text = _media.Artist;
        FlyArtist.Visibility = _media.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FlySource.Text = _media.HasSession && _media.Source.Length > 0 ? $"Playing in {_media.Source}" : "";

        PlayButton.IsEnabled = FlyPlayPause.IsEnabled = FlyPrevious.IsEnabled = FlyNext.IsEnabled = _media.HasSession;
        PlayGlyph.Visibility = _media.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Visibility = _media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        FlyPlayGlyph.Glyph = _media.IsPlaying ? "" : "";
        ToolTipService.SetToolTip(PlayButton, _media.IsPlaying ? "Pause" : "Play");
    }

    private void SizeArtwork()
    {
        var aspect = _media.ArtworkAspect;
        (ArtFrame.Width, ArtFrame.Height) = Fit(ArtHost.Width, ArtHost.Height, 16.0 / 9);
        (ArtPoster.Width, ArtPoster.Height) = Fit(ArtFrame.Width, ArtFrame.Height, aspect);
        (FlyPoster.Width, FlyPoster.Height) = Fit(FlyArt.Width, FlyArt.Height, aspect);
    }

    private void UpdateTimeline()
    {
        var duration = _media.Duration;
        FlyTimeline.Visibility = _media.HasSession && duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        if (FlyTimeline.Visibility == Visibility.Collapsed) return;

        var position = _media.CurrentPosition;
        FlyProgress.Value = Math.Clamp(position / duration, 0, 1);
        FlyElapsed.Text = FormatTime(position);
        FlyDuration.Text = FormatTime(duration);
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private void MediaFlyout_Opened(object sender, object e)
    {
        UpdateTimeline();
        _timelineTimer.Start();
    }

    private void MediaFlyout_Closed(object sender, object e) => _timelineTimer.Stop();

    private static readonly TimeSpan?[] AwakeDurations = [TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), null];

    private void AwakeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (AwakeToggle.IsOn == _awake.IsOn) return;
        if (AwakeToggle.IsOn) _awake.Start(AwakeDurations[Math.Max(0, AwakeDuration.SelectedIndex)]);
        else _awake.Stop();
    }

    private void AwakeDuration_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_awake.IsOn) _awake.Start(AwakeDurations[Math.Max(0, AwakeDuration.SelectedIndex)]);
    }

    private void AwakeScreen_Changed(object sender, RoutedEventArgs e) => _awake.KeepScreenOn = AwakeScreen.IsChecked == true;

    private void UpdateAwake()
    {
        if (_awake is null) return;
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        AwakeToggle.IsOn = _awake.IsOn;
        AwakeBadge.Visibility = _awake.IsOn ? Visibility.Visible : Visibility.Collapsed;
        
        if (!_awake.IsOn)
        {
            AwakeGlyph.ClearValue(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty);
            AwakeStatus.Text = "Your PC sleeps as usual.";
            ToolTipService.SetToolTip(AwakeButton, "Keep awake");
            return;
        }

        AwakeGlyph.Stroke = accent;
        var left = _awake.Remaining;
        AwakeBadgeText.Text = left is { } t ? t.TotalMinutes >= 60 ? $"{(int)Math.Ceiling(t.TotalHours)}h" : $"{(int)Math.Ceiling(t.TotalMinutes)}m" : "∞";
        var when = left is { } r ? $"for {FormatLeft(r)} more" : "until you turn it off";
        AwakeStatus.Text = $"Your PC won't sleep {when}.";
        ToolTipService.SetToolTip(AwakeButton, $"Keeping your PC awake {when}");
    }

    private void DetectFromClipboard()
    {
        var latest = _clipboard.Items.OrderByDescending(i => i.Copied).FirstOrDefault();
        if (latest is { Kind: ClipKind.Text } && DateTimeOffset.Now - latest.Copied < TimeSpan.FromSeconds(5)) ShowDetected(latest.Text);
    }

    private void OnSelectionChanged(string? text)
    {
        _selectedText = text;
        SelectionBadge.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(AiButton, text is null ? "Selected text · AI coming soon" : $"Selected: {Shorten(text, 60)}");
        ShowDetected(text);
    }

    private void ShowDetected(string? text)
    {
        _detected = text is null ? null : _convert.Detect(text);
        ConvertBadge.Visibility = _detected is null ? Visibility.Collapsed : Visibility.Visible;
        if (_detected is null)
        {
            ToolTipService.SetToolTip(ConvertButton, "Convert currency, units and time zones");
            return;
        }
        var symbol = _detected.Currency is { } code && _convert.Currencies.TryGetValue(code, out var c) ? c.Symbol : _detected.Unit ?? "";
        ConvertBadgeText.Text = symbol.Length > 3 ? symbol[..3] : symbol;
        ToolTipService.SetToolTip(ConvertButton, $"Convert {text!.Trim()}");
    }

    private static string Shorten(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length > max ? line[..max] + "…" : line;
    }

    private void AiFlyout_Opened(object sender, object e)
    {
        var text = _selectedText;
        SelectionStatus.Visibility = Visibility.Collapsed;
        SelectionActions.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        SelectionPreview.Text = text is null ? "Nothing selected right now." : Shorten(text, 600);
        SelectionPreview.Foreground = (Brush)Application.Current.Resources[text is null ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"];
        SelectionInfo.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        SelectionInfo.Text = text is null ? "" : $"{text.Length:N0} characters";
        SelectionConvertButton.IsEnabled = _detected is not null;
    }

    private void ShowSelectionStatus(string message)
    {
        SelectionStatus.Text = message;
        SelectionStatus.Visibility = Visibility.Visible;
    }

    private void SelectionCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedText is not { } text) return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        ShowSelectionStatus("Copied.");
    }

    private void SelectionNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedText is not { } text) return;
        _notes.Update(_notes.Create(), text);
        _notes.SaveNow();
        ShowSelectionStatus("Saved as a new quick note.");
    }

    private async void SelectionSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedText is not { } text) return;
        AiFlyout.Hide();
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.google.com/search?q=" + Uri.EscapeDataString(Shorten(text, 300))));
    }

    private void SelectionConvert_Click(object sender, RoutedEventArgs e)
    {
        AiFlyout.Hide();
        ConvertFlyout.ShowAt(ConvertButton);
    }

    private void ConvertFlyout_Opened(object sender, object e) => ConvertPanelView.Opened(_detected);

    private void OpenNotes()
    {
        Activate();
        NotesFlyout.ShowAt(NotesButton);
    }

    private void NotesFlyout_Opened(object sender, object e) => NotesPanelView.Opened();

    private void ShelfFlyout_Opened(object sender, object e) => ShelfPanelView.Opened();

    private void UpdateShelfBadge()
    {
        var count = _shelf.Items.Count;
        ShelfBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfBadgeText.Text = count > 99 ? "99+" : count.ToString();
        ToolTipService.SetToolTip(ShelfButton, count == 0 ? "Shelf: drop files here" : $"Shelf · {count} item{(count == 1 ? "" : "s")}");
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Link;
        e.DragUIOverride.Caption = "Add to shelf";
        ShelfButton.Background = (Brush)Application.Current.Resources["AccentFillColorSecondaryBrush"];
    }

    private void Root_DragLeave(object sender, DragEventArgs e) => ShelfButton.ClearValue(Control.BackgroundProperty);

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        ShelfButton.ClearValue(Control.BackgroundProperty);
        var deferral = e.GetDeferral();
        _shelf.Add(await Controls.ShelfPanel.ReadDroppedPathsAsync(e.DataView));
        deferral.Complete();
    }

    private void Capture_Click(object sender, RoutedEventArgs e) => _ = StartCaptureAsync();

    // Uses Windows' own snipping overlay (region, window or full screen); the result lands on the
    // clipboard, where the clipboard watcher picks it up and opens the Capture panel.
    private async Task StartCaptureAsync()
    {
        _captureRequested = DateTimeOffset.Now;
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-screenclip:"));
    }

    private async Task ShowCaptureIfRequestedAsync()
    {
        if (_captureRequested is not { } requested || DateTimeOffset.Now - requested > TimeSpan.FromMinutes(2)) return;
        var latest = _clipboard.Items.OrderByDescending(i => i.Copied).FirstOrDefault();
        if (latest is not { Kind: ClipKind.Image } || latest.Copied < requested || _clipboard.ImagePath(latest) is not { } path) return;

        _captureRequested = null;
        await CapturePanelView.ShowAsync(path);
        Activate();
        CaptureFlyout.ShowAt(CaptureButton);
    }

    private void NotesFlyout_Closed(object sender, object e) => NotesPanelView.Closed();

    private void ConvertFlyout_Closed(object sender, object e)
    {
        ConvertPanelView.Closed();
        _detected = null;
        ConvertBadge.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(ConvertButton, "Convert currency, units and time zones");
    }

    private string _clipFilter = "all";

    private void OpenClipboard()
    {
        Activate();
        ClipboardFlyout.ShowAt(ClipboardButton);
    }

    private void ClipboardFlyout_Opened(object sender, object e)
    {
        UpdateClipboard();
        ClipSearch.Focus(FocusState.Programmatic);
    }

    private void ClipboardFlyout_Closed(object sender, object e) => ClipSearch.Text = "";

    private void ClipSearch_TextChanged(object sender, TextChangedEventArgs e) => UpdateClipboard();

    private void ClipFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _clipFilter = sender.SelectedItem?.Tag as string ?? "all";
        UpdateClipboard();
    }

    private void ClearClipboard_Click(object sender, RoutedEventArgs e) => _clipboard.ClearUnpinned();

    private void PinClip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string id) _clipboard.TogglePin(id);
    }

    private void DeleteClip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string id) _clipboard.Remove(id);
    }

    private async void Clip_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Models.ClipRow row) return;
        var item = _clipboard.Items.FirstOrDefault(i => i.Id == row.Id);
        if (item is null) return;

        await _clipboard.PutOnClipboardAsync(item);
        ClipboardFlyout.Hide();
        if (_lastAppWindow == 0) return;

        Win32.SetForegroundWindow(_lastAppWindow);
        await Task.Delay(120);
        Win32.SendCtrlV();
    }

    private void UpdateClipboard()
    {
        if (!ClipboardFlyout.IsOpen && RecentList.ItemsSource is not null) return;

        var query = ClipSearch.Text.Trim();
        var now = DateTimeOffset.Now;
        var visible = _clipboard.Items
            .Where(i => _clipFilter switch
            {
                "text" => i.Kind == ClipKind.Text,
                "link" => i.Kind == ClipKind.Link,
                "image" => i.Kind == ClipKind.Image,
                "pinned" => i.Pinned,
                _ => true,
            })
            .Where(i => query.Length == 0 || i.Text.Contains(query, StringComparison.OrdinalIgnoreCase) || i.Source.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Copied)
            .ToList();

        var pinned = visible.Where(i => i.Pinned).OrderByDescending(i => i.PinnedAt ?? i.Copied).Select(i => ToRow(i, now)).ToList();
        var recent = visible.Where(i => !i.Pinned).Select(i => ToRow(i, now)).ToList();
        PinnedList.ItemsSource = pinned;
        RecentList.ItemsSource = recent;
        PinnedHeader.Visibility = PinnedList.Visibility = pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentHeader.Visibility = RecentList.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipEmpty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipEmpty.Text = _clipboard.Items.Count == 0 ? "Things you copy will show up here." : "Nothing matches.";
    }

    private Models.ClipRow ToRow(ClipItem item, DateTimeOffset now)
    {
        Microsoft.UI.Xaml.Media.Imaging.BitmapImage? thumb = null;
        if (item.Kind == ClipKind.Image && _clipboard.ImagePath(item) is { } path)
            thumb = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path)) { DecodePixelWidth = 80 };

        var preview = item.Kind == ClipKind.Image ? "Image" : item.Text.ReplaceLineEndings(" ");
        if (preview.Length > 300) preview = preview[..300];
        var kind = item.Kind switch { ClipKind.Link => "Link", ClipKind.Image => "Image", _ => "Text" };
        var meta = string.Join(" · ", new[] { kind, item.Source, Ago(item.Copied, now) }.Where(s => s.Length > 0));
        var glyph = item.Kind == ClipKind.Link ? "" : "";
        return new Models.ClipRow(item.Id, glyph, thumb, preview, meta, item.Pinned);
    }

    private static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hr ago";
        return when.ToString("d MMM");
    }

    private static string AppName(nint hwnd)
    {
        if (hwnd == 0) return "";
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            try
            {
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description;
            }
            catch (Exception)
            {
            }
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    private void RemindersFlyout_Opened(object sender, object e)
    {
        if (ReminderTime.SelectedTime is null)
        {
            var next = DateTime.Now.AddMinutes(30);
            ReminderTime.SelectedTime = new TimeSpan(next.Hour, next.Minute / 5 * 5, 0);
        }
        UpdateReminders();
        ReminderText.Focus(FocusState.Programmatic);
    }

    private void ReminderText_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || string.IsNullOrWhiteSpace(ReminderText.Text)) return;
        AddReminder(DateTimeOffset.Now.AddMinutes(20));
        e.Handled = true;
    }

    private void AddReminder_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.Now;
        var due = (sender as Button)?.Tag switch
        {
            "tomorrow" => new DateTimeOffset(now.Date.AddDays(1).AddHours(9), now.Offset),
            "at" => AtTime(ReminderTime.SelectedTime ?? now.TimeOfDay.Add(TimeSpan.FromMinutes(30))),
            string minutes => now.AddMinutes(int.Parse(minutes)),
            _ => now.AddMinutes(20),
        };
        AddReminder(due);
    }

    private static DateTimeOffset AtTime(TimeSpan time)
    {
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Date + time, now.Offset);
        return today > now ? today : today.AddDays(1);
    }

    private void AddReminder(DateTimeOffset due)
    {
        _reminders.Add(ReminderText.Text, due);
        ReminderText.Text = "";
    }

    private void CompleteReminder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string id) _reminders.Remove(id);
    }

    private void DeleteReminder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string id) _reminders.Remove(id);
    }

    private void OnReminderAction(string action, string id)
    {
        if (action == "snooze") _reminders.Snooze(id, TimeSpan.FromMinutes(RailSettings.Current.SnoozeMinutes));
        else if (action == "done") _reminders.Remove(id);
    }

    private void UpdateReminders()
    {
        var now = DateTimeOffset.Now;
        var upcoming = _reminders.Upcoming;
        RemindersList.ItemsSource = upcoming
            .Select(r => new Models.ReminderRow(r.Id, r.Text, DescribeDue(r.Due, now), r.Due <= now))
            .ToList();
        RemindersEmpty.Visibility = upcoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemindersCount.Text = upcoming.Count == 0 ? "" : $"{upcoming.Count} upcoming";

        var next = upcoming.FirstOrDefault();
        var left = next?.Due - now;
        RemindersBadge.Visibility = next is not null && left < TimeSpan.FromDays(1) ? Visibility.Visible : Visibility.Collapsed;
        RemindersBadgeText.Text = left switch
        {
            null => "",
            { } t when t <= TimeSpan.Zero => "now",
            { } t when t.TotalMinutes < 60 => $"{(int)Math.Ceiling(t.TotalMinutes)}m",
            { } t => $"{(int)Math.Round(t.TotalHours)}h",
        };
        ToolTipService.SetToolTip(RemindersButton, next is null ? "Reminders" : $"{next.Text} · {DescribeDue(next.Due, now)}");
    }

    private static string DescribeDue(DateTimeOffset due, DateTimeOffset now)
    {
        var left = due - now;
        if (left <= TimeSpan.Zero) return "Due now";
        if (left.TotalMinutes < 60) return $"in {(int)Math.Ceiling(left.TotalMinutes)} min";
        if (due.Date == now.Date) return $"today, {due:h:mm tt}";
        if (due.Date == now.Date.AddDays(1)) return $"tomorrow, {due:h:mm tt}";
        return $"{due:ddd d MMM, h:mm tt}";
    }

    private static string FormatLeft(TimeSpan t) =>
        t.TotalMinutes >= 60 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{(int)Math.Ceiling(t.TotalMinutes)} min";

    private static (double Width, double Height) Fit(double maxWidth, double maxHeight, double aspect) =>
        aspect >= maxWidth / maxHeight
            ? (Math.Round(maxWidth), Math.Round(maxWidth / aspect))
            : (Math.Round(maxHeight * aspect), Math.Round(maxHeight));

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();

    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();

    private async void Previous_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private void ApplyThickness(double railWidth)
    {
        var slot = Math.Max(24, Math.Round(railWidth - 8));
        foreach (var panel in new[] { TopSlots, BottomSlots })
        {
            foreach (var child in panel.Children)
            {
                switch (child)
                {
                    case Button button:
                        button.Width = button.Height = slot;
                        if (button.Content is Grid art && art == ArtHost)
                        {
                            art.Width = art.Height = Math.Round(slot * 0.85);
                            SizeArtwork();
                        }
                        else if ((button.Content as Viewbox ?? (button.Content as Grid)?.Children.OfType<Viewbox>().FirstOrDefault()) is { } icon)
                        {
                            icon.Width = icon.Height = Math.Round(slot * (icon.Tag is "logo" ? 0.6 : 0.5));
                        }
                        break;
                    case Rectangle sep when _dock.IsHorizontal:
                        sep.Width = 1;
                        sep.Height = Math.Round(slot * 0.6);
                        sep.Margin = new Thickness(4, 0, 4, 0);
                        break;
                    case Rectangle sep:
                        sep.Width = Math.Round(slot * 0.6);
                        sep.Height = 1;
                        sep.Margin = new Thickness(0, 4, 0, 4);
                        break;
                }
            }
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Close();
}
