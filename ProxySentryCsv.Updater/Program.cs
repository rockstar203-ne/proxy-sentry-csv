using System.Diagnostics;
using System.IO.Compression;

var options = UpdateOptions.Parse(args);
if (!options.IsValid)
{
    return 2;
}

var log = new UpdateLog(options.LogPath);

try
{
    log.Info("Updater started.");
    log.Info($"Mode: {(options.Rollback ? "rollback" : "update")}");
    log.Info($"Package: {options.PackagePath}");
    log.Info($"Backup: {options.BackupDirectory}");
    log.Info($"Root: {options.RootDirectory}");

    if (options.ProcessId is > 0)
    {
        WaitForProcessExit(options.ProcessId.Value, log);
    }

    if (options.Rollback)
    {
        ApplyRollback(options.RootDirectory, options.BackupDirectory, log);
        LaunchApp(options, log);
        log.Info("Rollback finished.");
        return 0;
    }

    var extractDirectory = Path.Combine(options.RootDirectory, "Data", "updates", "extract-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    if (Directory.Exists(extractDirectory))
    {
        Directory.Delete(extractDirectory, recursive: true);
    }

    Directory.CreateDirectory(extractDirectory);
    ZipFile.ExtractToDirectory(options.PackagePath, extractDirectory, overwriteFiles: true);

    var packageRoot = ResolvePackageRoot(extractDirectory);
    log.Info($"Resolved package root: {packageRoot}");

    ApplyUpdate(packageRoot, options.RootDirectory, log);
    TryDeleteDirectory(extractDirectory, log);

    LaunchApp(options, log);

    log.Info("Updater finished.");
    return 0;
}
catch (Exception ex)
{
    log.Error("Updater failed.", ex);
    return 1;
}

static void WaitForProcessExit(int processId, UpdateLog log)
{
    try
    {
        var process = Process.GetProcessById(processId);
        log.Info($"Waiting for process {processId} to exit.");
        if (!process.WaitForExit(30000))
        {
            log.Info($"Process {processId} did not exit in time; killing it.");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10000);
        }
    }
    catch (ArgumentException)
    {
        log.Info($"Process {processId} is already gone.");
    }
}

static string ResolvePackageRoot(string extractDirectory)
{
    if (LooksLikePortableRoot(extractDirectory))
    {
        return extractDirectory;
    }

    var direct = Directory
        .EnumerateDirectories(extractDirectory)
        .FirstOrDefault(LooksLikePortableRoot);

    if (!string.IsNullOrWhiteSpace(direct))
    {
        return direct;
    }

    throw new InvalidOperationException("The update package does not contain a Proxy Sentry CSV portable root.");
}

static bool LooksLikePortableRoot(string directory)
{
    return File.Exists(Path.Combine(directory, "Proxy Sentry CSV.exe"))
        && Directory.Exists(Path.Combine(directory, "components"))
        && File.Exists(Path.Combine(directory, "components", "Proxy Sentry CSV.exe"));
}

static void LaunchApp(UpdateOptions options, UpdateLog log)
{
    if (string.IsNullOrWhiteSpace(options.LaunchPath) || !File.Exists(options.LaunchPath))
    {
        return;
    }

    log.Info($"Launching: {options.LaunchPath}");
    Process.Start(new ProcessStartInfo
    {
        FileName = options.LaunchPath,
        WorkingDirectory = options.RootDirectory,
        UseShellExecute = true
    });
}

static void ApplyUpdate(string sourceRoot, string targetRoot, UpdateLog log)
{
    var backupRoot = Path.Combine(targetRoot, "Data", "update-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(backupRoot);

    var sourceComponents = Path.Combine(sourceRoot, "components");
    var targetComponents = Path.Combine(targetRoot, "components");
    var backupComponents = Path.Combine(backupRoot, "components");

    try
    {
        BackupFile(targetRoot, backupRoot, "Proxy Sentry CSV.exe", log);
        BackupFile(targetRoot, backupRoot, "README-ProxySentryCSV.txt", log);

        if (Directory.Exists(targetComponents))
        {
            log.Info("Backing up components.");
            Directory.Move(targetComponents, backupComponents);
        }

        log.Info("Copying components.");
        CopyDirectory(sourceComponents, targetComponents);
        CopyFileIfExists(sourceRoot, targetRoot, "Proxy Sentry CSV.exe", log);
        CopyFileIfExists(sourceRoot, targetRoot, "README-ProxySentryCSV.txt", log);
    }
    catch
    {
        log.Info("Update failed; attempting rollback.");
        TryRollback(targetRoot, backupRoot, log);
        throw;
    }

    CleanupOldBackups(Path.Combine(targetRoot, "Data", "update-backups"), keep: 3, log);
}

static void ApplyRollback(string targetRoot, string backupRoot, UpdateLog log)
{
    if (!LooksLikeUpdateBackup(backupRoot))
    {
        throw new InvalidOperationException("The selected backup does not look like a Proxy Sentry CSV update backup.");
    }

    log.Info($"Restoring update backup: {backupRoot}");
    TryRollback(targetRoot, backupRoot, log);
}

static bool LooksLikeUpdateBackup(string directory)
{
    return Directory.Exists(directory)
        && (File.Exists(Path.Combine(directory, "Proxy Sentry CSV.exe"))
            || Directory.Exists(Path.Combine(directory, "components")));
}

static void BackupFile(string targetRoot, string backupRoot, string relativePath, UpdateLog log)
{
    var source = Path.Combine(targetRoot, relativePath);
    if (!File.Exists(source))
    {
        return;
    }

    var target = Path.Combine(backupRoot, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(source, target, overwrite: true);
    log.Info($"Backed up {relativePath}.");
}

static void CopyFileIfExists(string sourceRoot, string targetRoot, string relativePath, UpdateLog log)
{
    var source = Path.Combine(sourceRoot, relativePath);
    if (!File.Exists(source))
    {
        return;
    }

    var target = Path.Combine(targetRoot, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(source, target, overwrite: true);
    log.Info($"Copied {relativePath}.");
}

static void CopyDirectory(string sourceDirectory, string targetDirectory)
{
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

static void TryRollback(string targetRoot, string backupRoot, UpdateLog log)
{
    TryRestoreFile(targetRoot, backupRoot, "Proxy Sentry CSV.exe", log);
    TryRestoreFile(targetRoot, backupRoot, "README-ProxySentryCSV.txt", log);

    var targetComponents = Path.Combine(targetRoot, "components");
    var backupComponents = Path.Combine(backupRoot, "components");
    try
    {
        if (Directory.Exists(targetComponents))
        {
            Directory.Delete(targetComponents, recursive: true);
        }

        if (Directory.Exists(backupComponents))
        {
            CopyDirectory(backupComponents, targetComponents);
            log.Info("Restored components backup.");
        }
    }
    catch (Exception ex)
    {
        log.Error("Components rollback failed.", ex);
    }
}

static void TryRestoreFile(string targetRoot, string backupRoot, string relativePath, UpdateLog log)
{
    try
    {
        var backup = Path.Combine(backupRoot, relativePath);
        if (!File.Exists(backup))
        {
            return;
        }

        File.Copy(backup, Path.Combine(targetRoot, relativePath), overwrite: true);
        log.Info($"Restored {relativePath}.");
    }
    catch (Exception ex)
    {
        log.Error($"Rollback failed for {relativePath}.", ex);
    }
}

static void CleanupOldBackups(string directory, int keep, UpdateLog log)
{
    try
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var backup in Directory.GetDirectories(directory).OrderByDescending(Path.GetFileName).Skip(keep))
        {
            Directory.Delete(backup, recursive: true);
            log.Info($"Deleted old backup: {backup}");
        }
    }
    catch (Exception ex)
    {
        log.Error("Backup cleanup failed.", ex);
    }
}

static void TryDeleteDirectory(string directory, UpdateLog log)
{
    try
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    catch (Exception ex)
    {
        log.Error($"Could not delete {directory}.", ex);
    }
}

internal sealed class UpdateOptions
{
    public string PackagePath { get; private init; } = "";
    public string BackupDirectory { get; private init; } = "";
    public string RootDirectory { get; private init; } = "";
    public string LaunchPath { get; private init; } = "";
    public string LogPath { get; private init; } = "";
    public int? ProcessId { get; private init; }
    public bool Rollback { get; private init; }

    public bool IsValid => Directory.Exists(RootDirectory)
        && (Rollback ? Directory.Exists(BackupDirectory) : File.Exists(PackagePath));

    public static UpdateOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[index][2..];
            var value = "";
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[index + 1];
                index++;
            }

            values[key] = value;
        }

        var root = values.GetValueOrDefault("root") ?? "";
        var rollback = values.ContainsKey("rollback");
        return new UpdateOptions
        {
            PackagePath = FullPathOrEmpty(values.GetValueOrDefault("package") ?? ""),
            BackupDirectory = FullPathOrEmpty(values.GetValueOrDefault("backup") ?? ""),
            RootDirectory = string.IsNullOrWhiteSpace(root) ? "" : Path.GetFullPath(root),
            LaunchPath = values.GetValueOrDefault("launch") ?? "",
            LogPath = values.GetValueOrDefault("log") ?? "",
            ProcessId = int.TryParse(values.GetValueOrDefault("pid"), out var pid) ? pid : null,
            Rollback = rollback
        };
    }

    private static string FullPathOrEmpty(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
    }
}

internal sealed class UpdateLog
{
    private readonly string _path;

    public UpdateLog(string path)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Path.GetTempPath(), "ProxySentryCsv.Updater.log")
            : path;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception exception) => Write("ERROR", $"{message} {exception}");

    private void Write(string level, string message)
    {
        try
        {
            File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never block an update.
        }
    }
}
