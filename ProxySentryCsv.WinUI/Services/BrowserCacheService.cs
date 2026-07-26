using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class BrowserCacheService
{
    private static readonly string[] SystemProviders = ["chrome", "edge", "firefox"];

    private static readonly string[] ChromiumCacheRelativePaths =
    [
        "Default\\Cache",
        "Default\\Code Cache",
        "Default\\GPUCache",
        "Default\\DawnCache",
        "Default\\ShaderCache",
        "Default\\GrShaderCache",
        "Default\\Media Cache",
        "Default\\Network\\Cache",
        "Default\\Service Worker\\CacheStorage",
        "Default\\Service Worker\\ScriptCache",
        "Cache",
        "Code Cache",
        "GPUCache",
        "DawnCache",
        "ShaderCache",
        "GrShaderCache"
    ];

    private static readonly string[] FirefoxCacheRelativePaths =
    [
        "cache2",
        "startupCache",
        "jumpListCache",
        "OfflineCache"
    ];

    private readonly string _profilesRoot;

    public BrowserCacheService(string appDataPath)
    {
        _profilesRoot = Path.Combine(appDataPath, "SystemBrowserProfilesClean");
    }

    public Task<long> GetTotalCacheSizeAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var total = 0L;
            foreach (var profilePath in EnumerateAllProfilePaths())
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += GetCacheSize(profilePath);
            }

            return total;
        }, cancellationToken);
    }

    public Task<BrowserCacheResult> ClearAllCacheAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var result = new BrowserCacheResult();
            foreach (var profilePath in EnumerateAllProfilePaths())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(ClearCache(profilePath));
            }

            return result;
        }, cancellationToken);
    }

    public Task<BrowserCacheResult> ClearProxyCacheAsync(ProxyEntry proxy, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var result = new BrowserCacheResult();
            foreach (var profilePath in EnumerateProfilePaths(proxy))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(ClearCache(profilePath));
            }

            return result;
        }, cancellationToken);
    }

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        double display = value;
        while (display >= 1024 && unit < suffixes.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value} {suffixes[unit]}"
            : $"{display:0.##} {suffixes[unit]}";
    }

    private IEnumerable<string> EnumerateAllProfilePaths()
    {
        if (!Directory.Exists(_profilesRoot))
        {
            yield break;
        }

        foreach (var provider in SystemProviders)
        {
            var providerRoot = Path.Combine(_profilesRoot, provider);
            if (!Directory.Exists(providerRoot))
            {
                continue;
            }

            foreach (var profilePath in Directory.EnumerateDirectories(providerRoot, "proxy-*", SearchOption.TopDirectoryOnly))
            {
                yield return profilePath;
            }
        }
    }

    private IEnumerable<string> EnumerateProfilePaths(ProxyEntry proxy)
    {
        foreach (var provider in SystemProviders)
        {
            var profilePath = Path.Combine(_profilesRoot, provider, $"proxy-{proxy.ProfileKey}");
            if (Directory.Exists(profilePath))
            {
                yield return profilePath;
            }
        }
    }

    private static long GetCacheSize(string profilePath)
    {
        var total = 0L;
        foreach (var cachePath in EnumerateCachePaths(profilePath))
        {
            total += GetDirectorySize(cachePath);
        }

        return total;
    }

    private static BrowserCacheResult ClearCache(string profilePath)
    {
        var result = new BrowserCacheResult();
        foreach (var cachePath in EnumerateCachePaths(profilePath))
        {
            result.BytesBefore += GetDirectorySize(cachePath);
            ClearDirectoryContents(cachePath, result);
        }

        return result;
    }

    private static IEnumerable<string> EnumerateCachePaths(string profilePath)
    {
        var isFirefox = profilePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, "firefox", StringComparison.OrdinalIgnoreCase));
        var relativePaths = isFirefox ? FirefoxCacheRelativePaths : ChromiumCacheRelativePaths;
        foreach (var relativePath in relativePaths)
        {
            var path = Path.Combine(profilePath, relativePath);
            if (Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static long GetDirectorySize(string directoryPath)
    {
        var total = 0L;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // Cache files may disappear while a browser is closing.
                }
            }
        }
        catch
        {
            // Cache folders may be locked or removed by the browser.
        }

        return total;
    }

    private static void ClearDirectoryContents(string directoryPath, BrowserCacheResult result)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    result.DeletedFiles++;
                }
                catch
                {
                    result.FailedItems++;
                }
            }

            var directories = Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length)
                .ToList();
            foreach (var directory in directories)
            {
                try
                {
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory, recursive: false);
                        result.DeletedDirectories++;
                    }
                }
                catch
                {
                    result.FailedItems++;
                }
            }
        }
        catch
        {
            result.FailedItems++;
        }
    }
}

public sealed class BrowserCacheResult
{
    public long BytesBefore { get; set; }

    public int DeletedFiles { get; set; }

    public int DeletedDirectories { get; set; }

    public int FailedItems { get; set; }

    public void Add(BrowserCacheResult result)
    {
        BytesBefore += result.BytesBefore;
        DeletedFiles += result.DeletedFiles;
        DeletedDirectories += result.DeletedDirectories;
        FailedItems += result.FailedItems;
    }
}
