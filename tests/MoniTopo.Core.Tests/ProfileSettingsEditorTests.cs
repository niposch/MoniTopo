using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;
using MoniTopo.Core.Normalization;
using MoniTopo.Core.Validation;

namespace MoniTopo.Core.Tests;

public sealed class ProfileSettingsEditorTests
{
    [Fact]
    public async Task RequestedModeSurvivesConfigurationPersistenceWithoutInventedTiming()
    {
        var folder = Path.Combine(Path.GetTempPath(), "MoniTopo.Editor.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var requested = Change(Profile(), NewMode);
            var store = new MoniTopo.Core.Persistence.JsonConfigurationStore(Path.Combine(folder, "config.json"));
            await store.SaveAsync(TestData.Configuration(requested));
            var loaded = Assert.Single((await store.LoadAsync()).Profiles);
            Assert.Equal(5, loaded.CaptureSchemaVersion);
            Assert.Equal(requested.Displays[0].ModeRequest, loaded.Displays[0].ModeRequest);
            Assert.Null(loaded.Displays[0].TargetSignal);
            Assert.Equal(NewMode.RefreshRate, loaded.Displays[0].RefreshRate);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ModeChangeClearsStaleTimingsAndPreservesProfileMetadata()
    {
        var profile = Profile();
        var edited = Change(profile, mode: NewMode);
        Assert.Equal(profile.Id, edited.Id);
        Assert.Equal(profile.DirectHotkey, edited.DirectHotkey);
        Assert.Equal(profile.PrimaryDisplayId, edited.PrimaryDisplayId);
        Assert.Equal(5, edited.CaptureSchemaVersion);
        Assert.Null(edited.Displays[0].TargetSignal);
        Assert.Null(edited.Displays[0].DesktopImage);
        Assert.False(edited.Displays[0].BoostRefreshRate);
        Assert.Equal(new DisplayModeRequest(1), edited.Displays[0].ModeRequest);
        Assert.Equal(NewMode.Resolution, edited.Displays[0].SourceResolution);
        Assert.Empty(ProfileValidator.Validate(edited));
        Assert.Equal(edited.Displays[0].ModeRequest, ProfileNormalizer.Normalize(edited).Displays[0].ModeRequest);
    }

    [Fact]
    public void PropertyOnlyEditsKeepCapturedSignalAndUnavailableSavedValuesMayRemain()
    {
        var profile = Profile();
        var edited = Change(profile, scale: 200, hdr: true);
        Assert.Equal(profile.Displays[0].TargetSignal, edited.Displays[0].TargetSignal);
        Assert.Null(edited.Displays[0].ModeRequest);
        Assert.Equal(200, edited.Displays[0].WindowsUiScalePercent);
        Assert.True(edited.Displays[0].HdrEnabled);
        Assert.Equal(profile.Displays, ProfileSettingsEditor.Apply(profile, Edits(profile), []).Displays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnsupportedChangesAreRejected(int kind)
    {
        var profile = Profile();
        var edit = Edits(profile)[0];
        edit = kind switch { 0 => edit with { Mode = NewMode }, 1 => edit with { ScalePercent = 300 }, _ => edit with { HdrEnabled = true } };
        Assert.Throws<InvalidOperationException>(() => ProfileSettingsEditor.Apply(profile, [edit], []));
    }

    [Fact]
    public void MissingDuplicateAndUnknownDisplayEditsAreRejected()
    {
        var profile = Profile(); var edit = Edits(profile)[0];
        Assert.Throws<InvalidOperationException>(() => ProfileSettingsEditor.Apply(profile, [], []));
        Assert.Throws<InvalidOperationException>(() => ProfileSettingsEditor.Apply(profile, [edit, edit], []));
        Assert.Throws<InvalidOperationException>(() => ProfileSettingsEditor.Apply(profile, [edit with { DisplayId = "unknown" }], []));
    }

    [Theory]
    [InlineData(2560, 0, 1920, 0)]
    [InlineData(-2560, 0, -2560, 0)]
    [InlineData(0, 1440, 0, 1080)]
    [InlineData(0, -1440, 0, -1440)]
    public void ResolutionChangesKeepNeighborOnItsConnectedEdge(int x, int y, int expectedX, int expectedY)
    {
        var profile = Profile();
        profile = profile with
        {
            Displays = [profile.Displays[0], profile.Displays[0] with
        { DisplayId = "second", SourceGroupId = "second-source", IsPrimary = false, Position = new DisplayPoint(x, y) }]
        };
        var edits = Edits(profile); edits[0] = edits[0] with { Mode = NewMode };
        var edited = ProfileSettingsEditor.Apply(profile, edits, [Capability(profile.Displays[0].DisplayId)]);
        Assert.Equal(new DisplayPoint(expectedX, expectedY), edited.Displays[1].Position);
        Assert.Equal(profile.Displays[1].TargetSignal, edited.Displays[1].TargetSignal);
    }

    [Fact]
    public void AmbiguousResizeOverlapAndCloneModeChangesAreRejected()
    {
        var profile = Profile();
        profile = profile with
        {
            Displays = [profile.Displays[0], profile.Displays[0] with
        { DisplayId = "second", SourceGroupId = "second-source", IsPrimary = false, Position = new DisplayPoint(2000, 2000) }]
        };
        var huge = new AvailableDisplayMode(new DisplaySize(4096, 4096), new RefreshRate(60, 1), 1);
        var edits = Edits(profile); edits[0] = edits[0] with { Mode = huge };
        Assert.Throws<InvalidOperationException>(() => ProfileSettingsEditor.Apply(profile, edits,
            [Capability(profile.Displays[0].DisplayId) with { Modes = [huge] }]));
        profile = profile with
        {
            Displays = [profile.Displays[0], profile.Displays[1] with
        { SourceGroupId = profile.Displays[0].SourceGroupId, Position = new DisplayPoint(0, 0) }]
        };
        edits = Edits(profile); edits[0] = edits[0] with { Mode = NewMode };
        Assert.Throws<InvalidOperationException>(() => ProfileSettingsEditor.Apply(profile, edits, [Capability(profile.Displays[0].DisplayId)]));
    }

    [Fact]
    public void RequestedModeMustMatchObservedResolutionRateHdrScaleAndValidPhysicalTiming()
    {
        var requested = Change(Profile(), mode: NewMode);
        var saved = requested.Displays[0];
        var observed = saved with { ModeRequest = null, TargetSignal = Signal(NewMode.Resolution, NewMode.RefreshRate), PathScaling = DisplayPathScaling.Identity };
        var matcher = new ActiveProfileMatcher(new MonitorIdentityResolver());
        CapturedDisplaySnapshot Snapshot(DesiredDisplayPath display) => new([display],
            [new ConnectedDisplayState("runtime", display.Identity, true, display.FriendlyLabel, display)], display.DisplayId, DateTimeOffset.UnixEpoch);
        Assert.True(matcher.Match(requested, Snapshot(observed)).IsMatch);
        foreach (var mismatch in new[] { observed with { SourceResolution = new DisplaySize(1280, 720) },
            observed with { RefreshRate = new RefreshRate(30, 1) }, observed with { RefreshRate = new RefreshRate(6003, 50) }, observed with { HdrEnabled = true },
            observed with { WindowsUiScalePercent = 200 }, observed with { TargetSignal = null } })
            Assert.False(matcher.Match(requested, Snapshot(mismatch)).IsMatch);
        Assert.Contains(ProfileValidator.Validate(requested with { Displays = [saved with { TargetSignal = observed.TargetSignal }] }), error => error.Code == "profile.display.request");
        Assert.Contains(ProfileValidator.Validate(requested with { Displays = [saved with { ModeRequest = new DisplayModeRequest(99) }] }), error => error.Code == "profile.display.request");
    }

    private static readonly AvailableDisplayMode NewMode = new(new DisplaySize(1920, 1080), new RefreshRate(120, 1), 1);
    private static DisplaySettingsCapability Capability(string id) => new(id, [NewMode], [100, 150, 200], [false, true], null);
    private static DisplayProfile Profile()
    {
        var display = TestData.Display() with { TargetSignal = Signal(new DisplaySize(2560, 1440), new RefreshRate(60, 1)), BoostRefreshRate = false };
        return TestData.Profile(hotkey: new HotkeyBinding(HotkeyModifiers.Control, 0x31), displays: [display]) with { CaptureSchemaVersion = 4 };
    }
    private static DisplayTargetSignal Signal(DisplaySize size, RefreshRate rate) => new(241_500_000,
        new RefreshRate(90_000, 1), rate, size, new DisplaySize(size.Width + 160, size.Height + 40), 0, 1);
    private static DisplaySettingsEdit[] Edits(DisplayProfile profile) => profile.Displays.Select(display =>
        new DisplaySettingsEdit(display.DisplayId, ProfileSettingsEditor.ModeOf(display), display.WindowsUiScalePercent, display.HdrEnabled)).ToArray();
    private static DisplayProfile Change(DisplayProfile profile, AvailableDisplayMode? mode = null, int? scale = null, bool? hdr = null) =>
        ProfileSettingsEditor.Apply(profile, [Edits(profile)[0] with { Mode = mode ?? ProfileSettingsEditor.ModeOf(profile.Displays[0]),
            ScalePercent = scale ?? profile.Displays[0].WindowsUiScalePercent, HdrEnabled = hdr ?? profile.Displays[0].HdrEnabled }], [Capability(profile.Displays[0].DisplayId)]);
}
