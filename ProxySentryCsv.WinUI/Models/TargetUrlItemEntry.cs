namespace ProxySentryCsv.Models;

public sealed class TargetUrlItemEntry
{
    public TargetUrlItemEntry(int index, string url)
    {
        Index = index;
        Url = url;
    }

    public int Index { get; }
    public string Url { get; }
    public string DisplayName => $"{Index + 1}. {Url}";
}
