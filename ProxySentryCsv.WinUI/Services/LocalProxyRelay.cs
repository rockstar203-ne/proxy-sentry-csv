using System.Net;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using MailKit.Net.Proxy;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public enum ProxyRouteMode { Proxy, Direct }

public sealed record RelaySnapshot(ProxyRouteMode Route, int Connections, long UploadedBytes,
    long DownloadedBytes, long Failures, string LastError);

public sealed class LocalProxyRelay : IDisposable
{
    private const int HeaderLimitBytes = 64 * 1024;
    private readonly string _scheme;
    private readonly string _host;
    private readonly int _port;
    private readonly NetworkCredential _credentials;
    private readonly Action<string>? _progress;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, TcpClient> _clients = new();
    private readonly object _routeGate = new();
    private CancellationTokenSource _routeStop = new();
    private readonly Task _acceptLoop;
    private ProxyRouteMode _route;
    private bool _disposed;
    private long _nextClient;
    private long _uploaded;
    private long _downloaded;
    private long _failures;
    private string _lastError = "";

    private LocalProxyRelay(ProxyEntry proxy, Action<string>? progress)
    {
        _scheme = proxy.Scheme.Trim().ToLowerInvariant();
        _host = proxy.Host;
        _port = proxy.Port;
        _credentials = new NetworkCredential(proxy.Username, proxy.Password);
        _progress = progress;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(128);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }
    public ProxyRouteMode Route { get { lock (_routeGate) return _route; } }
    public RelaySnapshot Snapshot => new(Route, _clients.Count, Interlocked.Read(ref _uploaded),
        Interlocked.Read(ref _downloaded), Interlocked.Read(ref _failures), _lastError);

    public static LocalProxyRelay Start(ProxyEntry proxy, Action<string>? progress = null)
    {
        if (proxy.Scheme.ToLowerInvariant() is not ("http" or "https" or "socks5"))
            throw new NotSupportedException("Supported proxy types: HTTP, HTTPS and SOCKS5.");
        if (string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port is < 1 or > 65535)
            throw new ArgumentException("Invalid proxy address or port.");
        return new LocalProxyRelay(proxy, progress);
    }

    public void SetRoute(ProxyRouteMode mode)
    {
        lock (_routeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_route == mode) return;
            _route = mode;
            // Existing tunnels must not retain the old route after a switch.
            _routeStop.Cancel();
            foreach (var client in _clients.Values) client.Dispose();
            _routeStop.Dispose();
            _routeStop = new CancellationTokenSource();
        }
        _progress?.Invoke(mode == ProxyRouteMode.Direct ? "Direct connection enabled. Real IP is exposed." : "Proxy connection restored.");
    }

    public void Dispose()
    {
        lock (_routeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _routeStop.Cancel();
            _listener.Stop();
            foreach (var client in _clients.Values) client.Dispose();
        }
        _ = _acceptLoop.ContinueWith(_ => { _stop.Dispose(); _routeStop.Dispose(); }, TaskScheduler.Default);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if (_clients.Count >= 256) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref _nextClient);
                CancellationTokenSource lifetime;
                ProxyRouteMode route;
                lock (_routeGate)
                {
                    if (_disposed) { client.Dispose(); return; }
                    route = _route;
                    lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _routeStop.Token);
                    _clients[id] = client;
                }
                _ = HandleClientAsync(id, client, route, lifetime);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            if (!_stop.IsCancellationRequested) RecordFailure(ex);
        }
    }

    private async Task HandleClientAsync(long id, TcpClient client, ProxyRouteMode route, CancellationTokenSource lifetime)
    {
        using (client)
        using (lifetime)
        {
            var token = lifetime.Token;
            var responseStarted = false;
            try
            {
                client.NoDelay = true;
                var downstream = client.GetStream();
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshake.CancelAfter(TimeSpan.FromSeconds(20));
                var header = await ReadHeaderAsync(downstream, handshake.Token).ConfigureAwait(false);
                var request = ParseRequest(header.Text);
                await using var upstream = await ConnectAsync(request.Host, request.Port, route, handshake.Token).ConfigureAwait(false);
                var throughHttpProxy = route == ProxyRouteMode.Proxy && _scheme != "socks5";
                if (request.IsConnect && !throughHttpProxy)
                {
                    await downstream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token).ConfigureAwait(false);
                    responseStarted = true;
                }
                else
                {
                    var rewritten = RewriteHeader(header.Text, request, throughHttpProxy);
                    await upstream.WriteAsync(Encoding.Latin1.GetBytes(rewritten), handshake.Token).ConfigureAwait(false);
                }
                if (header.Extra.Length > 0)
                    await upstream.WriteAsync(header.Extra, handshake.Token).ConfigureAwait(false);

                if (request.IsConnect && throughHttpProxy)
                {
                    var response = await ReadHeaderAsync(upstream, handshake.Token).ConfigureAwait(false);
                    var status = GetStatus(response.Text);
                    if (status == 407) throw new ProxyAuthenticationException();
                    await downstream.WriteAsync(Encoding.Latin1.GetBytes(response.Text), token).ConfigureAwait(false);
                    if (response.Extra.Length > 0) await downstream.WriteAsync(response.Extra, token).ConfigureAwait(false);
                    responseStarted = true;
                    if (status is < 200 or > 299) return;
                }

                // The site TLS stream remains opaque; only proxy handshakes are interpreted.
                using var pumping = CancellationTokenSource.CreateLinkedTokenSource(token);
                var upload = PumpAsync(downstream, upstream, true, pumping.Token);
                var download = request.IsConnect
                    ? PumpAsync(upstream, downstream, false, pumping.Token)
                    : PumpHttpResponseAsync(upstream, downstream, () => responseStarted = true, pumping.Token);
                try
                {
                    var completed = await Task.WhenAny(upload, download).ConfigureAwait(false);
                    // A client can half-close after sending a POST; its response still needs to drain.
                    if (completed == upload && !token.IsCancellationRequested)
                        await download.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    else
                        await download.ConfigureAwait(false);
                }
                finally
                {
                    pumping.Cancel();
                    await Task.WhenAll(upload, download).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    var error = RecordFailure(ex);
                    if (!responseStarted)
                    {
                        try
                        {
                            using var errorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                            await WriteErrorAsync(client.GetStream(), error, errorTimeout.Token).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
            }
            finally { _clients.TryRemove(id, out _); }
        }
    }

    private async Task<Stream> ConnectAsync(string targetHost, int targetPort, ProxyRouteMode route, CancellationToken token)
    {
        if (route == ProxyRouteMode.Proxy && _scheme == "socks5")
        {
            var socks = string.IsNullOrEmpty(_credentials.UserName)
                ? new Socks5Client(_host, _port)
                : new Socks5Client(_host, _port, _credentials);
            return await socks.ConnectAsync(targetHost, targetPort, token).ConfigureAwait(false);
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(route == ProxyRouteMode.Direct ? targetHost : _host,
                route == ProxyRouteMode.Direct ? targetPort : _port, token).ConfigureAwait(false);
            Stream stream = new NetworkStream(socket, ownsSocket: true);
            if (route == ProxyRouteMode.Proxy && _scheme == "https")
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                try
                {
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _host }, token).ConfigureAwait(false);
                    stream = tls;
                }
                catch (Exception ex)
                {
                    try { tls.Dispose(); } catch { }
                    if (ex is OperationCanceledException) throw;
                    throw new AuthenticationException("HTTPS proxy TLS handshake failed.", ex);
                }
            }
            return stream;
        }
        catch { socket.Dispose(); throw; }
    }

    private string RewriteHeader(string header, Request request, bool throughHttpProxy)
    {
        var lines = header.Split("\r\n", StringSplitOptions.None);
        var builder = new StringBuilder();
        builder.Append(throughHttpProxy || request.IsConnect ? lines[0] : $"{request.Method} {request.Path} HTTP/1.1").Append("\r\n");
        if (throughHttpProxy && !string.IsNullOrEmpty(_credentials.UserName))
            builder.Append("Proxy-Authorization: Basic ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_credentials.UserName}:{_credentials.Password}"))).Append("\r\n");
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) break;
            if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.IsConnect && !request.IsUpgrade && line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)) continue;
            builder.Append(line).Append("\r\n");
        }
        // Plain HTTP requests are independent; CONNECT and WebSocket streams stay persistent.
        if (!request.IsConnect && !request.IsUpgrade) builder.Append("Connection: close\r\n");
        return builder.Append("\r\n").ToString();
    }

    private static Request ParseRequest(string header)
    {
        var first = header.Split("\r\n", 2)[0].Split(' ', 3);
        if (first.Length != 3) throw new IOException("Invalid HTTP request.");
        var connect = first[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(connect ? "http://" + first[1] : first[1], UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "ws" or "wss") || uri.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new IOException("Invalid proxy destination.");
        return new Request(first[0], uri.Host.Trim('[', ']'), uri.Port, uri.PathAndQuery, connect,
            header.Contains("\r\nUpgrade:", StringComparison.OrdinalIgnoreCase));
    }

    private static int GetStatus(string header)
    {
        var parts = header.Split("\r\n", 2)[0].Split(' ', 3);
        return parts.Length > 1 && int.TryParse(parts[1], out var status) ? status : 502;
    }

    private static async Task<HeaderRead> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        var bytes = new byte[HeaderLimitBytes];
        var count = 0;
        while (count < bytes.Length)
        {
            var previous = count;
            var read = await stream.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) throw new IOException("Connection closed before HTTP headers were received.");
            count += read;
            for (var i = Math.Max(3, previous); i < count; i++)
                if (bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10)
                    return new HeaderRead(Encoding.Latin1.GetString(bytes, 0, i + 1), bytes.AsMemory(i + 1, count - i - 1));
        }
        throw new IOException("HTTP header exceeds 64 KB.");
    }

    private async Task PumpHttpResponseAsync(Stream source, Stream destination, Action started, CancellationToken token)
    {
        var response = await ReadHeaderAsync(source, token).ConfigureAwait(false);
        if (GetStatus(response.Text) == 407)
        {
            started();
            await WriteErrorAsync(destination, RecordFailure(new ProxyAuthenticationException()), token).ConfigureAwait(false);
            return;
        }
        started();
        await destination.WriteAsync(Encoding.Latin1.GetBytes(response.Text), token).ConfigureAwait(false);
        if (response.Extra.Length > 0) await destination.WriteAsync(response.Extra, token).ConfigureAwait(false);
        await PumpAsync(source, destination, false, token).ConfigureAwait(false);
    }

    private async Task PumpAsync(Stream source, Stream destination, bool upload, CancellationToken token)
    {
        var bytes = new byte[32768];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(bytes, token).ConfigureAwait(false);
                if (read == 0) return;
                await destination.WriteAsync(bytes.AsMemory(0, read), token).ConfigureAwait(false);
                if (upload) Interlocked.Add(ref _uploaded, read); else Interlocked.Add(ref _downloaded, read);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private string RecordFailure(Exception ex)
    {
        var error = ex switch
        {
            AuthenticationException => "TLS handshake or certificate validation failed for the HTTPS proxy.",
            OperationCanceledException or TimeoutException => "Proxy connection timed out.",
            ProxyProtocolException => "SOCKS5 proxy rejected the connection or credentials.",
            ProxyAuthenticationException => "Proxy authentication failed (407). Check username and password.",
            SocketException => "Cannot connect to the proxy or destination server.",
            _ => "Proxy connection was interrupted or returned an invalid response."
        };
        _lastError = error;
        Interlocked.Increment(ref _failures);
        _progress?.Invoke(error);
        return error;
    }

    private static async Task WriteErrorAsync(Stream stream, string message, CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
    }

    private sealed record Request(string Method, string Host, int Port, string Path, bool IsConnect, bool IsUpgrade);
    private sealed record HeaderRead(string Text, ReadOnlyMemory<byte> Extra);
    private sealed class ProxyAuthenticationException : IOException;
}
