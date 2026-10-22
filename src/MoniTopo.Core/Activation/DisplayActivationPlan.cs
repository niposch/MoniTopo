using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;

namespace MoniTopo.Core.Activation;

public sealed record DisplayActivationPlan(
    bool CoreConfigurationRequired,
    bool ScalingRequired,
    bool HdrRequired,
    IdentityResolutionResult IdentityResolution)
{
    public bool HasChanges => CoreConfigurationRequired || ScalingRequired || HdrRequired;

    public static DisplayActivationPlan Create(
        DisplayProfile profile, CapturedDisplaySnapshot current, ActiveProfileMatcher matcher)
    {
        var core = matcher.MatchCoreConfiguration(profile, current);
        var currentByRuntime = current.ConnectedDisplays.ToDictionary(display => display.RuntimeId, StringComparer.Ordinal);
        var bindings = core.IdentityResolution.Bindings.ToDictionary(binding => binding.DisplayId, StringComparer.OrdinalIgnoreCase);
        var scale = false;
        var hdr = false;
        foreach (var desired in profile.Displays)
        {
            // An inactive/unresolved target's property state is unknown, not unchanged.
            // Recompute the plan after the requested topology has activated it.
            if (!bindings.TryGetValue(desired.DisplayId, out var binding) ||
                !currentByRuntime.TryGetValue(binding.RuntimeId, out var connected) ||
                !connected.IsActive || connected.ActivePath is not { } observed)
            {
                scale = true;
                hdr = true;
                continue;
            }

            scale |= desired.WindowsUiScalePercent != observed.WindowsUiScalePercent;
            hdr |= desired.HdrEnabled != observed.HdrEnabled;
        }

        return new DisplayActivationPlan(!core.IsMatch, scale, hdr, core.IdentityResolution);
    }
}
