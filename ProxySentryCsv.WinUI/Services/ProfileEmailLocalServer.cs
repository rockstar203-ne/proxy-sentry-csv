using System.Collections.Concurrent;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ProxySentryCsv.Services;

public sealed class ProfileEmailLocalServer : IDisposable
{
    public const int Port = 61984;

    private readonly ConcurrentDictionary<int, ProfileEmailProfile> _profiles = new();
    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public IDisposable Register(int processId, string email, string label, Action<string>? progress)
    {
        EnsureStarted(progress);
        _profiles[processId] = new ProfileEmailProfile(email.Trim(), label.Trim());
        return new Registration(this, processId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _cts?.Cancel();
            _listener?.Stop();
            _cts?.Dispose();
            _cts = null;
            _listener = null;
        }

        _profiles.Clear();
    }

    private void EnsureStarted(Action<string>? progress)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ProfileEmailLocalServer));
        }

        lock (_gate)
        {
            if (_listener is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
            progress?.Invoke($"Firefox profile email endpoint: http://127.0.0.1:{Port}/profile-email");
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 2000;
            client.SendTimeout = 2000;
            var request = await ReadRequestAsync(client.GetStream(), cancellationToken);
            var line = request.Split(["\r\n", "\n"], StringSplitOptions.None).FirstOrDefault() ?? "";
            if (line.StartsWith("OPTIONS ", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(client.GetStream(), "{}", "200 OK", cancellationToken);
                return;
            }

            if (!line.StartsWith("GET /profile-email", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(client.GetStream(), "{\"success\":false,\"message\":\"not found\"}", "404 Not Found", cancellationToken);
                return;
            }

            var profile = ResolveProfile(client);
            var payload = profile is null
                ? JsonSerializer.Serialize(new
                {
                    success = false,
                    email = "",
                    label = "",
                    message = "No matching Firefox profile is open."
                })
                : JsonSerializer.Serialize(new
                {
                    success = true,
                    email = profile.Email,
                    label = profile.Label
                });
            await WriteResponseAsync(client.GetStream(), payload, "200 OK", cancellationToken);
        }
        catch
        {
            // Browser popup requests are best-effort. A failed request must not affect browsing.
        }
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var memory = new MemoryStream();
        while (memory.Length < 16384)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            memory.Write(buffer, 0, read);
            var text = Encoding.ASCII.GetString(memory.ToArray());
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal) || text.Contains("\n\n", StringComparison.Ordinal))
            {
                return text;
            }
        }

        return Encoding.ASCII.GetString(memory.ToArray());
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string body, string status, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: GET, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
    }

    private ProfileEmailProfile? ResolveProfile(TcpClient client)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remote
            || client.Client.LocalEndPoint is not IPEndPoint local)
        {
            return _profiles.Count == 1 ? _profiles.Values.FirstOrDefault() : null;
        }

        var processId = TcpProcessLookup.FindOwnerProcessId(remote, local);
        if (processId.HasValue)
        {
            if (_profiles.TryGetValue(processId.Value, out var direct))
            {
                return direct;
            }

            var ancestor = FindRegisteredAncestor(processId.Value);
            if (ancestor is not null)
            {
                return ancestor;
            }
        }

        return _profiles.Count == 1 ? _profiles.Values.FirstOrDefault() : null;
    }

    private ProfileEmailProfile? FindRegisteredAncestor(int processId)
    {
        var current = processId;
        for (var depth = 0; depth < 8; depth++)
        {
            var parent = GetParentProcessId(current);
            if (!parent.HasValue || parent.Value == current)
            {
                return null;
            }

            if (_profiles.TryGetValue(parent.Value, out var profile))
            {
                return profile;
            }

            current = parent.Value;
        }

        return null;
    }

    private static int? GetParentProcessId(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (ManagementObject process in searcher.Get().Cast<ManagementObject>())
            {
                if (process["ParentProcessId"] is uint parent)
                {
                    return unchecked((int)parent);
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private void Unregister(int processId)
    {
        _profiles.TryRemove(processId, out _);
    }

    private sealed record ProfileEmailProfile(string Email, string Label);

    private sealed class Registration(ProfileEmailLocalServer owner, int processId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unregister(processId);
            }
        }
    }

    private static class TcpProcessLookup
    {
        private const int AfInet = 2;
        private const int TcpTableOwnerPidAll = 5;

        public static int? FindOwnerProcessId(IPEndPoint clientEndPoint, IPEndPoint serverEndPoint)
        {
            var size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
            if (size <= 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
                if (result != 0)
                {
                    return null;
                }

                var count = Marshal.ReadInt32(buffer);
                var rowPtr = IntPtr.Add(buffer, 4);
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowPtr, i * rowSize));
                    if (!AddressMatches(row.LocalAddr, clientEndPoint.Address)
                        || !AddressMatches(row.RemoteAddr, serverEndPoint.Address)
                        || DecodePort(row.LocalPort) != clientEndPoint.Port
                        || DecodePort(row.RemotePort) != serverEndPoint.Port)
                    {
                        continue;
                    }

                    return unchecked((int)row.OwningPid);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return null;
        }

        private static bool AddressMatches(uint value, IPAddress address)
        {
            var bytes = BitConverter.GetBytes(value);
            return new IPAddress(value).Equals(address)
                || new IPAddress(bytes).Equals(address)
                || new IPAddress(bytes.Reverse().ToArray()).Equals(address);
        }

        private static int DecodePort(uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            return (bytes[0] << 8) | bytes[1];
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr tcpTable,
            ref int tcpTableLength,
            bool sort,
            int ipVersion,
            int tcpTableClass,
            int reserved);

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct MibTcpRowOwnerPid
        {
            public readonly uint State;
            public readonly uint LocalAddr;
            public readonly uint LocalPort;
            public readonly uint RemoteAddr;
            public readonly uint RemotePort;
            public readonly uint OwningPid;
        }
    }
}
