using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class SystemBrowserLauncher : IDisposable
{
    private readonly string _profilesRoot;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackedBrowser> _running = new(StringComparer.OrdinalIgnoreCase);

    public SystemBrowserLauncher(string appDataPath)
    {
        _profilesRoot = Path.Combine(appDataPath, "SystemBrowserProfiles");
    }

    public event Action<IReadOnlyList<string>>? ProfilesClosed;

    public static bool IsSystemBrowser(string? provider)
    {
        return NormalizeProvider(provider) is "chrome" or "edge" or "firefox";
    }

    public Task LaunchAsync(ProxyEntry proxy, string targetUrl, string provider, Action<string>? progress = null)
    {
        var normalizedProvider = NormalizeProvider(provider);
        if (!IsSystemBrowser(normalizedProvider))
        {
            throw new InvalidOperationException("Select Chrome, Edge, or Firefox to use an installed Windows browser.");
        }

        Close(proxy);
        var executablePath = ResolveBrowserExecutable(normalizedProvider);
        var profilePath = GetProfilePath(normalizedProvider, proxy);
        Directory.CreateDirectory(profilePath);
        progress?.Invoke("Starting proxy relay");
        var relay = LocalProxyRelay.Start(proxy);

        try
        {
            progress?.Invoke($"Launching {GetBrowserDisplayName(normalizedProvider)}");
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false
            };

            if (normalizedProvider is "chrome" or "edge")
            {
                AddChromiumArguments(startInfo, profilePath, targetUrl, relay.Port);
            }
            else
            {
                WriteFirefoxProfile(profilePath, relay.Port);
                startInfo.ArgumentList.Add("-no-remote");
                startInfo.ArgumentList.Add("-profile");
                startInfo.ArgumentList.Add(profilePath);
                startInfo.ArgumentList.Add("-new-window");
                startInfo.ArgumentList.Add(targetUrl);
            }

            var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {GetBrowserDisplayName(normalizedProvider)}.");
            process.EnableRaisingEvents = true;
            if (normalizedProvider != "firefox")
            {
                process.Exited += (_, _) => MarkClosed(proxy.StableId);
            }
            lock (_gate)
            {
                _running[proxy.StableId] = new TrackedBrowser(process, normalizedProvider, relay);
            }
        }
        catch
        {
            relay.Dispose();
            throw;
        }

        return Task.CompletedTask;
    }

    public void Close(ProxyEntry proxy)
    {
        TrackedBrowser? tracked;
        lock (_gate)
        {
            if (!_running.TryGetValue(proxy.StableId, out tracked))
            {
                return;
            }

            _running.Remove(proxy.StableId);
        }

        try
        {
            if (tracked.Process.HasExited)
            {
                return;
            }

            if (tracked.Process.CloseMainWindow())
            {
                if (tracked.Process.WaitForExit(3000))
                {
                    return;
                }
            }

            if (!tracked.Process.HasExited)
            {
                tracked.Process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Browser processes can exit while the user is closing windows manually.
        }
        finally
        {
            tracked.Relay.Dispose();
            ProfilesClosed?.Invoke([proxy.StableId]);
        }
    }

    public void Dispose()
    {
        List<TrackedBrowser> running;
        lock (_gate)
        {
            running = _running.Values.ToList();
            _running.Clear();
        }

        foreach (var tracked in running)
        {
            try
            {
                if (!tracked.Process.HasExited)
                {
                    tracked.Process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                tracked.Relay.Dispose();
            }
        }
    }

    private static void AddChromiumArguments(ProcessStartInfo startInfo, string profilePath, string targetUrl, int relayPort)
    {
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add($"--user-data-dir={profilePath}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-session-crashed-bubble");
        startInfo.ArgumentList.Add($"--proxy-server=http://127.0.0.1:{relayPort}");
        startInfo.ArgumentList.Add("--proxy-bypass-list=<-loopback>");
        startInfo.ArgumentList.Add(targetUrl);
    }

    private static void WriteFirefoxProfile(string profilePath, int relayPort)
    {
        var prefs = new StringBuilder();
        prefs.AppendLine("user_pref(\"network.proxy.type\", 1);");
        prefs.AppendLine("user_pref(\"network.proxy.no_proxies_on\", \"\");");
        prefs.AppendLine("user_pref(\"network.proxy.share_proxy_settings\", false);");
        prefs.AppendLine("user_pref(\"signon.autologin.proxy\", true);");
        prefs.AppendLine($"user_pref(\"network.proxy.http\", {Json("127.0.0.1")});");
        prefs.AppendLine($"user_pref(\"network.proxy.http_port\", {relayPort});");
        prefs.AppendLine($"user_pref(\"network.proxy.ssl\", {Json("127.0.0.1")});");
        prefs.AppendLine($"user_pref(\"network.proxy.ssl_port\", {relayPort});");

        var prefsText = prefs.ToString();
        File.WriteAllText(Path.Combine(profilePath, "user.js"), prefsText, Encoding.UTF8);
        File.WriteAllText(Path.Combine(profilePath, "prefs.js"), prefsText, Encoding.UTF8);
    }

    private string GetProfilePath(string provider, ProxyEntry proxy)
    {
        return Path.Combine(_profilesRoot, provider, $"proxy-{proxy.ProfileKey}");
    }

    private static string ResolveBrowserExecutable(string provider)
    {
        foreach (var candidate in GetBrowserCandidates(provider))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var executableName = provider switch
        {
            "chrome" => "chrome.exe",
            "edge" => "msedge.exe",
            "firefox" => "firefox.exe",
            _ => ""
        };
        var fromPath = FindOnPath(executableName);
        if (fromPath is not null)
        {
            return fromPath;
        }

        throw new InvalidOperationException($"{GetBrowserDisplayName(provider)} was not found in Windows. Install it or add it to PATH.");
    }

    private static IEnumerable<string> GetBrowserCandidates(string provider)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return provider switch
        {
            "chrome" =>
            [
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")
            ],
            "edge" =>
            [
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe")
            ],
            "firefox" =>
            [
                Path.Combine(programFiles, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(programFilesX86, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(localAppData, "Mozilla Firefox", "firefox.exe")
            ],
            _ => []
        };
    }

    private static string? FindOnPath(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static string NormalizeProvider(string? provider)
    {
        return provider?.Trim().ToLowerInvariant() switch
        {
            "chrome" => "chrome",
            "edge" => "edge",
            "firefox" => "firefox",
            _ => ""
        };
    }

    private static string GetBrowserDisplayName(string provider)
    {
        return provider switch
        {
            "chrome" => "Chrome",
            "edge" => "Edge",
            "firefox" => "Firefox",
            _ => "Browser"
        };
    }

    private static string Json(string value)
    {
        return JsonSerializer.Serialize(value);
    }

    private void MarkClosed(string stableId)
    {
        TrackedBrowser? tracked = null;
        lock (_gate)
        {
            if (_running.TryGetValue(stableId, out tracked))
            {
                _running.Remove(stableId);
            }
        }

        if (tracked is not null)
        {
            tracked.Relay.Dispose();
            ProfilesClosed?.Invoke([stableId]);
        }
    }

    private sealed record TrackedBrowser(Process Process, string Provider, LocalProxyRelay Relay);
}
