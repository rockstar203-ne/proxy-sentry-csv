using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ProxySentryCsv.Models;
using ProxySentryCsv.Services;

if (args.Contains("--firefox-sync")) return await FirefoxSyncSmoke.RunAsync();
if (args.Contains("--chrome-sync")) return await FirefoxSyncSmoke.RunAsync("chrome");
if (args.Contains("--ads-fixture")) return await AdsPowerApiTests.FixtureAsync();

var tests = new (string Name, Func<Task> Run)[]
{
    ("AdsPower HTTP contract, pagination and ownership", AdsPowerApiTests.ContractAsync),
    ("AdsPower local access, lifecycle and port conflict", AdsPowerApiTests.LifecycleAsync),
    ("AdsPower port settings persist with compatible defaults", AdsPowerApiTests.SettingsAsync),
    ("Sync coordinates preserve zero and negative wheel deltas", SyncCoordinates),
    ("Firefox key mapping uses WebDriver values", FirefoxKeys),
    ("HTTP authentication and response", HttpProxy),
    ("407 becomes actionable 502", AuthFailure),
    ("CONNECT preserves coalesced bytes", ConnectBytes),
    ("POST half-close preserves response", HalfClose),
    ("SOCKS5 authentication and remote DNS", Socks),
    ("HTTPS proxy rejects untrusted certificate", Tls),
    ("Proxy / Direct / Proxy routing", Routes),
    ("Route switch closes existing tunnels", RouteCleanup),
    ("State backup recovery", State),
    ("Identity survives credential update", Identity),
    ("Ambiguous identities stay separate", Ambiguous),
    ("Invalid imported ports rejected", Ports),
    ("Follower queues preserve order independently", QueueIsolation),
    ("Follower queue overflow pauses explicitly", QueueOverflow),
    ("HTTP 403 does not mark transport dead", HttpDenied),
    ("Check cancellation propagates", CheckCancellation),
    ("History omits URL secrets", HistoryPrivacy),
    ("Concurrent history writers preserve entries", HistoryConcurrency),
    ("Sessions survive reload and recovery replacement", Sessions),
    ("Missing primary state restores backup", MissingState),
    ("Profile metadata persists without changing identity", Metadata),
    ("Cache-only clear preserves cookies and other browsers", ProfileCache),
    ("Reset affects only the selected browser profile", ProfileReset),
    ("Reset refuses running and invalid profiles", ProfileResetGuard),
    ("Built-in configuration is culture-independent and uses native UA", NativeConfiguration),
    ("Configuration migration preserves identity and stored dimensions", ConfigurationMigration),
    ("Damaged configuration fails explicitly without regeneration", ConfigurationCorruption),
    ("Unknown and gateway geo never invent a US location", UnknownGeo),
    ("Firefox UA migration preserves other preferences and backup", FirefoxNativeUa),
    ("Regional diagnostics distinguish mismatch and unknown", RegionReport),
    ("Configuration cancellation does not start lookup or save", ConfigurationCancellation)
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run().WaitAsync(TimeSpan.FromSeconds(15)); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static Task SyncCoordinates()
{
    using var json = System.Text.Json.JsonDocument.Parse("{\"zero\":0,\"up\":-500,\"bad\":\"0\",\"huge\":1e999}");
    Assert(ChromeSyncController.TryGetDouble(json.RootElement, "zero", 500) == 0, "Top/left must not reuse old coordinates");
    Assert(ChromeSyncController.TryGetDouble(json.RootElement, "up", 1) == -500, "Upward wheel delta lost");
    Assert(ChromeSyncController.TryGetDouble(json.RootElement, "bad", 7) == 7, "Invalid type must fall back");
    Assert(ChromeSyncController.TryGetDouble(json.RootElement, "huge", 7) == 7, "Non-finite value must fall back");
    return Task.CompletedTask;
}
static Task FirefoxKeys()
{
    Assert(FirefoxSyncClient.KeyValue("Enter") == "\uE007", "Enter mapping");
    Assert(FirefoxSyncClient.KeyValue("Control") == "\uE009", "Control mapping");
    Assert(FirefoxSyncClient.KeyValue("ArrowUp") == "\uE013", "Arrow mapping");
    Assert(FirefoxSyncClient.KeyValue("F12") == "\uE03C", "F12 mapping");
    Assert(FirefoxSyncClient.KeyValue("a") == "a", "Printable mapping");
    Assert(FirefoxSyncClient.KeyValue("Unidentified") is null, "Unsupported keys must not become arbitrary text");
    return Task.CompletedTask;
}

static ProxyEntry Proxy(int port, string scheme = "http") =>
    new() { Host = "127.0.0.1", Port = port, Scheme = scheme, Username = "test-user", Password = "test-pass" };
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Send(Stream stream, string text) => await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
static async Task<string> Header(Stream stream)
{
    var bytes = new List<byte>();
    var one = new byte[1];
    while (bytes.Count < 65536)
    {
        if (await stream.ReadAsync(one) == 0) throw new IOException("EOF reading test header");
        bytes.Add(one[0]);
        if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] {13, 10, 13, 10}))
            return Encoding.ASCII.GetString(bytes.ToArray());
    }
    throw new IOException("Test header too large");
}
static async Task<string> Fetch(int port, string target)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    await Send(client.GetStream(), $"GET {target} HTTP/1.1\r\nHost: test.invalid\r\nConnection: close\r\n\r\n");
    using var reader = new StreamReader(client.GetStream());
    return await reader.ReadToEndAsync();
}
static Task Reply(Stream stream, string body) => Send(stream,
    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");

static async Task HttpProxy()
{
    await using var server = new Server(async client =>
    {
        var header = await Header(client.GetStream());
        Assert(header.StartsWith("GET http://example.invalid/path"), "Expected absolute-form URI");
        Assert(header.Contains("Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("test-user:test-pass"))), "Missing auth");
        await Reply(client.GetStream(), "proxy-ok");
    });
    using var relay = LocalProxyRelay.Start(Proxy(server.Port));
    Assert((await Fetch(relay.Port, "http://example.invalid/path")).EndsWith("proxy-ok"), "Missing response");
    await server.Completion;
}
static async Task AuthFailure()
{
    await using var server = new Server(async client =>
    {
        await Header(client.GetStream());
        await Send(client.GetStream(), "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=test\r\nContent-Length: 0\r\n\r\n");
    });
    using var relay = LocalProxyRelay.Start(Proxy(server.Port));
    var response = await Fetch(relay.Port, "http://example.invalid/");
    Assert(response.StartsWith("HTTP/1.1 502"), response);
    Assert(!response.Contains("Proxy-Authenticate:"), "Auth prompt forwarded");
    Assert(relay.Snapshot.Failures == 1, "Failure not counted");
    await server.Completion;
}
static async Task ConnectBytes()
{
    await using var server = new Server(async client =>
    {
        Assert((await Header(client.GetStream())).StartsWith("CONNECT example.invalid:443"), "Bad CONNECT");
        await Send(client.GetStream(), "HTTP/1.1 200 Connection Established\r\n\r\nHELLO");
        var bytes = new byte[4];
        await client.GetStream().ReadExactlyAsync(bytes);
        Assert(Encoding.ASCII.GetString(bytes) == "PING", "Lost request read-ahead");
        await Send(client.GetStream(), "PONG");
    });
    using var relay = LocalProxyRelay.Start(Proxy(server.Port));
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, relay.Port);
    await Send(client.GetStream(), "CONNECT example.invalid:443 HTTP/1.1\r\nHost: example.invalid:443\r\n\r\nPING");
    await Header(client.GetStream());
    using var reader = new StreamReader(client.GetStream());
    Assert(await reader.ReadToEndAsync() == "HELLOPONG", "Lost response read-ahead");
    await server.Completion;
}
static async Task HalfClose()
{
    await using var server = new Server(async client =>
    {
        await Header(client.GetStream());
        await client.GetStream().ReadExactlyAsync(new byte[4]);
        await Task.Delay(80);
        await Reply(client.GetStream(), "post-ok");
    });
    using var relay = LocalProxyRelay.Start(Proxy(server.Port));
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, relay.Port);
    await Send(client.GetStream(), "POST http://example.invalid/ HTTP/1.1\r\nHost: example.invalid\r\nContent-Length: 4\r\n\r\nDATA");
    var stream = client.GetStream();
    client.Client.Shutdown(SocketShutdown.Send);
    using var reader = new StreamReader(stream);
    Assert((await reader.ReadToEndAsync()).EndsWith("post-ok"), "Response truncated");
    await server.Completion;
}
static async Task Socks()
{
    await using var server = new Server(async client =>
    {
        var s = client.GetStream();
        var prefix = new byte[2];
        await s.ReadExactlyAsync(prefix);
        Assert(prefix[0] == 5, "Not SOCKS5");
        var methods = new byte[prefix[1]];
        await s.ReadExactlyAsync(methods);
        Assert(methods.Contains((byte)2), "Auth not offered");
        await s.WriteAsync(new byte[] {5, 2});
        await s.ReadExactlyAsync(prefix);
        var user = new byte[prefix[1]];
        await s.ReadExactlyAsync(user);
        var size = new byte[1];
        await s.ReadExactlyAsync(size);
        var password = new byte[size[0]];
        await s.ReadExactlyAsync(password);
        Assert(Encoding.UTF8.GetString(user) == "test-user" && Encoding.UTF8.GetString(password) == "test-pass", "Wrong credentials");
        await s.WriteAsync(new byte[] {1, 0});
        var command = new byte[4];
        await s.ReadExactlyAsync(command);
        Assert(command[1] == 1 && command[3] == 3, "Expected remote DNS");
        await s.ReadExactlyAsync(size);
        var domain = new byte[size[0]];
        await s.ReadExactlyAsync(domain);
        Assert(Encoding.ASCII.GetString(domain) == "remote-name.invalid", "Wrong domain");
        await s.ReadExactlyAsync(prefix);
        await s.WriteAsync(new byte[] {5, 0, 0, 1, 127, 0, 0, 1, 0, 80});
        var header = await Header(s);
        Assert(header.StartsWith("GET /test HTTP/1.1"), "Not origin form");
        Assert(!header.Contains("Proxy-Authorization"), "Credentials leaked");
        await Reply(s, "socks-ok");
    });
    using var relay = LocalProxyRelay.Start(Proxy(server.Port, "socks5"));
    Assert((await Fetch(relay.Port, "http://remote-name.invalid/test")).EndsWith("socks-ok"), "Missing response");
    await server.Completion;
}
static async Task Tls()
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    var tlsSeen = false;
    await using var server = new Server(async client =>
    {
        using var tls = new SslStream(client.GetStream());
        try { await tls.AuthenticateAsServerAsync(certificate); tlsSeen = true; await Header(tls); }
        catch (Exception ex) when (ex is IOException or AuthenticationException) { tlsSeen = true; }
    });
    using var relay = LocalProxyRelay.Start(Proxy(server.Port, "https"));
    Assert((await Fetch(relay.Port, "http://example.invalid/")).StartsWith("HTTP/1.1 502"), "Untrusted proxy accepted");
    await server.Completion;
    Assert(tlsSeen && relay.Snapshot.LastError.Contains("TLS"), $"Expected TLS handshake with validation; seen={tlsSeen}; error={relay.Snapshot.LastError}");
}
static async Task Routes()
{
    await using var upstream = new Server(async client => { await Header(client.GetStream()); await Reply(client.GetStream(), "PROXY"); }, 2);
    await using var destination = new Server(async client =>
    {
        var header = await Header(client.GetStream());
        Assert(header.StartsWith("GET / HTTP/1.1"), "Not origin form");
        Assert(!header.Contains("Proxy-Authorization"), "Credentials leaked to direct destination");
        await Reply(client.GetStream(), "DIRECT");
    });
    using var relay = LocalProxyRelay.Start(Proxy(upstream.Port));
    var uri = $"http://127.0.0.1:{destination.Port}/";
    Assert((await Fetch(relay.Port, uri)).EndsWith("PROXY"), "Default route not Proxy");
    relay.SetRoute(ProxyRouteMode.Direct);
    Assert((await Fetch(relay.Port, uri)).EndsWith("DIRECT"), "Direct failed");
    relay.SetRoute(ProxyRouteMode.Proxy);
    Assert((await Fetch(relay.Port, uri)).EndsWith("PROXY"), "Proxy not restored");
    await upstream.Completion;
    await destination.Completion;
}
static async Task RouteCleanup()
{
    await using var upstream = new Server(async client =>
    {
        await Header(client.GetStream());
        await Send(client.GetStream(), "HTTP/1.1 200 OK\r\n\r\n");
        try { await client.GetStream().ReadAsync(new byte[1]); } catch (IOException) { }
    });
    using var relay = LocalProxyRelay.Start(Proxy(upstream.Port));
    using var browser = new TcpClient();
    await browser.ConnectAsync(IPAddress.Loopback, relay.Port);
    await Send(browser.GetStream(), "CONNECT test.invalid:443 HTTP/1.1\r\nHost: test.invalid\r\n\r\n");
    await Header(browser.GetStream());
    relay.SetRoute(ProxyRouteMode.Direct);
    try { Assert(await browser.GetStream().ReadAsync(new byte[1]) == 0, "Old tunnel survived"); }
    catch (IOException) { }
    await upstream.Completion;
}
static string TestDirectory() => Path.Combine(Path.GetTempPath(), "ProxySentryCsv-tests", Guid.NewGuid().ToString("N"));
static async Task QueueIsolation()
{
    using var slow = new WindowActionQueue(CancellationToken.None);
    using var fast = new WindowActionQueue(CancellationToken.None);
    var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var values = new List<int>();
    slow.Enqueue(async token => { entered.SetResult(); await hold.Task.WaitAsync(token); });
    await entered.Task;
    slow.Enqueue(_ => { values.Add(1); return Task.CompletedTask; }, "move");
    slow.Enqueue(_ => { values.Add(2); return Task.CompletedTask; }, "move");
    slow.Enqueue(_ => { values.Add(3); done.SetResult(); return Task.CompletedTask; });
    var fastDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fast.Enqueue(_ => { fastDone.SetResult(); return Task.CompletedTask; });
    await fastDone.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Assert(values.Count == 0, "Slow queue unexpectedly completed");
    hold.SetResult();
    await done.Task;
    Assert(values.SequenceEqual(new[] {2, 3}), "State coalescing crossed an input barrier");
}
static async Task QueueOverflow()
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var queue = new WindowActionQueue(CancellationToken.None, capacity: 1);
    queue.Enqueue(async token => { entered.SetResult(); await Task.Delay(10000, token); });
    await entered.Task;
    Assert(queue.Enqueue(_ => Task.CompletedTask), "First pending item rejected");
    Assert(!queue.Enqueue(_ => Task.CompletedTask) && queue.Snapshot.Paused, "Overflow did not pause");
}
static async Task HttpDenied()
{
    await using var server = new Server(async client =>
    {
        await Header(client.GetStream());
        await Send(client.GetStream(), "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    });
    using var relay = LocalProxyRelay.Start(Proxy(server.Port));
    var result = await new ProxySpeedChecker().CheckAsync(Proxy(1), new Uri("http://site.invalid/"), CancellationToken.None, relay);
    Assert(result.HttpStatus == 403 && result.SpeedGroup != "Dead" && result.ResultKind == "Site denied access", "Site denial misclassified");
    await server.Completion;
}
static async Task CheckCancellation()
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await new ProxySpeedChecker().CheckAsync(Proxy(1), new Uri("http://site.invalid/"), cancellation.Token);
        throw new Exception("Cancellation was swallowed");
    }
    catch (OperationCanceledException) { }
}
static Task State()
{
    var path = Path.Combine(TestDirectory(), "state.json");
    AtomicJsonFile.Write(path, new[] {"first"});
    AtomicJsonFile.Write(path, new[] {"second"});
    File.WriteAllText(path, "{interrupted");
    Assert(AtomicJsonFile.Read<string[]>(path)?.Single() == "first", "Backup recovery failed");
    return Task.CompletedTask;
}
static Task Identity()
{
    var dir = TestDirectory();
    var store = new ProfileIdentityStore(dir);
    var original = Proxy(8080); original.RowNumber = 2;
    var legacy = original.StableId;
    store.Bind(new[] { original }, Path.Combine(dir, "list.csv"));
    Assert(original.StableId == legacy, "Migration changed profile path");
    var updated = Proxy(8080); updated.RowNumber = 2; updated.Password = "new-password";
    store.Bind(new[] { updated }, Path.Combine(dir, "list.csv"));
    Assert(updated.StableId == legacy, "Password change lost profile");
    Assert(!File.ReadAllText(Path.Combine(dir, "profile-identities.json")).Contains("new-password"), "Credentials in index");
    return Task.CompletedTask;
}
static Task Ambiguous()
{
    var dir = TestDirectory();
    var store = new ProfileIdentityStore(dir);
    var a = Proxy(8080); a.RowNumber = 2;
    var b = Proxy(8080); b.RowNumber = 3; b.Password = "different";
    store.Bind(new[] { a, b }, Path.Combine(dir, "list.csv"));
    Assert(a.StableId != b.StableId, "Distinct profiles merged");
    return Task.CompletedTask;
}
static Task HistoryPrivacy()
{
    var dir = TestDirectory();
    var store = new ConnectionHistoryStore(dir);
    var proxy = Proxy(8080);
    store.Add(proxy, "test-version", new Uri("https://url-user:url-password@site.invalid/private-path?token=secret-token#fragment"),
        ProxyRouteMode.Proxy, new ProxyCheckResult(10, 20, "Fast", "HTTP 200"));
    var saved = File.ReadAllText(Path.Combine(dir, "connection-history.json"));
    foreach (var secret in new[] { "url-user", "url-password", "private-path", "secret-token", "test-user", "test-pass" })
        Assert(!saved.Contains(secret), "Sensitive value persisted in history: " + secret);
    Assert(store.Read().Single().Site == "https://site.invalid", "Unexpected stored site authority");
    return Task.CompletedTask;
}
static async Task HistoryConcurrency()
{
    var dir = TestDirectory();
    await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        new ConnectionHistoryStore(dir).Add(Proxy(8080 + i), "test", new Uri("https://site.invalid"),
            ProxyRouteMode.Proxy, new ProxyCheckResult(1, 2, "Fast", "HTTP 200")))));
    Assert(new ConnectionHistoryStore(dir).Read().Count == 16, "Concurrent checks lost history entries");
}
static Task Sessions()
{
    var dir = TestDirectory();
    var store = new WorkspaceSessionStore(dir);
    store.Save(new WorkspaceSession { Id = "saved", Name = "Named session", TargetUrl = "https://site.invalid", Profiles = [new("profile-one", "chrome")] });
    store.Save(new WorkspaceSession { Id = "recovery", Name = "Recovery", TargetUrl = "https://old.invalid" });
    store.Save(new WorkspaceSession { Id = "recovery", Name = "Recovery", TargetUrl = "https://new.invalid" });
    var loaded = new WorkspaceSessionStore(dir).Read();
    Assert(loaded.Count == 2 && loaded[0].TargetUrl == "https://new.invalid", "Recovery created duplicate sessions");
    Assert(loaded[1].Profiles.Single().ProfileId == "profile-one", "Session lost profile identity");
    return Task.CompletedTask;
}
static Task MissingState()
{
    var path = Path.Combine(TestDirectory(), "state.json");
    AtomicJsonFile.Write(path, new[] { "first" });
    AtomicJsonFile.Write(path, new[] { "second" });
    File.Delete(path);
    Assert(AtomicJsonFile.Read<string[]>(path)?.Single() == "first", "Missing primary did not restore backup");
    return Task.CompletedTask;
}
static Task Metadata()
{
    var dir = TestDirectory();
    var proxy = Proxy(8080);
    var id = proxy.StableId;
    proxy.DisplayName = "Profile name";
    proxy.Notes = "Local note\nSecond line";
    proxy.Tags = "work, urgent, WORK, , personal";
    proxy.Email = "user@example.test";
    proxy.IsOpen = true;
    var opened = proxy.LastUsedUtc;
    proxy.IsOpen = false;
    new AppStateStore(dir).SaveState([proxy], new AppStateStore.AppSettings());
    var saved = new AppStateStore(dir).LoadProxyStates()[id];
    Assert(proxy.StableId == id && saved.Notes == proxy.Notes && saved.Email == proxy.Email && saved.DisplayName == proxy.DisplayName, "Metadata changed identity or was not saved");
    Assert(saved.Tags == "work, urgent, personal" && saved.LastUsedUtc == opened && opened is not null, "Tags or last-opened timestamp were lost");
    return Task.CompletedTask;
}
static string CreateProfileFixture(string dir, ProxyEntry proxy, string browser)
{
    var path = Path.Combine(dir, "SystemBrowserProfilesClean", browser, "proxy-" + proxy.ProfileKey);
    var cache = Path.Combine(path, "Default", "Cache");
    Directory.CreateDirectory(cache);
    File.WriteAllText(Path.Combine(cache, "cache.bin"), "cache-data");
    File.WriteAllText(Path.Combine(path, "Default", "Cookies"), "saved-session");
    return path;
}
static async Task ProfileCache()
{
    var dir = TestDirectory();
    var proxy = Proxy(8080);
    var chrome = CreateProfileFixture(dir, proxy, "chrome");
    var edge = CreateProfileFixture(dir, proxy, "edge");
    var service = new BrowserCacheService(dir);
    var size = await service.GetProfileStorageAsync(proxy, "chrome");
    Assert(size.CacheBytes == 10 && size.TotalBytes > size.CacheBytes, "Incorrect profile size");
    await service.ClearProfileCacheAsync(proxy, "chrome");
    Assert(File.Exists(Path.Combine(chrome, "Default", "Cookies")), "Cache clear removed cookies");
    Assert(File.Exists(Path.Combine(edge, "Default", "Cache", "cache.bin")), "Cache clear affected other browser");
    Assert((await service.GetProfileStorageAsync(proxy, "chrome")).CacheBytes == 0, "Cache was not cleared");
}
static async Task ProfileReset()
{
    var dir = TestDirectory();
    var proxy = Proxy(8080);
    var id = proxy.StableId;
    var chrome = CreateProfileFixture(dir, proxy, "chrome");
    var edge = CreateProfileFixture(dir, proxy, "edge");
    await new BrowserCacheService(dir).ResetProfileAsync(proxy, "chrome", () => { });
    Assert(!Directory.Exists(chrome) && Directory.Exists(edge) && proxy.StableId == id, "Reset affected other profile or identity");
}
static async Task ProfileResetGuard()
{
    var dir = TestDirectory();
    var proxy = Proxy(8080);
    var path = CreateProfileFixture(dir, proxy, "chrome");
    var service = new BrowserCacheService(dir);
    proxy.IsOpen = true;
    try { await service.ResetProfileAsync(proxy, "chrome", () => { }); throw new Exception("Open profile reset accepted"); }
    catch (InvalidOperationException) { }
    proxy.IsOpen = false;
    try { await service.ResetProfileAsync(proxy, "chrome", () => throw new InvalidOperationException("Process still running")); throw new Exception("Process guard ignored"); }
    catch (InvalidOperationException) { }
    try { await service.ResetProfileAsync(proxy, "../chrome", () => { }); throw new Exception("Invalid provider accepted"); }
    catch (ArgumentException) { }
    Assert(File.Exists(Path.Combine(path, "Default", "Cookies")), "Refused reset changed data");
}
static ProxyGeoInfo KnownGeo() => new() { Source = "proxy-exit", CountryCode = "US", Locale = "en-US", TimeZone = "America/Los_Angeles", City = "Los Angeles" };
static Task NativeConfiguration()
{
    var culture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        var proxy = Proxy(8080);
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("uk-UA");
        var a = new BrowserForgeFingerprintService(TestDirectory()).LoadConfiguration(proxy, "chrome", 150, KnownGeo());
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var b = new BrowserForgeFingerprintService(TestDirectory()).LoadConfiguration(proxy, "chrome", 150, KnownGeo());
        Assert(System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b), "Machine culture changed configuration");
        Assert(a.UserAgent == "" && !a.Headers.ContainsKey("User-Agent") && a.WebGLRenderer == "", "Synthetic browser identity generated");
        Assert(a.AcceptLanguage == "en-US,en;q=0.9" && a.Source == BrowserForgeFingerprintService.ConfigurationSource, "Unexpected language or generator");
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    return Task.CompletedTask;
}
static Task ConfigurationMigration()
{
    var dir = TestDirectory(); var proxy = Proxy(8080); var id = proxy.StableId;
    var legacy = Path.Combine(dir, "BrowserForge", $"{proxy.ProfileKey}-firefox-150-US-en-US.json");
    AtomicJsonFile.Write(legacy, new BrowserForgeFingerprint { UserAgent = "Old UA", NoiseSeed = 42, Screen = new() { Width = 1700 }, Languages = ["uk-UA"], Headers = new() { ["user-agent"] = "Old UA" } });
    var service = new BrowserForgeFingerprintService(dir);
    var a = service.LoadConfiguration(proxy, "firefox", 150, KnownGeo());
    var b = service.LoadConfiguration(proxy, "firefox", 151, KnownGeo());
    Assert(a.NoiseSeed == 42 && b.Screen.Width == 1700 && proxy.StableId == id, "Migration reset profile configuration");
    Assert(a.UserAgent == "" && !a.Headers.Keys.Any(k => k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)), "Legacy UA was retained");
    Assert(a.Languages.SequenceEqual(new[] { "en-US", "en" }) && File.Exists(legacy), "Migration lost original or kept old primary language");
    return Task.CompletedTask;
}
static Task ConfigurationCorruption()
{
    var dir = TestDirectory(); var proxy = Proxy(8080); var service = new BrowserForgeFingerprintService(dir);
    var path = Path.Combine(dir, "BrowserForge", $"{proxy.ProfileKey}-chrome-native-v1.json");
    File.WriteAllText(path, "damaged");
    try { service.LoadConfiguration(proxy, "chrome", 150, KnownGeo()); throw new Exception("Corruption silently regenerated"); }
    catch (InvalidDataException) { }
    Assert(File.ReadAllText(path) == "damaged", "Corrupt configuration overwritten");
    return Task.CompletedTask;
}
static Task UnknownGeo()
{
    var config = BrowserForgeFingerprintService.CompleteConfiguration(new(), "chrome", new());
    Assert(config.CountryCode == "" && config.TimeZone == "" && config.Locale == "", "Unknown lookup invented regional values");
    config = BrowserForgeFingerprintService.CompleteConfiguration(new() { TimeZone = "Europe/Paris", Locale = "fr-FR" }, "chrome", new() { Source = "proxy-host", TimeZone = "America/New_York", Locale = "en-US" });
    Assert(config.TimeZone == "Europe/Paris" && config.GeoSource == "proxy-host", "Gateway was mistaken for exit");
    Assert(!new ProxyGeoInfo().HasCoordinates, "Unknown coordinates reported as valid");
    return Task.CompletedTask;
}
static Task FirefoxNativeUa()
{
    var dir = TestDirectory(); var path = Path.Combine(dir, "prefs.js");
    Directory.CreateDirectory(dir);
    const string other = "user_pref(\"network.proxy.http\", \"127.0.0.1\");\r\nuser_pref(\"browser.startup.homepage\", \"https://example.test/\");\r\n";
    var old = "// Mozilla preferences\r\nuser_pref(\"general.useragent.override\", \"old agent\");\r\n" + other;
    File.WriteAllText(path, old);
    FirefoxNativePreferences.Migrate(dir);
    FirefoxNativePreferences.Migrate(dir);
    Assert(File.ReadAllText(path) == "// Mozilla preferences\r\n" + other, "Other preferences changed");
    Assert(File.ReadAllText(path + ".before-native-ua.bak") == old, "Migration backup missing or overwritten");
    return Task.CompletedTask;
}
static Task RegionReport()
{
    var response = System.Text.Json.JsonSerializer.SerializeToElement(new { result = new { value = "{\"timezone\":\"Europe/Kiev\",\"locale\":\"en-US\",\"language\":\"en-US\"}" } });
    var expected = new BrowserForgeFingerprint { TimeZone = "America/Los_Angeles", Locale = "en-US", GeoSource = "proxy-exit" };
    Assert(BrowserRegionDiagnostics.Describe(response, expected).Contains("MISMATCH"), "Timezone mismatch not reported");
    Assert(BrowserRegionDiagnostics.Describe(response, new()).Contains("NO VERIFIED EXPECTATION"), "Unknown comparison called a match");
    Assert(BrowserRegionDiagnostics.SameZone("Europe/Kiev", "Europe/Kyiv"), "Equivalent timezone alias rejected");
    var html = BrowserRegionDiagnostics.BuildLocalPage(expected);
    Assert(html.Contains("connect-src 'none'") && html.Contains("new Worker") && !html.Contains("fetch("), "Local diagnostics sends data or skips worker");
    var output = Environment.GetEnvironmentVariable("PROXY_SENTRY_QA_REGION_HTML");
    if (!string.IsNullOrWhiteSpace(output)) File.WriteAllText(output, html);
    return Task.CompletedTask;
}
static async Task ConfigurationCancellation()
{
    var service = new BrowserForgeFingerprintService(TestDirectory());
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await service.GetOrCreateAsync(Proxy(8080), "chrome", 150, cancellationToken: cancellation.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
}
static Task Ports()
{
    var dir = TestDirectory();
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "list.txt");
    File.WriteAllLines(path, new[] {"host:0", "host:65536", "host:8080"});
    Assert(CsvProxyLoader.Load(path).Count == 1, "Invalid port accepted");
    return Task.CompletedTask;
}

sealed class Server : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    public int Port { get; }
    public Task Completion { get; }
    public Server(Func<TcpClient, Task> handler, int connections = 1)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Completion = Run(handler, connections);
    }
    private async Task Run(Func<TcpClient, Task> handler, int connections)
    {
        for (var i = 0; i < connections; i++)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await handler(client);
        }
    }
    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try { await Completion.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch { }
    }
}
