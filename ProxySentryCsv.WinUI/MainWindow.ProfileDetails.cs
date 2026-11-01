using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProxySentryCsv.Models;
using ProxySentryCsv.Services;
using Windows.ApplicationModel.DataTransfer;

namespace ProxySentryCsv;

public sealed partial class MainWindow
{
    private TextBlock? _profileLastUsed;
    private Button? _profileReset;
    private Button? _profileClearCache;
    private bool _profileDataBusy;

    private Button CopyDetailButton(Func<string> value, string label)
    {
        var button = new Button { Content = new SymbolIcon(Symbol.Copy), Width = 32, Height = 32, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => CopyProfileDetail(value(), label);
        return button;
    }

    private void CopyProfileDetail(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            var data = new DataPackage();
            data.SetText(value);
            Clipboard.SetContent(data);
            ShowMessage(label + ": copied.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage(ex.Message, InfoBarSeverity.Error); }
    }

    private void AddProfileMetadataControls(StackPanel content, ProxyEntry proxy)
    {
        var nameRow = new Grid { ColumnSpacing = 8 };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition());
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameRow.Children.Add(_profileName!);
        var copyName = CopyDetailButton(() => proxy.DisplayName, "Copy profile name");
        Grid.SetColumn(copyName, 1);
        nameRow.Children.Add(copyName);
        content.Children.Add(nameRow);

        var emailContent = new Grid { ColumnSpacing = 8 };
        emailContent.ColumnDefinitions.Add(new ColumnDefinition());
        emailContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        emailContent.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(proxy.Email) ? "No email in CSV" : proxy.Email, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var copyIcon = new SymbolIcon(Symbol.Copy) { Width = 18, Height = 18 };
        Grid.SetColumn(copyIcon, 1);
        emailContent.Children.Add(copyIcon);
        var email = new Button { Content = emailContent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = !string.IsNullOrWhiteSpace(proxy.Email) };
        ToolTipService.SetToolTip(email, "Copy email");
        email.Click += (_, _) => CopyProfileDetail(proxy.Email, "Email");
        content.Children.Add(email);

        var editor = new StackPanel { Spacing = 10 };
        var name = new TextBox { Header = "Name", Text = proxy.DisplayName, MaxLength = 200 };
        var tags = new TextBox { Header = "Tags (comma-separated)", Text = proxy.Tags, MaxLength = 500 };
        var notes = new TextBox { Header = "Notes", Text = proxy.Notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 88, MaxHeight = 180, MaxLength = 5000 };
        var result = DetailText();
        var save = new Button { Content = "Save details", HorizontalAlignment = HorizontalAlignment.Stretch };
        save.Click += (_, _) =>
        {
            try
            {
                proxy.DisplayName = name.Text.Trim();
                proxy.Tags = tags.Text;
                proxy.Notes = notes.Text;
                SaveState();
                tags.Text = proxy.Tags;
                result.Text = "Saved";
                RefreshConnectionStats();
            }
            catch (Exception ex) { result.Text = "Save failed: " + ex.Message; }
        };
        foreach (var child in new UIElement[] { name, tags, notes, save, result }) editor.Children.Add(child);
        var expander = new Expander { Header = "Name, tags and notes", Content = editor, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        expander.Expanding += (_, _) => { name.Text = proxy.DisplayName; tags.Text = proxy.Tags; notes.Text = proxy.Notes; result.Text = ""; };
        content.Children.Add(expander);
    }

    private static string ProfileDataProvider(ProxyEntry proxy) => NormalizeBrowserProvider(string.IsNullOrWhiteSpace(proxy.OpenedProvider) ? proxy.BrowserProvider : proxy.OpenedProvider);

    private void AddProfileRegionControls(StackPanel content, ProxyEntry proxy)
    {
        var body = new StackPanel { Spacing = 10 };
        var expected = DetailText();
        var result = DetailText();
        var check = new Button { Content = "Check open tabs", HorizontalAlignment = HorizontalAlignment.Stretch };
        var local = new Button { Content = "Copy local check URL", HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTipService.SetToolTip(local, "Paste this address into a tab of the browser profile to check its native locale, timezone and worker.");
        var progress = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
        void RefreshExpected()
        {
            var provider = ProfileDataProvider(proxy);
            var config = _browserForgeFingerprints.ReadConfiguration(proxy, provider);
            expected.Text = provider == "jancy" ? "Regional settings are managed by Jancy." : config is null ? "Open this profile after updating to create its regional configuration."
                : $"{config.Source}\nUser-Agent: native\nExpected timezone: {(config.TimeZone.Length == 0 ? "unknown" : config.TimeZone)}\nExpected language: {(config.Locale.Length == 0 ? "unknown" : config.Locale)}\nGeo source: {config.GeoSource}\nGeo checked: {(config.GeoCheckedAtUtc is { } checkedAt ? checkedAt.ToLocalTime().ToString("g") : "not recorded")}\n{config.GeoWarning}";
            check.IsEnabled = proxy.IsOpen && !proxy.IsBusy && provider is "chrome" or "edge";
            local.IsEnabled = config is not null && provider != "jancy";
        }
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            progress.IsActive = true;
            progress.Visibility = Visibility.Visible;
            result.Text = "Checking open tabs...";
            try { result.Text = await _systemBrowserLauncher.CheckBrowserRegionsAsync(proxy); }
            catch (Exception ex) { result.Text = "Check unavailable: " + ex.Message; }
            finally { progress.IsActive = false; progress.Visibility = Visibility.Collapsed; RefreshExpected(); }
        };
        local.Click += async (_, _) =>
        {
            try
            {
                var provider = ProfileDataProvider(proxy);
                var config = _browserForgeFingerprints.ReadConfiguration(proxy, provider);
                if (config is null) return;
                var folder = Path.Combine(_appDataPath, "Diagnostics");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, $"region-{proxy.ProfileKey}-{provider}.html");
                await File.WriteAllTextAsync(path, BrowserRegionDiagnostics.BuildLocalPage(config));
                CopyProfileDetail(new Uri(path).AbsoluteUri, "Local check URL");
            }
            catch (Exception ex) { result.Text = "Local check failed: " + ex.Message; }
        };
        foreach (var child in new UIElement[] { expected, check, local, progress, result }) body.Children.Add(child);
        var expander = new Expander { Header = "Regional settings", Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        expander.Expanding += (_, _) => RefreshExpected();
        content.Children.Add(expander);
        RefreshExpected();
    }

    private void AddProfileDataControls(StackPanel content, ProxyEntry proxy)
    {
        var body = new StackPanel { Spacing = 10 };
        var size = DetailText("Not measured");
        _profileLastUsed = DetailText();
        body.Children.Add(size);
        body.Children.Add(_profileLastUsed);
        var progress = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
        var refresh = new Button { Content = new SymbolIcon(Symbol.Refresh), Width = 32, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(refresh, "Refresh profile storage size");
        body.Children.Add(refresh);
        body.Children.Add(progress);
        var measureBusy = false;
        async Task MeasureAsync()
        {
            if (measureBusy) return;
            var provider = ProfileDataProvider(proxy);
            if (provider == "jancy") { size.Text = "Jancy data is managed by Jancy."; return; }
            measureBusy = true;
            refresh.IsEnabled = false;
            progress.IsActive = true;
            progress.Visibility = Visibility.Visible;
            try
            {
                var usage = await _browserCache.GetProfileStorageAsync(proxy, provider);
                size.Text = $"{GetBrowserDisplayName(provider)}\nCache: {BrowserCacheService.FormatBytes(usage.CacheBytes)}\nProfile data: {BrowserCacheService.FormatBytes(usage.TotalBytes)}";
            }
            catch (Exception ex) { size.Text = "Storage check failed: " + ex.Message; }
            finally { measureBusy = false; refresh.IsEnabled = true; progress.IsActive = false; progress.Visibility = Visibility.Collapsed; }
        }
        refresh.Click += async (_, _) => await MeasureAsync();
        var clear = _profileClearCache = new Button { Content = "Clear cache only", HorizontalAlignment = HorizontalAlignment.Stretch };
        var reset = _profileReset = new Button { Content = "Reset browser profile", HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(clear);
        body.Children.Add(reset);
        var operationResult = DetailText();
        body.Children.Add(operationResult);
        async Task OperateAsync(bool fullReset)
        {
            if (_profileDataBusy || proxy.IsBusy) return;
            var provider = ProfileDataProvider(proxy);
            if (provider == "jancy") return;
            if (fullReset)
            {
                if (proxy.IsOpen) { operationResult.Text = "Close the profile before resetting."; return; }
                var dialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, Title = $"Reset {GetBrowserDisplayName(provider)} profile?",
                    Content = DetailText($"{proxy.DisplayName}\n\nCookies, saved sign-ins, history, site data and browser settings for this browser profile will be deleted. This cannot be undone.\n\nProxy, name, email, notes, tags and other browser profiles stay unchanged."),
                    PrimaryButtonText = "Reset profile", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                if (_profileDataBusy || proxy.IsBusy || proxy.IsOpen) { operationResult.Text = "Profile state changed. Close it and try again."; return; }
            }
            _profileDataBusy = true;
            proxy.IsBusy = true;
            clear.IsEnabled = reset.IsEnabled = false;
            operationResult.Text = fullReset ? "Resetting profile..." : "Clearing cache...";
            try
            {
                if (fullReset)
                {
                    await _browserCache.ResetProfileAsync(proxy, provider, () => _systemBrowserLauncher.EnsureProfileStopped(proxy, provider));
                    operationResult.Text = "Browser profile reset.";
                }
                else
                {
                    var cleared = await _browserCache.ClearProfileCacheAsync(proxy, provider);
                    operationResult.Text = cleared.FailedItems == 0 ? "Cache cleared. Cookies and sign-ins preserved." : $"Cache partially cleared. Skipped locked items: {cleared.FailedItems}.";
                }
                await MeasureAsync();
                await RefreshCacheSizeAsync();
            }
            catch (Exception ex) { operationResult.Text = "Operation failed: " + ex.Message; }
            finally { _profileDataBusy = false; proxy.IsBusy = false; clear.IsEnabled = true; reset.IsEnabled = !proxy.IsOpen; RefreshConnectionStats(); }
        }
        clear.Click += async (_, _) => await OperateAsync(false);
        reset.Click += async (_, _) => await OperateAsync(true);
        var data = new Expander { Header = "Profile data", Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        data.Expanding += async (_, _) => await MeasureAsync();
        content.Children.Add(data);
    }

    private void RefreshProfileDataActions(ProxyEntry proxy, string provider)
    {
        if (_profileLastUsed is not null) _profileLastUsed.Text = proxy.LastUsedUtc is { } used ? $"Last opened: {used.ToLocalTime():g}" : "Last opened: not recorded";
        if (_profileClearCache is not null) _profileClearCache.IsEnabled = !_profileDataBusy && !proxy.IsBusy && provider != "jancy";
        if (_profileReset is not null) _profileReset.IsEnabled = !_profileDataBusy && !proxy.IsBusy && !proxy.IsOpen && provider != "jancy";
    }
}
