using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public static class EventSentrySelection
{
    public static int ApplyChecked(IEnumerable<ProxyEntry> source)
    {
        var rows = source.ToArray();
        // Resolve checked copies before deduplication: the same native profile
        // can appear in several loaded CSV lists.
        var selected = rows.Where(row => row.SyncSelected).Select(row => row.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) return 0;
        foreach (var row in rows) row.UseForEventSentry = selected.Contains(row.StableId);
        return selected.Count;
    }

    public static int CountSelected(IEnumerable<ProxyEntry> source) => source
        .Where(row => row.UseForEventSentry).Select(row => row.StableId)
        .Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public static void Clear(IEnumerable<ProxyEntry> source)
    {
        foreach (var row in source) row.UseForEventSentry = false;
    }
}
