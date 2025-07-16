using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Transom.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace Transom.Controls;

public sealed partial class CapturePanel : UserControl
{
    private ShelfService? _shelf;
    private string? _imagePath;
    private string? _savedPath;

    public event Action? NewCaptureRequested;

    public event Action? EditorClosed;

    public CapturePanel()
    {
        InitializeComponent();
    }

    public void Attach(ShelfService shelf) => _shelf = shelf;

    public async Task ShowAsync(string imagePath)
    {
        _imagePath = imagePath;
        _savedPath = null;
        Status.Visibility = ShowInFolderLink.Visibility = OcrText.Visibility = Visibility.Collapsed;
        OcrText.Text = "";

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            using var stream = await file.OpenReadAsync();
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            Preview.Source = image;
            Info.Text = $"Screenshot · {image.PixelWidth} × {image.PixelHeight}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void NewCapture_Click(object sender, RoutedEventArgs e) => NewCaptureRequested?.Invoke();

    private void Edit_Click(object sender, RoutedEventArgs e) => OpenEditor(pickColor: false);

    private void PickColor_Click(object sender, RoutedEventArgs e) => OpenEditor(pickColor: true);

    private void OpenEditor(bool pickColor)
    {
        if (_imagePath is null) return;
        var editor = new CaptureEditor(_imagePath, pickColor);
        editor.Flattened += path => DispatcherQueue.TryEnqueue(async () => await ShowAsync(path));
        editor.Closed += (_, _) => EditorClosed?.Invoke();
        editor.Activate();
    }

    private async void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (_imagePath is null) return;
        CopyTextButton.IsEnabled = false;
        ShowStatus("Reading text…");
        try
        {
            var text = await RecognizeAsync(_imagePath);
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowStatus("No text found in this screenshot.");
                return;
            }
            var package = new DataPackage();
            package.SetText(text);
            ClipboardWriter.TrySet(package);
            OcrText.Text = text;
            OcrText.Visibility = Visibility.Visible;
            var lines = text.Split('\n').Length;
            ShowStatus($"Copied {lines} line{(lines == 1 ? "" : "s")} of text.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowStatus("Couldn't read text. Windows text recognition may not be available for your language.");
        }
        finally
        {
            CopyTextButton.IsEnabled = true;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveAsync() is { } path)
        {
            ShowStatus($"Saved to Pictures › Transom › {Path.GetFileName(path)}");
            ShowInFolderLink.Visibility = Visibility.Visible;
        }
    }

    private async void AddToShelf_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveAsync() is not { } path) return;
        _shelf?.Add([path]);
        ShowStatus("Saved to Pictures › Transom and added to your shelf.");
        ShowInFolderLink.Visibility = Visibility.Visible;
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_savedPath is null) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{_savedPath}\"");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task<string?> SaveAsync()
    {
        if (_savedPath is not null) return _savedPath;
        if (_imagePath is null) return null;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Transom");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, $"Capture {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            await Task.Run(() => File.Copy(_imagePath, target, overwrite: true));
            return _savedPath = target;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowStatus("Couldn't save the screenshot.");
            return null;
        }
    }

    private static async Task<string> RecognizeAsync(string path)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new InvalidOperationException("No OCR language available.");
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var scale = Math.Min(1.0, OcrEngine.MaxImageDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)(decoder.PixelWidth * scale),
            ScaledHeight = (uint)(decoder.PixelHeight * scale),
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join("\n", result.Lines.Select(l => l.Text));
    }

    private void ShowStatus(string text)
    {
        Status.Text = text;
        Status.Visibility = Visibility.Visible;
    }
}
