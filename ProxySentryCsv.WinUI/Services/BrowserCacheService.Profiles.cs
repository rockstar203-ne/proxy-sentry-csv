using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed record ProfileStorageSize(long CacheBytes, long TotalBytes);

public sealed partial class BrowserCacheService
{
    public Task<ProfileStorageSize> GetProfileStorageAsync(ProxyEntry proxy, string provider) => Task.Run(() =>
    {
        var path = ValidateProfilePath(proxy, provider);
        ValidateProfileTree(path);
        return new ProfileStorageSize(GetCacheSize(path), GetDirectorySize(path));
    });

    public Task<BrowserCacheResult> ClearProfileCacheAsync(ProxyEntry proxy, string provider) => Task.Run(() =>
    {
        var path = ValidateProfilePath(proxy, provider);
        ValidateProfileTree(path);
        return ClearCache(path);
    });

    public Task ResetProfileAsync(ProxyEntry proxy, string provider, Action ensureStopped) => Task.Run(() =>
    {
        if (proxy.IsOpen) throw new InvalidOperationException("Close the profile before resetting its data.");
        ensureStopped();
        var path = ValidateProfilePath(proxy, provider);
        ValidateProfileTree(path);
        ensureStopped();
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    });

    private string ValidateProfilePath(ProxyEntry proxy, string provider)
    {
        if (!SystemProviders.Contains(provider, StringComparer.Ordinal))
            throw new ArgumentException("Profile data controls support Chrome, Edge and Firefox only.");
        var key = proxy.ProfileKey;
        if (key.Length != 16 || !key.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid profile key.");
        var root = Path.GetFullPath(_profilesRoot);
        var path = Path.GetFullPath(Path.Combine(root, provider, "proxy-" + key));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Profile path is outside the managed profile directory.");
        // Reject links at every ancestor, not only inside the browser profile.
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked profile directories cannot be cleared or reset.");
        return path;
    }

    private static void ValidateProfileTree(string path)
    {
        if (!Directory.Exists(path)) return;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Profile contains a linked file or directory; operation stopped.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
    }
}
