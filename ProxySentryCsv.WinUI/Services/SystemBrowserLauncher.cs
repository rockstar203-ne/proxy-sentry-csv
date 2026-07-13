using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class SystemBrowserLauncher : IDisposable
{
    private readonly string _profilesRoot;
    private readonly BrowserForgeFingerprintService _fingerprints;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackedBrowser> _running = new(StringComparer.OrdinalIgnoreCase);

    public SystemBrowserLauncher(string appDataPath, BrowserForgeFingerprintService fingerprints)
    {
        _profilesRoot = Path.Combine(appDataPath, "SystemBrowserProfiles");
        _fingerprints = fingerprints;
    }

    public event Action<IReadOnlyList<string>>? ProfilesClosed;

    public static bool IsSystemBrowser(string? provider)
    {
        return NormalizeProvider(provider) is "chrome" or "edge" or "firefox";
    }

    public async Task LaunchAsync(ProxyEntry proxy, string targetUrl, string provider, Action<string>? progress = null)
    {
        var normalizedProvider = NormalizeProvider(provider);
        if (!IsSystemBrowser(normalizedProvider))
        {
            throw new InvalidOperationException("Select Chrome, Edge, or Firefox to use an installed Windows browser.");
        }

        Close(proxy);
        var executablePath = ResolveBrowserExecutable(normalizedProvider);
        var browserMajor = GetBrowserMajorVersion(executablePath);
        var fingerprint = await _fingerprints.GetOrCreateAsync(proxy, normalizedProvider, browserMajor, progress);
        var profilePath = GetProfilePath(normalizedProvider, proxy);
        Directory.CreateDirectory(profilePath);
        progress?.Invoke("Starting proxy relay");
        var relay = LocalProxyRelay.Start(proxy);

        try
        {
            progress?.Invoke($"Launching {GetBrowserDisplayName(normalizedProvider)}");
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

            if (normalizedProvider is "chrome" or "edge")
            {
                var extensionPath = WriteChromiumBrowserForgeExtension(profilePath, fingerprint);
                AddChromiumArguments(startInfo, profilePath, targetUrl, relay.Port, fingerprint, extensionPath, normalizedProvider);
            }
            else
            {
                WriteFirefoxProfile(profilePath, relay.Port, fingerprint);
                startInfo.ArgumentList.Add("-no-remote");
                startInfo.ArgumentList.Add("-profile");
                startInfo.ArgumentList.Add(profilePath);
                startInfo.ArgumentList.Add("-new-window");
                startInfo.ArgumentList.Add(targetUrl);
            }

            var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {GetBrowserDisplayName(normalizedProvider)}.");
            process.EnableRaisingEvents = true;
            if (normalizedProvider != "firefox")
            {
                process.Exited += (_, _) => MarkClosed(proxy.StableId);
            }
            lock (_gate)
            {
                _running[proxy.StableId] = new TrackedBrowser(process, normalizedProvider, relay);
            }
        }
        catch
        {
            relay.Dispose();
            throw;
        }

    }

    public void Close(ProxyEntry proxy)
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

        try
        {
            if (tracked.Process.HasExited)
            {
                return;
            }

            if (tracked.Process.CloseMainWindow())
            {
                if (tracked.Process.WaitForExit(3000))
                {
                    return;
                }
            }

            if (!tracked.Process.HasExited)
            {
                tracked.Process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Browser processes can exit while the user is closing windows manually.
        }
        finally
        {
            tracked.Relay.Dispose();
            ProfilesClosed?.Invoke([proxy.StableId]);
        }
    }

    public void Dispose()
    {
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
                tracked.Relay.Dispose();
            }
        }
    }

    private static void AddChromiumArguments(
        ProcessStartInfo startInfo,
        string profilePath,
        string targetUrl,
        int relayPort,
        BrowserForgeFingerprint fingerprint,
        string extensionPath,
        string provider)
    {
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add($"--user-data-dir={profilePath}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-session-crashed-bubble");
        startInfo.ArgumentList.Add("--disable-quic");
        startInfo.ArgumentList.Add("--disable-background-networking");
        startInfo.ArgumentList.Add("--disable-preconnect");
        startInfo.ArgumentList.Add("--dns-prefetch-disable");
        startInfo.ArgumentList.Add("--disable-features=UseDnsHttpsSvcb,UseDnsHttpsSvcbAlpn,DnsOverHttps,msEdgeStartupBoost,StartupBoost,EdgeStartupBoost");
        startInfo.ArgumentList.Add($"--user-agent={fingerprint.UserAgent}");
        startInfo.ArgumentList.Add($"--lang={NormalizePrimaryLocale(fingerprint.Locale)}");
        startInfo.ArgumentList.Add($"--proxy-server=http://127.0.0.1:{relayPort}");
        startInfo.ArgumentList.Add("--proxy-bypass-list=<-loopback>");
        if (provider == "edge")
        {
            startInfo.ArgumentList.Add("--edge-skip-compat-layer-relaunch");
        }
        if (fingerprint.Screen.Width is > 0 && fingerprint.Screen.Height is > 0)
        {
            startInfo.ArgumentList.Add($"--window-size={fingerprint.Screen.Width.Value},{fingerprint.Screen.Height.Value}");
        }
        if (Directory.Exists(extensionPath))
        {
            startInfo.ArgumentList.Add($"--disable-extensions-except={extensionPath}");
            startInfo.ArgumentList.Add($"--load-extension={extensionPath}");
        }
        startInfo.ArgumentList.Add(targetUrl);
    }

    private static void WriteFirefoxProfile(string profilePath, int relayPort, BrowserForgeFingerprint fingerprint)
    {
        var prefs = new StringBuilder();
        prefs.AppendLine("user_pref(\"network.proxy.type\", 1);");
        prefs.AppendLine("user_pref(\"network.proxy.no_proxies_on\", \"\");");
        prefs.AppendLine("user_pref(\"network.proxy.share_proxy_settings\", false);");
        prefs.AppendLine("user_pref(\"signon.autologin.proxy\", true);");
        prefs.AppendLine($"user_pref(\"network.proxy.http\", {Json("127.0.0.1")});");
        prefs.AppendLine($"user_pref(\"network.proxy.http_port\", {relayPort});");
        prefs.AppendLine($"user_pref(\"network.proxy.ssl\", {Json("127.0.0.1")});");
        prefs.AppendLine($"user_pref(\"network.proxy.ssl_port\", {relayPort});");
        prefs.AppendLine($"user_pref(\"general.useragent.override\", {Json(fingerprint.UserAgent)});");
        prefs.AppendLine($"user_pref(\"intl.accept_languages\", {Json(ToFirefoxAcceptLanguages(fingerprint))});");
        prefs.AppendLine($"user_pref(\"javascript.use_us_english_locale\", false);");
        prefs.AppendLine($"user_pref(\"privacy.resistFingerprinting\", false);");
        prefs.AppendLine($"user_pref(\"dom.webdriver.enabled\", false);");

        var prefsText = prefs.ToString();
        File.WriteAllText(Path.Combine(profilePath, "user.js"), prefsText, Encoding.UTF8);
        File.WriteAllText(Path.Combine(profilePath, "prefs.js"), prefsText, Encoding.UTF8);
    }

    private static string WriteChromiumBrowserForgeExtension(string profilePath, BrowserForgeFingerprint fingerprint)
    {
        var extensionPath = Path.Combine(profilePath, "ProxySentryBrowserForgeExtension");
        Directory.CreateDirectory(extensionPath);
        File.WriteAllText(Path.Combine(extensionPath, "manifest.json"), """
{
  "manifest_version": 3,
  "name": "Proxy Sentry BrowserForge",
  "version": "1.0.0",
  "description": "Applies Proxy Sentry CSV BrowserForge profile values.",
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
  const source = `(${function applyBrowserForgeFingerprint(fp) {
    const defineGetter = (target, name, value) => {
      try {
        Object.defineProperty(target, name, { get: () => value, configurable: true });
      } catch (_) {}
    };

    const nav = Navigator.prototype;
    defineGetter(nav, "userAgent", fp.userAgent || fp.UserAgent || "");
    defineGetter(nav, "platform", fp.platform || fp.Platform || "Win32");
    defineGetter(nav, "vendor", fp.vendor || fp.Vendor || "");
    defineGetter(nav, "language", fp.locale || fp.Locale || "en-US");
    defineGetter(nav, "languages", fp.languages || fp.Languages || ["en-US", "en"]);
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

    const timeZone = fp.timeZone || fp.TimeZone;
    if (timeZone && Intl && Intl.DateTimeFormat) {
      const original = Intl.DateTimeFormat.prototype.resolvedOptions;
      Intl.DateTimeFormat.prototype.resolvedOptions = function() {
        const options = original.call(this);
        options.timeZone = timeZone;
        return options;
      };
    }
  }.toString()})(${JSON.stringify(fp)});`;

  const element = document.documentElement || document.head || document.body;
  const script = document.createElement("script");
  script.textContent = source;
  if (element) {
    element.appendChild(script);
    script.remove();
  }
})();
""";
        File.WriteAllText(Path.Combine(extensionPath, "inject.js"), script, Encoding.UTF8);
        return extensionPath;
    }

    private string GetProfilePath(string provider, ProxyEntry proxy)
    {
        return Path.Combine(_profilesRoot, provider, $"proxy-{proxy.ProfileKey}");
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

    private static int? GetBrowserMajorVersion(string executablePath)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executablePath);
            var raw = !string.IsNullOrWhiteSpace(version.ProductVersion)
                ? version.ProductVersion
                : version.FileVersion;
            var first = raw?.Split(['.', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return int.TryParse(first, out var major) && major > 0 ? major : null;
        }
        catch
        {
            return null;
        }
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
            tracked.Relay.Dispose();
            ProfilesClosed?.Invoke([stableId]);
        }
    }

    private sealed record TrackedBrowser(Process Process, string Provider, LocalProxyRelay Relay);
}
