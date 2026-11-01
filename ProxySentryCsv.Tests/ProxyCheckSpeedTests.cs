using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ProxySentryCsv.Models;
using ProxySentryCsv.Services;

internal static class ProxyCheckSpeedTests
{
    public static async Task RefusedAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var timer = Stopwatch.StartNew();
        var checker = new ProxySpeedChecker(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));
        var result = await checker.CheckAsync(new ProxyEntry { Host = "127.0.0.1", Port = port }, new Uri("http://site.invalid/"), CancellationToken.None);
        if (result.SpeedGroup != "Dead" || result.Status != "Proxy unreachable" || result.ResponseMs is not null
            || timer.ElapsedMilliseconds > 1500)
            throw new Exception("Refused TCP connection must not proceed to the HTTP timeout");
        Console.WriteLine($"  Refused proxy rejected in {timer.ElapsedMilliseconds} ms");
    }

    public static async Task SilentAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var connections = new List<TcpClient>();
        var accept = Task.Run(async () =>
        {
            try
            {
                while (true) connections.Add(await listener.AcceptTcpClientAsync(stop.Token));
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var timer = Stopwatch.StartNew();
            var checker = new ProxySpeedChecker(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250));
            var result = await checker.CheckAsync(new ProxyEntry { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port },
                new Uri("http://site.invalid/"), CancellationToken.None);
            if (result.PingMs is null || result.ResultKind != "Timeout" || result.SpeedGroup == "Dead" || timer.ElapsedMilliseconds > 2000)
                throw new Exception("Reachable but silent proxy must obey response budget without being called proven dead");
            Console.WriteLine($"  Silent response timed out in {timer.ElapsedMilliseconds} ms");
        }
        finally
        {
            stop.Cancel();
            await accept;
            listener.Stop();
            foreach (var client in connections) client.Dispose();
        }
    }
}
