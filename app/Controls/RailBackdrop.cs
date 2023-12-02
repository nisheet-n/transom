using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Transom.Services;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Transom.Controls;

public sealed partial class RailBackdrop : SystemBackdrop
{
    // Windows keeps the taskbar's acrylic values private; these are calibrated side by side
    // against the real taskbar. Mode, accent, transparency and wallpaper are still read live.
    private static readonly Color DarkTint = Color.FromArgb(255, 0x23, 0x23, 0x23);
    private static readonly Color LightTint = Color.FromArgb(255, 0xEE, 0xEE, 0xEE);
    private static readonly Color DarkSolid = Color.FromArgb(255, 0x1C, 0x1C, 0x1C);
    private static readonly Color LightSolid = Color.FromArgb(255, 0xEE, 0xEE, 0xEE);

    private readonly UISettings _uiSettings = new();
    private ICompositionSupportsSystemBackdrop? _target;
    private SystemBackdropConfiguration? _config;
    private object? _controller;

    public event Action? Connected;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _target = target;
        _config = new SystemBackdropConfiguration { IsInputActive = true };
        Connected?.Invoke();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        Drop();
        _target = null;
    }

    public Color? Apply(RailSettings settings, TaskbarAppearance taskbar, bool isLight, bool forceSolid)
    {
        var style = forceSolid ? BackgroundStyle.Solid : settings.Background;
        if (style == BackgroundStyle.Solid)
        {
            Drop();
            return isLight ? LightSolid : DarkSolid;
        }
        if (_target is null || _config is null) return null;

        _config.Theme = isLight ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;

        if (style is BackgroundStyle.WallpaperSubtle or BackgroundStyle.WallpaperDeep)
        {
            var mica = Use(() => new MicaController());
            mica.Kind = style == BackgroundStyle.WallpaperDeep ? MicaKind.BaseAlt : MicaKind.Base;
            return null;
        }

        var acrylic = Use(() => new DesktopAcrylicController());
        acrylic.ResetProperties();
        switch (style)
        {
            case BackgroundStyle.Frosted:
                acrylic.Kind = DesktopAcrylicKind.Thin;
                break;
            case BackgroundStyle.Accent:
                Tint(acrylic, Accent(isLight), 0.7f, 0.96f);
                break;
            case BackgroundStyle.Preset:
                Tint(acrylic, settings.PresetColor, 0.75f, 0.96f);
                break;
            case BackgroundStyle.Custom:
                var opacity = (float)settings.CustomOpacity;
                Tint(acrylic, settings.CustomColor, opacity, 0.5f + opacity / 2);
                break;
            default:
                if (taskbar.UsesAccent) Tint(acrylic, Accent(isLight), 0.8f, 0.96f);
                else Tint(acrylic, isLight ? LightTint : DarkTint, 0.38f, 0.96f);
                break;
        }
        return null;
    }

    private static void Tint(DesktopAcrylicController acrylic, Color color, float tint, float luminosity)
    {
        acrylic.Kind = DesktopAcrylicKind.Base;
        acrylic.TintColor = color;
        acrylic.TintOpacity = tint;
        acrylic.LuminosityOpacity = luminosity;
        acrylic.FallbackColor = color;
    }

    private Color Accent(bool isLight) =>
        _uiSettings.GetColorValue(isLight ? UIColorType.AccentLight2 : UIColorType.AccentDark2);

    private T Use<T>(Func<T> create) where T : class
    {
        if (_controller is T existing) return existing;
        Drop();
        var controller = create();
        switch (controller)
        {
            case MicaController mica:
                mica.AddSystemBackdropTarget(_target!);
                mica.SetSystemBackdropConfiguration(_config!);
                break;
            case DesktopAcrylicController acrylic:
                acrylic.AddSystemBackdropTarget(_target!);
                acrylic.SetSystemBackdropConfiguration(_config!);
                break;
        }
        _controller = controller;
        return controller;
    }

    private void Drop()
    {
        switch (_controller)
        {
            case MicaController mica:
                if (_target is not null) mica.RemoveSystemBackdropTarget(_target);
                mica.Dispose();
                break;
            case DesktopAcrylicController acrylic:
                if (_target is not null) acrylic.RemoveSystemBackdropTarget(_target);
                acrylic.Dispose();
                break;
        }
        _controller = null;
    }
}
