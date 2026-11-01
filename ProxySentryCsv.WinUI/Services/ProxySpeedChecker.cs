using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class ProxySpeedChecker
{
    public static readonly Uri DefaultTestUri = new("https://www.gstatic.com/generate_204");

    public async Task<ProxyCheckResult> CheckAsync(ProxyEntry proxy, Uri? targetUri,
        CancellationToken cancellationToken, LocalProxyRelay? activeRelay = null)
    {
        var target = targetUri ?? DefaultTestUri;
        if (target.Scheme is not ("http" or "https")) throw new ArgumentException("Only HTTP and HTTPS URLs can be checked.");
        var ping = await MeasureTcpConnectAsync(proxy, cancellationToken);
        using var ownedRelay = activeRelay is null ? LocalProxyRelay.Start(proxy) : null;
        var relay = activeRelay ?? ownedRelay!;
        var failuresBefore = relay.Snapshot.Failures;
        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{relay.Port}"), UseProxy = true, AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var code = (int)response.StatusCode;
            var elapsed = (int)Math.Min(timer.ElapsedMilliseconds, int.MaxValue);
            var transportFailed = relay.Snapshot.Failures > failuresBefore;
            var kind = transportFailed ? "Transport" : code switch
            {
                407 => "Proxy authentication", 403 => "Site denied access", 429 => "Rate limited",
                >= 500 => "Server response", >= 400 => "HTTP error", _ => "Response received"
            };
            var retry = response.Headers.RetryAfter;
            return new ProxyCheckResult(ping, elapsed, transportFailed ? "Dead" : Classify(elapsed),
                transportFailed ? "Proxy error" : $"HTTP {code}")
            {
                HttpStatus = code, ResultKind = kind,
                Detail = transportFailed ? relay.Snapshot.LastError :
                    "HTTP response received. This check does not execute JavaScript or prove the page fully loads in a browser.",
                RetryAfterUtc = retry?.Date ?? (retry?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : null)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var snapshot = relay.Snapshot;
            var message = snapshot.Failures > failuresBefore ? snapshot.LastError :
                ex is OperationCanceledException ? "No response within 20 seconds." :
                ex is HttpRequestException requestError ? $"Network error: {requestError.HttpRequestError}." :
                "Connection failed before an HTTP response was received.";
            return new ProxyCheckResult(ping, null, "Dead", "Connection error")
            {
                ResultKind = ex is OperationCanceledException ? "Timeout" : "Transport",
                Detail = message
            };
        }
    }

    private static async Task<int?> MeasureTcpConnectAsync(ProxyEntry proxy, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            var timer = Stopwatch.StartNew();
            await client.ConnectAsync(proxy.Host, proxy.Port, timeout.Token);
            return (int)Math.Min(timer.ElapsedMilliseconds, int.MaxValue);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static string Classify(int responseMs) => responseMs switch { <= 1500 => "Fast", <= 4000 => "Good", _ => "Slow" };
}

public sealed record ProxyCheckResult(int? PingMs, int? ResponseMs, string SpeedGroup, string Status)
{
    public int? HttpStatus { get; init; }
    public string ResultKind { get; init; } = "";
    public string Detail { get; init; } = "";
    public DateTimeOffset? RetryAfterUtc { get; init; }
    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
