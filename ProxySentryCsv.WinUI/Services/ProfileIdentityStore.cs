using System.Security.Cryptography;
using System.Text;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class ProfileIdentityStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public ProfileIdentityStore(string appDataPath) =>
        _path = Path.Combine(appDataPath, "profile-identities.json");

    public void Bind(IReadOnlyList<ProxyEntry> proxies, string sourcePath)
    {
        lock (_gate)
        {
            var entries = AtomicJsonFile.Read<List<Identity>>(_path) ?? [];
            var source = Hash(Path.GetFullPath(sourcePath).ToUpperInvariant());
            var counts = proxies.GroupBy(Endpoint).ToDictionary(g => g.Key, g => g.Select(p => p.CredentialKey).Distinct().Count());
            foreach (var proxy in proxies)
            {
                var credential = proxy.CredentialKey;
                var endpoint = Endpoint(proxy);
                var identity = entries.FirstOrDefault(e => e.Credentials.Contains(credential));
                if (identity is null && counts[endpoint] == 1)
                {
                    var matches = entries.Where(e => e.Endpoint == endpoint &&
                        e.Source == source && e.Row == proxy.RowNumber).ToList();
                    if (matches.Count == 1) identity = matches[0];
                }
                if (identity is null)
                {
                    // The initial ID is the legacy hash, so existing cookies stay in place.
                    identity = new Identity { Id = credential, Source = source, Row = proxy.RowNumber, Endpoint = endpoint };
                    entries.Add(identity);
                }
                if (identity.Id.Length != 64 || !identity.Id.All(Uri.IsHexDigit))
                    throw new IOException("Invalid profile identity index. Restore its backup before opening profiles.");
                identity.Credentials.Add(credential);
                proxy.ProfileId = identity.Id;
            }
            AtomicJsonFile.Write(_path, entries);
        }
    }

    private static string Endpoint(ProxyEntry proxy) =>
        Hash($"{proxy.Scheme.ToLowerInvariant()}|{proxy.Host.ToLowerInvariant()}|{proxy.Port}|{proxy.Username}");
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public sealed class Identity
    {
        public string Id { get; set; } = "";
        public string Source { get; set; } = "";
        public int Row { get; set; }
        public string Endpoint { get; set; } = "";
        public HashSet<string> Credentials { get; set; } = [];
    }
}
