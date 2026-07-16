using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class BrowserForgeFingerprintService
{
    private readonly string _fingerprintRoot;
    private readonly ProxyGeoService _geoService;

    public BrowserForgeFingerprintService(string appDataPath)
    {
        _fingerprintRoot = Path.Combine(appDataPath, "BrowserForge");
        Directory.CreateDirectory(_fingerprintRoot);
        _geoService = new ProxyGeoService(appDataPath);
    }

    public async Task<BrowserForgeFingerprint> GetOrCreateAsync(
        ProxyEntry proxy,
        string browser,
        int? browserMajorVersion,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedBrowser = NormalizeBrowser(browser);
        var geo = await _geoService.GetAsync(proxy, progress, cancellationToken);
        var path = GetFingerprintPath(proxy, normalizedBrowser, browserMajorVersion, geo);
        if (File.Exists(path))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<BrowserForgeFingerprint>(await File.ReadAllTextAsync(path, cancellationToken));
                if (cached is not null && !string.IsNullOrWhiteSpace(cached.UserAgent))
                {
                    CompleteFingerprint(cached, proxy, normalizedBrowser, browserMajorVersion, geo, string.IsNullOrWhiteSpace(cached.Source) ? "BrowserForge" : cached.Source);
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(cached, AppJsonOptions.Indented), cancellationToken);
                    progress?.Invoke($"BrowserForge fingerprint loaded ({cached.Source}).");
                    return cached;
                }
            }
            catch
            {
                // Broken cache entries are regenerated below.
            }
        }

        try
        {
            var generated = await GenerateWithPythonAsync(normalizedBrowser, browserMajorVersion, geo, cancellationToken);
            CompleteFingerprint(generated, proxy, normalizedBrowser, browserMajorVersion, geo, "BrowserForge");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(generated, AppJsonOptions.Indented), cancellationToken);
            progress?.Invoke($"BrowserForge fingerprint generated for {geo.CountryCode} ({geo.Locale}, {geo.TimeZone}).");
            return generated;
        }
        catch (Exception ex)
        {
            progress?.Invoke($"BrowserForge unavailable, using local fallback: {ex.Message}");
            var fallback = CreateFallback(normalizedBrowser, browserMajorVersion, geo);
            CompleteFingerprint(fallback, proxy, normalizedBrowser, browserMajorVersion, geo, "Fallback");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fallback, AppJsonOptions.Indented), cancellationToken);
            return fallback;
        }
    }

    private string GetFingerprintPath(ProxyEntry proxy, string browser, int? majorVersion, ProxyGeoInfo geo)
    {
        var version = majorVersion.HasValue ? majorVersion.Value.ToString() : "any";
        return Path.Combine(_fingerprintRoot, $"{proxy.ProfileKey}-{browser}-{version}-{geo.CountryCode}-{geo.Locale}.json");
    }

    private static void CompleteFingerprint(BrowserForgeFingerprint fingerprint, ProxyEntry proxy, string browser, int? browserMajorVersion, ProxyGeoInfo geo, string source)
    {
        fingerprint.Browser = browser;
        fingerprint.CountryCode = geo.CountryCode;
        fingerprint.Locale = geo.Locale;
        fingerprint.TimeZone = geo.TimeZone;
        fingerprint.Latitude = geo.Latitude;
        fingerprint.Longitude = geo.Longitude;
        fingerprint.City = geo.City;
        fingerprint.Region = geo.Region;
        fingerprint.PostalCode = geo.PostalCode;
        fingerprint.Source = source;
        fingerprint.NoiseSeed = fingerprint.NoiseSeed != 0 ? fingerprint.NoiseSeed : CreateStableSeed($"{proxy.StableId}:{browser}:{geo.CountryCode}:{geo.Locale}");
        fingerprint.Languages = NormalizeLanguages(fingerprint.Languages, geo.Locale);
        fingerprint.AcceptLanguage = BuildAcceptLanguage(fingerprint.Languages, geo.Locale);
        if (string.IsNullOrWhiteSpace(fingerprint.WebGLVendor))
        {
            fingerprint.WebGLVendor = browser == "firefox" ? "Mozilla" : "Google Inc. (Intel)";
        }
        if (string.IsNullOrWhiteSpace(fingerprint.WebGLRenderer))
        {
            fingerprint.WebGLRenderer = browser == "firefox"
                ? "Mozilla"
                : "ANGLE (Intel, Intel(R) UHD Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)";
        }
        if (browserMajorVersion is > 0)
        {
            fingerprint.UserAgent = AlignUserAgentMajor(fingerprint.UserAgent, browser, browserMajorVersion.Value);
            if (fingerprint.Headers.ContainsKey("User-Agent"))
            {
                fingerprint.Headers["User-Agent"] = fingerprint.UserAgent;
            }
            else if (fingerprint.Headers.ContainsKey("user-agent"))
            {
                fingerprint.Headers["user-agent"] = fingerprint.UserAgent;
            }
        }

        fingerprint.Headers["Accept-Language"] = fingerprint.AcceptLanguage;
    }

    private static string[] NormalizeLanguages(IEnumerable<string>? languages, string locale)
    {
        var normalizedLocale = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale.Trim();
        var primary = normalizedLocale.Split('-')[0];
        var result = (languages ?? [])
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Select(language => language.Trim())
            .Append(normalizedLocale)
            .Append(primary)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return result.Length > 0 ? result : [normalizedLocale, primary];
    }

    private static string BuildAcceptLanguage(IEnumerable<string>? languages, string locale)
    {
        var normalized = NormalizeLanguages(languages, locale);
        return string.Join(",", normalized.Select((language, index) =>
            index == 0
                ? language
                : $"{language};q={Math.Max(0.1, 1.0 - (index * 0.1)).ToString("0.0", CultureInfo.InvariantCulture)}"));
    }

    private static string AlignUserAgentMajor(string userAgent, string browser, int major)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return userAgent;
        }

        return browser switch
        {
            "firefox" => Regex.Replace(
                Regex.Replace(userAgent, @"rv:\d+(?=\.)", $"rv:{major}", RegexOptions.IgnoreCase),
                @"Firefox/\d+(?=\.)",
                $"Firefox/{major}",
                RegexOptions.IgnoreCase),
            "edge" => Regex.Replace(
                Regex.Replace(userAgent, @"Chrome/\d+(?=\.)", $"Chrome/{major}", RegexOptions.IgnoreCase),
                @"Edg/\d+(?=\.)",
                $"Edg/{major}",
                RegexOptions.IgnoreCase),
            _ => Regex.Replace(userAgent, @"Chrome/\d+(?=\.)", $"Chrome/{major}", RegexOptions.IgnoreCase)
        };
    }

    private static int CreateStableSeed(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return (int)(BitConverter.ToUInt32(bytes, 0) & 0x7FFFFFFF);
    }

    private static async Task<BrowserForgeFingerprint> GenerateWithPythonAsync(string browser, int? browserMajorVersion, ProxyGeoInfo geo, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var command in new[]
                 {
                     new PythonCommand("python", []),
                     new PythonCommand("py", ["-3"])
                 })
        {
            try
            {
                return await GenerateWithPythonCommandAsync(command, browser, browserMajorVersion, geo, cancellationToken);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(lastError?.Message ?? "Python is not available.");
    }

    private static async Task<BrowserForgeFingerprint> GenerateWithPythonCommandAsync(
        PythonCommand command,
        string browser,
        int? browserMajorVersion,
        ProxyGeoInfo geo,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var prefixArgument in command.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefixArgument);
        }
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(PythonGeneratorScript);
        startInfo.ArgumentList.Add(browser);
        startInfo.ArgumentList.Add(browserMajorVersion?.ToString() ?? "");
        startInfo.ArgumentList.Add(geo.Locale);
        startInfo.ArgumentList.Add(geo.TimeZone);
        startInfo.ArgumentList.Add(geo.CountryCode);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Python.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"Python exited with code {process.ExitCode}." : stderr.Trim());
        }

        var fingerprint = JsonSerializer.Deserialize<BrowserForgeFingerprint>(stdout, AppJsonOptions.CaseInsensitive);
        if (fingerprint is null || string.IsNullOrWhiteSpace(fingerprint.UserAgent))
        {
            throw new InvalidOperationException("BrowserForge returned an empty fingerprint.");
        }

        return fingerprint;
    }

    private static BrowserForgeFingerprint CreateFallback(string browser, int? majorVersion, ProxyGeoInfo geo)
    {
        var major = majorVersion ?? (browser == "firefox" ? 140 : 126);
        var userAgent = browser switch
        {
            "firefox" => $"Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{major}.0) Gecko/20100101 Firefox/{major}.0",
            "edge" => $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0",
            _ => $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36"
        };

        return new BrowserForgeFingerprint
        {
            Browser = browser,
            CountryCode = geo.CountryCode,
            Locale = geo.Locale,
            TimeZone = geo.TimeZone,
            Latitude = geo.Latitude,
            Longitude = geo.Longitude,
            City = geo.City,
            Region = geo.Region,
            PostalCode = geo.PostalCode,
            Source = "Fallback",
            UserAgent = userAgent,
            AcceptLanguage = BuildAcceptLanguage(null, geo.Locale),
            Platform = "Win32",
            Vendor = browser == "firefox" ? "" : "Google Inc.",
            WebGLVendor = browser == "firefox" ? "Mozilla" : "Google Inc. (Intel)",
            WebGLRenderer = browser == "firefox" ? "Mozilla" : "ANGLE (Intel, Intel(R) UHD Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)",
            Languages = NormalizeLanguages(null, geo.Locale),
            HardwareConcurrency = 8,
            DeviceMemory = browser == "firefox" ? null : 8,
            MaxTouchPoints = 0,
            Screen = new BrowserForgeScreen
            {
                Width = 1920,
                Height = 1080,
                AvailWidth = 1920,
                AvailHeight = 1040,
                ColorDepth = 24,
                PixelDepth = 24,
                DevicePixelRatio = 1
            },
            Headers = new Dictionary<string, string>
            {
                ["User-Agent"] = userAgent,
                ["Accept-Language"] = BuildAcceptLanguage(null, geo.Locale)
            }
        };
    }

    private static string NormalizeBrowser(string browser)
    {
        return browser.Trim().ToLowerInvariant() switch
        {
            "portable-chromium" or "portable chromium" or "chromium" or "portable" => "chrome",
            "edge" => "edge",
            "firefox" => "firefox",
            _ => "chrome"
        };
    }

    private const string PythonGeneratorScript = """
import dataclasses
import json
import sys

browser = (sys.argv[1] if len(sys.argv) > 1 else "chrome").strip().lower()
major_raw = (sys.argv[2] if len(sys.argv) > 2 else "").strip()
major = int(major_raw) if major_raw.isdigit() else None
locale = (sys.argv[3] if len(sys.argv) > 3 else "en-US").strip() or "en-US"
timezone = (sys.argv[4] if len(sys.argv) > 4 else "America/New_York").strip() or "America/New_York"
country = (sys.argv[5] if len(sys.argv) > 5 else "US").strip().upper() or "US"

from browserforge.headers import Browser, HeaderGenerator
from browserforge.fingerprints import FingerprintGenerator, Screen

def convert(value):
    if dataclasses.is_dataclass(value):
        return dataclasses.asdict(value)
    if isinstance(value, dict):
        return {str(k): convert(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [convert(v) for v in value]
    if hasattr(value, "__dict__"):
        return {str(k): convert(v) for k, v in value.__dict__.items()}
    return value

bf_browser = Browser(name=browser, min_version=major, max_version=major, http_version=2) if major else browser
screen = Screen(max_width=1920, max_height=1080)
fp = convert(FingerprintGenerator(browser=bf_browser, os="windows", device="desktop", locale=locale, screen=screen).generate())
headers = fp.get("headers") or HeaderGenerator(browser=bf_browser, os="windows", device="desktop", locale=locale, http_version=2).generate()
navigator = fp.get("navigator") or {}
screen_data = fp.get("screen") or {}
video_card = fp.get("videoCard") or {}

payload = {
    "browser": browser,
    "source": "BrowserForge",
    "countryCode": country,
    "locale": locale,
    "timeZone": timezone,
    "userAgent": navigator.get("userAgent") or headers.get("User-Agent") or headers.get("user-agent") or "",
    "acceptLanguage": headers.get("Accept-Language") or headers.get("accept-language") or navigator.get("language") or "en-US,en;q=0.9",
    "platform": navigator.get("platform") or "Win32",
    "vendor": navigator.get("vendor") or ("" if browser == "firefox" else "Google Inc."),
    "webGLVendor": video_card.get("vendor") or ("" if browser == "firefox" else "Google Inc. (Intel)"),
    "webGLRenderer": video_card.get("renderer") or ("" if browser == "firefox" else "ANGLE (Intel, Intel(R) UHD Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)"),
    "languages": navigator.get("languages") or [navigator.get("language") or locale],
    "hardwareConcurrency": navigator.get("hardwareConcurrency"),
    "deviceMemory": navigator.get("deviceMemory"),
    "maxTouchPoints": navigator.get("maxTouchPoints"),
    "screen": {
        "width": screen_data.get("width"),
        "height": screen_data.get("height"),
        "availWidth": screen_data.get("availWidth"),
        "availHeight": screen_data.get("availHeight"),
        "availLeft": screen_data.get("availLeft"),
        "availTop": screen_data.get("availTop"),
        "colorDepth": screen_data.get("colorDepth"),
        "pixelDepth": screen_data.get("pixelDepth"),
        "devicePixelRatio": screen_data.get("devicePixelRatio"),
        "hasHDR": screen_data.get("hasHDR")
    },
    "headers": headers
}

print(json.dumps(payload, ensure_ascii=False, separators=(",", ":")))
""";

    private sealed record PythonCommand(string Executable, string[] PrefixArguments);
}

public sealed class BrowserForgeFingerprint
{
    public string Browser { get; set; } = "";
    public string Source { get; set; } = "";
    public string CountryCode { get; set; } = "US";
    public string Locale { get; set; } = "en-US";
    public string TimeZone { get; set; } = "America/New_York";
    public double Latitude { get; set; } = 34.0522;
    public double Longitude { get; set; } = -118.2437;
    public string City { get; set; } = "Los Angeles";
    public string Region { get; set; } = "California";
    public string PostalCode { get; set; } = "90013";
    public string UserAgent { get; set; } = "";
    public string AcceptLanguage { get; set; } = "en-US,en;q=0.9";
    public string Platform { get; set; } = "Win32";
    public string Vendor { get; set; } = "Google Inc.";
    public string WebGLVendor { get; set; } = "Google Inc. (Intel)";
    public string WebGLRenderer { get; set; } = "ANGLE (Intel, Intel(R) UHD Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)";
    public string[] Languages { get; set; } = ["en-US", "en"];
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
