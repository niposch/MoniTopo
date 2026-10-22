namespace MoniTopo.Windows.Display;

public sealed class DisplayStateRefreshService : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _refresh;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _debounceInterval;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ITimer _fallbackTimer;
    private CancellationTokenSource? _pendingDebounce;
    private bool _sessionLocked;
    private bool _shuttingDown;
    private int _activeOperations;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _disposeTask;

    public DisplayStateRefreshService(
        Func<CancellationToken, Task> refresh,
        TimeProvider? timeProvider = null,
        TimeSpan? debounceInterval = null,
        TimeSpan? fallbackInterval = null)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _debounceInterval = debounceInterval ?? TimeSpan.FromMilliseconds(350);
        var pollInterval = fallbackInterval ?? TimeSpan.FromSeconds(15);
        _fallbackTimer = _timeProvider.CreateTimer(
            static state => ((DisplayStateRefreshService)state!).OnFallbackTimer(),
            this,
            pollInterval,
            pollInterval);
    }

    public Task NotifyDisplayChangeAsync()
    {
        CancellationToken token;
        lock (_sync)
        {
            if (_shuttingDown)
            {
                return Task.CompletedTask;
            }

            _pendingDebounce?.Cancel();
            _pendingDebounce?.Dispose();
            _pendingDebounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            token = _pendingDebounce.Token;
        }

        return TrackAsync(() => DebounceAndRefreshAsync(token));
    }

    public void SetSessionLocked(bool isLocked)
    {
        lock (_sync)
        {
            _sessionLocked = isLocked;
        }
    }

    public void BeginShutdown()
    {
        lock (_sync)
        {
            if (_shuttingDown)
            {
                return;
            }

            _shuttingDown = true;
            _pendingDebounce?.Cancel();
            if (_activeOperations == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    public Task RunConsistencyCheckAsync(CancellationToken cancellationToken = default) =>
        TrackAsync(() => RefreshIfAllowedAsync(cancellationToken));

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        BeginShutdown();
        _lifetime.Cancel();
        await _fallbackTimer.DisposeAsync().ConfigureAwait(false);
        await _drained.Task.ConfigureAwait(false);
        _pendingDebounce?.Dispose();
        _lifetime.Dispose();
        _refreshGate.Dispose();
    }

    private async Task DebounceAndRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_debounceInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            await RefreshIfAllowedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshIfAllowedAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_sessionLocked || _shuttingDown)
            {
                return;
            }
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (!await _refreshGate.WaitAsync(0, linked.Token).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await _refresh(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void OnFallbackTimer()
    {
        _ = TrackAsync(() => RefreshIfAllowedAsync(_lifetime.Token));
    }

    private Task TrackAsync(Func<Task> operation)
    {
        lock (_sync)
        {
            if (_shuttingDown)
            {
                return Task.CompletedTask;
            }

            _activeOperations++;
        }

        return RunTrackedAsync(operation);
    }

    private async Task RunTrackedAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_sync)
            {
                _activeOperations--;
                if (_shuttingDown && _activeOperations == 0)
                {
                    _drained.TrySetResult();
                }
            }
        }
    }
}
