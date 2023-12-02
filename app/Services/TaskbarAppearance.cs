using Microsoft.Win32;

namespace Transom.Services;

public readonly record struct TaskbarAppearance(bool IsLight, bool UsesAccent, bool IsTransparent)
{
    public static TaskbarAppearance Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return new(Flag("SystemUsesLightTheme", false), Flag("ColorPrevalence", false), Flag("EnableTransparency", true));

        bool Flag(string name, bool fallback) => key?.GetValue(name) is int value ? value != 0 : fallback;
    }
}
