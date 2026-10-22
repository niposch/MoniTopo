using MoniTopo.App.Popup;
using MoniTopo.Core.Models;

namespace MoniTopo.App.Tests;

public sealed class LayoutSchematicTests
{
    [Fact]
    public void PreservesRelativeSizePositionAndPrimaryForDifferentProfiles()
    {
        var profile = SyntheticProfile();
        var monitors = LayoutSchematic.Build(profile);
        Assert.Equal(2, monitors.Count);
        Assert.True(monitors[0].Left < monitors[1].Left);
        Assert.True(monitors[0].Width > monitors[1].Width);
        Assert.Equal(2, monitors[0].StrokeThickness);
        Assert.Equal(1, monitors[1].StrokeThickness);
        Assert.All(monitors, AssertInside);
        Assert.Single(LayoutSchematic.Build(profile with { Displays = [profile.Displays[0]] }));
    }

    [Fact]
    public void NegativeCoordinatesAndPortraitDisplayRemainInsideCenteredViewport()
    {
        var profile = SyntheticProfile();
        profile = profile with
        {
            Displays = [profile.Displays[0], profile.Displays[1] with
        {
            Position = new DisplayPoint(-1080, -1200), SourceResolution = new DisplaySize(1080, 1920),
        }]
        };
        var monitors = LayoutSchematic.Build(profile);
        Assert.True(monitors[1].Left < monitors[0].Left);
        Assert.True(monitors[1].Top < monitors[0].Top);
        Assert.True(monitors[1].Height > monitors[1].Width);
        Assert.All(monitors, AssertInside);
    }

    [Fact]
    public void ClonesAreStaggeredAndPopupRefreshPreservesGeometryAndUpdatedHotkeys()
    {
        var profile = SyntheticProfile();
        profile = profile with { Displays = [profile.Displays[0], profile.Displays[0] with { DisplayId = "clone", IsPrimary = false }] };
        var vm = new PopupViewModel();
        vm.Load([profile], profile.Id);
        var item = Assert.Single(vm.Profiles);
        Assert.Equal("F8", item.HotkeyText);
        Assert.True(item.Monitors[1].Left > item.Monitors[0].Left);
        Assert.True(item.Monitors[1].Top > item.Monitors[0].Top);
        Assert.All(item.Monitors, AssertInside);
        vm.CompleteActivation(true, "Activated", profile.Id);
        Assert.Equal(item.Monitors, vm.Profiles[0].Monitors);
        vm.RefreshProfiles([profile with { DirectHotkey = new HotkeyBinding(HotkeyModifiers.Control, 0x39) }], profile.Id);
        Assert.Equal("Ctrl+9", vm.Profiles[0].HotkeyText);
    }

    private static void AssertInside(SchematicMonitor monitor)
    {
        Assert.True(monitor.Left >= 0 && monitor.Top >= 0);
        Assert.True(monitor.Width > 0 && monitor.Height > 0);
        Assert.True(monitor.Left + monitor.Width <= 90.001);
        Assert.True(monitor.Top + monitor.Height <= 46.001);
    }

    internal static DisplayProfile SyntheticProfile()
    {
        var identity = new MonitorIdentityFingerprint("synthetic://panel", null, null, "SYNTHETIC", "TST", 1,
            "Synthetic", null, null, DisplayOutputTechnology.DisplayPort, 1, null, null);
        var first = new DesiredDisplayPath("primary", identity, "source-1", null, new DisplayPoint(0, 0),
            new DisplaySize(3440, 1440), new RefreshRate(60, 1), DisplayOrientation.Landscape,
            DisplayPathScaling.Identity, 100, false, true, "Synthetic wide");
        var second = first with
        {
            DisplayId = "second",
            SourceGroupId = "source-2",
            Position = new DisplayPoint(3440, 360),
            SourceResolution = new DisplaySize(1920, 1080),
            IsPrimary = false,
            FriendlyLabel = "Synthetic second"
        };
        return new DisplayProfile(Guid.NewGuid(), "Desktop", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            new HotkeyBinding(HotkeyModifiers.None, 0x77), [first, second], first.DisplayId, 1, []);
    }
}
