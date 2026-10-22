using MoniTopo.Core.Activation;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;

namespace MoniTopo.Core.Tests;

public sealed class DisplayActivationPlanTests
{
    private static readonly ActiveProfileMatcher Matcher = new(new MonitorIdentityResolver());

    [Theory]
    [InlineData("none", false, false, false)]
    [InlineData("hdr", false, false, true)]
    [InlineData("scale", false, true, false)]
    [InlineData("combined", true, true, true)]
    [InlineData("resolution", true, false, false)]
    [InlineData("refresh", true, false, false)]
    [InlineData("orientation", true, false, false)]
    [InlineData("path-scaling", true, false, false)]
    [InlineData("position", true, false, false)]
    [InlineData("primary", true, false, false)]
    [InlineData("clones", true, false, false)]
    [InlineData("renumbered-sources", false, false, false)]
    [InlineData("equivalent-refresh", false, false, false)]
    [InlineData("physical-timing", true, false, false)]
    [InlineData("driver-label", false, false, false)]
    [InlineData("desktop-image", true, false, false)]
    [InlineData("boost", true, false, false)]
    public void SeparatesEveryManagedCoreAndPropertyChange(string change, bool core, bool scale, bool hdr)
    {
        var profile = Profile();
        var paths = profile.Displays.Select((path, index) => change switch
        {
            "hdr" when index == 1 => path with { HdrEnabled = true },
            "scale" when index == 1 => path with { WindowsUiScalePercent = 100 },
            "combined" when index == 1 => path with { SourceResolution = new DisplaySize(1920, 1080), WindowsUiScalePercent = 100, HdrEnabled = true },
            "resolution" when index == 1 => path with { SourceResolution = new DisplaySize(1920, 1080) },
            "refresh" when index == 1 => path with { RefreshRate = new RefreshRate(75, 1) },
            "orientation" when index == 1 => path with { Orientation = DisplayOrientation.Portrait },
            "path-scaling" when index == 1 => path with { PathScaling = DisplayPathScaling.Centered },
            "position" when index == 1 => path with { Position = path.Position with { Y = 1 } },
            "primary" => path with { IsPrimary = !path.IsPrimary },
            "clones" => path with { SourceGroupId = "shared-source" },
            "renumbered-sources" => path with { SourceGroupId = $"new-{index}" },
            "equivalent-refresh" => path with { RefreshRate = new RefreshRate(60, 1) },
            "physical-timing" when index == 1 => path with { TargetSignal = path.TargetSignal! with { PixelRate = 300_000_000 } },
            "driver-label" => path with { TargetSignal = path.TargetSignal! with { VideoStandard = 10 } },
            "desktop-image" when index == 1 => path with { DesktopImage = path.DesktopImage! with { Clip = new DisplayRectangle(0, 0, 1920, 1080) } },
            "boost" when index == 1 => path with { BoostRefreshRate = true },
            _ => path,
        }).ToArray();
        var snapshot = Snapshot(paths);

        var plan = DisplayActivationPlan.Create(profile, snapshot, Matcher);

        Assert.Equal(core, plan.CoreConfigurationRequired);
        Assert.Equal(scale, plan.ScalingRequired);
        Assert.Equal(hdr, plan.HdrRequired);
        Assert.Equal(!(core || scale || hdr), Matcher.Match(profile, snapshot).IsMatch);
    }

    [Fact]
    public void CanonicalTranslationAndInactiveExtrasDoNotRequireChanges()
    {
        var profile = Profile();
        var paths = profile.Displays.Select(path => path with
        { Position = new DisplayPoint(path.Position.X + 120, path.Position.Y - 60) }).ToArray();
        var snapshot = Snapshot(paths);
        snapshot = snapshot with
        {
            ConnectedDisplays = snapshot.ConnectedDisplays.Append(new ConnectedDisplayState(
            "extra", TestData.Identity("extra"), false, "Synthetic inactive", null)).ToArray()
        };
        Assert.False(DisplayActivationPlan.Create(profile, snapshot, Matcher).HasChanges);
    }

    [Fact]
    public void ExtraActivePathRequiresAnExactCoreChange()
    {
        var profile = Profile();
        var paths = profile.Displays.Append(TestData.Display("extra", primary: false)).ToArray();
        var plan = DisplayActivationPlan.Create(profile, Snapshot(paths), Matcher);
        Assert.True(plan.CoreConfigurationRequired);
        Assert.False(plan.ScalingRequired);
        Assert.False(plan.HdrRequired);
    }

    [Fact]
    public void InactiveRequiredPathHasUnknownPropertiesAndRequiresActivation()
    {
        var profile = Profile();
        var snapshot = Snapshot(profile.Displays);
        snapshot = snapshot with
        {
            ActivePaths = [profile.Displays[0]],
            ConnectedDisplays = [snapshot.ConnectedDisplays[0], snapshot.ConnectedDisplays[1] with { IsActive = false, ActivePath = null }],
        };
        var plan = DisplayActivationPlan.Create(profile, snapshot, Matcher);
        Assert.True(plan.CoreConfigurationRequired);
        Assert.True(plan.ScalingRequired);
        Assert.True(plan.HdrRequired);
    }

    [Theory]
    [InlineData(60, false)]
    [InlineData(59.94, true)]
    public void EditedModeUsesStrictRateAndObservedTimingRatherThanStalePathScaling(double observedRate, bool core)
    {
        var display = TestData.Display() with { ModeRequest = new DisplayModeRequest(1), BoostRefreshRate = false };
        var profile = TestData.Profile(displays: [display]) with { CaptureSchemaVersion = 5 };
        var observed = display with
        {
            ModeRequest = null,
            PathScaling = DisplayPathScaling.Centered,
            RefreshRate = new RefreshRate((long)(observedRate * 1000), 1000),
            TargetSignal = Signal(),
        };
        Assert.Equal(core, DisplayActivationPlan.Create(profile, Snapshot([observed]), Matcher).CoreConfigurationRequired);
    }

    private static DisplayTargetSignal Signal() => new(241_500_000, new RefreshRate(90_000, 1),
        new RefreshRate(60, 1), new DisplaySize(2560, 1440), new DisplaySize(2720, 1480), 0, 1);

    private static DisplayProfile Profile() => TestData.Profile(displays:
        [TestData.Display(), TestData.Display("display-2", false, new DisplayPoint(2560, 0))]) with
    {
        CaptureSchemaVersion = 4,
        Displays = new[] { TestData.Display(), TestData.Display("display-2", false, new DisplayPoint(2560, 0)) }
            .Select(path => path with
            {
                TargetSignal = Signal(),
                BoostRefreshRate = false,
                DesktopImage = new DisplayDesktopImage(path.SourceResolution, new DisplayRectangle(0, 0, 2560, 1440), new DisplayRectangle(0, 0, 2560, 1440)),
            }).ToArray(),
    };

    private static CapturedDisplaySnapshot Snapshot(IReadOnlyList<DesiredDisplayPath> paths) => new(
        paths, paths.Select((path, index) => new ConnectedDisplayState($"runtime-{index}", path.Identity, true, "Synthetic", path)).ToArray(),
        paths.First(path => path.IsPrimary).DisplayId, DateTimeOffset.UnixEpoch);
}
