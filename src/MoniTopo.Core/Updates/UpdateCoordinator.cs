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
    private bool _disposed;

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

    public bool AutomaticChecksEnabled => _settings.AutomaticUpdateChecksEnabled;

    public event EventHandler<UpdateState>? Changed;

    public async Task SetAutomaticChecksEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        await _settings.SetAutomaticUpdateChecksEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);

    public async Task<UpdateState> CheckAutomaticallyAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (!_settings.AutomaticUpdateChecksEnabled ||
            _settings.LastSuccessfulUpdateCheckUtc is { } lastCheck && now - lastCheck < AutomaticCheckInterval)
        {
            return Current;
        }

        return await CheckCoreAsync(now, cancellationToken).ConfigureAwait(false);
    }

    public Task<UpdateState> CheckNowAsync(CancellationToken cancellationToken = default) =>
        CheckCoreAsync(_timeProvider.GetUtcNow(), cancellationToken);

    public async Task<UpdateState> DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (Current.Status != UpdateStatus.Available || Current.Update is not { } update)
        {
            return Current;
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Current;
        }

        try
        {
            SetState(new UpdateState(UpdateStatus.Downloading, update, 0, $"Downloading version {update.DisplayVersion}…"));
            var progress = new CallbackProgress<int>(value => SetState(new UpdateState(
                UpdateStatus.Downloading,
                update,
                Math.Clamp(value, 0, 100),
                $"Downloading version {update.DisplayVersion}: {Math.Clamp(value, 0, 100)}%")));
            await _client.DownloadAsync(update, progress, cancellationToken).ConfigureAwait(false);
            SetState(new UpdateState(
                UpdateStatus.ReadyToInstall,
                update,
                100,
                $"Version {update.DisplayVersion} is ready to install."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(new UpdateState(UpdateStatus.Available, update, 0, "Update download canceled."));
        }
        catch (UpdateClientException exception)
        {
            SetState(ErrorState(exception, update));
        }
        catch (Exception exception)
        {
            SetState(ErrorState(new UpdateClientException(UpdateClientError.Unknown, exception.Message, exception), update));
        }
        finally
        {
            _operationGate.Release();
        }

        return Current;
    }

    public void InstallAndRestart()
    {
        if (Current.Status != UpdateStatus.ReadyToInstall || Current.Update is not { } update)
        {
            return;
        }

        _client.ApplyAndRestart(update);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _operationGate.Dispose();
        _disposed = true;
    }

    private async Task<UpdateState> CheckCoreAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_client.IsInstalled)
        {
            SetState(new UpdateState(
                UpdateStatus.NotInstalled,
                null,
                0,
                "Update checks are available in an installed or packaged copy of MoniTopo."));
            return Current;
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Current;
        }

        try
        {
            SetState(new UpdateState(UpdateStatus.Checking, null, 0, "Checking for updates…"));
            var update = await _client.CheckAsync(cancellationToken).ConfigureAwait(false);
            await _settings.SetLastSuccessfulUpdateCheckUtcAsync(now, cancellationToken).ConfigureAwait(false);
            SetState(update is null
                ? new UpdateState(UpdateStatus.UpToDate, null, 0, "MoniTopo is up to date.")
                : new UpdateState(UpdateStatus.Available, update, 0, $"Version {update.DisplayVersion} is available."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(UpdateState.Idle);
        }
        catch (UpdateClientException exception)
        {
            SetState(ErrorState(exception, null));
        }
        catch (Exception exception)
        {
            SetState(ErrorState(new UpdateClientException(UpdateClientError.Unknown, exception.Message, exception), null));
        }
        finally
        {
            _operationGate.Release();
        }

        return Current;
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
        Changed?.Invoke(this, state);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
