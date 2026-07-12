using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ProxySentryCsv.Models;

public sealed class ProxyListEntry : INotifyPropertyChanged
{
    public ProxyListEntry(string key, string name, string? path = null, bool isFavorites = false)
    {
        Key = key;
        Name = name;
        Path = path;
        IsFavorites = isFavorites;
    }

    public string Key { get; }
    public string Name { get; }
    public string? Path { get; }
    public bool IsFavorites { get; }
    public ObservableCollection<ProxyEntry> Items { get; } = [];
    public int Count => Items.Count;
    public string CountText => IsFavorites ? $"{Count} saved" : $"{Count} proxies";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyCount()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
