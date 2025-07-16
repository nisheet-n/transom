using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Transom.Services;

public enum ClipKind { Text, Link, Image }

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string? ImageFile { get; set; }
    public string Source { get; set; } = "";
    public DateTimeOffset Copied { get; set; }
    public bool Pinned { get; set; }
    public DateTimeOffset? PinnedAt { get; set; }
}

[JsonSerializable(typeof(List<ClipItem>))]
internal sealed partial class ClipJson : JsonSerializerContext;

public sealed class ClipboardService
{
    private const int MaxTextLength = 100_000;

    private readonly List<ClipItem> _items;
    private readonly string? _folder;
    private bool _ignoreNext;
    private bool _reading;
    private string? _pendingJson;
    private int _writing;

    public event Action? Changed;

    public IReadOnlyList<ClipItem> Items => _items;

    public ClipboardService()
    {
        try
        {
            _folder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "clipboard");
            Directory.CreateDirectory(_folder);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        _items = Load();
    }

    public async Task CaptureAsync(string source)
    {
        if (_ignoreNext)
        {
            _ignoreNext = false;
            return;
        }
        if (_reading) return;
        _reading = true;
        try
        {
            var view = Clipboard.GetContent();
            if (await IsPrivateAsync(view)) return;

            if (view.Contains(StandardDataFormats.Bitmap) && !view.Contains(StandardDataFormats.Text))
            {
                var file = await SaveImageAsync(await view.GetBitmapAsync());
                if (file is not null) Add(new ClipItem { Kind = ClipKind.Image, ImageFile = file, Source = source });
            }
            else if (view.Contains(StandardDataFormats.Text))
            {
                var text = (await view.GetTextAsync()).Trim();
                if (text.Length == 0) return;
                if (text.Length > MaxTextLength) text = text[..MaxTextLength];
                var isLink = !text.Contains(' ') && !text.Contains('\n')
                    && Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
                Add(new ClipItem { Kind = isLink ? ClipKind.Link : ClipKind.Text, Text = text, Source = source });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            _reading = false;
        }
    }

    public async Task PutOnClipboardAsync(ClipItem item)
    {
        var package = new DataPackage();
        if (item.Kind == ClipKind.Image && item.ImageFile is not null && _folder is not null)
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.Combine(_folder, Path.GetFileName(item.ImageFile)));
            package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        }
        else
        {
            package.SetText(item.Text);
        }
        _ignoreNext = true;
        ClipboardWriter.TrySet(package);
        item.Copied = DateTimeOffset.Now;
        SaveAndNotify();
    }

    public string? ImagePath(ClipItem item) =>
        item.ImageFile is not null && _folder is not null ? Path.Combine(_folder, Path.GetFileName(item.ImageFile)) : null;

    public void TogglePin(string id)
    {
        var item = _items.Find(i => i.Id == id);
        if (item is null) return;
        item.Pinned = !item.Pinned;
        item.PinnedAt = item.Pinned ? DateTimeOffset.Now : null;
        Trim();
        SaveAndNotify();
    }

    public void Remove(string id)
    {
        var item = _items.Find(i => i.Id == id);
        if (item is null) return;
        _items.Remove(item);
        DeleteImage(item);
        SaveAndNotify();
    }

    public void ClearUnpinned()
    {
        foreach (var item in _items.Where(i => !i.Pinned).ToList())
        {
            _items.Remove(item);
            DeleteImage(item);
        }
        SaveAndNotify();
    }

    private void Add(ClipItem item)
    {
        item.Copied = DateTimeOffset.Now;
        var existing = item.Kind == ClipKind.Image ? null : _items.Find(i => i.Kind != ClipKind.Image && i.Text == item.Text);
        if (existing is not null)
        {
            existing.Copied = item.Copied;
            existing.Source = item.Source;
        }
        else
        {
            _items.Add(item);
        }
        Trim();
        SaveAndNotify();
    }

    private void Trim()
    {
        foreach (var old in _items.Where(i => !i.Pinned).OrderByDescending(i => i.Copied).Skip(RailSettings.Current.ClipboardLimit).ToList())
        {
            _items.Remove(old);
            DeleteImage(old);
        }
    }

    // Password managers mark secrets with these clipboard formats so history tools skip them.
    private static async Task<bool> IsPrivateAsync(DataPackageView view)
    {
        if (view.Contains("ExcludeClipboardContentFromMonitorProcessing") || view.Contains("Clipboard Viewer Ignore")) return true;
        if (!view.Contains("CanIncludeInClipboardHistory")) return false;
        try
        {
            if (await view.GetDataAsync("CanIncludeInClipboardHistory") is IRandomAccessStream stream)
            {
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync(4);
                reader.ByteOrder = ByteOrder.LittleEndian;
                return reader.ReadUInt32() == 0;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        return false;
    }

    private async Task<string?> SaveImageAsync(RandomAccessStreamReference reference)
    {
        if (_folder is null) return null;
        using var source = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(source);
        var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var name = $"{Guid.NewGuid():N}.png";
        var folder = await StorageFolder.GetFolderFromPathAsync(_folder);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        return name;
    }

    private void DeleteImage(ClipItem item)
    {
        if (ImagePath(item) is not { } path) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    // History can reach several MB, so the file is written off the UI thread. Only the newest
    // snapshot matters: a save that arrives while one is being written replaces the queued one.
    private void SaveAndNotify()
    {
        if (_folder is not null)
        {
            Volatile.Write(ref _pendingJson, JsonSerializer.Serialize(_items, ClipJson.Default.ListClipItem));
            if (Interlocked.Exchange(ref _writing, 1) == 0) _ = Task.Run(WritePending);
        }
        Changed?.Invoke();
    }

    private void WritePending()
    {
        do
        {
            while (Interlocked.Exchange(ref _pendingJson, null) is { } json)
            {
                try
                {
                    SafeFile.Write(Path.Combine(_folder!, "history.json"), json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }
            Volatile.Write(ref _writing, 0);
        }
        while (Volatile.Read(ref _pendingJson) is not null && Interlocked.Exchange(ref _writing, 1) == 0);
    }

    public void Flush()
    {
        while (Volatile.Read(ref _writing) == 1) Thread.Sleep(10);
        if (Interlocked.Exchange(ref _pendingJson, null) is { } json && _folder is not null) SafeFile.Write(Path.Combine(_folder, "history.json"), json);
    }

    private List<ClipItem> Load()
    {
        try
        {
            var path = _folder is null ? null : Path.Combine(_folder, "history.json");
            if (path is not null && File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), ClipJson.Default.ListClipItem) ?? [];
        }
        catch (Exception ex)
        {
            if (ex is JsonException && _folder is not null) SafeFile.SetAside(Path.Combine(_folder, "history.json"));
            Debug.WriteLine(ex);
        }
        return [];
    }
}
