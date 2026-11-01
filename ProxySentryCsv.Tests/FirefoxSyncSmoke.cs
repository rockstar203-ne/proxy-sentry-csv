using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ProxySentryCsv.Services;

internal static class FirefoxSyncSmoke
{
    public static async Task<int> RunAsync(string provider = "firefox")
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            provider == "firefox" ? @"Mozilla Firefox\firefox.exe" : @"Google\Chrome\Application\chrome.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException(provider + " is required for this opt-in local smoke test.", executable);
        var root = Path.Combine(Path.GetTempPath(), "ProxySentry-FirefoxSync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var posts = 0;
        var connections = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var serve = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var socket = await server.AcceptTcpClientAsync(stop.Token);
                connections.Add(ServeClient(socket));
            }
        });
        async Task ServeClient(TcpClient socket)
        {
            using (socket)
            try
            {
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(stop.Token);
                if (request is null) return;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                if (request?.StartsWith("POST ") == true) Interlocked.Increment(ref posts);
                var body = Encoding.UTF8.GetBytes("""
<!doctype html><html><body style="margin:0;height:5000px">
<button id="hit" style="position:absolute;left:20px;top:30px;width:150px;height:30px" onclick="window.count++">Count</button>
<input id="entry" style="position:absolute;left:20px;top:90px;width:150px;height:30px">
<form action="/posted" method="post"><button style="position:absolute;left:20px;top:150px;width:150px;height:30px">Submit</button></form>
<script>window.count=0;window.shiftUps=0;addEventListener('keyup',e=>{if(e.key==='Shift')window.shiftUps++});</script>
</body></html>
""");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                await stream.WriteAsync(body, stop.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
        var url = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/";
        var sessions = new List<SystemBrowserSyncSession>();
        using var sync = new ChromeSyncController();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var profile = Path.Combine(root, i.ToString());
                Directory.CreateDirectory(profile);
                var port = FreePort();
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                var launchArgs = provider == "firefox"
                    ? new[] { "-headless", "-no-remote", "-profile", profile, "--remote-debugging-port", port.ToString(), "--width", "900", "--height", "700", url }
                    : new[] { "--headless=new", "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--user-data-dir=" + profile, "--remote-debugging-port=" + port, "--window-size=900,700", url };
                foreach (var arg in launchArgs)
                    start.ArgumentList.Add(arg);
                var process = Process.Start(start)!;
                sessions.Add(new SystemBrowserSyncSession("test-" + i, i + 1, process, provider, port));
            }
            await sync.StartAsync(sessions, Console.WriteLine, stop.Token);
            var clients = Clients(sync);
            await Wait(async () => await Number(clients[0], "typeof window.count === 'number' ? 1 : 0") == 1);
            await Click(clients[0], 60, 45);
            await Wait(async () => await Number(clients[1], "window.count") == 1);
            Check(await Number(clients[0], "window.count") == 1, "master click");
            Console.WriteLine("PASS real pointer click mirrored once");
            await Click(clients[0], 60, 105);
            await Key(clients[0], "a", "keyDown"); await Key(clients[0], "a", "keyUp");
            await Wait(async () => await Number(clients[1], "document.getElementById('entry').value === 'a' ? 1 : 0") == 1);
            Console.WriteLine("PASS keyboard input");
            await Evaluate(clients[0], "(()=>{scrollTo(0,500);dispatchEvent(new WheelEvent('wheel',{deltaY:500}));return true})()");
            await Wait(async () => Math.Abs(await Number(clients[1], "scrollY") - 500) <= 3);
            await Evaluate(clients[0], "(()=>{scrollTo(0,0);dispatchEvent(new WheelEvent('wheel',{deltaY:-500}));return true})()");
            await Wait(async () => await Number(clients[1], "scrollY") <= 3);
            Console.WriteLine("PASS absolute scroll and return to top");
            sync.SetCtrlClickDelay(TimeSpan.FromMilliseconds(500));
            var started = Stopwatch.StartNew();
            await Click(clients[0], 60, 45, 2);
            await Wait(async () => await Number(clients[1], "window.count") == 2);
            Check(started.ElapsedMilliseconds >= 450, "follower delay");
            Console.WriteLine("PASS Ctrl-click delay");
            await Click(clients[0], 60, 165);
            await Wait(() => Task.FromResult(Volatile.Read(ref posts) == 2));
            await Wait(async () => await Number(clients[1], "location.pathname === '/posted' ? 1 : 0") == 1);
            Console.WriteLine("PASS both browsers submit POST (not URL-only navigation)");
            await Click(clients[0], 60, 45);
            await Wait(async () => await Number(clients[1], "window.count") == 1);
            Console.WriteLine("PASS capture survives navigation");
            await sync.NavigateAsync(url + "apply", stop.Token);
            await Wait(async () => await Number(clients[1], "location.pathname === '/apply' ? 1 : 0") == 1);
            Console.WriteLine("PASS Apply target URL");
            if (provider == "firefox") await Key(clients[0], "Shift", "keyDown", 8);
            await Task.Delay(200);
            sync.Stop();
            Check(sessions.All(s => !s.Process.HasExited), "Stop must preserve browsers");
            await sync.StartAsync(sessions, Console.WriteLine, stop.Token);
            clients = Clients(sync);
            await Click(clients[0], 60, 45);
            await Wait(async () => await Number(clients[1], "window.count") == 1);
            if (provider == "firefox") Check(await Number(clients[1], "window.shiftUps") >= 1, "Stop must release held Shift");
            Console.WriteLine("PASS Stop/restart and no duplicated capture");
            if (provider == "firefox") Console.WriteLine("PASS held-key release on Stop");
            sync.Stop();
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"FAIL {provider} smoke: " + ex); return 1; }
        finally
        {
            sync.Stop();
            foreach (var session in sessions)
            {
                try { if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true); await session.Process.WaitForExitAsync(); }
                finally { session.Process.Dispose(); }
            }
            stop.Cancel(); server.Stop();
            try { await serve; } catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
            await Task.WhenAll(connections);
            // Only this test's unique temporary profile directory is eligible for cleanup.
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("ProxySentry-FirefoxSync-", StringComparison.Ordinal))
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static List<IBrowserSyncClient> Clients(ChromeSyncController sync) =>
        ((IEnumerable)typeof(ChromeSyncController).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sync)!)
            .Cast<object>().Select(tab => (IBrowserSyncClient)tab.GetType().GetProperty("Client")!.GetValue(tab)!).ToList();
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 80; i++) { if (await condition()) return; await Task.Delay(100); }
        throw new TimeoutException("Browser sync condition not reached.");
    }
    private static Task<JsonElement> Evaluate(IBrowserSyncClient client, string expression) => client.SendAsync("Runtime.evaluate", new { expression, returnByValue = true });
    private static async Task<double> Number(IBrowserSyncClient client, string expression) => (await Evaluate(client, expression)).GetProperty("result").GetProperty("value").GetDouble();
    private static async Task Click(IBrowserSyncClient client, int x, int y, int modifiers = 0)
    {
        await client.SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", modifiers, clickCount = 1, buttons = 1 });
        await client.SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", modifiers, clickCount = 1, buttons = 0 });
    }
    private static Task Key(IBrowserSyncClient client, string key, string type, int modifiers = 0) => client.SendAsync("Input.dispatchKeyEvent", new
    {
        key, type, modifiers, text = type == "keyDown" && key.Length == 1 ? key : "",
        windowsVirtualKeyCode = key == "Shift" ? 16 : (int)char.ToUpperInvariant(key[0])
    });
}
