namespace Transom.Models;

public sealed partial class ReminderRow(string id, string text, string when, bool isDue)
{
    public string Id { get; } = id;
    public string Text { get; } = text;
    public string When { get; } = when;
    public bool IsDue { get; } = isDue;
}
