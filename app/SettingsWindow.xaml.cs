using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        ("ai", "Selected text and AI", ""),
        ("clipboard", "Clipboard history", ""),
        ("convert", "Convert", ""),
        ("notes", "Quick notes", ""),
        ("shelf", "Shelf", ""),
        ("capture", "Capture", ""),
        ("awake", "Keep awake", ""),
        ("reminders", "Reminders", ""),
    ];

    private readonly RailSettings _settings = RailSettings.Current;
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

        Nav.SelectedItem = GeneralItem;
        UpdateSections();
        _loading = false;
        _ = LoadStartupAsync();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        GeneralPage.Visibility = Show(tag == "general");
        AppearancePage.Visibility = Show(tag == "appearance");
        DockPage.Visibility = Show(tag == "dock");
        ToolsPage.Visibility = Show(tag == "tools");
        PrivacyPage.Visibility = Show(tag == "privacy");
        AboutPage.Visibility = Show(tag == "about");
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

            var icon = new FontIcon { Glyph = glyph, FontSize = 16 };
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            var toggle = new ToggleSwitch { IsOn = !_settings.HiddenTools.Contains(id), MinWidth = 0, OnContent = "", OffContent = "" };
            toggle.Toggled += (_, _) =>
            {
                _settings.HiddenTools.Remove(id);
                if (!toggle.IsOn) _settings.HiddenTools.Add(id);
                Save();
            };
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
