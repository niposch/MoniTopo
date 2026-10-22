using MoniTopo.App.Tray;
using MoniTopo.Core.Activation;
using MoniTopo.Core.Models;

namespace MoniTopo.App.Interaction;

public enum ActivationOrigin
{
    Popup,
    DirectHotkey,
    MainWindow,
}

public interface IProfileActivator
{
    Task<ActivationResult> ActivateAsync(
        DisplayProfile profile,
        IProgress<ActivationProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class ProfileActivationAdapter(ProfileActivationService service) : IProfileActivator
{
    public Task<ActivationResult> ActivateAsync(
        DisplayProfile profile,
        IProgress<ActivationProgress>? progress,
        CancellationToken cancellationToken) => service.ActivateAsync(profile, progress, cancellationToken);
}

public sealed class ActivationInteractionController(
    IProfileActivator activator,
    IUserNotificationService notifications,
    Action<bool> setProfileHotkeysEnabled,
    Action<string>? logFailure = null,
    IUiDispatcher? dispatcher = null)
{
    private readonly IUiDispatcher _dispatcher = dispatcher ?? new ImmediateUiDispatcher();
    private int _activationOwned;
    private TaskCompletionSource? _completion;
    private readonly object _sync = new();
    private bool _shuttingDown;

    public bool IsBusy => Volatile.Read(ref _activationOwned) != 0;

    public event EventHandler<bool>? BusyChanged;

    public Task WaitForIdleAsync()
    {
        lock (_sync) return _completion?.Task ?? Task.CompletedTask;
    }

    public Task BeginShutdownAsync()
    {
        lock (_sync)
        {
            _shuttingDown = true;
            return _completion?.Task ?? Task.CompletedTask;
        }
    }

    public async Task<IAsyncDisposable?> TryBeginInteractionAsync()
    {
        lock (_sync)
        {
            if (_shuttingDown || _activationOwned != 0)
            {
                return null;
            }

            _activationOwned = 1;
            _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                setProfileHotkeysEnabled(false);
                BusyChanged?.Invoke(this, true);
            }).ConfigureAwait(false);
            return new InteractionLease(this);
        }
        catch
        {
            await EndInteractionAsync().ConfigureAwait(false);
            throw;
        }
    }

    public event EventHandler<ActivationProgress>? PopupProgressChanged;

    public event EventHandler<ActivationResult>? PopupResultAvailable;

    public event EventHandler<ActivationResult>? ActivationCompleted;

    public async Task<ActivationResult> ActivateAsync(
        DisplayProfile profile,
        ActivationOrigin origin,
        CancellationToken cancellationToken = default)
    {
        await using var interaction = await TryBeginInteractionAsync().ConfigureAwait(false);
        if (interaction is null)
        {
            return ActivationResult.Busy;
        }

        var progress = origin == ActivationOrigin.Popup
                ? new UiProgress(_dispatcher, value => PopupProgressChanged?.Invoke(this, value))
                : null;
        var result = await activator.ActivateAsync(profile, progress, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ActivationOutcome.Failed)
        {
            var detail = string.IsNullOrWhiteSpace(result.TechnicalDetails)
                ? string.Empty
                : $" {result.TechnicalDetails}";
            logFailure?.Invoke($"{result.ErrorCode ?? "activation.failed"}: {result.Message}{detail}");
        }

        await _dispatcher.InvokeAsync(() =>
        {
            RouteResult(profile, origin, result);
            ActivationCompleted?.Invoke(this, result);
        }).ConfigureAwait(false);
        return result;
    }

    private async Task EndInteractionAsync()
    {
        var completion = _completion;
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    bool enabled;
                    lock (_sync) enabled = !_shuttingDown;
                    setProfileHotkeysEnabled(enabled);
                }
                finally
                {
                    lock (_sync) _activationOwned = 0;
                    BusyChanged?.Invoke(this, false);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            completion?.TrySetResult();
        }
    }

    private void RouteResult(DisplayProfile profile, ActivationOrigin origin, ActivationResult result)
    {
        if (origin == ActivationOrigin.Popup)
        {
            PopupResultAvailable?.Invoke(this, result);
            return;
        }

        if (origin != ActivationOrigin.DirectHotkey)
        {
            return;
        }

        if (result.Outcome == ActivationOutcome.Success)
        {
            notifications.ShowInformation($"{profile.Name} activated");
        }
        else
        {
            notifications.ShowError(result.Message);
        }
    }

    private sealed class UiProgress(IUiDispatcher dispatcher, Action<ActivationProgress> report) : IProgress<ActivationProgress>
    {
        public void Report(ActivationProgress value) => _ = dispatcher.InvokeAsync(() => report(value));
    }

    private sealed class InteractionLease(ActivationInteractionController owner) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0
            ? new ValueTask(owner.EndInteractionAsync())
            : ValueTask.CompletedTask;
    }
}
