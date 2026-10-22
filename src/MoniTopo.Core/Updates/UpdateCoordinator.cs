namespace MoniTopo.Core.Updates;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    NotInstalled,
    Error,
}

public enum UpdateClientError
{
    Network,
    RateLimited,
    Checksum,
    Busy,
    NotInstalled,
    Unknown,
}

public sealed record AvailableUpdate(string PackageVersion, string DisplayVersion, string? ReleaseNotes);

public sealed record UpdateState(
    UpdateStatus Status,
    AvailableUpdate? Update,
    int ProgressPercent,
    string Message)
{
    public static UpdateState Idle { get; } = new(UpdateStatus.Idle, null, 0, string.Empty);
}

public sealed class UpdateClientException(UpdateClientError error, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public UpdateClientError Error { get; } = error;
}

public interface IUpdateClient
{
    bool IsInstalled { get; }

    string? CurrentPackageVersion { get; }

    AvailableUpdate? PendingRestart { get; }

    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default);

    Task DownloadAsync(
        AvailableUpdate update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    void ApplyAndRestart(AvailableUpdate update);
}

public interface IUpdateSettingsStore
{
    bool AutomaticUpdateChecksEnabled { get; }

    DateTimeOffset? LastSuccessfulUpdateCheckUtc { get; }

    Task SetAutomaticUpdateChecksEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task SetLastSuccessfulUpdateCheckUtcAsync(DateTimeOffset value, CancellationToken cancellationToken = default);
}

public sealed class UpdateCoordinator : IDisposable
{
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    private readonly IUpdateClient _client;
    private readonly IUpdateSettingsStore _settings;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private DateTimeOffset? _lastAutomaticAttemptUtc;
    private int _disposed;
    private int _schedulerRunning;

    public UpdateCoordinator(
        IUpdateClient client,
        IUpdateSettingsStore settings,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _settings = settings;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Current = client.PendingRestart is { } pending
            ? new UpdateState(UpdateStatus.ReadyToInstall, pending, 100, $"Version {pending.DisplayVersion} is ready to install.")
            : UpdateState.Idle;
    }

    public UpdateState Current { get; private set; }

    public string? CurrentPackageVersion => _client.CurrentPackageVersion;

    public bool IsInstalled => _client.IsInstalled;

    public bool AutomaticChecksEnabled => _settings.AutomaticUpdateChecksEnabled;

    public event EventHandler<UpdateState>? Changed;
    public event EventHandler<Exception>? NotificationFailed;

    public async Task SetAutomaticChecksEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        await _settings.SetAutomaticUpdateChecksEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);

    public async Task<UpdateState> CheckAutomaticallyAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (!_settings.AutomaticUpdateChecksEnabled || !_client.IsInstalled ||
            _lastAutomaticAttemptUtc is { } attempt && now - attempt < TimeSpan.FromHours(1) ||
            _settings.LastSuccessfulUpdateCheckUtc is { } lastCheck && now - lastCheck < AutomaticCheckInterval)
        {
            return Current;
        }

        _lastAutomaticAttemptUtc = now;
        return await CheckCoreAsync(now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Run once for the application lifetime; checks never download an update.</summary>
    public async Task RunAutomaticChecksAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _schedulerRunning, 1) != 0)
            throw new InvalidOperationException("The automatic update scheduler is already running.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, linked.Token).ConfigureAwait(false);
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                await CheckAutomaticallyAsync(linked.Token).ConfigureAwait(false);
                // Re-evaluate settings periodically, including checks re-enabled while the app stays open.
                await Task.Delay(TimeSpan.FromMinutes(15), _timeProvider, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
        finally { Volatile.Write(ref _schedulerRunning, 0); }
    }

    public Task<UpdateState> CheckNowAsync(CancellationToken cancellationToken = default) =>
        CheckCoreAsync(_timeProvider.GetUtcNow(), cancellationToken);

    public async Task<UpdateState> DownloadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Current;
        }

        try
        {
            if (RefreshStagedState() || Current.Status != UpdateStatus.Available || Current.Update is not { } update)
                return Current;
            var previous = Current;
            try
            {
                SetState(new UpdateState(UpdateStatus.Downloading, update, 0, $"Downloading version {update.DisplayVersion}…"));
                var progress = new CallbackProgress<int>(value =>
                {
                    if (Current.Status != UpdateStatus.Downloading || Current.Update != update) return;
                    SetState(new UpdateState(UpdateStatus.Downloading, update, Math.Clamp(value, 0, 100),
                        $"Downloading version {update.DisplayVersion}: {Math.Clamp(value, 0, 100)}%"));
                });
                await _client.DownloadAsync(update, progress, cancellationToken).ConfigureAwait(false);
                SetState(new UpdateState(
                    UpdateStatus.ReadyToInstall,
                    update,
                    100,
                    $"Version {update.DisplayVersion} is ready to install."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!RefreshStagedState()) SetState(previous with { Message = "Update download canceled. Download update to try again." });
            }
            catch (UpdateClientException exception)
            {
                if (!RefreshStagedState()) SetState(previous with { Message = ErrorState(exception, update).Message });
            }
            catch (Exception exception)
            {
                if (!RefreshStagedState()) SetState(previous with { Message = ErrorState(new UpdateClientException(UpdateClientError.Unknown, exception.Message, exception), update).Message });
            }
        }
        finally
        {
            _operationGate.Release();
        }

        return Current;
    }

    public void InstallAndRestart()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Current.Status != UpdateStatus.ReadyToInstall || Current.Update is not { } update)
        {
            return;
        }

        try { _client.ApplyAndRestart(update); }
        catch (Exception exception)
        {
            var error = exception as UpdateClientException ?? new(UpdateClientError.Unknown, exception.Message, exception);
            SetState(Current with { Message = ErrorState(error, update).Message + " Restart now to try again." });
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        // Pending operations still own these managed synchronization objects until they finish.
    }

    private async Task<UpdateState> CheckCoreAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Current;
        }

        try
        {
            if (RefreshStagedState()) return Current;
            if (!_client.IsInstalled)
            {
                SetState(ErrorState(new(UpdateClientError.NotInstalled, string.Empty), null));
                return Current;
            }
            var previous = Current;
            try
            {
                SetState(new UpdateState(UpdateStatus.Checking, null, 0, "Checking for updates…"));
                var update = await _client.CheckAsync(cancellationToken).ConfigureAwait(false);
                await _settings.SetLastSuccessfulUpdateCheckUtcAsync(now, cancellationToken).ConfigureAwait(false);
                if (!RefreshStagedState()) SetState(update is null
                    ? new UpdateState(UpdateStatus.UpToDate, null, 0, "MoniTopo is up to date.")
                    : new UpdateState(UpdateStatus.Available, update, 0, $"Version {update.DisplayVersion} is available."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!RefreshStagedState()) SetState(previous);
            }
            catch (UpdateClientException exception)
            {
                if (!RefreshStagedState()) SetState(previous.Update is null ? ErrorState(exception, null) : previous with { Message = ErrorState(exception, previous.Update).Message });
            }
            catch (Exception exception)
            {
                if (!RefreshStagedState()) SetState(previous.Update is null ? ErrorState(new(UpdateClientError.Unknown, exception.Message, exception), null) : previous);
            }
        }
        finally
        {
            _operationGate.Release();
        }

        return Current;
    }

    private bool RefreshStagedState()
    {
        var pending = _client.PendingRestart ?? (Current.Status == UpdateStatus.ReadyToInstall ? Current.Update : null);
        if (pending is null) return false;
        SetState(new(UpdateStatus.ReadyToInstall, pending, 100, $"Version {pending.DisplayVersion} is ready to install."));
        return true;
    }

    private static UpdateState ErrorState(UpdateClientException exception, AvailableUpdate? update)
    {
        var message = exception.Error switch
        {
            UpdateClientError.Network => "Could not reach the update service. Check the network connection and try again.",
            UpdateClientError.RateLimited => "GitHub temporarily limited update checks. Try again later.",
            UpdateClientError.Checksum => "The downloaded update failed its integrity check and was not installed.",
            UpdateClientError.Busy => "Another update operation is already running.",
            UpdateClientError.NotInstalled => "Update checks are available in an installed or packaged copy of MoniTopo.",
            _ => "MoniTopo could not complete the update operation.",
        };
        return new UpdateState(
            exception.Error == UpdateClientError.NotInstalled ? UpdateStatus.NotInstalled : UpdateStatus.Error,
            update,
            0,
            message);
    }

    private void SetState(UpdateState state)
    {
        Current = state;
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
        {
            try { ((EventHandler<UpdateState>)subscriber)(this, state); }
            catch (Exception exception)
            {
                foreach (var observer in NotificationFailed?.GetInvocationList() ?? [])
                {
                    try { ((EventHandler<Exception>)observer)(this, exception); }
                    catch (Exception notificationException) { System.Diagnostics.Trace.TraceError("Update notification failed: {0}", notificationException); }
                }
            }
        }
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
