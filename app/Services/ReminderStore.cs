using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;
using Windows.Storage;

namespace Transom.Services;

public sealed class Reminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public DateTimeOffset Due { get; set; }
    public bool Notified { get; set; }
}

[JsonSerializable(typeof(List<Reminder>))]
internal sealed partial class ReminderJson : JsonSerializerContext;

public sealed class ReminderStore
{
    private readonly List<Reminder> _items;
    private readonly DispatcherQueueTimer _timer;
    private readonly string? _path;

    public event Action? Changed;
    public event Action<Reminder>? Due;

    public IReadOnlyList<Reminder> Upcoming => _items.OrderBy(r => r.Due).ToList();

    public ReminderStore(DispatcherQueue dispatcher)
    {
        try
        {
            _path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "reminders.json");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        _items = Load();

        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(20);
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    public void Add(string text, DateTimeOffset due)
    {
        _items.Add(new Reminder { Text = string.IsNullOrWhiteSpace(text) ? "Reminder" : text.Trim(), Due = due });
        SaveAndNotify();
    }

    public void Remove(string id)
    {
        _items.RemoveAll(r => r.Id == id);
        SaveAndNotify();
    }

    public void Snooze(string id, TimeSpan by)
    {
        var reminder = _items.Find(r => r.Id == id);
        if (reminder is null) return;
        reminder.Due = DateTimeOffset.Now + by;
        reminder.Notified = false;
        SaveAndNotify();
    }

    public void Check()
    {
        var due = _items.Where(r => !r.Notified && r.Due <= DateTimeOffset.Now).ToList();
        foreach (var reminder in due)
        {
            reminder.Notified = true;
            Due?.Invoke(reminder);
        }
        if (due.Count > 0) SaveAndNotify();
        else Changed?.Invoke();
    }

    private void SaveAndNotify()
    {
        if (_path is not null)
        {
            try
            {
                SafeFile.Write(_path, JsonSerializer.Serialize(_items, ReminderJson.Default.ListReminder));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
        Changed?.Invoke();
    }

    private List<Reminder> Load()
    {
        try
        {
            if (_path is not null && File.Exists(_path))
                return JsonSerializer.Deserialize(File.ReadAllText(_path), ReminderJson.Default.ListReminder) ?? [];
        }
        catch (Exception ex)
        {
            if (ex is JsonException) SafeFile.SetAside(_path!);
            Debug.WriteLine(ex);
        }
        return [];
    }
}
