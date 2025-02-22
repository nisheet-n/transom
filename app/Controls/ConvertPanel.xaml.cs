using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Transom.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Transom.Controls;

public sealed partial class ConvertPanel : UserControl
{
    private readonly Dictionary<string, TextBox> _currencyBoxes = [];
    private ConvertService? _service;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _clock;
    private string _tab = "currency";
    private string _sourceCode = "USD";
    private double _sourceAmount = 1;
    private string? _extraCode;
    private bool _updating;

    public ConvertPanel()
    {
        InitializeComponent();
    }

    private static RailSettings Settings => RailSettings.Current;
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public void Attach(ConvertService service)
    {
        _service = service;
        if (Settings.Currencies.Count == 0)
        {
            Settings.Currencies = new[] { service.HomeCurrency, "USD", "EUR", "GBP", "JPY", "AED" }
                .Where(service.Currencies.ContainsKey).Distinct().ToList();
        }
        _sourceCode = service.HomeCurrency == "USD" ? "EUR" : "USD";
        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(30);
        _clock.Tick += (_, _) => { if (_tab == "time") BuildTimes(); };
    }

    public async void Opened(DetectedValue? detected)
    {
        if (_service is null) return;
        _extraCode = null;
        SourceNote.Text = "";
        if (detected is { Currency: { } code })
        {
            _sourceCode = code;
            _sourceAmount = detected.Amount;
            if (!Settings.Currencies.Contains(code)) _extraCode = code;
            Tabs.SelectedItem = CurrencyTab;
            SourceNote.Text = "From your clipboard";
        }
        else if (detected is { Unit: { } unit })
        {
            UnitInput.Text = $"{detected.Amount.ToString("0.####", Culture)} {unit}";
            Tabs.SelectedItem = UnitsTab;
            SourceNote.Text = "From your clipboard";
        }
        Rebuild();
        _clock?.Start();

        await _service.RefreshRatesAsync();
        BuildCurrencies();
    }

    public void Closed()
    {
        _clock?.Stop();
        CurrencySearch.Text = "";
        TimeSearch.Text = "";
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _tab = sender.SelectedItem?.Tag as string ?? "currency";
        CurrencyView.Visibility = _tab == "currency" ? Visibility.Visible : Visibility.Collapsed;
        UnitsView.Visibility = _tab == "units" ? Visibility.Visible : Visibility.Collapsed;
        TimeView.Visibility = _tab == "time" ? Visibility.Visible : Visibility.Collapsed;
        Rebuild();
    }

    private void Rebuild()
    {
        switch (_tab)
        {
            case "currency": BuildCurrencies(); break;
            case "units": BuildUnits(); break;
            default: BuildTimes(); break;
        }
    }

    // Currency: every row is editable. Typing in a row makes it the source, and the other rows
    // are updated in place (not rebuilt) so the box being typed in keeps focus.
    private void BuildCurrencies()
    {
        if (_service is null) return;
        _currencyBoxes.Clear();
        CurrencyRows.Children.Clear();

        var codes = Settings.Currencies.ToList();
        if (_extraCode is not null) codes.Insert(0, _extraCode);
        foreach (var code in PinnedFirst(codes, c => $"cur:{c}"))
        {
            var (name, symbol) = _service.Currencies.TryGetValue(code, out var info) ? info : (code, code);
            var box = new TextBox
            {
                MinWidth = 0,
                Width = 130,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.Number) } },
            };
            box.TextChanged += (_, _) => OnCurrencyTyped(code, box);
            _currencyBoxes[code] = box;

            var key = $"cur:{code}";
            var label = code == _service.HomeCurrency ? $"{name} · Home" : name;
            var menu = RowMenu(key, () => box.Text, removable: code != _extraCode, onRemove: () => Settings.Currencies.Remove(code));
            CurrencyRows.Children.Add(Row(symbol.Length > 3 ? code : symbol, code, label, IsPinned(key), box, menu));
        }
        UpdateCurrencyValues(null);

        RatesUpdated.Text = _service.RatesUpdated is { } updated
            ? $" · updated {Ago(updated)}"
            : " · couldn't get rates yet. Check your internet connection.";
    }

    private void OnCurrencyTyped(string code, TextBox box)
    {
        if (_updating || box.FocusState == FocusState.Unfocused) return;
        if (!TryNumber(box.Text, out var amount)) return;
        _sourceCode = code;
        _sourceAmount = amount;
        UpdateCurrencyValues(code);
    }

    private void UpdateCurrencyValues(string? skip)
    {
        if (_service is null) return;
        _updating = true;
        foreach (var (code, box) in _currencyBoxes)
        {
            if (code == skip) continue;
            var value = code == _sourceCode ? _sourceAmount : _service.ConvertCurrency(_sourceAmount, _sourceCode, code);
            box.Text = value is { } v ? v.ToString("N2", Culture) : "—";
        }
        _updating = false;
    }

    private void CurrencySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        CurrencySuggestions.Children.Clear();
        var query = CurrencySearch.Text.Trim();
        if (_service is null || query.Length == 0) return;

        foreach (var (code, (name, _)) in _service.Currencies
            .Where(c => !Settings.Currencies.Contains(c.Key)
                && (c.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Key).Take(5))
        {
            CurrencySuggestions.Children.Add(Suggestion($"{code} · {name}", () =>
            {
                Settings.Currencies.Add(code);
                Settings.Save();
                CurrencySearch.Text = "";
                BuildCurrencies();
            }));
        }
    }

    // Units: one free-text box ("5 ft", "30 °C", "2 GB"); the category is worked out from the unit.
    private void UnitInput_TextChanged(object sender, TextChangedEventArgs e) => BuildUnits();

    private void BuildUnits()
    {
        UnitRows.Children.Clear();
        if (ConvertService.ParseMeasure(UnitInput.Text) is not { } measure)
        {
            UnitHint.Text = UnitInput.Text.Trim().Length == 0
                ? "Works with length, weight, temperature, volume, speed and data sizes."
                : "Add a unit after the number, like 5 ft, 70 kg or 30 °C.";
            return;
        }
        UnitHint.Text = $"{measure.Category.Name} · {measure.Unit.Name}";

        var rows = measure.Category.Units.Where(u => u != measure.Unit).ToList();
        foreach (var unit in PinnedFirst(rows, u => $"unit:{measure.Category.Name}:{u.Symbol}"))
        {
            var value = $"{FormatNumber(ConvertService.ConvertUnit(measure.Value, measure.Unit, unit))} {unit.Symbol}";
            var key = $"unit:{measure.Category.Name}:{unit.Symbol}";
            UnitRows.Children.Add(Row(unit.Symbol, value, unit.Name, IsPinned(key), null, RowMenu(key, () => value, removable: false)));
        }
    }

    private void BuildTimes()
    {
        TimeRows.Children.Clear();
        var now = DateTimeOffset.Now;
        TimeRows.Children.Add(Row("You", now.ToString("h:mm tt"), $"Your time · {TimeZoneInfo.Local.StandardName}", false, null, null));

        foreach (var id in PinnedFirst(Settings.TimeZones.ToList(), z => $"tz:{z}"))
        {
            if (!TryZone(id, out var zone)) continue;
            var there = TimeZoneInfo.ConvertTime(now, zone);
            var diff = there.Offset - now.Offset;
            var diffText = diff == TimeSpan.Zero ? "same time" : $"{(diff < TimeSpan.Zero ? "−" : "+")}{diff.Duration():h\\:mm}";
            var day = there.Date == now.Date ? "today" : there.Date > now.Date ? "tomorrow" : "yesterday";
            var key = $"tz:{id}";
            var time = there.ToString("h:mm tt");
            var menu = RowMenu(key, () => time, removable: true, onRemove: () => Settings.TimeZones.Remove(id));
            TimeRows.Children.Add(Row(ZoneBadge(diff), $"{ZoneName(zone, id)}  {time}", $"{day} · {diffText}", IsPinned(key), null, menu));
        }
    }

    private void TimeSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        TimeSuggestions.Children.Clear();
        var query = TimeSearch.Text.Trim();
        if (query.Length == 0) return;

        foreach (var zone in TimeZoneInfo.GetSystemTimeZones()
            .Where(z => !Settings.TimeZones.Contains(z.Id)
                && (z.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || z.Id.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(5))
        {
            TimeSuggestions.Children.Add(Suggestion(zone.DisplayName, () =>
            {
                Settings.TimeZones.Add(zone.Id);
                Settings.Save();
                TimeSearch.Text = "";
                BuildTimes();
            }));
        }
    }

    private FrameworkElement Row(string badge, string title, string subtitle, bool pinned, FrameworkElement? input, MenuFlyout? menu)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var circle = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new TextBlock { Text = badge, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        grid.Children.Add(circle);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        heading.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, IsTextSelectionEnabled = input is null });
        if (pinned) heading.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
        text.Children.Add(heading);
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (input is not null)
        {
            Grid.SetColumn(input, 2);
            grid.Children.Add(input);
        }

        if (menu is not null)
        {
            var more = new Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new FontIcon { Glyph = "", FontSize = 14 },
                Flyout = menu,
            };
            ToolTipService.SetToolTip(more, "More");
            Grid.SetColumn(more, 3);
            grid.Children.Add(more);
            grid.ContextFlyout = menu;
        }
        return grid;
    }

    private MenuFlyout RowMenu(string key, Func<string> value, bool removable, Action? onRemove = null)
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        var pinned = IsPinned(key);
        var pin = new MenuFlyoutItem { Text = pinned ? "Unpin" : "Pin to top", Icon = new FontIcon { Glyph = pinned ? "" : "" } };
        pin.Click += (_, _) =>
        {
            if (!Settings.PinnedConvert.Remove(key)) Settings.PinnedConvert.Insert(0, key);
            Settings.Save();
            Rebuild();
        };
        menu.Items.Add(pin);

        var copy = new MenuFlyoutItem { Text = "Copy", Icon = new FontIcon { Glyph = "" } };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(value());
            Clipboard.SetContent(package);
        };
        menu.Items.Add(copy);

        if (removable && onRemove is not null)
        {
            var remove = new MenuFlyoutItem { Text = "Remove", Icon = new FontIcon { Glyph = "" } };
            remove.Click += (_, _) =>
            {
                onRemove();
                Settings.PinnedConvert.Remove(key);
                Settings.Save();
                Rebuild();
            };
            menu.Items.Add(remove);
        }
        return menu;
    }

    private static Button Suggestion(string text, Action onPick)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis },
        };
        button.Click += (_, _) => onPick();
        return button;
    }

    private static bool IsPinned(string key) => Settings.PinnedConvert.Contains(key);

    // Pinned rows first, in pin order (the newest pin is stored at the front of the list);
    // use never reshuffles them. Everything else keeps its normal order.
    private static IEnumerable<T> PinnedFirst<T>(List<T> items, Func<T, string> key)
    {
        var pins = Settings.PinnedConvert;
        return items.Where(i => pins.Contains(key(i))).OrderBy(i => pins.IndexOf(key(i)))
            .Concat(items.Where(i => !pins.Contains(key(i))));
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, Culture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string FormatNumber(double value) =>
        Math.Abs(value) >= 1000 ? value.ToString("N2", Culture) : value.ToString("0.####", Culture);

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hr ago";
        return when.ToString("d MMM");
    }

    private static bool TryZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }

    private static string ZoneName(TimeZoneInfo zone, string id)
    {
        if (id.Contains('/')) return id[(id.LastIndexOf('/') + 1)..].Replace('_', ' ');
        if (id == "UTC") return "UTC";
        var display = zone.DisplayName;
        var close = display.IndexOf(')');
        var name = close >= 0 ? display[(close + 1)..].Trim() : display;
        var comma = name.IndexOf(',');
        return comma > 0 ? name[..comma] : name;
    }

    private static string ZoneBadge(TimeSpan diff)
    {
        if (diff == TimeSpan.Zero) return "=";
        var sign = diff < TimeSpan.Zero ? "−" : "+";
        return diff.Minutes == 0 ? $"{sign}{Math.Abs(diff.Hours)}" : $"{sign}{Math.Abs(diff.TotalHours):0.#}";
    }
}
