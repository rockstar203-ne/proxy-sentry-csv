using System.Diagnostics;

namespace ProxySentryCsv.Services;

public sealed record WindowQueueSnapshot(int Pending, bool Paused, double LastLagMs, string Error);

public sealed class WindowActionQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly LinkedList<Work> _pending = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _stop;
    private readonly Task _pump;
    private readonly int _capacity;
    private readonly TimeSpan _actionTimeout;
    private readonly Action<string>? _failed;
    private bool _disposed;
    private bool _paused;
    private int _active;
    private double _lag;
    private string _error = "";

    public WindowActionQueue(CancellationToken token, Action<string>? failed = null,
        int capacity = 128, TimeSpan? actionTimeout = null)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _failed = failed;
        _capacity = capacity;
        _actionTimeout = actionTimeout ?? TimeSpan.FromSeconds(5);
        _pump = Task.Run(PumpAsync);
    }

    public WindowQueueSnapshot Snapshot
    {
        get { lock (_gate) return new(_pending.Count + _active, _paused, _lag, _error); }
    }

    public bool Enqueue(Func<CancellationToken, Task> action, string? coalesceKey = null, TimeSpan? delay = null)
    {
        var now = Stopwatch.GetTimestamp();
        var work = new Work(action, coalesceKey, now, now + (long)((delay ?? TimeSpan.Zero).TotalSeconds * Stopwatch.Frequency));
        lock (_gate)
        {
            if (_disposed || _paused || _stop.IsCancellationRequested) return false;
            // Only adjacent state updates may merge; clicks and keys remain ordered barriers.
            if (coalesceKey is not null && _pending.Last?.Value.Key == coalesceKey)
            {
                _pending.Last.Value = work;
                return true;
            }
            if (_pending.Count < _capacity)
            {
                _pending.AddLast(work);
                _available.Release();
                return true;
            }
        }
        Pause("Queue limit reached. Restart sync to avoid replaying incomplete input.");
        return false;
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await _available.WaitAsync(_stop.Token).ConfigureAwait(false);
                Work work;
                lock (_gate)
                {
                    if (_pending.First is null) continue;
                    work = _pending.First.Value;
                    _pending.RemoveFirst();
                    _active = 1;
                }
                try
                {
                    var remaining = (work.Due - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
                    if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining), _stop.Token).ConfigureAwait(false);
                    lock (_gate) _lag = Math.Max(0, (Stopwatch.GetTimestamp() - work.Due) * 1000.0 / Stopwatch.Frequency);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(_actionTimeout);
                    await work.Action(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                finally { lock (_gate) _active = 0; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        {
            Pause("Window stopped responding. Restart sync after checking that window.");
        }
    }

    private void Pause(string error)
    {
        lock (_gate)
        {
            if (_disposed || _paused) return;
            _paused = true;
            _error = error;
            _pending.Clear();
        }
        _stop.Cancel();
        try { _failed?.Invoke(error); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
        }
        _stop.Cancel();
        _ = _pump.ContinueWith(_ => { _stop.Dispose(); _available.Dispose(); }, TaskScheduler.Default);
    }

    private sealed record Work(Func<CancellationToken, Task> Action, string? Key, long Captured, long Due);
}
