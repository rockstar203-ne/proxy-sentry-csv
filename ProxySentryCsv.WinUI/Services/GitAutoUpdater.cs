using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ProxySentryCsv.Services;

public static class GitAutoUpdater
{
    public const string RepositoryUrl = "https://github.com/rockstar203-ne/proxy-sentry-csv.git";

    public static string DetectRepositoryPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        var defaultPath = @"E:\codex\ProxySentryCSV";
        return Directory.Exists(Path.Combine(defaultPath, ".git")) ? defaultPath : "";
    }

    public static async Task<UpdateCheckResult> CheckAsync(
        string repositoryPath,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRepositoryPath(repositoryPath);
        log?.Invoke("Checking for updates.");
        log?.Invoke($"Git safe.directory: {NormalizeGitSafeDirectory(repositoryPath)}");

        await EnsureOriginAsync(repositoryPath, log, cancellationToken);
        await RunGitAsync(repositoryPath, ["fetch", "origin", "--prune"], cancellationToken, log);

        var localHead = (await RunGitAsync(repositoryPath, ["rev-parse", "--verify", "HEAD"], cancellationToken, log)).Trim();
        var remoteHead = (await RunGitAsync(repositoryPath, ["rev-parse", "--verify", "origin/main"], cancellationToken, log)).Trim();
        var localVersion = await TryReadProjectVersionAsync(repositoryPath, "HEAD", cancellationToken, log);
        var remoteVersion = await TryReadProjectVersionAsync(repositoryPath, "origin/main", cancellationToken, log);

        log?.Invoke(string.Equals(localHead, remoteHead, StringComparison.OrdinalIgnoreCase)
            ? "Update check result: already on the latest commit."
            : $"Update check result: update available {ShortHead(localHead)} -> {ShortHead(remoteHead)}.");

        return new UpdateCheckResult(
            localHead,
            remoteHead,
            localVersion,
            remoteVersion,
            !string.Equals(localHead, remoteHead, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<UpdateResult> UpdateAsync(string repositoryPath, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ValidateRepositoryPath(repositoryPath);
        log?.Invoke($"Repository: {repositoryPath}");
        log?.Invoke($"Remote URL: {RepositoryUrl}");
        log?.Invoke($"Git safe.directory: {NormalizeGitSafeDirectory(repositoryPath)}");

        var before = (await RunGitAsync(repositoryPath, ["rev-parse", "--verify", "HEAD"], cancellationToken, log)).Trim();
        log?.Invoke($"Current commit: {ShortHead(before)}");

        await EnsureOriginAsync(repositoryPath, log, cancellationToken);
        await RunGitAsync(repositoryPath, ["fetch", "origin", "--prune"], cancellationToken, log);
        var pull = await RunGitAsync(repositoryPath, ["pull", "--ff-only", "origin", "main"], cancellationToken, log);
        var after = (await RunGitAsync(repositoryPath, ["rev-parse", "--verify", "HEAD"], cancellationToken, log)).Trim();
        log?.Invoke(before == after
            ? "Update result: already on the latest commit."
            : $"Update result: {ShortHead(before)} -> {ShortHead(after)}");

        return new UpdateResult(before, after, pull.Trim());
    }

    public static async Task<string> PullAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var result = await UpdateAsync(repositoryPath, cancellationToken: cancellationToken);
        return string.IsNullOrWhiteSpace(result.Output)
            ? $"Updated {ShortHead(result.PreviousHead)} -> {ShortHead(result.CurrentHead)}"
            : result.Output;
    }

    public static async Task<UpdateResult> RollbackAsync(string repositoryPath, string previousHead, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ValidateRepositoryPath(repositoryPath);
        log?.Invoke($"Git safe.directory: {NormalizeGitSafeDirectory(repositoryPath)}");
        if (string.IsNullOrWhiteSpace(previousHead))
        {
            throw new InvalidOperationException("No previous update commit is saved for rollback.");
        }

        var before = (await RunGitAsync(repositoryPath, ["rev-parse", "--verify", "HEAD"], cancellationToken, log)).Trim();
        var target = (await RunGitAsync(repositoryPath, ["rev-parse", "--verify", previousHead], cancellationToken, log)).Trim();
        log?.Invoke($"Rollback target: {ShortHead(target)}");
        await RunGitAsync(repositoryPath, ["reset", "--hard", target], cancellationToken, log);
        var after = (await RunGitAsync(repositoryPath, ["rev-parse", "--verify", "HEAD"], cancellationToken, log)).Trim();
        log?.Invoke($"Rollback result: {ShortHead(before)} -> {ShortHead(after)}");
        return new UpdateResult(before, after, "");
    }

    private static async Task EnsureOriginAsync(string repositoryPath, Action<string>? log, CancellationToken cancellationToken)
    {
        var remote = await RunGitAsync(repositoryPath, ["remote"], cancellationToken, log);
        var hasOrigin = remote
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), "origin", StringComparison.OrdinalIgnoreCase));

        if (!hasOrigin)
        {
            await RunGitAsync(repositoryPath, ["remote", "add", "origin", RepositoryUrl], cancellationToken, log);
            return;
        }

        var currentUrl = (await RunGitAsync(repositoryPath, ["remote", "get-url", "origin"], cancellationToken, log)).Trim();
        if (!string.Equals(currentUrl, RepositoryUrl, StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke($"Updating origin URL from {currentUrl}.");
            await RunGitAsync(repositoryPath, ["remote", "set-url", "origin", RepositoryUrl], cancellationToken, log);
        }
    }

    private static async Task<string> TryReadProjectVersionAsync(
        string repositoryPath,
        string revision,
        CancellationToken cancellationToken,
        Action<string>? log)
    {
        try
        {
            var projectText = await RunGitAsync(
                repositoryPath,
                ["show", $"{revision}:ProxySentryCsv.WinUI/ProxySentryCsv.WinUI.csproj"],
                cancellationToken,
                log);
            var match = Regex.Match(projectText, @"<Version>\s*([^<]+?)\s*</Version>", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static void ValidateRepositoryPath(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw new InvalidOperationException("Git repository path is empty.");
        }

        if (!Directory.Exists(Path.Combine(repositoryPath, ".git")))
        {
            throw new InvalidOperationException($"Git repository was not found: {repositoryPath}");
        }
    }

    private static string NormalizeGitSafeDirectory(string repositoryPath)
    {
        return Path.GetFullPath(repositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
    }

    private static async Task<string> RunGitAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken, Action<string>? log)
    {
        log?.Invoke($"git {string.Join(' ', arguments)}");
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"safe.directory={NormalizeGitSafeDirectory(repositoryPath)}");
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var output = string.Join(Environment.NewLine, [stdout.Trim(), stderr.Trim()]).Trim();
        if (!string.IsNullOrWhiteSpace(output))
        {
            log?.Invoke(output);
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}{output}");
        }

        return output;
    }

    private static string ShortHead(string head)
    {
        return string.IsNullOrWhiteSpace(head)
            ? ""
            : head.Trim()[..Math.Min(7, head.Trim().Length)];
    }

    public sealed record UpdateCheckResult(
        string LocalHead,
        string RemoteHead,
        string LocalVersion,
        string RemoteVersion,
        bool IsUpdateAvailable);

    public sealed record UpdateResult(string PreviousHead, string CurrentHead, string Output);
}
