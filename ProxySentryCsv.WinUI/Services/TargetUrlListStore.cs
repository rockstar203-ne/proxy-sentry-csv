using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class TargetUrlListStore
{
    private readonly string _root;

    public TargetUrlListStore(string appDataPath)
    {
        _root = Path.Combine(appDataPath, "TargetUrlLists");
        Directory.CreateDirectory(_root);
    }

    public TargetUrlListEntry Import(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Target URL list file was not found.", sourcePath);
        }

        var urls = ReadUrls(sourcePath);
        if (urls.Count == 0)
        {
            throw new InvalidOperationException("Target URL list does not contain valid URLs.");
        }

        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var key = CreateKey(sourcePath);
        var targetPath = Path.Combine(_root, $"{SanitizeFileName(name)}-{key}.txt");
        File.Copy(sourcePath, targetPath, overwrite: true);
        return new TargetUrlListEntry(key, name, targetPath, urls);
    }

    public TargetUrlListEntry? Load(AppStateStore.TargetUrlListState state)
    {
        if (string.IsNullOrWhiteSpace(state.Path) || !File.Exists(state.Path))
        {
            return null;
        }

        var urls = ReadUrls(state.Path);
        if (urls.Count == 0)
        {
            return null;
        }

        var key = string.IsNullOrWhiteSpace(state.Key)
            ? CreateKey(state.Path)
            : state.Key;
        var name = string.IsNullOrWhiteSpace(state.Name)
            ? Path.GetFileNameWithoutExtension(state.Path)
            : state.Name;
        return new TargetUrlListEntry(key, name, state.Path, urls, state.CurrentIndex);
    }

    public void Delete(TargetUrlListEntry list)
    {
        if (!File.Exists(list.Path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(list.Path);
        var rootPath = Path.GetFullPath(_root);
        if (fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(fullPath);
        }
    }

    private static List<string> ReadUrls(string path)
    {
        return File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => TargetUrlNormalizer.TryNormalize(line, out var normalized) ? normalized : "")
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static string CreateKey(string value)
    {
        var seed = $"{Path.GetFullPath(value)}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)))[..12]
            .ToLowerInvariant();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        var sanitized = new string(chars).Trim(' ', '.', '-');
        return string.IsNullOrWhiteSpace(sanitized) ? "target-urls" : sanitized;
    }
}
