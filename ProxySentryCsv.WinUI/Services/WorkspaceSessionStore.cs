namespace ProxySentryCsv.Services;

public sealed record WorkspaceProfile(string ProfileId, string Browser);
public sealed class WorkspaceSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string TargetUrl { get; set; } = "";
    public string? MonitorId { get; set; }
    public string? MasterId { get; set; }
    public string? UrlListKey { get; set; }
    public int UrlIndex { get; set; }
    public List<WorkspaceProfile> Profiles { get; set; } = [];
}

public sealed class WorkspaceSessionStore(string appDataPath)
{
    private readonly string _path = Path.Combine(appDataPath, "workspace-sessions.json");
    public List<WorkspaceSession> Read() => AtomicJsonFile.Read<List<WorkspaceSession>>(_path) ?? [];
    public void Save(WorkspaceSession session)
    {
        var all = Read();
        all.RemoveAll(item => item.Id == session.Id);
        all.Insert(0, session);
        AtomicJsonFile.Write(_path, all.Take(30).ToList());
    }
}
