using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ProxySentryCsv.Models;

public sealed class BrowserExtensionEntry : INotifyPropertyChanged
{
    private bool _enabled = true;
    private bool _useChrome = true;
    private bool _useEdge = true;
    private bool _useFirefox = true;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string UnpackedPath { get; set; } = "";
    public string PackagePath { get; set; } = "";
    public string FirefoxExtensionId { get; set; } = "";
    public string SourceKind { get; set; } = "";

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public bool UseChrome
    {
        get => _useChrome;
        set => SetField(ref _useChrome, value);
    }

    public bool UseEdge
    {
        get => _useEdge;
        set => SetField(ref _useEdge, value);
    }

    public bool UseFirefox
    {
        get => _useFirefox;
        set => SetField(ref _useFirefox, value);
    }

    public bool SupportsChromium => !string.IsNullOrWhiteSpace(UnpackedPath) && Directory.Exists(UnpackedPath);

    public bool SupportsChrome => SupportsChromium
        && !IsFirefoxOnlyPackage
        && !IsEdgeStorePackage;

    public bool SupportsEdge => SupportsChromium
        && !IsFirefoxOnlyPackage
        && !IsChromeStorePackage;

    public bool SupportsFirefox => !string.IsNullOrWhiteSpace(PackagePath)
        && File.Exists(PackagePath)
        && !string.IsNullOrWhiteSpace(FirefoxExtensionId);

    private bool IsFirefoxOnlyPackage => SourceKind.Contains("firefox", StringComparison.OrdinalIgnoreCase)
        || string.Equals(SourceKind, "xpi", StringComparison.OrdinalIgnoreCase);

    private bool IsChromeStorePackage => SourceKind.Contains("chrome store", StringComparison.OrdinalIgnoreCase);

    private bool IsEdgeStorePackage => SourceKind.Contains("edge store", StringComparison.OrdinalIgnoreCase);

    public string SupportText
    {
        get
        {
            var browsers = new List<string>();
            if (SupportsChrome)
            {
                browsers.Add("Chrome");
            }

            if (SupportsEdge)
            {
                browsers.Add("Edge");
            }

            if (SupportsFirefox)
            {
                browsers.Add("Firefox");
            }

            return browsers.Count == 0
                ? "No supported package found"
                : string.Join(", ", browsers);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Name) ? Id : Name;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
