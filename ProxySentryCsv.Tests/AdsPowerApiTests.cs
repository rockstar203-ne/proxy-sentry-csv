using System.Net;
using System.Text.Json;
using ProxySentryCsv.Services;

internal static class AdsPowerApiTests
{
    private sealed class Backend
    {
        public readonly Dictionary<string, AdsProfile> Profiles = Enumerable.Range(1, 205)
            .Select(n => new AdsProfile($"profile-{n}", $"Test {n}", n, "192.0.2.1", 8080, "http", 0, false))
            .ToDictionary(p => p.Id);
        public int Starts, Stops;
        private readonly object _gate = new();
        public AdsPowerCompatibilityServer Create() => new(
            () => { lock (_gate) return Task.FromResult<IReadOnlyList<AdsProfile>>(Profiles.Values.ToArray()); },
            async (id, token) =>
            {
                await Task.Delay(30, token);
                lock (_gate)
                {
                    Starts++;
                    Profiles[id] = Profiles[id] with { ProcessId = 9000 + Starts };
                    return new AdsConnection(9000 + Starts, 9222, "ws://127.0.0.1:9222/devtools/browser/test");
                }
            },
            (id, pid, token) =>
            {
                lock (_gate)
                {
                    if (Profiles[id].ProcessId != 0 && Profiles[id].ProcessId != pid)
                        throw new InvalidOperationException("Not owned");
                    Stops++;
                    Profiles[id] = Profiles[id] with { ProcessId = 0 };
                    return Task.CompletedTask;
                }
            });
    }

    private static HttpClient Client(int port) => new(new HttpClientHandler { UseProxy = false })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
    private static async Task<JsonElement> Get(HttpClient http, string path)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(path));
        return doc.RootElement.Clone();
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static bool Ok(JsonElement result) => result.GetProperty("code").GetInt32() == 0;
    private static JsonElement Data(JsonElement result) { Check(Ok(result), result.ToString()); return result.GetProperty("data"); }

    public static async Task ContractAsync()
    {
        var backend = new Backend();
        var server = backend.Create();
        await server.StartAsync(0);
        try
        {
            using var http = Client(server.Port);
            Check(Ok(await Get(http, "/status")), "status");
            var groups = Data(await Get(http, "/api/v1/group/list")).GetProperty("list");
            Check(groups[0].GetProperty("group_name").GetString() == AdsPowerCompatibilityServer.GroupName, "group name");
            var page = Data(await Get(http, "/api/v1/user/list?group_id=1&page=3&page_size=100")).GetProperty("list");
            Check(page.GetArrayLength() == 5, "pagination");
            Check(!page.ToString().Contains("proxy_password") && !page.ToString().Contains("proxy_user"), "credentials exposed");
            Check(Data(await Get(http, "/api/v1/user/list?group_id=missing")).GetProperty("list").GetArrayLength() == 0, "unknown group");
            Check(!Ok(await Get(http, "/api/v1/user/list?page=0")), "invalid page");
            Check(!Ok(await Get(http, "/api/v1/browser/active?serial_number=9999")), "unknown serial");
            Check(!Ok(await Get(http, "/api/v1/browser/start?user_id=missing")), "unknown profile");
            Check(!Ok(await Get(http, "/api/v1/browser/start?user_id=profile-1&headless=1")), "headless rejection");
            Check(!Ok(await Get(http, "/api/v1/browser/start?user_id=profile-1&launch_args=[]")), "custom arguments");
            var starts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Get(http, "/api/v1/browser/start?user_id=profile-1&open_tabs=1")));
            Check(starts.All(Ok) && backend.Starts == 1, "concurrent/idempotent start");
            Check(Data(starts[0]).GetProperty("ws").GetProperty("puppeteer").GetString()!.StartsWith("ws://127.0.0.1:"), "CDP shape");
            Check(Data(await Get(http, "/api/v1/browser/active?user_id=profile-1")).GetProperty("status").GetString() == "Active", "active status");
            Check(Data(await Get(http, "/api/v1/browser/active")).GetProperty("list").GetArrayLength() == 1, "legacy active list");
            Check(Data(await Get(http, "/api/v1/browser/local-active")).GetProperty("list").GetArrayLength() == 1, "official active list");
            backend.Profiles["profile-2"] = backend.Profiles["profile-2"] with { ProcessId = 777 };
            Check(!Ok(await Get(http, "/api/v1/browser/start?user_id=profile-2")), "manual start guard");
            Check(!Ok(await Get(http, "/api/v1/browser/stop?user_id=profile-2")) && backend.Stops == 0, "manual close guard");
            backend.Profiles["profile-1"] = backend.Profiles["profile-1"] with { ProcessId = 888 };
            Check(!Ok(await Get(http, "/api/v1/browser/stop?user_id=profile-1")), "reopened process guard");
            backend.Profiles["profile-1"] = backend.Profiles["profile-1"] with { ProcessId = 9001 };
            Check(Ok(await Get(http, "/api/v1/browser/stop?user_id=profile-1")), "stop");
            Check(Ok(await Get(http, "/api/v1/browser/stop?user_id=profile-1")) && backend.Stops == 1, "idempotent stop");
            Check(Data(await Get(http, "/api/v1/browser/active?user_id=profile-1")).GetProperty("status").GetString() == "Inactive", "inactive status");
            backend.Profiles["profile-3"] = backend.Profiles["profile-3"] with { Busy = true };
            Check(!Ok(await Get(http, "/api/v1/browser/start?user_id=profile-3")), "busy guard");
        }
        finally { await server.StopAsync(); }
    }

    public static async Task LifecycleAsync()
    {
        var backend = new Backend();
        var server = backend.Create();
        await server.StartAsync(0);
        try
        {
            using var http = Client(server.Port);
            foreach (var header in new[] { "Origin", "Sec-Fetch-Site", "Host" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/browser/start?user_id=profile-1");
                request.Headers.TryAddWithoutValidation(header, header == "Host" ? "example.com" : "https://example.com");
                using var response = await http.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.Forbidden, $"{header} must be rejected");
            }
            using (var response = await http.PostAsync("/api/v1/user/update", new StringContent("{}")))
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(!Ok(doc.RootElement), "mutations must be rejected");
            }
            Check(backend.Starts == 0, "blocked requests launched a browser");
            var conflict = backend.Create();
            var refused = false;
            try { await conflict.StartAsync(server.Port); }
            catch (IOException) { refused = true; }
            finally { await conflict.StopAsync(); }
            Check(refused && Ok(await Get(http, "/status")), "port conflict damaged first server");
            Check(Ok(await Get(http, "/api/v1/browser/start?user_id=profile-1")), "start before disable");
            await server.StopAsync();
            Check(backend.Stops == 0 && backend.Profiles["profile-1"].ProcessId > 0, "disable closed browser");
            await server.StartAsync(0);
            using var restarted = Client(server.Port);
            Check(Ok(await Get(restarted, "/api/v1/browser/start?user_id=profile-1")) && backend.Starts == 1, "lost ownership on re-enable");
            Check(Ok(await Get(restarted, "/api/v1/browser/stop?user_id=profile-1")), "stop after re-enable");
            Check(Ok(await Get(restarted, "/api/v1/browser/start?user_id=profile-1")), "start before port change");
            var startsBeforeChange = backend.Starts;
            var secondServer = new Backend().Create();
            await secondServer.StartAsync(0);
            var oldPort = server.Port;
            try
            {
                var failed = false;
                try { await server.ChangePortAsync(secondServer.Port); }
                catch (IOException) { failed = true; }
                Check(failed && server.Port == oldPort && Ok(await Get(restarted, "/status")), "failed port change must restore original listener");
                var newPort = secondServer.Port;
                await secondServer.StopAsync();
                await server.ChangePortAsync(newPort);
                using var moved = Client(newPort);
                Check(Ok(await Get(moved, "/api/v1/browser/start?user_id=profile-1")) && backend.Starts == startsBeforeChange, "port change lost browser ownership");
                foreach (var invalid in new[] { -1, 0, 65536 })
                {
                    var rejected = false;
                    try { await server.ChangePortAsync(invalid); }
                    catch (ArgumentOutOfRangeException) { rejected = true; }
                    Check(rejected && Ok(await Get(moved, "/status")), "invalid port changed working listener");
                }
                Check(Ok(await Get(moved, "/api/v1/browser/stop?user_id=profile-1")), "cannot close owned browser after port change");
            }
            finally { await secondServer.StopAsync(); }
        }
        finally { await server.StopAsync(); }
    }

    public static Task SettingsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "proxy-sentry-ads-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppStateStore(root);
            Check(store.LoadSettings().AdsPowerApiPort == 50325, "default port");
            store.SaveState([], new AppStateStore.AppSettings { AdsPowerCompatibilityEnabled = true, AdsPowerApiPort = 51325, AppTheme = "dark", FirefoxExecutablePath = @"C:\Browsers\firefox.exe" });
            var saved = new AppStateStore(root).LoadSettings();
            Check(saved.AdsPowerCompatibilityEnabled && saved.AdsPowerApiPort == 51325, "port setting did not survive reload");
            Check(saved.AppTheme == "dark" && saved.FirefoxExecutablePath == @"C:\Browsers\firefox.exe", "theme/browser settings lost");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    public static async Task<int> FixtureAsync()
    {
        var backend = new Backend();
        var server = backend.Create();
        await server.StartAsync(0);
        Console.WriteLine($"ADS_FIXTURE_PORT={server.Port}");
        try { await Task.WhenAny(Task.Run(Console.ReadLine), Task.Delay(TimeSpan.FromSeconds(45))); }
        finally { await server.StopAsync(); }
        return 0;
    }
}
