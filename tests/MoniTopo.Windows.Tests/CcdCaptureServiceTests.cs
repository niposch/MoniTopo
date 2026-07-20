using MoniTopo.Core.Models;
using MoniTopo.Windows.Display;

namespace MoniTopo.Windows.Tests;

public sealed class CcdCaptureServiceTests
{
    [Fact]
    public async Task ConvertsSyntheticCcdSnapshotWithoutCallingRealDisplays()
    {
        var native = FakeDisplayNative.WithExtendedDesktop();
        var service = new CcdCaptureService(
            native,
            new FakeScaleService(),
            new FakeIdentityProvider(),
            new FrozenTimeProvider());

        var snapshot = await service.CaptureAsync();

        Assert.Equal(2, snapshot.ActivePaths.Count);
        Assert.Equal(3, snapshot.ConnectedDisplays.Count);
        Assert.Equal("display-1", snapshot.PrimaryDisplayId);
        Assert.True(snapshot.ActivePaths[0].IsPrimary);
        Assert.True(snapshot.ActivePaths[0].HdrEnabled);
        Assert.Equal(new DisplayPoint(0, 0), snapshot.ActivePaths[0].Position);
        Assert.Equal(new DisplayPoint(2560, 0), snapshot.ActivePaths[1].Position);
        Assert.Equal(100, snapshot.ActivePaths[0].WindowsUiScalePercent);
        Assert.Equal(150, snapshot.ActivePaths[1].WindowsUiScalePercent);
        Assert.Contains(snapshot.ConnectedDisplays, display => !display.IsActive);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero), snapshot.CapturedUtc);
    }

    [Fact]
    public async Task CaptureRejectsUnsupportedScalingRatherThanSavingPartialProfile()
    {
        var service = new CcdCaptureService(
            FakeDisplayNative.WithExtendedDesktop(),
            new FakeScaleService(isSupported: false),
            new FakeIdentityProvider(),
            new FrozenTimeProvider());

        var exception = await Assert.ThrowsAsync<DisplayCaptureException>(() => service.CaptureAsync());

        Assert.Contains("scaling", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureHonorsCancellationBeforeNativeQuery()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var service = new CcdCaptureService(
            new FakeDisplayNative(),
            new FakeScaleService(),
            new FakeIdentityProvider(),
            new FrozenTimeProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CaptureAsync(source.Token));
    }

    private sealed class FakeScaleService(bool isSupported = true) : IDisplayScaleService
    {
        public DisplayScaleCapability Query(DisplaySourceAddress source) => isSupported
            ? new DisplayScaleCapability(true, source.SourceId == 0 ? 100 : 150, 100, [100, 125, 150], null)
            : new DisplayScaleCapability(false, null, null, [], "Windows display scaling is unavailable.");
    }

    private sealed class FakeIdentityProvider : IMonitorIdentityProvider
    {
        public MonitorIdentityFingerprint Resolve(NativeTargetDeviceName targetName, DisplaySize? preferredMode) => new(
            MonitorDevicePath: $"synthetic://target/{targetName.Header.Id}",
            DeviceInstanceId: $"SYNTHETIC\\TARGET{targetName.Header.Id}",
            DeviceContainerId: null,
            EdidSerial: $"FAKE-{targetName.Header.Id}",
            EdidManufacturerId: "TST",
            EdidProductCode: checked((int)targetName.Header.Id),
            FriendlyModelName: "Synthetic panel",
            PhysicalWidthMillimeters: 600,
            PhysicalHeightMillimeters: 340,
            OutputTechnology: DisplayOutputTechnology.DisplayPort,
            ConnectorInstance: targetName.Header.Id,
            PreferredMode: preferredMode,
            SupportedModeSignature: "synthetic");
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 20, 15, 0, 0, TimeSpan.Zero);
    }
}

internal sealed class FakeDisplayNative : IDisplayConfigNativeFacade
{
    internal NativeDisplaySnapshot Snapshot { get; init; } = new([], []);

    internal int DpiErrorCode { get; init; }

    internal NativeDpiScaleGet DpiPacket { get; init; }

    public NativeDisplaySnapshot Query(uint flags) => Snapshot;

    public NativeTargetDeviceName GetTargetName(NativeAdapterId adapterId, uint targetId) => new()
    {
        Header = new NativeDeviceInfoHeader { AdapterId = adapterId, Id = targetId },
        OutputTechnology = 10,
        ConnectorInstance = targetId,
    };

    public string GetSourceName(NativeAdapterId adapterId, uint sourceId) => $"SYNTHETIC{sourceId}";

    public NativeTargetPreferredMode GetTargetPreferredMode(NativeAdapterId adapterId, uint targetId) => new()
    {
        Width = 2560,
        Height = 1440,
    };

    public NativeAdvancedColorInfo GetAdvancedColorInfo(NativeAdapterId adapterId, uint targetId) => new()
    {
        Value = targetId == 0 ? 0x3u : 0x1u,
    };

    public (int ErrorCode, NativeDpiScaleGet Packet) TryGetDpiScale(NativeAdapterId adapterId, uint sourceId) =>
        (DpiErrorCode, DpiPacket);

    public bool IsPrimaryGdiDevice(string gdiDeviceName) => gdiDeviceName == "SYNTHETIC0";

    internal static FakeDisplayNative WithExtendedDesktop()
    {
        var adapter = new NativeAdapterId(1, 0);
        var modes = new[]
        {
            SourceMode(adapter, sourceId: 0, modeIndex: 0, x: 0),
            SourceMode(adapter, sourceId: 1, modeIndex: 1, x: 2560),
        };
        var paths = new[]
        {
            Path(adapter, sourceId: 0, targetId: 0, sourceModeIndex: 0, active: true),
            Path(adapter, sourceId: 1, targetId: 1, sourceModeIndex: 1, active: true),
            Path(adapter, sourceId: 2, targetId: 2, sourceModeIndex: 0, active: false),
        };
        return new FakeDisplayNative { Snapshot = new NativeDisplaySnapshot(paths, modes) };
    }

    private static NativeModeInfo SourceMode(NativeAdapterId adapter, uint sourceId, uint modeIndex, int x) => new()
    {
        InfoType = 1,
        Id = sourceId,
        AdapterId = adapter,
        Mode = new NativeModeUnion
        {
            SourceMode = new NativeSourceMode
            {
                Width = 2560,
                Height = 1440,
                Position = new NativePoint(x, 0),
            },
        },
    };

    private static NativePathInfo Path(
        NativeAdapterId adapter,
        uint sourceId,
        uint targetId,
        uint sourceModeIndex,
        bool active) => new()
        {
            SourceInfo = new NativePathSourceInfo
            {
                AdapterId = adapter,
                Id = sourceId,
                ModeInfoIndex = sourceModeIndex << 16,
            },
            TargetInfo = new NativePathTargetInfo
            {
                AdapterId = adapter,
                Id = targetId,
                OutputTechnology = 10,
                Rotation = 1,
                Scaling = 1,
                RefreshRate = new NativeRational(60, 1),
                TargetAvailable = 1,
            },
            Flags = active ? 1u : 0u,
        };
}
