using System.Text.Json;

namespace ProxySentryCsv.Services;

// The synchronization engine uses a small CDP-shaped command vocabulary.
// Firefox translates it to WebDriver BiDi; it never receives CDP commands.
internal interface IBrowserSyncClient : IDisposable
{
    event Action<string, JsonElement>? EventReceived;
    Task<JsonElement> SendAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default, string? sessionId = null);
}

internal static class BrowserSyncClient
{
    public static async Task<IBrowserSyncClient> ConnectAsync(string provider, int port, CancellationToken token) =>
        provider.Equals("firefox", StringComparison.OrdinalIgnoreCase)
            ? await FirefoxSyncClient.ConnectAsync(port, token)
            : await ChromeDevToolsClient.ConnectAsync(port, token);
}
