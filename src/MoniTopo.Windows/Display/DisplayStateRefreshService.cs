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

        return DebounceAndRefreshAsync(token);
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
            _shuttingDown = true;
            _pendingDebounce?.Cancel();
        }
    }

    public Task RunConsistencyCheckAsync(CancellationToken cancellationToken = default) =>
        RefreshIfAllowedAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        BeginShutdown();
        _lifetime.Cancel();
        await _fallbackTimer.DisposeAsync().ConfigureAwait(false);
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

        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await _refresh(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void OnFallbackTimer()
    {
        _ = RefreshIfAllowedAsync(_lifetime.Token);
    }
}
