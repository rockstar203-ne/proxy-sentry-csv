using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ProxySentryCsv.Models;

public sealed class TargetUrlListEntry : INotifyPropertyChanged
{
    private int _currentIndex;

    public TargetUrlListEntry(string key, string name, string path, IReadOnlyList<string> urls, int currentIndex = 0)
    {
        Key = key;
        Name = name;
        Path = path;
        Urls = urls.ToList();
        CurrentIndex = currentIndex;
    }

    public string Key { get; }
    public string Name { get; }
    public string Path { get; }
    public List<string> Urls { get; }
    public int Count => Urls.Count;
    public int UsedCount => Math.Clamp(CurrentIndex, 0, Count);
    public bool IsExhausted => Count == 0 || CurrentIndex >= Count;
    public string CurrentUrl => IsExhausted ? "" : Urls[CurrentIndex];
    public string ProgressText => Count == 0 ? "0 / 0" : $"{Math.Min(CurrentIndex + 1, Count)} / {Count}";
    public string DisplayName => $"{Name} ({ProgressText})";

    public int CurrentIndex
    {
        get => _currentIndex;
        set
        {
            var next = Math.Clamp(value, 0, Count);
            if (_currentIndex == next)
            {
                return;
            }

            _currentIndex = next;
            NotifyProgressChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void MovePrevious()
    {
        CurrentIndex = Math.Max(0, CurrentIndex - 1);
    }

    public void MoveNext()
    {
        CurrentIndex = Math.Min(Count, CurrentIndex + 1);
    }

    public void ResetProgress()
    {
        CurrentIndex = 0;
    }

    public void NotifyProgressChanged()
    {
        OnPropertyChanged(nameof(CurrentIndex));
        OnPropertyChanged(nameof(UsedCount));
        OnPropertyChanged(nameof(IsExhausted));
        OnPropertyChanged(nameof(CurrentUrl));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(DisplayName));
    }

    public override string ToString()
    {
        return DisplayName;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
