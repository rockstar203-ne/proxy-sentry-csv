using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProxySentryCsv.Services;
using Windows.ApplicationModel.DataTransfer;

namespace ProxySentryCsv;

public sealed partial class MainWindow
{
    private async void BridgeDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var report = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 280, MaxHeight = 420, HorizontalAlignment = HorizontalAlignment.Stretch, Text = "Checking local bridge..." };
        var retry = new Button { Content = "Test connection" };
        var panel = new StackPanel { Spacing = 10, MaxWidth = 500 };
        panel.Children.Add(retry); panel.Children.Add(report);
        async Task CheckAsync()
        {
            retry.IsEnabled = false;
            try
            {
                var result = await BridgeDiagnostics.ProbeAsync(EventSentryBridgeServer.PipeName, Environment.ProcessId);
                using var identity = WindowsIdentity.GetCurrent();
                var admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                report.Text = $"Bridge diagnostic report\nUTC: {DateTimeOffset.UtcNow:O}\nVersion: {GetCurrentAppVersion()}\nPID: {Environment.ProcessId}\nSession: {Process.GetCurrentProcess().SessionId}\nWindows user: {identity.Name}\nSID: {identity.User?.Value}\nElevated: {admin}\nExecutable: {Environment.ProcessPath}\nData: {_appDataPath}\n\n{_eventSentryBridge.DiagnosticSummary}\n\nResult: {result.Code}\n{result.Detail}\n\nEvent Sentry must use this exact pipe name, protocol 1, the same Windows account, and normally the same elevation. Zero selected profiles is not a missing bridge.\nNo proxy credentials, cookies or site URLs are included. The report contains local account and installation paths.";
            }
            catch (Exception ex) { report.Text = $"Diagnostic failed: {ex.GetType().Name}, 0x{ex.HResult:X8}"; }
            finally { retry.IsEnabled = true; }
        }
        retry.Click += async (_, _) => await CheckAsync();
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Event Sentry bridge diagnostics", Content = panel, CloseButtonText = "Close", SecondaryButtonText = "Copy report" };
        dialog.Opened += async (_, _) => await CheckAsync();
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            try { var data = new DataPackage(); data.SetText(report.Text); Clipboard.SetContent(data); }
            catch { report.Text += "\nClipboard is unavailable; select and copy the report text manually."; }
        };
        await dialog.ShowAsync();
    }
}
