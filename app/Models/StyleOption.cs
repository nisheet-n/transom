using Microsoft.UI.Xaml.Media;
using Transom.Services;
using Windows.UI;

namespace Transom.Models;

public sealed partial class StyleOption(BackgroundStyle style, string name, string hint, Color from, Color to)
{
    public BackgroundStyle Style { get; } = style;
    public string Name { get; } = name;
    public string Hint { get; } = hint;
    public Brush Swatch { get; } = new LinearGradientBrush
    {
        StartPoint = new(0, 0),
        EndPoint = new(1, 1),
        GradientStops =
        {
            new GradientStop { Color = from, Offset = 0 },
            new GradientStop { Color = to, Offset = 1 },
        },
    };
}

public sealed partial class PresetOption(string name, Color color)
{
    public string Name { get; } = name;
    public Color Color { get; } = color;
    public Brush Swatch { get; } = new SolidColorBrush(color);
}
