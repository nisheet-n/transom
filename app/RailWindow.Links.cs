using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Transom.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Transom;

// Link tools in the selected-text panel: open, shorten (TinyURL, only when asked) and a QR code
// made on this PC.
public sealed partial class RailWindow
{
    private Uri? _link;
    private byte[]? _qrPng;

    private void ShowLinkTools(string? text)
    {
        _link = LinkTools.Detect(text);
        _qrPng = null;
        LinkSection.Visibility = _link is null ? Visibility.Collapsed : Visibility.Visible;
        ShortLinkRow.Visibility = Visibility.Collapsed;
        QrRow.Visibility = Visibility.Collapsed;
        LinkShortenButton.IsEnabled = true;
    }

    private async void LinkOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_link is not { } link) return;
        AiFlyout.Hide();
        await Windows.System.Launcher.LaunchUriAsync(link);
    }

    private async void LinkShorten_Click(object sender, RoutedEventArgs e)
    {
        if (_link is not { } link) return;
        LinkShortenButton.IsEnabled = false;
        ShowSelectionStatus("Shortening…");
        var shortened = await LinkTools.ShortenAsync(link);
        LinkShortenButton.IsEnabled = true;
        if (_link != link) return;
        if (shortened is null)
        {
            ShowSelectionStatus("Couldn't shorten this link. Check your connection and try again.");
            return;
        }
        ShortLinkText.Text = shortened;
        ShortLinkRow.Visibility = Visibility.Visible;
        CopyText(shortened);
        ShowSelectionStatus("Short link copied.");
    }

    private void ShortLinkCopy_Click(object sender, RoutedEventArgs e)
    {
        if (ShortLinkText.Text.Length == 0) return;
        CopyText(ShortLinkText.Text);
        ShowSelectionStatus("Short link copied.");
    }

    private async void LinkQr_Click(object sender, RoutedEventArgs e)
    {
        if (_link is not { } link) return;
        try
        {
            var text = ShortLinkRow.Visibility == Visibility.Visible ? ShortLinkText.Text : link.OriginalString;
            _qrPng = await Task.Run(() => LinkTools.QrPng(text));
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(_qrPng.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            QrImage.Source = bitmap;
            QrRow.Visibility = Visibility.Visible;
            SelectionStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            ShowSelectionStatus("This link is too long for a QR code.");
        }
    }

    private async void QrCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_qrPng is null) return;
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(_qrPng.AsBuffer());
        stream.Seek(0);
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        ShowSelectionStatus(ClipboardWriter.TrySet(package) ? "QR code copied as an image." : "Couldn't copy. Try again.");
    }

    private async void QrSave_Click(object sender, RoutedEventArgs e)
    {
        if (_qrPng is not { } png || _link is not { } link) return;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Transom");
            Directory.CreateDirectory(folder);
            var name = $"QR {link.Host} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";
            await File.WriteAllBytesAsync(Path.Combine(folder, string.Concat(name.Split(Path.GetInvalidFileNameChars()))), png);
            ShowSelectionStatus("Saved to Pictures › Transom.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            ShowSelectionStatus("Couldn't save the QR code.");
        }
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        ClipboardWriter.TrySet(package);
    }
}
