using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class SystemBrowserLauncher : IDisposable
{
    private readonly string _profilesRoot;
    private readonly BrowserForgeFingerprintService _fingerprints;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackedBrowser> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly ChromeSyncController _chromeSync = new();

    public SystemBrowserLauncher(string appDataPath, BrowserForgeFingerprintService fingerprints)
    {
        _profilesRoot = Path.Combine(appDataPath, "SystemBrowserProfilesClean");
        _fingerprints = fingerprints;
    }

    public event Action<IReadOnlyList<string>>? ProfilesClosed;

    public bool IsChromeSyncRunning => _chromeSync.IsRunning;

    public static bool IsSystemBrowser(string? provider)
    {
        return NormalizeProvider(provider) is "chrome" or "edge" or "firefox";
    }

    public async Task LaunchAsync(
        ProxyEntry proxy,
        string targetUrl,
        string provider,
        Action<string>? progress = null,
        SyncWindowPlacement? initialPlacement = null)
    {
        var normalizedProvider = NormalizeProvider(provider);
        if (!IsSystemBrowser(normalizedProvider))
        {
            throw new InvalidOperationException("Select Chrome, Edge, or Firefox to use an installed Windows browser.");
        }

        await CloseAsync(proxy);
        var executablePath = await ResolveBrowserExecutableAsync(normalizedProvider, progress);
        var browserVersion = GetBrowserVersion(executablePath);
        var browserMajor = browserVersion.Major;
        var fingerprint = await _fingerprints.GetOrCreateAsync(proxy, normalizedProvider, browserMajor, progress);
        ApplyFingerprintGeo(proxy, fingerprint);
        if (IsChromiumProvider(normalizedProvider))
        {
            ApplyChromiumBrowserVersion(fingerprint, normalizedProvider, browserVersion.FullVersion);
        }

        var profilePath = GetProfilePath(normalizedProvider, proxy);
        Directory.CreateDirectory(profilePath);
        progress?.Invoke("Starting proxy relay");
        var relay = LocalProxyRelay.Start(proxy, progress);
        IDisposable? devToolsSession = null;

        try
        {
            progress?.Invoke($"Launching {GetBrowserDisplayName(normalizedProvider)}");
            int? debuggingPort = null;
            string? chromiumExtensionPath = null;
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false
            };
            if (!string.IsNullOrWhiteSpace(fingerprint.TimeZone))
            {
                startInfo.Environment["TZ"] = fingerprint.TimeZone;
            }

            if (IsChromiumProvider(normalizedProvider))
            {
                debuggingPort = GetFreeLoopbackPort();
                chromiumExtensionPath = normalizedProvider == "chrome"
                    ? WriteChromiumBrowserForgeExtension(profilePath, fingerprint)
                    : null;
                WriteChromiumPreferences(profilePath, fingerprint);
                AddChromiumArguments(
                    startInfo,
                    profilePath,
                    "about:blank",
                    relay.Port,
                    fingerprint,
                    chromiumExtensionPath,
                    normalizedProvider,
                    debuggingPort.Value,
                    initialPlacement);
            }
            else
            {
                WriteFirefoxProfile(profilePath, relay.Port, fingerprint);
                WriteFirefoxBrowserForgeExtension(profilePath, fingerprint);
                startInfo.ArgumentList.Add("-no-remote");
                startInfo.ArgumentList.Add("-profile");
                startInfo.ArgumentList.Add(profilePath);
                startInfo.ArgumentList.Add("-new-window");
                startInfo.ArgumentList.Add(targetUrl);
            }

            var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {GetBrowserDisplayName(normalizedProvider)}.");
            process.EnableRaisingEvents = true;
            if (debuggingPort.HasValue)
            {
                devToolsSession = await ApplyChromiumDevToolsOverridesAsync(debuggingPort.Value, targetUrl, fingerprint, normalizedProvider, progress);
            }
            if (normalizedProvider != "firefox")
            {
                process.Exited += (_, _) => MarkClosed(proxy.StableId);
            }
            lock (_gate)
            {
                _running[proxy.StableId] = new TrackedBrowser(process, normalizedProvider, relay, devToolsSession, debuggingPort);
            }
        }
        catch
        {
            devToolsSession?.Dispose();
            relay.Dispose();
            throw;
        }

    }

    public async Task CloseAsync(ProxyEntry proxy, CancellationToken cancellationToken = default)
    {
        TrackedBrowser? tracked;
        lock (_gate)
        {
            if (!_running.TryGetValue(proxy.StableId, out tracked))
            {
                return;
            }

            _running.Remove(proxy.StableId);
        }

        var stableId = proxy.StableId;
        await Task.Run(() =>
        {
            _chromeSync.Stop();
            CloseTrackedBrowser(tracked);
        }, cancellationToken);

        ProfilesClosed?.Invoke([stableId]);
    }

    public async Task<IReadOnlyList<string>> CloseAllAsync(IEnumerable<ProxyEntry> proxies, CancellationToken cancellationToken = default)
    {
        var requestedIds = proxies.Select(proxy => proxy.StableId).ToHashSet(StringComparer.Ordinal);
        if (requestedIds.Count == 0)
        {
            return [];
        }

        List<KeyValuePair<string, TrackedBrowser>> tracked;
        lock (_gate)
        {
            tracked = _running
                .Where(pair => requestedIds.Contains(pair.Key))
                .ToList();
            foreach (var pair in tracked)
            {
                _running.Remove(pair.Key);
            }
        }

        if (tracked.Count == 0)
        {
            return [];
        }

        _chromeSync.Stop();
        await Task.WhenAll(tracked.Select(pair => Task.Run(() => CloseTrackedBrowser(pair.Value), cancellationToken)));
        var closedIds = tracked.Select(pair => pair.Key).ToList();
        ProfilesClosed?.Invoke(closedIds);
        return closedIds;
    }

    public void Close(ProxyEntry proxy)
    {
        CloseAsync(proxy).GetAwaiter().GetResult();
    }

    public async Task<int> NavigateChromeAsync(IReadOnlyList<ProxyEntry> proxies, string targetUrl, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Target URL must be an absolute URL.");
        }

        if (_chromeSync.IsRunning)
        {
            var count = await _chromeSync.NavigateAsync(targetUrl, cancellationToken);
            progress?.Invoke($"Chrome sync group navigated to {targetUrl}.");
            return count;
        }

        var sessions = GetOpenSyncSessions(proxies, "chromium-sync");
        if (sessions.Count == 0)
        {
            throw new InvalidOperationException("No opened Chrome windows are available.");
        }

        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = await ChromeDevToolsClient.ConnectAsync(session.DebuggingPort, cancellationToken);
            await client.SendAsync("Page.enable", cancellationToken: cancellationToken);
            await client.SendAsync("Page.navigate", new { url = targetUrl }, cancellationToken);
        }

        progress?.Invoke($"Navigated {sessions.Count} Chrome window(s) to {targetUrl}.");
        return sessions.Count;
    }

    private static void CloseTrackedBrowser(TrackedBrowser tracked)
    {
        try
        {
            if (tracked.Process.HasExited)
            {
                return;
            }

            if (tracked.Process.CloseMainWindow())
            {
                if (tracked.Process.WaitForExit(900))
                {
                    return;
                }
            }

            if (!tracked.Process.HasExited)
            {
                tracked.Process.Kill(entireProcessTree: true);
                tracked.Process.WaitForExit(800);
            }
        }
        catch
        {
            // Browser processes can exit while the user is closing windows manually.
        }
        finally
        {
            tracked.DevToolsSession?.Dispose();
            tracked.Relay.Dispose();
        }
    }

    public void Dispose()
    {
        _chromeSync.Stop();
        List<TrackedBrowser> running;
        lock (_gate)
        {
            running = _running.Values.ToList();
            _running.Clear();
        }

        foreach (var tracked in running)
        {
            try
            {
                if (!tracked.Process.HasExited)
                {
                    tracked.Process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                tracked.DevToolsSession?.Dispose();
                tracked.Relay.Dispose();
            }
        }
    }

    public async Task StartChromeSyncAsync(IReadOnlyList<ProxyEntry> proxies, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var sessions = GetOpenSyncSessions(proxies, "chromium-sync");
        await _chromeSync.StartAsync(sessions, progress, cancellationToken);
    }

    public void StopChromeSync()
    {
        _chromeSync.Stop();
    }

    public void SetChromeCtrlClickDelay(TimeSpan delay)
    {
        _chromeSync.SetCtrlClickDelay(delay);
    }

    public async Task ArrangeChromeAsync(IReadOnlyList<ProxyEntry> proxies, SyncMonitorOption? monitor, CancellationToken cancellationToken = default)
    {
        var sessions = GetOpenSyncSessions(proxies, "chromium-sync");
        await ChromeSyncController.ArrangeAsync(sessions, monitor, cancellationToken);
    }

    private IReadOnlyList<SystemBrowserSyncSession> GetOpenSyncSessions(IReadOnlyList<ProxyEntry> proxies, string provider)
    {
        var result = new List<SystemBrowserSyncSession>();
        lock (_gate)
        {
            foreach (var proxy in proxies)
            {
                if (!_running.TryGetValue(proxy.StableId, out var tracked)
                    || tracked.DebuggingPort is not > 0
                    || !MatchesSessionProvider(tracked.Provider, provider)
                    || tracked.Process.HasExited)
                {
                    continue;
                }

                result.Add(new SystemBrowserSyncSession(proxy.StableId, proxy.RowNumber, tracked.Process, tracked.Provider, tracked.DebuggingPort.Value));
            }
        }

        return result;
    }

    private static bool MatchesSessionProvider(string actualProvider, string requestedProvider)
    {
        return string.Equals(requestedProvider, "chromium-sync", StringComparison.OrdinalIgnoreCase)
            ? IsChromiumSyncProvider(actualProvider)
            : string.Equals(actualProvider, requestedProvider, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChromiumSyncProvider(string? provider)
    {
        return NormalizeProvider(provider) is "chrome";
    }

    private static bool IsChromiumProvider(string? provider)
    {
        return NormalizeProvider(provider) is "chrome" or "edge";
    }

    private static void AddChromiumArguments(
        ProcessStartInfo startInfo,
        string profilePath,
        string targetUrl,
        int relayPort,
        BrowserForgeFingerprint fingerprint,
        string? extensionPath,
        string provider,
        int debuggingPort,
        SyncWindowPlacement? initialPlacement)
    {
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add($"--user-data-dir={profilePath}");
        startInfo.ArgumentList.Add("--profile-directory=Default");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-sync");
        startInfo.ArgumentList.Add("--disable-session-crashed-bubble");
        startInfo.ArgumentList.Add("--disable-quic");
        startInfo.ArgumentList.Add("--disable-async-dns");
        startInfo.ArgumentList.Add("--disable-background-networking");
        startInfo.ArgumentList.Add("--disable-background-mode");
        startInfo.ArgumentList.Add("--disable-default-apps");
        startInfo.ArgumentList.Add("--disable-component-update");
        startInfo.ArgumentList.Add("--disable-domain-reliability");
        startInfo.ArgumentList.Add("--disable-client-side-phishing-detection");
        startInfo.ArgumentList.Add("--disable-features=UseDnsHttpsSvcb,UseDnsHttpsSvcbAlpn,DnsOverHttps,msEdgeStartupBoost,StartupBoost,EdgeStartupBoost");
        startInfo.ArgumentList.Add("--disable-preconnect");
        startInfo.ArgumentList.Add("--dns-prefetch-disable");
        startInfo.ArgumentList.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
        startInfo.ArgumentList.Add("--password-store=basic");
        startInfo.ArgumentList.Add("--metrics-recording-only");
        startInfo.ArgumentList.Add($"--remote-debugging-port={debuggingPort}");
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add("--remote-allow-origins=*");
        startInfo.ArgumentList.Add($"--lang={NormalizePrimaryLocale(fingerprint.Locale)}");
        startInfo.ArgumentList.Add($"--accept-lang={ToChromiumAcceptLanguage(fingerprint)}");
        startInfo.ArgumentList.Add($"--proxy-server=http://127.0.0.1:{relayPort}");
        startInfo.ArgumentList.Add("--proxy-bypass-list=<-loopback>");
        if (provider == "edge")
        {
            startInfo.ArgumentList.Add("--edge-skip-compat-layer-relaunch");
        }
        if (initialPlacement is { } placement)
        {
            startInfo.ArgumentList.Add($"--window-position={placement.X},{placement.Y}");
            startInfo.ArgumentList.Add($"--window-size={placement.Width},{placement.Height}");
        }
        else if (fingerprint.Screen.Width is > 0 && fingerprint.Screen.Height is > 0)
        {
            startInfo.ArgumentList.Add($"--window-size={fingerprint.Screen.Width.Value},{fingerprint.Screen.Height.Value}");
        }
        if (!string.IsNullOrWhiteSpace(extensionPath) && Directory.Exists(extensionPath))
        {
            startInfo.ArgumentList.Add($"--disable-extensions-except={extensionPath}");
            startInfo.ArgumentList.Add($"--load-extension={extensionPath}");
        }
        startInfo.ArgumentList.Add(targetUrl);
    }

    private static void ApplyFingerprintGeo(ProxyEntry proxy, BrowserForgeFingerprint fingerprint)
    {
        proxy.CountryCode = fingerprint.CountryCode;
        proxy.Region = fingerprint.Region;
        proxy.City = fingerprint.City;
    }

    private static void WriteChromiumPreferences(string profilePath, BrowserForgeFingerprint fingerprint)
    {
        var defaultProfilePath = Path.Combine(profilePath, "Default");
        Directory.CreateDirectory(defaultProfilePath);
        var preferencesPath = Path.Combine(defaultProfilePath, "Preferences");
        JsonObject root;
        try
        {
            root = File.Exists(preferencesPath)
                ? JsonNode.Parse(File.ReadAllText(preferencesPath)) as JsonObject ?? []
                : [];
        }
        catch
        {
            root = [];
        }

        SetJsonValue(root, ["intl"], "accept_languages", ToChromiumAcceptLanguage(fingerprint));
        SetJsonValue(root, ["profile", "default_content_setting_values"], "geolocation", 1);
        SetJsonValue(root, ["profile", "default_content_setting_values"], "media_stream_camera", 2);
        SetJsonValue(root, ["profile", "default_content_setting_values"], "media_stream_mic", 2);
        SetJsonValue(root, ["webrtc"], "ip_handling_policy", "disable_non_proxied_udp");
        SetJsonValue(root, ["webrtc"], "multiple_routes_enabled", false);
        SetJsonValue(root, ["webrtc"], "nonproxied_udp_enabled", false);
        SetJsonValue(root, ["dns_over_https"], "mode", "off");
        SetJsonValue(root, ["browser"], "has_seen_welcome_page", true);
        SetJsonValue(root, [], "credentials_enable_service", false);
        SetJsonValue(root, ["profile"], "password_manager_enabled", false);

        File.WriteAllText(preferencesPath, root.ToJsonString(AppJsonOptions.Indented), Encoding.UTF8);
    }

    private static void SetJsonValue(JsonObject root, string[] path, string propertyName, object? value)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (current[segment] is not JsonObject child)
            {
                child = [];
                current[segment] = child;
            }

            current = child;
        }

        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return;
        }

        current[propertyName] = JsonValue.Create(value);
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<IDisposable?> ApplyChromiumDevToolsOverridesAsync(
        int debuggingPort,
        string targetUrl,
        BrowserForgeFingerprint fingerprint,
        string provider,
        Action<string>? progress)
    {
        ChromeDevToolsClient? client = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            client = await ChromeDevToolsClient.ConnectAsync(debuggingPort, timeout.Token);
            await client.SendAsync("Page.enable", cancellationToken: timeout.Token);
            await client.SendAsync("Emulation.setTimezoneOverride", new { timezoneId = fingerprint.TimeZone }, timeout.Token);
            await client.SendAsync("Emulation.setLocaleOverride", new { locale = NormalizePrimaryLocale(fingerprint.Locale) }, timeout.Token);
            await client.SendAsync("Page.navigate", new { url = targetUrl }, timeout.Token);
            progress?.Invoke($"Chrome timezone applied: {fingerprint.TimeZone}, {fingerprint.Locale}.");
            var result = client;
            client = null;
            return result;
        }
        catch (Exception ex)
        {
            client?.Dispose();
            progress?.Invoke($"Chrome override failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<bool> TrySendCdpCommandAsync(
        ChromeDevToolsClient client,
        string method,
        object? parameters,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.SendAsync(method, parameters, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            progress?.Invoke($"{method} skipped: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> TrySendCdpCommandAsync(
        ClientWebSocket webSocket,
        int id,
        string method,
        object? parameters,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendCdpCommandAsync(webSocket, id, method, parameters, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            progress?.Invoke($"{method} skipped: {ex.Message}");
            return false;
        }
    }

    private static object BuildChromiumUserAgentOverride(BrowserForgeFingerprint fingerprint, bool includeMetadata)
    {
        var acceptLanguage = ToChromiumAcceptLanguageHeader(fingerprint);
        if (!includeMetadata)
        {
            return new
            {
                userAgent = fingerprint.UserAgent,
                acceptLanguage,
                platform = "Windows"
            };
        }

        var fullVersion = ExtractChromiumFullVersion(fingerprint);
        var majorVersion = fullVersion.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "120";
        var browserBrand = string.Equals(fingerprint.Browser, "edge", StringComparison.OrdinalIgnoreCase)
            ? "Microsoft Edge"
            : "Google Chrome";

        var brands = new[]
        {
            new { brand = "Not;A=Brand", version = "99" },
            new { brand = "Chromium", version = majorVersion },
            new { brand = browserBrand, version = majorVersion }
        };
        var fullVersionList = new[]
        {
            new { brand = "Not;A=Brand", version = "99.0.0.0" },
            new { brand = "Chromium", version = fullVersion },
            new { brand = browserBrand, version = fullVersion }
        };

        return new
        {
            userAgent = fingerprint.UserAgent,
            acceptLanguage,
            platform = "Windows",
            userAgentMetadata = new
            {
                brands,
                fullVersionList,
                fullVersion,
                platform = "Windows",
                platformVersion = "10.0.0",
                architecture = "x86",
                model = "",
                mobile = false,
                bitness = "64",
                wow64 = false
            }
        };
    }

    private static string ExtractChromiumFullVersion(BrowserForgeFingerprint fingerprint)
    {
        var userAgent = fingerprint.UserAgent ?? "";
        var browserMarker = string.Equals(fingerprint.Browser, "edge", StringComparison.OrdinalIgnoreCase) ? "Edg/" : "Chrome/";
        var match = Regex.Match(userAgent, $"{Regex.Escape(browserMarker)}(?<version>\\d+(?:\\.\\d+)+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups["version"].Value;
        }

        var fallback = Regex.Match(userAgent, "Chrome/(?<version>\\d+(?:\\.\\d+)+)", RegexOptions.IgnoreCase);
        return fallback.Success ? fallback.Groups["version"].Value : "120.0.0.0";
    }

    private static async Task<string> WaitForChromiumPageWebSocketAsync(int debuggingPort, CancellationToken cancellationToken)
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

    private static async Task SendCdpCommandAsync(ClientWebSocket webSocket, int id, string method, object? parameters, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method
        };
        if (parameters is not null)
        {
            payload["params"] = parameters;
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);

        while (true)
        {
            var response = await ReceiveWebSocketTextAsync(webSocket, cancellationToken);
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
            {
                continue;
            }

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(error.ToString());
            }

            return;
        }
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

    private static void WriteFirefoxProfile(string profilePath, int relayPort, BrowserForgeFingerprint fingerprint)
    {
        var prefs = new StringBuilder();
        prefs.AppendLine("user_pref(\"network.proxy.type\", 1);");
        prefs.AppendLine("user_pref(\"network.proxy.no_proxies_on\", \"\");");
        prefs.AppendLine("user_pref(\"network.proxy.share_proxy_settings\", false);");
        prefs.AppendLine("user_pref(\"signon.autologin.proxy\", true);");
        prefs.AppendLine("user_pref(\"network.trr.mode\", 5);");
        prefs.AppendLine("user_pref(\"network.dns.disablePrefetch\", true);");
        prefs.AppendLine("user_pref(\"network.dns.disablePrefetchFromHTTPS\", true);");
        prefs.AppendLine("user_pref(\"network.predictor.enabled\", false);");
        prefs.AppendLine("user_pref(\"network.http.speculative-parallel-limit\", 0);");
        prefs.AppendLine("user_pref(\"browser.fixup.dns_first_for_single_words\", false);");
        prefs.AppendLine("user_pref(\"media.peerconnection.enabled\", false);");
        prefs.AppendLine("user_pref(\"media.peerconnection.ice.proxy_only_if_behind_proxy\", true);");
        prefs.AppendLine("user_pref(\"media.peerconnection.ice.default_address_only\", true);");
        prefs.AppendLine("user_pref(\"media.peerconnection.ice.no_host\", true);");
        prefs.AppendLine($"user_pref(\"network.proxy.http\", {Json("127.0.0.1")});");
        prefs.AppendLine($"user_pref(\"network.proxy.http_port\", {relayPort});");
        prefs.AppendLine($"user_pref(\"network.proxy.ssl\", {Json("127.0.0.1")});");
        prefs.AppendLine($"user_pref(\"network.proxy.ssl_port\", {relayPort});");
        prefs.AppendLine($"user_pref(\"general.useragent.override\", {Json(fingerprint.UserAgent)});");
        prefs.AppendLine($"user_pref(\"intl.accept_languages\", {Json(ToFirefoxAcceptLanguages(fingerprint))});");
        prefs.AppendLine($"user_pref(\"intl.locale.requested\", {Json(NormalizePrimaryLocale(fingerprint.Locale))});");
        prefs.AppendLine("user_pref(\"intl.regional_prefs.use_os_locales\", false);");
        prefs.AppendLine($"user_pref(\"javascript.use_us_english_locale\", false);");
        prefs.AppendLine($"user_pref(\"privacy.resistFingerprinting\", false);");
        prefs.AppendLine($"user_pref(\"dom.webdriver.enabled\", false);");
        prefs.AppendLine("user_pref(\"extensions.enabledScopes\", 15);");
        prefs.AppendLine("user_pref(\"extensions.autoDisableScopes\", 0);");
        prefs.AppendLine("user_pref(\"extensions.install.requireBuiltInCerts\", false);");
        prefs.AppendLine("user_pref(\"extensions.install.requireSecureOrigin\", false);");
        prefs.AppendLine("user_pref(\"xpinstall.signatures.required\", false);");

        var prefsText = prefs.ToString();
        File.WriteAllText(Path.Combine(profilePath, "user.js"), prefsText, Encoding.UTF8);
        File.WriteAllText(Path.Combine(profilePath, "prefs.js"), prefsText, Encoding.UTF8);
    }

    private static void WriteFirefoxBrowserForgeExtension(string profilePath, BrowserForgeFingerprint fingerprint)
    {
        const string extensionId = "proxy-sentry-browserforge@local";
        var sourcePath = Path.Combine(profilePath, "ProxySentryFirefoxBrowserForgeExtension");
        var extensionsPath = Path.Combine(profilePath, "extensions");
        var xpiPath = Path.Combine(extensionsPath, $"{extensionId}.xpi");
        if (Directory.Exists(sourcePath))
        {
            Directory.Delete(sourcePath, recursive: true);
        }

        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(extensionsPath);

        File.WriteAllText(Path.Combine(sourcePath, "manifest.json"), """
{
  "manifest_version": 2,
  "name": "Proxy Sentry BrowserForge",
  "version": "1.2.0",
  "description": "Applies Proxy Sentry CSV BrowserForge profile values.",
  "applications": {
    "gecko": {
      "id": "proxy-sentry-browserforge@local",
      "strict_min_version": "91.0"
    }
  },
  "permissions": ["<all_urls>"],
  "content_scripts": [
    {
      "matches": ["<all_urls>"],
      "js": ["inject.js"],
      "run_at": "document_start",
      "all_frames": true
    }
  ]
}
""", Encoding.UTF8);

        var payload = JsonSerializer.Serialize(fingerprint);
        var script = $$"""
(() => {
  const fp = {{payload}};
  const source = `(() => {
    const fp = ${JSON.stringify(fp)};
    const locale = fp.locale || fp.Locale || "en-US";
    const languages = fp.languages || fp.Languages || [locale, locale.split("-")[0]];
    const timeZone = fp.timeZone || fp.TimeZone || "America/New_York";
    const defineGetter = (target, name, value) => {
      try {
        Object.defineProperty(target, name, { get: () => value, configurable: true });
      } catch (_) {}
    };
    try {
      const nav = Navigator.prototype;
      defineGetter(nav, "userAgent", fp.userAgent || fp.UserAgent || "");
      defineGetter(nav, "platform", fp.platform || fp.Platform || "Win32");
      defineGetter(nav, "language", locale);
      defineGetter(nav, "languages", languages);
      defineGetter(nav, "webdriver", false);
    } catch (_) {}

    const NativeDate = Date;
    const NativeDateTimeFormat = Intl && Intl.DateTimeFormat;
    const getPartsOffset = (date) => {
      try {
        const parts = new NativeDateTimeFormat("en-US", {
          timeZone,
          year: "numeric",
          month: "2-digit",
          day: "2-digit",
          hour: "2-digit",
          minute: "2-digit",
          second: "2-digit",
          hour12: false
        }).formatToParts(date).reduce((acc, part) => {
          if (part.type !== "literal") {
            acc[part.type] = part.value;
          }
          return acc;
        }, {});
        const hour = Number(parts.hour === "24" ? "0" : parts.hour);
        const asUtc = NativeDate.UTC(
          Number(parts.year),
          Number(parts.month) - 1,
          Number(parts.day),
          hour,
          Number(parts.minute),
          Number(parts.second)
        );
        return Math.round((date.getTime() - asUtc) / 60000);
      } catch (_) {
        return NativeDate.prototype.getTimezoneOffset.call(date);
      }
    };
    try {
      Object.defineProperty(NativeDate.prototype, "getTimezoneOffset", {
        value: function() {
          return getPartsOffset(this);
        },
        configurable: true
      });
    } catch (_) {}
    for (const methodName of ["toLocaleString", "toLocaleDateString", "toLocaleTimeString"]) {
      try {
        const original = NativeDate.prototype[methodName];
        Object.defineProperty(NativeDate.prototype, methodName, {
          value: function(locales, options) {
            return original.call(this, locales === undefined ? locale : locales, Object.assign({}, options || {}, { timeZone }));
          },
          configurable: true
        });
      } catch (_) {}
    }
    try {
      const original = Intl.DateTimeFormat.prototype.resolvedOptions;
      Object.defineProperty(Intl.DateTimeFormat.prototype, "resolvedOptions", {
        value: function() {
          const options = original.call(this);
          options.locale = locale;
          options.timeZone = timeZone;
          return options;
        },
        configurable: true
      });
    } catch (_) {}
    try {
      const OriginalDateTimeFormat = Intl.DateTimeFormat;
      const WrappedDateTimeFormat = function(locales, options) {
        return Reflect.construct(OriginalDateTimeFormat, [locales === undefined ? locale : locales, Object.assign({}, options || {}, { timeZone })], new.target || OriginalDateTimeFormat);
      };
      Object.setPrototypeOf(WrappedDateTimeFormat, OriginalDateTimeFormat);
      WrappedDateTimeFormat.prototype = OriginalDateTimeFormat.prototype;
      Object.defineProperty(Intl, "DateTimeFormat", { value: WrappedDateTimeFormat, configurable: true, writable: true });
    } catch (_) {}
  })();`;
  try {
    const element = document.documentElement || document.head || document.body;
    const script = document.createElement("script");
    script.textContent = source;
    if (element) {
      element.appendChild(script);
      script.remove();
    }
  } catch (_) {}
})();
""";
        File.WriteAllText(Path.Combine(sourcePath, "inject.js"), script, Encoding.UTF8);

        if (File.Exists(xpiPath))
        {
            File.Delete(xpiPath);
        }
        ZipFile.CreateFromDirectory(sourcePath, xpiPath, CompressionLevel.Fastest, includeBaseDirectory: false);
    }

    private static string WriteChromiumBrowserForgeExtension(string profilePath, BrowserForgeFingerprint fingerprint)
    {
        var extensionPath = Path.Combine(profilePath, "ProxySentryBrowserForgeExtension");
        Directory.CreateDirectory(extensionPath);
        File.WriteAllText(Path.Combine(extensionPath, "manifest.json"), """
{
  "manifest_version": 3,
  "name": "Proxy Sentry BrowserForge",
  "version": "1.3.0",
  "description": "Applies Proxy Sentry CSV browser privacy policy.",
  "permissions": ["privacy"],
  "host_permissions": ["<all_urls>"],
  "background": {
    "service_worker": "background.js"
  },
  "content_scripts": [
    {
      "matches": ["<all_urls>"],
      "js": ["inject.js"],
      "run_at": "document_start",
      "all_frames": true
    }
  ],
  "web_accessible_resources": [
    {
      "resources": ["page.js"],
      "matches": ["<all_urls>"]
    }
  ]
}
""", Encoding.UTF8);
        File.WriteAllText(Path.Combine(extensionPath, "background.js"), """
try {
  chrome.privacy.network.webRTCIPHandlingPolicy.set({ value: "disable_non_proxied_udp" });
  chrome.privacy.network.networkPredictionEnabled.set({ value: false });
} catch (_) {
}
""", Encoding.UTF8);

        var payload = JsonSerializer.Serialize(fingerprint);
        var script = $$"""
(() => {
  const fp = {{payload}};
  const applyBrowserForgeFingerprint = function(fp) {
    if (window.__proxySentryBrowserForgeApplied) {
      return;
    }
    try {
      Object.defineProperty(window, "__proxySentryBrowserForgeApplied", { value: true, configurable: false });
    } catch (_) {
      window.__proxySentryBrowserForgeApplied = true;
    }

    const defineGetter = (target, name, value) => {
      try {
        Object.defineProperty(target, name, { get: () => value, configurable: true });
      } catch (_) {}
    };

    const locale = fp.locale || fp.Locale || "en-US";
    const languages = fp.languages || fp.Languages || [locale, locale.split("-")[0]];
    const timeZone = fp.timeZone || fp.TimeZone || "America/New_York";
    const noiseSeed = Number(fp.noiseSeed || fp.NoiseSeed || 1);
    const webGLVendor = fp.webGLVendor || fp.WebGLVendor || "Google Inc. (Intel)";
    const webGLRenderer = fp.webGLRenderer || fp.WebGLRenderer || "ANGLE (Intel, Intel(R) UHD Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)";

    const nav = Navigator.prototype;
    defineGetter(nav, "userAgent", fp.userAgent || fp.UserAgent || "");
    defineGetter(nav, "platform", fp.platform || fp.Platform || "Win32");
    defineGetter(nav, "vendor", fp.vendor || fp.Vendor || "");
    defineGetter(nav, "language", locale);
    defineGetter(nav, "languages", languages);
    defineGetter(nav, "webdriver", false);
    if ((fp.hardwareConcurrency || fp.HardwareConcurrency) !== null && (fp.hardwareConcurrency || fp.HardwareConcurrency) !== undefined) {
      defineGetter(nav, "hardwareConcurrency", fp.hardwareConcurrency || fp.HardwareConcurrency);
    }
    if ((fp.deviceMemory || fp.DeviceMemory) !== null && (fp.deviceMemory || fp.DeviceMemory) !== undefined) {
      defineGetter(nav, "deviceMemory", fp.deviceMemory || fp.DeviceMemory);
    }
    if ((fp.maxTouchPoints || fp.MaxTouchPoints) !== null && (fp.maxTouchPoints || fp.MaxTouchPoints) !== undefined) {
      defineGetter(nav, "maxTouchPoints", fp.maxTouchPoints || fp.MaxTouchPoints);
    }

    const allowedFonts = new Set([
      "Arial",
      "Arial Black",
      "Calibri",
      "Cambria",
      "Consolas",
      "Courier New",
      "Georgia",
      "Segoe UI",
      "Tahoma",
      "Times New Roman",
      "Trebuchet MS",
      "Verdana"
    ]);
    const extractFontFamily = (font) => {
      try {
        const match = String(font || "").match(/(?:^|\s)(?:\d+(?:px|pt|em|rem)|normal|bold|italic|small-caps|lighter|bolder|[\d.]+)(?:\s|\/|,).*?(["']?)([A-Za-z][A-Za-z0-9\s-]+)\1(?:,|$)/);
        return match ? match[2].trim() : "";
      } catch (_) {
        return "";
      }
    };
    try {
      const fontSetProto = typeof FontFaceSet !== "undefined" ? FontFaceSet.prototype : null;
      if (fontSetProto && fontSetProto.check) {
        const nativeCheck = fontSetProto.check;
        Object.defineProperty(fontSetProto, "check", {
          value: function(font, text) {
            const family = extractFontFamily(font);
            if (family && !allowedFonts.has(family)) {
              return false;
            }
            return nativeCheck.call(this, font, text);
          },
          configurable: true
        });
      }
      if (fontSetProto && fontSetProto.load) {
        const nativeLoad = fontSetProto.load;
        Object.defineProperty(fontSetProto, "load", {
          value: function(font, text) {
            const family = extractFontFamily(font);
            if (family && !allowedFonts.has(family)) {
              return Promise.resolve([]);
            }
            return nativeLoad.call(this, font, text);
          },
          configurable: true
        });
      }
    } catch (_) {}

    const screenData = fp.screen || fp.Screen || {};
    const screenMap = {
      width: screenData.width || screenData.Width,
      height: screenData.height || screenData.Height,
      availWidth: screenData.availWidth || screenData.AvailWidth,
      availHeight: screenData.availHeight || screenData.AvailHeight,
      availLeft: screenData.availLeft || screenData.AvailLeft || 0,
      availTop: screenData.availTop || screenData.AvailTop || 0,
      colorDepth: screenData.colorDepth || screenData.ColorDepth,
      pixelDepth: screenData.pixelDepth || screenData.PixelDepth
    };
    for (const [key, value] of Object.entries(screenMap)) {
      if (value !== null && value !== undefined) {
        defineGetter(Screen.prototype, key, value);
      }
    }
    const dpr = screenData.devicePixelRatio || screenData.DevicePixelRatio;
    if (dpr !== null && dpr !== undefined) {
      defineGetter(window, "devicePixelRatio", dpr);
    }

    const noiseDelta = (salt) => {
      const value = ((noiseSeed ^ salt) * 1664525 + 1013904223) >>> 0;
      return (value % 3) - 1;
    };

    const clampByte = (value) => Math.max(0, Math.min(255, value));

    const applyImageNoise = (imageData, salt) => {
      try {
        if (!imageData || !imageData.data || imageData.data.length < 4) {
          return imageData;
        }
        const data = imageData.data;
        data[0] = clampByte(data[0] + noiseDelta(salt));
        data[1] = clampByte(data[1] + noiseDelta(salt + 17));
        data[2] = clampByte(data[2] + noiseDelta(salt + 31));
      } catch (_) {}
      return imageData;
    };

    let native2DGetImageData = null;
    let native2DPutImageData = null;
    try {
      const proto = typeof CanvasRenderingContext2D !== "undefined" ? CanvasRenderingContext2D.prototype : null;
      native2DGetImageData = proto && proto.getImageData;
      native2DPutImageData = proto && proto.putImageData;
      if (native2DGetImageData) {
        Object.defineProperty(proto, "getImageData", {
          value: function(...args) {
            return applyImageNoise(native2DGetImageData.apply(this, args), 101);
          },
          configurable: true
        });
      }
    } catch (_) {}

    const withCanvasNoise = (canvas, callback) => {
      let context = null;
      let original = null;
      try {
        context = canvas.getContext && canvas.getContext("2d", { willReadFrequently: true });
        if (context && native2DGetImageData && native2DPutImageData && canvas.width > 0 && canvas.height > 0) {
          original = native2DGetImageData.call(context, 0, 0, 1, 1);
          const changed = new ImageData(new Uint8ClampedArray(original.data), 1, 1);
          applyImageNoise(changed, 211);
          native2DPutImageData.call(context, changed, 0, 0);
        }
      } catch (_) {}
      try {
        return callback();
      } finally {
        try {
          if (context && original && native2DPutImageData) {
            native2DPutImageData.call(context, original, 0, 0);
          }
        } catch (_) {}
      }
    };

    try {
      const canvasProto = typeof HTMLCanvasElement !== "undefined" ? HTMLCanvasElement.prototype : null;
      const nativeToDataURL = canvasProto && canvasProto.toDataURL;
      const nativeToBlob = canvasProto && canvasProto.toBlob;
      if (nativeToDataURL) {
        Object.defineProperty(canvasProto, "toDataURL", {
          value: function(...args) {
            return withCanvasNoise(this, () => nativeToDataURL.apply(this, args));
          },
          configurable: true
        });
      }
      if (nativeToBlob) {
        Object.defineProperty(canvasProto, "toBlob", {
          value: function(...args) {
            return withCanvasNoise(this, () => nativeToBlob.apply(this, args));
          },
          configurable: true
        });
      }
    } catch (_) {}

    const patchWebGL = (proto) => {
      if (!proto) {
        return;
      }
      try {
        const nativeGetParameter = proto.getParameter;
        Object.defineProperty(proto, "getParameter", {
          value: function(parameter) {
            if (parameter === 37445) {
              return webGLVendor;
            }
            if (parameter === 37446) {
              return webGLRenderer;
            }
            return nativeGetParameter.call(this, parameter);
          },
          configurable: true
        });
      } catch (_) {}
      try {
        const nativeGetExtension = proto.getExtension;
        Object.defineProperty(proto, "getExtension", {
          value: function(name) {
            const extension = nativeGetExtension.call(this, name);
            if (extension && String(name).toLowerCase() === "webgl_debug_renderer_info") {
              try {
                Object.defineProperty(extension, "UNMASKED_VENDOR_WEBGL", { value: 37445, configurable: true });
                Object.defineProperty(extension, "UNMASKED_RENDERER_WEBGL", { value: 37446, configurable: true });
              } catch (_) {}
            }
            return extension;
          },
          configurable: true
        });
      } catch (_) {}
    };
    try {
      patchWebGL(typeof WebGLRenderingContext !== "undefined" ? WebGLRenderingContext.prototype : null);
      patchWebGL(typeof WebGL2RenderingContext !== "undefined" ? WebGL2RenderingContext.prototype : null);
    } catch (_) {}

    try {
      const audioProto = typeof AudioBuffer !== "undefined" ? AudioBuffer.prototype : null;
      const nativeGetChannelData = audioProto && audioProto.getChannelData;
      if (nativeGetChannelData) {
        Object.defineProperty(audioProto, "getChannelData", {
          value: function(...args) {
            const data = nativeGetChannelData.apply(this, args);
            try {
              if (data && !data.__proxySentryNoise) {
                const delta = noiseDelta(307) * 0.0000001;
                for (let index = 0; index < data.length; index += 100) {
                  data[index] = data[index] + delta;
                }
                Object.defineProperty(data, "__proxySentryNoise", { value: true, configurable: false });
              }
            } catch (_) {}
            return data;
          },
          configurable: true
        });
      }
    } catch (_) {}

    const nativeDateTimeFormat = Intl && Intl.DateTimeFormat;
    const nativeDate = Date;
    const getPartsOffset = (date) => {
      try {
        const parts = new nativeDateTimeFormat("en-US", {
          timeZone,
          year: "numeric",
          month: "2-digit",
          day: "2-digit",
          hour: "2-digit",
          minute: "2-digit",
          second: "2-digit",
          hour12: false
        }).formatToParts(date).reduce((acc, part) => {
          if (part.type !== "literal") {
            acc[part.type] = part.value;
          }
          return acc;
        }, {});
        const hour = Number(parts.hour === "24" ? "0" : parts.hour);
        const asUtc = nativeDate.UTC(
          Number(parts.year),
          Number(parts.month) - 1,
          Number(parts.day),
          hour,
          Number(parts.minute),
          Number(parts.second)
        );
        return Math.round((date.getTime() - asUtc) / 60000);
      } catch (_) {
        return nativeDate.prototype.getTimezoneOffset.call(date);
      }
    };

    try {
      Object.defineProperty(nativeDate.prototype, "getTimezoneOffset", {
        value: function() {
          return getPartsOffset(this);
        },
        configurable: true
      });
    } catch (_) {}

    for (const methodName of ["toLocaleString", "toLocaleDateString", "toLocaleTimeString"]) {
      try {
        const original = nativeDate.prototype[methodName];
        Object.defineProperty(nativeDate.prototype, methodName, {
          value: function(locales, options) {
            const nextLocales = locales === undefined ? locale : locales;
            const nextOptions = Object.assign({}, options || {}, { timeZone });
            return original.call(this, nextLocales, nextOptions);
          },
          configurable: true
        });
      } catch (_) {}
    }

    const timezoneLabel = (date) => {
      try {
        const parts = new nativeDateTimeFormat("en-US", { timeZone, timeZoneName: "short" }).formatToParts(date);
        return (parts.find(part => part.type === "timeZoneName") || {}).value || timeZone;
      } catch (_) {
        return timeZone;
      }
    };
    const offsetLabel = (date) => {
      const offset = -getPartsOffset(date);
      const sign = offset >= 0 ? "+" : "-";
      const absolute = Math.abs(offset);
      return `GMT${sign}${String(Math.floor(absolute / 60)).padStart(2, "0")}${String(absolute % 60).padStart(2, "0")}`;
    };
    try {
      Object.defineProperty(nativeDate.prototype, "toTimeString", {
        value: function() {
          const time = this.toLocaleTimeString("en-US", {
            timeZone,
            hour12: false,
            hour: "2-digit",
            minute: "2-digit",
            second: "2-digit"
          });
          return `${time} ${offsetLabel(this)} (${timezoneLabel(this)})`;
        },
        configurable: true
      });
    } catch (_) {}
    const dateParts = (date) => {
      try {
        return new nativeDateTimeFormat("en-US", {
          timeZone,
          weekday: "short",
          year: "numeric",
          month: "short",
          day: "2-digit"
        }).formatToParts(date).reduce((acc, part) => {
          if (part.type !== "literal") {
            acc[part.type] = part.value;
          }
          return acc;
        }, {});
      } catch (_) {
        return {};
      }
    };
    try {
      Object.defineProperty(nativeDate.prototype, "toDateString", {
        value: function() {
          const parts = dateParts(this);
          if (parts.weekday && parts.month && parts.day && parts.year) {
            return `${parts.weekday} ${parts.month} ${parts.day} ${parts.year}`;
          }
          return nativeDate.prototype.toLocaleDateString.call(this, "en-US", { timeZone });
        },
        configurable: true
      });
    } catch (_) {}
    try {
      Object.defineProperty(nativeDate.prototype, "toString", {
        value: function() {
          return `${this.toDateString()} ${this.toTimeString()}`;
        },
        configurable: true
      });
    } catch (_) {}

    const patchResolvedOptions = (ctor, patcher) => {
      try {
        const original = ctor.prototype.resolvedOptions;
        Object.defineProperty(ctor.prototype, "resolvedOptions", {
          value: function() {
            const options = original.call(this);
            options.locale = locale;
            patcher(options);
            return options;
          },
          configurable: true
        });
      } catch (_) {}
    };

    const wrapIntl = (name, defaults) => {
      try {
        const Original = Intl[name];
        const Wrapped = function(locales, options) {
          const nextLocales = locales === undefined ? locale : locales;
          const nextOptions = Object.assign({}, options || {}, defaults || {});
          return Reflect.construct(Original, [nextLocales, nextOptions], new.target || Original);
        };
        Object.setPrototypeOf(Wrapped, Original);
        Wrapped.prototype = Original.prototype;
        Object.defineProperty(Intl, name, { value: Wrapped, configurable: true, writable: true });
      } catch (_) {}
    };

    if (Intl) {
      wrapIntl("DateTimeFormat", { timeZone });
      wrapIntl("NumberFormat");
      wrapIntl("Collator");
      wrapIntl("PluralRules");
      wrapIntl("RelativeTimeFormat");
      wrapIntl("ListFormat");
      wrapIntl("DisplayNames");

      if (Intl.DateTimeFormat) {
        patchResolvedOptions(Intl.DateTimeFormat, options => { options.timeZone = timeZone; });
      }
      for (const name of ["NumberFormat", "Collator", "PluralRules", "RelativeTimeFormat", "ListFormat", "DisplayNames"]) {
        if (Intl[name]) {
          patchResolvedOptions(Intl[name], () => {});
        }
      }
    }

    const blockRtc = () => {
      for (const name of ["RTCPeerConnection", "webkitRTCPeerConnection"]) {
        const NativeRtc = window[name];
        if (!NativeRtc) {
          continue;
        }
        const WrappedRtc = function(configuration, constraints) {
          const nextConfig = Object.assign({}, configuration || {}, { iceTransportPolicy: "relay" });
          return new NativeRtc(nextConfig, constraints);
        };
        Object.setPrototypeOf(WrappedRtc, NativeRtc);
        WrappedRtc.prototype = NativeRtc.prototype;
        try {
          Object.defineProperty(window, name, { value: WrappedRtc, configurable: true, writable: true });
        } catch (_) {}
      }
    };
    blockRtc();

    let userAgentData = null;
    try {
      userAgentData = navigator.userAgentData;
    } catch (_) {}
    if (userAgentData) {
      const browser = fp.browser || fp.Browser || "chrome";
      const brand = browser === "edge" ? "Microsoft Edge" : browser === "firefox" ? "Firefox" : "Google Chrome";
      const userAgent = fp.userAgent || fp.UserAgent || "";
      const fullVersion = (userAgent.match(/(?:Edg|Chrome|Firefox)\/(\d+(?:\.\d+)+)/) || [])[1] || "120.0.0.0";
      const majorVersion = (fullVersion.match(/^(\d+)/) || [])[1] || "120";
      const brands = [
        { brand: "Not;A=Brand", version: "99" },
        { brand: "Chromium", version: majorVersion },
        { brand, version: majorVersion }
      ];
      const fullVersionList = [
        { brand: "Not;A=Brand", version: "99.0.0.0" },
        { brand: "Chromium", version: fullVersion },
        { brand, version: fullVersion }
      ];
      const uaPrototype = Object.getPrototypeOf(userAgentData);
      if (uaPrototype) {
        defineGetter(uaPrototype, "platform", "Windows");
        defineGetter(uaPrototype, "mobile", false);
        defineGetter(uaPrototype, "brands", brands);
        try {
          Object.defineProperty(uaPrototype, "getHighEntropyValues", {
            value: function(hints) {
              const requested = Array.isArray(hints) ? hints : [];
              const values = { brands, mobile: false, platform: "Windows" };
              for (const hint of requested) {
                if (hint === "architecture") values.architecture = "x86";
                if (hint === "bitness") values.bitness = "64";
                if (hint === "fullVersion") values.fullVersion = fullVersion;
                if (hint === "fullVersionList") values.fullVersionList = fullVersionList;
                if (hint === "model") values.model = "";
                if (hint === "platformVersion") values.platformVersion = "10.0.0";
                if (hint === "uaFullVersion") values.uaFullVersion = fullVersion;
                if (hint === "wow64") values.wow64 = false;
              }
              return Promise.resolve(values);
            },
            configurable: true
          });
        } catch (_) {}
      }
    }
  };

  try {
    applyBrowserForgeFingerprint(fp);
  } catch (_) {}

  const source = `(${applyBrowserForgeFingerprint.toString()})(${JSON.stringify(fp)});`;
  try {
    const element = document.documentElement || document.head || document.body;
    const script = document.createElement("script");
    script.textContent = source;
    if (element) {
      element.appendChild(script);
      script.remove();
    }
  } catch (_) {
  }
})();
""";
        File.WriteAllText(Path.Combine(extensionPath, "page.js"), script, Encoding.UTF8);
        File.WriteAllText(Path.Combine(extensionPath, "inject.js"), """
(() => {
  const inject = () => {
    if (window.__proxySentryBrowserForgeInjectRequested) {
      return true;
    }
    const target = document.documentElement || document.head || document.body;
    if (!target) {
      return false;
    }
    window.__proxySentryBrowserForgeInjectRequested = true;
    const script = document.createElement("script");
    script.src = chrome.runtime.getURL("page.js");
    script.onload = () => script.remove();
    (document.head || document.documentElement || target).prepend(script);
    return true;
  };

  if (!inject()) {
    const observer = new MutationObserver(() => {
      if (inject()) {
        observer.disconnect();
      }
    });
    observer.observe(document, { childList: true, subtree: true });
  }
})();
""", Encoding.UTF8);
        return extensionPath;
    }

    private string GetProfilePath(string provider, ProxyEntry proxy)
    {
        return Path.Combine(_profilesRoot, provider, $"proxy-{proxy.ProfileKey}");
    }

    private static Task<string> ResolveBrowserExecutableAsync(string provider, Action<string>? progress)
    {
        return Task.FromResult(ResolveBrowserExecutable(provider));
    }

    private static string ResolveBrowserExecutable(string provider)
    {
        foreach (var candidate in GetBrowserCandidates(provider))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var executableName = provider switch
        {
            "chrome" => "chrome.exe",
            "edge" => "msedge.exe",
            "firefox" => "firefox.exe",
            _ => ""
        };
        var fromPath = FindOnPath(executableName);
        if (fromPath is not null)
        {
            return fromPath;
        }

        throw new InvalidOperationException($"{GetBrowserDisplayName(provider)} was not found in Windows. Install it or add it to PATH.");
    }

    private static BrowserVersion GetBrowserVersion(string executablePath)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executablePath);
            var raw = !string.IsNullOrWhiteSpace(version.ProductVersion)
                ? version.ProductVersion
                : version.FileVersion;
            var fullVersion = ExtractVersionString(raw);
            var first = fullVersion?.Split(['.', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            int? majorVersion = int.TryParse(first, out var major) && major > 0 ? major : null;
            return new BrowserVersion(majorVersion, fullVersion);
        }
        catch
        {
            return new BrowserVersion(null, null);
        }
    }

    private static string? ExtractVersionString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var match = Regex.Match(raw, @"\d+(?:\.\d+){1,3}");
        return match.Success ? match.Value : null;
    }

    private static void ApplyChromiumBrowserVersion(BrowserForgeFingerprint fingerprint, string provider, string? fullVersion)
    {
        if (string.IsNullOrWhiteSpace(fullVersion) || string.IsNullOrWhiteSpace(fingerprint.UserAgent))
        {
            return;
        }

        fingerprint.UserAgent = Regex.Replace(fingerprint.UserAgent, @"Chrome/\d+(?:\.\d+)+", $"Chrome/{fullVersion}", RegexOptions.IgnoreCase);
        if (provider == "edge")
        {
            fingerprint.UserAgent = Regex.Replace(fingerprint.UserAgent, @"Edg/\d+(?:\.\d+)+", $"Edg/{fullVersion}", RegexOptions.IgnoreCase);
        }

        fingerprint.Headers["User-Agent"] = fingerprint.UserAgent;
    }

    private static IEnumerable<string> GetBrowserCandidates(string provider)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return provider switch
        {
            "chrome" =>
            [
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")
            ],
            "edge" =>
            [
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe")
            ],
            "firefox" =>
            [
                Path.Combine(programFiles, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(programFilesX86, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(localAppData, "Mozilla Firefox", "firefox.exe")
            ],
            _ => []
        };
    }

    private static string? FindOnPath(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static string NormalizeProvider(string? provider)
    {
        return provider?.Trim().ToLowerInvariant() switch
        {
            "portable-chromium" or "portable chromium" or "chromium" or "portable" => "chrome",
            "chrome" => "chrome",
            "edge" => "edge",
            "firefox" => "firefox",
            _ => ""
        };
    }

    private static string GetBrowserDisplayName(string provider)
    {
        return provider switch
        {
            "chrome" => "Chrome",
            "edge" => "Edge",
            "firefox" => "Firefox",
            _ => "Browser"
        };
    }

    private static string Json(string value)
    {
        return JsonSerializer.Serialize(value);
    }

    private static string NormalizePrimaryLocale(string? locale)
    {
        return string.IsNullOrWhiteSpace(locale) ? "en-US" : locale.Trim();
    }

    private static string ToChromiumAcceptLanguage(BrowserForgeFingerprint fingerprint)
    {
        var languages = fingerprint.Languages
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Select(language => language.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (languages.Count == 0)
        {
            var locale = NormalizePrimaryLocale(fingerprint.Locale);
            languages.Add(locale);
            var language = locale.Split('-')[0];
            if (!string.Equals(locale, language, StringComparison.OrdinalIgnoreCase))
            {
                languages.Add(language);
            }
        }

        return string.Join(",", languages);
    }

    private static string ToFirefoxAcceptLanguages(BrowserForgeFingerprint fingerprint)
    {
        if (fingerprint.Languages.Length > 0)
        {
            return string.Join(",", fingerprint.Languages.Where(language => !string.IsNullOrWhiteSpace(language)).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        var locale = NormalizePrimaryLocale(fingerprint.Locale);
        var language = locale.Split('-')[0];
        return string.Equals(locale, language, StringComparison.OrdinalIgnoreCase)
            ? locale
            : $"{locale},{language},en";
    }

    private static string ToChromiumAcceptLanguageHeader(BrowserForgeFingerprint fingerprint)
    {
        return string.IsNullOrWhiteSpace(fingerprint.AcceptLanguage)
            ? ToChromiumAcceptLanguage(fingerprint)
            : fingerprint.AcceptLanguage.Trim();
    }

    private void MarkClosed(string stableId)
    {
        TrackedBrowser? tracked = null;
        lock (_gate)
        {
            if (_running.TryGetValue(stableId, out tracked))
            {
                _running.Remove(stableId);
            }
        }

        if (tracked is not null)
        {
            tracked.DevToolsSession?.Dispose();
            tracked.Relay.Dispose();
            ProfilesClosed?.Invoke([stableId]);
        }
    }

    private sealed record TrackedBrowser(Process Process, string Provider, LocalProxyRelay Relay, IDisposable? DevToolsSession, int? DebuggingPort);

    private sealed record BrowserVersion(int? Major, string? FullVersion);

}
