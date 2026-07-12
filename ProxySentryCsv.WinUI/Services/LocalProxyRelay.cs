using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class LocalProxyRelay : IDisposable
{
    private const int HeaderLimitBytes = 64 * 1024;
    private readonly string _upstreamScheme;
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly string _username;
    private readonly string _password;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private bool _disposed;

    private LocalProxyRelay(ProxyEntry proxy)
    {
        _upstreamScheme = proxy.Scheme.Trim().ToLowerInvariant();
        _upstreamHost = proxy.Host;
        _upstreamPort = proxy.Port;
        _username = proxy.Username;
        _password = proxy.Password;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(128);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public static LocalProxyRelay Start(ProxyEntry proxy)
    {
        if (proxy.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("System browser proxy relay currently supports HTTP/HTTPS proxy endpoints. SOCKS proxies need direct browser support.");
        }

        return new LocalProxyRelay(proxy);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => HandleClientAsync(client, _stop.Token));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            TcpClient? upstream = null;
            try
            {
                client.NoDelay = true;
                var clientStream = client.GetStream();
                var header = await ReadHeaderAsync(clientStream, token);
                if (header.HeaderLength == 0)
                {
                    return;
                }

                upstream = new TcpClient { NoDelay = true };
                await upstream.ConnectAsync(_upstreamHost, _upstreamPort, token);
                await using var upstreamStream = upstream.GetStream();

                var headerText = Encoding.ASCII.GetString(header.Buffer, 0, header.HeaderLength);
                var rewrittenHeader = RewriteHeader(headerText);
                await upstreamStream.WriteAsync(Encoding.ASCII.GetBytes(rewrittenHeader), token);

                var extraLength = header.BytesRead - header.HeaderLength;
                if (extraLength > 0)
                {
                    await upstreamStream.WriteAsync(header.Buffer.AsMemory(header.HeaderLength, extraLength), token);
                }

                var upload = PumpAsync(clientStream, upstreamStream, token);
                var download = PumpAsync(upstreamStream, clientStream, token);
                await Task.WhenAny(upload, download);
            }
            catch
            {
            }
            finally
            {
                upstream?.Dispose();
            }
        }
    }

    private async Task<HeaderRead> ReadHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[HeaderLimitBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), token);
            if (read <= 0)
            {
                return new HeaderRead(buffer, total, 0);
            }

            total += read;
            var headerEnd = FindHeaderEnd(buffer, total);
            if (headerEnd > 0)
            {
                return new HeaderRead(buffer, total, headerEnd);
            }
        }

        return new HeaderRead(buffer, total, 0);
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var index = 3; index < length; index++)
        {
            if (buffer[index - 3] == '\r'
                && buffer[index - 2] == '\n'
                && buffer[index - 1] == '\r'
                && buffer[index] == '\n')
            {
                return index + 1;
            }
        }

        return 0;
    }

    private string RewriteHeader(string headerText)
    {
        var lines = headerText.Split(["\r\n"], StringSplitOptions.None);
        var builder = new StringBuilder();
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
        {
            return headerText;
        }

        builder.Append(lines[0]);
        builder.Append("\r\n");

        var authHeader = BuildProxyAuthorizationHeader();
        if (authHeader is not null)
        {
            builder.Append(authHeader);
            builder.Append("\r\n");
        }

        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Append(line);
            builder.Append("\r\n");
        }

        builder.Append("\r\n");
        return builder.ToString();
    }

    private string? BuildProxyAuthorizationHeader()
    {
        if (string.IsNullOrWhiteSpace(_username) && string.IsNullOrWhiteSpace(_password))
        {
            return null;
        }

        var raw = $"{_username}:{_password}";
        return "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static async Task PumpAsync(NetworkStream source, NetworkStream destination, CancellationToken token)
    {
        var buffer = new byte[81920];
        while (!token.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer, token);
            }
            catch
            {
                return;
            }

            if (read <= 0)
            {
                return;
            }

            try
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
            }
            catch
            {
                return;
            }
        }
    }

    private sealed record HeaderRead(byte[] Buffer, int BytesRead, int HeaderLength);
}
