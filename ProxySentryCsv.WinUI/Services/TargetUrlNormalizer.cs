namespace ProxySentryCsv.Services;

public static class TargetUrlNormalizer
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(uri.Host);
        }

        return true;
    }

    public static string Normalize(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return $"https:{trimmed}";
        }

        if (trimmed.Contains("://", StringComparison.Ordinal)
            || trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("chrome:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("edge:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("firefox:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"https://{trimmed.TrimStart('/')}";
    }
}
