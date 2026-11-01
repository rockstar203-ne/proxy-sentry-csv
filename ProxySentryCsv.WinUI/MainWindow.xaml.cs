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
    private readonly BrowserExtensionStore _extensionStore;
    private readonly TargetUrlListStore _targetUrlListStore;
    private readonly ProxySpeedChecker _proxySpeedChecker = new();
    private readonly EventSentryBridgeServer _eventSentryBridge;
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
    private bool _syncingTargetUrlSelection;
    private bool _useTargetUrlList;
    private bool _disableTargetUrlAutoAdvance;
    private CancellationTokenSource? _checkAllCancellation;
    private readonly List<string> _logLines = [];
    private bool _logExpanded;
    private List<string> _lastJancySyncStableIds = [];
    private List<string> _lastChromeSyncStableIds = [];
    private int _syncClickDelayMs;
    private bool _showChromeEmailButton;
    private bool _showEdgeEmailButton;
    private bool _showFirefoxEmailButton;
    private const double ProxyColumnMinWidth = 44;
    private const double ProxyColumnResizeHitWidth = 14;
    private const double ProxyTablePaddingWidth = 20;
    private const double ProxyTableColumnSpacing = 10;
    private readonly double[] _defaultProxyColumnWidths = [48, 72, 150, 114, 164, 170, 190, 72, 130, 88, 242];
    private readonly List<WeakReference<Grid>> _proxyRowGridRefs = [];
    private int _resizingProxyColumnIndex = -1;
    private double _resizeStartX;
    private double _resizeStartWidth;

    public ObservableCollection<ProxyEntry> Proxies { get; } = [];
    public ObservableCollection<ProxyListEntry> ProxyLists { get; } = [];
    public ObservableCollection<TargetUrlListEntry> TargetUrlLists { get; } = [];
    public ObservableCollection<TargetUrlItemEntry> ActiveTargetUrls { get; } = [];
    public ObservableCollection<SyncMonitorOption> SyncMonitors { get; } = [];
    public ObservableCollection<BrowserExtensionEntry> GlobalExtensions { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        _stateStore = new AppStateStore(_appDataPath);
        _browserForgeFingerprints = new BrowserForgeFingerprintService(_appDataPath);
        _proxyGeoService = new ProxyGeoService(_appDataPath);
        _browserCache = new BrowserCacheService(_appDataPath);
        _extensionStore = new BrowserExtensionStore(_appDataPath);
        _targetUrlListStore = new TargetUrlListStore(_appDataPath);
        _systemBrowserLauncher = new SystemBrowserLauncher(_appDataPath, _browserForgeFingerprints);
        _jancyLauncher = new JancyLauncher(_appDataPath);
        _systemBrowserLauncher.ProfilesClosed += SystemBrowserLauncher_ProfilesClosed;
        _eventSentryBridge = new EventSentryBridgeServer(
            DispatcherQueue,
            () => GetAllLoadedProxies().ToList(),
            _systemBrowserLauncher,
            line => DispatcherQueue.TryEnqueue(() => ShowMessage(line, InfoBarSeverity.Informational)),
            () => DispatcherQueue.TryEnqueue(() => SaveState()),
            () => DispatcherQueue.TryEnqueue(() => UpdateSummary()));
        _eventSentryBridge.StateChanged += () => DispatcherQueue.TryEnqueue(UpdateEventSentryBridgeIndicator);
        InitializeWindowChrome();
        _suppressStateSave = true;
        InitializeSyncMonitors();
        RestoreSavedState();
        _suppressStateSave = false;
        Closed += MainWindow_Closed;
        _eventSentryBridge.Start();
        UpdateEventSentryBridgeIndicator();
        _ = RefreshCacheSizeAsync();
        InitializeWorkspace();
        if (_adsPowerApiEnabled) _ = ConfigureAdsPowerApiAsync(true);
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
        if ((sender as FrameworkElement)?.DataContext is not ProxyEntry proxy || proxy.IsBusy) return;
        try
        {
            if (proxy.IsOpen)
            {
                proxy.IsBusy = true;
                await CloseProxyWindowAsync(proxy);
            }
            else
            {
                var target = ResolveTargetUrlForBrowserAction();
                if (target is null) return;
                await OpenWorkspaceProfileAsync(proxy, target);
            }
            SaveState();
            UpdateSummary();
        }
        catch (Exception ex)
        {
            proxy.Status = "Error";
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally { proxy.IsBusy = false; RefreshProfileInspector(); }
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
        proxy.IsDirect = false;
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

        var targetUrl = ResolveTargetUrlForBrowserAction();
        if (targetUrl is null)
        {
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
                UpdateSyncStatusPanel();
                ShowMessage($"Jancy group opened: master row {selected[0].RowNumber}, {selected.Count - 1} followers.", InfoBarSeverity.Success);
            }
            else if (IsChromiumSyncProvider(provider))
            {
                if (selected.Any(proxy => proxy.IsOpen && !_systemBrowserLauncher.CanSynchronizeWindow(proxy)))
                {
                    ShowMessage("Close Firefox windows opened individually, then use Open sync group to enable Firefox synchronization. Existing tabs will not be closed automatically.", InfoBarSeverity.Warning);
                    return;
                }
                var monitor = SyncMonitorBox.SelectedItem as SyncMonitorOption;
                IReadOnlyList<SyncWindowPlacement> placements = monitor is null
                    ? Array.Empty<SyncWindowPlacement>()
                    : ChromeSyncController.CalculateWindowPlacements(selected.Count, monitor);
                for (var index = 0; index < selected.Count; index++)
                {
                    var proxy = selected[index];
                    proxy.Status = GetBrowserDisplayName(provider) + (index == 0 ? " master opening" : " follower opening");
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
                        }, placement, enableSync: true);
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
                UpdateSyncStatusPanel();
                ShowMessage($"{GetBrowserDisplayName(provider)} group opened and aligned: master row {selected[0].RowNumber}, {selected.Count - 1} followers.", InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage("Sync is available for Chrome, Firefox and Jancy groups. Select one browser for the group.", InfoBarSeverity.Warning);
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
                    chromeSelected[index].Status = index == 0 ? "Sync MASTER" : "Sync follower";
                }

                _lastChromeSyncStableIds = chromeSelected.Select(proxy => proxy.StableId).ToList();
                UpdateSyncStatusPanel();
                ShowMessage($"Sync started: master row {chromeSelected[0].RowNumber}, {chromeSelected.Count - 1} followers.", InfoBarSeverity.Success);
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
            ShowMessage("Select at least two opened Chrome, Firefox or Jancy rows, or use Open sync group first.", InfoBarSeverity.Warning);
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
            UpdateSyncStatusPanel();
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
        var targetUrl = ResolveTargetUrlForBrowserAction();
        if (targetUrl is null)
        {
            return;
        }

        var chromeSelected = GetChromeSyncRows();
        if (chromeSelected.Count == 0)
        {
            ShowMessage("Open or select Chrome or Firefox sync windows before applying Target URL.", InfoBarSeverity.Warning);
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
                chromeSelected[index].Status = index == 0 ? "Sync MASTER" : "Sync follower";
            }

            _lastChromeSyncStableIds = chromeSelected.Select(proxy => proxy.StableId).ToList();
            AdvanceTargetUrlListAfterSuccessfulApply();
            UpdateSyncStatusPanel();
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

    private async void PasteTargetUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var clipboardContent = Clipboard.GetContent();
            if (!clipboardContent.Contains(StandardDataFormats.Text))
            {
                ShowMessage("Clipboard does not contain text.", InfoBarSeverity.Warning);
                return;
            }

            var text = (await clipboardContent.GetTextAsync()).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowMessage("Clipboard text is empty.", InfoBarSeverity.Warning);
                return;
            }

            if (_useTargetUrlList)
            {
                SelectTargetUrlMode(false);
            }

            TargetUrlBox.Text = text;
            UpdateTargetUrlControls();
            SaveState();
            ShowMessage("Target URL replaced from clipboard.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage($"Could not read clipboard: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private string? ResolveTargetUrlForBrowserAction(bool showMessages = true)
    {
        string rawUrl;
        if (!_useTargetUrlList)
        {
            rawUrl = TargetUrlBox.Text.Trim();
        }
        else
        {
            var list = GetActiveTargetUrlList();
            if (list is null)
            {
                if (showMessages)
                {
                    ShowMessage("Select a Target URL list first.", InfoBarSeverity.Warning);
                }

                return null;
            }

            if (list.IsExhausted)
            {
                if (showMessages)
                {
                    ShowMessage($"Target URL list {list.Name} is finished.", InfoBarSeverity.Warning);
                }

                return null;
            }

            rawUrl = list.CurrentUrl;
        }

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            if (showMessages)
            {
                ShowMessage("Target URL is empty.", InfoBarSeverity.Warning);
            }

            return null;
        }

        if (!TargetUrlNormalizer.TryNormalize(rawUrl, out var targetUrl))
        {
            if (showMessages)
            {
                ShowMessage("Target URL must be a valid URL, for example https://tickets.bso.org.", InfoBarSeverity.Warning);
            }

            return null;
        }

        if (!string.Equals(TargetUrlBox.Text.Trim(), targetUrl, StringComparison.Ordinal))
        {
            TargetUrlBox.Text = targetUrl;
        }

        UpdateTargetUrlControls();
        return targetUrl;
    }

    private void AdvanceTargetUrlListAfterSuccessfulApply()
    {
        if (!_useTargetUrlList || _disableTargetUrlAutoAdvance)
        {
            return;
        }

        var list = GetActiveTargetUrlList();
        if (list is null)
        {
            return;
        }

        list.MoveNext();
        RefreshActiveTargetUrlItems();
        SyncTargetUrlBoxFromActiveList();
        UpdateTargetUrlControls();
    }

    private TargetUrlListEntry? GetActiveTargetUrlList()
    {
        return TargetUrlListBox.SelectedItem as TargetUrlListEntry
            ?? TargetUrlLists.FirstOrDefault();
    }

    private void TargetUrlModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _useTargetUrlList = string.Equals((TargetUrlModeBox.SelectedValue as string) ?? "", "list", StringComparison.OrdinalIgnoreCase);
        if (_useTargetUrlList && TargetUrlListBox.SelectedItem is null && TargetUrlLists.Count > 0)
        {
            TargetUrlListBox.SelectedItem = TargetUrlLists[0];
        }

        RefreshActiveTargetUrlItems();
        SyncTargetUrlBoxFromActiveList();
        UpdateTargetUrlControls();
        if (!_suppressStateSave)
        {
            SaveState();
        }
    }

    private void TargetUrlListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshActiveTargetUrlItems();
        SyncTargetUrlBoxFromActiveList();
        UpdateTargetUrlControls();
        if (!_suppressStateSave)
        {
            SaveState();
        }
    }

    private void TargetUrlPrev_Click(object sender, RoutedEventArgs e)
    {
        var list = GetActiveTargetUrlList();
        if (list is null)
        {
            return;
        }

        list.MovePrevious();
        RefreshActiveTargetUrlItems();
        SyncTargetUrlBoxFromActiveList();
        UpdateTargetUrlControls();
        SaveState();
    }

    private void TargetUrlNext_Click(object sender, RoutedEventArgs e)
    {
        var list = GetActiveTargetUrlList();
        if (list is null)
        {
            return;
        }

        list.MoveNext();
        RefreshActiveTargetUrlItems();
        SyncTargetUrlBoxFromActiveList();
        UpdateTargetUrlControls();
        SaveState();
    }

    private void TargetUrlItemBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTargetUrlSelection || !_useTargetUrlList)
        {
            return;
        }

        var list = GetActiveTargetUrlList();
        if (list is null || TargetUrlItemBox.SelectedItem is not TargetUrlItemEntry item)
        {
            return;
        }

        list.CurrentIndex = item.Index;
        TargetUrlBox.Text = item.Url;
        UpdateTargetUrlControls();
        if (!_suppressStateSave)
        {
            SaveState();
        }
    }

    private void TargetUrlNoAutoNextBox_Changed(object sender, RoutedEventArgs e)
    {
        _disableTargetUrlAutoAdvance = TargetUrlNoAutoNextBox.IsChecked == true;
        if (!_suppressStateSave)
        {
            SaveState();
        }
    }

    private async void TargetUrlLists_Click(object sender, RoutedEventArgs e)
    {
        await ShowTargetUrlListsDialogAsync();
    }

    private void SyncTargetUrlBoxFromActiveList()
    {
        if (!_useTargetUrlList)
        {
            return;
        }

        var list = GetActiveTargetUrlList();
        if (list is null || list.IsExhausted)
        {
            return;
        }

        TargetUrlBox.Text = list.CurrentUrl;
    }

    private void RefreshActiveTargetUrlItems()
    {
        if (TargetUrlItemBox is null)
        {
            return;
        }

        _syncingTargetUrlSelection = true;
        try
        {
            ActiveTargetUrls.Clear();
            var list = GetActiveTargetUrlList();
            if (list is null)
            {
                TargetUrlItemBox.SelectedIndex = -1;
                return;
            }

            for (var index = 0; index < list.Urls.Count; index++)
            {
                ActiveTargetUrls.Add(new TargetUrlItemEntry(index, list.Urls[index]));
            }

            TargetUrlItemBox.SelectedIndex = list.IsExhausted
                ? -1
                : Math.Clamp(list.CurrentIndex, 0, Math.Max(0, list.Count - 1));
        }
        finally
        {
            _syncingTargetUrlSelection = false;
        }
    }

    private void UpdateTargetUrlControls()
    {
        if (TargetUrlListBox is null || TargetUrlItemBox is null)
        {
            return;
        }

        var listMode = _useTargetUrlList;
        var listVisibility = listMode ? Visibility.Visible : Visibility.Collapsed;
        TargetUrlContainerBorder.Padding = listMode ? new Thickness(12) : new Thickness(8);
        SearchHost.Padding = new Thickness(0, listMode ? 12 : 8, 0, 0);
        TargetUrlBox.IsReadOnly = listMode;
        TargetUrlListControlsPanel.Visibility = listVisibility;
        TargetUrlListBox.Visibility = listVisibility;
        TargetUrlItemBox.Visibility = listVisibility;
        TargetUrlListProgressText.Visibility = listVisibility;
        TargetUrlPrevButton.Visibility = listVisibility;
        TargetUrlNextButton.Visibility = listVisibility;
        TargetUrlListsButton.Visibility = listVisibility;
        TargetUrlNoAutoNextBox.Visibility = listVisibility;

        var list = GetActiveTargetUrlList();
        TargetUrlListProgressText.Text = list is null ? "No list" : list.ProgressText;
        TargetUrlPrevButton.IsEnabled = list is not null && list.CurrentIndex > 0;
        TargetUrlNextButton.IsEnabled = list is not null && list.CurrentIndex < list.Count - 1;
    }

    private void SelectTargetUrlMode(bool useList)
    {
        _useTargetUrlList = useList;
        foreach (var item in TargetUrlModeBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), useList ? "list" : "manual", StringComparison.OrdinalIgnoreCase))
            {
                TargetUrlModeBox.SelectedItem = item;
                break;
            }
        }

        RefreshActiveTargetUrlItems();
        UpdateTargetUrlControls();
    }

    private async Task ShowTargetUrlListsDialogAsync()
    {
        var statusText = new TextBlock
        {
            Text = "TXT format: one URL per line. Domains without https:// are allowed. Empty lines and # comments are ignored.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        };
        var listView = new ListView
        {
            ItemsSource = TargetUrlLists,
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 260
        };
        listView.SelectedItem = TargetUrlListBox.SelectedItem;

        var addButton = new Button { Content = "Add TXT list" };
        var removeButton = new Button { Content = "Remove selected" };
        var resetButton = new Button { Content = "Reset progress" };
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        buttonPanel.Children.Add(addButton);
        buttonPanel.Children.Add(removeButton);
        buttonPanel.Children.Add(resetButton);

        addButton.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".txt");
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0)
            {
                return;
            }

            var added = 0;
            foreach (var file in files)
            {
                try
                {
                    var list = _targetUrlListStore.Import(file.Path);
                    TargetUrlLists.Add(list);
                    TargetUrlListBox.SelectedItem = list;
                    listView.SelectedItem = list;
                    added++;
                }
                catch (Exception ex)
                {
                    statusText.Text = ex.Message;
                }
            }

            if (added > 0)
            {
                SelectTargetUrlMode(true);
                SyncTargetUrlBoxFromActiveList();
                UpdateTargetUrlControls();
                SaveState();
                statusText.Text = $"Added {added} Target URL list(s).";
            }
        };

        removeButton.Click += (_, _) =>
        {
            if (listView.SelectedItem is not TargetUrlListEntry list)
            {
                statusText.Text = "Select a Target URL list to remove.";
                return;
            }

            _targetUrlListStore.Delete(list);
            TargetUrlLists.Remove(list);
            var next = TargetUrlLists.FirstOrDefault();
            TargetUrlListBox.SelectedItem = next;
            listView.SelectedItem = next;
            if (next is null)
            {
                SelectTargetUrlMode(false);
            }
            else
            {
                SyncTargetUrlBoxFromActiveList();
            }

            UpdateTargetUrlControls();
            SaveState();
            statusText.Text = $"Removed {list.Name}.";
        };

        resetButton.Click += (_, _) =>
        {
            if (listView.SelectedItem is not TargetUrlListEntry list)
            {
                statusText.Text = "Select a Target URL list to reset.";
                return;
            }

            list.ResetProgress();
            if (ReferenceEquals(TargetUrlListBox.SelectedItem, list))
            {
                SyncTargetUrlBoxFromActiveList();
            }

            UpdateTargetUrlControls();
            SaveState();
            statusText.Text = $"Reset {list.Name}.";
        };

        var panel = new StackPanel
        {
            Spacing = 10,
            Width = 560
        };
        panel.Children.Add(statusText);
        panel.Children.Add(buttonPanel);
        panel.Children.Add(listView);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Target URL lists",
            Content = panel,
            CloseButtonText = "Close"
        };
        await dialog.ShowAsync();
        TargetUrlListBox.SelectedItem = listView.SelectedItem ?? TargetUrlListBox.SelectedItem;
        UpdateTargetUrlControls();
        SaveState();
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

                UpdateSyncStatusPanel();
                ShowMessage("Sync stopped. Browser windows stay open.", InfoBarSeverity.Informational);
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
            UpdateSyncStatusPanel();
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
                var lastGroup = ResolveLastOpenSyncGroup(_lastChromeSyncStableIds, "native-sync");
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
                && (provider == "native-sync" ? IsChromiumSyncProvider(GetEffectiveBrowserProvider(proxy))
                    : string.Equals(GetEffectiveBrowserProvider(proxy), provider, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(proxy => proxy.StableId, StringComparer.Ordinal);
        return stableIds
            .Select(stableId => activeByStableId.TryGetValue(stableId, out var proxy) ? proxy : null)
            .Where(proxy => proxy is not null)
            .Cast<ProxyEntry>()
            .ToList();
    }

    private void UpdateSyncStatusPanel()
    {
        if (SyncStatusPanel is null || SyncStatusText is null)
        {
            return;
        }

        var chromeGroup = ResolveLastOpenSyncGroup(_lastChromeSyncStableIds, "native-sync");
        if (IsActiveSyncGroup(chromeGroup))
        {
            ShowSyncStatusPanel(GetBrowserDisplayName(GetEffectiveBrowserProvider(chromeGroup[0])), chromeGroup);
            return;
        }

        var jancyGroup = ResolveLastOpenSyncGroup(_lastJancySyncStableIds, "jancy");
        if (IsActiveSyncGroup(jancyGroup))
        {
            ShowSyncStatusPanel("Jancy", jancyGroup);
            return;
        }

        SyncStatusText.Text = "Sync idle";
        SyncStatusPanel.Visibility = Visibility.Collapsed;
    }

    private static bool IsActiveSyncGroup(IReadOnlyList<ProxyEntry> group)
    {
        return group.Count >= 2
            && group.Any(proxy =>
                proxy.Status.Contains("master", StringComparison.OrdinalIgnoreCase)
                || proxy.Status.Contains("follower", StringComparison.OrdinalIgnoreCase));
    }

    private void ShowSyncStatusPanel(string browserName, IReadOnlyList<ProxyEntry> group)
    {
        var master = group.FirstOrDefault(proxy => proxy.Status.Contains("master", StringComparison.OrdinalIgnoreCase))
            ?? group[0];
        var monitor = SyncMonitorBox.SelectedItem as SyncMonitorOption;
        var monitorText = monitor is null ? "default monitor" : monitor.DisplayName;
        var delayText = _syncClickDelayMs <= 0
            ? "off"
            : $"{_syncClickDelayMs / 1000.0:0.#} sec";

        SyncStatusText.Text = $"{browserName} sync: master row {master.RowNumber}, followers {Math.Max(0, group.Count - 1)}, click delay {delayText}, {monitorText}.";
        SyncStatusPanel.Visibility = Visibility.Visible;
    }

    private void SyncMonitorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSyncStatusPanel();
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

    private void BulkBrowserApply_Click(object sender, RoutedEventArgs e)
    {
        var provider = NormalizeBrowserProvider(BulkBrowserBox.SelectedValue?.ToString() ?? _defaultBrowserProvider);
        var selected = GetSyncSelectedProxies()
            .DistinctBy(proxy => proxy.StableId)
            .ToList();
        if (selected.Count == 0)
        {
            ShowMessage("Select proxies first, then press Set browser.", InfoBarSeverity.Warning);
            return;
        }

        foreach (var proxy in selected)
        {
            proxy.BrowserProvider = provider;
        }

        SaveState();
        ShowMessage($"Browser set to {GetBrowserDisplayName(provider)} for {selected.Count} selected proxies.", InfoBarSeverity.Success);
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

                UpdateSyncStatusPanel();
                ShowMessage($"Aligned {chromeSelected.Count} browser windows.", InfoBarSeverity.Success);
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

            UpdateSyncStatusPanel();
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
            var targetUrl = ResolveTargetUrlForBrowserAction() ?? "";
            var profilePath = _jancyLauncher.ExportProfiles(GetAllLoadedProxies(), targetUrl);
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
- synchronize Chrome, Firefox or Jancy windows.

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
            Width = 560
        };

        var content = new ScrollViewer
        {
            Width = 600,
            MaxHeight = 520,
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
                if (!string.Equals(proxy.EventSentryState, "Cleanup required", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(proxy.EventSentryState, "Blocked", StringComparison.OrdinalIgnoreCase))
                {
                    proxy.EventSentryState = "Closed";
                }
                proxy.EventSentryRunId = "";
                proxy.EventSentryHeartbeat = null;
                proxy.OpenedProvider = "";
                ClearClosedProxySelection(proxy);
            }
            UpdateSummary();
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
        if (_checkAllCancellation is not null) return;
        if (Proxies.Count == 0)
        {
            ShowMessage("Load a proxy list before checking proxies.", InfoBarSeverity.Warning);
            return;
        }

        CheckAllButton.IsEnabled = false;
        SortButton.IsEnabled = false;
        OpenCsvButton.IsEnabled = false;
        CancelCheckButton.IsEnabled = true;
        CancelCheckButton.Visibility = Visibility.Visible;
        CheckProgressBar.Visibility = Visibility.Visible;
        CheckProgressBar.Value = 0;
        CheckProgressText.Text = "Preparing check...";
        var checkAllContent = CheckAllButton.Content;
        var checkStatusText = new TextBlock
        {
            Text = "Checking...",
            VerticalAlignment = VerticalAlignment.Center
        };
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
                checkStatusText
            }
        };

        try
        {
            var snapshot = Proxies.ToList();
            foreach (var proxy in snapshot)
            {
                if (!proxy.IsOpen) proxy.Status = "Queued";
            }

            var completed = 0;
            var checkedCount = 0;
            var historyFailures = 0;
            var history = new ConnectionHistoryStore(_appDataPath);
            var targetUrl = ResolveTargetUrlForBrowserAction(showMessages: false);
            var targetUri = Uri.TryCreate(targetUrl, UriKind.Absolute, out var parsedTarget)
                ? parsedTarget
                : null;
            using var semaphore = new SemaphoreSlim(4);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            _checkAllCancellation = timeout;

            var tasks = snapshot.Select(async proxy =>
            {
                var entered = false;
                try
                {
                    await semaphore.WaitAsync(timeout.Token);
                    entered = true;
                    timeout.Token.ThrowIfCancellationRequested();
                    await RunOnUiAsync(() => { if (!proxy.IsOpen) proxy.Status = "Checking"; });
                    var result = await _proxySpeedChecker.CheckAsync(proxy, targetUri, timeout.Token);
                    await RunOnUiAsync(() =>
                    {
                        proxy.PingMs = result.PingMs;
                        proxy.ResponseMs = result.ResponseMs;
                        proxy.SpeedGroup = result.SpeedGroup;
                        if (!proxy.IsOpen) proxy.Status = result.Status;
                        checkedCount++;
                    });
                    try
                    {
                        await Task.Run(() => history.Add(proxy,
                            _systemBrowserLauncher.GetInstalledVersion(proxy.BrowserProvider),
                            targetUri ?? ProxySpeedChecker.DefaultTestUri, ProxyRouteMode.Proxy, result));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Interlocked.Increment(ref historyFailures);
                    }
                    try
                    {
                        var geo = await _proxyGeoService.GetAsync(proxy, cancellationToken: timeout.Token);
                        await RunOnUiAsync(() => ApplyProxyGeo(proxy, geo));
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
                    catch
                    {
                        // Region is supplemental; preserve the completed connection result.
                    }
                }
                catch (OperationCanceledException)
                {
                    await RunOnUiAsync(() =>
                    {
                        if (!proxy.IsOpen && proxy.Status is "Checking" or "Queued") proxy.Status = "Cancelled";
                    });
                }
                catch
                {
                    await RunOnUiAsync(() =>
                    {
                        if (!proxy.IsOpen) proxy.Status = "Check failed";
                    });
                }
                finally
                {
                    if (entered) semaphore.Release();
                    await RunOnUiAsync(() =>
                    {
                        completed++;
                        UpdateCheckProgress(completed, snapshot.Count, checkStatusText);
                    });
                }
            });

            await Task.WhenAll(tasks);
            if (!timeout.IsCancellationRequested)
            {
                SortProxiesBySpeed();
            }
            SaveState();
            UpdateSummary();
            ShowMessage(timeout.IsCancellationRequested
                ? $"Proxy check cancelled. Checked {checkedCount}/{snapshot.Count}."
                : $"Proxy check finished. Checked {checkedCount}/{snapshot.Count}." +
                    (historyFailures > 0 ? $" History could not be saved for {historyFailures} checks." : ""),
                timeout.IsCancellationRequested ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            SaveState();
            UpdateSummary();
            ShowMessage("Proxy check cancelled.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _checkAllCancellation = null;
            CheckAllButton.Content = checkAllContent;
            CheckAllButton.IsEnabled = true;
            SortButton.IsEnabled = true;
            OpenCsvButton.IsEnabled = true;
            CancelCheckButton.Visibility = Visibility.Collapsed;
            CancelCheckButton.IsEnabled = true;
            CheckProgressBar.Visibility = Visibility.Collapsed;
            CheckProgressText.Text = "";
        }
    }

    private void CancelCheck_Click(object sender, RoutedEventArgs e)
    {
        _checkAllCancellation?.Cancel();
        CancelCheckButton.IsEnabled = false;
        CheckProgressText.Text = "Cancelling...";
    }

    private void UpdateCheckProgress(int completed, int total, TextBlock? checkStatusText = null)
    {
        var text = $"Checked {completed}/{total}";
        if (checkStatusText is not null)
        {
            checkStatusText.Text = text;
        }

        CheckProgressText.Text = text;
        CheckProgressBar.Value = total <= 0 ? 0 : Math.Clamp(completed * 100.0 / total, 0, 100);
        LogTitleText.Text = "Check";
        LogText.Text = text;
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
            new ProfileIdentityStore(_appDataPath).Bind(proxies, path);
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
        proxy.Notes = savedState.Notes;
        proxy.Tags = savedState.Tags;
        proxy.LastUsedUtc = savedState.LastUsedUtc;
        proxy.Used = savedState.Used;
        proxy.Copied = savedState.Copied;
        proxy.IsFavorite = savedState.IsFavorite;
        proxy.UseForEventSentry = savedState.UseForEventSentry;
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
                ProfileFilter = (ProfileFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all",
                TargetUrl = TargetUrlBox.Text.Trim(),
                UseTargetUrlList = _useTargetUrlList,
                DisableTargetUrlAutoAdvance = _disableTargetUrlAutoAdvance,
                ActiveTargetUrlListKey = (TargetUrlListBox.SelectedItem as TargetUrlListEntry)?.Key,
                TargetUrlLists = TargetUrlLists.Select(list => new AppStateStore.TargetUrlListState
                {
                    Key = list.Key,
                    Name = list.Name,
                    Path = list.Path,
                    CurrentIndex = list.CurrentIndex
                }).ToList(),
                SyncMonitorId = (SyncMonitorBox.SelectedItem as SyncMonitorOption)?.Id,
                BrowserProvider = _defaultBrowserProvider,
                JancyEndpointUrl = _jancyEndpointUrl,
                AutoUpdateEnabled = _autoUpdateEnabled,
                GitRepositoryPath = _gitRepositoryPath,
                LastUpdatePreviousHead = _lastUpdatePreviousHead,
                SyncClickDelayMs = _syncClickDelayMs,
                ShowChromeEmailButton = _showChromeEmailButton,
                ShowEdgeEmailButton = _showEdgeEmailButton,
                ShowFirefoxEmailButton = _showFirefoxEmailButton,
                AdsPowerCompatibilityEnabled = _adsPowerApiEnabled,
                AdsPowerApiPort = _adsPowerApiPort,
                ProxyTableColumnWidths = CaptureProxyColumnWidths(),
                GlobalExtensions = GlobalExtensions.ToList()
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
        _adsPowerApiEnabled = settings.AdsPowerCompatibilityEnabled;
        _adsPowerApiPort = settings.AdsPowerApiPort is >= 1 and <= 65535
            ? settings.AdsPowerApiPort : AdsPowerCompatibilityServer.DefaultPort;
        RestoreProxyColumnWidths(settings.ProxyTableColumnWidths);
        ApplyBrowserEmailButtonSettings();
        GlobalExtensions.Clear();
        foreach (var extension in settings.GlobalExtensions)
        {
            GlobalExtensions.Add(extension);
        }
        ApplyGlobalExtensions();
        _gitRepositoryPath = GitAutoUpdater.DetectRepositoryPath();
        _lastUpdatePreviousHead = settings.LastUpdatePreviousHead;
        SearchBox.Text = settings.SearchText;
        ProfileFilterBox.SelectedItem = ProfileFilterBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == settings.ProfileFilter) ?? ProfileFilterBox.Items[0];
        _disableTargetUrlAutoAdvance = settings.DisableTargetUrlAutoAdvance;
        TargetUrlNoAutoNextBox.IsChecked = _disableTargetUrlAutoAdvance;

        if (!string.IsNullOrWhiteSpace(settings.TargetUrl))
        {
            TargetUrlBox.Text = settings.TargetUrl;
        }

        TargetUrlLists.Clear();
        foreach (var targetUrlListState in settings.TargetUrlLists)
        {
            var list = _targetUrlListStore.Load(targetUrlListState);
            if (list is not null)
            {
                TargetUrlLists.Add(list);
            }
        }

        TargetUrlListBox.SelectedItem = TargetUrlLists.FirstOrDefault(list =>
                string.Equals(list.Key, settings.ActiveTargetUrlListKey, StringComparison.OrdinalIgnoreCase))
            ?? TargetUrlLists.FirstOrDefault();
        SelectTargetUrlMode(settings.UseTargetUrlList && TargetUrlLists.Count > 0);
        SyncTargetUrlBoxFromActiveList();
        UpdateTargetUrlControls();

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

    private void RestoreProxyColumnWidths(IReadOnlyList<double>? savedWidths)
    {
        var widths = NormalizeProxyColumnWidths(savedWidths);
        ApplyProxyColumnWidths(ProxyHeaderGrid, widths);
        ApplyProxyColumnWidthsToRows(widths);
        UpdateProxyTableMinWidths(widths);
    }

    private List<double> CaptureProxyColumnWidths()
    {
        if (ProxyHeaderGrid is null)
        {
            return _defaultProxyColumnWidths.ToList();
        }

        var widths = new List<double>(_defaultProxyColumnWidths.Length);
        for (var index = 0; index < _defaultProxyColumnWidths.Length; index++)
        {
            var width = index < ProxyHeaderGrid.ColumnDefinitions.Count
                ? ProxyHeaderGrid.ColumnDefinitions[index].Width.Value
                : _defaultProxyColumnWidths[index];
            widths.Add(NormalizeProxyColumnWidth(index, width));
        }

        return widths;
    }

    private List<double> NormalizeProxyColumnWidths(IReadOnlyList<double>? savedWidths)
    {
        if (savedWidths is null || savedWidths.Count != _defaultProxyColumnWidths.Length)
        {
            return _defaultProxyColumnWidths
                .Select((width, index) => NormalizeProxyColumnWidth(index, width))
                .ToList();
        }

        var widths = new List<double>(_defaultProxyColumnWidths.Length);
        for (var index = 0; index < _defaultProxyColumnWidths.Length; index++)
        {
            widths.Add(NormalizeProxyColumnWidth(index, savedWidths[index]));
        }

        return widths;
    }

    private double NormalizeProxyColumnWidth(int index, double width)
    {
        var fallback = index >= 0 && index < _defaultProxyColumnWidths.Length
            ? _defaultProxyColumnWidths[index]
            : ProxyColumnMinWidth;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
        {
            width = fallback;
        }

        return Math.Max(index == 10 ? 242 : ProxyColumnMinWidth, width);
    }

    private void ProxyRowGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid grid)
        {
            return;
        }

        _proxyRowGridRefs.RemoveAll(reference => !reference.TryGetTarget(out var target) || ReferenceEquals(target, grid));
        _proxyRowGridRefs.Add(new WeakReference<Grid>(grid));
        ApplyProxyColumnWidths(grid, CaptureProxyColumnWidths());
    }

    private void ProxyRowGrid_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid grid)
        {
            return;
        }

        _proxyRowGridRefs.RemoveAll(reference => !reference.TryGetTarget(out var target) || ReferenceEquals(target, grid));
    }

    private void ProxyColumnHeader_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement header || !TryGetProxyColumnIndex(header, out var index))
        {
            return;
        }

        var headerPoint = e.GetCurrentPoint(header);
        if (header.ActualWidth - headerPoint.Position.X > ProxyColumnResizeHitWidth)
        {
            return;
        }

        _resizingProxyColumnIndex = index;
        _resizeStartX = e.GetCurrentPoint(Root).Position.X;
        _resizeStartWidth = GetProxyColumnWidth(index);
        header.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ProxyColumnHeader_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizingProxyColumnIndex < 0)
        {
            return;
        }

        var delta = e.GetCurrentPoint(Root).Position.X - _resizeStartX;
        SetProxyColumnWidth(_resizingProxyColumnIndex, _resizeStartWidth + delta);
        e.Handled = true;
    }

    private void ProxyColumnHeader_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        FinishProxyColumnResize(sender as FrameworkElement, e);
    }

    private void ProxyColumnHeader_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        FinishProxyColumnResize(sender as FrameworkElement, e);
    }

    private void FinishProxyColumnResize(FrameworkElement? header, PointerRoutedEventArgs e)
    {
        if (_resizingProxyColumnIndex < 0)
        {
            return;
        }

        header?.ReleasePointerCapture(e.Pointer);
        _resizingProxyColumnIndex = -1;
        SaveState();
        e.Handled = true;
    }

    private bool TryGetProxyColumnIndex(FrameworkElement element, out int index)
    {
        return int.TryParse(element.Tag?.ToString(), out index)
            && index >= 0
            && index < _defaultProxyColumnWidths.Length;
    }

    private double GetProxyColumnWidth(int index)
    {
        if (ProxyHeaderGrid is not null && index >= 0 && index < ProxyHeaderGrid.ColumnDefinitions.Count)
        {
            return NormalizeProxyColumnWidth(index, ProxyHeaderGrid.ColumnDefinitions[index].Width.Value);
        }

        return NormalizeProxyColumnWidth(index, _defaultProxyColumnWidths.ElementAtOrDefault(index));
    }

    private void SetProxyColumnWidth(int index, double width)
    {
        if (index < 0 || index >= _defaultProxyColumnWidths.Length)
        {
            return;
        }

        width = NormalizeProxyColumnWidth(index, width);
        SetGridColumnWidth(ProxyHeaderGrid, index, width);

        _proxyRowGridRefs.RemoveAll(reference =>
        {
            if (!reference.TryGetTarget(out var grid))
            {
                return true;
            }

            SetGridColumnWidth(grid, index, width);
            return false;
        });

        UpdateProxyTableMinWidths(CaptureProxyColumnWidths());
        UpdatePinnedActions();
    }

    private void ApplyProxyColumnWidthsToRows(IReadOnlyList<double> widths)
    {
        _proxyRowGridRefs.RemoveAll(reference =>
        {
            if (!reference.TryGetTarget(out var grid))
            {
                return true;
            }

            ApplyProxyColumnWidths(grid, widths);
            return false;
        });
    }

    private void ApplyProxyColumnWidths(Grid? grid, IReadOnlyList<double> widths)
    {
        if (grid is null)
        {
            return;
        }

        for (var index = 0; index < _defaultProxyColumnWidths.Length && index < grid.ColumnDefinitions.Count; index++)
        {
            SetGridColumnWidth(grid, index, widths[index]);
        }

        grid.MinWidth = CalculateProxyTableWidth(widths);
    }

    private static void SetGridColumnWidth(Grid? grid, int index, double width)
    {
        if (grid is null || index < 0 || index >= grid.ColumnDefinitions.Count)
        {
            return;
        }

        grid.ColumnDefinitions[index].Width = new GridLength(width);
    }

    private void UpdateProxyTableMinWidths(IReadOnlyList<double> widths)
    {
        var minWidth = CalculateProxyTableWidth(widths);
        ProxyList.MinWidth = minWidth;
        if (ProxyHeaderGrid is not null)
        {
            ProxyHeaderGrid.MinWidth = minWidth;
        }

        _proxyRowGridRefs.RemoveAll(reference =>
        {
            if (!reference.TryGetTarget(out var grid))
            {
                return true;
            }

            grid.MinWidth = minWidth;
            return false;
        });
    }

    private double CalculateProxyTableWidth(IReadOnlyList<double> widths)
    {
        return widths.Sum()
            + Math.Max(0, widths.Count - 1) * ProxyTableColumnSpacing
            + ProxyTablePaddingWidth;
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
        return NormalizeBrowserProvider(provider) is "chrome" or "firefox";
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
        var filter = (ProfileFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        filtered = filter switch
        {
            "open" => filtered.Where(p => p.IsOpen),
            "closed" => filtered.Where(p => !p.IsOpen),
            "favorites" => filtered.Where(p => p.IsFavorite),
            "new" => filtered.Where(p => !p.Used),
            "used" => filtered.Where(p => p.Used),
            "errors" => filtered.Where(p => p.SpeedGroup == "Dead"),
            _ => filtered
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(proxy =>
                proxy.Host.Contains(search, StringComparison.OrdinalIgnoreCase)
                || proxy.StandardProxy.Contains(search, StringComparison.OrdinalIgnoreCase)
                || proxy.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || proxy.RegionText.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var visible = filtered.ToList();
        _syncingSelectionState = true;
        try
        {
            Proxies.Clear();
            for (var index = 0; index < visible.Count; index++)
            {
                var proxy = visible[index];
                proxy.ListNumber = index + 1;
                Proxies.Add(proxy);
                if (proxy.SyncSelected) ProxyList.SelectedItems.Add(proxy);
            }
        }
        finally { _syncingSelectionState = false; }

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
        RefreshProfileInspector();
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

    private void SelectForEventSentry_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetAllLoadedProxies().Where(proxy => proxy.SyncSelected).ToList();

        if (targets.Count == 0)
        {
            ShowMessage("Check rows in the Select column first, then press Select for Event Sentry.", InfoBarSeverity.Warning);
            return;
        }

        var selectedIds = targets
            .Select(proxy => proxy.StableId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var proxy in GetAllLoadedProxies())
        {
            proxy.UseForEventSentry = selectedIds.Contains(proxy.StableId);
        }

        SaveState();
        UpdateSummary();
        ShowMessage($"Event Sentry profiles selected: {targets.Count}.", InfoBarSeverity.Success);
    }

    private void ClearEventSentrySelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var proxy in GetAllLoadedProxies()) proxy.UseForEventSentry = false;
        SaveState();
        UpdateSummary();
        ShowMessage("Event Sentry proxy selection cleared.", InfoBarSeverity.Success);
    }

    private List<ProxyEntry> GetSyncSelectedProxies()
    {
        var checkedRows = GetAllLoadedProxies().Where(proxy => proxy.SyncSelected).ToList();
        return checkedRows.Count > 0 ? checkedRows : GetSelectedProxies();
    }

    private void ShowSelectionSummary()
    {
        UpdateSelectionSummaryText();
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
        var eventSentrySelected = GetAllLoadedProxies().Count(proxy => proxy.UseForEventSentry);
        ShowMessage($"Selected proxies: {selected.Count}. Sync selected: {syncSelected.Count}. Event Sentry: {eventSentrySelected}{suffix}", InfoBarSeverity.Informational);
    }

    private void UpdateSelectionSummaryText()
    {
        if (SelectedSummaryText is null)
        {
            return;
        }

        var selected = GetSelectedProxies().Count;
        var syncSelected = GetAllLoadedProxies().Count(proxy => proxy.SyncSelected);
        var displayCount = syncSelected > 0 ? syncSelected : selected;
        SelectedSummaryText.Text = $"Selected: {displayCount}";
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
        CaptureRecoverySession();
        UpdateEventSentryBridgeIndicator();
        UpdateSelectionSummaryText();
        UpdateSyncStatusPanel();
    }

    private void UpdateEventSentryBridgeIndicator()
    {
        if (EventSentryBridgeText is null)
        {
            return;
        }

        var selected = GetAllLoadedProxies().Count(proxy => proxy.UseForEventSentry);
        if (SelectEventSentryButton is not null)
        {
            SelectEventSentryButton.Content = selected > 0
                ? $"Event Sentry: {selected} selected - Clear"
                : "Select for Event Sentry";
        }

        EventSentryBridgeText.Text = _eventSentryBridge.IsRunning
            ? "Event Sentry bridge: Running"
            : "Event Sentry bridge: Stopped";
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
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogTitleText.Text = severity switch
        {
            InfoBarSeverity.Error => "Error",
            InfoBarSeverity.Warning => "Warning",
            InfoBarSeverity.Success => "Success",
            _ => "Log"
        };
        LogText.Text = line;
        AppendLogLine(line);
        LogBorder.BorderBrush = severity switch
        {
            InfoBarSeverity.Error => new SolidColorBrush(ColorHelper.FromArgb(255, 220, 53, 69)),
            InfoBarSeverity.Warning => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 193, 7)),
            InfoBarSeverity.Success => new SolidColorBrush(ColorHelper.FromArgb(255, 40, 167, 69)),
            _ => new SolidColorBrush(ColorHelper.FromArgb(255, 108, 117, 125))
        };
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        _logExpanded = !_logExpanded;
        LogDetailsScrollViewer.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
        ToggleLogButton.Content = _logExpanded ? "Collapse" : "Expand";
        RefreshExpandedLogText();
    }

    private void AppendLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _logLines.Add(line);
        if (_logLines.Count > 250)
        {
            _logLines.RemoveRange(0, _logLines.Count - 250);
        }

        RefreshExpandedLogText();
    }

    private void RefreshExpandedLogText()
    {
        if (LogDetailsText is null)
        {
            return;
        }

        LogDetailsText.Text = string.Join(Environment.NewLine, _logLines);
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
        AppendLogLine(line);
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
        UpdateSyncStatusPanel();
    }

    private void ApplyBrowserEmailButtonSettings()
    {
        _systemBrowserLauncher.SetProfileEmailButtonOptions(
            _showChromeEmailButton,
            _showEdgeEmailButton,
            _showFirefoxEmailButton);
    }

    private void ApplyGlobalExtensions()
    {
        foreach (var extension in GlobalExtensions)
        {
            NormalizeGlobalExtensionTargets(extension);
        }

        _systemBrowserLauncher.SetGlobalExtensions(GlobalExtensions);
    }

    private static void NormalizeGlobalExtensionTargets(BrowserExtensionEntry extension)
    {
        if (!extension.SupportsChrome)
        {
            extension.UseChrome = false;
        }

        if (!extension.SupportsEdge)
        {
            extension.UseEdge = false;
        }

        if (!extension.SupportsFirefox)
        {
            extension.UseFirefox = false;
        }

        if (!extension.UseChrome && !extension.UseEdge && !extension.UseFirefox)
        {
            extension.Enabled = false;
        }
    }

    private async void Extensions_Click(object sender, RoutedEventArgs e)
    {
        var storeUrlBox = new TextBox
        {
            Header = "Install from store URL",
            PlaceholderText = "Chrome Web Store, Edge Add-ons, or Firefox Add-ons URL",
            Width = 760
        };
        var installStoreButton = new Button { Content = "Install URL" };
        var addFolderButton = new Button { Content = "Add unpacked folder" };
        var addPackageButton = new Button { Content = "Add .zip/.xpi" };
        var statusText = new TextBlock
        {
            Text = "Ready",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.82
        };
        var hint = new TextBlock
        {
            Text = "Store installs are local to Proxy Sentry CSV. No system browser policies are written, and normal Chrome, Edge, and Firefox profiles are not changed.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.82
        };
        var selectedExtensionBrowser = "chrome";
        var browserTabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        var chromeTabButton = new Button { MinWidth = 120 };
        var edgeTabButton = new Button { MinWidth = 120 };
        var firefoxTabButton = new Button { MinWidth = 120 };
        var extensionSectionHost = new ContentControl
        {
            MinHeight = 300
        };

        void RefreshExtensionSections()
        {
            foreach (var extension in GlobalExtensions)
            {
                NormalizeGlobalExtensionTargets(extension);
            }

            chromeTabButton.Content = $"Chrome ({GlobalExtensions.Count(extension => extension.SupportsChrome)})";
            edgeTabButton.Content = $"Edge ({GlobalExtensions.Count(extension => extension.SupportsEdge)})";
            firefoxTabButton.Content = $"Firefox ({GlobalExtensions.Count(extension => extension.SupportsFirefox)})";
            ApplyExtensionTabState(chromeTabButton, selectedExtensionBrowser, "chrome");
            ApplyExtensionTabState(edgeTabButton, selectedExtensionBrowser, "edge");
            ApplyExtensionTabState(firefoxTabButton, selectedExtensionBrowser, "firefox");

            extensionSectionHost.Content = selectedExtensionBrowser switch
            {
                "edge" => BuildBrowserExtensionSection(
                    "Edge",
                    "Runs only with Edge windows opened by Proxy Sentry CSV.",
                    extension => extension.SupportsEdge,
                    extension => extension.UseEdge,
                    (extension, enabled) => extension.UseEdge = enabled,
                    RefreshExtensionSections),
                "firefox" => BuildBrowserExtensionSection(
                    "Firefox",
                    "Runs only with Firefox windows opened by Proxy Sentry CSV.",
                    extension => extension.SupportsFirefox,
                    extension => extension.UseFirefox,
                    (extension, enabled) => extension.UseFirefox = enabled,
                    RefreshExtensionSections),
                _ => BuildBrowserExtensionSection(
                    "Chrome",
                    "Runs only with Chrome windows opened by Proxy Sentry CSV.",
                    extension => extension.SupportsChrome,
                    extension => extension.UseChrome,
                    (extension, enabled) => extension.UseChrome = enabled,
                    RefreshExtensionSections)
            };
        }

        void SelectExtensionBrowser(string browser)
        {
            selectedExtensionBrowser = browser;
            RefreshExtensionSections();
        }

        chromeTabButton.Click += (_, _) => SelectExtensionBrowser("chrome");
        edgeTabButton.Click += (_, _) => SelectExtensionBrowser("edge");
        firefoxTabButton.Click += (_, _) => SelectExtensionBrowser("firefox");
        browserTabs.Children.Add(chromeTabButton);
        browserTabs.Children.Add(edgeTabButton);
        browserTabs.Children.Add(firefoxTabButton);

        installStoreButton.Click += async (_, _) =>
        {
            var storeUrl = storeUrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(storeUrl))
            {
                ShowMessage("Paste a store extension URL first.", InfoBarSeverity.Warning);
                return;
            }

            try
            {
                installStoreButton.IsEnabled = false;
                statusText.Text = "Downloading extension...";
                var extension = await _extensionStore.ImportFromStoreUrlAsync(storeUrl, line =>
                {
                    DispatcherQueue.TryEnqueue(() => statusText.Text = line);
                });
                GlobalExtensions.Add(extension);
                ApplyGlobalExtensions();
                SaveState();
                RefreshExtensionSections();
                storeUrlBox.Text = "";
                statusText.Text = $"Installed: {extension.Name}";
                ShowMessage($"Extension installed: {extension.Name}", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                statusText.Text = ex.Message;
                ShowMessage(ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                installStoreButton.IsEnabled = true;
            }
        };

        addFolderButton.Click += async (_, _) =>
        {
            var path = await PickExtensionFolderAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var extension = _extensionStore.Import(path);
                GlobalExtensions.Add(extension);
                ApplyGlobalExtensions();
                SaveState();
                RefreshExtensionSections();
                ShowMessage($"Extension added: {extension.Name}", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message, InfoBarSeverity.Error);
            }
        };

        addPackageButton.Click += async (_, _) =>
        {
            var path = await PickExtensionPackageAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var extension = _extensionStore.Import(path);
                GlobalExtensions.Add(extension);
                ApplyGlobalExtensions();
                SaveState();
                RefreshExtensionSections();
                ShowMessage($"Extension added: {extension.Name}", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message, InfoBarSeverity.Error);
            }
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        buttonPanel.Children.Add(installStoreButton);
        buttonPanel.Children.Add(addFolderButton);
        buttonPanel.Children.Add(addPackageButton);

        var panel = new StackPanel
        {
            Spacing = 10,
            Width = 760
        };
        panel.Children.Add(hint);
        panel.Children.Add(storeUrlBox);
        panel.Children.Add(buttonPanel);
        panel.Children.Add(statusText);
        panel.Children.Add(browserTabs);
        RefreshExtensionSections();
        panel.Children.Add(extensionSectionHost);

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            Width = 800,
            MaxHeight = 620,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Global browser extensions",
            Content = scrollViewer,
            CloseButtonText = "Close"
        };

        await dialog.ShowAsync();
        ApplyGlobalExtensions();
        SaveState();
    }

    private static void ApplyExtensionTabState(Button button, string selectedBrowser, string browser)
    {
        var isSelected = string.Equals(selectedBrowser, browser, StringComparison.OrdinalIgnoreCase);
        button.Background = isSelected
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 13, 110, 253))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 52, 58, 64));
        button.Foreground = new SolidColorBrush(Colors.White);
        button.BorderBrush = isSelected
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 91, 157, 255))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 73, 80, 87));
    }

    private FrameworkElement BuildBrowserExtensionSection(
        string browserName,
        string description,
        Func<BrowserExtensionEntry, bool> canUse,
        Func<BrowserExtensionEntry, bool> isSelected,
        Action<BrowserExtensionEntry, bool> setSelected,
        Action refreshSections)
    {
        var section = new StackPanel
        {
            Spacing = 8
        };
        section.Children.Add(new TextBlock
        {
            Text = browserName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 16
        });
        section.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });

        var entries = GlobalExtensions
            .Where(canUse)
            .OrderByDescending(isSelected)
            .ThenBy(extension => extension.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0)
        {
            section.Children.Add(new TextBlock
            {
                Text = $"No {browserName} extensions installed.",
                Opacity = 0.72
            });
            return CreateExtensionSectionBorder(section);
        }

        foreach (var extension in entries)
        {
            section.Children.Add(BuildBrowserExtensionRow(browserName, extension, canUse, isSelected, setSelected, refreshSections));
        }

        return CreateExtensionSectionBorder(section);
    }

    private FrameworkElement BuildBrowserExtensionRow(
        string browserName,
        BrowserExtensionEntry extension,
        Func<BrowserExtensionEntry, bool> canUse,
        Func<BrowserExtensionEntry, bool> isSelected,
        Action<BrowserExtensionEntry, bool> setSelected,
        Action refreshSections)
    {
        var row = new Grid
        {
            Padding = new Thickness(10, 8, 10, 8),
            ColumnSpacing = 12,
            MinHeight = 48,
            Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255))
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        var runBox = new CheckBox
        {
            Content = "Run",
            IsChecked = isSelected(extension),
            IsEnabled = canUse(extension),
            VerticalAlignment = VerticalAlignment.Center
        };
        runBox.Checked += (_, _) =>
        {
            extension.Enabled = true;
            setSelected(extension, true);
            ApplyGlobalExtensions();
            SaveState();
        };
        runBox.Unchecked += (_, _) =>
        {
            setSelected(extension, false);
            if (!extension.UseChrome && !extension.UseEdge && !extension.UseFirefox)
            {
                extension.Enabled = false;
            }

            ApplyGlobalExtensions();
            SaveState();
        };

        var details = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        details.Children.Add(new TextBlock
        {
            Text = extension.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        details.Children.Add(new TextBlock
        {
            Text = $"{browserName} | {extension.SourceKind} | {extension.SupportText}",
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            Opacity = 0.72
        });

        var removeButton = new Button
        {
            Content = "Remove",
            MinWidth = 86,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        removeButton.Click += (_, _) =>
        {
            try
            {
                _extensionStore.Remove(extension);
                GlobalExtensions.Remove(extension);
                ApplyGlobalExtensions();
                SaveState();
                refreshSections();
                ShowMessage($"Extension removed: {extension.Name}", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message, InfoBarSeverity.Error);
            }
        };

        Grid.SetColumn(runBox, 0);
        Grid.SetColumn(details, 1);
        Grid.SetColumn(removeButton, 2);
        row.Children.Add(runBox);
        row.Children.Add(details);
        row.Children.Add(removeButton);

        return new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(4)
        };
    }

    private static Border CreateExtensionSectionBorder(UIElement content)
    {
        return new Border
        {
            Child = content,
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(96, 128, 128, 128)),
            CornerRadius = new CornerRadius(6)
        };
    }

    private async Task<string?> PickExtensionFolderAsync()
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async Task<string?> PickExtensionPackageAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".crx");
        picker.FileTypeFilter.Add(".xpi");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        GitAutoUpdater.UpdateCheckResult? lastCheck = null;
        var busy = false;
        _lastUpdatePreviousHead = GitAutoUpdater.GetLatestBackupPath(GetConfiguredRepositoryPath()) ?? _lastUpdatePreviousHead;
        var currentVersionText = new TextBlock
        {
            Text = $"Current version: {GetCurrentAppVersion()}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        var availableVersionText = new TextBlock
        {
            Text = "Latest version: not checked yet",
            Opacity = 0.82
        };
        var statusText = new TextBlock
        {
            Text = "Checking for updates...",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15
        };
        var progressRing = new ProgressRing
        {
            Width = 28,
            Height = 28,
            IsActive = true,
            Visibility = Visibility.Visible
        };
        var detailsText = new TextBlock
        {
            Text = "Proxy Sentry CSV will check the GitHub release channel and compare your version with the latest available version.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        };
        var checkButton = new Button { Content = "Check again" };
        var updateButton = new Button { Content = "Install update", IsEnabled = false };
        var rollbackButton = new Button
        {
            Content = "Rollback previous version",
            IsEnabled = GitAutoUpdater.CanRollback(GetConfiguredRepositoryPath(), _lastUpdatePreviousHead)
        };

        void SetBusy(bool value, string status)
        {
            busy = value;
            statusText.Text = status;
            progressRing.IsActive = value;
            progressRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            checkButton.IsEnabled = !value;
            updateButton.IsEnabled = !value && lastCheck?.IsUpdateAvailable == true;
            rollbackButton.IsEnabled = !value && GitAutoUpdater.CanRollback(GetConfiguredRepositoryPath(), _lastUpdatePreviousHead);
        }

        void SetFinished(string status, InfoBarSeverity severity = InfoBarSeverity.Informational)
        {
            SetBusy(false, status);
            ShowMessage(status, severity);
        }

        async Task RunCheckAsync()
        {
            try
            {
                lastCheck = null;
                availableVersionText.Text = "Latest version: checking...";
                updateButton.IsEnabled = false;
                SetBusy(true, "Checking for updates...");
                var check = await GitAutoUpdater.CheckAsync(GetConfiguredRepositoryPath());
                lastCheck = check;
                var remoteVersion = string.IsNullOrWhiteSpace(check.RemoteVersion)
                    ? "latest commit"
                    : check.RemoteVersion;
                availableVersionText.Text = $"Latest version: {remoteVersion}";

                if (check.IsUpdateAvailable)
                {
                    SetFinished($"Update available: version {remoteVersion}.", InfoBarSeverity.Success);
                    updateButton.IsEnabled = true;
                    return;
                }

                SetFinished("Your version is up to date.", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                lastCheck = null;
                availableVersionText.Text = "Latest version: check failed";
                detailsText.Text = ex.Message;
                SetFinished($"Update check failed: {ex.Message}", InfoBarSeverity.Error);
            }
        }

        async Task RunUpdateAsync()
        {
            try
            {
                SaveState();
                SetBusy(true, "Installing update...");
                detailsText.Text = "Downloading the latest portable package. Proxy Sentry CSV will restart when the updater starts.";
                var result = await GitAutoUpdater.UpdateAsync(GetConfiguredRepositoryPath());
                if (!string.Equals(result.PreviousHead, result.CurrentHead, StringComparison.OrdinalIgnoreCase))
                {
                    SaveState();
                    lastCheck = lastCheck is null
                        ? null
                        : lastCheck with { IsUpdateAvailable = false, LocalHead = result.CurrentHead };
                    SetFinished("Update downloaded. Proxy Sentry CSV will close and restart to apply it.", InfoBarSeverity.Success);
                    detailsText.Text = "The updater is applying the release package. Your Data folder and app state are kept.";
                    await Task.Delay(800);
                    Application.Current.Exit();
                    return;
                }

                lastCheck = lastCheck is null
                    ? null
                    : lastCheck with { IsUpdateAvailable = false };
                SetFinished("Your version is up to date.", InfoBarSeverity.Success);
                detailsText.Text = "No update was applied because the app is already on the latest version.";
            }
            catch (Exception ex)
            {
                detailsText.Text = ex.Message;
                SetFinished($"Update failed: {ex.Message}", InfoBarSeverity.Error);
            }
        }

        async Task RunRollbackAsync()
        {
            try
            {
                SaveState();
                SetBusy(true, "Rolling back to the previous version...");
                detailsText.Text = "Restoring the previous portable backup. Your app state and Data folder are kept.";
                var result = await GitAutoUpdater.RollbackAsync(GetConfiguredRepositoryPath(), _lastUpdatePreviousHead);
                SaveState();
                lastCheck = null;
                SetFinished("Rollback started. Proxy Sentry CSV will close and restart.", InfoBarSeverity.Success);
                detailsText.Text = "The updater is restoring the previous backup.";
                await Task.Delay(800);
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                detailsText.Text = ex.Message;
                SetFinished($"Rollback failed: {ex.Message}", InfoBarSeverity.Error);
            }
        }

        checkButton.Click += async (_, _) =>
        {
            if (!busy)
            {
                await RunCheckAsync();
            }
        };
        updateButton.Click += async (_, _) =>
        {
            if (!busy)
            {
                await RunUpdateAsync();
            }
        };
        rollbackButton.Click += async (_, _) =>
        {
            if (!busy)
            {
                await RunRollbackAsync();
            }
        };

        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        statusRow.Children.Add(progressRing);
        statusRow.Children.Add(statusText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        buttons.Children.Add(checkButton);
        buttons.Children.Add(updateButton);
        buttons.Children.Add(rollbackButton);

        var panel = new StackPanel
        {
            Width = 520,
            Spacing = 12
        };
        panel.Children.Add(currentVersionText);
        panel.Children.Add(availableVersionText);
        panel.Children.Add(statusRow);
        panel.Children.Add(detailsText);
        panel.Children.Add(buttons);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Updates",
            Content = panel,
            CloseButtonText = "Close"
        };
        dialog.Opened += (_, _) => _ = RunCheckAsync();
        await dialog.ShowAsync();
        SaveState();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var adsPowerBox = new CheckBox { Content = "AdsPower-compatible API", IsChecked = _adsPowerApiEnabled };
        var adsPowerPortBox = new TextBox
        {
            Header = "API port", Text = _adsPowerApiPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Width = 180, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 5
        };
        var adsPowerPortError = new TextBlock { Text = "Enter a whole port number from 1 to 65535.", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var adsPowerStatus = new TextBlock { Text = AdsPowerApiStatus, TextWrapping = TextWrapping.Wrap };
        var adsPowerHint = new TextBlock
        {
            Text = "Chrome profiles marked Select for Event Sentry appear in the Inventory Etix (DO NOT TOUCH) group. The API is local-only. Event Sentry bridge acquisition is paused while this mode is enabled. The client must use the same port. Default: 50325.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.82
        };
        var endpointBox = new TextBox
        {
            Header = "Jancy Web Interface",
            Text = _jancyEndpointUrl,
            PlaceholderText = "http://127.0.0.1:43033",
            Width = 520
        };
        var installBridgeButton = new Button { Content = "Install Jancy bridge" };
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
        installBridgeButton.Click += (_, _) =>
        {
            try
            {
                var bridgePath = _jancyLauncher.InstallBridge();
                var targetUrl = ResolveTargetUrlForBrowserAction() ?? "";
                var profilePath = _jancyLauncher.ExportProfiles(GetAllLoadedProxies(), targetUrl);
                ShowMessage($"Installed Jancy bridge. Exported profiles: {profilePath}", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message, InfoBarSeverity.Error);
            }
        };

        var panel = new StackPanel
        {
            Spacing = 10,
            Width = 520
        };
        panel.Children.Add(endpointBox);
        panel.Children.Add(installBridgeButton);
        panel.Children.Add(defaultBrowserBox);
        panel.Children.Add(emailButtonPanel);
        panel.Children.Add(adsPowerBox);
        panel.Children.Add(adsPowerPortBox);
        panel.Children.Add(adsPowerPortError);
        panel.Children.Add(adsPowerStatus);
        panel.Children.Add(adsPowerHint);

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            Width = 560,
            MaxHeight = 560,
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

        var requestedAdsPort = _adsPowerApiPort;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var valid = int.TryParse(adsPowerPortBox.Text, out requestedAdsPort) && requestedAdsPort is >= 1 and <= 65535;
            adsPowerPortError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            args.Cancel = !valid;
            if (!valid) adsPowerPortBox.Focus(FocusState.Programmatic);
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        _jancyEndpointUrl = string.IsNullOrWhiteSpace(endpointBox.Text)
            ? "http://127.0.0.1:43033"
            : endpointBox.Text.Trim();
        _autoUpdateEnabled = false;
        _defaultBrowserProvider = NormalizeBrowserProvider((defaultBrowserBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        _showChromeEmailButton = chromeEmailButtonBox.IsChecked == true;
        _showEdgeEmailButton = edgeEmailButtonBox.IsChecked == true;
        _showFirefoxEmailButton = firefoxEmailButtonBox.IsChecked == true;
        await ConfigureAdsPowerApiAsync(adsPowerBox.IsChecked == true, requestedAdsPort);
        ApplyBrowserEmailButtonSettings();
        _gitRepositoryPath = GetConfiguredRepositoryPath();
        SaveState();
    }

    private async Task RunAutoUpdateOnStartupAsync()
    {
        await Task.CompletedTask;
    }

    private string GetConfiguredRepositoryPath()
    {
        _gitRepositoryPath = GitAutoUpdater.DetectRepositoryPath();
        return _gitRepositoryPath;
    }

    private static string GetCurrentAppVersion()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        if (version is null)
        {
            return "unknown";
        }

        var parts = new[] { version.Major, version.Minor, version.Build }
            .Where(part => part >= 0)
            .ToArray();
        return parts.Length == 0 ? "unknown" : string.Join('.', parts);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _inspectorTimer?.Stop();
        _diagnosticCancellation?.Cancel();
        _checkAllCancellation?.Cancel();
        CaptureRecoverySession();
        SaveState();
        _workspaceReady = false;
        if (_adsPowerApi is not null) _ = _adsPowerApi.StopAsync();
        _eventSentryBridge.Dispose();
        _systemBrowserLauncher.Dispose();
    }

    private static string GetAppDataPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("PROXY_SENTRY_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath) && Path.IsPathFullyQualified(overridePath))
            return Path.GetFullPath(overridePath);
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
