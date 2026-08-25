using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProxySentryCsv.Services;

public static class GitAutoUpdater
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/rockstar203-ne/proxy-sentry-csv/releases/latest";
    private const string PortableZipPrefix = "ProxySentryCSV-portable-win10-win11";
    private const string RootLauncherName = "Proxy Sentry CSV.exe";
    private const string WinUiExeName = "Proxy Sentry CSV.exe";
    private const string UpdaterExeName = "ProxySentryCsv.Updater.exe";

    public static string DetectRepositoryPath() => ResolvePortableRoot("");

    public static bool CanRollback(string updateRoot, string? preferredBackup = null)
    {
        var root = ResolvePortableRoot(updateRoot);
        if (!string.IsNullOrWhiteSpace(preferredBackup) && LooksLikeUpdateBackup(preferredBackup))
        {
            return true;
        }

        return GetLatestBackupPath(root) is not null;
    }

    public static string? GetLatestBackupPath(string updateRoot)
    {
        var root = ResolvePortableRoot(updateRoot);
        var backupsDirectory = Path.Combine(root, "Data", "update-backups");
        if (!Directory.Exists(backupsDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateDirectories(backupsDirectory)
            .Where(LooksLikeUpdateBackup)
            .OrderByDescending(Path.GetFileName)
            .FirstOrDefault();
    }

    public static async Task<UpdateCheckResult> CheckAsync(
        string repositoryPath,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var updateRoot = ResolvePortableRoot(repositoryPath);
        log?.Invoke("Checking GitHub release channel.");
        log?.Invoke($"Portable root: {updateRoot}");

        var manifest = await FetchLatestManifestAsync(updateRoot, log, cancellationToken);
        var currentVersion = CurrentVersion;
        var remoteVersion = manifest.Version;
        var isUpdateAvailable = IsRemoteVersionNewer(remoteVersion, currentVersion);

        log?.Invoke(isUpdateAvailable
            ? $"Update check result: update available {currentVersion} -> {remoteVersion}."
            : $"Update check result: already on the latest version ({currentVersion}).");

        return new UpdateCheckResult(
            currentVersion,
            remoteVersion,
            currentVersion,
            remoteVersion,
            isUpdateAvailable);
    }

    public static async Task<UpdateResult> UpdateAsync(
        string repositoryPath,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var updateRoot = ResolvePortableRoot(repositoryPath);
        log?.Invoke($"Portable root: {updateRoot}");
        var manifest = await FetchLatestManifestAsync(updateRoot, log, cancellationToken);
        var currentVersion = CurrentVersion;
        if (!IsRemoteVersionNewer(manifest.Version, currentVersion))
        {
            return new UpdateResult(currentVersion, currentVersion, "Already up to date.");
        }

        var packagePath = await DownloadPackageAsync(updateRoot, manifest, log, cancellationToken);
        StartInstaller(updateRoot, packagePath, log);
        return new UpdateResult(currentVersion, manifest.Version, packagePath);
    }

    public static async Task<string> PullAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var result = await UpdateAsync(repositoryPath, cancellationToken: cancellationToken);
        return result.Output;
    }

    public static Task<UpdateResult> RollbackAsync(
        string repositoryPath,
        string previousHead,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var updateRoot = ResolvePortableRoot(repositoryPath);
        var backupPath = !string.IsNullOrWhiteSpace(previousHead) && LooksLikeUpdateBackup(previousHead)
            ? previousHead
            : GetLatestBackupPath(updateRoot);
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new InvalidOperationException("No previous update backup was found.");
        }

        var currentVersion = CurrentVersion;
        var backupVersion = TryReadBackupVersion(backupPath);
        StartRollback(updateRoot, backupPath, log);
        return Task.FromResult(new UpdateResult(currentVersion, string.IsNullOrWhiteSpace(backupVersion) ? "previous" : backupVersion, backupPath));
    }

    private static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static async Task<UpdateManifest> FetchLatestManifestAsync(
        string updateRoot,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(updateRoot);
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
        ApplyGitHubApiHeaders(request);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        await EnsureUpdateResponseAsync(response, "check for updates", cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var manifest = ParseGitHubRelease(json);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Url))
        {
            throw new InvalidOperationException("The latest GitHub release does not contain a Proxy Sentry CSV portable ZIP.");
        }

        log?.Invoke($"Latest release: {manifest.Version}");
        log?.Invoke($"Package asset: {manifest.AssetName}");
        return manifest;
    }

    private static async Task<string> DownloadPackageAsync(
        string updateRoot,
        UpdateManifest manifest,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var updatesDirectory = Path.Combine(updateRoot, "Data", "updates");
        Directory.CreateDirectory(updatesDirectory);

        var safeAssetName = SanitizeFileName(string.IsNullOrWhiteSpace(manifest.AssetName)
            ? $"{PortableZipPrefix}-{manifest.Version}.zip"
            : manifest.AssetName);
        var packagePath = Path.Combine(updatesDirectory, safeAssetName);
        var partialPath = packagePath + ".download";
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        using var client = CreateClient(updateRoot);
        using var request = new HttpRequestMessage(HttpMethod.Get, manifest.Url);
        if (IsGitHubReleaseAssetApiUrl(manifest.Url))
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("application/octet-stream");
        }
        else
        {
            ApplyGitHubApiHeaders(request);
        }

        log?.Invoke("Downloading update package.");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureUpdateResponseAsync(response, "download the update", cancellationToken);

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(partialPath))
        {
            var buffer = new byte[1024 * 128];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            var actual = await ComputeSha256Async(partialPath, cancellationToken);
            if (!string.Equals(actual, NormalizeSha256(manifest.Sha256), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partialPath);
                throw new InvalidOperationException($"Downloaded update checksum mismatch. Expected {manifest.Sha256}, got {actual}.");
            }
        }

        ValidatePortablePackage(partialPath);
        File.Move(partialPath, packagePath, overwrite: true);
        log?.Invoke($"Downloaded package: {packagePath}");
        return packagePath;
    }

    private static void StartInstaller(string updateRoot, string packagePath, Action<string>? log)
    {
        var updaterRunPath = PrepareUpdaterRunner(updateRoot, packagePath, log);
        var logPath = Path.Combine(updateRoot, "Data", "updates", "updater.log");
        var launchPath = Path.Combine(updateRoot, RootLauncherName);

        log?.Invoke($"Starting updater: {updaterRunPath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterRunPath,
            WorkingDirectory = updateRoot,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--package");
        startInfo.ArgumentList.Add(packagePath);
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(updateRoot);
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--launch");
        startInfo.ArgumentList.Add(launchPath);
        startInfo.ArgumentList.Add("--log");
        startInfo.ArgumentList.Add(logPath);
        Process.Start(startInfo);
    }

    private static void StartRollback(string updateRoot, string backupPath, Action<string>? log)
    {
        var updaterRunPath = PrepareUpdaterRunner(updateRoot, null, log);
        var logPath = Path.Combine(updateRoot, "Data", "updates", "updater.log");
        var launchPath = Path.Combine(updateRoot, RootLauncherName);

        log?.Invoke($"Starting rollback updater: {updaterRunPath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterRunPath,
            WorkingDirectory = updateRoot,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--rollback");
        startInfo.ArgumentList.Add("--backup");
        startInfo.ArgumentList.Add(backupPath);
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(updateRoot);
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--launch");
        startInfo.ArgumentList.Add(launchPath);
        startInfo.ArgumentList.Add("--log");
        startInfo.ArgumentList.Add(logPath);
        Process.Start(startInfo);
    }

    private static string PrepareUpdaterRunner(string updateRoot, string? packagePath, Action<string>? log)
    {
        var runDirectory = Path.Combine(updateRoot, "Data", "updates", "runner");
        Directory.CreateDirectory(runDirectory);
        var runPath = Path.Combine(runDirectory, UpdaterExeName);

        var source = ResolveUpdaterSourcePath(updateRoot);
        if (File.Exists(source))
        {
            File.Copy(source, runPath, overwrite: true);
            return runPath;
        }

        if (!string.IsNullOrWhiteSpace(packagePath) && File.Exists(packagePath))
        {
            log?.Invoke("Local updater is missing; extracting updater from the downloaded package.");
            if (TryExtractUpdaterFromPackage(packagePath, runPath))
            {
                return runPath;
            }
        }

        throw new FileNotFoundException(
            "ProxySentryCsv.Updater.exe was not found in the application components or in the downloaded update package.",
            source);
    }

    private static bool TryExtractUpdaterFromPackage(string packagePath, string runPath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var updaterEntry = archive.Entries.FirstOrDefault(entry =>
        {
            var name = entry.FullName.Replace('\\', '/');
            return name.EndsWith($"/components/{UpdaterExeName}", StringComparison.OrdinalIgnoreCase)
                || name.Equals($"components/{UpdaterExeName}", StringComparison.OrdinalIgnoreCase);
        });

        if (updaterEntry is null)
        {
            return false;
        }

        updaterEntry.ExtractToFile(runPath, overwrite: true);
        return true;
    }

    private static string ResolveUpdaterSourcePath(string updateRoot)
    {
        var appDirectory = AppContext.BaseDirectory;
        var local = Path.Combine(appDirectory, UpdaterExeName);
        if (File.Exists(local))
        {
            return local;
        }

        var rootLocal = Path.Combine(updateRoot, "components", UpdaterExeName);
        if (File.Exists(rootLocal))
        {
            return rootLocal;
        }

        var devCandidate = Path.GetFullPath(Path.Combine(
            appDirectory,
            "..", "..", "..", "..", "..",
            "ProxySentryCsv.Updater",
            "bin",
            "Release",
            "net8.0-windows",
            "win-x64",
            "publish",
            UpdaterExeName));
        return File.Exists(devCandidate) ? devCandidate : local;
    }

    private static HttpClient CreateClient(string updateRoot)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxySentryCSV-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, application/octet-stream;q=0.9, */*;q=0.8");

        return client;
    }

    private static void ApplyGitHubApiHeaders(HttpRequestMessage request)
    {
        if (!Uri.TryCreate(request.RequestUri?.ToString(), UriKind.Absolute, out var uri)
            || !uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    private static async Task EnsureUpdateResponseAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        _ = await response.Content.ReadAsStringAsync(cancellationToken);
        var githubRequest = response.RequestMessage?.RequestUri?.Host.Equals(
            "api.github.com",
            StringComparison.OrdinalIgnoreCase) == true;
        var authFailure = response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound;
        if (githubRequest && authFailure)
        {
            throw new InvalidOperationException(
                $"GitHub could not {operation} (HTTP {(int)response.StatusCode}). "
                + "Check internet access and the public Proxy Sentry CSV release channel.");
        }

        response.EnsureSuccessStatusCode();
    }

    private static UpdateManifest? ParseGitHubRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagProperty))
        {
            return JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        var version = (tagProperty.GetString() ?? "").TrimStart('v', 'V');
        var releaseUrl = root.GetPropertyOrDefault("html_url");
        GitHubAsset? selectedAsset = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetPropertyOrDefault("name");
                if (!IsPortableZipAsset(name))
                {
                    continue;
                }

                var candidate = new GitHubAsset(
                    name,
                    FirstNonEmpty(
                        asset.GetPropertyOrDefault("url"),
                        asset.GetPropertyOrDefault("browser_download_url")),
                    asset.GetPropertyOrDefault("digest"));

                if (selectedAsset is null
                    || name.StartsWith(PortableZipPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    selectedAsset = candidate;
                }
            }
        }

        var sha256 = "";
        if (!string.IsNullOrWhiteSpace(selectedAsset?.Digest)
            && selectedAsset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            sha256 = selectedAsset.Digest["sha256:".Length..];
        }

        return new UpdateManifest
        {
            Version = version,
            Url = selectedAsset?.Url ?? "",
            AssetName = selectedAsset?.Name ?? "",
            Sha256 = sha256,
            ReleaseUrl = releaseUrl
        };
    }

    private static bool IsPortableZipAsset(string name)
    {
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && (name.StartsWith(PortableZipPrefix, StringComparison.OrdinalIgnoreCase)
                || name.Contains("ProxySentryCSV-portable", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRemoteVersionNewer(string remoteVersion, string currentVersion)
    {
        var remote = ParseVersion(remoteVersion);
        var current = ParseVersion(currentVersion);
        return remote is not null && current is not null
            ? remote > current
            : !string.Equals(remoteVersion.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static Version? ParseVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out var version) ? version : null;
    }

    private static bool IsGitHubReleaseAssetApiUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/releases/assets/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeSha256(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? trimmed["sha256:".Length..]
            : trimmed;
    }

    private static void ValidatePortablePackage(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var hasRootExe = archive.Entries.Any(entry =>
            entry.FullName.Replace('\\', '/').EndsWith($"/{RootLauncherName}", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.Equals(RootLauncherName, StringComparison.OrdinalIgnoreCase));
        var hasComponents = archive.Entries.Any(entry =>
            entry.FullName.Replace('\\', '/').Contains("/components/", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.StartsWith("components/", StringComparison.OrdinalIgnoreCase));
        var hasWinUiExe = archive.Entries.Any(entry =>
            entry.FullName.Replace('\\', '/').EndsWith($"/components/{WinUiExeName}", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.Replace('\\', '/').Equals($"components/{WinUiExeName}", StringComparison.OrdinalIgnoreCase));

        if (!hasRootExe || !hasComponents || !hasWinUiExe)
        {
            throw new InvalidOperationException("The downloaded ZIP is not a Proxy Sentry CSV portable package.");
        }
    }

    private static bool LooksLikeUpdateBackup(string directory)
    {
        return Directory.Exists(directory)
            && (File.Exists(Path.Combine(directory, RootLauncherName))
                || Directory.Exists(Path.Combine(directory, "components")));
    }

    private static string TryReadBackupVersion(string directory)
    {
        var candidates = new[]
        {
            Path.Combine(directory, "components", WinUiExeName),
            Path.Combine(directory, "components", "Proxy Sentry CSV.dll"),
            Path.Combine(directory, RootLauncherName)
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var version = FileVersionInfo.GetVersionInfo(candidate).ProductVersion;
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version.Trim();
                }
            }
            catch
            {
                // Version display is best-effort only.
            }
        }

        return "";
    }

    private static string ResolvePortableRoot(string configuredRoot)
    {
        var appDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var appInfo = new DirectoryInfo(appDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (appInfo.Name.Equals("components", StringComparison.OrdinalIgnoreCase)
            && appInfo.Parent is not null)
        {
            return appInfo.Parent.FullName;
        }

        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var full = Path.GetFullPath(configuredRoot);
            if (Directory.Exists(Path.Combine(full, "components")))
            {
                return full;
            }
        }

        return appDirectory;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? $"{PortableZipPrefix}.zip" : cleaned;
    }

    public sealed record UpdateCheckResult(
        string LocalHead,
        string RemoteHead,
        string LocalVersion,
        string RemoteVersion,
        bool IsUpdateAvailable);

    public sealed record UpdateResult(string PreviousHead, string CurrentHead, string Output);

    private sealed record GitHubAsset(string Name, string Url, string Digest);

    private sealed class UpdateManifest
    {
        public string Version { get; set; } = "";
        public string Url { get; set; } = "";
        public string AssetName { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string ReleaseUrl { get; set; } = "";
    }
}

internal static class JsonElementUpdateExtensions
{
    public static string GetPropertyOrDefault(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }
}
