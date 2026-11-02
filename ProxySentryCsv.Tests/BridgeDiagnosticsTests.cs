using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ProxySentryCsv.Services;

internal static class BridgeDiagnosticsTests
{
    public static async Task RunAsync()
    {
        foreach (var kind in new[] { "ok", "other", "protocol", "empty", "invalid", "locked" })
        {
            var name = "ProxySentry-diagnostic-test-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var serve = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                if (kind == "empty") return;
                var response = kind == "invalid" ? "not-json" : JsonSerializer.Serialize(new
                {
                    protocolVersion = kind == "protocol" ? 99 : 1,
                    requestId = request.RootElement.GetProperty("requestId").GetString(), ok = true,
                    serverProcessId = kind == "other" ? Environment.ProcessId + 1 : Environment.ProcessId,
                    acquisitionBlocked = kind == "locked", selectedCount = 0, readyCount = 0
                });
                await server.WriteAsync(Encoding.UTF8.GetBytes(response + "\n"));
                await server.FlushAsync();
            });
            var probe = BridgeDiagnostics.ProbeAsync(name, Environment.ProcessId);
            await serve;
            if (kind == "empty") server.Dispose();
            var result = await probe;
            var expected = kind switch
            {
                "ok" => "BRIDGE_OK", "other" => "BRIDGE_OTHER_INSTANCE", "protocol" => "BRIDGE_PROTOCOL_MISMATCH",
                "empty" => "BRIDGE_EMPTY_RESPONSE", "invalid" => "BRIDGE_INVALID_JSON", _ => "BRIDGE_API_LOCK"
            };
            if (result.Code != expected) throw new Exception($"Expected {expected}, got {result.Code}: {result.Detail}");
        }
        var missing = await BridgeDiagnostics.ProbeAsync("ProxySentry-missing-" + Guid.NewGuid().ToString("N"));
        if (missing.Code != "BRIDGE_CONNECT_TIMEOUT") throw new Exception("Missing bridge not identified");
    }
}
