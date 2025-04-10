using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Storage;

namespace Transom.Services;

public sealed class ShelfItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public DateTimeOffset Added { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Path;

    [JsonIgnore]
    public bool IsFolder => Directory.Exists(Path);

    [JsonIgnore]
    public bool Exists => File.Exists(Path) || Directory.Exists(Path);

    [JsonIgnore]
    public long Size => File.Exists(Path) ? new FileInfo(Path).Length : 0;
}

[JsonSerializable(typeof(List<ShelfItem>))]
internal sealed partial class ShelfJson : JsonSerializerContext;

// The shelf keeps links to files where they already are; it never copies or moves them.
public sealed class ShelfService
{
    private readonly List<ShelfItem> _items;
    private readonly string? _path;

    public event Action? Changed;

    public IReadOnlyList<ShelfItem> Items => _items.OrderByDescending(i => i.Added).ToList();

    public ShelfService()
    {
        try
        {
            _path = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "shelf.json");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        _items = Load();
    }

    public int Add(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var existing = _items.Find(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Added = DateTimeOffset.Now;
                continue;
            }
            _items.Add(new ShelfItem { Path = path });
            added++;
        }
        Save();
        return added;
    }

    public void Remove(string id)
    {
        _items.RemoveAll(i => i.Id == id);
        Save();
    }

    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private void Save()
    {
        if (_path is not null)
        {
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_items, ShelfJson.Default.ListShelfItem));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
        Changed?.Invoke();
    }

    private List<ShelfItem> Load()
    {
        try
        {
            if (_path is not null && File.Exists(_path))
                return JsonSerializer.Deserialize(File.ReadAllText(_path), ShelfJson.Default.ListShelfItem) ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        return [];
    }
}
