using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;
using MoniTopo.Core.Normalization;

namespace MoniTopo.Core.Tests;

public sealed class ActiveProfileMatcherTests
{
    private readonly ActiveProfileMatcher _matcher = new(new MonitorIdentityResolver());

    [Fact]
    public void ExtraInactiveDisplayIsIgnored()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var snapshot = Snapshot(profile, extraDisplayActive: false);

        Assert.True(_matcher.Match(profile, snapshot).IsMatch);
    }

    [Fact]
    public void DisconnectedDisplayNotCapturedInDesktopProfileIsIgnored()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var snapshot = Snapshot(profile, includeExtraDisplay: false);

        Assert.True(_matcher.Match(profile, snapshot).IsMatch);
    }

    [Fact]
    public void ExtraActiveDisplayProducesCustom()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var snapshot = Snapshot(profile, extraDisplayActive: true);

        var state = _matcher.FindActiveProfile([profile], snapshot, null);

        Assert.True(state.IsCustom);
        Assert.Equal("Custom", state.DisplayName);
    }

    [Fact]
    public void MissingRequiredMovieDisplayProducesCustom()
    {
        var movie = ProfileNormalizer.Normalize(TestData.Profile("Movie"));
        var differentIdentity = TestData.Identity("DESK");
        var snapshot = new CapturedDisplaySnapshot(
            ActivePaths: [TestData.Display() with { Identity = differentIdentity }],
            ConnectedDisplays: [new ConnectedDisplayState("desk", differentIdentity, true, "Desk", TestData.Display() with { Identity = differentIdentity })],
            PrimaryDisplayId: "display-1",
            CapturedUtc: DateTimeOffset.UtcNow);

        Assert.True(_matcher.FindActiveProfile([movie], snapshot, null).IsCustom);
    }

    [Fact]
    public void ManualScaleChangeProducesCustomWithoutReverting()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var snapshot = Snapshot(profile, mutate: path => path with { WindowsUiScalePercent = 175 });

        var result = _matcher.Match(profile, snapshot);

        Assert.False(result.IsMatch);
        Assert.Equal("display-state", result.MismatchCode);
    }

    [Fact]
    public void EquivalentFractionalRefreshStillMatches()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var snapshot = Snapshot(profile, mutate: path => path with { RefreshRate = new RefreshRate(60000, 1001) });

        Assert.True(_matcher.Match(profile, snapshot).IsMatch);
    }

    [Fact]
    public void PrimaryMismatchProducesCustom()
    {
        var displays = new[]
        {
            TestData.Display("a", primary: true, position: new DisplayPoint(0, 0), serial: "A"),
            TestData.Display("b", primary: false, position: new DisplayPoint(2560, 0), serial: "B"),
        };
        var profile = ProfileNormalizer.Normalize(TestData.Profile(displays: displays, primaryDisplayId: "a"));
        var snapshot = Snapshot(profile, mutate: path => path with { IsPrimary = path.DisplayId == "b" });

        Assert.False(_matcher.Match(profile, snapshot).IsMatch);
    }

    [Fact]
    public void CloneRelationshipMustMatch()
    {
        var displays = new[]
        {
            TestData.Display("a", primary: true, serial: "A") with { SourceGroupId = "clone", CloneGroupId = "clone" },
            TestData.Display("b", primary: false, serial: "B") with { SourceGroupId = "clone", CloneGroupId = "clone" },
        };
        var profile = ProfileNormalizer.Normalize(TestData.Profile(displays: displays, primaryDisplayId: "a"));
        var snapshot = Snapshot(profile, mutate: path => path with { SourceGroupId = $"runtime-{path.DisplayId}" });

        Assert.Equal("topology", _matcher.Match(profile, snapshot).MismatchCode);
    }

    [Fact]
    public void LastActivatedProfileBreaksDuplicateLegacyMatchTie()
    {
        var first = ProfileNormalizer.Normalize(TestData.Profile("First"));
        var second = first with { Id = Guid.NewGuid(), Name = "Second" };
        var snapshot = Snapshot(first);

        var state = _matcher.FindActiveProfile([first, second], snapshot, second.Id);

        Assert.Equal(second.Id, state.ProfileId);
        Assert.Equal("Second", state.DisplayName);
    }

    private static CapturedDisplaySnapshot Snapshot(
        DisplayProfile profile,
        bool includeExtraDisplay = true,
        bool extraDisplayActive = false,
        Func<DesiredDisplayPath, DesiredDisplayPath>? mutate = null)
    {
        var connected = profile.Displays.Select((saved, index) =>
        {
            var current = mutate?.Invoke(saved) ?? saved;
            current = current with { SourceGroupId = current.SourceGroupId.Replace("source-", "runtime-source-", StringComparison.Ordinal) };
            return new ConnectedDisplayState($"runtime-{index}", saved.Identity, true, saved.FriendlyLabel, current);
        }).ToList();

        if (includeExtraDisplay)
        {
            var extraIdentity = TestData.Identity("EXTRA");
            var extraPath = TestData.Display("extra", primary: false, serial: "EXTRA");
            connected.Add(new ConnectedDisplayState(
                "runtime-extra",
                extraIdentity,
                extraDisplayActive,
                "Extra",
                extraDisplayActive ? extraPath : null));
        }

        return new CapturedDisplaySnapshot(
            connected.Where(display => display.ActivePath is not null).Select(display => display.ActivePath!).ToArray(),
            connected,
            profile.PrimaryDisplayId,
            DateTimeOffset.UtcNow);
    }
}
