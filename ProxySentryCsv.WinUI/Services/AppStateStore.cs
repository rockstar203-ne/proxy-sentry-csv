using System.Text.Json;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class AppStateStore
{
    private readonly string _statePath;
    private readonly string _legacyStatePath;

    public AppStateStore(string appDataPath)
    {
        Directory.CreateDirectory(appDataPath);
        _statePath = Path.Combine(appDataPath, "proxy_sentry_csv_state.json");
        _legacyStatePath = Path.Combine(appDataPath, "maestro_proxy_launcher_state.json");
    }

    public Dictionary<string, ProxyPersistedState> LoadProxyStates()
    {
        return LoadState().ProxyStates;
    }

    public AppSettings LoadSettings()
    {
        return LoadState().Settings;
    }

    public void SaveState(IEnumerable<ProxyEntry> proxies, AppSettings settings)
    {
        var proxyStates = proxies.ToDictionary(
            proxy => proxy.StableId,
            proxy => new ProxyPersistedState
            {
                DisplayName = proxy.DisplayName,
                Used = proxy.Used,
                Copied = proxy.Copied,
                IsFavorite = proxy.IsFavorite,
                UseForEventSentry = proxy.UseForEventSentry,
                PingMs = proxy.PingMs,
                ResponseMs = proxy.ResponseMs,
                SpeedGroup = proxy.SpeedGroup,
                BrowserProvider = proxy.BrowserProvider,
                Email = proxy.Email,
                CountryCode = proxy.CountryCode,
                Region = proxy.Region,
                City = proxy.City
            },
            StringComparer.OrdinalIgnoreCase);

        var state = new AppState
        {
            ProxyStates = proxyStates,
            Settings = settings
        };
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state, AppJsonOptions.Indented));
    }

    public void SaveProxyStates(IEnumerable<ProxyEntry> proxies)
    {
        SaveState(proxies, LoadSettings());
    }

    private AppState LoadState()
    {
        try
        {
            var path = File.Exists(_statePath)
                ? _statePath
                : _legacyStatePath;
            if (!File.Exists(path))
            {
                return new AppState();
            }

            return JsonSerializer.Deserialize<AppState>(File.ReadAllText(path)) ?? new AppState();
        }
        catch
        {
            return new AppState();
        }
    }

    public sealed class AppSettings
    {
        public string? LastCsvPath { get; set; }
        public List<string> CsvPaths { get; set; } = [];
        public string? ActiveListKey { get; set; }
        public string SearchText { get; set; } = "";
        public string TargetUrl { get; set; } = "https://www.browserscan.net";
        public string? SyncMonitorId { get; set; }
        public string BrowserProvider { get; set; } = "chrome";
        public string JancyEndpointUrl { get; set; } = "http://127.0.0.1:43033";
        public bool AutoUpdateEnabled { get; set; }
        public string GitRepositoryPath { get; set; } = "";
        public string LastUpdatePreviousHead { get; set; } = "";
        public int SyncClickDelayMs { get; set; }
        public bool ShowChromeEmailButton { get; set; }
        public bool ShowEdgeEmailButton { get; set; }
        public bool ShowFirefoxEmailButton { get; set; }
        public List<double> ProxyTableColumnWidths { get; set; } = [];
        public List<BrowserExtensionEntry> GlobalExtensions { get; set; } = [];
    }

    public sealed class ProxyPersistedState
    {
        public string DisplayName { get; set; } = "";
        public bool Used { get; set; }
        public bool Copied { get; set; }
        public bool IsFavorite { get; set; }
        public bool UseForEventSentry { get; set; }
        public int? PingMs { get; set; }
        public int? ResponseMs { get; set; }
        public string SpeedGroup { get; set; } = "Unchecked";
        public string BrowserProvider { get; set; } = "chrome";
        public string Email { get; set; } = "";
        public string CountryCode { get; set; } = "";
        public string Region { get; set; } = "";
        public string City { get; set; } = "";
    }

    private sealed class AppState
    {
        public Dictionary<string, ProxyPersistedState> ProxyStates { get; set; } = [];
        public AppSettings Settings { get; set; } = new();
    }
}
