using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class BrowserExtensionStore
{
    private const string StoreUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";
    private readonly string _root;
    private readonly string _unpackedRoot;
    private readonly string _packagesRoot;

    public BrowserExtensionStore(string appDataPath)
    {
        _root = Path.Combine(appDataPath, "GlobalBrowserExtensions");
        _unpackedRoot = Path.Combine(_root, "unpacked");
        _packagesRoot = Path.Combine(_root, "packages");
        Directory.CreateDirectory(_unpackedRoot);
        Directory.CreateDirectory(_packagesRoot);
    }

    public BrowserExtensionEntry Import(string sourcePath)
    {
        if (Directory.Exists(sourcePath))
        {
            return ImportDirectory(sourcePath);
        }

        if (File.Exists(sourcePath))
        {
            return ImportPackage(sourcePath);
        }

        throw new FileNotFoundException("Extension source was not found.", sourcePath);
    }

    public async Task<BrowserExtensionEntry> ImportFromStoreUrlAsync(
        string storeUrl,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = ParseStoreUrl(storeUrl);
        progress?.Invoke($"Downloading {source.DisplayName} extension.");

        return source.Kind switch
        {
            StoreExtensionKind.Chrome => await ImportChromiumStorePackageAsync(
                source,
                BuildChromeStoreDownloadUrl(source.ExtensionId),
                useChrome: true,
                useEdge: false,
                progress,
                cancellationToken),
            StoreExtensionKind.Edge => await ImportChromiumStorePackageAsync(
                source,
                BuildEdgeStoreDownloadUrl(source.ExtensionId),
                useChrome: false,
                useEdge: true,
                progress,
                cancellationToken),
            StoreExtensionKind.Firefox => await ImportFirefoxStorePackageAsync(source, progress, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported extension store URL.")
        };
    }

    public void Remove(BrowserExtensionEntry extension)
    {
        DeleteOwnedPath(extension.UnpackedPath);
        DeleteOwnedPath(extension.PackagePath);
    }

    private BrowserExtensionEntry ImportDirectory(string sourcePath)
    {
        var manifestPath = Path.Combine(sourcePath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException("The selected folder does not contain manifest.json.");
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var name = GetManifestName(manifest.RootElement, Path.GetFileName(sourcePath), sourcePath);
        var id = CreateEntryId(name, sourcePath);
        var targetPath = Path.Combine(_unpackedRoot, id);
        CopyDirectory(sourcePath, targetPath);

        return new BrowserExtensionEntry
        {
            Id = id,
            Name = name,
            SourceKind = "unpacked",
            UnpackedPath = targetPath,
            PackagePath = "",
            FirefoxExtensionId = "",
            Enabled = true,
            UseChrome = true,
            UseEdge = true,
            UseFirefox = false
        };
    }

    private BrowserExtensionEntry ImportPackage(
        string sourcePath,
        string? sourceKind = null,
        bool? useChrome = null,
        bool? useEdge = null,
        bool? useFirefox = null)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".zip" and not ".xpi" and not ".crx")
        {
            throw new InvalidOperationException("Use an unpacked extension folder, .zip, .crx, or signed Firefox .xpi package.");
        }

        var archivePath = sourcePath;
        var temporaryZipPath = "";
        if (extension == ".crx")
        {
            temporaryZipPath = Path.Combine(Path.GetTempPath(), $"proxy-sentry-extension-{Guid.NewGuid():N}.zip");
            ExtractCrxZipPayload(sourcePath, temporaryZipPath);
            archivePath = temporaryZipPath;
        }

        using var cleanup = new TempFileCleanup(temporaryZipPath);
        using var archive = ZipFile.OpenRead(archivePath);
        var manifestEntry = FindManifestEntry(archive)
            ?? throw new InvalidOperationException("The extension package does not contain manifest.json.");

        using var manifestStream = manifestEntry.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var name = GetManifestName(
            manifest.RootElement,
            Path.GetFileNameWithoutExtension(sourcePath),
            sourceDirectory: null,
            archive,
            manifestEntry.FullName);
        var firefoxId = GetFirefoxExtensionId(manifest.RootElement);
        var id = CreateEntryId(name, sourcePath);

        var packagePath = Path.Combine(_packagesRoot, $"{id}{extension}");
        File.Copy(sourcePath, packagePath, overwrite: true);

        var unpackedPath = Path.Combine(_unpackedRoot, id);
        Directory.CreateDirectory(unpackedPath);
        ZipFile.ExtractToDirectory(archivePath, unpackedPath, overwriteFiles: true);
        var unpackedExtensionPath = GetExtractedManifestDirectory(unpackedPath, manifestEntry.FullName);

        return new BrowserExtensionEntry
        {
            Id = id,
            Name = name,
            SourceKind = sourceKind ?? extension.TrimStart('.'),
            UnpackedPath = unpackedExtensionPath,
            PackagePath = packagePath,
            FirefoxExtensionId = firefoxId,
            Enabled = true,
            UseChrome = useChrome ?? extension is ".zip" or ".crx",
            UseEdge = useEdge ?? extension is ".zip" or ".crx",
            UseFirefox = useFirefox ?? (extension == ".xpi" && !string.IsNullOrWhiteSpace(firefoxId))
        };
    }

    private async Task<BrowserExtensionEntry> ImportChromiumStorePackageAsync(
        StoreExtensionSource source,
        Uri downloadUrl,
        bool useChrome,
        bool useEdge,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var tempDirectory = CreateTempImportDirectory();
        try
        {
            var packagePath = Path.Combine(tempDirectory, $"{source.ExtensionId}.crx");
            await DownloadFileAsync(downloadUrl, packagePath, cancellationToken);
            progress?.Invoke($"Downloaded {source.DisplayName} package.");
            return ImportPackage(
                packagePath,
                sourceKind: source.SourceKind,
                useChrome: useChrome,
                useEdge: useEdge,
                useFirefox: false);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task<BrowserExtensionEntry> ImportFirefoxStorePackageAsync(
        StoreExtensionSource source,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var tempDirectory = CreateTempImportDirectory();
        try
        {
            var xpiUrl = await ResolveFirefoxXpiUrlAsync(source.ExtensionId, cancellationToken);
            var packagePath = Path.Combine(tempDirectory, $"{source.ExtensionId}.xpi");
            await DownloadFileAsync(xpiUrl, packagePath, cancellationToken);
            progress?.Invoke("Downloaded Firefox signed XPI package.");
            return ImportPackage(
                packagePath,
                sourceKind: source.SourceKind,
                useChrome: false,
                useEdge: false,
                useFirefox: true);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private void DeleteOwnedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(_root);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
        else if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    private static string CreateEntryId(string name, string sourcePath)
    {
        var safeName = MakeSafeName(name);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sourcePath}|{Guid.NewGuid():N}")))[..8].ToLowerInvariant();
        return $"{safeName}-{stamp}-{hash}";
    }

    private static string MakeSafeName(string value)
    {
        var chars = (string.IsNullOrWhiteSpace(value) ? "extension" : value.Trim())
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var compact = string.Join('-', new string(chars)
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(compact))
        {
            compact = "extension";
        }

        return compact.Length > 42 ? compact[..42] : compact;
    }

    private static string GetManifestName(
        JsonElement root,
        string fallback,
        string? sourceDirectory = null,
        ZipArchive? archive = null,
        string manifestEntryName = "")
    {
        var name = root.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(name) && !IsManifestMessageReference(name))
        {
            return name.Trim();
        }

        var localizedName = ResolveManifestMessage(root, name, sourceDirectory, archive, manifestEntryName);
        if (!string.IsNullOrWhiteSpace(localizedName))
        {
            return localizedName.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? "Extension" : fallback.Trim();
    }

    private static bool IsManifestMessageReference(string value)
    {
        return value.StartsWith("__MSG_", StringComparison.OrdinalIgnoreCase)
            && value.EndsWith("__", StringComparison.Ordinal);
    }

    private static string ResolveManifestMessage(
        JsonElement root,
        string? value,
        string? sourceDirectory,
        ZipArchive? archive,
        string manifestEntryName)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsManifestMessageReference(value))
        {
            return "";
        }

        var messageKey = value[6..^2];
        if (string.IsNullOrWhiteSpace(messageKey))
        {
            return "";
        }

        foreach (var locale in GetManifestLocaleCandidates(root))
        {
            var message = sourceDirectory is not null
                ? TryReadLocaleMessageFromDirectory(sourceDirectory, locale, messageKey)
                : TryReadLocaleMessageFromArchive(archive, manifestEntryName, locale, messageKey);
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }

        return "";
    }

    private static IEnumerable<string> GetManifestLocaleCandidates(JsonElement root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("default_locale", out var defaultLocaleElement))
        {
            var defaultLocale = defaultLocaleElement.GetString();
            if (!string.IsNullOrWhiteSpace(defaultLocale) && seen.Add(defaultLocale))
            {
                yield return defaultLocale;
            }
        }

        foreach (var locale in new[] { "en_US", "en", "uk", "ru" })
        {
            if (seen.Add(locale))
            {
                yield return locale;
            }
        }
    }

    private static string TryReadLocaleMessageFromDirectory(string sourceDirectory, string locale, string key)
    {
        var messagesPath = Path.Combine(sourceDirectory, "_locales", locale, "messages.json");
        if (!File.Exists(messagesPath))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(messagesPath));
            return TryGetLocaleMessage(document.RootElement, key);
        }
        catch
        {
            return "";
        }
    }

    private static string TryReadLocaleMessageFromArchive(
        ZipArchive? archive,
        string manifestEntryName,
        string locale,
        string key)
    {
        if (archive is null)
        {
            return "";
        }

        var manifestPrefix = Path.GetDirectoryName(manifestEntryName.Replace('\\', '/'))?.Replace('\\', '/');
        var candidates = string.IsNullOrWhiteSpace(manifestPrefix)
            ? new[] { $"_locales/{locale}/messages.json" }
            : new[] { $"{manifestPrefix}/_locales/{locale}/messages.json", $"_locales/{locale}/messages.json" };

        foreach (var candidate in candidates)
        {
            var entry = archive.GetEntry(candidate);
            if (entry is null)
            {
                continue;
            }

            try
            {
                using var stream = entry.Open();
                using var document = JsonDocument.Parse(stream);
                var message = TryGetLocaleMessage(document.RootElement, key);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
            catch
            {
                return "";
            }
        }

        return "";
    }

    private static string TryGetLocaleMessage(JsonElement messagesRoot, string key)
    {
        if (!TryGetJsonProperty(messagesRoot, key, out var messageEntry)
            || !messageEntry.TryGetProperty("message", out var messageValue))
        {
            return "";
        }

        return messageValue.GetString()?.Trim() ?? "";
    }

    private static bool TryGetJsonProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string GetFirefoxExtensionId(JsonElement root)
    {
        if (TryReadFirefoxId(root, "browser_specific_settings", out var browserSpecificId))
        {
            return browserSpecificId;
        }

        return TryReadFirefoxId(root, "applications", out var applicationsId)
            ? applicationsId
            : "";
    }

    private static StoreExtensionSource ParseStoreUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Enter a valid Chrome Web Store, Edge Add-ons, or Firefox Add-ons URL.");
        }

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (host.Contains("chromewebstore.google.com", StringComparison.OrdinalIgnoreCase))
        {
            var id = segments.FirstOrDefault(IsChromiumExtensionId)
                ?? throw new InvalidOperationException("Chrome Web Store extension ID was not found in the URL.");
            return new StoreExtensionSource(StoreExtensionKind.Chrome, id, "Chrome Web Store", "chrome store");
        }

        if (host.Contains("microsoftedge.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            var id = segments.FirstOrDefault(IsChromiumExtensionId)
                ?? throw new InvalidOperationException("Edge Add-ons extension ID was not found in the URL.");
            return new StoreExtensionSource(StoreExtensionKind.Edge, id, "Edge Add-ons", "edge store");
        }

        if (host.Contains("addons.mozilla.org", StringComparison.OrdinalIgnoreCase))
        {
            var addonIndex = Array.FindIndex(segments, segment => string.Equals(segment, "addon", StringComparison.OrdinalIgnoreCase));
            if (addonIndex < 0 || addonIndex + 1 >= segments.Length || string.IsNullOrWhiteSpace(segments[addonIndex + 1]))
            {
                throw new InvalidOperationException("Firefox Add-ons slug was not found in the URL.");
            }

            return new StoreExtensionSource(StoreExtensionKind.Firefox, segments[addonIndex + 1], "Firefox Add-ons", "firefox store");
        }

        throw new InvalidOperationException("Supported stores: Chrome Web Store, Microsoft Edge Add-ons, Firefox Add-ons.");
    }

    private static bool IsChromiumExtensionId(string value)
    {
        return value.Length == 32 && value.All(ch => ch is >= 'a' and <= 'p');
    }

    private static Uri BuildChromeStoreDownloadUrl(string extensionId)
    {
        return new Uri($"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=150.0.0.0&acceptformat=crx2,crx3&x=id%3D{Uri.EscapeDataString(extensionId)}%26installsource%3Dondemand%26uc");
    }

    private static Uri BuildEdgeStoreDownloadUrl(string extensionId)
    {
        return new Uri($"https://edge.microsoft.com/extensionwebstorebase/v1/crx?response=redirect&acceptformat=crx3&x=id%3D{Uri.EscapeDataString(extensionId)}%26installsource%3Dondemand%26uc");
    }

    private static async Task<Uri> ResolveFirefoxXpiUrlAsync(string slug, CancellationToken cancellationToken)
    {
        using var client = CreateStoreHttpClient();
        var apiUrl = new Uri($"https://addons.mozilla.org/api/v5/addons/addon/{Uri.EscapeDataString(slug)}/");
        using var response = await client.GetAsync(apiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var url = GetFirefoxCurrentVersionFileUrl(document.RootElement);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Firefox Add-ons download URL was not found.");
        }

        return uri;
    }

    private static string GetFirefoxCurrentVersionFileUrl(JsonElement root)
    {
        if (!root.TryGetProperty("current_version", out var currentVersion))
        {
            return "";
        }

        if (currentVersion.TryGetProperty("file", out var file)
            && file.TryGetProperty("url", out var urlElement))
        {
            return urlElement.GetString() ?? "";
        }

        if (!currentVersion.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var candidates = files.EnumerateArray().ToList();
        var preferred = candidates.FirstOrDefault(fileElement =>
            HasJsonStringValue(fileElement, "platform", "all")
            || HasJsonStringValue(fileElement, "platform", "windows"));
        if (preferred.ValueKind == JsonValueKind.Undefined)
        {
            preferred = candidates.FirstOrDefault();
        }

        return preferred.ValueKind != JsonValueKind.Undefined
            && preferred.TryGetProperty("url", out var preferredUrl)
                ? preferredUrl.GetString() ?? ""
                : "";
    }

    private static bool HasJsonStringValue(JsonElement element, string propertyName, string value)
    {
        return element.TryGetProperty(propertyName, out var property)
            && string.Equals(property.GetString(), value, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadFileAsync(Uri url, string targetPath, CancellationToken cancellationToken)
    {
        using var client = CreateStoreHttpClient();
        using var response = await GetWithStoreRedirectsAsync(client, url, cancellationToken);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(targetPath);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task<HttpResponseMessage> GetWithStoreRedirectsAsync(
        HttpClient client,
        Uri url,
        CancellationToken cancellationToken)
    {
        var current = url;
        for (var redirect = 0; redirect < 8; redirect++)
        {
            var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is < 300 or > 399)
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new InvalidOperationException("Extension store redirect did not include a download location.");
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new InvalidOperationException("Extension store returned too many redirects.");
    }

    private static HttpClient CreateStoreHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(StoreUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private static void ExtractCrxZipPayload(string crxPath, string zipPath)
    {
        using var input = File.OpenRead(crxPath);
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != 'C' || magic[1] != 'r' || magic[2] != '2' || magic[3] != '4')
        {
            throw new InvalidOperationException("The downloaded package is not a valid CRX extension.");
        }

        var version = reader.ReadUInt32();
        long zipOffset = version switch
        {
            2 => 16L + reader.ReadUInt32() + reader.ReadUInt32(),
            3 => 12L + reader.ReadUInt32(),
            _ => throw new InvalidOperationException($"Unsupported CRX version: {version}.")
        };

        if (zipOffset <= 0 || zipOffset >= input.Length)
        {
            throw new InvalidOperationException("The CRX package has an invalid ZIP payload offset.");
        }

        input.Position = zipOffset;
        using var output = File.Create(zipPath);
        input.CopyTo(output);
    }

    private static string CreateTempImportDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ProxySentryCsvExtensionImports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static ZipArchiveEntry? FindManifestEntry(ZipArchive archive)
    {
        var rootManifest = archive.GetEntry("manifest.json");
        if (rootManifest is not null)
        {
            return rootManifest;
        }

        return archive.Entries
            .Where(entry => string.Equals(Path.GetFileName(entry.FullName), "manifest.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName.Count(ch => ch is '/' or '\\'))
            .FirstOrDefault();
    }

    private static string GetExtractedManifestDirectory(string unpackedRoot, string manifestEntryName)
    {
        var normalized = manifestEntryName
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var relativeDirectory = Path.GetDirectoryName(normalized);
        if (string.IsNullOrWhiteSpace(relativeDirectory))
        {
            return unpackedRoot;
        }

        var fullRoot = Path.GetFullPath(unpackedRoot);
        var fullDirectory = Path.GetFullPath(Path.Combine(unpackedRoot, relativeDirectory));
        return fullDirectory.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            ? fullDirectory
            : unpackedRoot;
    }

    private static bool TryReadFirefoxId(JsonElement root, string sectionName, out string id)
    {
        id = "";
        if (!root.TryGetProperty(sectionName, out var section)
            || !section.TryGetProperty("gecko", out var gecko)
            || !gecko.TryGetProperty("id", out var idElement))
        {
            return false;
        }

        id = idElement.GetString()?.Trim() ?? "";
        return !string.IsNullOrWhiteSpace(id);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private sealed record StoreExtensionSource(
        StoreExtensionKind Kind,
        string ExtensionId,
        string DisplayName,
        string SourceKind);

    private enum StoreExtensionKind
    {
        Chrome,
        Edge,
        Firefox
    }

    private sealed class TempFileCleanup(string path) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
