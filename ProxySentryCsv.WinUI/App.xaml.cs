using System.Text;
using Microsoft.UI.Xaml;

namespace ProxySentryCsv;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
        UnhandledException += (_, args) =>
        {
            LogCrash("Application.UnhandledException", args.Exception);
            args.Handled = true;
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Title = "Proxy Sentry CSV";
            _window.Activate();
        }
        catch (Exception ex)
        {
            LogCrash("App.OnLaunched", ex);
            throw;
        }
    }

    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ProxySentryCSV");
            Directory.CreateDirectory(appData);
            var logPath = Path.Combine(appData, "crash.log");
            var builder = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}");
            builder.AppendLine(exception?.ToString() ?? "No exception object.");
            File.AppendAllText(logPath, builder.AppendLine().ToString(), Encoding.UTF8);
        }
        catch
        {
        }
    }
}
