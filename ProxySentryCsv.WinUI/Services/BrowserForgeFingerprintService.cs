using System.Globalization;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

// The legacy cache format is retained so existing profile data is not reset.
public sealed class BrowserForgeFingerprintService
{
    private readonly string _fingerprintRoot;
    private readonly ProxyGeoService _geoService;
    public const string ConfigurationSource = "Built-in native v1";

    public BrowserForgeFingerprintService(string appDataPath)
    {
        _fingerprintRoot = Path.Combine(appDataPath, "BrowserForge");
        Directory.CreateDirectory(_fingerprintRoot);
        _geoService = new ProxyGeoService(appDataPath);
    }

    public async Task<BrowserForgeFingerprint> GetOrCreateAsync(
        ProxyEntry proxy, string browser, int? browserMajorVersion,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var geo = await _geoService.GetAsync(proxy, progress, cancellationToken);
        return LoadConfiguration(proxy, browser, browserMajorVersion, geo, progress, cancellationToken);
    }

    public BrowserForgeFingerprint? ReadConfiguration(ProxyEntry proxy, string browser) =>
        AtomicJsonFile.Read<BrowserForgeFingerprint>(Path.Combine(_fingerprintRoot, $"{proxy.ProfileKey}-{NormalizeBrowser(browser)}-native-v1.json"));

    internal BrowserForgeFingerprint LoadConfiguration(ProxyEntry proxy, string browser, int? major,
        ProxyGeoInfo geo, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        browser = NormalizeBrowser(browser);
        var path = Path.Combine(_fingerprintRoot, $"{proxy.ProfileKey}-{browser}-native-v1.json");
        var cached = AtomicJsonFile.Read<BrowserForgeFingerprint>(path);
        if (cached is null && (File.Exists(path) || File.Exists(path + ".bak")))
            throw new InvalidDataException("Browser configuration is damaged. Restore its backup; the profile has not been reset.");
        if (cached is null)
        {
            var legacyPath = Path.Combine(_fingerprintRoot, $"{proxy.ProfileKey}-{browser}-{major?.ToString(CultureInfo.InvariantCulture) ?? "any"}-{geo.CountryCode}-{geo.Locale}.json");
            if (!File.Exists(legacyPath) && !File.Exists(legacyPath + ".bak"))
            {
                legacyPath = Directory.EnumerateFiles(_fingerprintRoot, $"{proxy.ProfileKey}-{browser}-*.json")
                    .Where(candidate => candidate != path)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? legacyPath;
            }
            cached = AtomicJsonFile.Read<BrowserForgeFingerprint>(legacyPath);
            if (cached is null && File.Exists(legacyPath))
                throw new InvalidDataException("Legacy browser configuration is damaged. The profile has not been reset.");
            if (cached is not null) progress?.Invoke("Existing browser configuration migrated; cookies and profile data preserved.");
        }
        var config = CompleteConfiguration(cached ?? new BrowserForgeFingerprint(), browser, geo);
        cancellationToken.ThrowIfCancellationRequested();
        AtomicJsonFile.Write(path, config);
        progress?.Invoke($"{ConfigurationSource}: no Python required; User-Agent supplied by the browser.");
        progress?.Invoke($"Region: {config.GeoSource}. {config.GeoWarning}");
        return config;
    }

    internal static BrowserForgeFingerprint CompleteConfiguration(BrowserForgeFingerprint config, string browser, ProxyGeoInfo geo)
    {
        config.Browser = browser;
        config.Source = ConfigurationSource;
        config.UserAgent = "";
        config.Headers ??= [];
        foreach (var key in config.Headers.Keys.Where(k => k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).ToArray())
            config.Headers.Remove(key);
        config.GeoSource = geo.Source;
        config.GeoCheckedAtUtc = geo.CheckedAtUtc;
        config.GeoWarning = geo.Source switch
        {
            "proxy-exit" => "IP location is approximate; language is a country-based suggestion.",
            "proxy-host" => "Gateway location only; exit location is not verified. Regional settings were not replaced.",
            _ => "Exit location is unknown. Regional settings were not replaced."
        };
        if (geo.Source == "proxy-exit")
        {
            if (!string.IsNullOrWhiteSpace(geo.TimeZone)) TimeZoneInfo.FindSystemTimeZoneById(geo.TimeZone);
            if (!string.IsNullOrWhiteSpace(geo.Locale)) _ = CultureInfo.GetCultureInfo(geo.Locale);
            config.CountryCode = geo.CountryCode;
            config.Locale = geo.Locale;
            config.TimeZone = geo.TimeZone;
            config.Latitude = geo.Latitude;
            config.Longitude = geo.Longitude;
            config.City = geo.City;
            config.Region = geo.Region;
            config.PostalCode = geo.PostalCode;
        }
        config.Languages = string.IsNullOrWhiteSpace(config.Locale) ? []
            : new[] { config.Locale, config.Locale.Split('-')[0] }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        config.AcceptLanguage = string.Join(",", config.Languages.Select((language, index) => index == 0 ? language : $"{language};q=0.9"));
        foreach (var key in config.Headers.Keys.Where(k => k.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase)).ToArray())
            config.Headers.Remove(key);
        if (config.AcceptLanguage.Length > 0) config.Headers["Accept-Language"] = config.AcceptLanguage;
        return config;
    }

    private static string NormalizeBrowser(string browser) => browser.Trim().ToLowerInvariant() switch
    {
        "edge" => "edge", "firefox" => "firefox", _ => "chrome"
    };
}

public sealed class BrowserForgeFingerprint
{
    public string Browser { get; set; } = "";
    public string Source { get; set; } = "";
    public string GeoSource { get; set; } = "unknown";
    public DateTimeOffset? GeoCheckedAtUtc { get; set; }
    public string GeoWarning { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string Locale { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string AcceptLanguage { get; set; } = "";
    public string Platform { get; set; } = "Win32";
    public string Vendor { get; set; } = "Google Inc.";
    public string WebGLVendor { get; set; } = "";
    public string WebGLRenderer { get; set; } = "";
    public string[] Languages { get; set; } = [];
    public int NoiseSeed { get; set; }
    public int? HardwareConcurrency { get; set; }
    public int? DeviceMemory { get; set; }
    public int? MaxTouchPoints { get; set; }
    public BrowserForgeScreen Screen { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = [];
}

public sealed class BrowserForgeScreen
{
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? AvailWidth { get; set; }
    public int? AvailHeight { get; set; }
    public int? AvailLeft { get; set; }
    public int? AvailTop { get; set; }
    public int? ColorDepth { get; set; }
    public int? PixelDepth { get; set; }
    public double? DevicePixelRatio { get; set; }
    public bool? HasHDR { get; set; }
}
