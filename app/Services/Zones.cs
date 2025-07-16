namespace Transom.Services;

public static class Zones
{
    public static bool TryFind(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }

    public static string Name(TimeZoneInfo zone, string id)
    {
        if (id.Contains('/')) return id[(id.LastIndexOf('/') + 1)..].Replace('_', ' ');
        if (id == "UTC") return "UTC";
        var display = zone.DisplayName;
        var close = display.IndexOf(')');
        var name = close >= 0 ? display[(close + 1)..].Trim() : display;
        var comma = name.IndexOf(',');
        return comma > 0 ? name[..comma] : name;
    }

    // Windows names zones by their cities ("(UTC+09:00) Osaka, Sapporo, Tokyo"); the matching
    // city is shown, and the zone is stored by its IANA name so the row reads "Tokyo".
    public static List<(string Id, string Label)> Search(string query, ICollection<string> exclude)
    {
        var matches = new List<(string, string)>();
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (!zone.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) && !zone.StandardName.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var id = TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
            if (exclude.Contains(id) || exclude.Contains(zone.Id)) continue;
            var city = zone.DisplayName[(zone.DisplayName.IndexOf(')') + 1)..].Split(',').Select(c => c.Trim())
                .FirstOrDefault(c => c.Contains(query, StringComparison.OrdinalIgnoreCase)) ?? zone.StandardName;
            var offset = zone.DisplayName[..(zone.DisplayName.IndexOf(')') + 1)];
            matches.Add((id, $"{city} {offset}"));
            if (matches.Count == 5) break;
        }
        return matches;
    }

    public static string Offset(TimeSpan diff) =>
        diff == TimeSpan.Zero ? "same time" : $"{(diff < TimeSpan.Zero ? "−" : "+")}{diff.Duration():h\\:mm}";
}
