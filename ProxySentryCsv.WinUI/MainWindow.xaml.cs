using System.Collections.ObjectModel;
using ProxySentryCsv.Models;
using ProxySentryCsv.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
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
    private readonly BrowserForgeFingerprintService _browserForgeFingerprints;
    private readonly ProxyGeoService _proxyGeoService;
    private readonly BrowserCacheService _browserCache;
    private readonly ProxySpeedChecker _proxySpeedChecker = new();
    private AppWindow? _appWindow;
    private ProxyListEntry? _activeList;
    private ProxyListEntry? _favoritesList;
    private string _jancyEndpointUrl = "http://127.0.0.1:43033";
    private bool _autoUpdateEnabled;
    private string _gitRepositoryPath = "";
    private string _defaultBrowserProvider = "chrome";
    private string _lastUpdatePreviousHead = "";
    private bool _suppressStateSave;
    private bool _syncingSelectionState;
    private List<string> _lastJancySyncStableIds = [];
    private List<string> _lastChromeSyncStableIds = [];
    private int _syncClickDelayMs;
    private bool _showChromeEmailButton;
    private bool _showEdgeEmailButton;
    private bool _showFirefoxEmailButton;

    public ObservableCollection<ProxyEntry> Proxies { get; } = [];
    public ObservableCollection<ProxyListEntry> ProxyLists { get; } = [];
    public ObservableCollection<SyncMonitorOption> SyncMonitors { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        _stateStore = new AppStateStore(_appDataPath);
        _browserForgeFingerprints = new BrowserForgeFingerprintService(_appDataPath);
        _proxyGeoService = new ProxyGeoService(_appDataPath);
        _browserCache = new BrowserCacheService(_appDataPath);
        _systemBrowserLauncher = new SystemBrowserLauncher(_appDataPath, _browserForgeFingerprints);
        _jancyLauncher = new JancyLauncher(_appDataPath);
        _systemBrowserLauncher.ProfilesClosed += SystemBrowserLauncher_ProfilesClosed;
        InitializeWindowChrome();
        _suppressStateSave = true;
        InitializeSyncMonitors();
        RestoreSavedState();
        _suppressStateSave = false;
        Closed += MainWindow_Closed;
        _ = RunAutoUpdateOnStartupAsync();
        _ = RefreshCacheSizeAsync();
    }

    private async void OpenCsv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".txt");

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
                await CloseProxyWindowAsync(proxy);
                SaveState();
                UpdateSummary();
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
            var provider = NormalizeBrowserProvider(proxy.BrowserProvider);
            proxy.BrowserProvider = provider;
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
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        proxy.Status = status;
                        ShowMessage(status, InfoBarSeverity.Informational);
                    });
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

    private async Task CloseProxyWindowAsync(ProxyEntry proxy)
    {
        proxy.Status = "Closing";
        if (string.Equals(proxy.OpenedProvider, "jancy", StringComparison.OrdinalIgnoreCase))
        {
            await _jancyLauncher.CloseAsync(proxy, _jancyEndpointUrl);
        }
        else if (SystemBrowserLauncher.IsSystemBrowser(proxy.OpenedProvider))
        {
            await _systemBrowserLauncher.CloseAsync(proxy);
        }

        proxy.IsOpen = false;
        proxy.OpenedProvider = "";
        proxy.Status = "Closed";
        ClearClosedProxySelection(proxy);
    }

    private async void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        var openRows = GetAllLoadedProxies()
            .Where(proxy => proxy.IsOpen)
            .ToList();
        if (openRows.Count == 0)
        {
            ShowMessage("No open browser windows.", InfoBarSeverity.Informational);
            return;
        }

        CloseAllButton.IsEnabled = false;
        try
        {
            _systemBrowserLauncher.StopChromeSync();
            _lastChromeSyncStableIds.Clear();
            _lastJancySyncStableIds.Clear();

            foreach (var proxy in openRows)
            {
                proxy.Status = "Closing";
            }

            var systemRows = openRows
                .Where(proxy => SystemBrowserLauncher.IsSystemBrowser(proxy.OpenedProvider))
                .ToList();
            if (systemRows.Count > 0)
            {
                await _systemBrowserLauncher.CloseAllAsync(systemRows);
            }

            var jancyRows = openRows
                .Where(proxy => string.Equals(proxy.OpenedProvider, "jancy", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var proxy in jancyRows)
            {
                try
                {
                    await _jancyLauncher.CloseAsync(proxy, _jancyEndpointUrl);
                }
                catch
                {
                    // Close All should keep closing the rest of the group even if Jancy misses one window.
                }
            }

            foreach (var proxy in openRows)
            {
                proxy.IsOpen = false;
                proxy.OpenedProvider = "";
                proxy.Status = "Closed";
                ClearClosedProxySelection(proxy);
            }

            SaveState();
            UpdateSummary();
            ShowMessage($"Closed {openRows.Count} browser window(s).", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            CloseAllButton.IsEnabled = true;
        }
    }

    private async void OpenSyncGroup_Click(object sender, RoutedEventArgs e)
    {
        var rawSelected = GetSyncSelectedProxies();
        var selected = BuildOpenSyncGroup(rawSelected);
        if (selected.Count < 2)
        {
            ShowMessage("Select at least two rows for a sync group, or select one row to add to the current group.", InfoBarSeverity.Warning);
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
            var providers = selected.Select(GetEffectiveBrowserProvider)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (providers.Count != 1)
            {
                ShowMessage("Select rows with the same Browser provider before opening a sync group.", InfoBarSeverity.Warning);
                return;
            }

            var provider = providers[0];
            if (string.Equals(provider, "jancy", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var proxy in selected)
                {
                    proxy.Status = proxy == selected[0] ? "Sync master" : "Sync follower";
                }

                var monitor = SyncMonitorBox.SelectedItem as SyncMonitorOption;
                _jancyLauncher.ExportProfiles(GetAllLoadedProxies(), targetUrl);
                for (var index = 0; index < selected.Count; index++)
                {
                    var proxy = selected[index];
                    proxy.Status = index == 0 ? "Jancy master" : "Jancy follower";
                    if (!proxy.IsOpen || !string.Equals(proxy.OpenedProvider, "jancy", StringComparison.OrdinalIgnoreCase))
                    {
                        await _jancyLauncher.LaunchAsync(proxy, targetUrl, _jancyEndpointUrl, monitor, index, selected.Count);
                        proxy.OpenedProvider = "jancy";
                        proxy.IsOpen = true;
                    }
                    proxy.Used = true;
                }

                if (selected.Any(proxy => proxy.IsOpen) && monitor is not null)
                {
                    await _jancyLauncher.ArrangeAsync(selected, _jancyEndpointUrl, monitor);
                }

                _lastJancySyncStableIds = selected.Select(proxy => proxy.StableId).ToList();
                ShowMessage($"Jancy group opened: master row {selected[0].RowNumber}, {selected.Count - 1} followers.", InfoBarSeverity.Success);
            }
            else if (IsChromiumSyncProvider(provider))
            {
                var monitor = SyncMonitorBox.SelectedItem as SyncMonitorOption;
                IReadOnlyList<SyncWindowPlacement> placements = monitor is null
                    ? Array.Empty<SyncWindowPlacement>()
                    : ChromeSyncController.CalculateWindowPlacements(selected.Count, monitor);
                for (var index = 0; index < selected.Count; index++)
                {
                    var proxy = selected[index];
                    proxy.Status = index == 0 ? "Chrome master opening" : "Chrome follower opening";
                    proxy.BrowserProvider = provider;
                    if (!proxy.IsOpen || !IsChromiumSyncProvider(proxy.OpenedProvider))
                    {
                        var placement = index < placements.Count ? placements[index] : (SyncWindowPlacement?)null;
                        await _systemBrowserLauncher.LaunchAsync(proxy, targetUrl, provider, status =>
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                proxy.Status = status;
                                ShowMessage(status, InfoBarSeverity.Informational);
                            });
                        }, placement);
                        proxy.OpenedProvider = provider;
                        proxy.IsOpen = true;
                    }
                    proxy.Used = true;
                }

                await _systemBrowserLauncher.ArrangeChromeAsync(selected, monitor);
                _lastChromeSyncStableIds = selected.Select(proxy => proxy.StableId).ToList();
                if (_systemBrowserLauncher.IsChromeSyncRunning)
                {
                    await _systemBrowserLauncher.StartChromeSyncAsync(selected, line =>
                    {
                        DispatcherQueue.TryEnqueue(() => ShowMessage(line, InfoBarSeverity.Informational));
                    });
                }
                ShowMessage($"{GetBrowserDisplayName(provider)} group opened and aligned: master row {selected[0].RowNumber}, {selected.Count - 1} followers.", InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage("Sync is available for Chrome rows. Edge/Firefox can be opened individually.", InfoBarSeverity.Warning);
                return;
            }

            foreach (var proxy in selected)
            {
                proxy.Used = true;
                proxy.IsOpen = true;
            }

            SaveState();
            UpdateSummary();
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
        var chromeSelected = GetChromeSyncRows();
        if (chromeSelected.Count >= 2)
        {
            try
            {
                StartSyncButton.IsEnabled = false;
                await _systemBrowserLauncher.StartChromeSyncAsync(chromeSelected, line =>
                {
                    DispatcherQueue.TryEnqueue(() => ShowMessage(line, InfoBarSeverity.Informational));
                });
                for (var index = 0; index < chromeSelected.Count; index++)
                {
                    chromeSelected[index].Status = index == 0 ? "Chrome sync MASTER" : "Chrome sync follower";
                }

                _lastChromeSyncStableIds = chromeSelected.Select(proxy => proxy.StableId).ToList();
                ShowMessage($"Chrome sync started: master row {chromeSelected[0].RowNumber}, {chromeSelected.Count - 1} followers.", InfoBarSeverity.Success);
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

        var selected = GetJancySyncRows();
        if (selected.Count < 2)
        {
            ShowMessage("Select at least two opened Chrome or Jancy rows, or use Open sync group first.", InfoBarSeverity.Warning);
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
    }

    private async void ApplyTargetUrl_Click(object sender, RoutedEventArgs e)
    {
        var targetUrl = TargetUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            ShowMessage("Target URL is empty.", InfoBarSeverity.Warning);
            return;
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out _))
        {
            ShowMessage("Target URL must be an absolute URL.", InfoBarSeverity.Warning);
            return;
        }

        var chromeSelected = GetChromeSyncRows();
        if (chromeSelected.Count == 0)
        {
            ShowMessage("Open or select Chrome windows before applying Target URL.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            ApplyTargetUrlButton.IsEnabled = false;
            var affectedCount = await _systemBrowserLauncher.NavigateChromeAsync(chromeSelected, targetUrl, line =>
            {
                DispatcherQueue.TryEnqueue(() => ShowMessage(line, InfoBarSeverity.Informational));
            });

            for (var index = 0; index < chromeSelected.Count; index++)
            {
                chromeSelected[index].Status = index == 0 ? "Chrome sync MASTER" : "Chrome sync follower";
            }

            _lastChromeSyncStableIds = chromeSelected.Select(proxy => proxy.StableId).ToList();
            SaveState();
            ShowMessage($"Applied Target URL to {affectedCount} Chrome window(s).", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            ApplyTargetUrlButton.IsEnabled = true;
        }
    }

    private async void StopSync_Click(object sender, RoutedEventArgs e)
    {
        var chromeSelected = GetChromeSyncRows();
        if (chromeSelected.Count >= 2)
        {
            try
            {
                StopSyncButton.IsEnabled = false;
                _systemBrowserLauncher.StopChromeSync();
                foreach (var proxy in chromeSelected)
                {
                    proxy.Status = proxy.IsOpen ? $"{GetBrowserDisplayName(proxy.OpenedProvider)} opened" : proxy.Status;
                }

                ShowMessage("Chrome sync stopped. Browser windows stay open.", InfoBarSeverity.Informational);
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
    }

    private List<ProxyEntry> GetJancySyncRows()
    {
        var rawSelected = GetSelectedProxies();
        var selected = rawSelected
            .Where(proxy => proxy.IsOpen
                && string.Equals(proxy.OpenedProvider, "jancy", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (rawSelected.Count > 0)
        {
            if (selected.Count == 1)
            {
                var lastGroup = ResolveLastOpenSyncGroup(_lastJancySyncStableIds, "jancy");
                if (lastGroup.Count >= 2 && lastGroup.Any(proxy => string.Equals(proxy.StableId, selected[0].StableId, StringComparison.Ordinal)))
                {
                    return lastGroup;
                }
            }

            return selected;
        }

        if (selected.Count >= 2)
        {
            return selected;
        }

        if (_lastJancySyncStableIds.Count < 2)
        {
            return selected;
        }

        var activeByStableId = GetAllLoadedProxies()
            .Where(proxy => proxy.IsOpen
                && string.Equals(proxy.OpenedProvider, "jancy", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(proxy => proxy.StableId, StringComparer.Ordinal);

        return _lastJancySyncStableIds
            .Select(stableId => activeByStableId.TryGetValue(stableId, out var proxy) ? proxy : null)
            .Where(proxy => proxy is not null)
            .Cast<ProxyEntry>()
            .ToList();
    }

    private List<ProxyEntry> GetChromeSyncRows()
    {
        var rawSelected = GetSelectedProxies();
        var selected = rawSelected
            .Where(proxy => proxy.IsOpen
                && IsChromiumSyncProvider(proxy.OpenedProvider))
            .ToList();

        if (rawSelected.Count > 0)
        {
            if (selected.Count == 1)
            {
                var lastGroup = ResolveLastOpenSyncGroup(_lastChromeSyncStableIds, "chrome");
                if (lastGroup.Count >= 2 && lastGroup.Any(proxy => string.Equals(proxy.StableId, selected[0].StableId, StringComparison.Ordinal)))
                {
                    return lastGroup;
                }
            }

            return selected;
        }

        if (selected.Count >= 2)
        {
            return selected;
        }

        if (_lastChromeSyncStableIds.Count < 2)
        {
            return selected;
        }

        var activeByStableId = GetAllLoadedProxies()
            .Where(proxy => proxy.IsOpen
                && IsChromiumSyncProvider(proxy.OpenedProvider))
            .ToDictionary(proxy => proxy.StableId, StringComparer.Ordinal);

        return _lastChromeSyncStableIds
            .Select(stableId => activeByStableId.TryGetValue(stableId, out var proxy) ? proxy : null)
            .Where(proxy => proxy is not null)
            .Cast<ProxyEntry>()
            .ToList();
    }

    private List<ProxyEntry> ResolveLastOpenSyncGroup(IReadOnlyList<string> stableIds, string provider)
    {
        if (stableIds.Count < 2)
        {
            return [];
        }

        var activeByStableId = GetAllLoadedProxies()
            .Where(proxy => proxy.IsOpen
                && string.Equals(GetEffectiveBrowserProvider(proxy), provider, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(proxy => proxy.StableId, StringComparer.Ordinal);
        return stableIds
            .Select(stableId => activeByStableId.TryGetValue(stableId, out var proxy) ? proxy : null)
            .Where(proxy => proxy is not null)
            .Cast<ProxyEntry>()
            .ToList();
    }

    private void SyncMonitorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveState();
    }

    private void SyncClickDelayBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSyncClickDelayFromSelection();
        if (!_suppressStateSave)
        {
            SaveState();
        }
    }

    private void ProxyBrowser_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveState();
    }

    private void ProxyListItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProxyListEntry list)
        {
            return;
        }

        var flyout = new MenuFlyout();
        if (list.IsFavorites)
        {
            var saveItem = new MenuFlyoutItem
            {
                Text = "Save as CSV list",
                IsEnabled = list.Items.Count > 0
            };
            saveItem.Click += async (_, _) => await SaveFavoritesListAsync();
            flyout.Items.Add(saveItem);
        }
        else
        {
            var deleteItem = new MenuFlyoutItem
            {
                Text = "Delete list",
                IsEnabled = list.IsRemovable
            };
            deleteItem.Click += async (_, _) => await RemoveProxyListAsync(list);
            flyout.Items.Add(deleteItem);
        }

        if (sender is FrameworkElement element)
        {
            flyout.ShowAt(element, e.GetPosition(element));
        }
    }

    private async Task SaveFavoritesListAsync()
    {
        RefreshFavoritesList();
        var records = _favoritesList?.Items.ToList() ?? [];
        if (records.Count == 0)
        {
            ShowMessage("Favorites is empty.", InfoBarSeverity.Warning);
            return;
        }

        var path = await PickSavePathAsync("Save Favorites", $"favorites-{records.Count}.csv", ".csv", "CSV file");
        if (path is null)
        {
            return;
        }

        await ProxyExporters.ExportProxyCsvAsync(path, records);
        AddCsvFile(path, select: true);
        ShowMessage($"Saved Favorites as {Path.GetFileName(path)} and added it as a CSV list.", InfoBarSeverity.Success);
    }

    private async Task RemoveProxyListAsync(ProxyListEntry? list)
    {
        if (list is null)
        {
            ShowMessage("Select a proxy list to remove.", InfoBarSeverity.Warning);
            return;
        }

        if (list.IsFavorites)
        {
            ShowMessage("Favorites is a virtual list and cannot be removed.", InfoBarSeverity.Warning);
            return;
        }

        var openRows = list.Items.Where(proxy => proxy.IsOpen).ToList();
        foreach (var proxy in openRows)
        {
            await CloseProxyWindowAsync(proxy);
        }

        ProxyLists.Remove(list);
        RefreshFavoritesList();
        _activeList = ProxyLists.FirstOrDefault(proxyList => !proxyList.IsFavorites) ?? _favoritesList;
        ProxyListsView.SelectedItem = _activeList;
        RefreshVisibleProxies();
        SaveState();
        UpdateSummary();
        ShowMessage(openRows.Count == 0
            ? $"Removed list {list.Name}."
            : $"Removed list {list.Name} and closed {openRows.Count} browser window(s).", InfoBarSeverity.Success);
    }

    private async void AlignSelected_Click(object sender, RoutedEventArgs e)
    {
        var chromeSelected = GetChromeSyncRows();
        if (chromeSelected.Count >= 2)
        {
            try
            {
                AlignSelectedButton.IsEnabled = false;
                var monitor = SyncMonitorBox.SelectedItem as SyncMonitorOption;
                await _systemBrowserLauncher.ArrangeChromeAsync(chromeSelected, monitor);
                _lastChromeSyncStableIds = chromeSelected.Select(proxy => proxy.StableId).ToList();
                for (var index = 0; index < chromeSelected.Count; index++)
                {
                    chromeSelected[index].Status = index == 0 ? "Chrome aligned master" : "Chrome aligned follower";
                }

                ShowMessage($"Aligned {chromeSelected.Count} Chrome windows.", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                AlignSelectedButton.IsEnabled = true;
            }

            return;
        }

        var selected = GetJancySyncRows();
        if (selected.Count < 2)
        {
            ShowMessage("Select at least two opened Jancy rows to align.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            AlignSelectedButton.IsEnabled = false;
            var monitor = SyncMonitorBox.SelectedItem as SyncMonitorOption;
            await _jancyLauncher.ArrangeAsync(selected, _jancyEndpointUrl, monitor);
            _lastJancySyncStableIds = selected.Select(proxy => proxy.StableId).ToList();
            for (var index = 0; index < selected.Count; index++)
            {
                selected[index].Status = index == 0 ? "Aligned master" : "Aligned follower";
            }
            ShowMessage($"Aligned {selected.Count} Jancy windows.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AlignSelectedButton.IsEnabled = true;
        }
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

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var text = """
Proxy Sentry CSV is a proxy launcher for CSV proxy lists.

Main features:
- open isolated Chrome, Edge, Firefox, or Jancy browser profiles through authenticated proxies;
- keep proxy state, names, used/copied/favorite marks, cookies and profile data per proxy;
- check proxy ping and response speed;
- export proxies to TXT and ADS-compatible formats;
- synchronize Chrome or Jancy windows.

Jancy setup:
1. Install and open Jancy.
2. Enable Jancy Web Interface and copy its local URL/port.
3. In Proxy Sentry CSV open Settings.
4. Set Jancy Web Interface URL.
5. Press Install Jancy bridge.
6. In Jancy open Settings -> JYL Scripts and press Rescan JYL directory, or restart Jancy.
7. Select Jancy in the Browser column and press Open or Open sync group.
""";

        var aboutText = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.WrapWholeWords,
            IsTextSelectionEnabled = true,
            Width = 430
        };

        var content = new ScrollViewer
        {
            Width = 450,
            MaxHeight = 360,
            Padding = new Thickness(0, 0, 8, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = aboutText
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "About Proxy Sentry CSV",
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private void SystemBrowserLauncher_ProfilesClosed(IReadOnlyList<string> stableIds)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var closed = stableIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var proxy in GetAllLoadedProxies().Where(proxy => closed.Contains(proxy.StableId)))
            {
                proxy.IsOpen = false;
                if (proxy.Status is "Opened" or "Closing" or "Sync master" or "Sync follower"
                    || proxy.Status.EndsWith(" opened", StringComparison.OrdinalIgnoreCase))
                {
                    proxy.Status = "Closed";
                }
                proxy.OpenedProvider = "";
                ClearClosedProxySelection(proxy);
            }
        });
    }

    private void ProxyName_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveState();
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
            ShowMessage("Load a proxy list before checking proxies.", InfoBarSeverity.Warning);
            return;
        }

        CheckAllButton.IsEnabled = false;
        SortButton.IsEnabled = false;
        OpenCsvButton.IsEnabled = false;
        var checkAllContent = CheckAllButton.Content;
        CheckAllButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new ProgressRing
                {
                    IsActive = true,
                    Width = 16,
                    Height = 16
                },
                new TextBlock
                {
                    Text = "Checking...",
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

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
                try
                {
                    var geo = await _proxyGeoService.GetAsync(proxy, cancellationToken: timeout.Token);
                    await RunOnUiAsync(() => ApplyProxyGeo(proxy, geo));
                }
                catch
                {
                    // Region is supplemental; proxy speed status should not depend on geo lookup.
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
            CheckAllButton.Content = checkAllContent;
            CheckAllButton.IsEnabled = true;
            SortButton.IsEnabled = true;
            OpenCsvButton.IsEnabled = true;
        }
    }

    private void SortBySpeed_Click(object sender, RoutedEventArgs e)
    {
        SortProxiesBySpeed();
        SaveState();
        UpdateSummary();
        ShowMessage("Sorted by speed.", InfoBarSeverity.Informational);
    }

    private void SortByDocumentOrder_Click(object sender, RoutedEventArgs e)
    {
        SortProxiesByDocumentOrder();
        SaveState();
        UpdateSummary();
        ShowMessage("Sorted by document order.", InfoBarSeverity.Informational);
    }

    private async void Cache_Click(object sender, RoutedEventArgs e)
    {
        CacheButton.IsEnabled = false;
        try
        {
            var size = await _browserCache.GetTotalCacheSizeAsync();
            CacheButton.Content = $"Cache: {BrowserCacheService.FormatBytes(size)}";

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Browser cache",
                Content = $"System browser cache: {BrowserCacheService.FormatBytes(size)}{Environment.NewLine}{Environment.NewLine}Only Chrome, Edge, and Firefox cache folders will be cleared. Cookies, sessions, fingerprints, proxy state, and Jancy profiles will not be touched.",
                PrimaryButtonText = size > 0 ? "Clear cache" : "",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary || size <= 0)
            {
                return;
            }

            CacheButton.Content = "Cache: clearing...";
            ShowMessage("Clearing system browser cache. Close browser windows first if files are locked.", InfoBarSeverity.Informational);
            var cleanup = await _browserCache.ClearAllCacheAsync();
            await RefreshCacheSizeAsync();

            var message = cleanup.FailedItems == 0
                ? $"Cleared {BrowserCacheService.FormatBytes(cleanup.BytesBefore)} cache."
                : $"Cleared {BrowserCacheService.FormatBytes(cleanup.BytesBefore)} cache. Skipped locked items: {cleanup.FailedItems}.";
            ShowMessage(message, cleanup.FailedItems == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            CacheButton.IsEnabled = true;
        }
    }

    private async void ClearProxyCache_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProxyEntry proxy)
        {
            return;
        }

        if (proxy.IsOpen && SystemBrowserLauncher.IsSystemBrowser(proxy.OpenedProvider))
        {
            ShowMessage("Close this browser window before clearing its cache.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            proxy.Status = "Clearing cache";
            var cleanup = await _browserCache.ClearProxyCacheAsync(proxy);
            await RefreshCacheSizeAsync();

            if (cleanup.BytesBefore == 0)
            {
                proxy.Status = "No cache";
                ShowMessage($"No Chrome/Edge/Firefox cache found for {proxy.ProxyDisplay}. Jancy is not touched.", InfoBarSeverity.Informational);
                return;
            }

            proxy.Status = cleanup.FailedItems == 0 ? "Cache cleared" : "Cache partial";
            var message = cleanup.FailedItems == 0
                ? $"Cleared {BrowserCacheService.FormatBytes(cleanup.BytesBefore)} cache for {proxy.ProxyDisplay}."
                : $"Cleared {BrowserCacheService.FormatBytes(cleanup.BytesBefore)} cache for {proxy.ProxyDisplay}. Skipped locked items: {cleanup.FailedItems}.";
            ShowMessage(message, cleanup.FailedItems == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            proxy.Status = "Cache error";
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
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

                ShowMessage($"Proxy list already added: {Path.GetFileName(path)}.", InfoBarSeverity.Informational);
                return;
            }

            var savedStates = _stateStore.LoadProxyStates();
            var proxies = CsvProxyLoader.Load(path);
            var list = new ProxyListEntry(path, Path.GetFileName(path), path);
            foreach (var proxy in proxies)
            {
                proxy.SourcePath = path;
                if (!proxy.BrowserProviderFromCsv)
                {
                    proxy.BrowserProvider = _defaultBrowserProvider;
                }
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
        var savedProvider = NormalizeBrowserProvider(savedState.BrowserProvider);
        proxy.BrowserProvider = savedProvider;
        if (string.IsNullOrWhiteSpace(proxy.Email) && !string.IsNullOrWhiteSpace(savedState.Email))
        {
            proxy.Email = savedState.Email;
        }
        proxy.CountryCode = savedState.CountryCode;
        proxy.Region = savedState.Region;
        proxy.City = savedState.City;
    }

    private static void ApplyProxyGeo(ProxyEntry proxy, ProxyGeoInfo geo)
    {
        proxy.CountryCode = geo.CountryCode;
        proxy.Region = geo.Region;
        proxy.City = geo.City;
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
                BrowserProvider = _defaultBrowserProvider,
                JancyEndpointUrl = _jancyEndpointUrl,
                AutoUpdateEnabled = _autoUpdateEnabled,
                GitRepositoryPath = _gitRepositoryPath,
                LastUpdatePreviousHead = _lastUpdatePreviousHead,
                SyncClickDelayMs = _syncClickDelayMs,
                ShowChromeEmailButton = _showChromeEmailButton,
                ShowEdgeEmailButton = _showEdgeEmailButton,
                ShowFirefoxEmailButton = _showFirefoxEmailButton
            });
    }

    private void RestoreSavedState()
    {
        var settings = _stateStore.LoadSettings();
        EnsureFavoritesList();
        SelectSyncMonitor(settings.SyncMonitorId);
        _defaultBrowserProvider = NormalizeBrowserProvider(settings.BrowserProvider);
        _jancyEndpointUrl = string.IsNullOrWhiteSpace(settings.JancyEndpointUrl)
            ? "http://127.0.0.1:43033"
            : settings.JancyEndpointUrl;
        _autoUpdateEnabled = settings.AutoUpdateEnabled;
        SelectSyncClickDelay(settings.SyncClickDelayMs);
        UpdateSyncClickDelayFromSelection();
        _showChromeEmailButton = settings.ShowChromeEmailButton;
        _showEdgeEmailButton = settings.ShowEdgeEmailButton;
        _showFirefoxEmailButton = settings.ShowFirefoxEmailButton;
        ApplyBrowserEmailButtonSettings();
        _gitRepositoryPath = string.IsNullOrWhiteSpace(settings.GitRepositoryPath)
            ? GitAutoUpdater.DetectRepositoryPath()
            : settings.GitRepositoryPath;
        _lastUpdatePreviousHead = settings.LastUpdatePreviousHead;
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

    private static string NormalizeBrowserProvider(string? provider)
    {
        return provider?.Trim().ToLowerInvariant() switch
        {
            "portable-chromium" or "portable chromium" or "chromium" or "portable" => "chrome",
            "chrome" => "chrome",
            "edge" => "edge",
            "firefox" => "firefox",
            _ => "jancy"
        };
    }

    private static bool IsJancyProvider(string? provider)
    {
        return string.Equals(NormalizeBrowserProvider(provider), "jancy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChromiumSyncProvider(string? provider)
    {
        return NormalizeBrowserProvider(provider) is "chrome";
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
                || proxy.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || proxy.RegionText.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var visible = filtered.ToList();
        Proxies.Clear();
        for (var index = 0; index < visible.Count; index++)
        {
            var proxy = visible[index];
            proxy.ListNumber = index + 1;
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

    private void ProxyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelectionState)
        {
            foreach (var proxy in e.AddedItems.OfType<ProxyEntry>())
            {
                proxy.SyncSelected = true;
            }

            foreach (var proxy in e.RemovedItems.OfType<ProxyEntry>())
            {
                proxy.SyncSelected = false;
            }
        }

        ShowSelectionSummary();
    }

    private void SyncSelect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProxyEntry proxy)
        {
            var shouldSelect = !proxy.SyncSelected;
            proxy.SyncSelected = shouldSelect;

            _syncingSelectionState = true;
            try
            {
                if (shouldSelect)
                {
                    if (!ProxyList.SelectedItems.Contains(proxy))
                    {
                        ProxyList.SelectedItems.Add(proxy);
                    }
                }
                else if (ProxyList.SelectedItems.Contains(proxy))
                {
                    ProxyList.SelectedItems.Remove(proxy);
                }
            }
            finally
            {
                _syncingSelectionState = false;
            }
        }

        ShowSelectionSummary();
    }

    private List<ProxyEntry> GetSyncSelectedProxies()
    {
        var checkedRows = GetAllLoadedProxies().Where(proxy => proxy.SyncSelected).ToList();
        return checkedRows.Count > 0 ? checkedRows : GetSelectedProxies();
    }

    private void ShowSelectionSummary()
    {
        var selected = GetSelectedProxies();
        var syncSelected = GetAllLoadedProxies().Where(proxy => proxy.SyncSelected).ToList();
        var rows = syncSelected.Count > 0
            ? string.Join(", ", syncSelected.Take(8).Select(proxy => proxy.RowNumber))
            : selected.Count > 0
                ? string.Join(", ", selected.Take(8).Select(proxy => proxy.RowNumber))
                : "";
        if ((syncSelected.Count > 0 ? syncSelected.Count : selected.Count) > 8)
        {
            rows += ", ...";
        }

        var suffix = string.IsNullOrWhiteSpace(rows) ? "" : $". Rows: {rows}";
        ShowMessage($"Selected proxies: {selected.Count}. Sync selected: {syncSelected.Count}{suffix}", InfoBarSeverity.Informational);
    }

    private void ClearClosedProxySelection(ProxyEntry proxy)
    {
        proxy.SyncSelected = false;
        if (ProxyList.SelectedItems.Contains(proxy))
        {
            ProxyList.SelectedItems.Remove(proxy);
        }

        _lastChromeSyncStableIds.RemoveAll(stableId => string.Equals(stableId, proxy.StableId, StringComparison.OrdinalIgnoreCase));
        _lastJancySyncStableIds.RemoveAll(stableId => string.Equals(stableId, proxy.StableId, StringComparison.OrdinalIgnoreCase));
    }

    private List<ProxyEntry> BuildOpenSyncGroup(IReadOnlyList<ProxyEntry> selected)
    {
        if (selected.Count != 1)
        {
            return selected.ToList();
        }

        var target = selected[0];
        var provider = GetEffectiveBrowserProvider(target);
        IReadOnlyList<string> lastIds = string.Equals(provider, "jancy", StringComparison.OrdinalIgnoreCase)
            ? _lastJancySyncStableIds
            : IsChromiumSyncProvider(provider)
                ? _lastChromeSyncStableIds
                : Array.Empty<string>();
        if (lastIds.Count < 2)
        {
            return selected.ToList();
        }

        var openByStableId = GetAllLoadedProxies()
            .Where(proxy => proxy.IsOpen
                && string.Equals(GetEffectiveBrowserProvider(proxy), provider, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(proxy => proxy.StableId, StringComparer.Ordinal);
        var group = lastIds
            .Select(stableId => openByStableId.TryGetValue(stableId, out var proxy) ? proxy : null)
            .Where(proxy => proxy is not null)
            .Cast<ProxyEntry>()
            .ToList();
        if (group.Count < 2)
        {
            return selected.ToList();
        }

        group.Add(target);
        return group
            .DistinctBy(proxy => proxy.StableId)
            .ToList();
    }

    private static string GetEffectiveBrowserProvider(ProxyEntry proxy)
    {
        return NormalizeBrowserProvider(!string.IsNullOrWhiteSpace(proxy.OpenedProvider)
            ? proxy.OpenedProvider
            : proxy.BrowserProvider);
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
            ? "No list loaded"
            : $"{prefix}: {Proxies.Count} shown, {used} used, {copied} copied, {favorites} favorites, {fast}/{good}/{slow}/{dead} fast/good/slow/dead";
    }

    private async Task RefreshCacheSizeAsync()
    {
        try
        {
            CacheButton.Content = "Cache: ...";
            var size = await _browserCache.GetTotalCacheSizeAsync();
            CacheButton.Content = $"Cache: {BrowserCacheService.FormatBytes(size)}";
        }
        catch
        {
            CacheButton.Content = "Cache: ?";
        }
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

    private void SortProxiesByDocumentOrder()
    {
        if (_activeList is null)
        {
            return;
        }

        var sorted = _activeList.Items
            .OrderBy(proxy => proxy.RowNumber)
            .ThenBy(proxy => proxy.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(proxy => proxy.Port)
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

    private void AppendUpdateLog(TextBox target, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        target.Text = string.IsNullOrWhiteSpace(target.Text) || string.Equals(target.Text, "Ready", StringComparison.OrdinalIgnoreCase)
            ? line
            : $"{target.Text}{Environment.NewLine}{line}";
        target.SelectionStart = target.Text.Length;
        LogTitleText.Text = "Update";
        LogText.Text = line;
        LogBorder.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 13, 110, 253));
    }

    private static void SelectComboBoxValue(ComboBox comboBox, string provider)
    {
        var normalized = NormalizeBrowserProvider(provider);
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private void SelectSyncClickDelay(int delayMs)
    {
        var normalized = delayMs switch
        {
            >= 1900 => 2000,
            >= 900 => 1000,
            >= 400 => 500,
            _ => 0
        };

        foreach (var item in SyncClickDelayBox.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse(item.Tag?.ToString(), out var tagDelay) && tagDelay == normalized)
            {
                SyncClickDelayBox.SelectedItem = item;
                return;
            }
        }

        SyncClickDelayBox.SelectedIndex = 0;
    }

    private void UpdateSyncClickDelayFromSelection()
    {
        var selected = SyncClickDelayBox.SelectedItem as ComboBoxItem;
        _syncClickDelayMs = int.TryParse(selected?.Tag?.ToString(), out var delayMs) ? delayMs : 0;
        _systemBrowserLauncher.SetChromeCtrlClickDelay(TimeSpan.FromMilliseconds(_syncClickDelayMs));
    }

    private void ApplyBrowserEmailButtonSettings()
    {
        _systemBrowserLauncher.SetProfileEmailButtonOptions(
            _showChromeEmailButton,
            _showEdgeEmailButton,
            _showFirefoxEmailButton);
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var endpointBox = new TextBox
        {
            Header = "Jancy Web Interface",
            Text = _jancyEndpointUrl,
            PlaceholderText = "http://127.0.0.1:43033",
            Width = 430
        };
        var installBridgeButton = new Button { Content = "Install Jancy bridge" };
        var autoUpdateBox = new CheckBox
        {
            Content = "Auto-update on startup through git pull --ff-only",
            IsChecked = _autoUpdateEnabled,
            Width = 430
        };
        var defaultBrowserBox = new ComboBox
        {
            Header = "Default browser for new proxies",
            SelectedValuePath = "Tag",
            Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        defaultBrowserBox.Items.Add(new ComboBoxItem { Content = "Chrome (recommended)", Tag = "chrome" });
        defaultBrowserBox.Items.Add(new ComboBoxItem { Content = "Jancy", Tag = "jancy" });
        defaultBrowserBox.Items.Add(new ComboBoxItem { Content = "Edge", Tag = "edge" });
        defaultBrowserBox.Items.Add(new ComboBoxItem { Content = "Firefox", Tag = "firefox" });
        SelectComboBoxValue(defaultBrowserBox, _defaultBrowserProvider);
        var emailButtonHeader = new TextBlock
        {
            Text = "Browser email button",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        var chromeEmailButtonBox = new CheckBox
        {
            Content = "Chrome",
            IsChecked = _showChromeEmailButton
        };
        var edgeEmailButtonBox = new CheckBox
        {
            Content = "Edge",
            IsChecked = _showEdgeEmailButton
        };
        var firefoxEmailButtonBox = new CheckBox
        {
            Content = "Firefox signed extension",
            IsChecked = _showFirefoxEmailButton
        };
        var emailButtonHint = new TextBlock
        {
            Text = "Chrome and Edge use a toolbar button. Firefox requires a Mozilla-signed toolbar extension. No page overlay or site DOM injection is added for this email control.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.82
        };
        var emailButtonPanel = new StackPanel
        {
            Spacing = 4
        };
        emailButtonPanel.Children.Add(emailButtonHeader);
        emailButtonPanel.Children.Add(chromeEmailButtonBox);
        emailButtonPanel.Children.Add(edgeEmailButtonBox);
        emailButtonPanel.Children.Add(firefoxEmailButtonBox);
        emailButtonPanel.Children.Add(emailButtonHint);
        var runUpdateButton = new Button { Content = "Run git update" };
        var rollbackButton = new Button { Content = "Rollback previous update" };
        var statusText = new TextBox
        {
            Header = "Update log",
            IsReadOnly = true,
            AcceptsReturn = true,
            Width = 430,
            Height = 110,
            TextWrapping = TextWrapping.Wrap,
            Text = string.IsNullOrWhiteSpace(_lastUpdatePreviousHead)
                ? "Ready"
                : $"Ready. Rollback target: {_lastUpdatePreviousHead[..Math.Min(7, _lastUpdatePreviousHead.Length)]}"
        };

        installBridgeButton.Click += (_, _) =>
        {
            try
            {
                var bridgePath = _jancyLauncher.InstallBridge();
                var profilePath = _jancyLauncher.ExportProfiles(GetAllLoadedProxies(), TargetUrlBox.Text.Trim());
                AppendUpdateLog(statusText, $"Installed Jancy bridge: {bridgePath}");
                AppendUpdateLog(statusText, $"Exported Jancy profiles: {profilePath}");
            }
            catch (Exception ex)
            {
                AppendUpdateLog(statusText, ex.Message);
            }
        };

        runUpdateButton.Click += async (_, _) =>
        {
            try
            {
                runUpdateButton.IsEnabled = false;
                SaveState();
                statusText.Text = "";
                AppendUpdateLog(statusText, "Starting git update.");
                var result = await GitAutoUpdater.UpdateAsync(GetConfiguredRepositoryPath(), line =>
                {
                    DispatcherQueue.TryEnqueue(() => AppendUpdateLog(statusText, line));
                });
                if (!string.Equals(result.PreviousHead, result.CurrentHead, StringComparison.OrdinalIgnoreCase))
                {
                    _lastUpdatePreviousHead = result.PreviousHead;
                    SaveState();
                    AppendUpdateLog(statusText, $"Rollback saved: {_lastUpdatePreviousHead[..Math.Min(7, _lastUpdatePreviousHead.Length)]}");
                }
                AppendUpdateLog(statusText, "Git update completed. Restart Proxy Sentry CSV to run updated binaries.");
            }
            catch (Exception ex)
            {
                AppendUpdateLog(statusText, ex.Message);
            }
            finally
            {
                runUpdateButton.IsEnabled = true;
            }
        };

        rollbackButton.Click += async (_, _) =>
        {
            try
            {
                rollbackButton.IsEnabled = false;
                SaveState();
                AppendUpdateLog(statusText, "Starting rollback.");
                var result = await GitAutoUpdater.RollbackAsync(GetConfiguredRepositoryPath(), _lastUpdatePreviousHead, line =>
                {
                    DispatcherQueue.TryEnqueue(() => AppendUpdateLog(statusText, line));
                });
                _lastUpdatePreviousHead = result.PreviousHead;
                SaveState();
                AppendUpdateLog(statusText, "Rollback completed. Restart Proxy Sentry CSV to run restored binaries.");
            }
            catch (Exception ex)
            {
                AppendUpdateLog(statusText, ex.Message);
            }
            finally
            {
                rollbackButton.IsEnabled = true;
            }
        };

        var updateButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        updateButtons.Children.Add(runUpdateButton);
        updateButtons.Children.Add(rollbackButton);

        var panel = new StackPanel
        {
            Spacing = 10,
            Width = 430
        };
        panel.Children.Add(endpointBox);
        panel.Children.Add(installBridgeButton);
        panel.Children.Add(defaultBrowserBox);
        panel.Children.Add(emailButtonPanel);
        panel.Children.Add(autoUpdateBox);
        panel.Children.Add(updateButtons);
        panel.Children.Add(statusText);

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            Width = 450,
            MaxHeight = 470,
            Padding = new Thickness(0, 0, 8, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Settings",
            Content = scrollViewer,
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
        _defaultBrowserProvider = NormalizeBrowserProvider((defaultBrowserBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        _showChromeEmailButton = chromeEmailButtonBox.IsChecked == true;
        _showEdgeEmailButton = edgeEmailButtonBox.IsChecked == true;
        _showFirefoxEmailButton = firefoxEmailButtonBox.IsChecked == true;
        ApplyBrowserEmailButtonSettings();
        _gitRepositoryPath = GetConfiguredRepositoryPath();
        SaveState();
    }

    private async Task RunAutoUpdateOnStartupAsync()
    {
        if (!_autoUpdateEnabled)
        {
            return;
        }

        try
        {
            var result = await GitAutoUpdater.UpdateAsync(GetConfiguredRepositoryPath(), line =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    LogTitleText.Text = "Update";
                    LogText.Text = $"[{DateTime.Now:HH:mm:ss}] {line}";
                });
            });

            if (!string.Equals(result.PreviousHead, result.CurrentHead, StringComparison.OrdinalIgnoreCase))
            {
                _lastUpdatePreviousHead = result.PreviousHead;
                DispatcherQueue.TryEnqueue(() =>
                {
                    SaveState();
                    ShowMessage($"Git auto-update completed: {result.PreviousHead[..7]} -> {result.CurrentHead[..7]}. Restart to run updated binaries.", InfoBarSeverity.Success);
                });
            }
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => ShowMessage($"Git auto-update failed: {ex.Message}", InfoBarSeverity.Warning));
        }
    }

    private string GetConfiguredRepositoryPath()
    {
        if (string.IsNullOrWhiteSpace(_gitRepositoryPath))
        {
            _gitRepositoryPath = GitAutoUpdater.DetectRepositoryPath();
        }

        return _gitRepositoryPath;
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
