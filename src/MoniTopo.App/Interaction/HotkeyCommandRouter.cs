using MoniTopo.Core.Models;
using MoniTopo.Windows.Input;

namespace MoniTopo.App.Interaction;

public interface IApplicationSurfaces
{
    void TogglePopup();

    void OpenSettings();
}

public sealed class HotkeyCommandRouter(
    Func<IReadOnlyList<DisplayProfile>> profiles,
    ActivationInteractionController activation,
    IApplicationSurfaces surfaces)
{
    public async Task HandleAsync(HotkeyCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Kind == HotkeyCommandKind.TogglePopup)
        {
            surfaces.TogglePopup();
            return;
        }

        var profile = profiles().FirstOrDefault(item => item.Id == command.ProfileId);
        if (profile is not null)
        {
            await activation.ActivateAsync(profile, ActivationOrigin.DirectHotkey, cancellationToken).ConfigureAwait(false);
        }
    }
}
