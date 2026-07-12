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
            throw new InvalidOperationException("CSV does not contain a Proxy column.");
        }

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

            var rawProxy = fields[proxyIndex].Trim();
            if (string.IsNullOrWhiteSpace(rawProxy))
            {
                continue;
            }

            if (TryParseProxy(rawProxy, i + 1, out var proxy))
            {
                proxies.Add(proxy);
            }
        }

        return proxies;
    }

    private static bool TryParseProxy(string rawProxy, int rowNumber, out ProxyEntry proxy)
    {
        proxy = new ProxyEntry();
        rawProxy = rawProxy.Trim();

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
            scheme = parts[0];
            host = parts[1];
            portText = parts[2];
            username = parts[3];
            password = string.Join(':', parts.Skip(4));
        }
        else if (parts.Length >= 4)
        {
            scheme = "http";
            host = parts[0];
            portText = parts[1];
            username = parts[2];
            password = string.Join(':', parts.Skip(3));
        }
        else if (parts.Length == 3 && LooksLikeScheme(parts[0]))
        {
            scheme = parts[0];
            host = parts[1];
            portText = parts[2];
        }
        else if (parts.Length == 2)
        {
            scheme = "http";
            host = parts[0];
            portText = parts[1];
        }
        else
        {
            return false;
        }

        if (!int.TryParse(portText, out var port))
        {
            return false;
        }

        proxy = new ProxyEntry
        {
            RowNumber = rowNumber,
            Scheme = scheme,
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
            Scheme = uri.Scheme,
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
            || value.Equals("https", StringComparison.OrdinalIgnoreCase)
            || value.Equals("socks5", StringComparison.OrdinalIgnoreCase);
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
}
