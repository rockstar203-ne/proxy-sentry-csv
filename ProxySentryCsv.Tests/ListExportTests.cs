using ProxySentryCsv.Models;
using ProxySentryCsv.Services;

internal static class ListExportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProxySentry-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "favorites.csv");
            var proxy = new ProxyEntry { Host = "192.0.2.1", Port = 8080, Username = "user", Password = "pass", DisplayName = "Name, with \"quotes\"", Email = "test@example.test", BrowserProvider = "firefox" };
            await ProxyExporters.ExportProxyCsvAsync(path, new[] { proxy });
            var imported = CsvProxyLoader.Load(path);
            if (imported.Count != 1 || imported[0].DisplayName != proxy.DisplayName || imported[0].Email != proxy.Email)
                throw new Exception("CSV round trip failed");
            var before = await File.ReadAllTextAsync(path);
            if (OperatingSystem.IsWindows())
            {
                using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                var rejected = false;
                try { await ProxyExporters.ExportProxyCsvAsync(path, Array.Empty<ProxyEntry>()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
                if (!rejected) throw new Exception("Locked destination was overwritten");
            }
            if (before != await File.ReadAllTextAsync(path) || Directory.GetFiles(root, "*.tmp").Length != 0)
                throw new Exception("Failed export damaged the original or left a temporary file");
        }
        finally { Directory.Delete(root, true); }
    }
}
