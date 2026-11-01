using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public static class CsvProxyLoader
{
    public static IReadOnlyList<ProxyEntry> Load(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            return [];
        }

        var headers = ParseLine(lines[0]);
        var proxyIndex = headers.FindIndex(header => string.Equals(header.Trim().TrimStart('\uFEFF'), "Proxy", StringComparison.OrdinalIgnoreCase));
        if (proxyIndex < 0)
        {
            return LoadPlainTextProxyList(lines);
        }

        var nameIndex = FindHeader(headers, "Name");
        var emailIndex = FindAnyHeader(headers, "Email", "E-mail", "Mail", "ProfileEmail", "Profile Email");
        var browserIndex = FindHeader(headers, "Browser");
        var countryIndex = FindHeader(headers, "Country");
        var regionIndex = FindHeader(headers, "Region");
        var cityIndex = FindHeader(headers, "City");
        var proxies = new List<ProxyEntry>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var fields = ParseLine(lines[i]);
            if (proxyIndex >= fields.Count)
            {
                continue;
            }

            var rawProxy = NormalizeField(fields[proxyIndex]);
            if (string.IsNullOrWhiteSpace(rawProxy))
            {
                continue;
            }

            if (TryParseProxy(rawProxy, i + 1, out var proxy))
            {
                proxy.DisplayName = GetField(fields, nameIndex);
                proxy.Email = GetField(fields, emailIndex);
                var browserProvider = GetField(fields, browserIndex);
                if (!string.IsNullOrWhiteSpace(browserProvider))
                {
                    proxy.BrowserProvider = browserProvider;
                    proxy.BrowserProviderFromCsv = true;
                }
                proxy.CountryCode = GetField(fields, countryIndex);
                proxy.Region = GetField(fields, regionIndex);
                proxy.City = GetField(fields, cityIndex);
                proxies.Add(proxy);
            }
        }

        return proxies;
    }

    private static IReadOnlyList<ProxyEntry> LoadPlainTextProxyList(IReadOnlyList<string> lines)
    {
        var proxies = new List<ProxyEntry>();
        for (var i = 0; i < lines.Count; i++)
        {
            var rawProxy = NormalizeField(lines[i]);
            if (string.IsNullOrWhiteSpace(rawProxy) || rawProxy.StartsWith('#'))
            {
                continue;
            }

            if (TryParseProxy(rawProxy, i + 1, out var proxy))
            {
                proxies.Add(proxy);
            }
        }

        if (proxies.Count == 0)
        {
            throw new InvalidOperationException("Proxy list does not contain supported proxy rows.");
        }

        return proxies;
    }

    private static bool TryParseProxy(string rawProxy, int rowNumber, out ProxyEntry proxy)
    {
        proxy = new ProxyEntry();
        rawProxy = NormalizeField(rawProxy);

        if (TryParseUriProxy(rawProxy, rowNumber, out proxy))
        {
            return true;
        }

        var parts = rawProxy.Split(':');
        string scheme;
        string host;
        string portText;
        var username = "";
        var password = "";

        if (parts.Length >= 5 && LooksLikeScheme(parts[0]))
        {
            scheme = NormalizeScheme(parts[0]);
            host = parts[1].Trim().TrimStart('/');
            portText = parts[2].Trim();
            username = parts[3].Trim();
            password = string.Join(':', parts.Skip(4)).Trim();
        }
        else if (parts.Length >= 4)
        {
            scheme = "http";
            host = parts[0].Trim();
            portText = parts[1].Trim();
            username = parts[2].Trim();
            password = string.Join(':', parts.Skip(3)).Trim();
        }
        else if (parts.Length == 3 && LooksLikeScheme(parts[0]))
        {
            scheme = NormalizeScheme(parts[0]);
            host = parts[1].Trim().TrimStart('/');
            portText = parts[2].Trim();
        }
        else if (parts.Length == 2)
        {
            scheme = "http";
            host = parts[0].Trim();
            portText = parts[1].Trim();
        }
        else
        {
            return false;
        }

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        proxy = new ProxyEntry
        {
            RowNumber = rowNumber,
            Scheme = NormalizeScheme(scheme),
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            RawProxy = rawProxy
        };
        return true;
    }

    private static bool TryParseUriProxy(string rawProxy, int rowNumber, out ProxyEntry proxy)
    {
        proxy = new ProxyEntry();

        if (!rawProxy.Contains("://", StringComparison.Ordinal)
            || !Uri.TryCreate(rawProxy, UriKind.Absolute, out var uri)
            || !LooksLikeScheme(uri.Scheme)
            || uri.Port <= 0)
        {
            return false;
        }

        var username = "";
        var password = "";
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            var userParts = uri.UserInfo.Split(':', 2);
            username = Uri.UnescapeDataString(userParts[0]);
            password = userParts.Length > 1 ? Uri.UnescapeDataString(userParts[1]) : "";
        }

        proxy = new ProxyEntry
        {
            RowNumber = rowNumber,
            Scheme = NormalizeScheme(uri.Scheme),
            Host = uri.Host,
            Port = uri.Port,
            Username = username,
            Password = password,
            RawProxy = rawProxy
        };
        return true;
    }

    private static bool LooksLikeScheme(string value)
    {
        return value.Equals("http", StringComparison.OrdinalIgnoreCase)
            || value.Equals("httpa", StringComparison.OrdinalIgnoreCase)
            || value.Equals("https", StringComparison.OrdinalIgnoreCase)
            || value.Equals("socks5", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeScheme(string value)
    {
        var scheme = value.Trim().ToLowerInvariant();
        return scheme == "httpa" ? "http" : scheme;
    }

    private static string NormalizeField(string value)
    {
        return value.Trim().TrimStart('\uFEFF');
    }

    private static List<string> ParseLine(string line)
    {
        var result = new List<string>();
        var current = new List<char>();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Add('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                result.Add(new string(current.ToArray()));
                current.Clear();
            }
            else
            {
                current.Add(ch);
            }
        }

        result.Add(new string(current.ToArray()));
        return result;
    }

    private static int FindHeader(IReadOnlyList<string> headers, string name)
    {
        return headers.ToList().FindIndex(header => string.Equals(header.Trim().TrimStart('\uFEFF'), name, StringComparison.OrdinalIgnoreCase));
    }

    private static int FindAnyHeader(IReadOnlyList<string> headers, params string[] names)
    {
        foreach (var name in names)
        {
            var index = FindHeader(headers, name);
            if (index >= 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string GetField(IReadOnlyList<string> fields, int index)
    {
        return index >= 0 && index < fields.Count ? NormalizeField(fields[index]) : "";
    }
}
