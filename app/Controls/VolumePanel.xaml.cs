using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Transom.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Transom.Controls;

public sealed partial class VolumePanel : UserControl
{
    private const string MutedGlyph = "", SpeakerGlyph = "";

    private readonly Dictionary<uint, (Slider Slider, Button Mute, FontIcon Icon, TextBlock Value)> _rows = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refresh;
    private AudioService? _audio;
    private bool _updating;

    public VolumePanel()
    {
        InitializeComponent();
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(2);
        _refresh.Tick += (_, _) => Refresh();
    }

    public void Attach(AudioService audio) => _audio = audio;

    public void Opened()
    {
        Refresh();
        _refresh.Start();
    }

    public void Closed() => _refresh.Stop();

    // Rebuilds rows only when apps start or stop playing, so sliders aren't swapped out mid-drag.
    private void Refresh()
    {
        if (_audio is null) return;
        _updating = true;
        var master = _audio.MasterVolume ?? 0;
        var masterMuted = _audio.MasterMuted;
        if (!MasterSlider.FocusState.HasFlag(FocusState.Pointer)) MasterSlider.Value = Math.Round(master * 100);
        MasterValue.Text = $"{Math.Round(master * 100)}";
        MasterIcon.Glyph = masterMuted ? MutedGlyph : SpeakerGlyph;

        var apps = _audio.Apps();
        if (!apps.Select(a => a.ProcessId).SequenceEqual(_rows.Keys))
        {
            AppRows.Children.Clear();
            _rows.Clear();
            foreach (var app in apps) AppRows.Children.Add(CreateRow(app));
        }
        foreach (var app in apps)
        {
            if (!_rows.TryGetValue(app.ProcessId, out var row)) continue;
            row.Slider.Value = Math.Round(app.Volume * 100);
            row.Value.Text = $"{Math.Round(app.Volume * 100)}";
            row.Icon.Glyph = app.Muted ? MutedGlyph : SpeakerGlyph;
        }
        Empty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _updating = false;
    }

    private Grid CreateRow(AudioApp app)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 4, 12, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        var image = new Image { Width = 20, Height = 20 };
        var icon = new FontIcon { FontSize = 14, Glyph = app.Muted ? MutedGlyph : SpeakerGlyph };
        var mute = new Button
        {
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new Grid { Children = { image, new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -6, -6), Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"], Child = icon } } },
        };
        AutomationProperties.SetName(mute, $"Mute {app.Name}");
        ToolTipService.SetToolTip(mute, "Mute or unmute");

        var name = new TextBlock { Text = app.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        var slider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = Math.Round(app.Volume * 100) };
        AutomationProperties.SetName(slider, $"{app.Name} volume");
        var value = new TextBlock
        {
            Text = $"{Math.Round(app.Volume * 100)}",
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        slider.ValueChanged += (_, e) =>
        {
            value.Text = $"{Math.Round(e.NewValue)}";
            if (!_updating) _audio?.SetAppVolume(app.ProcessId, (float)(e.NewValue / 100));
        };
        mute.Click += (_, _) =>
        {
            var muted = icon.Glyph != MutedGlyph;
            _audio?.SetAppMuted(app.ProcessId, muted);
            icon.Glyph = muted ? MutedGlyph : SpeakerGlyph;
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, slider } };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(value, 2);
        row.Children.Add(mute);
        row.Children.Add(text);
        row.Children.Add(value);
        _rows[app.ProcessId] = (slider, mute, icon, value);
        _ = LoadIconAsync(app, image);
        return row;
    }

    private static async Task LoadIconAsync(AudioApp app, Image image)
    {
        if (app.ExePath is null)
        {
            image.Source = null;
            return;
        }
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(app.ExePath);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            image.Source = bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void MasterSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        MasterValue.Text = $"{Math.Round(e.NewValue)}";
        if (!_updating) _audio?.MasterVolume = (float)(e.NewValue / 100);
    }

    private void MasterMute_Click(object sender, RoutedEventArgs e)
    {
        if (_audio is null) return;
        _audio.MasterMuted = !_audio.MasterMuted;
        MasterIcon.Glyph = _audio.MasterMuted ? MutedGlyph : SpeakerGlyph;
    }
}
