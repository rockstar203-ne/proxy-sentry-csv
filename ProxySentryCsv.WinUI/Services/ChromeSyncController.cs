using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed record SystemBrowserSyncSession(string StableId, int RowNumber, Process Process, string Provider, int DebuggingPort);

public readonly record struct SyncWindowPlacement(int X, int Y, int Width, int Height);

internal sealed class ChromeSyncController : IDisposable
{
    private readonly object _gate = new();
    private readonly Channel<ChromeSyncEvent> _events = Channel.CreateUnbounded<ChromeSyncEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly LowLevelMouseProc _mouseProc;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly List<SyncedChromeTab> _tabs = [];
    private CancellationTokenSource? _syncStop;
    private Task? _eventPump;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private long _sequence;
    private long _lastMouseMoveTicks;
    private string _lastMasterUrl = "";
    private bool _navigationReplay;
    private int _inputReplayDepth;
    private long _lastBridgeMoveTicks;
    private long _lastBridgePressTicks;
    private long _lastBridgeReleaseTicks;
    private long _lastBridgeWheelTicks;
    private long _lastBridgeKeyDownTicks;
    private long _lastBridgeKeyUpTicks;
    private long _lastInputTicks;
    private long _lastScrollInputTicks;
    private int _scrollCorrectionRunning;
    private int _ctrlClickDelayMs;

    public ChromeSyncController()
    {
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _syncStop is { IsCancellationRequested: false };
            }
        }
    }

    public async Task StartAsync(IReadOnlyList<SystemBrowserSyncSession> sessions, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (sessions.Count < 2)
        {
            throw new InvalidOperationException("Select at least two opened Chrome windows.");
        }

        Stop();
        var ordered = sessions.Where(session =>
                IsSupportedSyncProvider(session.Provider)
                && session.DebuggingPort > 0
                && !session.Process.HasExited)
            .ToList();
        if (ordered.Count < 2)
        {
            throw new InvalidOperationException("Chrome sync requires at least two opened Chrome windows.");
        }

        var createdTabs = new List<SyncedChromeTab>();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            progress?.Invoke($"Chrome sync connecting {ordered.Count} DevTools targets...");
            for (var index = 0; index < ordered.Count; index++)
            {
                var session = ordered[index];
                var client = await ChromeDevToolsClient.ConnectAsync(session.DebuggingPort, linked.Token);
                await client.SendAsync("Page.enable", cancellationToken: linked.Token);
                await client.SendAsync("Runtime.enable", cancellationToken: linked.Token);
                var tab = new SyncedChromeTab(session, client, isMaster: index == 0);
                await InstallPageSyncBridgeAsync(tab, linked.Token);
                await InstallSyncRoleBadgeAsync(tab, linked.Token);
                await RefreshMetricsAsync(tab, linked.Token);
                createdTabs.Add(tab);
            }
        }
        catch
        {
            foreach (var tab in createdTabs)
            {
                tab.Dispose();
            }

            throw;
        }

        var stop = new CancellationTokenSource();
        lock (_gate)
        {
            _tabs.Clear();
            _tabs.AddRange(createdTabs);
            _syncStop = stop;
            _lastMasterUrl = "";
            _navigationReplay = false;
            _lastBridgeMoveTicks = 0;
            _lastBridgePressTicks = 0;
            _lastBridgeReleaseTicks = 0;
            _lastBridgeWheelTicks = 0;
            _lastBridgeKeyDownTicks = 0;
            _lastBridgeKeyUpTicks = 0;
            _lastInputTicks = 0;
            _lastScrollInputTicks = 0;
            _scrollCorrectionRunning = 0;
            _eventPump = Task.Run(() => EventPumpAsync(stop.Token));
        }

        foreach (var tab in _tabs)
        {
            tab.DevToolsEventHandler = (method, payload) => OnDevToolsEvent(tab, method, payload);
            tab.Client.EventReceived += tab.DevToolsEventHandler;
        }

        await RefreshAllMetricsAsync(stop.Token);
        InstallHooks();
        var master = _tabs.First(tab => tab.IsMaster);
        progress?.Invoke($"Chrome sync started: master row {master.Session.RowNumber}, {_tabs.Count - 1} followers.");
    }

    public void Stop()
    {
        List<SyncedChromeTab> tabs;
        CancellationTokenSource? stop;
        Task? eventPump;
        lock (_gate)
        {
            stop = _syncStop;
            _syncStop = null;
            eventPump = _eventPump;
            _eventPump = null;
            tabs = [.. _tabs];
            _tabs.Clear();
        }

        UninstallHooks();
        try
        {
            stop?.Cancel();
        }
        catch
        {
        }

        foreach (var tab in tabs)
        {
            try
            {
                if (tab.DevToolsEventHandler is not null)
                {
                    tab.Client.EventReceived -= tab.DevToolsEventHandler;
                }

                tab.Dispose();
            }
            catch
            {
            }
        }

        try
        {
            eventPump?.Wait(500);
        }
        catch
        {
        }

        stop?.Dispose();
    }

    public void Dispose()
    {
        Stop();
    }

    public void SetCtrlClickDelay(TimeSpan delay)
    {
        var delayMs = delay <= TimeSpan.Zero ? 0 : (int)Math.Clamp(delay.TotalMilliseconds, 0, 5000);
        Interlocked.Exchange(ref _ctrlClickDelayMs, delayMs);
    }

    public static async Task ArrangeAsync(IReadOnlyList<SystemBrowserSyncSession> sessions, SyncMonitorOption? monitor, CancellationToken cancellationToken = default)
    {
        if (monitor is null)
        {
            throw new InvalidOperationException("Select a sync monitor before aligning windows.");
        }

        var active = sessions
            .Where(session => IsSupportedSyncProvider(session.Provider)
                && !session.Process.HasExited)
            .ToList();
        if (active.Count < 2)
        {
            throw new InvalidOperationException("Select at least two opened Chrome windows to align.");
        }

        var placements = CalculateWindowPlacements(active.Count, monitor);
        for (var index = 0; index < active.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var placement = placements[index];
            var hwnd = FindMainWindow(active[index].Process.Id);
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            ShowWindow(hwnd, ShowWindowRestore);
            SetWindowPos(hwnd, IntPtr.Zero, placement.X, placement.Y, placement.Width, placement.Height, SwpNoZOrder | SwpShowWindow);
            await Task.Delay(30, cancellationToken);
        }
    }

    public static IReadOnlyList<SyncWindowPlacement> CalculateWindowPlacements(int count, SyncMonitorOption monitor)
    {
        if (count <= 0)
        {
            return [];
        }

        var gap = count > 6 ? 6 : 8;
        var (columns, rows) = CalculateGrid(count, monitor.AvailWidth, monitor.AvailHeight);
        var width = Math.Max(260, (monitor.AvailWidth - (columns - 1) * gap) / columns);
        var height = Math.Max(220, (monitor.AvailHeight - (rows - 1) * gap) / rows);
        var placements = new List<SyncWindowPlacement>(count);
        for (var index = 0; index < count; index++)
        {
            var column = index % columns;
            var row = index / columns;
            placements.Add(new SyncWindowPlacement(
                monitor.AvailLeft + column * (width + gap),
                monitor.AvailTop + row * (height + gap),
                width,
                height));
        }

        return placements;
    }

    public async Task<int> NavigateAsync(string targetUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Target URL must be an absolute URL.");
        }

        List<SyncedChromeTab> tabs;
        lock (_gate)
        {
            tabs = [.. _tabs];
        }

        if (tabs.Count == 0)
        {
            throw new InvalidOperationException("Chrome sync is not connected to any open window.");
        }

        try
        {
            _navigationReplay = true;
            Interlocked.Exchange(ref _lastInputTicks, 0);
            _lastMasterUrl = targetUrl;
            foreach (var tab in tabs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await tab.Client.SendAsync("Page.navigate", new { url = targetUrl }, cancellationToken);
                    tab.Url = targetUrl;
                }
                catch
                {
                }
            }

            await Task.Delay(250, cancellationToken);
            foreach (var tab in tabs)
            {
                try
                {
                    await RefreshMetricsAsync(tab, cancellationToken);
                }
                catch
                {
                }
            }

            return tabs.Count;
        }
        finally
        {
            _navigationReplay = false;
        }
    }

    private async Task RefreshAllMetricsAsync(CancellationToken cancellationToken)
    {
        List<SyncedChromeTab> snapshot;
        lock (_gate)
        {
            snapshot = [.. _tabs];
        }

        foreach (var tab in snapshot)
        {
            await RefreshMetricsAsync(tab, cancellationToken);
        }
    }

    private static async Task RefreshMetricsAsync(SyncedChromeTab tab, CancellationToken cancellationToken)
    {
        var hwnd = FindMainWindow(tab.Session.Process.Id);
        tab.MainWindow = hwnd;
        tab.WindowRect = hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rootRect) ? Rect.Empty : rootRect;
        tab.ContentRect = hwnd == IntPtr.Zero ? Rect.Empty : FindContentRect(hwnd);
        await RefreshWindowBoundsFromDevToolsAsync(tab, cancellationToken);
        try
        {
            var result = await tab.Client.SendAsync("Runtime.evaluate", new
            {
                expression = "({width: Math.max(window.innerWidth || 0, document.documentElement ? document.documentElement.clientWidth : 0), height: Math.max(window.innerHeight || 0, document.documentElement ? document.documentElement.clientHeight : 0), outerWidth: window.outerWidth || 0, outerHeight: window.outerHeight || 0, dpr: window.devicePixelRatio || 1, scrollX: window.scrollX || (window.visualViewport ? window.visualViewport.pageLeft : 0) || 0, scrollY: window.scrollY || (window.visualViewport ? window.visualViewport.pageTop : 0) || 0, scrollWidth: Math.max(document.documentElement ? document.documentElement.scrollWidth : 0, document.body ? document.body.scrollWidth : 0, window.innerWidth || 0), scrollHeight: Math.max(document.documentElement ? document.documentElement.scrollHeight : 0, document.body ? document.body.scrollHeight : 0, window.innerHeight || 0), href: location.href})",
                returnByValue = true
            }, cancellationToken);
            if (TryGetRuntimeValue(result, out var value))
            {
                tab.ViewportWidth = TryGetDouble(value, "width", tab.ViewportWidth);
                tab.ViewportHeight = TryGetDouble(value, "height", tab.ViewportHeight);
                tab.OuterWidth = TryGetDouble(value, "outerWidth", tab.OuterWidth);
                tab.OuterHeight = TryGetDouble(value, "outerHeight", tab.OuterHeight);
                tab.DeviceScaleFactor = TryGetDouble(value, "dpr", tab.DeviceScaleFactor);
                tab.ScrollX = TryGetDouble(value, "scrollX", tab.ScrollX);
                tab.ScrollY = TryGetDouble(value, "scrollY", tab.ScrollY);
                tab.ScrollWidth = TryGetDouble(value, "scrollWidth", tab.ScrollWidth);
                tab.ScrollHeight = TryGetDouble(value, "scrollHeight", tab.ScrollHeight);
                tab.Url = TryGetString(value, "href", tab.Url);
                tab.ContentRect = CalibrateContentRect(tab, tab.ContentRect);
            }
        }
        catch
        {
            // Navigation can briefly invalidate a target; metrics are refreshed again before input.
        }
    }

    private static bool IsSupportedSyncProvider(string? provider)
    {
        return provider?.Trim().ToLowerInvariant() is "chrome";
    }

    private static async Task RefreshWindowBoundsFromDevToolsAsync(SyncedChromeTab tab, CancellationToken cancellationToken)
    {
        try
        {
            var result = await tab.Client.SendAsync("Browser.getWindowForTarget", cancellationToken: cancellationToken);
            if (!result.TryGetProperty("bounds", out var bounds))
            {
                return;
            }

            var left = TryGetInt(bounds, "left", 0);
            var top = TryGetInt(bounds, "top", 0);
            var width = TryGetInt(bounds, "width", 0);
            var height = TryGetInt(bounds, "height", 0);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var windowRect = new Rect(left, top, left + width, top + height);
            tab.WindowRect = windowRect;
            if (tab.ContentRect.IsEmpty || !tab.WindowRect.Contains(tab.ContentRect.Left, tab.ContentRect.Top))
            {
                tab.ContentRect = EstimateChromiumContentRect(windowRect);
            }
        }
        catch
        {
            if (tab.ContentRect.IsEmpty && !tab.WindowRect.IsEmpty)
            {
                tab.ContentRect = EstimateChromiumContentRect(tab.WindowRect);
            }
        }
    }

    private static async Task InstallPageSyncBridgeAsync(SyncedChromeTab tab, CancellationToken cancellationToken)
    {
        try
        {
            await tab.Client.SendAsync("Runtime.addBinding", new { name = SyncBindingName }, cancellationToken);
        }
        catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
        }

        await tab.Client.SendAsync("Page.addScriptToEvaluateOnNewDocument", new
        {
            source = PageSyncBridgeScript
        }, cancellationToken);
        await tab.Client.SendAsync("Runtime.evaluate", new
        {
            expression = PageSyncBridgeScript,
            returnByValue = true
        }, cancellationToken);
    }

    private static async Task InstallSyncRoleBadgeAsync(SyncedChromeTab tab, CancellationToken cancellationToken)
    {
        var script = BuildSyncRoleBadgeScript(tab.IsMaster);
        await tab.Client.SendAsync("Page.addScriptToEvaluateOnNewDocument", new
        {
            source = script
        }, cancellationToken);
        await tab.Client.SendAsync("Runtime.evaluate", new
        {
            expression = script,
            returnByValue = true
        }, cancellationToken);
    }

    private static string BuildSyncRoleBadgeScript(bool isMaster)
    {
        var label = isMaster ? "SYNC MASTER" : "SYNC FOLLOWER";
        var color = isMaster ? "#f97316" : "#2563eb";
        var borderOpacity = isMaster ? "1" : "0.55";
        return $$"""
(() => {
  const id = "__proxySentrySyncRoleBadge";
  const borderId = "__proxySentrySyncRoleBorder";
  const label = {{JsonSerializer.Serialize(label)}};
  const color = {{JsonSerializer.Serialize(color)}};
  const borderOpacity = {{JsonSerializer.Serialize(borderOpacity)}};
  const ensure = () => {
    if (!document.documentElement || !document.body) {
      requestAnimationFrame(ensure);
      return;
    }

    let badge = document.getElementById(id);
    if (!badge) {
      badge = document.createElement("div");
      badge.id = id;
      document.documentElement.appendChild(badge);
    }

    badge.textContent = label;
    Object.assign(badge.style, {
      position: "fixed",
      top: "8px",
      left: "50%",
      transform: "translateX(-50%)",
      zIndex: "2147483647",
      background: color,
      color: "#fff",
      font: "700 12px/1.2 Arial, sans-serif",
      letterSpacing: "0",
      padding: "5px 10px",
      borderRadius: "6px",
      boxShadow: "0 2px 10px rgba(0,0,0,.35)",
      pointerEvents: "none",
      opacity: "0.92"
    });

    let border = document.getElementById(borderId);
    if (!border) {
      border = document.createElement("div");
      border.id = borderId;
      document.documentElement.appendChild(border);
    }

    Object.assign(border.style, {
      position: "fixed",
      inset: "0",
      zIndex: "2147483646",
      border: `3px solid ${color}`,
      boxSizing: "border-box",
      pointerEvents: "none",
      opacity: borderOpacity
    });
  };

  ensure();
  return true;
})();
""";
    }

    private async Task EventPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var syncEvent in _events.Reader.ReadAllAsync(cancellationToken))
            {
                if (syncEvent is ChromeSyncMouseEvent mouse)
                {
                    await ReplayMouseAsync(mouse, cancellationToken);
                }
                else if (syncEvent is ChromeSyncKeyEvent key)
                {
                    await ReplayKeyAsync(key, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReplayMouseAsync(ChromeSyncMouseEvent syncEvent, CancellationToken cancellationToken)
    {
        SyncedChromeTab? source;
        List<SyncedChromeTab> followers;
        lock (_gate)
        {
            source = _tabs.FirstOrDefault(tab => tab.IsMaster && string.Equals(tab.Session.StableId, syncEvent.SourceStableId, StringComparison.Ordinal));
            followers = _tabs
                .Where(tab => !tab.IsMaster)
                .ToList();
        }

        if (source is null)
        {
            return;
        }

        if (syncEvent.Type == "mouseWheel")
        {
            await RefreshMetricsAsync(source, cancellationToken);
        }
        else
        {
            await RefreshMetricsAsync(source, cancellationToken);
        }

        Interlocked.Increment(ref _inputReplayDepth);
        try
        {
            if (IsDelayedCtrlClick(syncEvent))
            {
                if (syncEvent.Type == "mouseReleased")
                {
                    await ReplayDelayedClickAsync(syncEvent, followers, cancellationToken);
                }

                return;
            }

            if (source is not null && syncEvent.Type == "mouseWheel")
            {
                QueueScrollCorrection(source.Session.StableId);
                return;
            }

            foreach (var follower in followers)
            {
                try
                {
                    if (follower.ViewportWidth <= 0 || follower.ViewportHeight <= 0 || syncEvent.Type != "mouseMoved")
                    {
                        await RefreshMetricsAsync(follower, cancellationToken);
                    }

                    var x = Math.Clamp(syncEvent.XRatio * follower.ViewportWidth, 1, Math.Max(1, follower.ViewportWidth - 1));
                    var y = Math.Clamp(syncEvent.YRatio * follower.ViewportHeight, 1, Math.Max(1, follower.ViewportHeight - 1));
                    object parameters = syncEvent.Type == "mouseWheel"
                        ? new
                        {
                            type = syncEvent.Type,
                            x,
                            y,
                            deltaX = syncEvent.WheelDeltaX,
                            deltaY = syncEvent.WheelDeltaY,
                            modifiers = syncEvent.Modifiers
                        }
                        : new
                        {
                            type = syncEvent.Type,
                            x,
                            y,
                            button = syncEvent.Button,
                            buttons = syncEvent.Buttons,
                            clickCount = syncEvent.ClickCount,
                            modifiers = syncEvent.Modifiers,
                            pointerType = "mouse"
                        };

                    await follower.Client.SendAsync("Input.dispatchMouseEvent", parameters, cancellationToken);
                }
                catch
                {
                }
            }

            if (source is not null && syncEvent.Type == "mouseWheel")
            {
                QueueScrollCorrection(source.Session.StableId);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inputReplayDepth);
        }
    }

    private async Task ReplayDelayedClickAsync(ChromeSyncMouseEvent syncEvent, IReadOnlyList<SyncedChromeTab> followers, CancellationToken cancellationToken)
    {
        var delayMs = Interlocked.CompareExchange(ref _ctrlClickDelayMs, 0, 0);
        if (delayMs <= 0)
        {
            return;
        }

        var button = syncEvent.Button is "left" or "right" or "middle" ? syncEvent.Button : "left";
        var buttonMask = ButtonMask(button);
        var modifiers = syncEvent.Modifiers & ~CdpModifierControl;
        var clickCount = Math.Max(1, syncEvent.ClickCount);
        for (var index = 0; index < followers.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(delayMs, cancellationToken);

            var follower = followers[index];
            try
            {
                await RefreshMetricsAsync(follower, cancellationToken);
                var x = Math.Clamp(syncEvent.XRatio * follower.ViewportWidth, 1, Math.Max(1, follower.ViewportWidth - 1));
                var y = Math.Clamp(syncEvent.YRatio * follower.ViewportHeight, 1, Math.Max(1, follower.ViewportHeight - 1));
                await follower.Client.SendAsync("Input.dispatchMouseEvent", new
                {
                    type = "mouseMoved",
                    x,
                    y,
                    button = "none",
                    buttons = 0,
                    modifiers,
                    pointerType = "mouse"
                }, cancellationToken);
                await follower.Client.SendAsync("Input.dispatchMouseEvent", new
                {
                    type = "mousePressed",
                    x,
                    y,
                    button,
                    buttons = buttonMask,
                    clickCount,
                    modifiers,
                    pointerType = "mouse"
                }, cancellationToken);
                await Task.Delay(35, cancellationToken);
                await follower.Client.SendAsync("Input.dispatchMouseEvent", new
                {
                    type = "mouseReleased",
                    x,
                    y,
                    button,
                    buttons = 0,
                    clickCount,
                    modifiers,
                    pointerType = "mouse"
                }, cancellationToken);
            }
            catch
            {
            }
        }
    }

    private bool IsDelayedCtrlClick(ChromeSyncMouseEvent syncEvent)
    {
        return Interlocked.CompareExchange(ref _ctrlClickDelayMs, 0, 0) > 0
            && (syncEvent.Modifiers & CdpModifierControl) != 0
            && syncEvent.Type is "mousePressed" or "mouseReleased"
            && syncEvent.Button is "left" or "right" or "middle";
    }

    private async Task ReplayKeyAsync(ChromeSyncKeyEvent syncEvent, CancellationToken cancellationToken)
    {
        SyncedChromeTab? source;
        List<SyncedChromeTab> followers;
        lock (_gate)
        {
            source = _tabs.FirstOrDefault(tab => tab.IsMaster && string.Equals(tab.Session.StableId, syncEvent.SourceStableId, StringComparison.Ordinal));
            followers = _tabs
                .Where(tab => !tab.IsMaster)
                .ToList();
        }

        if (source is null)
        {
            return;
        }

        if (source is not null)
        {
            await RefreshMetricsAsync(source, cancellationToken);
        }

        Interlocked.Increment(ref _inputReplayDepth);
        try
        {
            foreach (var follower in followers)
            {
                try
                {
                    await follower.Client.SendAsync("Input.dispatchKeyEvent", new
                    {
                        type = syncEvent.Type,
                        key = syncEvent.Key,
                        code = syncEvent.Code,
                        windowsVirtualKeyCode = syncEvent.VirtualKey,
                        nativeVirtualKeyCode = syncEvent.VirtualKey,
                        text = syncEvent.Text,
                        unmodifiedText = syncEvent.Text,
                        autoRepeat = syncEvent.AutoRepeat,
                        isKeypad = false,
                        modifiers = syncEvent.Modifiers
                    }, cancellationToken);
                }
                catch
                {
                }
            }

            if (source is not null && syncEvent.Type == "keyUp" && IsScrollKey(syncEvent.VirtualKey))
            {
                QueueScrollCorrection(source.Session.StableId);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inputReplayDepth);
        }
    }

    private static async Task ApplyScrollAsync(SyncedChromeTab follower, double scrollX, double scrollY, int durationMs, CancellationToken cancellationToken)
    {
        if (double.IsNaN(scrollX) || double.IsInfinity(scrollX) || double.IsNaN(scrollY) || double.IsInfinity(scrollY))
        {
            return;
        }

        if (Math.Abs(follower.ScrollX - scrollX) < ScrollCorrectionTolerancePx
            && Math.Abs(follower.ScrollY - scrollY) < ScrollCorrectionTolerancePx)
        {
            return;
        }

        var x = Math.Max(0, scrollX).ToString(CultureInfo.InvariantCulture);
        var y = Math.Max(0, scrollY).ToString(CultureInfo.InvariantCulture);
        var duration = Math.Max(0, durationMs).ToString(CultureInfo.InvariantCulture);
        await follower.Client.SendAsync("Runtime.evaluate", new
        {
            expression = $$"""
(() => {
  const targetX = {{x}};
  const targetY = {{y}};
  const duration = {{duration}};
  const startX = window.scrollX || 0;
  const startY = window.scrollY || 0;
  if (Math.abs(startX - targetX) < 1 && Math.abs(startY - targetY) < 1) {
    return true;
  }

  const token = `${Date.now()}-${Math.random()}`;
  window.__proxySentryScrollToken = token;
  if (duration <= 0) {
    window.scrollTo(targetX, targetY);
    return true;
  }

  const start = performance.now();
  const ease = (t) => 1 - Math.pow(1 - t, 3);
  const step = (now) => {
    if (window.__proxySentryScrollToken !== token) {
      return;
    }

    const progress = Math.min(1, (now - start) / duration);
    const eased = ease(progress);
    window.scrollTo(startX + (targetX - startX) * eased, startY + (targetY - startY) * eased);
    if (progress < 1) {
      requestAnimationFrame(step);
    }
  };
  requestAnimationFrame(step);
  return true;
})()
""",
            returnByValue = true
        }, cancellationToken);
        follower.ScrollX = scrollX;
        follower.ScrollY = scrollY;
    }

    private void QueueScrollCorrection(string sourceStableId)
    {
        Interlocked.Exchange(ref _lastScrollInputTicks, Stopwatch.GetTimestamp());
        if (Interlocked.Exchange(ref _scrollCorrectionRunning, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (IsRunning)
                {
                    await Task.Delay(ScrollSyncIntervalMs);
                    await ApplyScrollCorrectionAsync(sourceStableId);

                    var ageMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastScrollInputTicks)) * 1000.0 / Stopwatch.Frequency;
                    if (ageMs > ScrollCorrectionDebounceMs)
                    {
                        break;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _scrollCorrectionRunning, 0);
                var ageMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastScrollInputTicks)) * 1000.0 / Stopwatch.Frequency;
                if (IsRunning && ageMs <= ScrollCorrectionDebounceMs)
                {
                    QueueScrollCorrection(sourceStableId);
                }
            }
        });
    }

    private async Task ApplyScrollCorrectionAsync(string sourceStableId)
    {
        SyncedChromeTab? source;
        List<SyncedChromeTab> followers;
        lock (_gate)
        {
            source = _tabs.FirstOrDefault(tab => tab.IsMaster && string.Equals(tab.Session.StableId, sourceStableId, StringComparison.Ordinal));
            followers = _tabs
                .Where(tab => !tab.IsMaster)
                .ToList();
        }

        if (source is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await RefreshMetricsAsync(source, timeout.Token);
        var maxSourceX = Math.Max(1, source.ScrollWidth - source.ViewportWidth);
        var maxSourceY = Math.Max(1, source.ScrollHeight - source.ViewportHeight);
        var ratioX = Math.Clamp(source.ScrollX / maxSourceX, 0, 1);
        var ratioY = Math.Clamp(source.ScrollY / maxSourceY, 0, 1);
        if (source.ScrollX < ScrollTopSnapThresholdPx)
        {
            ratioX = 0;
        }

        if (source.ScrollY < ScrollTopSnapThresholdPx)
        {
            ratioY = 0;
        }

        foreach (var follower in followers)
        {
            try
            {
                await RefreshMetricsAsync(follower, timeout.Token);
                var targetX = ratioX <= 0 ? 0 : ratioX * Math.Max(1, follower.ScrollWidth - follower.ViewportWidth);
                var targetY = ratioY <= 0 ? 0 : ratioY * Math.Max(1, follower.ScrollHeight - follower.ViewportHeight);
                await ApplyScrollAsync(follower, targetX, targetY, ScrollAnimationMs, timeout.Token);
            }
            catch
            {
            }
        }
    }

    private async void OnDevToolsEvent(SyncedChromeTab source, string method, JsonElement payload)
    {
        if (!source.IsMaster)
        {
            return;
        }

        if (method == "Runtime.bindingCalled")
        {
            TryCapturePageBridgeEvent(source, payload);
            return;
        }

        if (method is not "Page.frameNavigated" and not "Page.navigatedWithinDocument")
        {
            return;
        }

        if (_navigationReplay)
        {
            return;
        }

        var lastInputAgeMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastInputTicks)) * 1000.0 / Stopwatch.Frequency;
        if (lastInputAgeMs >= 0 && lastInputAgeMs < NavigationReplayInputQuietMs)
        {
            // A click/key action should be replayed as input. Forcing Page.navigate here skips
            // button-side effects such as POSTs, state changes, or anti-bot workflow steps.
            return;
        }

        var url = ExtractNavigationUrl(method, payload);
        if (string.IsNullOrWhiteSpace(url)
            || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url, _lastMasterUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastMasterUrl = url;
        List<SyncedChromeTab> followers;
        lock (_gate)
        {
            followers = _tabs
                .Where(tab => !ReferenceEquals(tab, source))
                .ToList();
        }

        try
        {
            _navigationReplay = true;
            foreach (var follower in followers)
            {
                try
                {
                    await follower.Client.SendAsync("Page.navigate", new { url }, CancellationToken.None);
                }
                catch
                {
                }
            }

            await Task.Delay(250);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            foreach (var follower in followers)
            {
                await RefreshMetricsAsync(follower, timeout.Token);
            }
        }
        finally
        {
            _navigationReplay = false;
        }
    }

    private void TryCapturePageBridgeEvent(SyncedChromeTab source, JsonElement payload)
    {
        if (!source.IsMaster)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _inputReplayDepth, 0, 0) > 0)
        {
            return;
        }

        try
        {
            if (!payload.TryGetProperty("params", out var parameters)
                || !parameters.TryGetProperty("name", out var name)
                || !string.Equals(name.GetString(), SyncBindingName, StringComparison.Ordinal)
                || !parameters.TryGetProperty("payload", out var payloadElement))
            {
                return;
            }

            using var document = JsonDocument.Parse(payloadElement.GetString() ?? "{}");
            var root = document.RootElement;
            var kind = TryGetString(root, "kind", "");
            if (kind == "mouse")
            {
                var eventType = TryGetString(root, "type", "mouseMoved");
                var eventButtons = TryGetInt(root, "buttons", 0);
                if (eventType == "mouseMoved" && eventButtons == 0)
                {
                    return;
                }

                var syncEvent = new ChromeSyncMouseEvent(
                    Interlocked.Increment(ref _sequence),
                    source.Session.StableId,
                    eventType,
                    Math.Clamp(TryGetDouble(root, "xRatio", 0), 0, 1),
                    Math.Clamp(TryGetDouble(root, "yRatio", 0), 0, 1),
                    TryGetString(root, "button", "none"),
                    eventButtons,
                    TryGetInt(root, "clickCount", 0),
                    TryGetDouble(root, "deltaX", 0),
                    TryGetDouble(root, "deltaY", 0),
                    TryGetInt(root, "modifiers", 0));
                MarkInput(syncEvent, Stopwatch.GetTimestamp());
                UpdateBridgeTicks(syncEvent, Stopwatch.GetTimestamp());
                UpdateSourceStateFromBridge(source, root);
                _events.Writer.TryWrite(syncEvent);
            }
            else if (kind == "key")
            {
                var syncEvent = new ChromeSyncKeyEvent(
                    Interlocked.Increment(ref _sequence),
                    source.Session.StableId,
                    TryGetString(root, "type", "keyDown"),
                    TryGetInt(root, "virtualKey", 0),
                    TryGetString(root, "key", ""),
                    TryGetString(root, "code", ""),
                    TryGetString(root, "text", ""),
                    TryGetInt(root, "modifiers", 0),
                    root.TryGetProperty("autoRepeat", out var autoRepeat) && autoRepeat.ValueKind == JsonValueKind.True);
                MarkInput(syncEvent, Stopwatch.GetTimestamp());
                UpdateBridgeTicks(syncEvent, Stopwatch.GetTimestamp());
                UpdateSourceStateFromBridge(source, root);
                _events.Writer.TryWrite(syncEvent);
            }
        }
        catch
        {
        }
    }

    private static void UpdateSourceStateFromBridge(SyncedChromeTab source, JsonElement root)
    {
        source.ViewportWidth = TryGetDouble(root, "viewportWidth", source.ViewportWidth);
        source.ViewportHeight = TryGetDouble(root, "viewportHeight", source.ViewportHeight);
        source.ScrollX = TryGetDouble(root, "scrollX", source.ScrollX);
        source.ScrollY = TryGetDouble(root, "scrollY", source.ScrollY);
        source.ScrollWidth = TryGetDouble(root, "scrollWidth", source.ScrollWidth);
        source.ScrollHeight = TryGetDouble(root, "scrollHeight", source.ScrollHeight);
    }

    private void UpdateBridgeTicks(ChromeSyncEvent syncEvent, long ticks)
    {
        switch (syncEvent)
        {
            case ChromeSyncMouseEvent { Type: "mouseMoved" }:
                Interlocked.Exchange(ref _lastBridgeMoveTicks, ticks);
                break;
            case ChromeSyncMouseEvent { Type: "mousePressed" }:
                Interlocked.Exchange(ref _lastBridgePressTicks, ticks);
                break;
            case ChromeSyncMouseEvent { Type: "mouseReleased" }:
                Interlocked.Exchange(ref _lastBridgeReleaseTicks, ticks);
                break;
            case ChromeSyncMouseEvent { Type: "mouseWheel" }:
                Interlocked.Exchange(ref _lastBridgeWheelTicks, ticks);
                break;
            case ChromeSyncKeyEvent { Type: "keyDown" }:
                Interlocked.Exchange(ref _lastBridgeKeyDownTicks, ticks);
                break;
            case ChromeSyncKeyEvent { Type: "keyUp" }:
                Interlocked.Exchange(ref _lastBridgeKeyUpTicks, ticks);
                break;
        }
    }

    private void MarkInput(ChromeSyncEvent syncEvent, long ticks)
    {
        if (syncEvent is ChromeSyncMouseEvent { Type: "mouseMoved" })
        {
            return;
        }

        Interlocked.Exchange(ref _lastInputTicks, ticks);
    }

    private long GetBridgeTicks(ChromeSyncEvent syncEvent)
    {
        return syncEvent switch
        {
            ChromeSyncMouseEvent { Type: "mouseMoved" } => Interlocked.Read(ref _lastBridgeMoveTicks),
            ChromeSyncMouseEvent { Type: "mousePressed" } => Interlocked.Read(ref _lastBridgePressTicks),
            ChromeSyncMouseEvent { Type: "mouseReleased" } => Interlocked.Read(ref _lastBridgeReleaseTicks),
            ChromeSyncMouseEvent { Type: "mouseWheel" } => Interlocked.Read(ref _lastBridgeWheelTicks),
            ChromeSyncKeyEvent { Type: "keyDown" } => Interlocked.Read(ref _lastBridgeKeyDownTicks),
            ChromeSyncKeyEvent { Type: "keyUp" } => Interlocked.Read(ref _lastBridgeKeyUpTicks),
            _ => 0
        };
    }

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        if (message is WmMouseMove or WmLeftButtonDown or WmLeftButtonUp or WmRightButtonDown or WmRightButtonUp or WmMiddleButtonDown or WmMiddleButtonUp or WmMouseWheel)
        {
            var data = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            TryCaptureMouse(message, data);
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        if (message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
        {
            var data = Marshal.PtrToStructure<KeyboardHookStruct>(lParam);
            TryCaptureKey(message, data);
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void TryCaptureMouse(int message, MouseHookStruct data)
    {
        SyncedChromeTab? source;
        lock (_gate)
        {
            source = FindSourceTabByPoint(_tabs.Where(tab => tab.IsMaster), data.Point);
        }

        if (source is null)
        {
            return;
        }

        if (source.MainWindow == IntPtr.Zero)
        {
            source.MainWindow = FindMainWindow(source.Session.Process.Id);
        }

        if (source.MainWindow != IntPtr.Zero)
        {
            source.ContentRect = CalibrateContentRect(source, FindContentRect(source.MainWindow));
        }

        var rect = source.ContentRect;
        if (rect.IsEmpty || !rect.Contains(data.Point.X, data.Point.Y))
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (message == WmMouseMove)
        {
            var elapsedMs = (now - Interlocked.Read(ref _lastMouseMoveTicks)) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs < 16)
            {
                return;
            }

            Interlocked.Exchange(ref _lastMouseMoveTicks, now);
        }

        var xRatio = (data.Point.X - rect.Left) / (double)Math.Max(1, rect.Width);
        var yRatio = (data.Point.Y - rect.Top) / (double)Math.Max(1, rect.Height);
        var (type, button, buttons, clickCount, wheelDeltaY) = ConvertMouseMessage(message, data.MouseData);
        if (type.Length == 0)
        {
            return;
        }

        if (type == "mouseMoved")
        {
            buttons = GetPressedMouseButtons();
            if (buttons == 0)
            {
                return;
            }
        }

        var syncEvent = new ChromeSyncMouseEvent(
            Interlocked.Increment(ref _sequence),
            source.Session.StableId,
            type,
            Math.Clamp(xRatio, 0, 1),
            Math.Clamp(yRatio, 0, 1),
            button,
            buttons,
            clickCount,
            0,
            wheelDeltaY,
            GetCdpModifiers());
        MarkInput(syncEvent, now);
        QueueWin32Fallback(syncEvent, now);
    }

    private void TryCaptureKey(int message, KeyboardHookStruct data)
    {
        SyncedChromeTab? source;
        lock (_gate)
        {
            var active = FindActiveSourceTab(_tabs);
            source = active is { IsMaster: true } ? active : null;
        }

        if (source is null)
        {
            return;
        }

        var isDown = message is WmKeyDown or WmSysKeyDown;
        var virtualKey = (int)data.VirtualKeyCode;
        var text = isDown ? GetPrintableText(virtualKey) : "";
        var now = Stopwatch.GetTimestamp();
        var syncEvent = new ChromeSyncKeyEvent(
            Interlocked.Increment(ref _sequence),
            source.Session.StableId,
            isDown ? "keyDown" : "keyUp",
            virtualKey,
            GetCdpKey(virtualKey),
            GetCdpCode(virtualKey),
            text,
            GetCdpModifiers(),
            false);
        MarkInput(syncEvent, now);
        QueueWin32Fallback(syncEvent, now);
    }

    private void QueueWin32Fallback(ChromeSyncEvent syncEvent, long capturedTicks)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Win32FallbackDelayMs);
                if (!IsRunning || GetBridgeTicks(syncEvent) >= capturedTicks)
                {
                    return;
                }

                MarkInput(syncEvent, capturedTicks);
                _events.Writer.TryWrite(syncEvent);
            }
            catch
            {
            }
        });
    }

    private void InstallHooks()
    {
        if (_mouseHook == IntPtr.Zero)
        {
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Could not install mouse sync hook. Win32 error: {Marshal.GetLastWin32Error()}");
            }
        }

        if (_keyboardHook == IntPtr.Zero)
        {
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Could not install keyboard sync hook. Win32 error: {Marshal.GetLastWin32Error()}");
            }
        }
    }

    private void UninstallHooks()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private static string ExtractNavigationUrl(string method, JsonElement payload)
    {
        try
        {
            if (method == "Page.navigatedWithinDocument"
                && payload.TryGetProperty("params", out var withinParams)
                && withinParams.TryGetProperty("url", out var withinUrl))
            {
                return withinUrl.GetString() ?? "";
            }

            if (payload.TryGetProperty("params", out var frameParams)
                && frameParams.TryGetProperty("frame", out var frame)
                && !frame.TryGetProperty("parentId", out _)
                && frame.TryGetProperty("url", out var url))
            {
                return url.GetString() ?? "";
            }
        }
        catch
        {
        }

        return "";
    }

    private static (string Type, string Button, int Buttons, int ClickCount, double WheelDeltaY) ConvertMouseMessage(int message, int mouseData)
    {
        return message switch
        {
            WmMouseMove => ("mouseMoved", "none", 0, 0, 0),
            WmLeftButtonDown => ("mousePressed", "left", 1, 1, 0),
            WmLeftButtonUp => ("mouseReleased", "left", 0, 1, 0),
            WmRightButtonDown => ("mousePressed", "right", 2, 1, 0),
            WmRightButtonUp => ("mouseReleased", "right", 0, 1, 0),
            WmMiddleButtonDown => ("mousePressed", "middle", 4, 1, 0),
            WmMiddleButtonUp => ("mouseReleased", "middle", 0, 1, 0),
            WmMouseWheel => ("mouseWheel", "none", 0, 0, -GetWheelDelta(mouseData)),
            _ => ("", "none", 0, 0, 0)
        };
    }

    private static int GetWheelDelta(int mouseData)
    {
        return unchecked((short)((mouseData >> 16) & 0xFFFF));
    }

    private static int ButtonMask(string button)
    {
        return button switch
        {
            "left" => 1,
            "right" => 2,
            "middle" => 4,
            _ => 0
        };
    }

    private static int GetPressedMouseButtons()
    {
        var buttons = 0;
        if ((GetKeyState(VkLeftButton) & 0x8000) != 0)
        {
            buttons |= 1;
        }

        if ((GetKeyState(VkRightButton) & 0x8000) != 0)
        {
            buttons |= 2;
        }

        if ((GetKeyState(VkMiddleButton) & 0x8000) != 0)
        {
            buttons |= 4;
        }

        return buttons;
    }


    private static bool TryGetRuntimeValue(JsonElement result, out JsonElement value)
    {
        value = default;
        if (result.TryGetProperty("result", out var inner)
            && inner.TryGetProperty("value", out value))
        {
            return true;
        }

        if (result.TryGetProperty("value", out value))
        {
            return true;
        }

        return false;
    }

    private static double TryGetDouble(JsonElement element, string propertyName, double fallback)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value) && value > 0
            ? value
            : fallback;
    }

    private static string TryGetString(JsonElement element, string propertyName, string fallback)
    {
        return element.TryGetProperty(propertyName, out var property) ? property.GetString() ?? fallback : fallback;
    }

    private static int TryGetInt(JsonElement element, string propertyName, int fallback)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : fallback;
    }

    private static (int Columns, int Rows) CalculateGrid(int count, int width, int height)
    {
        if (count <= 1)
        {
            return (1, 1);
        }

        if (width >= height * 2 && count > 6)
        {
            var rows = 2;
            return ((int)Math.Ceiling(count / (double)rows), rows);
        }

        var columns = count switch
        {
            <= 2 => count,
            <= 4 => 2,
            <= 9 => 3,
            _ => 4
        };
        var rowsNeeded = (int)Math.Ceiling(count / (double)columns);
        return (columns, rowsNeeded);
    }

    private static Rect FindContentRect(IntPtr rootWindow)
    {
        var best = Rect.Empty;
        EnumChildWindows(rootWindow, (child, _) =>
        {
            var className = GetClassName(child);
            if (className.Contains("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase)
                && GetWindowRect(child, out var childRect)
                && childRect.Width > best.Width
                && childRect.Height > best.Height)
            {
                best = childRect;
            }

            return true;
        }, IntPtr.Zero);

        if (!best.IsEmpty)
        {
            return best;
        }

        if (!GetWindowRect(rootWindow, out var root))
        {
            return Rect.Empty;
        }

        return new Rect(root.Left + 8, root.Top + 88, root.Right - 8, root.Bottom - 8);
    }

    private static Rect EstimateChromiumContentRect(Rect root)
    {
        if (root.IsEmpty)
        {
            return Rect.Empty;
        }

        var topInset = Math.Min(104, Math.Max(72, root.Height / 8));
        return new Rect(root.Left + 8, root.Top + topInset, root.Right - 8, root.Bottom - 8);
    }

    private static Rect CalibrateContentRect(SyncedChromeTab tab, Rect detected)
    {
        var anchor = !detected.IsEmpty ? detected : EstimateChromiumContentRect(tab.WindowRect);
        if (anchor.IsEmpty || tab.ViewportWidth <= 0 || tab.ViewportHeight <= 0)
        {
            return anchor;
        }

        if (!tab.WindowRect.IsEmpty && tab.OuterWidth > 0 && tab.OuterHeight > 0)
        {
            var scaleX = Math.Clamp(tab.WindowRect.Width / tab.OuterWidth, 0.5, 4);
            var scaleY = Math.Clamp(tab.WindowRect.Height / tab.OuterHeight, 0.5, 4);
            var sideCss = Math.Max(0, (tab.OuterWidth - tab.ViewportWidth) / 2);
            var topCss = Math.Max(0, tab.OuterHeight - tab.ViewportHeight - sideCss);
            var left = tab.WindowRect.Left + (int)Math.Round(sideCss * scaleX);
            var top = tab.WindowRect.Top + (int)Math.Round(topCss * scaleY);
            var right = left + (int)Math.Round(tab.ViewportWidth * scaleX);
            var bottom = top + (int)Math.Round(tab.ViewportHeight * scaleY);
            var windowCalibrated = new Rect(left, top, right, bottom);
            if (!windowCalibrated.IsEmpty
                && tab.WindowRect.Contains(windowCalibrated.Left, windowCalibrated.Top)
                && tab.WindowRect.Contains(windowCalibrated.Right - 1, windowCalibrated.Bottom - 1))
            {
                return windowCalibrated;
            }
        }

        var scale = Math.Clamp(tab.DeviceScaleFactor, 0.5, 4);
        var width = (int)Math.Round(tab.ViewportWidth * scale);
        var height = (int)Math.Round(tab.ViewportHeight * scale);
        if (width <= 0 || height <= 0)
        {
            return anchor;
        }

        var maxWidth = !tab.WindowRect.IsEmpty ? tab.WindowRect.Width : anchor.Width;
        var maxHeight = !tab.WindowRect.IsEmpty ? tab.WindowRect.Height : anchor.Height;
        if (width > maxWidth + 24 || height > maxHeight + 120)
        {
            return anchor;
        }

        var anchorRight = anchor.Right;
        var anchorBottom = anchor.Bottom;
        var calibrated = new Rect(anchorRight - width, anchorBottom - height, anchorRight, anchorBottom);
        if (!tab.WindowRect.IsEmpty
            && (!tab.WindowRect.Contains(calibrated.Left, calibrated.Top)
                || !tab.WindowRect.Contains(calibrated.Right - 1, calibrated.Bottom - 1)))
        {
            return anchor;
        }

        return calibrated;
    }

    private static SyncedChromeTab? FindSourceTabByPoint(IEnumerable<SyncedChromeTab> tabs, Point point)
    {
        SyncedChromeTab? fallback = null;
        foreach (var tab in tabs)
        {
            if (tab.ContentRect.Contains(point.X, point.Y))
            {
                return tab;
            }

            if (fallback is null && tab.WindowRect.Contains(point.X, point.Y))
            {
                fallback = tab;
            }
        }

        return fallback;
    }

    private static SyncedChromeTab? FindActiveSourceTab(IEnumerable<SyncedChromeTab> tabs)
    {
        var snapshot = tabs.ToList();
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            GetWindowThreadProcessId(foreground, out var foregroundProcessId);
            foreach (var tab in snapshot)
            {
                if ((tab.MainWindow != IntPtr.Zero && tab.MainWindow == foreground)
                    || tab.Session.Process.Id == foregroundProcessId)
                {
                    return tab;
                }
            }
        }

        return GetCursorPos(out var point)
            ? FindSourceTabByPoint(snapshot, point)
            : null;
    }

    private static IntPtr FindMainWindow(int processId)
    {
        var result = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowProcessId);
            if (windowProcessId == processId && IsWindowVisible(hwnd))
            {
                result = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        var length = GetClassName(hwnd, builder, builder.Capacity);
        return length <= 0 ? "" : builder.ToString(0, length);
    }

    private static int GetCdpModifiers()
    {
        var modifiers = 0;
        if ((GetKeyState(VkAlt) & 0x8000) != 0)
        {
            modifiers |= 1;
        }
        if ((GetKeyState(VkControl) & 0x8000) != 0)
        {
            modifiers |= 2;
        }
        if ((GetKeyState(VkShift) & 0x8000) != 0)
        {
            modifiers |= 8;
        }

        return modifiers;
    }

    private static string GetPrintableText(int virtualKey)
    {
        if ((GetCdpModifiers() & (1 | 2)) != 0)
        {
            return "";
        }

        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            var c = (char)virtualKey;
            var shift = (GetKeyState(VkShift) & 0x8000) != 0;
            return shift ? c.ToString() : char.ToLowerInvariant(c).ToString();
        }

        return virtualKey switch
        {
            VkSpace => " ",
            _ => ""
        };
    }

    private static string GetCdpKey(int virtualKey)
    {
        return virtualKey switch
        {
            VkBack => "Backspace",
            VkTab => "Tab",
            VkEnter => "Enter",
            VkShift => "Shift",
            VkControl => "Control",
            VkAlt => "Alt",
            VkEscape => "Escape",
            VkSpace => " ",
            VkPageUp => "PageUp",
            VkPageDown => "PageDown",
            VkEnd => "End",
            VkHome => "Home",
            VkLeft => "ArrowLeft",
            VkUp => "ArrowUp",
            VkRight => "ArrowRight",
            VkDown => "ArrowDown",
            VkDelete => "Delete",
            _ when virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
            _ => virtualKey.ToString()
        };
    }

    private static string GetCdpCode(int virtualKey)
    {
        return virtualKey switch
        {
            VkBack => "Backspace",
            VkTab => "Tab",
            VkEnter => "Enter",
            VkShift => "ShiftLeft",
            VkControl => "ControlLeft",
            VkAlt => "AltLeft",
            VkEscape => "Escape",
            VkSpace => "Space",
            VkPageUp => "PageUp",
            VkPageDown => "PageDown",
            VkEnd => "End",
            VkHome => "Home",
            VkLeft => "ArrowLeft",
            VkUp => "ArrowUp",
            VkRight => "ArrowRight",
            VkDown => "ArrowDown",
            VkDelete => "Delete",
            _ when virtualKey is >= 0x30 and <= 0x39 => $"Digit{(char)virtualKey}",
            _ when virtualKey is >= 0x41 and <= 0x5A => $"Key{(char)virtualKey}",
            _ => ""
        };
    }

    private static bool IsScrollKey(int virtualKey)
    {
        return virtualKey is VkSpace or VkPageUp or VkPageDown or VkEnd or VkHome or VkLeft or VkUp or VkRight or VkDown;
    }

    private abstract record ChromeSyncEvent(long Sequence);

    private sealed record ChromeSyncMouseEvent(
        long Sequence,
        string SourceStableId,
        string Type,
        double XRatio,
        double YRatio,
        string Button,
        int Buttons,
        int ClickCount,
        double WheelDeltaX,
        double WheelDeltaY,
        int Modifiers) : ChromeSyncEvent(Sequence);

    private sealed record ChromeSyncKeyEvent(
        long Sequence,
        string SourceStableId,
        string Type,
        int VirtualKey,
        string Key,
        string Code,
        string Text,
        int Modifiers,
        bool AutoRepeat) : ChromeSyncEvent(Sequence);

    private sealed class SyncedChromeTab(SystemBrowserSyncSession session, ChromeDevToolsClient client, bool isMaster) : IDisposable
    {
        public SystemBrowserSyncSession Session { get; } = session;
        public ChromeDevToolsClient Client { get; } = client;
        public bool IsMaster { get; } = isMaster;
        public IntPtr MainWindow { get; set; }
        public Rect WindowRect { get; set; } = Rect.Empty;
        public Rect ContentRect { get; set; } = Rect.Empty;
        public double ViewportWidth { get; set; } = 1280;
        public double ViewportHeight { get; set; } = 720;
        public double OuterWidth { get; set; }
        public double OuterHeight { get; set; }
        public double DeviceScaleFactor { get; set; } = 1;
        public double ScrollX { get; set; }
        public double ScrollY { get; set; }
        public double ScrollWidth { get; set; } = 1280;
        public double ScrollHeight { get; set; } = 720;
        public string Url { get; set; } = "";
        public Action<string, JsonElement>? DevToolsEventHandler { get; set; }

        public void Dispose()
        {
            Client.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public Rect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public static Rect Empty { get; } = new(0, 0, 0, 0);
        public readonly int Width => Math.Max(0, Right - Left);
        public readonly int Height => Math.Max(0, Bottom - Top);
        public readonly bool IsEmpty => Width <= 0 || Height <= 0;
        public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonDown = 0x0204;
    private const int WmRightButtonUp = 0x0205;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmMiddleButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int ShowWindowRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;
    private const int VkLeftButton = 0x01;
    private const int VkRightButton = 0x02;
    private const int VkMiddleButton = 0x04;
    private const int VkBack = 0x08;
    private const int VkTab = 0x09;
    private const int VkEnter = 0x0D;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkAlt = 0x12;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const int VkPageUp = 0x21;
    private const int VkPageDown = 0x22;
    private const int VkEnd = 0x23;
    private const int VkHome = 0x24;
    private const int VkLeft = 0x25;
    private const int VkUp = 0x26;
    private const int VkRight = 0x27;
    private const int VkDown = 0x28;
    private const int VkDelete = 0x2E;
    private const int Win32FallbackDelayMs = 28;
    private const int ScrollSyncIntervalMs = 55;
    private const int ScrollAnimationMs = 85;
    private const int ScrollCorrectionDebounceMs = 220;
    private const double ScrollCorrectionTolerancePx = 3;
    private const double ScrollTopSnapThresholdPx = 6;
    private const int NavigationReplayInputQuietMs = 3500;
    private const int CdpModifierControl = 2;
    private const string SyncBindingName = "__proxySentrySyncEvent";
    private const string PageSyncBridgeScript = """
(() => {
  const bindingName = "__proxySentrySyncEvent";
  if (window.__proxySentrySyncInstalled) {
    return true;
  }
  window.__proxySentrySyncInstalled = true;

  const getViewport = () => ({
    width: Math.max(1, window.innerWidth || (document.documentElement && document.documentElement.clientWidth) || 1),
    height: Math.max(1, window.innerHeight || (document.documentElement && document.documentElement.clientHeight) || 1)
  });

  const modifiers = (event) =>
    (event.altKey ? 1 : 0) |
    (event.ctrlKey ? 2 : 0) |
    (event.metaKey ? 4 : 0) |
    (event.shiftKey ? 8 : 0);

  const buttonName = (button) => {
    if (button === 0) return "left";
    if (button === 1) return "middle";
    if (button === 2) return "right";
    return "none";
  };

  const send = (payload) => {
    try {
      const binding = window[bindingName];
      if (typeof binding !== "function") {
        return;
      }

      binding(JSON.stringify({
        ...payload,
        href: String(location.href || ""),
        timestamp: Date.now()
      }));
    } catch {
    }
  };

  let lastMoveAt = 0;
  const sendMouse = (event, type) => {
    const viewport = getViewport();
    const x = Math.max(0, Math.min(viewport.width - 1, event.clientX));
    const y = Math.max(0, Math.min(viewport.height - 1, event.clientY));
    send({
      kind: "mouse",
      type,
      viewportWidth: viewport.width,
      viewportHeight: viewport.height,
      scrollX: window.scrollX || (window.visualViewport ? window.visualViewport.pageLeft : 0) || 0,
      scrollY: window.scrollY || (window.visualViewport ? window.visualViewport.pageTop : 0) || 0,
      scrollWidth: Math.max(document.documentElement ? document.documentElement.scrollWidth : 0, document.body ? document.body.scrollWidth : 0, viewport.width),
      scrollHeight: Math.max(document.documentElement ? document.documentElement.scrollHeight : 0, document.body ? document.body.scrollHeight : 0, viewport.height),
      xRatio: x / viewport.width,
      yRatio: y / viewport.height,
      button: type === "mouseMoved" || type === "mouseWheel" ? "none" : buttonName(event.button),
      buttons: type === "mouseWheel" ? 0 : (event.buttons || 0),
      clickCount: type === "mousePressed" || type === "mouseReleased" ? 1 : 0,
      deltaX: type === "mouseWheel" ? event.deltaX || 0 : 0,
      deltaY: type === "mouseWheel" ? event.deltaY || 0 : 0,
      modifiers: modifiers(event)
    });
  };

  window.addEventListener("mousemove", (event) => {
    const now = performance.now();
    if (now - lastMoveAt < 16) {
      return;
    }

    lastMoveAt = now;
    sendMouse(event, "mouseMoved");
  }, true);
  window.addEventListener("mousedown", (event) => sendMouse(event, "mousePressed"), true);
  window.addEventListener("mouseup", (event) => sendMouse(event, "mouseReleased"), true);
  window.addEventListener("wheel", (event) => sendMouse(event, "mouseWheel"), { capture: true, passive: true });

  const sendKey = (event, type) => {
    const viewport = getViewport();
    const printable = type === "keyDown"
      && event.key
      && event.key.length === 1
      && !event.ctrlKey
      && !event.altKey
      && !event.metaKey;
    send({
      kind: "key",
      type,
      viewportWidth: viewport.width,
      viewportHeight: viewport.height,
      scrollX: window.scrollX || (window.visualViewport ? window.visualViewport.pageLeft : 0) || 0,
      scrollY: window.scrollY || (window.visualViewport ? window.visualViewport.pageTop : 0) || 0,
      scrollWidth: Math.max(document.documentElement ? document.documentElement.scrollWidth : 0, document.body ? document.body.scrollWidth : 0, viewport.width),
      scrollHeight: Math.max(document.documentElement ? document.documentElement.scrollHeight : 0, document.body ? document.body.scrollHeight : 0, viewport.height),
      key: event.key || "",
      code: event.code || "",
      virtualKey: event.keyCode || event.which || 0,
      text: printable ? event.key : "",
      modifiers: modifiers(event),
      autoRepeat: !!event.repeat
    });
  };

  window.addEventListener("keydown", (event) => sendKey(event, "keyDown"), true);
  window.addEventListener("keyup", (event) => sendKey(event, "keyUp"), true);
  return true;
})();
""";

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookStruct
    {
        public Point Point;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookStruct
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
}
