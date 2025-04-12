using Windows.Storage;
using Windows.UI;

namespace Transom.Services;

public enum BackgroundStyle { MatchTaskbar, WallpaperSubtle, WallpaperDeep, Frosted, Accent, Preset, Custom, Solid }

public enum RailTheme { FollowTaskbar, Light, Dark }

public sealed class RailSettings
{
    public static readonly (string Name, Color Color)[] Presets =
    [
        ("Graphite", Color.FromArgb(255, 0x2B, 0x2B, 0x2E)),
        ("Midnight", Color.FromArgb(255, 0x1B, 0x22, 0x36)),
        ("Brass", Color.FromArgb(255, 0x3A, 0x2A, 0x14)),
        ("Forest", Color.FromArgb(255, 0x16, 0x29, 0x1F)),
        ("Ocean", Color.FromArgb(255, 0x12, 0x29, 0x3A)),
        ("Plum", Color.FromArgb(255, 0x2D, 0x1A, 0x33)),
    ];

    public static RailSettings Current { get; } = Load();

    public event Action? Changed;

    public BackgroundStyle Background { get; set; } = BackgroundStyle.MatchTaskbar;
    public Color PresetColor { get; set; } = Presets[0].Color;
    public Color CustomColor { get; set; } = Color.FromArgb(255, 0x2A, 0x2F, 0x3A);
    public double CustomOpacity { get; set; } = 0.75;
    public RailTheme Theme { get; set; } = RailTheme.FollowTaskbar;
    public bool SolidOnBatterySaver { get; set; } = true;
    public List<string> Currencies { get; set; } = [];
    public List<string> TimeZones { get; set; } = ["UTC", "America/New_York", "Europe/London", "Asia/Tokyo"];
    public List<string> PinnedConvert { get; set; } = [];
    public string Dock { get; set; } = "left";
    public List<string> HiddenTools { get; set; } = [];
    public int ClipboardLimit { get; set; } = 50;
    public bool ClearClipboardOnQuit { get; set; }
    public int SnoozeMinutes { get; set; } = 5;
    public int AwakeDefault { get; set; } = 1;
    public bool HideFromCapture { get; set; }
    public bool KeyboardNavigation { get; set; }
    public bool WatchSelection { get; set; } = true;

    public void Save()
    {
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            values[nameof(Background)] = (int)Background;
            values[nameof(PresetColor)] = Pack(PresetColor);
            values[nameof(CustomColor)] = Pack(CustomColor);
            values[nameof(CustomOpacity)] = CustomOpacity;
            values[nameof(Theme)] = (int)Theme;
            values[nameof(SolidOnBatterySaver)] = SolidOnBatterySaver;
            values[nameof(Currencies)] = string.Join(",", Currencies);
            values[nameof(TimeZones)] = string.Join(",", TimeZones);
            values[nameof(PinnedConvert)] = string.Join("|", PinnedConvert);
            values[nameof(Dock)] = Dock;
            values[nameof(HiddenTools)] = string.Join(",", HiddenTools);
            values[nameof(ClipboardLimit)] = ClipboardLimit;
            values[nameof(ClearClipboardOnQuit)] = ClearClipboardOnQuit;
            values[nameof(SnoozeMinutes)] = SnoozeMinutes;
            values[nameof(AwakeDefault)] = AwakeDefault;
            values[nameof(HideFromCapture)] = HideFromCapture;
            values[nameof(KeyboardNavigation)] = KeyboardNavigation;
            values[nameof(WatchSelection)] = WatchSelection;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        Changed?.Invoke();
    }

    private static RailSettings Load()
    {
        var settings = new RailSettings();
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            if (values[nameof(Background)] is int background) settings.Background = (BackgroundStyle)background;
            if (values[nameof(PresetColor)] is uint preset) settings.PresetColor = Unpack(preset);
            if (values[nameof(CustomColor)] is uint custom) settings.CustomColor = Unpack(custom);
            if (values[nameof(CustomOpacity)] is double opacity) settings.CustomOpacity = opacity;
            if (values[nameof(Theme)] is int theme) settings.Theme = (RailTheme)theme;
            if (values[nameof(SolidOnBatterySaver)] is bool saver) settings.SolidOnBatterySaver = saver;
            if (values[nameof(Currencies)] is string currencies) settings.Currencies = [.. currencies.Split(',', StringSplitOptions.RemoveEmptyEntries)];
            if (values[nameof(TimeZones)] is string zones) settings.TimeZones = [.. zones.Split(',', StringSplitOptions.RemoveEmptyEntries)];
            if (values[nameof(PinnedConvert)] is string pins) settings.PinnedConvert = [.. pins.Split('|', StringSplitOptions.RemoveEmptyEntries)];
            if (values[nameof(Dock)] is string dock) settings.Dock = dock;
            if (values[nameof(HiddenTools)] is string hidden) settings.HiddenTools = [.. hidden.Split(',', StringSplitOptions.RemoveEmptyEntries)];
            if (values[nameof(ClipboardLimit)] is int limit) settings.ClipboardLimit = limit;
            if (values[nameof(ClearClipboardOnQuit)] is bool clear) settings.ClearClipboardOnQuit = clear;
            if (values[nameof(SnoozeMinutes)] is int snooze) settings.SnoozeMinutes = snooze;
            if (values[nameof(AwakeDefault)] is int awake) settings.AwakeDefault = awake;
            if (values[nameof(HideFromCapture)] is bool hide) settings.HideFromCapture = hide;
            if (values[nameof(KeyboardNavigation)] is bool keys) settings.KeyboardNavigation = keys;
            if (values[nameof(WatchSelection)] is bool watch) settings.WatchSelection = watch;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        return settings;
    }

    private static uint Pack(Color c) => (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);

    private static Color Unpack(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
}
