using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Transom.Models;
using Transom.Services;
using Windows.Graphics;
using Windows.UI;

namespace Transom;

public sealed partial class SettingsWindow : Window
{
    private readonly RailSettings _settings = RailSettings.Current;
    private bool _loading = true;

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
        AppWindow.Resize(new SizeInt32(1000, 900));

        StyleGrid.SelectedItem = Styles.First(s => s.Style == _settings.Background);
        PresetGrid.SelectedItem = Presets.FirstOrDefault(p => p.Color == _settings.PresetColor);
        CustomPicker.Color = _settings.CustomColor;
        OpacitySlider.Value = Math.Round(_settings.CustomOpacity * 100);
        ThemeBox.SelectedIndex = (int)_settings.Theme;
        BatteryToggle.IsOn = _settings.SolidOnBatterySaver;
        UpdateSections();
        _loading = false;
    }

    private void UpdateSections()
    {
        PresetPanel.Visibility = _settings.Background == BackgroundStyle.Preset ? Visibility.Visible : Visibility.Collapsed;
        CustomPanel.Visibility = _settings.Background == BackgroundStyle.Custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save()
    {
        if (_loading) return;
        _settings.Save();
        UpdateSections();
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

    private static Color Hex(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
