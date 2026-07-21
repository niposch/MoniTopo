using MoniTopo.App.State;
using MoniTopo.Core.Models;
using MoniTopo.Windows.Input;

namespace MoniTopo.App.Settings;

public sealed class PopupHotkeySettingsCoordinator(
    ConfigurationSession configuration,
    Func<HotkeyBinding, HotkeyRegistrationResult> register)
{
    public async Task SetAsync(HotkeyBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var previous = configuration.Current.ApplicationSettings.PopupHotkey;
        var registration = register(binding);
        if (!registration.Succeeded)
        {
            throw new InvalidOperationException(registration.Message);
        }

        try
        {
            await configuration.UpdateAsync(current => current with
            {
                ApplicationSettings = current.ApplicationSettings with { PopupHotkey = binding },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = register(previous);
            throw;
        }
    }
}
