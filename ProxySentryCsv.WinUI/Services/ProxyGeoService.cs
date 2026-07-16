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
                    && cached.HasCoordinates)
                {
                    progress?.Invoke($"Proxy geo loaded: {cached.CountryCode}, {cached.Locale}, {cached.TimeZone}.");
                    return cached;
                }
            }
            catch
            {
            }
        }

        try
        {
            var geo = await DetectViaProxyAsync(proxy, cancellationToken);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(geo, AppJsonOptions.Indented), cancellationToken);
            progress?.Invoke($"Proxy geo detected through proxy: {geo.CountryCode}, {geo.Locale}, {geo.TimeZone}.");
            return geo;
        }
        catch (Exception proxyEx)
        {
            progress?.Invoke($"Proxy geo through proxy failed: {proxyEx.Message}");
        }

        try
        {
            var geo = await DetectHostGeoAsync(proxy, cancellationToken);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(geo, AppJsonOptions.Indented), cancellationToken);
            progress?.Invoke($"Proxy host geo detected: {geo.CountryCode}, {geo.Locale}, {geo.TimeZone}.");
            return geo;
        }
        catch (Exception hostEx)
        {
            progress?.Invoke($"Proxy geo fallback: {hostEx.Message}");
            return FromCountry("", "", proxy.Host);
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
        var locale = CountryLocale(country);
        var zone = !string.IsNullOrWhiteSpace(timeZone) ? timeZone.Trim() : CountryTimeZone(country);
        var fallback = CountryFallbackLocation(country);
        return new ProxyGeoInfo
        {
            Query = query ?? "",
            CountryCode = string.IsNullOrWhiteSpace(country) ? "US" : country,
            Locale = locale,
            TimeZone = zone,
            Latitude = latitude is > -90 and < 90 ? latitude.Value : fallback.Latitude,
            Longitude = longitude is > -180 and < 180 ? longitude.Value : fallback.Longitude,
            City = !string.IsNullOrWhiteSpace(city) ? city.Trim() : fallback.City,
            Region = !string.IsNullOrWhiteSpace(region) ? region.Trim() : fallback.Region,
            PostalCode = !string.IsNullOrWhiteSpace(postalCode) ? postalCode.Trim() : fallback.PostalCode
        };
    }

    private static ProxyGeoLocation CountryFallbackLocation(string countryCode)
    {
        return countryCode switch
        {
            "US" => new ProxyGeoLocation(34.0522, -118.2437, "Los Angeles", "California", "90013"),
            "CA" => new ProxyGeoLocation(43.6532, -79.3832, "Toronto", "Ontario", "M5H"),
            "GB" or "IE" => new ProxyGeoLocation(51.5074, -0.1278, "London", "England", "SW1A"),
            _ => new ProxyGeoLocation(34.0522, -118.2437, "Los Angeles", "California", "90013")
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
            _ => "en-US"
        };
    }

    private static string CountryTimeZone(string countryCode)
    {
        return countryCode switch
        {
            "US" => "America/New_York",
            "CA" => "America/Toronto",
            "GB" or "IE" => "Europe/London",
            "AU" => "Australia/Sydney",
            "NZ" => "Pacific/Auckland",
            "DE" or "AT" or "CH" or "FR" or "BE" or "NL" or "ES" or "IT" or "PL" or "CZ" or "SK" => "Europe/Berlin",
            "UA" => "Europe/Kyiv",
            "RO" => "Europe/Bucharest",
            "PT" => "Europe/Lisbon",
            "BR" => "America/Sao_Paulo",
            "MX" => "America/Mexico_City",
            "AR" => "America/Argentina/Buenos_Aires",
            "CL" => "America/Santiago",
            "JP" => "Asia/Tokyo",
            "KR" => "Asia/Seoul",
            "CN" => "Asia/Shanghai",
            "TW" => "Asia/Taipei",
            "HK" => "Asia/Hong_Kong",
            "SG" => "Asia/Singapore",
            "IN" => "Asia/Kolkata",
            "TR" => "Europe/Istanbul",
            "IL" => "Asia/Jerusalem",
            "AE" => "Asia/Dubai",
            "SA" => "Asia/Riyadh",
            "ZA" => "Africa/Johannesburg",
            _ => "America/New_York"
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
    public string Query { get; set; } = "";
    public string CountryCode { get; set; } = "US";
    public string Locale { get; set; } = "en-US";
    public string TimeZone { get; set; } = "America/New_York";
    public double Latitude { get; set; } = 34.0522;
    public double Longitude { get; set; } = -118.2437;
    public string City { get; set; } = "Los Angeles";
    public string Region { get; set; } = "California";
    public string PostalCode { get; set; } = "90013";
    public bool HasCoordinates => Latitude is > -90 and < 90 && Longitude is > -180 and < 180;
}

public sealed record ProxyGeoLocation(double Latitude, double Longitude, string City, string Region, string PostalCode);
