using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.UI.Dispatching;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public sealed class EventSentryBridgeServer : IDisposable
{
    public const string PipeName = "ProxySentryCSV.EventSentry.v1";
    private const int ProtocolVersion = 1;
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly TimeSpan StaleLeaseAfter = TimeSpan.FromSeconds(90);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly DispatcherQueue _dispatcher;
    private readonly Func<IReadOnlyList<ProxyEntry>> _getProfiles;
    private readonly SystemBrowserLauncher _launcher;
    private readonly Action<string> _log;
    private readonly Action _saveState;
    private readonly Action _updateSummary;
    private readonly object _gate = new();
    private readonly Dictionary<string, BridgeLease> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private Task? _serverTask;
    private bool _compatibilityMode;
    private int _acquiring;
    private volatile bool _listening;
    private readonly SemaphoreSlim _connections = new(8, 8);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private string _lastError = "";
    private string _lastCommand = "";
    private DateTimeOffset? _lastRequest;
    private int _received;
    public string DiagnosticState { get; private set; } = "Stopped";
    public string DiagnosticSummary
    {
        get
        {
            lock (_gate) return $"State: {DiagnosticState}\nPipe: {PipeName}\nProtocol: {ProtocolVersion}\nRequests: {_received}\nLast request (UTC): {_lastRequest:O}\nLast command: {_lastCommand}\nLast error: {_lastError}\nAdsPower API acquisition lock: {_compatibilityMode}\nActive acquisitions: {_acquiring}\nLeases: {_leases.Count}";
        }
    }

    private void SetDiagnosticState(string state, Exception? error = null)
    {
        lock (_gate)
        {
            DiagnosticState = state;
            if (error is not null) _lastError = $"{error.GetType().Name} (0x{error.HResult:X8}): {error.Message}";
        }
        StateChanged?.Invoke();
    }

    public bool TrySetCompatibilityMode(bool enabled)
    {
        lock (_gate)
        {
            if (enabled && (_acquiring > 0 || _leases.Count > 0)) return false;
            _compatibilityMode = enabled;
            return true;
        }
    }

    public EventSentryBridgeServer(
        DispatcherQueue dispatcher,
        Func<IReadOnlyList<ProxyEntry>> getProfiles,
        SystemBrowserLauncher launcher,
        Action<string> log,
        Action saveState,
        Action updateSummary)
    {
        _dispatcher = dispatcher;
        _getProfiles = getProfiles;
        _launcher = launcher;
        _log = log;
        _saveState = saveState;
        _updateSummary = updateSummary;
    }

    public event Action? StateChanged;

    public bool IsRunning => _listening && !_stop.IsCancellationRequested;

    public void Start()
    {
        if (_serverTask is not null)
        {
            return;
        }

        SetDiagnosticState("Starting");
        _serverTask = Task.Run(ServerLoopAsync);
    }

    public int ReleaseStaleLeases()
    {
        var now = DateTimeOffset.UtcNow;
        List<BridgeLease> released = [];
        lock (_gate)
        {
            foreach (var lease in _leases.Values.ToList())
            {
                if (!IsLeaseStale(lease, now)
                    || IsProtectedState(lease.State))
                {
                    continue;
                }

                _leases.Remove(lease.ProfileId);
                released.Add(lease);
            }
        }

        foreach (var lease in released)
        {
            _ = UpdateProxyAsync(lease.ProfileId, proxy =>
            {
                proxy.EventSentryState = "Ready";
                proxy.EventSentryRunId = "";
                proxy.EventSentryHeartbeat = null;
            });
        }

        if (released.Count > 0)
        {
            _log($"Event Sentry bridge released {released.Count} stale lease(s).");
            StateChanged?.Invoke();
        }

        return released.Count;
    }

    public void Dispose()
    {
        try
        {
            _stop.Cancel();
        }
        catch
        {
        }
    }

    private async Task ServerLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            var entered = false;
            try
            {
                await _connections.WaitAsync(_stop.Token);
                entered = true;
                pipe = CreatePipe();
                _listening = true;
                SetDiagnosticState("Listening");
                await pipe.WaitForConnectionAsync(_stop.Token);
                _ = ServeConnectionAsync(pipe);
                pipe = null;
                entered = false;
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _listening = false;
                SetDiagnosticState("Error", ex);
                _log($"Event Sentry bridge error: {ex.Message}");
                await Task.Delay(500, _stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
            finally { if (entered) _connections.Release(); }
        }
        _listening = false;
        SetDiagnosticState("Stopped");
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe)
    {
        try { await HandleConnectionAsync(pipe); }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        { SetDiagnosticState("Client error", ex); }
        catch (OperationCanceledException) { }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        finally { pipe.Dispose(); _connections.Release(); }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var currentSid = WindowsIdentity.GetCurrent().User;
        if (currentSid is null)
        {
            throw new InvalidOperationException("Could not resolve current Windows user SID for Event Sentry bridge pipe.");
        }

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            currentSid,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.SetOwner(currentSid);

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            MaxMessageBytes,
            MaxMessageBytes,
            security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            BridgeRequest request;
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await reader.ReadLineAsync(readTimeout.Token);
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                if (Encoding.UTF8.GetByteCount(line) > MaxMessageBytes)
                {
                    await WriteResponseAsync(pipe, BridgeResponse.Fail(ProtocolVersion, "", "Request is larger than 1 MB."));
                    return;
                }

                request = JsonSerializer.Deserialize<BridgeRequest>(line, JsonOptions) ?? new BridgeRequest();
                lock (_gate)
                {
                    if (!(request.RequestId ?? "").StartsWith("diagnostic-", StringComparison.Ordinal))
                    {
                        _received++;
                        _lastRequest = DateTimeOffset.UtcNow;
                        _lastCommand = NormalizeCommand(request.Command) is "status" or "acquire_etix_profiles" or "set_profile_status" or "bring_to_front" or "release_etix_profiles"
                            ? NormalizeCommand(request.Command) : "unsupported";
                    }
                }
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                lock (_gate) _lastError = $"Request read failed: {ex.GetType().Name}, 0x{ex.HResult:X8}";
                StateChanged?.Invoke();
                await WriteResponseAsync(pipe, BridgeResponse.Fail(ProtocolVersion, "", $"Invalid request: {ex.Message}"));
                return;
            }

            BridgeResponse response;
            try
            {
                // Status probes stay available during a long browser launch. Mutations remain serialized.
                if (NormalizeCommand(request.Command) == "status") response = await HandleRequestAsync(request, _stop.Token);
                else
                {
                    await _commands.WaitAsync(_stop.Token);
                    try { response = await HandleRequestAsync(request, _stop.Token); }
                    finally { _commands.Release(); }
                }
            }
            catch (Exception ex)
            {
                response = BridgeResponse.Fail(ProtocolVersion, request.RequestId ?? "", ex.Message);
            }

            if (!response.Ok)
            {
                lock (_gate) _lastError = request.ProtocolVersion != ProtocolVersion
                    ? $"Protocol mismatch: client {request.ProtocolVersion}, server {ProtocolVersion}. Update both applications."
                    : _compatibilityMode && NormalizeCommand(request.Command) == "acquire_etix_profiles"
                        ? "Acquisition blocked by AdsPower compatibility mode."
                        : NormalizeCommand(request.Command) == "acquire_etix_profiles" && NormalizeCommand(request.Browser) != "auto"
                            ? "Legacy client browser selection. Update Event Sentry (browser=auto is required)."
                            : $"Command {_lastCommand} was rejected; inspect Event Sentry's error for details.";
                StateChanged?.Invoke();
            }
            await WriteResponseAsync(pipe, response);
        }
    }

    private static async Task WriteResponseAsync(PipeStream pipe, BridgeResponse response)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.WriteAsync(bytes, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
    }

    private async Task<BridgeResponse> HandleRequestAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        if (request.ProtocolVersion != ProtocolVersion)
        {
            return BridgeResponse.Fail(
                ProtocolVersion,
                request.RequestId,
                $"Unsupported protocol version. Expected {ProtocolVersion}.");
        }

        CleanupStaleLeases();
        return NormalizeCommand(request.Command) switch
        {
            "status" => await HandleStatusAsync(request),
            "acquire_etix_profiles" => await HandleAcquireAsync(request, cancellationToken),
            "set_profile_status" => await HandleSetProfileStatusAsync(request),
            "bring_to_front" => await HandleBringToFrontAsync(request, cancellationToken),
            "release_etix_profiles" => await HandleReleaseAsync(request, cancellationToken),
            _ => BridgeResponse.Fail(ProtocolVersion, request.RequestId, $"Unknown command: {request.Command}")
        };
    }

    private async Task<BridgeResponse> HandleStatusAsync(BridgeRequest request)
    {
        var profiles = await GetEventProfilesAsync();
        var now = DateTimeOffset.UtcNow;
        int busy;
        lock (_gate)
        {
            busy = _leases.Values.Count(lease => !IsLeaseStale(lease, now));
        }

        var ready = profiles.Count(proxy => !HasActiveLease(proxy.ProfileKey, now)
            && !IsProtectedState(proxy.EventSentryState));

        return BridgeResponse.Success(ProtocolVersion, request.RequestId, "Event Sentry bridge is running.")
            with
            {
                SelectedCount = profiles.Count,
                ReadyCount = ready,
                BusyCount = busy,
                ServerProcessId = Environment.ProcessId,
                ServerVersion = typeof(EventSentryBridgeServer).Assembly.GetName().Version?.ToString(),
                AcquisitionBlocked = _compatibilityMode
            };
    }

    private async Task<BridgeResponse> HandleAcquireAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_compatibilityMode)
                return BridgeResponse.Fail(ProtocolVersion, request.RequestId, "Disable AdsPower compatibility in Proxy Sentry Settings before acquiring Event Sentry bridge profiles.");
            _acquiring++;
        }
        try { return await HandleAcquireCoreAsync(request, cancellationToken); }
        finally { lock (_gate) _acquiring--; }
    }

    private async Task<BridgeResponse> HandleAcquireCoreAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            return BridgeResponse.Fail(ProtocolVersion, request.RequestId, "runId is required.");
        }

        var browser = NormalizeCommand(request.Browser);
        if (browser != "auto")
        {
            return BridgeResponse.Fail(ProtocolVersion, request.RequestId, "Update Event Sentry: browser must be selected per profile in Proxy Sentry (browser=auto).");
        }

        var candidates = await GetEventProfilesAsync();
        if (candidates.Count == 0)
        {
            return BridgeResponse.Fail(ProtocolVersion, request.RequestId, "No profiles are marked Use for Event Sentry.");
        }

        var acquired = new List<BridgeProfile>();
        var failures = new List<string>();
        foreach (var proxy in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profileId = proxy.ProfileKey;
            BridgeLease? existing;
            lock (_gate)
            {
                _leases.TryGetValue(profileId, out existing);
            }

            if (existing is not null
                && !string.Equals(existing.RunId, request.RunId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (existing is not null
                && string.Equals(existing.RunId, request.RunId, StringComparison.OrdinalIgnoreCase))
            {
                existing.Touch(existing.State);
                acquired.Add(existing.ToProfile(proxy));
                await UpdateProxyAsync(profileId, proxyEntry =>
                {
                    proxyEntry.EventSentryHeartbeat = existing.Heartbeat;
                    proxyEntry.EventSentryRunId = existing.RunId;
                    proxyEntry.EventSentryState = ToDisplayState(existing.State);
                });
                continue;
            }

            if (IsProtectedState(proxy.EventSentryState))
            {
                continue;
            }

            var requiresCleanup = NormalizeStatus(proxy.EventSentryState) == "cleanup_required";

            await UpdateProxyAsync(profileId, proxyEntry =>
            {
                proxyEntry.EventSentryState = "Starting";
                proxyEntry.EventSentryRunId = request.RunId;
                proxyEntry.EventSentryHeartbeat = DateTimeOffset.Now;
                proxyEntry.Status = "Event Sentry starting";
            });

            try
            {
                var leaseResult = await _launcher.EnsureNativeReadyForEventSentryAsync(
                    proxy, request.RunId, request.Background,
                    line => _log($"Event Sentry {profileId}: {line}"), cancellationToken);
                var lease = new BridgeLease(profileId, request.RunId, "leased", leaseResult.CdpEndpoint,
                    leaseResult.Browser, leaseResult.ProcessId, requiresCleanup, leaseResult.BidiEndpoint);
                lock (_gate)
                {
                    _leases[profileId] = lease;
                }

                await UpdateProxyAsync(profileId, proxyEntry =>
                {
                    proxyEntry.IsOpen = true;
                    proxyEntry.OpenedProvider = leaseResult.Browser;
                    proxyEntry.Used = true;
                    proxyEntry.EventSentryState = "Leased";
                    proxyEntry.EventSentryRunId = request.RunId;
                    proxyEntry.EventSentryHeartbeat = lease.Heartbeat.ToLocalTime();
                    proxyEntry.Status = "Event Sentry leased";
                });

                acquired.Add(lease.ToProfile(proxy));
            }
            catch (Exception ex)
            {
                failures.Add($"{profileId}: {ex.Message}");
                await UpdateProxyAsync(profileId, proxyEntry =>
                {
                    proxyEntry.EventSentryState = "Error";
                    proxyEntry.Status = "Event Sentry error";
                });
                _log($"Event Sentry acquire failed for {profileId}: {ex.Message}");
            }
        }

        if (acquired.Count == 0)
        {
            return BridgeResponse.Fail(ProtocolVersion, request.RequestId, failures.Count > 0
                ? "No Event Sentry profiles could be acquired. " + string.Join(" ", failures.Take(3))
                : "No Event Sentry profiles could be acquired. The selected profiles may be busy or holding carts.")
                with
                {
                    SelectedCount = candidates.Count,
                    ReadyCount = 0,
                    BusyCount = candidates.Count
                };
        }

        _saveState();
        _updateSummary();
        StateChanged?.Invoke();
        return BridgeResponse.Success(ProtocolVersion, request.RequestId, $"{acquired.Count} profiles acquired.")
            with
            {
                SelectedCount = candidates.Count,
                ReadyCount = acquired.Count,
                BusyCount = candidates.Count - acquired.Count,
                Profiles = acquired
            };
    }

    private async Task<BridgeResponse> HandleSetProfileStatusAsync(BridgeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RunId)
            || string.IsNullOrWhiteSpace(request.ProfileId)
            || string.IsNullOrWhiteSpace(request.Status))
        {
            return BridgeResponse.Fail(ProtocolVersion, request.RequestId, "runId, profileId, and status are required.");
        }

        var normalizedStatus = NormalizeStatus(request.Status);
        if (!AllowedStatuses.Contains(normalizedStatus))
        {
            return BridgeResponse.Fail(ProtocolVersion, request.RequestId, $"Unsupported status: {request.Status}");
        }

        BridgeLease lease;
        lock (_gate)
        {
            if (!_leases.TryGetValue(request.ProfileId, out lease!))
            {
                return BridgeResponse.Fail(ProtocolVersion, request.RequestId, "Profile is not leased.");
            }

            if (!string.Equals(lease.RunId, request.RunId, StringComparison.OrdinalIgnoreCase))
            {
                return BridgeResponse.Fail(ProtocolVersion, request.RequestId, "Profile is leased by another runId.");
            }

            lease.Touch(normalizedStatus);
        }

        await UpdateProxyAsync(request.ProfileId, proxy =>
        {
            proxy.EventSentryState = ToDisplayState(normalizedStatus);
            proxy.EventSentryRunId = request.RunId;
            proxy.EventSentryHeartbeat = lease.Heartbeat.ToLocalTime();
            proxy.Status = $"Event Sentry {ToDisplayState(normalizedStatus)}";
        });

        _log($"Event Sentry {Short(request.RunId)} {request.ProfileId}: {normalizedStatus}");
        return BridgeResponse.Success(ProtocolVersion, request.RequestId, "Profile status updated.")
            with
            {
                Profiles = [lease.ToProfile()]
            };
    }

    private async Task<BridgeResponse> HandleBringToFrontAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RunId) || string.IsNullOrWhiteSpace(request.ProfileId))
        {
            return BridgeResponse.Fail(ProtocolVersion, request.RequestId, "runId and profileId are required.");
        }

        var proxy = await FindProfileAsync(request.ProfileId);
        if (proxy is null)
        {
            return BridgeResponse.Fail(ProtocolVersion, request.RequestId, "Profile was not found.");
        }

        BridgeLease lease;
        lock (_gate)
        {
            if (!_leases.TryGetValue(request.ProfileId, out lease!))
            {
                return BridgeResponse.Fail(ProtocolVersion, request.RequestId, "Profile is not leased.");
            }

            if (!string.Equals(lease.RunId, request.RunId, StringComparison.OrdinalIgnoreCase))
            {
                return BridgeResponse.Fail(ProtocolVersion, request.RequestId, "Profile is leased by another runId.");
            }

            lease.Touch("captcha");
        }

        await UpdateProxyAsync(request.ProfileId, proxyEntry =>
        {
            proxyEntry.EventSentryState = "CAPTCHA";
            proxyEntry.EventSentryHeartbeat = lease.Heartbeat.ToLocalTime();
            proxyEntry.Status = "CAPTCHA - waiting for user";
        });
        await _launcher.BringToFrontAsync(proxy, cancellationToken);
        return BridgeResponse.Success(ProtocolVersion, request.RequestId, "Profile brought to front.")
            with
            {
                Profiles = [lease.ToProfile()]
            };
    }

    private async Task<BridgeResponse> HandleReleaseAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            return BridgeResponse.Fail(ProtocolVersion, request.RequestId, "runId is required.");
        }

        List<BridgeLease> released;
        lock (_gate)
        {
            released = _leases.Values
                .Where(lease => string.Equals(lease.RunId, request.RunId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var lease in released)
            {
                _leases.Remove(lease.ProfileId);
            }
        }

        foreach (var lease in released)
        {
            var proxy = await FindProfileAsync(lease.ProfileId);
            if (proxy is null)
            {
                continue;
            }

            if (!request.KeepOpen)
            {
                await _launcher.CloseAsync(proxy, cancellationToken);
                await UpdateProxyAsync(lease.ProfileId, proxyEntry =>
                {
                    proxyEntry.IsOpen = false;
                    proxyEntry.OpenedProvider = "";
                    proxyEntry.EventSentryState = "Closed";
                    proxyEntry.EventSentryRunId = "";
                    proxyEntry.EventSentryHeartbeat = null;
                    proxyEntry.Status = "Closed";
                });
                continue;
            }

            await UpdateProxyAsync(lease.ProfileId, proxyEntry =>
            {
                var normalizedState = NormalizeStatus(proxyEntry.EventSentryState);
                if (normalizedState == "cleanup_required")
                {
                    proxyEntry.Status = "Event Sentry cleanup required";
                }
                else if (!IsProtectedState(normalizedState))
                {
                    proxyEntry.EventSentryState = "Ready";
                    proxyEntry.Status = "Event Sentry ready";
                }

                proxyEntry.EventSentryRunId = "";
                proxyEntry.EventSentryHeartbeat = null;
            });
        }

        _saveState();
        _updateSummary();
        StateChanged?.Invoke();
        return BridgeResponse.Success(ProtocolVersion, request.RequestId, $"{released.Count} profiles released.")
            with
            {
                SelectedCount = (await GetEventProfilesAsync()).Count,
                ReadyCount = released.Count,
                BusyCount = 0,
                Profiles = released.Select(lease => lease.ToProfile()).ToList()
            };
    }

    private async Task<List<ProxyEntry>> GetEventProfilesAsync()
    {
        return await OnUiAsync(() => _getProfiles()
            .Where(proxy => proxy.UseForEventSentry)
            .OrderBy(proxy => proxy.ListNumber)
            .ToList());
    }

    private async Task<ProxyEntry?> FindProfileAsync(string profileId)
    {
        return await OnUiAsync(() => _getProfiles()
            .FirstOrDefault(proxy => string.Equals(proxy.ProfileKey, profileId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(proxy.StableId, profileId, StringComparison.OrdinalIgnoreCase)));
    }

    private Task UpdateProxyAsync(string profileId, Action<ProxyEntry> update)
    {
        return OnUiAsync(() =>
        {
            var proxy = _getProfiles()
                .FirstOrDefault(item => string.Equals(item.ProfileKey, profileId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.StableId, profileId, StringComparison.OrdinalIgnoreCase));
            if (proxy is not null)
            {
                update(proxy);
            }
        });
    }

    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return Task.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    completion.SetResult(action());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            completion.SetException(new InvalidOperationException("UI dispatcher is not available."));
        }

        return completion.Task;
    }

    private Task OnUiAsync(Action action)
    {
        return OnUiAsync(() =>
        {
            action();
            return true;
        });
    }

    private bool HasActiveLease(string profileId, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _leases.TryGetValue(profileId, out var lease) && !IsLeaseStale(lease, now);
        }
    }

    private void CleanupStaleLeases()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            foreach (var lease in _leases.Values.ToList())
            {
                if (IsLeaseStale(lease, now) && !IsProtectedState(lease.State))
                {
                    _leases.Remove(lease.ProfileId);
                }
            }
        }
    }

    private static bool IsLeaseStale(BridgeLease lease, DateTimeOffset now)
    {
        return now - lease.Heartbeat > StaleLeaseAfter;
    }

    private static string NormalizeCommand(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant();
    }

    private static string NormalizeStatus(string? value)
    {
        return NormalizeCommand(value).Replace('-', '_').Replace(' ', '_');
    }

    private static string ToDisplayState(string status)
    {
        return status switch
        {
            "connecting" => "Connecting",
            "ready" => "Ready",
            "reading" => "Reading",
            "carting" => "Carting",
            "held" => "Held",
            "clearing" => "Clearing",
            "captcha" => "CAPTCHA",
            "blocked" => "Blocked",
            "cleanup_required" => "Cleanup required",
            "unavailable" => "Unavailable",
            "error" => "Error",
            "leased" => "Leased",
            _ => status
        };
    }

    private static bool IsProtectedState(string? state)
    {
        var normalized = NormalizeStatus(state);
        return normalized == "held";
    }

    private static string Short(string? runId)
    {
        var value = (runId ?? "").Trim();
        return value.Length <= 12 ? value : value[..12];
    }

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "connecting",
        "ready",
        "reading",
        "carting",
        "held",
        "clearing",
        "captcha",
        "blocked",
        "cleanup_required",
        "unavailable",
        "error"
    };

    private sealed class BridgeLease
    {
        public BridgeLease(
            string profileId,
            string runId,
            string state,
            string cdpEndpoint,
            string browser,
            int processId,
            bool requiresCleanup,
            string bidiEndpoint)
        {
            ProfileId = profileId;
            RunId = runId;
            State = state;
            CdpEndpoint = cdpEndpoint;
            BidiEndpoint = bidiEndpoint;
            Browser = browser;
            ProcessId = processId;
            RequiresCleanup = requiresCleanup;
            Heartbeat = DateTimeOffset.UtcNow;
        }

        public string ProfileId { get; }
        public string RunId { get; }
        public string State { get; private set; }
        public string CdpEndpoint { get; }
        public string BidiEndpoint { get; }
        public string Browser { get; }
        public int ProcessId { get; }
        public bool RequiresCleanup { get; private set; }
        public DateTimeOffset Heartbeat { get; private set; }

        public void Touch(string state)
        {
            State = state;
            if (state == "cleanup_required")
            {
                RequiresCleanup = true;
            }
            else if (state == "ready")
            {
                RequiresCleanup = false;
            }
            Heartbeat = DateTimeOffset.UtcNow;
        }

        public BridgeProfile ToProfile(ProxyEntry? proxy = null)
        {
            return new BridgeProfile
            {
                ProfileId = ProfileId,
                RowNumber = proxy?.RowNumber ?? 0,
                DisplayName = string.IsNullOrWhiteSpace(proxy?.DisplayName)
                    ? $"Proxy {ProfileId}"
                    : proxy!.DisplayName,
                CdpEndpoint = CdpEndpoint,
                BidiEndpoint = BidiEndpoint,
                Browser = Browser,
                ProcessId = ProcessId,
                State = State,
                RequiresCleanup = RequiresCleanup
            };
        }
    }

    private sealed record BridgeRequest
    {
        public int ProtocolVersion { get; init; }
        public string RequestId { get; init; } = "";
        public string Command { get; init; } = "";
        public string RunId { get; init; } = "";
        public string ProfileId { get; init; } = "";
        public string Status { get; init; } = "";
        public string Message { get; init; } = "";
        public string ShowUrl { get; init; } = "";
        public bool KeepOpen { get; init; } = true;
        public string Browser { get; init; } = "auto";
        public bool Background { get; init; } = true;
    }

    private sealed record BridgeResponse
    {
        public int? ServerProcessId { get; init; }
        public string? ServerVersion { get; init; }
        public bool? AcquisitionBlocked { get; init; }
        public int ProtocolVersion { get; init; }
        public string RequestId { get; init; } = "";
        public bool Ok { get; init; }
        public string Error { get; init; } = "";
        public string Message { get; init; } = "";
        public int SelectedCount { get; init; }
        public int ReadyCount { get; init; }
        public int BusyCount { get; init; }
        public IReadOnlyList<BridgeProfile> Profiles { get; init; } = [];

        public static BridgeResponse Success(int protocolVersion, string requestId, string message)
        {
            return new BridgeResponse
            {
                ProtocolVersion = protocolVersion,
                RequestId = requestId,
                Ok = true,
                Message = message
            };
        }

        public static BridgeResponse Fail(int protocolVersion, string requestId, string error)
        {
            return new BridgeResponse
            {
                ProtocolVersion = protocolVersion,
                RequestId = requestId,
                Ok = false,
                Error = error,
                Message = error
            };
        }
    }

    private sealed record BridgeProfile
    {
        public string ProfileId { get; init; } = "";
        public int RowNumber { get; init; }
        public string DisplayName { get; init; } = "";
        public string CdpEndpoint { get; init; } = "";
        public string BidiEndpoint { get; init; } = "";
        public string Browser { get; init; } = "chrome";
        public int ProcessId { get; init; }
        public string State { get; init; } = "";
        public bool RequiresCleanup { get; init; }
    }
}
