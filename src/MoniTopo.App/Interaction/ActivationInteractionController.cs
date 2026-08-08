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
    Action<string>? logFailure = null)
{
    public event EventHandler<ActivationProgress>? PopupProgressChanged;

    public event EventHandler<ActivationResult>? PopupResultAvailable;

    public async Task<ActivationResult> ActivateAsync(
        DisplayProfile profile,
        ActivationOrigin origin,
        CancellationToken cancellationToken = default)
    {
        setProfileHotkeysEnabled(false);
        try
        {
            var progress = origin == ActivationOrigin.Popup
                ? new Progress<ActivationProgress>(value => PopupProgressChanged?.Invoke(this, value))
                : null;
            var result = await activator.ActivateAsync(profile, progress, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == ActivationOutcome.Failed)
            {
                var detail = string.IsNullOrWhiteSpace(result.TechnicalDetails)
                    ? string.Empty
                    : $" {result.TechnicalDetails}";
                logFailure?.Invoke($"{result.ErrorCode ?? "activation.failed"}: {result.Message}{detail}");
            }

            RouteResult(profile, origin, result);
            return result;
        }
        finally
        {
            setProfileHotkeysEnabled(true);
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
}
