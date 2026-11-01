using System.Text;
using System.Text.Json;

namespace ProxySentryCsv.Services;

internal sealed class FirefoxSyncClient : IBrowserSyncClient
{
    private readonly ChromeDevToolsClient _transport;
    private readonly string _context;
    private readonly List<string> _preloads = [];
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private string? _binding;
    private int _modifiers;
    private int _disposed;
    private const string Channel = "proxy-sentry-sync";
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    private FirefoxSyncClient(ChromeDevToolsClient transport, string context)
    {
        _transport = transport;
        _context = context;
        _transport.EventReceived += OnEvent;
    }

    public event Action<string, JsonElement>? EventReceived;

    public static async Task<FirefoxSyncClient> ConnectAsync(int port, CancellationToken token)
    {
        ChromeDevToolsClient? transport = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        while (transport is null)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                transport = await ChromeDevToolsClient.ConnectWebSocketAsync(
                    new Uri($"ws://127.0.0.1:{port}/session"), timeout.Token).ConfigureAwait(false);
            }
            catch (System.Net.WebSockets.WebSocketException)
            {
                await Task.Delay(150, timeout.Token).ConfigureAwait(false);
            }
        }
        try
        {
            await transport.SendAsync("session.new", new { capabilities = new { } }, timeout.Token).ConfigureAwait(false);
            var tree = await transport.SendAsync("browsingContext.getTree", new { maxDepth = 0 }, timeout.Token).ConfigureAwait(false);
            var contexts = tree.GetProperty("contexts").EnumerateArray().ToList();
            if (contexts.Count == 0) throw new InvalidOperationException("Firefox has no page to synchronize.");
            // A sync profile starts with one tab. Do not silently attach to an extension page.
            var page = contexts.FirstOrDefault(c => !c.GetProperty("url").GetString()!.StartsWith("moz-extension:", StringComparison.Ordinal));
            if (page.ValueKind == JsonValueKind.Undefined) page = contexts[0];
            var client = new FirefoxSyncClient(transport, page.GetProperty("context").GetString()!);
            await transport.SendAsync("session.subscribe", new
            {
                events = new[] { "script.message", "browsingContext.navigationStarted", "browsingContext.fragmentNavigated" },
                contexts = new[] { client._context }
            }, timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch { transport.Dispose(); throw; }
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default, string? sessionId = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var p = parameters is null ? Empty : JsonSerializer.SerializeToElement(parameters);
        switch (method)
        {
            case "Page.enable":
            case "Runtime.enable": return Empty;
            case "Browser.getWindowForTarget": throw new NotSupportedException("Firefox window bounds use the native window handle.");
            case "Page.navigate":
                return await Command("browsingContext.navigate", new { context = _context, url = p.GetProperty("url").GetString(), wait = "none" }, token).ConfigureAwait(false);
            case "Runtime.evaluate":
                var expression = p.GetProperty("expression").GetString()!.Trim().TrimEnd(';');
                var result = await Command("script.evaluate", new
                {
                    expression = $"JSON.stringify(({expression}))",
                    target = new { context = _context }, awaitPromise = true, resultOwnership = "none"
                }, token).ConfigureAwait(false);
                ThrowScriptError(result);
                var remote = result.GetProperty("result");
                object? value = remote.TryGetProperty("value", out var serialized) && serialized.ValueKind == JsonValueKind.String
                    ? JsonSerializer.Deserialize<JsonElement>(serialized.GetString()!) : null;
                return JsonSerializer.SerializeToElement(new { result = new { value } });
            case "Runtime.addBinding":
                _binding = p.GetProperty("name").GetString()!;
                var declaration = $"(send) => {{ window[{JsonSerializer.Serialize(_binding)}] = send; }}";
                var arguments = new[] { new { type = "channel", value = new { channel = Channel } } };
                var preload = await Command("script.addPreloadScript", new
                {
                    functionDeclaration = declaration, arguments, contexts = new[] { _context }
                }, token).ConfigureAwait(false);
                _preloads.Add(preload.GetProperty("script").GetString()!);
                var bindingResult = await Command("script.callFunction", new
                {
                    functionDeclaration = declaration, arguments, target = new { context = _context }, awaitPromise = false
                }, token).ConfigureAwait(false);
                ThrowScriptError(bindingResult);
                return Empty;
            case "Page.addScriptToEvaluateOnNewDocument":
                var script = await Command("script.addPreloadScript", new
                {
                    functionDeclaration = "() => { " + p.GetProperty("source").GetString() + "\n}",
                    contexts = new[] { _context }
                }, token).ConfigureAwait(false);
                _preloads.Add(script.GetProperty("script").GetString()!);
                return Empty;
            case "Input.dispatchMouseEvent":
            case "Input.dispatchKeyEvent":
                await _inputGate.WaitAsync(token).ConfigureAwait(false);
                try { await DispatchInputAsync(method, p, token).ConfigureAwait(false); }
                finally { _inputGate.Release(); }
                return Empty;
            default: throw new NotSupportedException($"Firefox sync does not support {method}.");
        }
    }

    private Task<JsonElement> Command(string method, object parameters, CancellationToken token) =>
        _transport.SendAsync(method, parameters, token);

    private async Task DispatchInputAsync(string method, JsonElement p, CancellationToken token)
    {
        var type = p.GetProperty("type").GetString();
        var desired = p.TryGetProperty("modifiers", out var flags) ? flags.GetInt32() : 0;
        var isKey = method == "Input.dispatchKeyEvent";
        var key = isKey ? p.GetProperty("key").GetString() ?? "" : "";
        var keyMask = key switch { "Alt" => 1, "Control" => 2, "Meta" => 4, "Shift" => 8, _ => 0 };
        var modifiers = new List<object>();
        foreach (var (mask, name) in new[] { (1, "Alt"), (2, "Control"), (4, "Meta"), (8, "Shift") })
        {
            if (mask == keyMask || (_modifiers & mask) == (desired & mask)) continue;
            modifiers.Add(new { type = (desired & mask) != 0 ? "keyDown" : "keyUp", value = KeyValue(name) });
        }
        if (modifiers.Count > 0)
            await Perform("key", "keyboard", modifiers, token).ConfigureAwait(false);
        _modifiers = (desired & ~keyMask) | (_modifiers & keyMask);
        if (isKey)
        {
            var value = KeyValue(key);
            if (value is null) throw new NotSupportedException($"Firefox sync: unsupported key {key}.");
            var up = type == "keyUp";
            await Perform("key", "keyboard", new object[] { new { type = up ? "keyUp" : "keyDown", value } }, token).ConfigureAwait(false);
            if (keyMask != 0) _modifiers = up ? _modifiers & ~keyMask : _modifiers | keyMask;
            return;
        }
        var actions = new List<object>
        {
            new { type = "pointerMove", x = (int)Math.Round(p.GetProperty("x").GetDouble()),
                y = (int)Math.Round(p.GetProperty("y").GetDouble()), duration = 0, origin = "viewport" }
        };
        if (type is "mousePressed" or "mouseReleased")
        {
            var button = p.GetProperty("button").GetString() switch { "left" => 0, "middle" => 1, "right" => 2, _ => -1 };
            if (button < 0) throw new InvalidOperationException("Invalid Firefox pointer button.");
            actions.Add(new { type = type == "mousePressed" ? "pointerDown" : "pointerUp", button });
        }
        await Perform("pointer", "mouse", actions, token).ConfigureAwait(false);
    }

    private Task<JsonElement> Perform(string type, string id, IEnumerable<object> actions, CancellationToken token) =>
        Command("input.performActions", new { context = _context, actions = new[] { new { type, id, actions = actions.ToArray() } } }, token);

    internal static string? KeyValue(string key) => key switch
    {
        "Backspace" => "\uE003", "Tab" => "\uE004", "Enter" => "\uE007", "Shift" => "\uE008",
        "Control" => "\uE009", "Alt" => "\uE00A", "Pause" => "\uE00B", "Escape" => "\uE00C",
        "PageUp" => "\uE00E", "PageDown" => "\uE00F", "End" => "\uE010", "Home" => "\uE011",
        "ArrowLeft" => "\uE012", "ArrowUp" => "\uE013", "ArrowRight" => "\uE014", "ArrowDown" => "\uE015",
        "Insert" => "\uE016", "Delete" => "\uE017", "Meta" => "\uE03D",
        _ when key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 12 => ((char)(0xE030 + f)).ToString(),
        _ when key.EnumerateRunes().Count() == 1 => key,
        _ => null
    };

    private static void ThrowScriptError(JsonElement result)
    {
        if (result.TryGetProperty("type", out var type) && type.GetString() == "exception")
            throw new InvalidOperationException("Firefox sync script failed: " + result.GetProperty("exceptionDetails").GetProperty("text").GetString());
    }

    private void OnEvent(string method, JsonElement message)
    {
        if (!message.TryGetProperty("params", out var p)) return;
        if (method == "script.message")
        {
            if (_binding is null || p.GetProperty("channel").GetString() != Channel
                || p.GetProperty("source").GetProperty("context").GetString() != _context) return;
            if (!p.GetProperty("data").TryGetProperty("value", out var data)
                || data.ValueKind != JsonValueKind.String) return;
            EventReceived?.Invoke("Runtime.bindingCalled", JsonSerializer.SerializeToElement(new
            {
                @params = new { name = _binding, payload = data.GetString() }
            }));
        }
        else if (p.TryGetProperty("context", out var context) && context.GetString() == _context)
        {
            EventReceived?.Invoke("Page.navigatedWithinDocument", JsonSerializer.SerializeToElement(new
            {
                @params = new { url = p.GetProperty("url").GetString() }
            }));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _transport.EventReceived -= OnEvent;
        // End the control session, not the browser or its profile.
        try { Task.Run(CleanupAsync).GetAwaiter().GetResult(); }
        catch { }
        _transport.Dispose();
    }

    private async Task CleanupAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = timeout.Token;
        try
        {
            await Command("input.releaseActions", new { context = _context }, token).ConfigureAwait(false);
            if (_binding is not null)
                await Command("script.evaluate", new
                {
                    expression = $"delete window[{JsonSerializer.Serialize(_binding)}]; window.__proxySentryScrollToken = null; "
                        + "document.getElementById('__proxySentrySyncRoleBadge')?.remove(); document.getElementById('__proxySentrySyncRoleBorder')?.remove();",
                    target = new { context = _context }, awaitPromise = false
                }, token).ConfigureAwait(false);
            foreach (var id in _preloads)
                await Command("script.removePreloadScript", new { script = id }, token).ConfigureAwait(false);
        }
        finally { await Command("session.end", new { }, token).ConfigureAwait(false); }
    }
}
