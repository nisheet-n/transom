using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Transom.Native;
using Transom.Models;
using Transom.Services;
using Windows.ApplicationModel;
using Windows.Graphics;
using Windows.Storage;
using Windows.UI;

namespace Transom;

public sealed partial class SettingsWindow : Window
{
    private const string StartupTaskId = "TransomStartup";
    private static readonly int[] ClipLimits = [25, 50, 100, 200];
    private static readonly int[] SnoozeOptions = [5, 10, 15, 30];

    private static readonly (string Id, string Name, string Glyph)[] Tools =
    [
        ("media", "Media", ""),
        ("meeting", "Call controls (during calls)", ""),
        ("volume", "Volume", ""),
        ("ai", "Selected text and AI", ""),
        ("clipboard", "Clipboard history", ""),
        ("convert", "Convert", ""),
        ("notes", "Quick notes", ""),
        ("capture", "Capture", ""),
        ("shelf", "Shelf", ""),
        ("awake", "Keep awake", ""),
        ("reminders", "Reminders", ""),
    ];

    private const double NavItemHeight = 36, NavWidth = 236, GroupItemHeight = 32, GroupLabelHeight = 18;
    private static readonly int[] VolumeSteps = [1, 2, 5, 10];

    private static readonly (string Id, string Name, string Glyph)[] AppSections =
    [
        ("general", "General", ""),
        ("appearance", "Appearance", ""),
        ("dock", "Dock", ""),
        ("tools", "Tools", ""),
        ("privacy", "Privacy", ""),
    ];

    private readonly RailSettings _settings = RailSettings.Current;
    private readonly Dictionary<string, Button> _navButtons = [];
    private readonly List<ToggleSwitch> _toolSwitches = [];
    private readonly TextBlock _groupLabel = new() { Text = "Transom", Opacity = 0.7, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
    private Dictionary<string, FrameworkElement> _pages = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _layoutTimer;
    private string _selected = "general";
    private bool _syncing, _closed;
    private bool _loading = true;
    private StartupTask? _startup;

    public List<StyleOption> Styles { get; } =
    [
        new(BackgroundStyle.MatchTaskbar, "Match taskbar", "Same glass and colors as your taskbar.", Hex(0x2A2A2C), Hex(0x1C1C1E)),
        new(BackgroundStyle.WallpaperSubtle, "Wallpaper tint", "Soft colors taken from your wallpaper.", Hex(0x3A3248), Hex(0x2A3A3A)),
        new(BackgroundStyle.WallpaperDeep, "Wallpaper tint, deep", "Darker, richer wallpaper colors.", Hex(0x1E1828), Hex(0x10201E)),
        new(BackgroundStyle.Frosted, "Frosted glass", "More see-through, softly blurred.", Hex(0x6A7380), Hex(0x3C4250)),
        new(BackgroundStyle.Accent, "Accent color", "Your Windows accent color, frosted.", Hex(0x2F5BBF), Hex(0x1C3A80)),
        new(BackgroundStyle.Preset, "Preset color", "Pick from a set of colors below.", Hex(0x3A2A14), Hex(0x16291F)),
        new(BackgroundStyle.Custom, "Custom color", "Any color, with your own opacity.", Hex(0x7A3FB0), Hex(0xE0782A)),
        new(BackgroundStyle.Solid, "Solid", "No transparency. Lightest on battery.", Hex(0x1C1C1C), Hex(0x1C1C1C)),
    ];

    public List<PresetOption> Presets { get; } = [.. RailSettings.Presets.Select(p => new PresetOption(p.Name, p.Color))];

    public SettingsWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1040, 860));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Maximize();

        StyleGrid.SelectedItem = Styles.First(s => s.Style == _settings.Background);
        PresetGrid.SelectedItem = Presets.FirstOrDefault(p => p.Color == _settings.PresetColor);
        CustomPicker.Color = _settings.CustomColor;
        OpacitySlider.Value = Math.Round(_settings.CustomOpacity * 100);
        ThemeBox.SelectedIndex = (int)_settings.Theme;
        BatteryToggle.IsOn = _settings.SolidOnBatterySaver;

        NavToggle.IsOn = _settings.KeyboardNavigation;
        DockGrid.SelectedItem = DockGrid.Items.OfType<GridViewItem>().FirstOrDefault(i => (string)i.Tag == _settings.Dock) ?? DockGrid.Items[0];
        BuildToolToggles();
        ClipLimitBox.SelectedIndex = Math.Max(0, Array.IndexOf(ClipLimits, _settings.ClipboardLimit));
        ClearOnQuitToggle.IsOn = _settings.ClearClipboardOnQuit;
        SnoozeBox.SelectedIndex = Math.Max(0, Array.IndexOf(SnoozeOptions, _settings.SnoozeMinutes));
        AwakeBox.SelectedIndex = Math.Clamp(_settings.AwakeDefault, 0, 3);
        HideCaptureToggle.IsOn = _settings.HideFromCapture;
        SelectionToggle.IsOn = _settings.WatchSelection;
        VersionText.Text = $"Version {AppVersion()}";

        VolumeStepBox.SelectedIndex = Math.Max(0, Array.IndexOf(VolumeSteps, _settings.VolumeStep));
        IconSizeBox.SelectedIndex = Math.Clamp(_settings.IconSize, 0, 2);
        BadgeColorToggle.IsOn = _settings.BadgesMatchIcons;
        SeparatorsToggle.IsOn = _settings.ShowSeparators;
        LiveDetailsToggle.IsOn = _settings.LiveDetails;
        BuildIconSwatches();

        _pages = new()
        {
            ["general"] = GeneralPage, ["appearance"] = AppearancePage, ["dock"] = DockPage, ["tools"] = ToolsPage,
            ["privacy"] = PrivacyPage, ["about"] = AboutPage, ["media"] = MediaPage, ["volume"] = VolumePage, ["ai"] = AiPage,
            ["clipboard"] = ClipboardPage, ["convert"] = ConvertPage, ["notes"] = NotesPage, ["shelf"] = ShelfPage,
            ["capture"] = CapturePage, ["awake"] = AwakePage, ["reminders"] = RemindersPage,
        };
        AddShowOnRailCards();
        BuildNav();
        Select("general");
        UpdateSections();
        _loading = false;
        _ = LoadStartupAsync();

        _layoutTimer = DispatcherQueue.CreateTimer();
        _layoutTimer.Interval = TimeSpan.FromMilliseconds(120);
        _layoutTimer.IsRepeating = false;
        _layoutTimer.Tick += (_, _) => LayoutNav();
        Activated += (_, _) => QueueLayout();
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange) QueueLayout();
        };
        NavColumn.SizeChanged += (_, _) => QueueLayout();
        if (App.Window is RailWindow rail) rail.SlotsMoved += QueueLayout;
        _settings.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _layoutTimer.Stop();
            _settings.Changed -= OnSettingsChanged;
            if (App.Window is RailWindow r) r.SlotsMoved -= QueueLayout;
        };
    }

    private void OnSettingsChanged()
    {
        SyncToolSwitches();
        QueueLayout();
    }

    private void QueueLayout()
    {
        if (_closed) return;
        _layoutTimer?.Stop();
        _layoutTimer?.Start();
    }

    private void Select(string id)
    {
        _selected = id;
        foreach (var (key, page) in _pages) page.Visibility = Show(key == id);
        foreach (var (key, button) in _navButtons)
        {
            var selected = key == id;
            if (selected) button.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
            else button.ClearValue(Control.BackgroundProperty);
            if (button.Content is StackPanel panel && panel.Children[0] is Rectangle indicator) indicator.Opacity = selected ? 1 : 0;
        }
    }

    private void BuildNav()
    {
        foreach (var (id, name, glyph) in Tools.Where(t => t.Id != "meeting").Concat(AppSections).Append(("about", "About", "")))
        {
            var indicator = new Rectangle
            {
                Width = 3,
                Height = 16,
                RadiusX = 1.5,
                RadiusY = 1.5,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                Opacity = 0,
            };
            var button = new Button
            {
                Tag = id,
                Width = NavWidth,
                Height = NavItemHeight,
                Padding = new Thickness(4, 0, 8, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { indicator, ToolIcon(id, glyph), new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center } },
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => Select(id);
            _navButtons[id] = button;
        }
    }

    // Same icons as the rail: Volume is the rail's own drawing, the rest come from the icon font.
    private static FrameworkElement ToolIcon(string id, string glyph)
    {
        if (id != "volume") return new FontIcon { Glyph = glyph, FontSize = 16 };
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), VolumeDrawing),
            Stroke = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        return new Viewbox { Width = 16, Height = 16, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
    }

    private const string VolumeDrawing = "M4 9 L8 9 L13 5 L13 19 L8 15 L4 15 Z M16.5 9 A4 4 0 0 1 16.5 15 M19 6.5 A7.5 7.5 0 0 1 19 17.5";

    private void LayoutNav()
    {
        if (_closed || Content?.XamlRoot is null) return;
        foreach (var element in _navButtons.Values.Cast<UIElement>().Append(_groupLabel))
        {
            if (VisualTreeHelper.GetParent(element) is Panel parent) parent.Children.Remove(element);
        }

        var aligned = AlignNav();
        foreach (var (id, button) in _navButtons) ShowNavIcon(button, !aligned || AppSections.Any(a => a.Id == id));
        NavCanvas.Visibility = Show(aligned);
        NavScroll.Visibility = Show(!aligned);
        if (aligned) return;

        foreach (var (id, _, _) in Tools.Where(t => t.Id != "meeting")) NavStack.Children.Add(Indented(_navButtons[id]));
        _groupLabel.Margin = new Thickness(16, 14, 0, 4);
        NavStack.Children.Add(_groupLabel);
        foreach (var (id, _, _) in AppSections) NavStack.Children.Add(Indented(_navButtons[id]));
        NavStack.Children.Add(Indented(_navButtons["about"]));
    }

    // Beside a side-docked rail the rail itself shows each tool's icon, so those items are text only;
    // app-wide sections keep their icons.
    private static void ShowNavIcon(Button button, bool show)
    {
        if (button.Content is StackPanel panel && panel.Children.Count > 1) panel.Children[1].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Button Indented(Button button)
    {
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Height = NavItemHeight;
        return button;
    }

    // When Settings sits right beside a vertical rail, each tool's section is placed level with
    // its icon, About with the Transom logo, and the app-wide sections in the rail's empty middle.
    // Anything that doesn't fit (horizontal dock, Settings moved away, a short screen) falls back
    // to a plain list.
    private bool AlignNav()
    {
        if (App.Window is not RailWindow rail || !rail.IsVertical) return false;
        var slots = rail.SlotBounds();
        if (slots.Count == 0) return false;

        var scale = Content.XamlRoot.RasterizationScale;
        var origin = new Win32.POINT();
        Win32.ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref origin);
        if (Math.Abs(origin.X - slots.Max(s => s.Bounds.Right)) > 40 * scale) return false;

        var height = NavColumn.ActualHeight;
        var placed = new List<(UIElement Element, double Top, double Height)>();
        double tools = 0, bottomStart = height;
        var bottomIds = new[] { "shelf", "awake", "reminders", "about" };
        foreach (var (id, bounds) in slots)
        {
            var itemHeight = Math.Min(NavItemHeight, bounds.Height / scale);
            var top = Math.Max(0, (bounds.Top + bounds.Height / 2 - origin.Y) / scale - itemHeight / 2);
            if (top + itemHeight > height) return false;
            placed.Add((_navButtons[id], top, itemHeight));
            if (bottomIds.Contains(id)) bottomStart = Math.Min(bottomStart, top);
            else tools = Math.Max(tools, top + itemHeight);
        }

        var groupTop = tools + 6;
        if (groupTop + GroupLabelHeight + AppSections.Length * GroupItemHeight > bottomStart - 6) return false;
        placed.Add((_groupLabel, groupTop, GroupLabelHeight));
        for (var i = 0; i < AppSections.Length; i++) placed.Add((_navButtons[AppSections[i].Id], groupTop + GroupLabelHeight + i * GroupItemHeight, GroupItemHeight - 2));

        _groupLabel.Margin = new Thickness(0);
        foreach (var (element, top, itemHeight) in placed)
        {
            if (element is Button button)
            {
                button.Margin = new Thickness(0);
                button.Height = itemHeight;
            }
            Canvas.SetLeft(element, element == _groupLabel ? 14 : 4);
            Canvas.SetTop(element, top);
            NavCanvas.Children.Add(element);
        }
        return true;
    }

    private void AddShowOnRailCards()
    {
        foreach (var (page, ids) in new (string, string[])[]
        {
            ("media", ["media", "meeting"]), ("volume", ["volume"]), ("ai", ["ai"]), ("clipboard", ["clipboard"]), ("convert", ["convert"]),
            ("notes", ["notes"]), ("shelf", ["shelf"]), ("capture", ["capture"]), ("awake", ["awake"]), ("reminders", ["reminders"]),
        })
        {
            var panel = (StackPanel)_pages[page];
            var index = 2;
            foreach (var id in ids)
            {
                var (title, hint) = id == "meeting"
                    ? ("Call controls", "Appear above Media while an app is using your microphone, with mute, camera and leave.")
                    : ("Show on the rail", "Turn this off to hide the tool. Its settings stay here.");
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = title },
                        new TextBlock { Text = hint, Style = (Style)RootGrid.Resources["Hint"] },
                    },
                });
                var toggle = ToolSwitch(id);
                Grid.SetColumn(toggle, 1);
                row.Children.Add(toggle);
                panel.Children.Insert(index++, new Border { Style = (Style)RootGrid.Resources["Card"], Child = row });
            }
        }
    }

    private ToggleSwitch ToolSwitch(string id)
    {
        var toggle = new ToggleSwitch { Tag = id, MinWidth = 0, IsOn = !_settings.HiddenTools.Contains(id) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, $"Show {Tools.FirstOrDefault(t => t.Id == id).Name ?? id} on the rail");
        toggle.Toggled += (_, _) =>
        {
            if (_syncing) return;
            _settings.HiddenTools.Remove(id);
            if (!toggle.IsOn) _settings.HiddenTools.Add(id);
            Save();
        };
        _toolSwitches.Add(toggle);
        return toggle;
    }

    private void SyncToolSwitches()
    {
        _syncing = true;
        foreach (var toggle in _toolSwitches) toggle.IsOn = !_settings.HiddenTools.Contains((string)toggle.Tag);
        _syncing = false;
    }

    private static readonly (string Value, string Name)[] IconColors =
    [
        ("auto", "Automatic"), ("accent", "Accent color"), ("#FFFFFFFF", "White"), ("#FF34D8A9", "Mint"), ("#FF4CC2FF", "Sky"),
        ("#FFB4A7FF", "Lavender"), ("#FFFF8A80", "Coral"), ("#FFFFC857", "Amber"), ("#FFFF7AB6", "Rose"),
    ];

    private void BuildIconSwatches()
    {
        var dark = Microsoft.UI.ColorHelper.FromArgb(255, 0x20, 0x20, 0x20);
        foreach (var (value, name) in IconColors)
        {
            var color = RailWindow.IconColorFor(value, isLight: false);
            var swatch = new Button
            {
                Tag = value,
                Width = 34,
                Height = 34,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(17),
                BorderThickness = new Thickness(2),
                Background = value == "auto" ? new SolidColorBrush(dark) : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, color.R, color.G, color.B)),
                Content = value == "auto" ? new FontIcon { Glyph = "", FontSize = 14, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } : null,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, $"Icon color: {name}");
            ToolTipService.SetToolTip(swatch, name);
            swatch.Click += (_, _) =>
            {
                _settings.IconColor = value;
                Save();
                UpdateIconSwatches();
            };
            IconColorSwatches.Children.Add(swatch);
        }
        UpdateIconSwatches();
    }

    private void UpdateIconSwatches()
    {
        foreach (var swatch in IconColorSwatches.Children.OfType<Button>())
        {
            swatch.BorderBrush = (string)swatch.Tag == _settings.IconColor
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        }
    }

    private void IconSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IconSizeBox.SelectedIndex >= 0) _settings.IconSize = IconSizeBox.SelectedIndex;
        Save();
    }

    private void BadgeColorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.BadgesMatchIcons = BadgeColorToggle.IsOn;
        Save();
    }

    private void SeparatorsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.ShowSeparators = SeparatorsToggle.IsOn;
        Save();
    }

    private void LiveDetailsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.LiveDetails = LiveDetailsToggle.IsOn;
        Save();
    }

    private void VolumeStepBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VolumeStepBox.SelectedIndex >= 0) _settings.VolumeStep = VolumeSteps[VolumeStepBox.SelectedIndex];
        Save();
    }

    private void UpdateSections()
    {
        PresetPanel.Visibility = Show(_settings.Background == BackgroundStyle.Preset);
        CustomPanel.Visibility = Show(_settings.Background == BackgroundStyle.Custom);
    }

    private void Save()
    {
        if (_loading) return;
        _settings.Save();
        UpdateSections();
    }

    private async Task LoadStartupAsync()
    {
        try
        {
            _startup = await StartupTask.GetAsync(StartupTaskId);
            ShowStartupState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            StartupToggle.IsEnabled = false;
            StartupHint.Text = "Available when Transom is installed.";
        }
    }

    private void ShowStartupState()
    {
        if (_startup is null) return;
        _loading = true;
        StartupToggle.IsOn = _startup.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        _loading = false;
        StartupToggle.IsEnabled = _startup.State is not (StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy);
        StartupHint.Text = _startup.State switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Task Manager › Startup apps. Turn it back on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by your organization.",
            _ => "Open Transom automatically when you sign in.",
        };
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _startup is null) return;
        try
        {
            if (StartupToggle.IsOn) await _startup.RequestEnableAsync();
            else _startup.Disable();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        ShowStartupState();
    }

    private void NavToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.KeyboardNavigation = NavToggle.IsOn;
        Save();
    }

    private void StyleGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StyleGrid.SelectedItem is StyleOption option) _settings.Background = option.Style;
        Save();
    }

    private void PresetGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetGrid.SelectedItem is PresetOption option) _settings.PresetColor = option.Color;
        Save();
    }

    private void CustomPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        _settings.CustomColor = args.NewColor;
        Save();
    }

    private void OpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _settings.CustomOpacity = e.NewValue / 100;
        Save();
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedIndex >= 0) _settings.Theme = (RailTheme)ThemeBox.SelectedIndex;
        Save();
    }

    private void BatteryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.SolidOnBatterySaver = BatteryToggle.IsOn;
        Save();
    }

    private void DockGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DockGrid.SelectedItem is GridViewItem { Tag: string edge }) _settings.Dock = edge;
        Save();
    }

    private void BuildToolToggles()
    {
        foreach (var (id, name, glyph) in Tools)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = ToolIcon(id, glyph);
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            var toggle = ToolSwitch(id);
            toggle.OnContent = toggle.OffContent = "";
            Grid.SetColumn(label, 1);
            Grid.SetColumn(toggle, 2);
            row.Children.Add(icon);
            row.Children.Add(label);
            row.Children.Add(toggle);
            ToolToggles.Children.Add(row);
        }
    }

    private void ClipLimitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipLimitBox.SelectedIndex >= 0) _settings.ClipboardLimit = ClipLimits[ClipLimitBox.SelectedIndex];
        Save();
    }

    private void ClearOnQuitToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.ClearClipboardOnQuit = ClearOnQuitToggle.IsOn;
        Save();
    }

    private void SnoozeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SnoozeBox.SelectedIndex >= 0) _settings.SnoozeMinutes = SnoozeOptions[SnoozeBox.SelectedIndex];
        Save();
    }

    private void AwakeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AwakeBox.SelectedIndex >= 0) _settings.AwakeDefault = AwakeBox.SelectedIndex;
        Save();
    }

    private void HideCaptureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.HideFromCapture = HideCaptureToggle.IsOn;
        Save();
    }

    private void SelectionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.WatchSelection = SelectionToggle.IsOn;
        Save();
    }

    private void OpenScreenshots_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Transom");
        Directory.CreateDirectory(folder);
        OpenFolder(folder);
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(ApplicationData.Current.LocalFolder.Path);

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static string AppVersion()
    {
        try
        {
            var v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "dev";
        }
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static Color Hex(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
