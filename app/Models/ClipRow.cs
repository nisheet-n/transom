using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Transom.Models;

public sealed partial class ClipRow(string id, string glyph, ImageSource? thumbnail, string preview, string meta, bool pinned)
{
    public string Id { get; } = id;
    public string Glyph { get; } = glyph;
    public ImageSource? Thumbnail { get; } = thumbnail;
    public Visibility GlyphVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThumbnailVisibility => Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
    public string Preview { get; } = preview;
    public string Meta { get; } = meta;
    public string PinGlyph { get; } = pinned ? "" : "";
    public Brush PinBrush { get; } = (Brush)Application.Current.Resources[pinned ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush"];
    public string PinTip { get; } = pinned ? "Unpin" : "Pin";
}
