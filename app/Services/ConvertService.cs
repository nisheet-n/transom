using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Storage;

namespace Transom.Services;

public sealed record Unit(string Name, string Symbol, double Factor, double Offset = 0);

public sealed record UnitCategory(string Name, Unit[] Units);

public sealed record DetectedValue(double Amount, string? Currency, string? Category, string? Unit, string? Time = null);

public sealed record ParsedTime(DateTimeOffset Moment, TimeZoneInfo Zone, string ZoneLabel);

public sealed partial class ConvertService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_000_000 };
    private static readonly TimeSpan RatesMaxAge = TimeSpan.FromHours(12);

    private readonly string? _cachePath;
    private Dictionary<string, double> _rates = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? RatesUpdated { get; private set; }
    public IReadOnlyDictionary<string, (string Name, string Symbol)> Currencies { get; } = BuildCurrencyList();
    public string HomeCurrency { get; } = SafeHomeCurrency();

    public static readonly UnitCategory[] Categories =
    [
        new("Length", [new("Millimetres", "mm", 0.001), new("Centimetres", "cm", 0.01), new("Metres", "m", 1), new("Kilometres", "km", 1000),
            new("Inches", "in", 0.0254), new("Feet", "ft", 0.3048), new("Yards", "yd", 0.9144), new("Miles", "mi", 1609.344)]),
        new("Weight", [new("Grams", "g", 0.001), new("Kilograms", "kg", 1), new("Ounces", "oz", 0.028349523125), new("Pounds", "lb", 0.45359237), new("Stone", "st", 6.35029318)]),
        new("Temperature", [new("Celsius", "°C", 1, 0), new("Fahrenheit", "°F", 5.0 / 9, -32), new("Kelvin", "K", 1, -273.15)]),
        new("Volume", [new("Millilitres", "ml", 0.001), new("Litres", "l", 1), new("US fluid ounces", "fl oz", 0.0295735), new("US cups", "cup", 0.2365882), new("US gallons", "gal", 3.785411784)]),
        new("Speed", [new("Kilometres per hour", "km/h", 1 / 3.6), new("Miles per hour", "mph", 0.44704), new("Metres per second", "m/s", 1), new("Knots", "kn", 0.514444)]),
        new("Data", [new("Kilobytes", "KB", 1e3), new("Megabytes", "MB", 1e6), new("Gigabytes", "GB", 1e9), new("Terabytes", "TB", 1e12), new("Gibibytes", "GiB", 1073741824)]),
    ];

    public ConvertService()
    {
        try
        {
            _cachePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "rates.json");
            if (File.Exists(_cachePath)) ParseRates(File.ReadAllText(_cachePath));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public bool HasRates => _rates.Count > 0;

    public async Task RefreshRatesAsync()
    {
        if (RatesUpdated is { } updated && DateTimeOffset.Now - updated < RatesMaxAge) return;
        try
        {
            var json = await Http.GetStringAsync("https://open.er-api.com/v6/latest/USD");
            if (!ParseRates(json)) return;
            if (_cachePath is not null) await File.WriteAllTextAsync(_cachePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public double? ConvertCurrency(double amount, string from, string to) =>
        _rates.TryGetValue(from, out var a) && _rates.TryGetValue(to, out var b) && a > 0 ? amount / a * b : null;

    public static double ConvertUnit(double value, Unit from, Unit to)
    {
        var baseValue = (value + from.Offset) * from.Factor;
        return baseValue / to.Factor - to.Offset;
    }

    // A short phrase like "costs $250 a month" still counts when it holds one clear value.
    // Single-letter units and "in" are skipped inside phrases, so "version 3 in" isn't 3 inches.
    public DetectedValue? Detect(string text)
    {
        text = text.Trim();
        if (text.Length is 0 or > 80) return null;
        if (DetectExact(text) is { } exact) return exact;
        if (text.Contains('\n') || text.Contains('\r')) return null;

        foreach (Match match in EmbeddedValueRegex().Matches(text))
        {
            var unit = match.Groups["unit"].Value;
            var unclear = unit.Length == 1 || unit.Equals("in", StringComparison.OrdinalIgnoreCase);
            if (!unclear && DetectExact(match.Value.Trim()) is { } whole) return whole;
            if (DetectExact(match.Groups["core"].Value.Trim()) is { Currency: not null } price) return price;
        }
        return null;
    }

    private DetectedValue? DetectExact(string text)
    {
        if (text.Length > 40) return null;

        var price = PriceRegex().Match(text);
        if (price.Success)
        {
            var symbol = (price.Groups["pre"].Value + price.Groups["post"].Value).Trim();
            var currency = SymbolToCode(symbol);
            if (currency is not null && TryNumber(price.Groups["num"].Value, out var amount)) return new(amount, currency, null, null);
        }

        if (ParseMeasure(text) is { } measure) return new(measure.Value, null, measure.Category.Name, measure.Unit.Symbol);
        return ParseTime(text, strict: true) is { } time ? new(0, null, "Time", time.ZoneLabel, text) : null;
    }

    private static readonly Dictionary<string, string> ZoneAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = "UTC", ["GMT"] = "UTC", ["Z"] = "UTC",
        ["ET"] = "Eastern Standard Time", ["EST"] = "Eastern Standard Time", ["EDT"] = "Eastern Standard Time",
        ["CT"] = "Central Standard Time", ["CST"] = "Central Standard Time", ["CDT"] = "Central Standard Time",
        ["MT"] = "Mountain Standard Time", ["MST"] = "Mountain Standard Time", ["MDT"] = "Mountain Standard Time",
        ["PT"] = "Pacific Standard Time", ["PST"] = "Pacific Standard Time", ["PDT"] = "Pacific Standard Time",
        ["BST"] = "GMT Standard Time", ["CET"] = "W. Europe Standard Time", ["CEST"] = "W. Europe Standard Time",
        ["IST"] = "India Standard Time", ["JST"] = "Tokyo Standard Time", ["KST"] = "Korea Standard Time",
        ["SGT"] = "Singapore Standard Time", ["HKT"] = "China Standard Time", ["AEST"] = "AUS Eastern Standard Time",
        ["AEDT"] = "AUS Eastern Standard Time", ["GST"] = "Arabian Standard Time",
    };

    // "3 PM EST", "15:30 London", "9am". Strict (used for selections) needs a zone and either
    // AM/PM or a colon, so plain numbers like "5 GB" or "3 in" are never read as times.
    public static ParsedTime? ParseTime(string text, bool strict, IEnumerable<string>? knownZones = null)
    {
        var match = TimeRegex().Match(text.Trim());
        if (!match.Success) return null;
        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var meridiem = match.Groups["ampm"].Value.Replace(".", "").ToLowerInvariant();
        var zoneText = match.Groups["zone"].Value.Trim();
        if (strict && (zoneText.Length == 0 || (meridiem.Length == 0 && !match.Groups["m"].Success))) return null;
        if (minute > 59 || hour > 23 || (meridiem.Length > 0 && (hour is 0 or > 12))) return null;
        if (meridiem == "pm" && hour < 12) hour += 12;
        if (meridiem == "am" && hour == 12) hour = 0;

        TimeZoneInfo zone;
        string label;
        if (zoneText.Length == 0)
        {
            zone = TimeZoneInfo.Local;
            label = "your time";
        }
        else if (FindZone(zoneText, knownZones) is { } found)
        {
            zone = found;
            label = zoneText.Length <= 4 ? zoneText.ToUpperInvariant() : zoneText;
        }
        else
        {
            return null;
        }

        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, zone).Date;
        var local = new DateTime(today.Year, today.Month, today.Day, hour, minute, 0, DateTimeKind.Unspecified);
        return new ParsedTime(new DateTimeOffset(local, zone.GetUtcOffset(local)), zone, label);
    }

    private static TimeZoneInfo? FindZone(string text, IEnumerable<string>? knownZones)
    {
        try
        {
            if (ZoneAbbreviations.TryGetValue(text, out var id)) return TimeZoneInfo.FindSystemTimeZoneById(id);
            foreach (var known in knownZones ?? [])
            {
                var city = known.Contains('/') ? known[(known.LastIndexOf('/') + 1)..].Replace('_', ' ') : known;
                if (city.Equals(text, StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.FindSystemTimeZoneById(known);
            }
            if (text.Length < 4) return null;
            return TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(z =>
                z.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) || z.StandardName.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    [GeneratedRegex(@"^(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ampm>[ap]\.?m\.?)?(?:\s+(?<zone>[A-Za-z][A-Za-z .'_-]*)|(?<zone>[A-Z]{2,4}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRegex();

    private static readonly Dictionary<string, string> UnitAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["foot"] = "ft", ["feet"] = "ft", ["'"] = "ft", ["inch"] = "in", ["inches"] = "in", ["\""] = "in",
        ["meter"] = "m", ["meters"] = "m", ["metre"] = "m", ["metres"] = "m", ["mile"] = "mi", ["miles"] = "mi",
        ["kilometer"] = "km", ["kilometers"] = "km", ["yard"] = "yd", ["yards"] = "yd",
        ["kilo"] = "kg", ["kilos"] = "kg", ["pound"] = "lb", ["pounds"] = "lb", ["lbs"] = "lb", ["ounce"] = "oz", ["ounces"] = "oz", ["gram"] = "g", ["grams"] = "g",
        ["c"] = "°C", ["celsius"] = "°C", ["f"] = "°F", ["fahrenheit"] = "°F", ["kelvin"] = "K",
        ["liter"] = "l", ["liters"] = "l", ["litre"] = "l", ["litres"] = "l", ["gallon"] = "gal", ["gallons"] = "gal", ["cups"] = "cup",
        ["kmh"] = "km/h", ["kph"] = "km/h", ["knot"] = "kn", ["knots"] = "kn",
    };

    public static (UnitCategory Category, Unit Unit, double Value)? ParseMeasure(string text)
    {
        var match = MeasureRegex().Match(text.Trim());
        if (!match.Success || !TryNumber(match.Groups["num"].Value, out var value)) return null;

        var token = match.Groups["unit"].Value.Trim().Replace("°", "").Replace(" ", "");
        if (UnitAliases.TryGetValue(token, out var alias)) token = alias.Replace("°", "").Replace(" ", "");
        foreach (var category in Categories)
        {
            var unit = category.Units.FirstOrDefault(u =>
                u.Symbol.Replace("°", "").Replace(" ", "").Equals(token, StringComparison.OrdinalIgnoreCase)
                || u.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
            if (unit is not null) return (category, unit, value);
        }
        return null;
    }

    [GeneratedRegex(@"^(?<num>-?\d[\d,]*(\.\d+)?)\s*(?<unit>.+)$")]
    private static partial Regex MeasureRegex();

    private static readonly HashSet<string> CommonCodes = ["USD", "EUR", "GBP", "INR", "JPY", "AUD", "CAD", "AED", "CHF", "CNY", "SGD"];

    private string? SymbolToCode(string symbol)
    {
        var upper = symbol.ToUpperInvariant();
        if (Currencies.ContainsKey(upper) && (symbol == upper || CommonCodes.Contains(upper))) return upper;
        return symbol switch
        {
            "$" or "US$" => "USD",
            "€" => "EUR",
            "£" => "GBP",
            "¥" => "JPY",
            "₹" or "RS" or "Rs" or "Rs." => "INR",
            "₩" => "KRW",
            "₽" => "RUB",
            "₺" => "TRY",
            "฿" => "THB",
            "A$" => "AUD",
            "C$" => "CAD",
            "₫" => "VND",
            "₱" => "PHP",
            "₦" => "NGN",
            _ => null,
        };
    }

    private static bool TryNumber(string raw, out double value) =>
        double.TryParse(raw.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private bool ParseRates(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("rates", out var rates)) return false;

            var parsed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var rate in rates.EnumerateObject()) parsed[rate.Name] = rate.Value.GetDouble();
            _rates = parsed;
            RatesUpdated = root.TryGetProperty("time_last_update_unix", out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix.GetInt64()).ToLocalTime()
                : DateTimeOffset.Now;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    private static Dictionary<string, (string, string)> BuildCurrencyList()
    {
        var list = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                list.TryAdd(region.ISOCurrencySymbol, (region.CurrencyEnglishName, region.CurrencySymbol));
            }
            catch (ArgumentException)
            {
            }
        }
        return list;
    }

    private static string SafeHomeCurrency()
    {
        try
        {
            return RegionInfo.CurrentRegion.ISOCurrencySymbol;
        }
        catch (Exception)
        {
            return "USD";
        }
    }

    [GeneratedRegex(@"^(?<pre>US\$|A\$|C\$|[$€£¥₹₩₽₺฿₫₱₦]|[A-Za-z]{3}|Rs\.?)?\s?(?<num>\d[\d,]*(\.\d+)?)\s?(?<post>[A-Za-z]{3}|[$€£¥₹])?$")]
    private static partial Regex PriceRegex();

    [GeneratedRegex(@"(?<core>(?:US\$|A\$|C\$|[$€£¥₹₩₽₺฿₫₱₦]|Rs\.?|\b[A-Z]{3}\s)?\s?\d[\d,]*(?:\.\d+)?)(?:\s?(?:°\s?[CFK]\b|[$€£¥₹]|(?<unit>[A-Za-z][A-Za-z/]*)))?")]
    private static partial Regex EmbeddedValueRegex();
}
