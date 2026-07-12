using System.Diagnostics;

namespace ProxySentryCsv.Services;

public static class GitAutoUpdater
{
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

    public static async Task<string> PullAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw new InvalidOperationException("Git repository path is empty.");
        }

        if (!Directory.Exists(Path.Combine(repositoryPath, ".git")))
        {
            throw new InvalidOperationException($"Git repository was not found: {repositoryPath}");
        }

        var fetch = await RunGitAsync(repositoryPath, ["fetch", "--all", "--prune"], cancellationToken);
        var pull = await RunGitAsync(repositoryPath, ["pull", "--ff-only"], cancellationToken);
        return string.Join(Environment.NewLine, [fetch.Trim(), pull.Trim()]).Trim();
    }

    private static async Task<string> RunGitAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

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

        if (process.ExitCode != 0)
        {
            var output = string.Join(Environment.NewLine, [stdout.Trim(), stderr.Trim()]).Trim();
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}{output}");
        }

        return string.Join(Environment.NewLine, [stdout.Trim(), stderr.Trim()]).Trim();
    }
}
