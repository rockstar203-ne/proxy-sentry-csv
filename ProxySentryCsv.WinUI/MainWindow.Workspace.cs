using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProxySentryCsv.Models;
using ProxySentryCsv.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ProxySentryCsv;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _inspectorTimer;
    private CancellationTokenSource? _diagnosticCancellation;
    private ProxyEntry? _inspectorProfile;
    private TextBlock? _profileFacts;
    private TextBlock? _profileName;
    private TextBlock? _profileConnection;
    private TextBlock? _profileDiagnostic;
    private TextBlock? _profileHistory;
    private Button? _profileRoute;
    private Button? _profileCheck;
    private Button? _profileCancel;
    private Button? _profileOpen;
    private ProgressRing? _profileProgress;
    private string _recoverySignature = "";
    private bool _workspaceReady;
    private bool _restoringWorkspace;

    private void InitializeWorkspace()
    {
        _workspaceReady = true;
        _inspectorTimer = DispatcherQueue.CreateTimer();
        _inspectorTimer.Interval = TimeSpan.FromSeconds(1);
        _inspectorTimer.Tick += (_, _) => RefreshConnectionStats();
        _inspectorTimer.Start();
    }

    private async Task OpenWorkspaceProfileAsync(ProxyEntry proxy, string target, SyncWindowPlacement? placement = null, bool enableSync = false)
    {
        if (proxy.IsBusy || proxy.IsOpen) return;
        proxy.IsBusy = true;
        proxy.Status = "Opening";
        try
        {
            var provider = NormalizeBrowserProvider(proxy.BrowserProvider);
            if (provider == "jancy")
            {
                _jancyLauncher.ExportProfiles(GetAllLoadedProxies(), target);
                await _jancyLauncher.LaunchAsync(proxy, target, _jancyEndpointUrl);
            }
            else
            {
                await _systemBrowserLauncher.LaunchAsync(proxy, target, provider,
                    status => DispatcherQueue.TryEnqueue(() => ShowMessage(status, InfoBarSeverity.Informational)), placement, enableSync);
            }
            proxy.OpenedProvider = provider;
            proxy.Used = true;
            proxy.IsOpen = true;
            proxy.IsDirect = false;
            proxy.Status = $"{GetBrowserDisplayName(provider)} opened";
        }
        catch
        {
            proxy.IsOpen = false;
            proxy.OpenedProvider = "";
            proxy.Status = "Open failed";
            throw;
        }
        finally { proxy.IsBusy = false; }
    }

    private void ToggleProfileInspector_Click(object sender, RoutedEventArgs e)
    {
        ProfileInspectorHost.Visibility = ProfileInspectorToggle.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshProfileInspector();
    }

    private static TextBlock DetailText(string text = "") =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    private void RefreshProfileInspector()
    {
        if (!_workspaceReady || ProfileInspectorHost.Visibility != Visibility.Visible) return;
        var proxy = ProxyList.SelectedItem as ProxyEntry ?? GetSelectedProxies().LastOrDefault();
        if (proxy == _inspectorProfile && ProfileInspectorHost.Child is not null)
        {
            RefreshConnectionStats();
            return;
        }
        _diagnosticCancellation?.Cancel();
        _inspectorProfile = proxy;
        var content = new StackPanel { Spacing = 14 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Profile details", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var close = new Button { Content = new SymbolIcon(Symbol.Cancel), Width = 32, Height = 32, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(close, "Close profile details");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "Close profile details");
        Grid.SetColumn(close, 1);
        close.Click += (_, _) => { ProfileInspectorToggle.IsChecked = false; ProfileInspectorHost.Visibility = Visibility.Collapsed; };
        header.Children.Add(close);
        content.Children.Add(header);
        ProfileInspectorHost.Child = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (proxy is null) { content.Children.Add(DetailText("No profile selected")); return; }
        _profileName = DetailText(proxy.DisplayName);
        _profileName.FontSize = 16;
        _profileName.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AddProfileMetadataControls(content, proxy);
        _profileFacts = DetailText();
        _profileFacts.FontSize = 12;
        content.Children.Add(_profileFacts);
        _profileOpen = new Button { Content = proxy.OpenButtonText, DataContext = proxy, HorizontalAlignment = HorizontalAlignment.Stretch };
        _profileOpen.Click += OpenProxy_Click;
        content.Children.Add(_profileOpen);
        AddProfileDataControls(content, proxy);
        AddProfileRegionControls(content, proxy);
        _profileConnection = DetailText();
        content.Children.Add(_profileConnection);
        _profileRoute = new Button { HorizontalAlignment = HorizontalAlignment.Stretch };
        _profileRoute.Click += (_, _) =>
        {
            try
            {
                var snapshot = _systemBrowserLauncher.GetConnectionSnapshot(proxy);
                if (snapshot is null) return;
                var next = snapshot.Route == ProxyRouteMode.Proxy ? ProxyRouteMode.Direct : ProxyRouteMode.Proxy;
                _systemBrowserLauncher.SetConnectionRoute(proxy, next);
                proxy.IsDirect = next == ProxyRouteMode.Direct;
                ShowMessage(next == ProxyRouteMode.Direct ? "Connection route: Direct." : "Connection route: Proxy.", InfoBarSeverity.Informational);
                RefreshConnectionStats();
            }
            catch (Exception ex) { ShowMessage(ex.Message, InfoBarSeverity.Error); }
        };
        content.Children.Add(_profileRoute);
        _profileProgress = new ProgressRing { Width = 22, Height = 22, IsActive = false, Visibility = Visibility.Collapsed };
        content.Children.Add(_profileProgress);
        _profileCheck = new Button { Content = "Check target connection", HorizontalAlignment = HorizontalAlignment.Stretch };
        _profileCheck.Click += async (_, _) => await DiagnoseProfileAsync(proxy);
        content.Children.Add(_profileCheck);
        _profileCancel = new Button { Content = "Cancel check", Visibility = Visibility.Collapsed };
        _profileCancel.Click += (_, _) => _diagnosticCancellation?.Cancel();
        content.Children.Add(_profileCancel);
        _profileDiagnostic = DetailText();
        content.Children.Add(_profileDiagnostic);
        content.Children.Add(new TextBlock { Text = "Recent checks", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        _profileHistory = DetailText();
        content.Children.Add(_profileHistory);
        RefreshConnectionStats();
        RefreshConnectionHistory();
    }

    private void RefreshConnectionStats()
    {
        var proxy = _inspectorProfile;
        if (ProfileInspectorHost.Visibility != Visibility.Visible || proxy is null || _profileFacts is null) return;
        var provider = string.IsNullOrWhiteSpace(proxy.OpenedProvider) ? proxy.BrowserProvider : proxy.OpenedProvider;
        _profileName!.Text = proxy.DisplayName;
        _profileFacts.Text = $"{GetBrowserDisplayName(provider)}\n{proxy.ProxySummary}\n{proxy.RegionText}\nID: {proxy.ProfileKey}";
        var snapshot = _systemBrowserLauncher.GetConnectionSnapshot(proxy);
        proxy.IsDirect = snapshot?.Route == ProxyRouteMode.Direct;
        _profileOpen!.Content = proxy.OpenButtonText;
        _profileOpen.IsEnabled = !proxy.IsBusy;
        RefreshProfileDataActions(proxy, provider);
        _profileRoute!.IsEnabled = snapshot is not null && !proxy.IsBusy;
        _profileRoute.Content = proxy.IsDirect ? "Restore proxy" : "Temporarily use Direct";
        _profileConnection!.Text = snapshot is null ? (provider == "jancy" ? "Connection controls are unavailable for Jancy." : "Profile is closed.")
            : $"Route: {snapshot.Route}{(proxy.IsDirect ? " - real IP" : "")}\nConnections: {snapshot.Connections}\nSent: {BrowserCacheService.FormatBytes(snapshot.UploadedBytes)}\nReceived: {BrowserCacheService.FormatBytes(snapshot.DownloadedBytes)}\nTransport errors: {snapshot.Failures}" +
              (snapshot.LastError.Length > 0 ? $"\nLast error: {snapshot.LastError}" : "");
        _profileCheck!.IsEnabled = provider != "jancy" && !proxy.IsBusy && _diagnosticCancellation is null;
        var queue = _systemBrowserLauncher.GetSyncQueueSnapshot(proxy);
        if (queue is not null)
            _profileConnection.Text += $"\nSync: {(queue.Paused ? "PAUSED" : "active")} | pending {queue.Pending} | lag {queue.LastLagMs:0} ms" +
                (queue.Error.Length > 0 ? $"\n{queue.Error}" : "");
    }

    private async Task DiagnoseProfileAsync(ProxyEntry proxy)
    {
        if (_diagnosticCancellation is not null) return;
        var target = ResolveTargetUrlForBrowserAction();
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        using var cancellation = new CancellationTokenSource();
        _diagnosticCancellation = cancellation;
        var text = _profileDiagnostic!;
        var progress = _profileProgress!;
        var button = _profileCheck!;
        var cancel = _profileCancel!;
        cancel.Visibility = Visibility.Visible;
        button.IsEnabled = false;
        progress.IsActive = true;
        progress.Visibility = Visibility.Visible;
        text.Text = "Checking target HTTP response...";
        try
        {
            var route = _systemBrowserLauncher.GetConnectionSnapshot(proxy)?.Route ?? ProxyRouteMode.Proxy;
            var result = await _systemBrowserLauncher.CheckConnectionAsync(proxy, uri, cancellation.Token);
            var history = new ConnectionHistoryStore(_appDataPath);
            await Task.Run(() => history.Add(proxy, _systemBrowserLauncher.GetInstalledVersion(proxy.BrowserProvider), uri, route, result));
            text.Text = $"{result.Status}: {result.ResultKind}\n{result.Detail}" +
                (result.RetryAfterUtc is { } retry ? $"\nRetry after: {retry.ToLocalTime():g}" : "");
            proxy.PingMs = result.PingMs;
            proxy.ResponseMs = result.ResponseMs;
            proxy.SpeedGroup = result.SpeedGroup;
            SaveState();
            RefreshConnectionHistory();
        }
        catch (OperationCanceledException) { text.Text = "Check cancelled."; }
        catch (Exception ex) { text.Text = $"Check failed: {ex.Message}"; }
        finally
        {
            _diagnosticCancellation = null;
            progress.IsActive = false;
            progress.Visibility = Visibility.Collapsed;
            button.IsEnabled = true;
            cancel.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshConnectionHistory()
    {
        if (_inspectorProfile is null || _profileHistory is null) return;
        var entries = new ConnectionHistoryStore(_appDataPath).Read(_inspectorProfile.StableId).Take(8);
        _profileHistory.Text = string.Join("\n\n", entries.Select(item =>
            $"{item.Time.ToLocalTime():g} | {item.Route}\n{item.Site}\n{item.Result.Status} | {item.Result.ResultKind}"));
        if (_profileHistory.Text.Length == 0) _profileHistory.Text = "No checks recorded";
    }

    private WorkspaceSession CreateWorkspaceSession(string name, IEnumerable<ProxyEntry> profiles)
    {
        var list = TargetUrlListBox.SelectedItem as TargetUrlListEntry;
        return new WorkspaceSession
        {
            Name = name, TargetUrl = TargetUrlBox.Text, MonitorId = (SyncMonitorBox.SelectedItem as SyncMonitorOption)?.Id,
            MasterId = _lastChromeSyncStableIds.FirstOrDefault(), UrlListKey = _useTargetUrlList ? list?.Key : null,
            UrlIndex = list?.CurrentIndex ?? 0,
            Profiles = profiles.Select(p => new WorkspaceProfile(p.StableId, string.IsNullOrWhiteSpace(p.OpenedProvider) ? p.BrowserProvider : p.OpenedProvider)).ToList()
        };
    }

    private void CaptureRecoverySession()
    {
        if (!_workspaceReady || _restoringWorkspace) return;
        var profiles = GetAllLoadedProxies().Where(p => p.IsOpen).ToList();
        if (profiles.Count == 0) return;
        var session = CreateWorkspaceSession("Last open workspace", profiles);
        var signature = JsonSerializer.Serialize(new { session.TargetUrl, session.MonitorId, session.MasterId, session.UrlListKey, session.UrlIndex, session.Profiles });
        if (_recoverySignature == signature) return;
        session.Id = "recovery";
        try { new WorkspaceSessionStore(_appDataPath).Save(session); _recoverySignature = signature; }
        catch (IOException) { }
    }

    private async void SaveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var profiles = GetAllLoadedProxies().Where(p => p.SyncSelected).ToList();
        if (profiles.Count == 0) profiles = GetAllLoadedProxies().Where(p => p.IsOpen).ToList();
        if (profiles.Count == 0) { ShowMessage("Select profiles or open a workspace first.", InfoBarSeverity.Warning); return; }
        var name = new TextBox { Header = "Session name", Text = $"Workspace {DateTime.Now:g}", MaxLength = 80 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"Save {profiles.Count} profiles", Content = name, PrimaryButtonText = "Save", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text)) return;
        try
        {
            new WorkspaceSessionStore(_appDataPath).Save(CreateWorkspaceSession(name.Text.Trim(), profiles));
            ShowMessage("Workspace session saved. Browser cookies stay in their existing profiles.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage(ex.Message, InfoBarSeverity.Error); }
    }

    private async void RestoreWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_restoringWorkspace) return;
        var sessions = new WorkspaceSessionStore(_appDataPath).Read();
        var choice = new ComboBox { ItemsSource = sessions, DisplayMemberPath = "Name", SelectedIndex = sessions.Count > 0 ? 0 : -1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Restore workspace", Content = choice,
            PrimaryButtonText = "Restore", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = sessions.Count > 0
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || choice.SelectedItem is not WorkspaceSession session) return;
        if (!Uri.TryCreate(session.TargetUrl, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
        { ShowMessage("Saved workspace contains an invalid Target URL.", InfoBarSeverity.Warning); return; }
        _restoringWorkspace = true;
        _systemBrowserLauncher.StopChromeSync();
        var opened = 0;
        var missing = 0;
        var errors = 0;
        try
        {
            SelectSyncMonitor(session.MonitorId);
            TargetUrlBox.Text = session.TargetUrl;
            var list = TargetUrlLists.FirstOrDefault(item => item.Key == session.UrlListKey);
            if (list is not null)
            {
                TargetUrlListBox.SelectedItem = list;
                list.CurrentIndex = session.UrlIndex;
                SelectTargetUrlMode(true);
                SyncTargetUrlBoxFromActiveList();
            }
            else SelectTargetUrlMode(false);
            var ordered = session.Profiles.OrderByDescending(p => p.ProfileId == session.MasterId).ToList();
            var monitor = SyncMonitorBox.SelectedItem as SyncMonitorOption;
            var placements = monitor is null ? null : ChromeSyncController.CalculateWindowPlacements(ordered.Count, monitor);
            for (var index = 0; index < ordered.Count; index++)
            {
                var saved = ordered[index];
                var proxy = GetAllLoadedProxies().FirstOrDefault(p => p.StableId == saved.ProfileId);
                if (proxy is null) { missing++; continue; }
                proxy.SyncSelected = true;
                if (proxy.IsOpen) continue;
                proxy.BrowserProvider = saved.Browser;
                try
                {
                    await OpenWorkspaceProfileAsync(proxy, target.AbsoluteUri, placements?[index], enableSync: session.MasterId is not null);
                    opened++;
                }
                catch (Exception ex) { errors++; ShowMessage(ex.Message, InfoBarSeverity.Error); }
            }
            _lastChromeSyncStableIds = ordered.Where(p => p.Browser is "chrome" or "firefox").Select(p => p.ProfileId).ToList();
            SaveState();
            ShowMessage($"Workspace restored: {opened} opened, {missing} unavailable, {errors} failed. Sync remains stopped.",
                errors > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        finally { _restoringWorkspace = false; UpdateSummary(); RefreshProfileInspector(); }
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = $"ProxySentry-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}" };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("Diagnostic JSON", new List<string> { ".json" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            var history = new ConnectionHistoryStore(_appDataPath).Read();
            var aliases = history.Select(e => e.ProfileId).Distinct().Select((id, index) => (id, name: $"Profile {index + 1}")).ToDictionary(x => x.id, x => x.name);
            var report = new
            {
                AppVersion = GetCurrentAppVersion(), CreatedAtUtc = DateTimeOffset.UtcNow,
                Browsers = new[] { "chrome", "edge", "firefox" }.Select(p => new { Browser = p, Version = _systemBrowserLauncher.GetInstalledVersion(p) }),
                Checks = history.Select(item => new { item.Time, Profile = aliases[item.ProfileId], item.Browser, item.BrowserVersion, item.Route, item.Result }),
                Notice = "Proxy addresses, credentials, emails, URLs, cookies and machine paths are excluded."
            };
            await File.WriteAllTextAsync(file.Path, JsonSerializer.Serialize(report, AppJsonOptions.Indented));
            ShowMessage("Sanitized diagnostic report exported.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage(ex.Message, InfoBarSeverity.Error); }
    }
}
