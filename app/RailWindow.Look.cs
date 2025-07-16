using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Transom.Services;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Transom;

// Icon and badge colors, and the live details that top and bottom docks show beside some slots.
public sealed partial class RailWindow
{
    private static readonly Dictionary<string, string> UnitPairs = new()
    {
        ["km"] = "mi", ["mi"] = "km", ["m"] = "ft", ["ft"] = "m", ["cm"] = "in", ["in"] = "cm", ["mm"] = "in", ["yd"] = "m",
        ["kg"] = "lb", ["lb"] = "kg", ["g"] = "oz", ["oz"] = "g", ["°C"] = "°F", ["°F"] = "°C", ["K"] = "°C",
        ["l"] = "gal", ["gal"] = "l", ["ml"] = "cup", ["cup"] = "ml", ["km/h"] = "mph", ["mph"] = "km/h",
        ["KB"] = "MB", ["MB"] = "GB", ["GB"] = "MB", ["TB"] = "GB",
    };

    private sealed class Expandable
    {
        public required Grid Host;
        public required FrameworkElement Content;
        public required FrameworkElement Card;
        public required SolidColorBrush Fill, Stroke;
        public double Width, Progress, FromWidth, FromProgress, ToWidth, ToProgress;
        public long Started;
        public bool Animating;
    }

    private const double ExpandSeconds = 0.22;
    private readonly List<Expandable> _expandables = [];
    private readonly Dictionary<Button, (TextBlock Label, Expandable Widget)> _liveLabels = [];
    private Expandable? _clipWidget, _shelfWidget;
    private bool _rendering;
    private double _slotLength = 34;

    public static Color IconColorFor(string setting, bool isLight)
    {
        if (setting == "accent")
        {
            var ui = new UISettings();
            return ui.GetColorValue(isLight ? UIColorType.AccentDark1 : UIColorType.AccentLight2);
        }
        if (setting.StartsWith('#') && uint.TryParse(setting[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        return isLight ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF);
    }

    private void ApplyIconLook()
    {
        var settings = RailSettings.Current;
        var color = IconColorFor(settings.IconColor, _isLight);
        ((SolidColorBrush)Root.Resources["RailIconBrush"]).Color = color;
        _groupFill.Color = _isLight ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
        _groupStroke.Color = _isLight ? Color.FromArgb(0x17, 0, 0, 0) : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
        _brightText.Color = _isLight ? Color.FromArgb(0xE4, 0, 0, 0) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        var solid = Color.FromArgb(255, color.R, color.G, color.B);
        var luminance = (0.299 * solid.R + 0.587 * solid.G + 0.114 * solid.B) / 255;
        Brush badge = settings.BadgesMatchIcons && settings.IconColor != "auto"
            ? new SolidColorBrush(solid)
            : (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        Brush badgeText = settings.BadgesMatchIcons && settings.IconColor != "auto"
            ? new SolidColorBrush(luminance > 0.6 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255))
            : (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        foreach (var border in new[] { ConvertBadge, ShelfBadge, AwakeBadge, RemindersBadge })
        {
            border.Background = badge;
            if (border.Child is TextBlock text) text.Foreground = badgeText;
        }
        SelectionBadge.Fill = badge;
        foreach (var widget in _expandables)
        {
            widget.Fill.Color = _groupFill.Color;
            widget.Stroke.Color = _groupStroke.Color;
        }
    }

    private Expandable NewExpandable(FrameworkElement content, FrameworkElement card)
    {
        content.HorizontalAlignment = HorizontalAlignment.Left;
        var host = new Grid { Width = 0, Visibility = Visibility.Collapsed, Clip = new RectangleGeometry(), Children = { content } };
        var widget = new Expandable
        {
            Host = host,
            Content = content,
            Card = card,
            Fill = new SolidColorBrush(_groupFill.Color) { Opacity = 0 },
            Stroke = new SolidColorBrush(_groupStroke.Color) { Opacity = 0 },
        };
        _expandables.Add(widget);
        return widget;
    }

    private void CreateLiveLabels()
    {
        foreach (var button in new[] { ArtButton, ConvertButton, AwakeButton, RemindersButton })
        {
            var original = (FrameworkElement)button.Content;
            button.Content = null;
            original.HorizontalAlignment = HorizontalAlignment.Center;
            var label = new TextBlock
            {
                FontSize = 12,
                MaxWidth = 220,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Root.Resources["RailIconBrush"],
            };
            var widget = NewExpandable(label, button);
            button.Background = widget.Fill;
            button.BorderBrush = widget.Stroke;
            var iconBox = new Grid { Tag = "iconbox", Children = { original } };
            button.Content = new StackPanel { Orientation = Orientation.Horizontal, Tag = "live", Children = { iconBox, widget.Host } };
            _liveLabels[button] = (label, widget);
        }
    }

    private static UIElement? SlotContent(Button button) =>
        button.Content is StackPanel { Tag: "live" } live && live.Children[0] is Grid { Tag: "iconbox" } box ? box.Children[0] : button.Content as UIElement;

    // On a top or bottom dock there's room beside the icon, so these slots show their value
    // (a conversion, the song, time left) without opening anything. Side docks keep icons only.
    private void UpdateLiveDetails()
    {
        if (_liveLabels.Count == 0 || _dock is null) return;
        var horizontal = _dock.IsHorizontal;
        var enabled = horizontal && RailSettings.Current.LiveDetails;
        foreach (var (button, (label, widget)) in _liveLabels)
        {
            var (lead, main, tail) = enabled ? LiveText(button) : ("", "", "");
            var show = main.Length > 0;
            if (show)
            {
                label.Inlines.Clear();
                if (lead.Length > 0) label.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = lead });
                label.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = main, Foreground = _brightText });
                if (tail.Length > 0) label.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = tail });
            }
            if (button.Content is StackPanel { Children: [Grid iconBox, ..] })
                iconBox.Width = horizontal ? _slotLength - button.BorderThickness.Left - button.BorderThickness.Right : double.NaN;
            if (horizontal) button.Width = double.NaN;
            Expand(widget, show);
            var badge = button == ConvertButton ? ConvertBadge : button == AwakeButton ? AwakeBadge : button == RemindersButton ? RemindersBadge : null;
            if (badge is not null && show) badge.Visibility = Visibility.Collapsed;
        }
        UpdateRailChips();
    }

    // The widget's own width is what animates, so it pushes its neighbours along frame by frame
    // instead of jumping open and overlapping them. A new value while open eases to the new width.
    private void Expand(Expandable? widget, bool open)
    {
        if (widget is null) return;
        double target = 0;
        if (open)
        {
            widget.Host.Visibility = Visibility.Visible;
            var content = widget.Content;
            content.ClearValue(FrameworkElement.WidthProperty);
            content.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            target = Math.Ceiling(content.DesiredSize.Width);
            content.Width = Math.Max(0, target - content.Margin.Left - content.Margin.Right);
        }
        double progress = open ? 1 : 0;
        var (currentWidth, currentProgress) = widget.Animating ? (widget.ToWidth, widget.ToProgress) : (widget.Width, widget.Progress);
        if (Math.Abs(target - currentWidth) < 0.5 && progress == currentProgress) return;

        widget.FromWidth = widget.Width;
        widget.FromProgress = widget.Progress;
        widget.ToWidth = target;
        widget.ToProgress = progress;
        widget.Started = System.Diagnostics.Stopwatch.GetTimestamp();
        widget.Animating = true;
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnExpandFrame;
    }

    private void OnExpandFrame(object? sender, object e)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var running = false;
        foreach (var widget in _expandables)
        {
            if (!widget.Animating) continue;
            var t = Math.Clamp((now - widget.Started) / (double)System.Diagnostics.Stopwatch.Frequency / ExpandSeconds, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            ApplyExpansion(widget,
                widget.FromWidth + (widget.ToWidth - widget.FromWidth) * eased,
                widget.FromProgress + (widget.ToProgress - widget.FromProgress) * eased);
            if (t < 1)
            {
                running = true;
                continue;
            }
            widget.Animating = false;
            if (widget.ToProgress == 0) widget.Host.Visibility = Visibility.Collapsed;
        }
        if (running) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnExpandFrame;
    }

    private static void ApplyExpansion(Expandable widget, double width, double progress)
    {
        widget.Width = width;
        widget.Progress = progress;
        widget.Host.Width = width;
        ((RectangleGeometry)widget.Host.Clip).Rect = new Windows.Foundation.Rect(0, 0, width, 400);
        widget.Content.Opacity = progress * progress;
        widget.Fill.Opacity = progress;
        widget.Stroke.Opacity = progress;
        widget.Card.Margin = new Thickness(4 * progress, 0, 4 * progress, 0);
        if (widget.Card is Border border) border.BorderThickness = new Thickness(progress);
    }

    private (string Lead, string Main, string Tail) LiveText(Button button)
    {
        if (button == ArtButton)
        {
            if (!_media.HasSession || string.IsNullOrEmpty(_media.Title)) return ("", "", "");
            return ("", _media.Title, string.IsNullOrEmpty(_media.Artist) ? "" : $" · {_media.Artist}");
        }
        if (button == ConvertButton) return _detected is { } detected ? InlineConversion(detected) : ("", "", "");
        if (button == AwakeButton) return !_awake.IsOn ? ("", "", "") : ("", _awake.Remaining is { } left ? FormatLeft(left) : "On", "");
        if (button == RemindersButton)
        {
            var next = _reminders.Upcoming.FirstOrDefault();
            if (next is null) return ("", "", "");
            var left = next.Due - DateTimeOffset.Now;
            if (left > TimeSpan.FromDays(1)) return ("", "", "");
            return left <= TimeSpan.Zero ? ("", "Now", "") : ("in ", FormatLeft(left), "");
        }
        return ("", "", "");
    }

    private readonly StackPanel _clipChips = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _shelfChips = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private string _clipChipsKey = "", _shelfChipsKey = "";

    private readonly SolidColorBrush _groupFill = new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _groupStroke = new(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _brightText = new(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    // Clipboard and Shelf each sit in a wrapper with their chips, so an expanded widget can be
    // shaded as one piece. With nothing expanded the wrapper is invisible.
    private void CreateRailChips()
    {
        _clipWidget = Group(ClipboardButton, _clipChips);
        _shelfWidget = Group(ShelfButton, _shelfChips);
    }

    private Expandable Group(Button button, StackPanel chips)
    {
        var panel = TopSlots.Children.Contains(button) ? TopSlots : BottomSlots;
        var index = panel.Children.IndexOf(button);
        panel.Children.RemoveAt(index);
        var divider = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 1, Height = 16, Opacity = 0.35, Fill = (Brush)Root.Resources["RailIconBrush"], VerticalAlignment = VerticalAlignment.Center };
        var contents = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { divider, chips },
        };
        var group = new Border
        {
            Tag = "group",
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var widget = NewExpandable(contents, group);
        group.Background = widget.Fill;
        group.BorderBrush = widget.Stroke;
        group.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { button, widget.Host } };
        panel.Children.Insert(index, group);
        return widget;
    }

    private static IEnumerable<UIElement> Slots(Panel panel) =>
        panel.Children.SelectMany(child => child is Border { Tag: "group", Child: Panel inner } ? [inner.Children[0]] : (IEnumerable<UIElement>)[child]);

    // Top and bottom docks also show up to three pinned clipboard items (newest pin first; click
    // to paste) and the first few shelf files (click to open, drag out to drop anywhere).
    private void UpdateRailChips()
    {
        if (_dock is null) return;
        var settings = RailSettings.Current;
        var on = _dock.IsHorizontal && settings.LiveDetails;
        var height = Math.Round(_slotLength * 0.76);

        var pins = on && !settings.HiddenTools.Contains("clipboard")
            ? _clipboard.Items.Where(i => i.Pinned).OrderByDescending(i => i.PinnedAt).Take(3).ToList()
            : [];
        var clipKey = $"{height}|{string.Join("|", pins.Select(p => p.Id))}";
        if (clipKey != _clipChipsKey && pins.Count > 0)
        {
            _clipChipsKey = clipKey;
            _clipChips.Children.Clear();
            foreach (var item in pins) _clipChips.Children.Add(ClipChip(item, height));
        }
        Expand(_clipWidget, pins.Count > 0);

        var all = on && !settings.HiddenTools.Contains("shelf") ? _shelf.Items : [];
        var shown = all.Take(4).ToList();
        var shelfKey = $"{height}|{all.Count}|{string.Join("|", shown.Select(s => s.Path))}";
        if (shelfKey != _shelfChipsKey && shown.Count > 0)
        {
            _shelfChipsKey = shelfKey;
            _shelfChips.Children.Clear();
            foreach (var item in shown) _shelfChips.Children.Add(ShelfChip(item, height));
            if (all.Count > shown.Count)
            {
                var more = new Button { Height = height, MinWidth = height, Padding = new Thickness(6, 0, 6, 0), Content = new TextBlock { Text = $"+{all.Count - shown.Count}", FontSize = 12, Foreground = (Brush)Root.Resources["RailIconBrush"] } };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, $"{all.Count - shown.Count} more on the shelf");
                more.Click += (_, _) => ShelfFlyout.ShowAt(ShelfButton);
                _shelfChips.Children.Add(more);
            }
        }
        Expand(_shelfWidget, shown.Count > 0);
        ShelfBadge.Visibility = shown.Count == 0 && _shelf.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button ClipChip(ClipItem item, double height)
    {
        var snippet = item.Text.ReplaceLineEndings(" ").Trim();
        UIElement content;
        if (item.Kind == ClipKind.Image && _clipboard.ImagePath(item) is { } path)
        {
            var image = new Image { Height = height - 8, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path)) { DecodePixelHeight = (int)(height * 2) } };
            image.ImageOpened += (_, _) => Expand(_clipWidget, _clipWidget?.ToProgress == 1);
            content = image;
        }
        else
            content = new TextBlock { Text = snippet, FontSize = 12, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Root.Resources["RailIconBrush"] };

        var chip = new Button
        {
            Height = height,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            Content = content,
        };
        var label = item.Kind == ClipKind.Image ? "pinned image" : snippet.Length > 200 ? snippet[..200] + "…" : snippet;
        ToolTipService.SetToolTip(chip, $"{label}\nClick to paste");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Paste {label}");
        chip.Click += async (_, _) => await PasteClipAsync(item);
        return chip;
    }

    private Button ShelfChip(ShelfItem item, double height)
    {
        var image = new Image { Width = height - 10, Height = height - 10 };
        var chip = new Button { Width = height, Height = height, Padding = new Thickness(0), Content = image, CanDrag = true };
        var name = Path.GetFileName(item.Path.TrimEnd('\\'));
        ToolTipService.SetToolTip(chip, $"{name}\nClick to open, or drag it anywhere");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Open {name}");
        _ = Controls.ShelfPanel.LoadThumbnailAsync(item, image);
        chip.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        };
        chip.DragStarting += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            if (await Controls.ShelfPanel.StorageItemAsync(item) is { } storage)
            {
                args.Data.SetStorageItems([storage]);
                args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            }
            deferral.Complete();
        };
        return chip;
    }

    private (string Lead, string Main, string Tail) InlineConversion(DetectedValue detected)
    {
        var culture = CultureInfo.CurrentCulture;
        if (detected is { Time: { } time })
        {
            if (ConvertService.ParseTime(time, strict: false, RailSettings.Current.TimeZones) is not { } parsed) return ("", "", "");
            return ($"{time} → ", $"{TimeZoneInfo.ConvertTime(parsed.Moment, TimeZoneInfo.Local):h:mm tt}", "");
        }
        if (detected is { Currency: { } code })
        {
            var target = code != _convert.HomeCurrency ? _convert.HomeCurrency : RailSettings.Current.Currencies.FirstOrDefault(c => c != code);
            if (target is null || _convert.ConvertCurrency(detected.Amount, code, target) is not { } value) return ("", "", "");
            return ($"{Money(code, detected.Amount)} → ", Money(target, value), "");
        }
        if (detected is { Category: { } categoryName, Unit: { } symbol })
        {
            var category = ConvertService.Categories.FirstOrDefault(c => c.Name == categoryName);
            var from = category?.Units.FirstOrDefault(u => u.Symbol == symbol);
            if (category is null || from is null) return ("", "", "");
            var to = (UnitPairs.TryGetValue(symbol, out var pair) ? category.Units.FirstOrDefault(u => u.Symbol == pair) : null)
                ?? category.Units.FirstOrDefault(u => u != from);
            if (to is null) return ("", "", "");
            var converted = ConvertService.ConvertUnit(detected.Amount, from, to);
            return ($"{detected.Amount.ToString("#,0.##", culture)} {from.Symbol} → ", $"{converted.ToString(Math.Abs(converted) >= 100 ? "#,0" : "#,0.##", culture)} {to.Symbol}", "");
        }
        return ("", "", "");

        string Money(string currency, double amount)
        {
            var number = amount.ToString("#,0.##", culture);
            return _convert.Currencies.TryGetValue(currency, out var info) && info.Symbol.Length <= 2 ? $"{info.Symbol}{number}" : $"{number} {currency}";
        }
    }
}
