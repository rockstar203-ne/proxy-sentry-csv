using System.Text.Json;

namespace ProxySentryCsv.Services;

public static class AtomicJsonFile
{
    private static readonly object Gate = new();

    public static T? Read<T>(string path)
    {
        lock (Gate)
        {
            foreach (var candidate in new[] { path, path + ".bak" })
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        var value = JsonSerializer.Deserialize<T>(File.ReadAllText(candidate));
                        if (value is not null) return value;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
            return default;
        }
    }

    public static void Write<T>(string path, T value)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, value, AppJsonOptions.Indented);
                    file.Flush(flushToDisk: true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
