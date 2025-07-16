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
            ShowCurrencyInput();
            Tabs.SelectedItem = CurrencyTab;
            SourceNote.Text = "From your selection";
        }
        else if (detected is { Time: { } time })
        {
            TimeInput.Text = time;
            Tabs.SelectedItem = TimeTab;
            SourceNote.Text = "From your selection";
        }
        else if (detected is { Unit: { } unit })
        {
            UnitInput.Text = $"{detected.Amount.ToString("0.####", Culture)} {unit}";
            Tabs.SelectedItem = UnitsTab;
            SourceNote.Text = "From your selection";
        }
        if (detected is null) ShowCurrencyInput();
        Rebuild();
        _clock?.Start();

        await _service.RefreshRatesAsync();
        BuildCurrencies();
    }

    public void Closed()
    {
        _clock?.Stop();
        CurrencySuggestions.Children.Clear();
        TimeInput.Text = "";
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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"Amount in {name}");
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
        ShowCurrencyInput();
        UpdateCurrencyValues(code);
    }

    // The top box converts "$100", "50 EUR", "₹2,000" or a bare number (in the current currency).
    // Typing a name instead ("Swiss", "CAD") offers currencies to add to your list.
    private void CurrencyInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || _service is null || CurrencyInput.FocusState == FocusState.Unfocused) return;
        CurrencySuggestions.Children.Clear();
        var text = CurrencyInput.Text.Trim();
        if (text.Length == 0)
        {
            CurrencyHint.Text = "";
            return;
        }

        if (_service.Detect(text) is { Currency: { } code } detected)
        {
            var listChanged = code != _sourceCode && !Settings.Currencies.Contains(code) && code != _extraCode;
            _sourceCode = code;
            _sourceAmount = detected.Amount;
            if (!Settings.Currencies.Contains(code)) _extraCode = code;
            if (listChanged) BuildCurrencies();
            else UpdateCurrencyValues(null);
        }
        else if (TryNumber(text, out var amount))
        {
            _sourceAmount = amount;
            UpdateCurrencyValues(null);
        }
        else
        {
            var matches = _service.Currencies
                .Where(c => !Settings.Currencies.Contains(c.Key)
                    && (c.Key.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Value.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => !c.Key.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ThenBy(c => c.Key)
                .Take(5).ToList();
            foreach (var (match, (name, _)) in matches) CurrencySuggestions.Children.Add(Suggestion($"Add {name} · {match}", () => AddCurrency(match)));
            var listed = Settings.Currencies.FirstOrDefault(c => c.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (_service.Currencies.TryGetValue(c, out var info) && info.Name.Contains(text, StringComparison.OrdinalIgnoreCase)));
            CurrencyHint.Text = matches.Count > 0 ? "Pick one to add it to your list."
                : listed is not null ? $"{CurrencyLabel(listed)} is already in your list."
                : "Type an amount like $100 or 50 EUR, or a currency like CAD.";
            return;
        }
        CurrencyHint.Text = CurrencyLabel(_sourceCode);
    }

    private void AddCurrency(string code)
    {
        Settings.Currencies.Add(code);
        Settings.Save();
        CurrencySuggestions.Children.Clear();
        BuildCurrencies();
        ShowCurrencyInput();
        CurrencyHint.Text = $"Added {CurrencyLabel(code)}.";
    }

    private void ShowCurrencyInput()
    {
        if (_service is null) return;
        _updating = true;
        var symbol = _service.Currencies.TryGetValue(_sourceCode, out var info) && info.Symbol.Length <= 2 ? info.Symbol : "";
        CurrencyInput.Text = symbol.Length > 0
            ? $"{symbol}{_sourceAmount.ToString("#,0.##", Culture)}"
            : $"{_sourceAmount.ToString("#,0.##", Culture)} {_sourceCode}";
        CurrencyHint.Text = CurrencyLabel(_sourceCode);
        _updating = false;
    }

    private string CurrencyLabel(string code) =>
        _service is not null && _service.Currencies.TryGetValue(code, out var info) ? $"{info.Name} · {code}" : code;

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

    // Units: one free-text box ("5 ft", "30 °C", "2 GB"); the category is worked out from the unit.
    private void UnitInput_TextChanged(object sender, TextChangedEventArgs e) => BuildUnits();

    private void BuildUnits()
    {
        UnitRows.Children.Clear();
        if (ConvertService.ParseMeasure(UnitInput.Text) is not { } measure)
        {
            UnitHint.Text = UnitInput.Text.Trim().Length == 0
                ? "Type a value to see it in other units."
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

    private void TimeInput_TextChanged(object sender, TextChangedEventArgs e) => BuildTimes();

    // A time in the top box ("3 PM EST") shows that moment in every row; a place name with no
    // time ("Tokyo") offers zones to add; an empty box shows the time now.
    private void BuildTimes()
    {
        TimeRows.Children.Clear();
        TimeSuggestions.Children.Clear();
        var typed = TimeInput.Text.Trim();
        var parsed = typed.Length == 0 ? null : ConvertService.ParseTime(typed, strict: false, Settings.TimeZones);
        if (typed.Length == 0) TimeHint.Text = "Showing the time now.";
        else if (parsed is not null) TimeHint.Text = $"{parsed.Moment:h:mm tt, ddd d MMM} · {parsed.ZoneLabel}";
        else if (!typed.Any(char.IsDigit) && Zones.Search(typed, Settings.TimeZones) is { Count: > 0 } zones)
        {
            foreach (var (id, label) in zones) TimeSuggestions.Children.Add(Suggestion($"Add {label}", () => AddZone(id)));
            TimeHint.Text = "Pick one to add it to your list.";
        }
        else if (!typed.Any(char.IsDigit) && Settings.TimeZones.FirstOrDefault(z => z.Contains(typed, StringComparison.OrdinalIgnoreCase)) is { } listed)
            TimeHint.Text = $"{listed[(listed.LastIndexOf('/') + 1)..].Replace('_', ' ')} is already in your list.";
        else TimeHint.Text = "Type a time like 3 PM EST or 15:30 London, or a city like Tokyo.";

        var now = parsed is { } p ? TimeZoneInfo.ConvertTime(p.Moment, TimeZoneInfo.Local) : DateTimeOffset.Now;
        var yourDay = parsed is null ? "Your time" : now.ToString("ddd d MMM");
        TimeRows.Children.Add(Row("You", now.ToString("h:mm tt"), $"{yourDay} · {TimeZoneInfo.Local.StandardName}", false, null, null));

        foreach (var id in PinnedFirst(Settings.TimeZones.ToList(), z => $"tz:{z}"))
        {
            if (!Zones.TryFind(id, out var zone)) continue;
            var there = TimeZoneInfo.ConvertTime(now, zone);
            var diff = there.Offset - now.Offset;
            var diffText = Zones.Offset(diff);
            var day = parsed is not null ? there.ToString("ddd d MMM") : there.Date == now.Date ? "today" : there.Date > now.Date ? "tomorrow" : "yesterday";
            var key = $"tz:{id}";
            var time = there.ToString("h:mm tt");
            var menu = RowMenu(key, () => time, removable: true, onRemove: () => Settings.TimeZones.Remove(id));
            TimeRows.Children.Add(Row(ZoneBadge(diff), $"{Zones.Name(zone, id)}  {time}", $"{day} · {diffText}", IsPinned(key), null, menu));
        }
    }

    private void AddZone(string id)
    {
        Settings.TimeZones.Add(id);
        Settings.Save();
        TimeInput.Text = "";
        BuildTimes();
        TimeHint.Text = "Added. Type a time to compare, like 3 PM EST.";
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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, $"More options for {title.Split("  ")[0]}");
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
            ClipboardWriter.TrySet(package);
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
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 12 },
                    new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
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
        Math.Abs(value) >= 1000 ? value.ToString("#,0.##", Culture) : value.ToString("0.####", Culture);

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hr ago";
        return when.ToString("d MMM");
    }

    private static string ZoneBadge(TimeSpan diff)
    {
        if (diff == TimeSpan.Zero) return "=";
        var sign = diff < TimeSpan.Zero ? "−" : "+";
        return diff.Minutes == 0 ? $"{sign}{Math.Abs(diff.Hours)}" : $"{sign}{Math.Abs(diff.TotalHours):0.#}";
    }
}
