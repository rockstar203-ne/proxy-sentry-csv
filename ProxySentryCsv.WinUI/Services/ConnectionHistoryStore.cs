using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed record ConnectionHistoryEntry(DateTimeOffset Time, string ProfileId, string Browser,
    string BrowserVersion, string Site, string Route, ProxyCheckResult Result);

public sealed class ConnectionHistoryStore
{
    private readonly string _path;
    private static readonly object Gate = new();
    public ConnectionHistoryStore(string appDataPath) => _path = Path.Combine(appDataPath, "connection-history.json");
    public List<ConnectionHistoryEntry> Read(string? profileId = null)
    {
        lock (Gate)
            return (AtomicJsonFile.Read<List<ConnectionHistoryEntry>>(_path) ?? [])
                .Where(e => profileId is null || e.ProfileId == profileId).ToList();
    }
    public void Add(ProxyEntry proxy, string version, Uri target, ProxyRouteMode route, ProxyCheckResult result)
    {
        lock (Gate)
        {
            var entries = Read();
            // Never persist URL queries, email, proxy credentials, headers or page content.
            entries.Insert(0, new(result.CheckedAtUtc, proxy.StableId, proxy.BrowserProvider,
                version, new UriBuilder(target.Scheme, target.Host, target.Port).Uri.GetLeftPart(UriPartial.Authority), route.ToString(), result));
            AtomicJsonFile.Write(_path, entries.Take(1000).ToList());
        }
    }
}
