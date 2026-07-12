using Microsoft.UI.Xaml;

namespace ProxySentryCsv;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Title = "Proxy Sentry CSV";
        _window.Activate();
    }
}
