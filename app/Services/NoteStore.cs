using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;
using Windows.Storage;

namespace Transom.Services;

public sealed class Note
{
    private static readonly char[] LineBreaks = ['\r', '\n'];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? PinnedAt { get; set; }

    [JsonIgnore]
    public bool Pinned => PinnedAt is not null;

    [JsonIgnore]
    public string Title
    {
        get
        {
            var line = Text.TrimStart().Split(LineBreaks, 2)[0].Trim();
            return line.Length == 0 ? "Untitled note" : line.Length > 60 ? line[..60] + "…" : line;
        }
    }

    [JsonIgnore]
    public string Preview
    {
        get
        {
            var rest = Text.TrimStart().Split(LineBreaks, 2);
            var body = rest.Length > 1 ? rest[1].ReplaceLineEndings(" ").Trim() : "";
            return body.Length > 80 ? body[..80] + "…" : body;
        }
    }
}

[JsonSerializable(typeof(List<Note>))]
internal sealed partial class NoteJson : JsonSerializerContext;

public sealed class NoteStore
{
    private readonly List<Note> _notes;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly string? _path;

    public event Action? Changed;

    public NoteStore(DispatcherQueue dispatcher)
    {
        try
        {
            _path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "notes.json");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        _notes = Load();

        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    // Pinned notes first, newest pin on top and never reshuffled by editing;
    // the rest by when they were last edited.
    public IReadOnlyList<Note> Ordered =>
        _notes.Where(n => n.Pinned).OrderByDescending(n => n.PinnedAt)
            .Concat(_notes.Where(n => !n.Pinned).OrderByDescending(n => n.Modified))
            .ToList();

    public Note? Find(string id) => _notes.Find(n => n.Id == id);

    public Note Create()
    {
        var note = new Note();
        _notes.Add(note);
        Changed?.Invoke();
        return note;
    }

    public void Update(Note note, string text)
    {
        if (note.Text == text) return;
        note.Text = text;
        note.Modified = DateTimeOffset.Now;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void TogglePin(Note note)
    {
        note.PinnedAt = note.Pinned ? null : DateTimeOffset.Now;
        SaveNow();
    }

    public void Delete(Note note)
    {
        _notes.Remove(note);
        SaveNow();
    }

    public void RemoveIfEmpty(Note? note)
    {
        if (note is not null && string.IsNullOrWhiteSpace(note.Text) && _notes.Remove(note)) SaveNow();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        if (_path is not null)
        {
            try
            {
                var keep = _notes.Where(n => !string.IsNullOrWhiteSpace(n.Text)).ToList();
                SafeFile.Write(_path, JsonSerializer.Serialize(keep, NoteJson.Default.ListNote));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
        Changed?.Invoke();
    }

    private List<Note> Load()
    {
        try
        {
            if (_path is not null && File.Exists(_path))
                return JsonSerializer.Deserialize(File.ReadAllText(_path), NoteJson.Default.ListNote) ?? [];
        }
        catch (Exception ex)
        {
            if (ex is JsonException) SafeFile.SetAside(_path!);
            Debug.WriteLine(ex);
        }
        return [];
    }
}
