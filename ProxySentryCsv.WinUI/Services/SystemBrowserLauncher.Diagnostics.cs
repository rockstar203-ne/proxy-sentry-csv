using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed partial class SystemBrowserLauncher
{
    public async Task<string> CheckBrowserRegionsAsync(ProxyEntry proxy, CancellationToken cancellationToken = default)
    {
        TrackedBrowser browser;
        lock (_gate)
        {
            if (!_running.TryGetValue(proxy.StableId, out browser!)) return "Open the browser profile first.";
        }
        if (!browser.DebuggingPort.HasValue)
            return "Firefox: use the local check URL inside the profile. Automatic tab inspection is not enabled.";
        var config = _fingerprints.ReadConfiguration(proxy, browser.Provider);
        if (config is null) return "Regional configuration unavailable. Reopen this profile after updating.";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = await ChromeDevToolsClient.ConnectBrowserAsync(browser.DebuggingPort.Value, timeout.Token);
        var targets = await client.SendAsync("Target.getTargets", cancellationToken: timeout.Token);
        var report = new List<string>();
        var pages = targets.GetProperty("targetInfos").EnumerateArray()
            .Where(t => t.GetProperty("type").GetString() == "page").ToArray();
        foreach (var target in pages)
        {
            timeout.Token.ThrowIfCancellationRequested();
            string? session = null;
            try
            {
                var attached = await client.SendAsync("Target.attachToTarget", new { targetId = target.GetProperty("targetId").GetString(), flatten = true }, timeout.Token);
                session = attached.GetProperty("sessionId").GetString();
                var result = await client.SendAsync("Runtime.evaluate", new { expression = BrowserRegionDiagnostics.ReadExpression, returnByValue = true }, timeout.Token, session);
                report.Add($"Tab {report.Count + 1}: {BrowserRegionDiagnostics.Describe(result, config)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.Add($"Tab {report.Count + 1}: not verified (closed or unavailable).");
            }
            finally
            {
                if (session is not null)
                {
                    try { await client.SendAsync("Target.detachFromTarget", new { sessionId = session }, timeout.Token); }
                    catch (Exception) { }
                }
            }
        }
        var route = browser.Relay.Snapshot.Route;
        return $"Route: {route}. Checked {report.Count} tabs. No page settings changed.\n\n"
            + string.Join("\n\n", report) + "\n\nWorkers and HTTP headers are not inspected by this check. Use the local check URL for a dedicated worker report.";
    }
}
