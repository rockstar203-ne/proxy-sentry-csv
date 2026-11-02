using ProxySentryCsv.Models;
using ProxySentryCsv.Services;

internal static class EventSentrySelectionTests
{
    public static Task RunAsync()
    {
        var a = new ProxyEntry { Host = "127.0.0.1", Port = 10001, UseForEventSentry = true };
        var aCopy = new ProxyEntry { Host = a.Host, Port = a.Port, SyncSelected = true };
        var b = new ProxyEntry { Host = a.Host, Port = 10002, SyncSelected = true };
        var c = new ProxyEntry { Host = a.Host, Port = 10003 };
        var rows = new[] { a, aCopy, b, c };
        Check(EventSentrySelection.ApplyChecked(rows) == 2, "Checked copies must be resolved before deduplication");
        Check(a.UseForEventSentry && aCopy.UseForEventSentry && b.UseForEventSentry && !c.UseForEventSentry, "All copies must agree");
        Check(EventSentrySelection.ApplyChecked(rows) == 2 && EventSentrySelection.CountSelected(rows) == 2, "Apply must be idempotent");
        foreach (var row in rows) row.SyncSelected = false;
        Check(EventSentrySelection.ApplyChecked(rows) == 0 && EventSentrySelection.CountSelected(rows) == 2, "Empty apply must preserve the existing pool");
        EventSentrySelection.Clear(rows);
        Check(rows.All(row => !row.UseForEventSentry), "Clear must also clear duplicate copies");
        c.SyncSelected = true;
        Check(EventSentrySelection.ApplyChecked(rows) == 1 && c.UseForEventSentry && !a.UseForEventSentry, "A new selection must replace the old pool");
        return Task.CompletedTask;
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
