using System.Runtime.InteropServices;
using System.Text;
using MoniTopo.Core.Models;
using MoniTopo.Windows.Display;

namespace MoniTopo.Windows.Tests;

public sealed class DisplaySettingsCapabilitiesTests
{
    [Fact]
    public async Task QueryFallsBackWhenVirtualRefreshAwarenessIsUnavailable()
    {
        var fixture = new Fixture(); fixture.Native.OlderWindows = true;
        var capability = Assert.Single(await fixture.Service.QueryAsync(fixture.Profile));
        Assert.Single(capability.Modes);
        Assert.Equal(2, fixture.Native.Queries);
    }

    [Fact]
    public void DxgiLayoutsAndModeConversionPreserveFractionalRatesAndFilterUnsafeEntries()
    {
        Assert.Equal(32, Marshal.SizeOf<NativeDxgiMode>());
        Assert.Equal(96, Marshal.SizeOf<NativeDxgiOutputDescription>());
        Assert.Equal(88, Marshal.OffsetOf<NativeDxgiOutputDescription>(nameof(NativeDxgiOutputDescription.Monitor)).ToInt32());
        var good = new NativeDxgiMode { Width = 1920, Height = 1080, Rate = new NativeRational(60000, 1001), ScanLineOrdering = 1 };
        var modes = DxgiDisplayModeCatalog.ConvertModes([good, good, good with { Width = 0 }, good with { Stereo = 1 },
            good with { ScanLineOrdering = 2 }, good with { Rate = new NativeRational(60, 0) }]).ToArray();
        Assert.Equal(new RefreshRate(60000, 1001), Assert.Single(modes).RefreshRate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveDisplayQueriesModeScaleAndHdrCapabilitiesWithoutAnyWriter(bool policyLimited)
    {
        var fixture = new Fixture(); fixture.Native.Hdr = new NativeHdrState(true, false, policyLimited);
        var capability = Assert.Single(await fixture.Service.QueryAsync(fixture.Profile));
        Assert.Single(capability.Modes);
        Assert.Equal([100, 125, 150], capability.ScalePercentages);
        Assert.Contains(false, capability.HdrStates);
        Assert.Equal(!policyLimited, capability.HdrStates.Contains(true));
        Assert.Equal(policyLimited ? 1 : 2, capability.HdrStates.Count);
        Assert.Equal(2, fixture.Native.Queries);
        Assert.Equal("synthetic-source", fixture.Modes.QueriedName);
    }

    [Fact]
    public async Task InactiveOrMissingDisplayDoesNotQueryAnUnrelatedSource()
    {
        var fixture = new Fixture();
        fixture.Capture.Value = fixture.Capture.Value with
        {
            ActivePaths = [],
            ConnectedDisplays =
            [fixture.Capture.Value.ConnectedDisplays[0] with { IsActive = false, ActivePath = null }]
        };
        var capability = Assert.Single(await fixture.Service.QueryAsync(fixture.Profile));
        Assert.Empty(capability.Modes); Assert.Empty(capability.HdrStates); Assert.Null(fixture.Modes.QueriedName);
        fixture.Capture.Value = fixture.Capture.Value with { ConnectedDisplays = [] };
        capability = Assert.Single(await fixture.Service.QueryAsync(fixture.Profile));
        Assert.Empty(capability.ScalePercentages); Assert.NotNull(capability.Message);
    }

    [Fact]
    public async Task ChangingSourceDuringEnumerationIsRejectedAndHdrFailureDoesNotDiscardOtherOptions()
    {
        var fixture = new Fixture(); fixture.Native.FailHdr = true;
        var capability = Assert.Single(await fixture.Service.QueryAsync(fixture.Profile));
        Assert.Empty(capability.HdrStates); Assert.Single(capability.Modes); Assert.NotNull(capability.Message);
        fixture.Native.ChangeAfterQuery = true;
        await Assert.ThrowsAsync<DisplayCaptureException>(() => fixture.Service.QueryAsync(fixture.Profile));
    }

    [Fact]
    public async Task IndistinguishableSavedDisplaysCannotExposeGuessedModes()
    {
        var fixture = new Fixture();
        fixture.Profile = fixture.Profile with { Displays = [fixture.Profile.Displays[0], fixture.Profile.Displays[0] with { DisplayId = "clone", IsPrimary = false }] };
        // Identity resolution of indistinguishable clones must not guess: no options is safe.
        var capabilities = await fixture.Service.QueryAsync(fixture.Profile);
        Assert.All(capabilities, capability => Assert.Empty(capability.Modes));
    }

    [Fact]
    public async Task CloneSourceModesAndScaleAreReadOnlyButHdrRemainsPerTarget()
    {
        var fixture = new Fixture(); fixture.Native.Clone = true;
        var clone = fixture.Profile.Displays[0] with
        {
            DisplayId = "clone",
            IsPrimary = false,
            Identity = fixture.Profile.Displays[0].Identity with { MonitorDevicePath = "synthetic://clone", EdidSerial = "TEST-CLONE" }
        };
        fixture.Profile = fixture.Profile with { Displays = [fixture.Profile.Displays[0], clone] };
        fixture.Capture.Value = fixture.Capture.Value with
        {
            ActivePaths = fixture.Profile.Displays,
            ConnectedDisplays = [fixture.Capture.Value.ConnectedDisplays[0], new ConnectedDisplayState("0000000000000001:T3", clone.Identity, true, "Clone", clone)]
        };
        var capabilities = await fixture.Service.QueryAsync(fixture.Profile);
        Assert.Equal(2, capabilities.Count);
        Assert.All(capabilities, capability => { Assert.Empty(capability.Modes); Assert.Empty(capability.ScalePercentages); Assert.Equal(2, capability.HdrStates.Count); });
    }

    [Fact]
    public async Task PortraitOptionsUseTheSavedOrientationWithoutChangingTheDesktop()
    {
        var fixture = new Fixture();
        fixture.Profile = fixture.Profile with { Displays = [fixture.Profile.Displays[0] with { Orientation = DisplayOrientation.Portrait }] };
        var capability = Assert.Single(await fixture.Service.QueryAsync(fixture.Profile));
        Assert.Equal(new DisplaySize(1080, 1920), Assert.Single(capability.Modes).Resolution);
    }

    private sealed class Fixture
    {
        internal Capture Capture { get; }
        internal Native Native { get; } = new();
        internal Modes Modes { get; } = new();
        internal DisplayProfile Profile { get; set; }
        internal DisplaySettingsCapabilities Service { get; }
        internal Fixture()
        {
            var identity = new MonitorIdentityFingerprint("synthetic://panel", null, null, "TEST-1", "TST", 1, "Synthetic", null, null, DisplayOutputTechnology.DisplayPort, 1, null, null);
            var display = new DesiredDisplayPath("one", identity, "0000000000000001:S0", null, new DisplayPoint(0, 0),
                new DisplaySize(1920, 1080), new RefreshRate(60, 1), DisplayOrientation.Landscape, DisplayPathScaling.Identity, 100, false, true, "Synthetic");
            Profile = new DisplayProfile(Guid.NewGuid(), "Desktop", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, [display], "one", 4, []);
            Capture = new Capture
            {
                Value = new CapturedDisplaySnapshot([display],
                [new ConnectedDisplayState("0000000000000001:T2", identity, true, "Synthetic", display)], "one", DateTimeOffset.UnixEpoch)
            };
            Service = new DisplaySettingsCapabilities(Capture, Native, Modes, new Scales());
        }
    }
    private sealed class Capture : IDisplayCaptureService
    {
        internal CapturedDisplaySnapshot Value = null!;
        public Task<CapturedDisplaySnapshot> CaptureAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
    }
    private sealed class Modes : IDisplayModeCatalog
    {
        internal string? QueriedName;
        public IReadOnlyList<AvailableDisplayMode> ReadModes(string gdiDeviceName)
        { QueriedName = gdiDeviceName; return [new(new DisplaySize(1920, 1080), new RefreshRate(60000, 1001), 1)]; }
    }
    private sealed class Scales : IDisplayScaleService
    {
        public DisplayScaleCapability Query(DisplaySourceAddress source) => new(true, 100, 100, [100, 125, 150], null);
    }
    private sealed class Native : IDisplayConfigNativeFacade
    {
        internal int Queries;
        internal bool ChangeAfterQuery;
        internal bool FailHdr;
        internal bool Clone;
        internal bool OlderWindows;
        internal NativeHdrState Hdr = new(true, false, false);
        public NativeDisplaySnapshot Query(uint flags)
        {
            if (OlderWindows && (flags & 0x40) != 0) throw new DisplayCaptureException(87, "Synthetic unsupported flag");
            Queries++;
            var path = new NativePathInfo
            {
                Flags = 1 | 8,
                SourceInfo = new NativePathSourceInfo { AdapterId = new NativeAdapterId(1, 0), Id = ChangeAfterQuery && Queries % 2 == 0 ? 1u : 0 },
                TargetInfo = new NativePathTargetInfo { AdapterId = new NativeAdapterId(1, 0), Id = 2, TargetAvailable = 1 }
            };
            return new NativeDisplaySnapshot(Clone ? [path, path with { TargetInfo = path.TargetInfo with { Id = 3 } }] : [path], []);
        }
        public NativeTargetDeviceName GetTargetName(NativeAdapterId adapterId, uint targetId)
        {
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeTargetDeviceName>());
            try
            {
                Marshal.Copy(new byte[Marshal.SizeOf<NativeTargetDeviceName>()], 0, pointer, Marshal.SizeOf<NativeTargetDeviceName>());
                var text = Encoding.Unicode.GetBytes(targetId == 3 ? "synthetic://clone\0" : "synthetic://panel\0");
                Marshal.Copy(text, 0, pointer + Marshal.OffsetOf<NativeTargetDeviceName>("_devicePath").ToInt32(), text.Length);
                return Marshal.PtrToStructure<NativeTargetDeviceName>(pointer);
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        public string GetSourceName(NativeAdapterId adapterId, uint sourceId) => "synthetic-source";
        public NativeTargetPreferredMode GetTargetPreferredMode(NativeAdapterId adapterId, uint targetId) => throw new InvalidOperationException();
        public NativeHdrState GetHdrState(NativeAdapterId adapterId, uint targetId) => FailHdr ? throw new DisplayCaptureException(50, "Synthetic HDR query failure") : Hdr;
        public (int ErrorCode, NativeDpiScaleGet Packet) TryGetDpiScale(NativeAdapterId adapterId, uint sourceId) => throw new InvalidOperationException();
        public bool IsPrimaryGdiDevice(string gdiDeviceName) => true;
    }
}
