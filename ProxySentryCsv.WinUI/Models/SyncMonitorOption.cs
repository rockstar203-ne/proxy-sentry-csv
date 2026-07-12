namespace ProxySentryCsv.Models;

public sealed class SyncMonitorOption
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int AvailLeft { get; set; }
    public int AvailTop { get; set; }
    public int AvailWidth { get; set; }
    public int AvailHeight { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }
}
