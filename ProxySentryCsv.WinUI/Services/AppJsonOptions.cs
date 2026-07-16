using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ProxySentryCsv.Services;

internal static class AppJsonOptions
{
    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static JsonSerializerOptions CaseInsensitive { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
}
