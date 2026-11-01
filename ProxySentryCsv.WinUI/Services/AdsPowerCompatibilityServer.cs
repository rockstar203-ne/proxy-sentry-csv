using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ProxySentryCsv.Services;

internal sealed record AdsProfile(string Id, string Name, int Number, string Host, int Port, string Scheme, int ProcessId, bool Busy);
internal sealed record AdsConnection(int ProcessId, int Port, string WebSocket);

// Implements the read/start/stop subset used by the legacy AdsPower CDP client.
// It deliberately does not implement profile mutation or expose proxy credentials.
internal sealed class AdsPowerCompatibilityServer(
    Func<Task<IReadOnlyList<AdsProfile>>> list,
    Func<string, CancellationToken, Task<AdsConnection>> start,
    Func<string, int, CancellationToken, Task> stop)
{
    public const int DefaultPort = 50325;
    public const string GroupName = "Inventory Etix (DO NOT TOUCH)";
    private const string GroupId = "1";
    private readonly Dictionary<string, AdsConnection> _owned = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _gate = new();
    private WebApplication? _app;
    private CancellationTokenSource? _shutdown;
    private static readonly JsonSerializerOptions JsonOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    public bool IsRunning => _app is not null;
    public int Port { get; private set; }

    public async Task StartAsync(int port = DefaultPort)
    {
        if (_app is not null) return;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 1024;
            options.Limits.MaxConcurrentConnections = 32;
            options.Listen(IPAddress.Loopback, port);
        });
        var app = builder.Build();
        var shutdown = new CancellationTokenSource();
        var shutdownToken = shutdown.Token;
        app.Run(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Proxy-Sentry-Compatibility"] = "AdsPower-v1-CDP";
            var host = context.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost" or "local.adspower.net")
                || context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("Sec-Fetch-Site"))
            {
                context.Response.StatusCode = 403;
                await Write(context, Fail("Only local desktop API clients are allowed."));
                return;
            }
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                await Write(context, Fail("Profile changes and this HTTP method are not supported."));
                return;
            }
            try
            {
                var query = context.Request.Query.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
                await Write(context, await HandleAsync(context.Request.Path.Value ?? "", query, shutdownToken));
            }
            catch (OperationCanceledException) { if (!context.RequestAborted.IsCancellationRequested) await Write(context, Fail("API operation cancelled.")); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { await Write(context, Fail(ex.Message)); }
            catch { await Write(context, Fail("Browser operation failed. Check Proxy Sentry's status log.")); }
        });
        try
        {
            await app.StartAsync();
            Port = new Uri(app.Urls.Single()).Port;
            _shutdown = shutdown;
            _app = app;
        }
        catch { shutdown.Dispose(); await app.DisposeAsync(); throw; }
    }

    public async Task StopAsync()
    {
        var app = _app;
        if (app is null) return;
        _app = null;
        _shutdown!.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(timeout.Token); }
        finally { await app.DisposeAsync(); _shutdown.Dispose(); _shutdown = null; }
        // Stopping the API never closes users' browser windows. Ownership survives re-enabling it.
    }

    public async Task ChangePortAsync(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "API port must be between 1 and 65535.");
        if (IsRunning && Port == port) return;
        await _commands.WaitAsync();
        try
        {
            var previousPort = IsRunning ? Port : 0;
            await StopAsync();
            try { await StartAsync(port); }
            catch
            {
                // Keep the same ownership map and restore the working listener on bind failure.
                if (previousPort > 0) await StartAsync(previousPort);
                throw;
            }
        }
        finally { _commands.Release(); }
    }

    private async Task<object> HandleAsync(string path, IReadOnlyDictionary<string, string> q, CancellationToken token)
    {
        if (path == "/status") return Ok(new { service = "Proxy Sentry CSV", compatibility = "AdsPower Local API v1" });
        if (path == "/api/v1/group/list")
        {
            var page = Page(q, "page", 1, int.MaxValue);
            var size = Page(q, "page_size", 100, 100);
            var groups = new[] { new { group_id = GroupId, group_name = GroupName } };
            return Ok(new { list = page == 1 ? groups : [], page, page_size = size, total = 1 });
        }
        if (path is not ("/api/v1/user/list" or "/api/v1/browser/start" or "/api/v1/browser/stop"
            or "/api/v1/browser/active" or "/api/v1/browser/local-active")) return Fail("Unsupported compatibility API endpoint.");
        var profiles = await list();
        var id = q.GetValueOrDefault("user_id", "");
        if (id.Length == 0 && q.TryGetValue("serial_number", out var serial))
        {
            var matching = profiles.Where(p => p.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) == serial).ToArray();
            if (matching.Length != 1) return Fail("Serial number is unknown or ambiguous. Use user_id.");
            id = matching[0].Id;
        }
        if (path == "/api/v1/user/list")
        {
            var page = Page(q, "page", 1, int.MaxValue);
            var size = Page(q, "page_size", 100, 100);
            var filtered = profiles.Where(p => (id.Length == 0 || p.Id == id)
                && (!q.TryGetValue("group_id", out var group) || group == GroupId)).OrderBy(p => p.Number).ThenBy(p => p.Id).ToList();
            var offset = (long)(page - 1) * size;
            var items = filtered.Skip((int)Math.Min(offset, int.MaxValue)).Take(size).Select(p => new
            {
                user_id = p.Id, name = p.Name, serial_number = p.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                group_id = GroupId, group_name = GroupName,
                user_proxy_config = new { proxy_soft = "other", proxy_type = p.Scheme, proxy_host = p.Host, proxy_port = p.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            }).ToArray();
            return Ok(new { list = items, page, page_size = size, total = filtered.Count });
        }
        if (path == "/api/v1/browser/local-active" || path == "/api/v1/browser/active" && id.Length == 0)
        {
            // The legacy client calls /active without user_id; AdsPower documents /local-active for this.
            return Ok(new { list = profiles.Where(p => p.ProcessId > 0 || p.Busy).Select(p => new { user_id = p.Id }).ToArray() });
        }
        if (id.Length == 0) return Fail("user_id is required.");
        await _commands.WaitAsync(token);
        try
        {
            profiles = await list();
            var profile = profiles.SingleOrDefault(p => p.Id == id);
            AdsConnection? owned;
            lock (_gate) _owned.TryGetValue(id, out owned);
            var matches = owned is not null && profile?.ProcessId == owned.ProcessId;
            if (path == "/api/v1/browser/active")
                return Ok(new { status = profile?.ProcessId > 0 ? "Active" : "Inactive", ws = matches ? Ws(owned!) : null });
            if (path == "/api/v1/browser/stop")
            {
                if (owned is null) return profile?.ProcessId > 0 ? Fail("This window was not started by the compatibility API.") : Ok(new { });
                await stop(id, owned.ProcessId, token);
                lock (_gate) _owned.Remove(id);
                return Ok(new { });
            }
            if (profile is null) return Fail("Select this Chrome profile for Event Sentry in Proxy Sentry first.");
            if (q.GetValueOrDefault("headless", "0") != "0") return Fail("Headless mode is not supported by this compatibility mode.");
            if (q.ContainsKey("launch_args")) return Fail("Custom launch arguments are not supported.");
            if (matches) return Started(owned!);
            if (profile.ProcessId > 0 || profile.Busy) return Fail("Profile is already open or controlled elsewhere. It has not been changed.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var connection = await start(id, timeout.Token);
            lock (_gate) _owned[id] = connection;
            return Started(connection);
        }
        finally { _commands.Release(); }
    }

    private static object Ws(AdsConnection connection) => new { puppeteer = connection.WebSocket, selenium = $"127.0.0.1:{connection.Port}" };
    private static object Started(AdsConnection connection) => Ok(new { ws = Ws(connection), debug_port = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), webdriver = "" });
    private static object Ok(object data) => new { code = 0, msg = "success", data };
    private static object Fail(string message) => new { code = -1, msg = message, data = new { } };
    private static int Page(IReadOnlyDictionary<string, string> q, string name, int fallback, int max) =>
        !q.TryGetValue(name, out var raw) ? fallback : int.TryParse(raw, out var number) && number >= 1 && number <= max
            ? number : throw new ArgumentException($"Invalid {name}.");
    private static Task Write(HttpContext context, object body) => context.Response.WriteAsJsonAsync(body, JsonOptions);
}
