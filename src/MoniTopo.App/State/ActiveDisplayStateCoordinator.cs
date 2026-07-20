using MoniTopo.Core.Configuration;
using MoniTopo.Core.Matching;
using MoniTopo.Windows.Display;

namespace MoniTopo.App.State;

public sealed record ActiveDisplayState(Guid? ProfileId, string DisplayName)
{
    public static ActiveDisplayState Custom { get; } = new(null, "Custom");
}

public sealed class ActiveDisplayStateCoordinator(
    IDisplayCaptureService captureService,
    ActiveProfileMatcher matcher,
    Func<ApplicationConfiguration> configuration)
{
    public ActiveDisplayState Current { get; private set; } = ActiveDisplayState.Custom;

    public event EventHandler<ActiveDisplayState>? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var config = configuration();
        var snapshot = await captureService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var byId = config.Profiles.ToDictionary(profile => profile.Id);
        var orderedProfiles = config.ProfileOrder.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
        var profile = matcher.FindActiveProfile(orderedProfiles, snapshot, config.LastActivatedProfileId);
        var next = profile.IsCustom
            ? ActiveDisplayState.Custom
            : new ActiveDisplayState(profile.ProfileId, profile.DisplayName);
        if (next != Current)
        {
            Current = next;
            Changed?.Invoke(this, next);
        }
    }
}
