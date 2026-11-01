using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ProxySentryCsv.Services;

internal sealed class ChromeDevToolsClient : IBrowserSyncClient
{
    private readonly ClientWebSocket _webSocket;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Task _receiveLoop;
    private int _nextCommandId;

    private ChromeDevToolsClient(ClientWebSocket webSocket)
    {
        _webSocket = webSocket;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public event Action<string, JsonElement>? EventReceived;

    internal static async Task<ChromeDevToolsClient> ConnectWebSocketAsync(Uri endpoint, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
            return new ChromeDevToolsClient(socket);
        }
        catch { socket.Dispose(); throw; }
    }

    public static async Task<ChromeDevToolsClient> ConnectAsync(int debuggingPort, CancellationToken cancellationToken)
    {
        var webSocketUrl = await GetPageWebSocketUrlAsync(debuggingPort, cancellationToken);
        var webSocket = new ClientWebSocket();
        await webSocket.ConnectAsync(new Uri(webSocketUrl), cancellationToken);
        return new ChromeDevToolsClient(webSocket);
    }

    public static async Task<ChromeDevToolsClient> ConnectBrowserAsync(int debuggingPort, CancellationToken cancellationToken)
    {
        var webSocketUrl = await GetBrowserWebSocketUrlAsync(debuggingPort, cancellationToken);
        var webSocket = new ClientWebSocket();
        await webSocket.ConnectAsync(new Uri(webSocketUrl), cancellationToken);
        return new ChromeDevToolsClient(webSocket);
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters = null, CancellationToken cancellationToken = default, string? sessionId = null)
    {
        var id = Interlocked.Increment(ref _nextCommandId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Could not register Chrome DevTools command.");
        }

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method
            };
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                payload["sessionId"] = sessionId;
            }
            if (parameters is not null)
            {
                payload["params"] = parameters;
            }

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendGate.Release();
            }

            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            _stop.Cancel();
            foreach (var pending in _pending.Values)
            {
                pending.TrySetCanceled();
            }

            if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Proxy Sentry CSV closing", timeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch
        {
        }
        finally
        {
            try
            {
                _receiveLoop.Wait(200);
            }
            catch
            {
            }

            _stop.Dispose();
            _sendGate.Dispose();
            _webSocket.Dispose();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested && _webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var response = await ReceiveWebSocketTextAsync(_webSocket, _stop.Token);
                using var document = JsonDocument.Parse(response);
                var root = document.RootElement.Clone();
                if (root.TryGetProperty("id", out var idElement)
                    && idElement.TryGetInt32(out var id)
                    && _pending.TryRemove(id, out var completion))
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        completion.TrySetException(new InvalidOperationException(error.ToString()));
                    }
                    else if (root.TryGetProperty("result", out var result))
                    {
                        completion.TrySetResult(result.Clone());
                    }
                    else
                    {
                        completion.TrySetResult(root);
                    }

                    continue;
                }

                if (root.TryGetProperty("method", out var methodElement))
                {
                    EventReceived?.Invoke(methodElement.GetString() ?? "", root);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(ex);
            }
        }
    }

    private static async Task<string> GetPageWebSocketUrlAsync(int debuggingPort, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var endpoint = $"http://127.0.0.1:{debuggingPort}/json/list";
        Exception? lastError = null;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await client.GetStringAsync(endpoint, cancellationToken);
                using var document = JsonDocument.Parse(json);
                foreach (var target in document.RootElement.EnumerateArray())
                {
                    if (target.TryGetProperty("type", out var type)
                        && string.Equals(type.GetString(), "page", StringComparison.OrdinalIgnoreCase)
                        && target.TryGetProperty("webSocketDebuggerUrl", out var webSocketUrl)
                        && !string.IsNullOrWhiteSpace(webSocketUrl.GetString()))
                    {
                        return webSocketUrl.GetString()!;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new InvalidOperationException(lastError?.Message ?? "Chrome DevTools page target was not available.");
    }

    private static async Task<string> GetBrowserWebSocketUrlAsync(int debuggingPort, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var endpoint = $"http://127.0.0.1:{debuggingPort}/json/version";
        Exception? lastError = null;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await client.GetStringAsync(endpoint, cancellationToken);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("webSocketDebuggerUrl", out var webSocketUrl)
                    && !string.IsNullOrWhiteSpace(webSocketUrl.GetString()))
                {
                    return webSocketUrl.GetString()!;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new InvalidOperationException(lastError?.Message ?? "Chrome DevTools browser target was not available.");
    }

    private static async Task<string> ReceiveWebSocketTextAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Chrome DevTools socket closed.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}
