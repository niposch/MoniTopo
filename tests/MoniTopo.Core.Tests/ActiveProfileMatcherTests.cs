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

    [Theory]
    [InlineData("active-size")]
    [InlineData("total-size")]
    [InlineData("pixel-clock")]
    [InlineData("horizontal-sync")]
    [InlineData("vertical-sync")]
    [InlineData("scanline")]
    [InlineData("vsync-divider")]
    [InlineData("missing")]
    public void PhysicalSignalChangesCannotPassCompleteVerification(string changedProperty)
    {
        var signal = Signal();
        var profile = TestData.Profile(displays: [TestData.Display() with { TargetSignal = signal }]);
        DisplayTargetSignal? changed = changedProperty switch
        {
            "active-size" => signal with { ActiveSize = new DisplaySize(1920, 1080) },
            "total-size" => signal with { TotalSize = new DisplaySize(2800, 1500) },
            "pixel-clock" => signal with { PixelRate = 300_000_000 },
            "horizontal-sync" => signal with { HorizontalSyncFrequency = new RefreshRate(90_000, 1) },
            "vertical-sync" => signal with { VerticalSyncFrequency = new RefreshRate(75, 1) },
            "scanline" => signal with { ScanLineOrdering = 2 },
            "vsync-divider" => signal with { VideoStandard = 2u << 16 },
            _ => null,
        };

        var result = _matcher.Match(profile, Snapshot(profile, mutate: path => path with { TargetSignal = changed }));

        Assert.False(result.IsMatch);
        Assert.Equal("target-signal", result.MismatchCode);
    }

    [Fact]
    public void EquivalentSignalRationalsAndDriverLabelsDoNotProduceCustom()
    {
        var signal = Signal();
        var profile = TestData.Profile(displays: [TestData.Display() with { TargetSignal = signal }]);
        var equivalent = signal with
        {
            PixelRate = signal.PixelRate + 10,
            HorizontalSyncFrequency = new RefreshRate(88_787_000, 1000),
            VerticalSyncFrequency = new RefreshRate(60_000, 1000),
            VideoStandard = 255 | 0xFFC00000u,
        };

        Assert.True(_matcher.Match(profile, Snapshot(profile, mutate: path => path with { TargetSignal = equivalent })).IsMatch);
    }

    [Fact]
    public void ConsistentFractionalAndRoundedPhysicalClocksRemainEquivalent()
    {
        var signal = Signal();
        var profile = TestData.Profile(displays: [TestData.Display() with { TargetSignal = signal }]);
        var fractional = signal with
        {
            PixelRate = signal.PixelRate * 1000 / 1001,
            HorizontalSyncFrequency = new RefreshRate(88_787_000, 1001),
            VerticalSyncFrequency = new RefreshRate(60_000, 1001),
        };

        Assert.True(_matcher.Match(profile, Snapshot(profile, mutate: path => path with { TargetSignal = fractional })).IsMatch);
        Assert.False(signal.IsEquivalentTo(fractional with { PixelRate = signal.PixelRate * 99 / 100 }));
    }

    [Fact]
    public void SavedVirtualImageAndRefreshBoostAreVerified()
    {
        var image = new DisplayDesktopImage(new DisplaySize(2560, 1440), new DisplayRectangle(0, 0, 2560, 1440), new DisplayRectangle(0, 0, 2560, 1440));
        var profile = TestData.Profile(displays: [TestData.Display() with { TargetSignal = Signal(), DesktopImage = image, BoostRefreshRate = true }]) with
        {
            CaptureSchemaVersion = DisplayProfileCaptureSchema.Current,
        };

        Assert.True(_matcher.Match(profile, Snapshot(profile)).IsMatch);
        Assert.Equal("virtual-mode", _matcher.Match(profile, Snapshot(profile, mutate: path => path with { BoostRefreshRate = false })).MismatchCode);
        Assert.Equal("virtual-mode", _matcher.Match(profile, Snapshot(profile, mutate: path => path with { DesktopImage = image with { Clip = new DisplayRectangle(0, 0, 1920, 1080) } })).MismatchCode);
    }

    [Fact]
    public void LegacyUnknownVirtualMetadataRemainsUsableButCurrentCaptureMustBeComplete()
    {
        var legacy = TestData.Profile() with { CaptureSchemaVersion = 3 };
        var current = Snapshot(legacy, mutate: path => path with
        {
            TargetSignal = Signal(),
            BoostRefreshRate = false,
            DesktopImage = new DisplayDesktopImage(new DisplaySize(2560, 1440), new DisplayRectangle(0, 0, 2560, 1440), new DisplayRectangle(0, 0, 2560, 1440)),
        });

        Assert.True(_matcher.Match(legacy, current).IsMatch);
        Assert.False(_matcher.Match(legacy with { CaptureSchemaVersion = DisplayProfileCaptureSchema.Current }, current).IsMatch);
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

    private static DisplayTargetSignal Signal() => new(
        241_500_000, new RefreshRate(88_787, 1), new RefreshRate(60, 1),
        new DisplaySize(2560, 1440), new DisplaySize(2720, 1481), 0, 1);
}
