using System.Collections.ObjectModel;
using ProxySentryCsv.Models;
using ProxySentryCsv.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ProxySentryCsv;

public sealed partial class MainWindow : Window
{
    private readonly string _appDataPath = GetAppDataPath();
    private readonly AppStateStore _stateStore;
    private readonly SystemBrowserLauncher _systemBrowserLauncher;
    private readonly JancyLauncher _jancyLauncher;
    private readonly ProxySpeedChecker _proxySpeedChecker = new();
    private AppWindow? _appWindow;
    private ProxyListEntry? _activeList;
    private ProxyListEntry? _favoritesList;
    private string _jancyEndpointUrl = "http://127.0.0.1:43033";
    private bool _autoUpdateEnabled;
    private string _gitRepositoryPath = "";
    private bool _suppressStateSave;
    private List<string> _lastJancySyncStableIds = [];

    public ObservableCollection<ProxyEntry> Proxies { get; } = [];
    public ObservableCollection<ProxyListEntry> ProxyLists { get; } = [];
    public ObservableCollection<SyncMonitorOption> SyncMonitors { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        _stateStore = new AppStateStore(_appDataPath);
        _systemBrowserLauncher = new SystemBrowserLauncher(_appDataPath);
        _jancyLauncher = new JancyLauncher(_appDataPath);
        _systemBrowserLauncher.ProfilesClosed += SystemBrowserLauncher_ProfilesClosed;
        InitializeWindowChrome();
        _suppressStateSave = true;
        InitializeSyncMonitors();
        RestoreSavedState();
        _suppressStateSave = false;
        Closed += MainWindow_Closed;
        _ = RunAutoUpdateOnStartupAsync();
    }

    private async void OpenCsv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".csv");

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
        {
            return;
        }

        foreach (var file in files)
        {
            AddCsvFile(file.Path);
        }
    }

    private async void OpenProxy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProxyEntry proxy)
        {
            return;
        }

        var targetUrl = TargetUrlBox.Text.Trim();
        if (proxy.IsOpen)
        {
            try
            {
                proxy.Status = "Closing";
                if (string.Equals(proxy.OpenedProvider, "jancy", StringComparison.OrdinalIgnoreCase))
                {
                    await _jancyLauncher.CloseAsync(proxy, _jancyEndpointUrl);
                    proxy.IsOpen = false;
                    proxy.OpenedProvider = "";
                    proxy.Status = "Closed";
                }
                else if (SystemBrowserLauncher.IsSystemBrowser(proxy.OpenedProvider))
                {
                    _systemBrowserLauncher.Close(proxy);
                    proxy.IsOpen = false;
                    proxy.OpenedProvider = "";
                    proxy.Status = "Closed";
                }
                else
                {
                    proxy.IsOpen = false;
                    proxy.OpenedProvider = "";
                    proxy.Status = "Closed";
                }
            }
            catch (Exception ex)
            {
                proxy.Status = "Close error";
                ShowMessage(ex.Message, InfoBarSeverity.Error);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            ShowMessage("Target URL is empty.", InfoBarSeverity.Warning);
            return;
        }

        proxy.Status = "Opening";

        try
        {
            var provider = GetSelectedBrowserProvider();
            if (string.Equals(provider, "jancy", StringComparison.OrdinalIgnoreCase))
            {
                _jancyLauncher.ExportProfiles(GetAllLoadedProxies(), targetUrl);
                await _jancyLauncher.LaunchAsync(proxy, targetUrl, _jancyEndpointUrl);
                proxy.OpenedProvider = "jancy";
            }
            else
            {
                await _systemBrowserLauncher.LaunchAsync(proxy, targetUrl, provider, status =>
                {
                    DispatcherQueue.TryEnqueue(() => proxy.Status = status);
                });
                proxy.OpenedProvider = provider;
            }

            proxy.Used = true;
            proxy.IsOpen = true;
            proxy.Status = provider == "jancy" ? "Jancy opened" : $"{GetBrowserDisplayName(provider)} opened";
            SaveState();
            UpdateSummary();
        }
        catch (Exception ex)
        {
            proxy.IsOpen = false;
            proxy.OpenedProvider = "";
            proxy.Status = "Error";
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OpenSyncGroup_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSyncSelectedProxies();
        if (selected.Count < 2)
        {
            ShowMessage("Select at least two rows for a sync group. The first selected row becomes master.", InfoBarSeverity.Warning);
            return;
        }

        var targetUrl = TargetUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            ShowMessage("Target URL is empty.", InfoBarSeverity.Warning);
            return;
        }

        OpenSyncGroupButton.IsEnabled = false;
        try
        {
            if (!IsJancySelected())
            {
                ShowMessage("Sync group is available only for Jancy controlled windows. Select Jancy as the browser provider.", InfoBarSeverity.Warning);
                return;
            }

            foreach (var proxy in selected)
            {
                proxy.Status = proxy == selected[0] ? "Sync master" : "Sync follower";
            }

            var monitor = SyncMonitorBox.SelectedItem as SyncMonitorOption;
            _jancyLauncher.ExportProfiles(GetAllLoadedProxies(), targetUrl);
            for (var index = 0; index < selected.Count; index++)
            {
                selected[index].Status = index == 0 ? "Jancy master" : "Jancy follower";
                await _jancyLauncher.LaunchAsync(selected[index], targetUrl, _jancyEndpointUrl, monitor, index, selected.Count);
                selected[index].OpenedProvider = "jancy";
                selected[index].IsOpen = true;
            }

            _lastJancySyncStableIds = selected.Select(proxy => proxy.StableId).ToList();

            foreach (var proxy in selected)
            {
                proxy.Used = true;
                proxy.IsOpen = true;
            }

            SaveState();
            UpdateSummary();
            ShowMessage($"Jancy group opened: master row {selected[0].RowNumber}, {selected.Count - 1} followers.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            foreach (var proxy in selected)
            {
                proxy.IsOpen = false;
                proxy.OpenedProvider = "";
                proxy.Status = "Error";
            }

            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            OpenSyncGroupButton.IsEnabled = true;
        }
    }

    private async void StartSync_Click(object sender, RoutedEventArgs e)
    {
        if (IsJancySelected())
        {
            var selected = GetJancySyncRows();
            if (selected.Count < 2)
            {
                ShowMessage("Select at least two opened Jancy rows, or use Open sync group first.", InfoBarSeverity.Warning);
                return;
            }

            try
            {
                StartSyncButton.IsEnabled = false;
                await _jancyLauncher.StartSyncAsync(selected, _jancyEndpointUrl);
                for (var index = 0; index < selected.Count; index++)
                {
                    selected[index].Status = index == 0 ? "Sync master" : "Sync follower";
                }
                _lastJancySyncStableIds = selected.Select(proxy => proxy.StableId).ToList();
                ShowMessage($"Jancy sync started: master row {selected[0].RowNumber}, {selected.Count - 1} followers.", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                StartSyncButton.IsEnabled = true;
            }

            return;
        }

        ShowMessage("Sync is available only for Jancy controlled windows.", InfoBarSeverity.Warning);
    }

    private async void StopSync_Click(object sender, RoutedEventArgs e)
    {
        if (IsJancySelected())
        {
            try
            {
                StopSyncButton.IsEnabled = false;
                await _jancyLauncher.StopSyncAsync(_jancyEndpointUrl);
                foreach (var proxy in GetJancySyncRows())
                {
                    proxy.Status = proxy.IsOpen ? "Jancy opened" : proxy.Status;
                }
                ShowMessage("Jancy sync stopped. Browser windows stay open.", InfoBarSeverity.Informational);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message, InfoBarSeverity.Warning);
            }
            finally
            {
                StopSyncButton.IsEnabled = true;
            }

            return;
        }

        ShowMessage("Sync is available only for Jancy controlled windows.", InfoBarSeverity.Warning);
    }

    private List<ProxyEntry> GetJancySyncRows()
    {
        var selected = GetSelectedProxies()
            .Where(proxy => proxy.IsOpen
                && string.Equals(proxy.OpenedProvider, "jancy", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (selected.Count >= 2)
        {
            return selected;
        }

        if (_lastJancySyncStableIds.Count < 2)
        {
            return selected;
        }

        var activeByStableId = Proxies
            .Where(proxy => proxy.IsOpen
                && string.Equals(proxy.OpenedProvider, "jancy", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(proxy => proxy.StableId, StringComparer.Ordinal);

        return _lastJancySyncStableIds
            .Select(stableId => activeByStableId.TryGetValue(stableId, out var proxy) ? proxy : null)
            .Where(proxy => proxy is not null)
            .Cast<ProxyEntry>()
            .ToList();
    }

    private void SyncMonitorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveState();
    }

    private void BrowserProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveState();
    }

    private void InstallJancyBridge_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var bridgePath = _jancyLauncher.InstallBridge();
            var profilePath = _jancyLauncher.ExportProfiles(GetAllLoadedProxies(), TargetUrlBox.Text.Trim());
            ShowMessage($"Jancy bridge installed: {bridgePath}. Profile CSV exported: {profilePath}. Restart Jancy or press Rescan JYL directory.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void SystemBrowserLauncher_ProfilesClosed(IReadOnlyList<string> stableIds)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var closed = stableIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var proxy in Proxies.Where(proxy => closed.Contains(proxy.StableId)))
            {
                proxy.IsOpen = false;
                if (proxy.Status is "Opened" or "Closing" or "Sync master" or "Sync follower"
                    || proxy.Status.EndsWith(" opened", StringComparison.OrdinalIgnoreCase))
                {
                    proxy.Status = "Closed";
                }
                proxy.OpenedProvider = "";
            }
        });
    }

    private void ProxyName_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveState();
    }

    private void ClearCookies_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProxyEntry proxy)
        {
            return;
        }

        try
        {
            var provider = string.IsNullOrWhiteSpace(proxy.OpenedProvider)
                ? GetSelectedBrowserProvider()
                : proxy.OpenedProvider;

            if (string.Equals(provider, "jancy", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Cookie clearing for Jancy profiles must be done inside Jancy. The Jancy provider owns its own cookie partitions.");
            }

            var deleted = _systemBrowserLauncher.ClearCookies(proxy, provider);
            proxy.Status = "Cookies cleared";
            ShowMessage(deleted == 0
                ? $"No cookie files found for row {proxy.RowNumber}."
                : $"Deleted {deleted} cookie file(s) for row {proxy.RowNumber}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            proxy.Status = "Cookie clear error";
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ProxyListsView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStateSave)
        {
            return;
        }

        _activeList = ProxyListsView.SelectedItem as ProxyListEntry;
        RefreshVisibleProxies();
        SaveState();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshVisibleProxies();
        SaveState();
    }

    private void CopyProxy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProxyEntry proxy)
        {
            return;
        }

        CopyRecords([proxy]);
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedProxies();
        if (selected.Count == 0)
        {
            ShowMessage("Select at least one proxy to copy.", InfoBarSeverity.Warning);
            return;
        }

        CopyRecords(selected);
    }

    private void CopyRecords(IReadOnlyList<ProxyEntry> records)
    {
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, records.Select(record => record.StandardProxy)));
        Clipboard.SetContent(package);

        foreach (var record in records)
        {
            record.Copied = true;
            record.Used = true;
            record.Status = "Copied";
        }

        SaveState();
        UpdateSummary();
    }

    private void FavoriteProxy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProxyEntry proxy)
        {
            return;
        }

        proxy.IsFavorite = !proxy.IsFavorite;
        RefreshFavoritesList();
        if (_activeList?.IsFavorites == true)
        {
            RefreshVisibleProxies();
        }

        SaveState();
        UpdateSummary();
    }

    private async void ExportTxt_Click(object sender, RoutedEventArgs e)
    {
        var records = GetSelectedOrVisibleProxies();
        if (records.Count == 0)
        {
            ShowMessage("No proxies to export.", InfoBarSeverity.Warning);
            return;
        }

        var path = await PickSavePathAsync("Export proxies", GetExportBaseName() + ".txt", ".txt", "Text file");
        if (path is null)
        {
            return;
        }

        await ProxyExporters.ExportPlainTxtAsync(path, records);
        ShowMessage($"Exported {records.Count} proxies to {Path.GetFileName(path)}.", InfoBarSeverity.Success);
    }

    private async void ExportAds_Click(object sender, RoutedEventArgs e)
    {
        var records = GetSelectedOrVisibleProxies();
        if (records.Count == 0)
        {
            ShowMessage("No proxies to export.", InfoBarSeverity.Warning);
            return;
        }

        var format = await AskAdsFormatAsync();
        if (format is null)
        {
            return;
        }

        var extension = format == "xlsx" ? ".xlsx" : ".txt";
        var path = await PickSavePathAsync("Export ADS", $"{GetExportBaseName()}-ads-{records.Count}{extension}", extension, format == "xlsx" ? "Excel workbook" : "Text file");
        if (path is null)
        {
            return;
        }

        var profileBase = GetExportBaseName();
        if (format == "xlsx")
        {
            await ProxyExporters.ExportAdsXlsxAsync(path, records, profileBase);
        }
        else
        {
            await ProxyExporters.ExportAdsTxtAsync(path, records, profileBase);
        }

        ShowMessage($"Exported {records.Count} ADS profiles to {Path.GetFileName(path)}.", InfoBarSeverity.Success);
    }

    private async void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        if (Proxies.Count == 0)
        {
            ShowMessage("Load a CSV before checking proxies.", InfoBarSeverity.Warning);
            return;
        }

        CheckAllButton.IsEnabled = false;
        SortSpeedButton.IsEnabled = false;
        OpenCsvButton.IsEnabled = false;

        try
        {
            var snapshot = Proxies.ToList();
            foreach (var proxy in snapshot)
            {
                proxy.Status = "Queued";
            }

            var completed = 0;
            var targetUri = Uri.TryCreate(TargetUrlBox.Text.Trim(), UriKind.Absolute, out var parsedTarget)
                ? parsedTarget
                : null;
            using var semaphore = new SemaphoreSlim(12);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));

            var tasks = snapshot.Select(async proxy =>
            {
                await semaphore.WaitAsync(timeout.Token);
                try
                {
                    await RunOnUiAsync(() => proxy.Status = "Checking");
                    var result = await _proxySpeedChecker.CheckAsync(proxy, targetUri, timeout.Token);
                    await RunOnUiAsync(() =>
                    {
                        proxy.PingMs = result.PingMs;
                        proxy.ResponseMs = result.ResponseMs;
                        proxy.SpeedGroup = result.SpeedGroup;
                        proxy.Status = result.Status;
                        completed++;
                        SummaryText.Text = $"Checked {completed}/{snapshot.Count}";
                    });
                }
                catch
                {
                    await RunOnUiAsync(() =>
                    {
                        proxy.SpeedGroup = "Dead";
                        proxy.Status = "Dead";
                        completed++;
                        SummaryText.Text = $"Checked {completed}/{snapshot.Count}";
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            SortProxiesBySpeed();
            SaveState();
            UpdateSummary();
            ShowMessage("Proxy check finished and grouped by speed.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            CheckAllButton.IsEnabled = true;
            SortSpeedButton.IsEnabled = true;
            OpenCsvButton.IsEnabled = true;
        }
    }

    private void SortSpeed_Click(object sender, RoutedEventArgs e)
    {
        SortProxiesBySpeed();
        SaveState();
        UpdateSummary();
    }

    private void ClearUsed_Click(object sender, RoutedEventArgs e)
    {
        foreach (var proxy in GetAllLoadedProxies())
        {
            proxy.Used = false;
            proxy.Copied = false;
            proxy.Status = "Ready";
        }

        SaveState();
        RefreshVisibleProxies();
        UpdateSummary();
        ShowMessage("Used marks cleared.", InfoBarSeverity.Informational);
    }

    private void AddCsvFile(string path, bool select = true)
    {
        try
        {
            var existing = ProxyLists.FirstOrDefault(list =>
                !list.IsFavorites && string.Equals(list.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (select)
                {
                    _activeList = existing;
                    ProxyListsView.SelectedItem = existing;
                    RefreshVisibleProxies();
                }

                ShowMessage($"CSV already added: {Path.GetFileName(path)}.", InfoBarSeverity.Informational);
                return;
            }

            var savedStates = _stateStore.LoadProxyStates();
            var proxies = CsvProxyLoader.Load(path);
            var list = new ProxyListEntry(path, Path.GetFileName(path), path);
            foreach (var proxy in proxies)
            {
                proxy.SourcePath = path;
                ApplySavedProxyState(proxy, savedStates);
                list.Items.Add(proxy);
            }

            ProxyLists.Add(list);
            list.NotifyCount();
            RefreshFavoritesList();

            if (select || _activeList is null)
            {
                _activeList = list;
                ProxyListsView.SelectedItem = list;
                RefreshVisibleProxies();
            }

            SaveState();
            UpdateSummary();
            ShowMessage($"Loaded {list.Count} proxies from {Path.GetFileName(path)}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private static void ApplySavedProxyState(ProxyEntry proxy, IReadOnlyDictionary<string, AppStateStore.ProxyPersistedState> savedStates)
    {
        if (!savedStates.TryGetValue(proxy.StableId, out var savedState))
        {
            return;
        }

        proxy.DisplayName = savedState.DisplayName;
        proxy.Used = savedState.Used;
        proxy.Copied = savedState.Copied;
        proxy.IsFavorite = savedState.IsFavorite;
        proxy.PingMs = savedState.PingMs;
        proxy.ResponseMs = savedState.ResponseMs;
        proxy.SpeedGroup = savedState.SpeedGroup;
    }

    private void SaveState()
    {
        if (_suppressStateSave)
        {
            return;
        }

        _stateStore.SaveState(
            GetAllLoadedProxies(),
            new AppStateStore.AppSettings
            {
                LastCsvPath = _activeList?.Path,
                CsvPaths = ProxyLists.Where(list => !list.IsFavorites && !string.IsNullOrWhiteSpace(list.Path)).Select(list => list.Path!).ToList(),
                ActiveListKey = _activeList?.Key,
                SearchText = SearchBox.Text.Trim(),
                TargetUrl = TargetUrlBox.Text.Trim(),
                SyncMonitorId = (SyncMonitorBox.SelectedItem as SyncMonitorOption)?.Id,
                BrowserProvider = GetSelectedBrowserProvider(),
                JancyEndpointUrl = _jancyEndpointUrl,
                AutoUpdateEnabled = _autoUpdateEnabled,
                GitRepositoryPath = _gitRepositoryPath
            });
    }

    private void RestoreSavedState()
    {
        var settings = _stateStore.LoadSettings();
        EnsureFavoritesList();
        SelectSyncMonitor(settings.SyncMonitorId);
        SelectBrowserProvider(settings.BrowserProvider);
        _jancyEndpointUrl = string.IsNullOrWhiteSpace(settings.JancyEndpointUrl)
            ? "http://127.0.0.1:43033"
            : settings.JancyEndpointUrl;
        _autoUpdateEnabled = settings.AutoUpdateEnabled;
        _gitRepositoryPath = string.IsNullOrWhiteSpace(settings.GitRepositoryPath)
            ? GitAutoUpdater.DetectRepositoryPath()
            : settings.GitRepositoryPath;
        SearchBox.Text = settings.SearchText;

        if (!string.IsNullOrWhiteSpace(settings.TargetUrl))
        {
            TargetUrlBox.Text = settings.TargetUrl;
        }

        var paths = settings.CsvPaths.Count > 0
            ? settings.CsvPaths
            : !string.IsNullOrWhiteSpace(settings.LastCsvPath)
                ? [settings.LastCsvPath]
                : [];

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                AddCsvFile(path, select: false);
            }
        }

        RefreshFavoritesList();
        _activeList = ProxyLists.FirstOrDefault(list => string.Equals(list.Key, settings.ActiveListKey, StringComparison.OrdinalIgnoreCase))
            ?? ProxyLists.FirstOrDefault(list => !list.IsFavorites)
            ?? _favoritesList;
        ProxyListsView.SelectedItem = _activeList;
        RefreshVisibleProxies();
        LogText.Text = "Ready";

        if (ProxyLists.Count > 1)
        {
            UpdateSummary();
        }
        else
        {
            UpdateSummary();
        }
    }

    private void InitializeSyncMonitors()
    {
        SyncMonitors.Clear();
        foreach (var monitor in SyncMonitorProvider.GetMonitors())
        {
            SyncMonitors.Add(monitor);
        }

        SyncMonitorBox.ItemsSource = SyncMonitors;
        SelectSyncMonitor(null);
    }

    private void SelectSyncMonitor(string? monitorId)
    {
        if (SyncMonitors.Count == 0)
        {
            return;
        }

        var selected = !string.IsNullOrWhiteSpace(monitorId)
            ? SyncMonitors.FirstOrDefault(monitor => string.Equals(monitor.Id, monitorId, StringComparison.OrdinalIgnoreCase))
            : null;

        selected ??= SyncMonitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? SyncMonitors[0];
        SyncMonitorBox.SelectedItem = selected;
    }

    private void SelectBrowserProvider(string? provider)
    {
        var normalized = provider?.Trim().ToLowerInvariant() switch
        {
            "chrome" => "chrome",
            "edge" => "edge",
            "firefox" => "firefox",
            _ => "jancy"
        };
        foreach (var item in BrowserProviderBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                BrowserProviderBox.SelectedItem = item;
                return;
            }
        }

        BrowserProviderBox.SelectedIndex = 0;
    }

    private string GetSelectedBrowserProvider()
    {
        return (BrowserProviderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()?.Trim().ToLowerInvariant() switch
        {
            "chrome" => "chrome",
            "edge" => "edge",
            "firefox" => "firefox",
            _ => "jancy"
        };
    }

    private bool IsJancySelected()
    {
        return string.Equals(GetSelectedBrowserProvider(), "jancy", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetBrowserDisplayName(string provider)
    {
        return provider switch
        {
            "chrome" => "Chrome",
            "edge" => "Edge",
            "firefox" => "Firefox",
            "jancy" => "Jancy",
            _ => "Browser"
        };
    }

    private void EnsureFavoritesList()
    {
        if (_favoritesList is not null)
        {
            return;
        }

        _favoritesList = new ProxyListEntry("__favorites__", "Favorites", isFavorites: true);
        ProxyLists.Insert(0, _favoritesList);
    }

    private void RefreshFavoritesList()
    {
        EnsureFavoritesList();
        _favoritesList!.Items.Clear();
        foreach (var proxy in GetAllLoadedProxies().Where(proxy => proxy.IsFavorite).DistinctBy(proxy => proxy.StableId))
        {
            _favoritesList.Items.Add(proxy);
        }

        _favoritesList.NotifyCount();
    }

    private void RefreshVisibleProxies()
    {
        var source = _activeList?.Items ?? Enumerable.Empty<ProxyEntry>();
        var search = SearchBox.Text.Trim();
        IEnumerable<ProxyEntry> filtered = source;
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(proxy =>
                proxy.Host.Contains(search, StringComparison.OrdinalIgnoreCase)
                || proxy.StandardProxy.Contains(search, StringComparison.OrdinalIgnoreCase)
                || proxy.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        Proxies.Clear();
        foreach (var proxy in filtered)
        {
            Proxies.Add(proxy);
        }

        UpdateSummary();
    }

    private IReadOnlyList<ProxyEntry> GetAllLoadedProxies()
    {
        return ProxyLists
            .Where(list => !list.IsFavorites)
            .SelectMany(list => list.Items)
            .DistinctBy(proxy => proxy.StableId)
            .ToList();
    }

    private List<ProxyEntry> GetSelectedProxies()
    {
        return ProxyList.SelectedItems.OfType<ProxyEntry>().ToList();
    }

    private List<ProxyEntry> GetSyncSelectedProxies()
    {
        return GetSelectedProxies();
    }

    private IReadOnlyList<ProxyEntry> GetSelectedOrVisibleProxies()
    {
        var selected = GetSelectedProxies();
        return selected.Count > 0 ? selected : Proxies.ToList();
    }

    private string GetExportBaseName()
    {
        if (_activeList is null)
        {
            return "proxies";
        }

        if (_activeList.IsFavorites)
        {
            return "favorites";
        }

        return string.IsNullOrWhiteSpace(_activeList.Path)
            ? "proxies"
            : Path.GetFileNameWithoutExtension(_activeList.Path);
    }

    private async Task<string?> PickSavePathAsync(string title, string suggestedName, string extension, string label)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName
        };
        picker.FileTypeChoices.Add(label, [extension]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private async Task<string?> AskAdsFormatAsync()
    {
        var xlsx = new RadioButton { Content = "Excel workbook (.xlsx)", IsChecked = true };
        var txt = new RadioButton { Content = "TXT template (.txt)" };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(xlsx);
        panel.Children.Add(txt);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Export ADS",
            Content = panel,
            PrimaryButtonText = "Export",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return txt.IsChecked == true ? "txt" : "xlsx";
    }

    private void UpdateSummary(string? fileName = null)
    {
        var used = GetAllLoadedProxies().Count(proxy => proxy.Used);
        var copied = GetAllLoadedProxies().Count(proxy => proxy.Copied);
        var favorites = GetAllLoadedProxies().Count(proxy => proxy.IsFavorite);
        var fast = Proxies.Count(proxy => proxy.SpeedGroup == "Fast");
        var good = Proxies.Count(proxy => proxy.SpeedGroup == "Good");
        var slow = Proxies.Count(proxy => proxy.SpeedGroup == "Slow");
        var dead = Proxies.Count(proxy => proxy.SpeedGroup == "Dead");
        var prefix = fileName ?? _activeList?.Name ?? "Loaded";
        SummaryText.Text = Proxies.Count == 0
            ? "No CSV loaded"
            : $"{prefix}: {Proxies.Count} shown, {used} used, {copied} copied, {favorites} favorites, {fast}/{good}/{slow}/{dead} fast/good/slow/dead";
    }

    private void SortProxiesBySpeed()
    {
        if (_activeList is null)
        {
            return;
        }

        var sorted = _activeList.Items
            .OrderBy(proxy => proxy.SpeedRank)
            .ThenBy(proxy => proxy.ResponseMs ?? int.MaxValue)
            .ThenBy(proxy => proxy.PingMs ?? int.MaxValue)
            .ThenBy(proxy => proxy.RowNumber)
            .ToList();

        _activeList.Items.Clear();
        foreach (var proxy in sorted)
        {
            _activeList.Items.Add(proxy);
        }

        RefreshVisibleProxies();
    }

    private Task RunOnUiAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetException(new InvalidOperationException("Could not enqueue UI update."));
        }

        return tcs.Task;
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        LogTitleText.Text = severity switch
        {
            InfoBarSeverity.Error => "Error",
            InfoBarSeverity.Warning => "Warning",
            InfoBarSeverity.Success => "Success",
            _ => "Log"
        };
        LogText.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogBorder.BorderBrush = severity switch
        {
            InfoBarSeverity.Error => new SolidColorBrush(ColorHelper.FromArgb(255, 220, 53, 69)),
            InfoBarSeverity.Warning => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 193, 7)),
            InfoBarSeverity.Success => new SolidColorBrush(ColorHelper.FromArgb(255, 40, 167, 69)),
            _ => new SolidColorBrush(ColorHelper.FromArgb(255, 108, 117, 125))
        };
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var endpointBox = new TextBox
        {
            Header = "Jancy Web Interface",
            Text = _jancyEndpointUrl,
            PlaceholderText = "http://127.0.0.1:43033"
        };
        var installBridgeButton = new Button { Content = "Install Jancy bridge" };
        var autoUpdateBox = new CheckBox
        {
            Content = "Auto-update on startup through git pull --ff-only",
            IsChecked = _autoUpdateEnabled
        };
        var repoPathBox = new TextBox
        {
            Header = "Git repository path",
            Text = string.IsNullOrWhiteSpace(_gitRepositoryPath) ? GitAutoUpdater.DetectRepositoryPath() : _gitRepositoryPath
        };
        var runUpdateButton = new Button { Content = "Run git update" };
        var statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 142, 150, 160))
        };

        installBridgeButton.Click += (_, _) =>
        {
            try
            {
                var bridgePath = _jancyLauncher.InstallBridge();
                var profilePath = _jancyLauncher.ExportProfiles(GetAllLoadedProxies(), TargetUrlBox.Text.Trim());
                statusText.Text = $"Installed: {bridgePath}\nProfiles: {profilePath}";
            }
            catch (Exception ex)
            {
                statusText.Text = ex.Message;
            }
        };

        runUpdateButton.Click += async (_, _) =>
        {
            try
            {
                runUpdateButton.IsEnabled = false;
                statusText.Text = "Running git update...";
                var output = await GitAutoUpdater.PullAsync(repoPathBox.Text.Trim());
                statusText.Text = string.IsNullOrWhiteSpace(output) ? "Git update completed." : output;
            }
            catch (Exception ex)
            {
                statusText.Text = ex.Message;
            }
            finally
            {
                runUpdateButton.IsEnabled = true;
            }
        };

        var panel = new StackPanel { Spacing = 12, Width = 620 };
        panel.Children.Add(endpointBox);
        panel.Children.Add(installBridgeButton);
        panel.Children.Add(autoUpdateBox);
        panel.Children.Add(repoPathBox);
        panel.Children.Add(runUpdateButton);
        panel.Children.Add(statusText);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Settings",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        _jancyEndpointUrl = string.IsNullOrWhiteSpace(endpointBox.Text)
            ? "http://127.0.0.1:43033"
            : endpointBox.Text.Trim();
        _autoUpdateEnabled = autoUpdateBox.IsChecked == true;
        _gitRepositoryPath = repoPathBox.Text.Trim();
        SaveState();
    }

    private async Task RunAutoUpdateOnStartupAsync()
    {
        if (!_autoUpdateEnabled || string.IsNullOrWhiteSpace(_gitRepositoryPath))
        {
            return;
        }

        try
        {
            var output = await GitAutoUpdater.PullAsync(_gitRepositoryPath);
            if (!string.IsNullOrWhiteSpace(output) && !output.Contains("Already up to date", StringComparison.OrdinalIgnoreCase))
            {
                DispatcherQueue.TryEnqueue(() => ShowMessage($"Git auto-update completed: {output}", InfoBarSeverity.Success));
            }
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => ShowMessage($"Git auto-update failed: {ex.Message}", InfoBarSeverity.Warning));
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        SaveState();
        _systemBrowserLauncher.Dispose();
    }

    private static string GetAppDataPath()
    {
        var basePath = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(basePath), "components", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Path.GetDirectoryName(basePath) ?? basePath, "Data");
        }

        return Path.Combine(basePath, "Data");
    }

    private void InitializeWindowChrome()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }

        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
        }

        ApplyTitleBarTheme();
    }

    private void ApplyTitleBarTheme()
    {
        if (_appWindow is null || !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = _appWindow.TitleBar;
        var theme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
        if (theme == ElementTheme.Dark)
        {
            titleBar.BackgroundColor = Color.FromArgb(255, 28, 34, 40);
            titleBar.ForegroundColor = Colors.White;
            titleBar.ButtonBackgroundColor = Color.FromArgb(255, 28, 34, 40);
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 44, 54, 64);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 18, 24, 30);
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }
        else
        {
            titleBar.BackgroundColor = Color.FromArgb(255, 246, 248, 250);
            titleBar.ForegroundColor = Color.FromArgb(255, 20, 28, 36);
            titleBar.ButtonBackgroundColor = Color.FromArgb(255, 246, 248, 250);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 20, 28, 36);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 229, 234, 240);
            titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 20, 28, 36);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 210, 218, 226);
            titleBar.ButtonPressedForegroundColor = Color.FromArgb(255, 20, 28, 36);
        }
    }
}
