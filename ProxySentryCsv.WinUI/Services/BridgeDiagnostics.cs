using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ProxySentryCsv.Services;

public sealed record BridgeProbeResult(bool Ok, string Code, string Detail);

public static class BridgeDiagnostics
{
    public static async Task<BridgeProbeResult> ProbeAsync(string pipeName, int? expectedProcessId = null, CancellationToken token = default)
    {
        var phase = "connect";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2500, timeout.Token);
            phase = "response";
            var id = "diagnostic-" + Guid.NewGuid().ToString("N");
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { protocolVersion = 1, requestId = id, command = "status" }) + "\n");
            await pipe.WriteAsync(bytes, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line)) return new(false, "BRIDGE_EMPTY_RESPONSE", "The pipe connected but returned no response.");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != 1)
                return new(false, "BRIDGE_PROTOCOL_MISMATCH", "Update both applications; protocol 1 is required.");
            if (!root.TryGetProperty("requestId", out var requestId) || requestId.GetString() != id)
                return new(false, "BRIDGE_RESPONSE_MISMATCH", "The response does not belong to this request.");
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                return new(false, "BRIDGE_REJECTED", "The server received the request but rejected status. Check both application versions.");
            var pid = root.TryGetProperty("serverProcessId", out var serverPid) && serverPid.TryGetInt32(out var parsed) ? parsed : (int?)null;
            if (expectedProcessId.HasValue && pid.HasValue && pid != expectedProcessId)
                return new(false, "BRIDGE_OTHER_INSTANCE", $"Another Proxy Sentry instance answered (PID {pid}); this instance is {expectedProcessId}.");
            var blocked = root.TryGetProperty("acquisitionBlocked", out var flag) && flag.ValueKind == JsonValueKind.True;
            return new(true, blocked ? "BRIDGE_API_LOCK" : "BRIDGE_OK",
                $"Round-trip succeeded. Server PID: {pid?.ToString() ?? "not reported (older version)"}. Selected: {root.GetProperty("selectedCount")}. Ready: {root.GetProperty("readyCount")}. "
                + (blocked ? "AdsPower API mode blocks Event Sentry acquisition. Disable it in Settings." : "If Event Sentry cannot connect, compare its pipe name, Windows account and elevation. This self-test does not prove access from another user/process."));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return new(false, "BRIDGE_ACCESS_DENIED", "Pipe access denied. Run both apps under the same Windows account and elevation; normally neither needs administrator rights."); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        { return new(false, phase == "connect" ? "BRIDGE_CONNECT_TIMEOUT" : "BRIDGE_RESPONSE_TIMEOUT", phase == "connect" ? "No connection: wrong pipe name, server not listening, busy instances, or security software. Check the server state and Windows account." : "Connected, but no status response. The server may be busy or its UI is blocked."); }
        catch (JsonException) { return new(false, "BRIDGE_INVALID_JSON", "The pipe returned invalid JSON. Check application versions and pipe name."); }
        catch (Exception ex) { return new(false, "BRIDGE_IO_ERROR", $"Phase: {phase}; {ex.GetType().Name}, 0x{ex.HResult:X8}. Check security software and application logs."); }
    }
}
