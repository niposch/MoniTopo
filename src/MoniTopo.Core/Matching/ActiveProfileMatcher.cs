using MoniTopo.Core.Identity;
using MoniTopo.Core.Models;

namespace MoniTopo.Core.Matching;

public sealed record ProfileMatchResult(bool IsMatch, string? MismatchCode, IdentityResolutionResult IdentityResolution);

public sealed record ActiveProfileState(Guid? ProfileId, string DisplayName, bool IsCustom)
{
    public static ActiveProfileState Custom { get; } = new(null, "Custom", true);
}

public sealed class ActiveProfileMatcher(MonitorIdentityResolver identityResolver)
{
    public ProfileMatchResult Match(DisplayProfile profile, CapturedDisplaySnapshot current)
    {
        var resolution = identityResolver.Resolve(profile, current.ConnectedDisplays);
        if (resolution.Status != IdentityResolutionStatus.Success)
        {
            return new ProfileMatchResult(false, $"identity.{resolution.Status.ToString().ToLowerInvariant()}", resolution);
        }

        var activeConnected = current.ConnectedDisplays.Where(display => display.IsActive).ToArray();
        if (activeConnected.Length != profile.Displays.Count)
        {
            return new ProfileMatchResult(false, "active-set", resolution);
        }

        var currentByRuntimeId = current.ConnectedDisplays.ToDictionary(display => display.RuntimeId, StringComparer.Ordinal);
        var savedById = profile.Displays.ToDictionary(display => display.DisplayId, StringComparer.OrdinalIgnoreCase);
        var mapped = new Dictionary<string, DesiredDisplayPath>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in resolution.Bindings)
        {
            if (!currentByRuntimeId.TryGetValue(binding.RuntimeId, out var connected) || connected.ActivePath is null)
            {
                return new ProfileMatchResult(false, "required-inactive", resolution);
            }

            mapped[binding.DisplayId] = connected.ActivePath;
        }

        var currentPrimary = mapped.Values.SingleOrDefault(display => display.IsPrimary);
        if (currentPrimary is null ||
            !mapped.TryGetValue(profile.PrimaryDisplayId, out var mappedPrimary) ||
            !ReferenceEquals(currentPrimary, mappedPrimary) && currentPrimary != mappedPrimary)
        {
            return new ProfileMatchResult(false, "primary", resolution);
        }

        var primaryOffset = mappedPrimary.Position;
        foreach (var (displayId, currentPath) in mapped)
        {
            var saved = savedById[displayId];
            if (saved.Position != currentPath.Position - primaryOffset ||
                saved.SourceResolution != currentPath.SourceResolution ||
                !saved.RefreshRate.IsEquivalentTo(currentPath.RefreshRate) ||
                saved.Orientation != currentPath.Orientation ||
                saved.PathScaling != currentPath.PathScaling ||
                saved.WindowsUiScalePercent != currentPath.WindowsUiScalePercent ||
                saved.HdrEnabled != currentPath.HdrEnabled)
            {
                return new ProfileMatchResult(false, "display-state", resolution);
            }
        }

        for (var left = 0; left < profile.Displays.Count; left++)
        {
            for (var right = left + 1; right < profile.Displays.Count; right++)
            {
                var savedSharesSource = string.Equals(
                    profile.Displays[left].SourceGroupId,
                    profile.Displays[right].SourceGroupId,
                    StringComparison.Ordinal);
                var currentSharesSource = string.Equals(
                    mapped[profile.Displays[left].DisplayId].SourceGroupId,
                    mapped[profile.Displays[right].DisplayId].SourceGroupId,
                    StringComparison.Ordinal);
                if (savedSharesSource != currentSharesSource)
                {
                    return new ProfileMatchResult(false, "topology", resolution);
                }
            }
        }

        return new ProfileMatchResult(true, null, resolution);
    }

    public ActiveProfileState FindActiveProfile(
        IReadOnlyList<DisplayProfile> orderedProfiles,
        CapturedDisplaySnapshot current,
        Guid? lastActivatedProfileId)
    {
        var matches = orderedProfiles.Where(profile => Match(profile, current).IsMatch).ToArray();
        if (matches.Length == 0)
        {
            return ActiveProfileState.Custom;
        }

        var selected = lastActivatedProfileId is Guid preferred
            ? matches.FirstOrDefault(profile => profile.Id == preferred) ?? matches[0]
            : matches[0];
        return new ActiveProfileState(selected.Id, selected.Name, false);
    }
}
