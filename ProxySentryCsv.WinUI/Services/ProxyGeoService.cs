using System.Net;
using System.Text.Json;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class ProxyGeoService
{
    private static readonly TimeSpan GeoCacheLifetime = TimeSpan.FromHours(24);
    private readonly string _geoRoot;
    private readonly HttpClient _directClient = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(4) };

    public ProxyGeoService(string appDataPath)
    {
        _geoRoot = Path.Combine(appDataPath, "ProxyGeo");
        Directory.CreateDirectory(_geoRoot);
    }

    public async Task<ProxyGeoInfo> GetAsync(ProxyEntry proxy, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_geoRoot, $"{proxy.ProfileKey}-{Sanitize(proxy.Host)}-{proxy.Port}.json");
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < GeoCacheLifetime)
        {
            try
            {
                var cached = JsonSerializer.Deserialize<ProxyGeoInfo>(await File.ReadAllTextAsync(path, cancellationToken));
                if (cached is not null
                    && !string.IsNullOrWhiteSpace(cached.CountryCode)
                    && cached.Source == "proxy-exit")
                {
                    progress?.Invoke($"Proxy geo loaded: {cached.CountryCode}, {cached.Locale}, {cached.TimeZone}.");
                    return cached;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        try
        {
            var geo = await DetectViaProxyAsync(proxy, cancellationToken);
            geo.Source = "proxy-exit";
            geo.CheckedAtUtc = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(geo, AppJsonOptions.Indented), cancellationToken);
            progress?.Invoke($"Proxy geo detected through proxy: {geo.CountryCode}, {geo.Locale}, {geo.TimeZone}.");
            return geo;
        }
        catch (Exception proxyEx) when (proxyEx is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            progress?.Invoke($"Proxy geo through proxy failed: {proxyEx.Message}");
        }

        try
        {
            var geo = await DetectHostGeoAsync(proxy, cancellationToken);
            geo.Source = "proxy-host";
            geo.CheckedAtUtc = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(geo, AppJsonOptions.Indented), cancellationToken);
            progress?.Invoke($"Proxy host geo detected: {geo.CountryCode}, {geo.Locale}, {geo.TimeZone}.");
            return geo;
        }
        catch (Exception hostEx) when (hostEx is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            progress?.Invoke($"Proxy geo fallback: {hostEx.Message}");
            return new ProxyGeoInfo();
        }
    }

    private static async Task<ProxyGeoInfo> DetectViaProxyAsync(ProxyEntry proxy, CancellationToken cancellationToken)
    {
        var proxyUri = new Uri($"{proxy.Scheme.ToLowerInvariant()}://{proxy.Host}:{proxy.Port}");
        var webProxy = new WebProxy(proxyUri);
        if (!string.IsNullOrWhiteSpace(proxy.Username))
        {
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
        }

        using var handler = new HttpClientHandler
        {
            Proxy = webProxy,
            UseProxy = true,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var payload = await FetchIpApiAsync(client, "http://ip-api.com/json/?fields=status,countryCode,timezone,query,lat,lon,city,regionName,zip", cancellationToken);
        return FromCountry(payload);
    }

    private async Task<ProxyGeoInfo> DetectHostGeoAsync(ProxyEntry proxy, CancellationToken cancellationToken)
    {
        var payload = await FetchIpApiAsync(
            _directClient,
            $"http://ip-api.com/json/{Uri.EscapeDataString(proxy.Host)}?fields=status,countryCode,timezone,query,lat,lon,city,regionName,zip",
            cancellationToken);
        return FromCountry(payload);
    }

    private static async Task<IpApiResponse> FetchIpApiAsync(HttpClient client, string uri, CancellationToken cancellationToken)
    {
        var response = await client.GetStringAsync(uri, cancellationToken);
        var payload = JsonSerializer.Deserialize<IpApiResponse>(response, AppJsonOptions.CaseInsensitive);
        if (payload is null || !string.Equals(payload.Status, "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("geo lookup failed");
        }

        return payload;
    }

    private static string Sanitize(string value)
    {
        var chars = value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray();
        return new string(chars);
    }

    private static ProxyGeoInfo FromCountry(IpApiResponse payload)
    {
        return FromCountry(
            payload.CountryCode,
            payload.Timezone,
            payload.Query,
            payload.Lat,
            payload.Lon,
            payload.City,
            payload.RegionName,
            payload.Zip);
    }

    private static ProxyGeoInfo FromCountry(
        string? countryCode,
        string? timeZone,
        string? query,
        double? latitude = null,
        double? longitude = null,
        string? city = null,
        string? region = null,
        string? postalCode = null)
    {
        var country = (countryCode ?? "").Trim().ToUpperInvariant();
        if (country.Length != 2 || country.Any(ch => ch is < 'A' or > 'Z')) country = "";
        var locale = country.Length == 0 ? "" : CountryLocale(country);
        var zone = timeZone?.Trim() ?? "";
        return new ProxyGeoInfo
        {
            Query = query ?? "",
            CountryCode = country,
            Locale = locale,
            TimeZone = zone,
            Latitude = latitude is >= -90 and <= 90 ? latitude.Value : 0,
            Longitude = longitude is >= -180 and <= 180 ? longitude.Value : 0,
            CoordinatesKnown = latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180,
            City = city?.Trim() ?? "",
            Region = region?.Trim() ?? "",
            PostalCode = postalCode?.Trim() ?? ""
        };
    }


    private static string CountryLocale(string countryCode)
    {
        return countryCode switch
        {
            "US" => "en-US",
            "CA" => "en-CA",
            "GB" => "en-GB",
            "IE" => "en-IE",
            "AU" => "en-AU",
            "NZ" => "en-NZ",
            "DE" => "de-DE",
            "AT" => "de-AT",
            "CH" => "de-CH",
            "FR" => "fr-FR",
            "BE" => "fr-BE",
            "NL" => "nl-NL",
            "ES" => "es-ES",
            "IT" => "it-IT",
            "PL" => "pl-PL",
            "CZ" => "cs-CZ",
            "SK" => "sk-SK",
            "UA" => "uk-UA",
            "RO" => "ro-RO",
            "PT" => "pt-PT",
            "BR" => "pt-BR",
            "MX" => "es-MX",
            "AR" => "es-AR",
            "CL" => "es-CL",
            "JP" => "ja-JP",
            "KR" => "ko-KR",
            "CN" => "zh-CN",
            "TW" => "zh-TW",
            "HK" => "zh-HK",
            "SG" => "en-SG",
            "IN" => "en-IN",
            "TR" => "tr-TR",
            "IL" => "he-IL",
            "AE" => "ar-AE",
            "SA" => "ar-SA",
            "ZA" => "en-ZA",
            _ => ""
        };
    }


    private sealed class IpApiResponse
    {
        public string Status { get; set; } = "";
        public string CountryCode { get; set; } = "";
        public string Timezone { get; set; } = "";
        public string Query { get; set; } = "";
        public double? Lat { get; set; }
        public double? Lon { get; set; }
        public string City { get; set; } = "";
        public string RegionName { get; set; } = "";
        public string Zip { get; set; } = "";
    }
}

public sealed class ProxyGeoInfo
{
    public string Source { get; set; } = "unknown";
    public DateTimeOffset? CheckedAtUtc { get; set; }
    public bool CoordinatesKnown { get; set; }
    public string Query { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string Locale { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public bool HasCoordinates => CoordinatesKnown && Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}
