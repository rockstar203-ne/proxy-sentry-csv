using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class ProxySpeedChecker
{
    private static readonly Uri DefaultTestUri = new("https://www.gstatic.com/generate_204");

    public async Task<ProxyCheckResult> CheckAsync(ProxyEntry proxy, Uri? targetUri, CancellationToken cancellationToken)
    {
        var pingMs = await MeasureTcpConnectAsync(proxy, cancellationToken);
        var responseMs = await MeasureProxyResponseAsync(proxy, targetUri ?? DefaultTestUri, cancellationToken);
        var group = Classify(pingMs, responseMs);

        return new ProxyCheckResult(
            PingMs: pingMs,
            ResponseMs: responseMs,
            SpeedGroup: group,
            Status: group == "Dead" ? "Dead" : "Checked");
    }

    private static async Task<int?> MeasureTcpConnectAsync(ProxyEntry proxy, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();
            await client.ConnectAsync(proxy.Host, proxy.Port, timeout.Token);
            stopwatch.Stop();
            return (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int?> MeasureProxyResponseAsync(ProxyEntry proxy, Uri testUri, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var proxyUri = new Uri($"{proxy.Scheme.ToLowerInvariant()}://{proxy.Host}:{proxy.Port}");
            var webProxy = new WebProxy(proxyUri);
            if (!string.IsNullOrWhiteSpace(proxy.Username))
            {
                webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
            }

            using var handler = new HttpClientHandler
            {
                Proxy = webProxy,
                UseProxy = true,
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, testUri);

            var stopwatch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            stopwatch.Stop();

            return response.IsSuccessStatusCode || (int)response.StatusCode is >= 200 and < 400
                ? (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Classify(int? pingMs, int? responseMs)
    {
        if (pingMs is null && responseMs is null)
        {
            return "Dead";
        }

        if (responseMs is null)
        {
            return pingMs <= 800 ? "Slow" : "Dead";
        }

        return responseMs.Value switch
        {
            <= 1500 => "Fast",
            <= 4000 => "Good",
            _ => "Slow"
        };
    }
}

public sealed record ProxyCheckResult(int? PingMs, int? ResponseMs, string SpeedGroup, string Status);
