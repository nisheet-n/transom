using System.Diagnostics;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using Transom.Services;

namespace Transom;

public sealed partial class CaptureEditor : Window
{
    private enum Tool { Pen, Highlighter, Rectangle, Arrow, Picker }

    private sealed record Stroke(Tool Tool, Color Color, double Width, List<Point> Points);

    private static readonly (string Name, Color Color)[] Palette =
    [
        ("Red", Color.FromArgb(255, 0xE8, 0x11, 0x23)),
        ("Yellow", Color.FromArgb(255, 0xFF, 0xB9, 0x00)),
        ("Green", Color.FromArgb(255, 0x10, 0x89, 0x3E)),
        ("Blue", Color.FromArgb(255, 0x00, 0x78, 0xD4)),
        ("Black", Color.FromArgb(255, 0x11, 0x11, 0x11)),
        ("White", Color.FromArgb(255, 0xFF, 0xFF, 0xFF)),
    ];

    private readonly string _sourcePath;
    private readonly List<(Stroke Stroke, Shape Shape)> _strokes = [];
    private byte[] _pixels = [];
    private int _width, _height;
    private Tool _tool;
    private readonly Dictionary<Tool, Color> _toolColors = new()
    {
        [Tool.Pen] = Palette[0].Color,
        [Tool.Highlighter] = Palette[1].Color,
        [Tool.Rectangle] = Palette[0].Color,
        [Tool.Arrow] = Palette[0].Color,
    };
    private Stroke? _current;
    private Shape? _currentShape;

    // Raised with the path of the flattened image whenever the edit is copied, saved or finished.
    public event Action<string>? Flattened;

    public CaptureEditor(string imagePath, bool pickColor)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Maximize();

        _sourcePath = imagePath;
        BuildSwatches();
        SelectTool(pickColor ? Tool.Picker : Tool.Pen);

        var undo = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Z, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        undo.Invoked += (_, args) =>
        {
            args.Handled = true;
            Undo();
        };
        Surface.KeyboardAccelerators.Add(undo);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_sourcePath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            _width = (int)decoder.PixelWidth;
            _height = (int)decoder.PixelHeight;
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            _pixels = data.DetachPixelData();

            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            Picture.Source = image;
            Surface.Width = Ink.Width = _width;
            Surface.Height = Ink.Height = _height;
            var scale = Content.XamlRoot?.RasterizationScale ?? 1;
            Fit.MaxWidth = _width / scale;
            Fit.MaxHeight = _height / scale;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Status.Text = "Couldn't open this screenshot.";
        }
    }

    private void BuildSwatches()
    {
        foreach (var (name, color) in Palette)
        {
            var swatch = new Button { Style = (Style)((Grid)Content).Resources["Swatch"], Background = new SolidColorBrush(color), Tag = color };
            AutomationProperties.SetName(swatch, name);
            ToolTipService.SetToolTip(swatch, name);
            swatch.Click += (_, _) =>
            {
                if (_tool == Tool.Picker) SelectTool(Tool.Pen);
                _toolColors[_tool] = color;
                UpdateSwatches();
            };
            Swatches.Children.Add(swatch);
        }
        UpdateSwatches();
    }

    private void UpdateSwatches()
    {
        foreach (var swatch in Swatches.Children.OfType<Button>())
        {
            var selected = _toolColors.TryGetValue(_tool, out var current) && swatch.Tag is Color c && c == current;
            swatch.BorderBrush = selected
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        }
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag }) SelectTool(Enum.Parse<Tool>(tag, ignoreCase: true));
    }

    private void SelectTool(Tool tool)
    {
        _tool = tool;
        PenTool.IsChecked = tool == Tool.Pen;
        HighlighterTool.IsChecked = tool == Tool.Highlighter;
        RectangleTool.IsChecked = tool == Tool.Rectangle;
        ArrowTool.IsChecked = tool == Tool.Arrow;
        UpdateSwatches();
        PickerTool.IsChecked = tool == Tool.Picker;
        if (tool == Tool.Picker && PickedSwatch.Visibility == Visibility.Collapsed) Status.Text = "Click anywhere on the screenshot to pick its color.";
    }

    // Widths are in image pixels, scaled to the screenshot so a stroke looks the same on any monitor.
    private double StrokeWidth(Tool tool)
    {
        var unit = Math.Max(_width, _height) / 640.0;
        return tool == Tool.Highlighter ? Math.Max(12, unit * 9) : Math.Max(3, unit * 2.5);
    }

    private void Ink_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_pixels.Length == 0) return;
        var point = e.GetCurrentPoint(Ink).Position;
        if (_tool == Tool.Picker)
        {
            PickColor(point);
            return;
        }

        Ink.CapturePointer(e.Pointer);
        _current = new Stroke(_tool, _toolColors[_tool], StrokeWidth(_tool), [point]);
        _currentShape = CreateShape(_current);
        Ink.Children.Add(_currentShape);
        UpdateShape(_current, _currentShape);
    }

    private void Ink_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_current is null || _currentShape is null) return;
        var point = e.GetCurrentPoint(Ink).Position;
        if (_current.Tool is Tool.Pen or Tool.Highlighter) _current.Points.Add(point);
        else if (_current.Points.Count == 1) _current.Points.Add(point);
        else _current.Points[^1] = point;
        UpdateShape(_current, _currentShape);
    }

    private void Ink_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Ink.ReleasePointerCapture(e.Pointer);
        if (_current is null || _currentShape is null) return;
        if (_current.Points.Count > 1) _strokes.Add((_current, _currentShape));
        else Ink.Children.Remove(_currentShape);
        _current = null;
        _currentShape = null;
    }

    private static Shape CreateShape(Stroke stroke)
    {
        var brush = new SolidColorBrush(stroke.Color);
        Shape shape = stroke.Tool switch
        {
            Tool.Rectangle => new Rectangle(),
            Tool.Arrow => new Microsoft.UI.Xaml.Shapes.Path(),
            _ => new Polyline { StrokeLineJoin = PenLineJoin.Round },
        };
        shape.Stroke = brush;
        shape.StrokeThickness = stroke.Width;
        shape.StrokeStartLineCap = shape.StrokeEndLineCap = PenLineCap.Round;
        shape.Opacity = stroke.Tool == Tool.Highlighter ? 0.4 : 1;
        shape.IsHitTestVisible = false;
        return shape;
    }

    private static void UpdateShape(Stroke stroke, Shape shape)
    {
        var first = stroke.Points[0];
        var last = stroke.Points[^1];
        switch (shape)
        {
            case Polyline line:
                line.Points = [.. stroke.Points];
                break;
            case Rectangle rect:
                Canvas.SetLeft(rect, Math.Min(first.X, last.X));
                Canvas.SetTop(rect, Math.Min(first.Y, last.Y));
                rect.Width = Math.Abs(last.X - first.X);
                rect.Height = Math.Abs(last.Y - first.Y);
                break;
            case Microsoft.UI.Xaml.Shapes.Path path:
                var geometry = new PathGeometry();
                foreach (var (a, b) in ArrowSegments(first, last, stroke.Width))
                {
                    var segment = new PathFigure { StartPoint = a };
                    segment.Segments.Add(new LineSegment { Point = b });
                    geometry.Figures.Add(segment);
                }
                path.Data = geometry;
                break;
        }
    }

    private static IEnumerable<(Point A, Point B)> ArrowSegments(Point from, Point to, double width)
    {
        yield return (from, to);
        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        var head = Math.Max(14, width * 4.5);
        foreach (var side in new[] { -1, 1 })
        {
            var a = angle + Math.PI + side * Math.PI / 7;
            yield return (to, new Point(to.X + head * Math.Cos(a), to.Y + head * Math.Sin(a)));
        }
    }

    private static IEnumerable<(Point A, Point B)> Segments(Stroke stroke)
    {
        var p = stroke.Points;
        switch (stroke.Tool)
        {
            case Tool.Rectangle:
                var (a, c) = (p[0], p[^1]);
                var b = new Point(c.X, a.Y);
                var d = new Point(a.X, c.Y);
                return [(a, b), (b, c), (c, d), (d, a)];
            case Tool.Arrow:
                return ArrowSegments(p[0], p[^1], stroke.Width);
            default:
                return p.Zip(p.Skip(1));
        }
    }

    private void PickColor(Point point)
    {
        var x = Math.Clamp((int)point.X, 0, _width - 1);
        var y = Math.Clamp((int)point.Y, 0, _height - 1);
        var i = (y * _width + x) * 4;
        var color = Color.FromArgb(255, _pixels[i + 2], _pixels[i + 1], _pixels[i]);
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        PickedSwatch.Background = new SolidColorBrush(color);
        PickedSwatch.Visibility = Visibility.Visible;
        Status.Text = $"{hex}  ·  RGB {color.R}, {color.G}, {color.B}  ·  copied";
        var package = new DataPackage();
        package.SetText(hex);
        ClipboardWriter.TrySet(package);
    }

    private void Undo()
    {
        if (_strokes.Count == 0) return;
        Ink.Children.Remove(_strokes[^1].Shape);
        _strokes.RemoveAt(_strokes.Count - 1);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _strokes.Clear();
        Ink.Children.Clear();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e) => await CopyAsync();

    private async Task CopyAsync()
    {
        if (await FlattenAsync() is not { } path) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            Status.Text = ClipboardWriter.TrySet(package) ? "Copied to your clipboard." : "The clipboard is busy. Try again.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Status.Text = "Couldn't copy the screenshot.";
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await FlattenAsync() is not { } path) return;
        try
        {
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Transom");
            Directory.CreateDirectory(folder);
            var target = System.IO.Path.Combine(folder, $"Capture {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            File.Copy(path, target, overwrite: true);
            Status.Text = $"Saved to Pictures › Transom › {System.IO.Path.GetFileName(target)}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Status.Text = "Couldn't save the screenshot.";
        }
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        if (_strokes.Count > 0) await CopyAsync();
        Close();
    }

    // Draws every stroke into a copy of the original pixels, so the result keeps the
    // screenshot's full resolution. Each stroke is turned into a coverage mask first, so a
    // highlighter that crosses itself doesn't get darker where it overlaps.
    private async Task<string?> FlattenAsync()
    {
        if (_pixels.Length == 0) return null;
        if (_strokes.Count == 0) return _sourcePath;
        try
        {
            var pixels = (byte[])_pixels.Clone();
            foreach (var (stroke, _) in _strokes) Rasterize(pixels, _width, _height, stroke);

            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("captures", CreationCollisionOption.OpenIfExists);
            var file = await folder.CreateFileAsync($"edited-{DateTime.Now:yyyyMMdd-HHmmss}.png", CreationCollisionOption.GenerateUniqueName);
            using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)_width, (uint)_height, 96, 96, pixels);
                await encoder.FlushAsync();
            }
            Flattened?.Invoke(file.Path);
            return file.Path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Status.Text = "Couldn't apply the drawing.";
            return null;
        }
    }

    private static void Rasterize(byte[] pixels, int width, int height, Stroke stroke)
    {
        var segments = Segments(stroke).ToList();
        var radius = stroke.Width / 2;
        var left = (int)Math.Max(0, segments.Min(s => Math.Min(s.A.X, s.B.X)) - radius - 1);
        var top = (int)Math.Max(0, segments.Min(s => Math.Min(s.A.Y, s.B.Y)) - radius - 1);
        var right = (int)Math.Min(width - 1, segments.Max(s => Math.Max(s.A.X, s.B.X)) + radius + 1);
        var bottom = (int)Math.Min(height - 1, segments.Max(s => Math.Max(s.A.Y, s.B.Y)) + radius + 1);
        if (right < left || bottom < top) return;

        var boxWidth = right - left + 1;
        var mask = new float[boxWidth * (bottom - top + 1)];
        foreach (var (a, b) in segments)
        {
            var x0 = (int)Math.Max(left, Math.Min(a.X, b.X) - radius - 1);
            var x1 = (int)Math.Min(right, Math.Max(a.X, b.X) + radius + 1);
            var y0 = (int)Math.Max(top, Math.Min(a.Y, b.Y) - radius - 1);
            var y1 = (int)Math.Min(bottom, Math.Max(a.Y, b.Y) + radius + 1);
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var coverage = (float)Math.Clamp(radius + 0.5 - Distance(x + 0.5, y + 0.5, a, b), 0, 1);
                    ref var cell = ref mask[(y - top) * boxWidth + (x - left)];
                    if (coverage > cell) cell = coverage;
                }
            }
        }

        var opacity = stroke.Tool == Tool.Highlighter ? 0.4f : 1f;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var alpha = mask[(y - top) * boxWidth + (x - left)] * opacity;
                if (alpha <= 0) continue;
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(pixels[i] + (stroke.Color.B - pixels[i]) * alpha);
                pixels[i + 1] = (byte)(pixels[i + 1] + (stroke.Color.G - pixels[i + 1]) * alpha);
                pixels[i + 2] = (byte)(pixels[i + 2] + (stroke.Color.R - pixels[i + 2]) * alpha);
                pixels[i + 3] = (byte)Math.Max(pixels[i + 3], 255 * alpha);
            }
        }
    }

    private static double Distance(double px, double py, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / lengthSquared, 0, 1);
        var cx = a.X + t * dx - px;
        var cy = a.Y + t * dy - py;
        return Math.Sqrt(cx * cx + cy * cy);
    }
}
