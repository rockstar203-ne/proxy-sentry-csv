using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using ProxySentryCsv.Models;
using ProxySentryCsv.Services;

namespace ProxySentryCsv;

public sealed partial class MainWindow
{
    private AdsPowerCompatibilityServer? _adsPowerApi;
    private bool _adsPowerApiEnabled;
    private int _adsPowerApiPort = AdsPowerCompatibilityServer.DefaultPort;
    private readonly SemaphoreSlim _adsPowerConfiguration = new(1, 1);
    private string AdsPowerApiStatus => _adsPowerApi?.IsRunning == true
        ? $"Running: http://127.0.0.1:{_adsPowerApi.Port}" : $"Stopped (port {_adsPowerApiPort})";

    private async Task ConfigureAdsPowerApiAsync(bool enabled, int? requestedPort = null)
    {
        await _adsPowerConfiguration.WaitAsync();
        var port = requestedPort ?? _adsPowerApiPort;
        try
        {
            if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(requestedPort), "API port must be between 1 and 65535.");
            if (enabled)
            {
                if (!_eventSentryBridge.TrySetCompatibilityMode(true))
                    throw new InvalidOperationException("Release Event Sentry bridge profiles before enabling AdsPower compatibility.");
                _adsPowerApi ??= new AdsPowerCompatibilityServer(ListAdsProfilesAsync, StartAdsProfileAsync, StopAdsProfileAsync);
                await _adsPowerApi.ChangePortAsync(port);
                _adsPowerApiEnabled = true;
                ShowMessage($"AdsPower-compatible API running at http://127.0.0.1:{port}. Selected Chrome profiles are available to local desktop clients.", InfoBarSeverity.Success);
            }
            else
            {
                if (_adsPowerApi is not null) await _adsPowerApi.StopAsync();
                _eventSentryBridge.TrySetCompatibilityMode(false);
                _adsPowerApiEnabled = false;
            }
            _adsPowerApiPort = port;
        }
        catch (Exception ex)
        {
            _adsPowerApiEnabled = _adsPowerApi?.IsRunning == true;
            if (_adsPowerApiEnabled) _adsPowerApiPort = _adsPowerApi!.Port;
            _eventSentryBridge.TrySetCompatibilityMode(_adsPowerApiEnabled);
            ShowMessage($"AdsPower API could not use port {port}. {AdsPowerApiStatus}. Browser windows were not closed. {ex.Message}", InfoBarSeverity.Error);
        }
        finally { _adsPowerConfiguration.Release(); }
    }

    private Task<IReadOnlyList<AdsProfile>> ListAdsProfilesAsync() => AdsOnUiAsync<IReadOnlyList<AdsProfile>>(() =>
        Task.FromResult<IReadOnlyList<AdsProfile>>(GetAllLoadedProxies()
            .Where(p => p.UseForEventSentry && NormalizeBrowserProvider(p.BrowserProvider) == "chrome")
            .DistinctBy(p => p.ProfileKey)
            .Select(p => new AdsProfile(p.ProfileKey, string.IsNullOrWhiteSpace(p.DisplayName) ? $"Profile {p.ListNumber}" : p.DisplayName,
                p.ListNumber, p.Host, p.Port, p.Scheme, _systemBrowserLauncher.GetRunningProcessId(p), p.IsBusy))
            .ToList()));

    private Task<AdsConnection> StartAdsProfileAsync(string id, CancellationToken token) => AdsOnUiAsync(async () =>
    {
        token.ThrowIfCancellationRequested();
        var proxy = GetAllLoadedProxies().FirstOrDefault(p => p.ProfileKey == id && p.UseForEventSentry && NormalizeBrowserProvider(p.BrowserProvider) == "chrome")
            ?? throw new InvalidOperationException("The selected Chrome profile is no longer available.");
        if (proxy.IsBusy || _systemBrowserLauncher.GetRunningProcessId(proxy) > 0)
            throw new InvalidOperationException("This profile is already open or busy.");
        proxy.IsBusy = true;
        proxy.Status = "AdsPower API opening";
        try
        {
            var lease = await _systemBrowserLauncher.EnsureNativeReadyForEventSentryAsync(proxy, "adspower-api", false,
                line => DispatcherQueue.TryEnqueue(() => ShowMessage(line, InfoBarSeverity.Informational)), token);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            using var response = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{lease.DebuggingPort}/json/version", token));
            var endpoint = response.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "ws" || !uri.IsLoopback || uri.Port != lease.DebuggingPort)
                throw new InvalidOperationException("Chrome returned an invalid local CDP endpoint.");
            proxy.IsOpen = true;
            proxy.OpenedProvider = "chrome";
            proxy.Used = true;
            proxy.Status = "AdsPower API running";
            SaveState(); UpdateSummary();
            return new AdsConnection(lease.ProcessId, lease.DebuggingPort, endpoint);
        }
        catch
        {
            // A failed API start must not leave an unowned browser behind.
            await _systemBrowserLauncher.CloseAsync(proxy);
            proxy.IsOpen = false;
            proxy.OpenedProvider = "";
            proxy.Status = "AdsPower API start failed";
            throw;
        }
        finally { proxy.IsBusy = false; }
    });

    private Task StopAdsProfileAsync(string id, int expectedProcess, CancellationToken token) => AdsOnUiAsync(async () =>
    {
        var proxy = GetAllLoadedProxies().FirstOrDefault(p => p.ProfileKey == id)
            ?? throw new InvalidOperationException("Profile is no longer loaded. Close its window in Proxy Sentry.");
        var actual = _systemBrowserLauncher.GetRunningProcessId(proxy);
        if (actual == 0) return true;
        if (actual != expectedProcess || proxy.IsBusy)
            throw new InvalidOperationException("The profile was reopened elsewhere and will not be closed by this API.");
        proxy.IsBusy = true;
        // Once removal starts, finish cleanup even if the API is being disabled.
        try { token.ThrowIfCancellationRequested(); await _systemBrowserLauncher.CloseAsync(proxy); return true; }
        finally { proxy.IsBusy = false; }
    });

    private Task<T> AdsOnUiAsync<T>(Func<Task<T>> action)
    {
        if (DispatcherQueue.HasThreadAccess) return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try { completion.TrySetResult(await action()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            })) completion.TrySetException(new InvalidOperationException("Proxy Sentry is closing."));
        return completion.Task;
    }
}
