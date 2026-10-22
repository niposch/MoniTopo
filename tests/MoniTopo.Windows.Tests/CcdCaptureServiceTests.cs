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
        Assert.Equal(241_500_000UL, snapshot.ActivePaths[0].TargetSignal?.PixelRate);
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

    [Fact]
    public async Task CaptureRetriesActiveTargetRemovalWithoutLeakingDictionaryErrors()
    {
        var stable = FakeDisplayNative.WithExtendedDesktop().Snapshot;
        var removing = stable with { Paths = stable.Paths.ToArray() };
        removing.Paths[0].TargetInfo.TargetAvailable = 0;
        var native = new FakeDisplayNative { QueryHandler = count => count == 1 ? removing : stable };

        var snapshot = await CreateService(native).CaptureAsync();

        Assert.Equal(2, snapshot.ActivePaths.Count);
        Assert.Equal(3, native.QueryCount);
    }

    [Fact]
    public async Task PermanentlyIncoherentActiveTargetReturnsClassifiedBoundedError()
    {
        var removing = FakeDisplayNative.WithExtendedDesktop().Snapshot;
        removing.Paths[0].TargetInfo.TargetAvailable = 0;
        var native = new FakeDisplayNative { Snapshot = removing };

        var error = await Assert.ThrowsAsync<DisplayCaptureException>(() => CreateService(native).CaptureAsync());

        Assert.True(error.IsTransient);
        Assert.Equal(3, native.QueryCount);
    }

    [Fact]
    public async Task OptionalPreferredModeFailureDoesNotPreventActiveCapture()
    {
        var native = new FakeDisplayNative
        {
            Snapshot = FakeDisplayNative.WithExtendedDesktop().Snapshot,
            PreferredModeHandler = _ => throw new DisplayCaptureException(31, "Synthetic optional preferred-mode failure."),
        };

        var snapshot = await CreateService(native).CaptureAsync();

        Assert.Equal(3, snapshot.ConnectedDisplays.Count);
        Assert.All(snapshot.ConnectedDisplays, display => Assert.Null(display.Identity.PreferredMode));
        Assert.All(snapshot.ActivePaths, path => Assert.NotNull(path.TargetSignal));
    }

    [Fact]
    public async Task UnavailableInactiveMetadataIsOmittedWithoutBreakingActiveDesktop()
    {
        var native = new FakeDisplayNative
        {
            Snapshot = FakeDisplayNative.WithExtendedDesktop().Snapshot,
            TargetNameError = target => target == 2 ? 31 : 0,
        };

        var snapshot = await CreateService(native).CaptureAsync();

        Assert.Equal(2, snapshot.ConnectedDisplays.Count);
        Assert.All(snapshot.ConnectedDisplays, display => Assert.True(display.IsActive));
    }

    [Fact]
    public async Task TransientActiveMetadataFailureRetriesEntireSnapshot()
    {
        var failed = false;
        var native = new FakeDisplayNative
        {
            Snapshot = FakeDisplayNative.WithExtendedDesktop().Snapshot,
            TargetNameError = target =>
            {
                if (target == 0 && !failed)
                {
                    failed = true;
                    return 1167;
                }

                return 0;
            },
        };

        Assert.Equal(2, (await CreateService(native).CaptureAsync()).ActivePaths.Count);
        Assert.Equal(3, native.QueryCount);
    }

    [Fact]
    public async Task CaptureRejectsSnapshotThatChangedDuringMetadataQueries()
    {
        var original = FakeDisplayNative.WithExtendedDesktop().Snapshot;
        var changed = original with { Modes = original.Modes.ToArray() };
        changed.Modes[0].Mode.SourceMode.Width = 1920;
        var native = new FakeDisplayNative { QueryHandler = count => count == 1 ? original : changed };

        var snapshot = await CreateService(native).CaptureAsync();

        Assert.Equal(1920, snapshot.ActivePaths[0].SourceResolution.Width);
        Assert.Equal(4, native.QueryCount);
    }

    [Fact]
    public async Task ConcurrentHdrChangeRetriesInsteadOfSavingMixedPropertyState()
    {
        var native = new FakeDisplayNative
        {
            Snapshot = FakeDisplayNative.WithExtendedDesktop().Snapshot,
            HdrHandler = (count, _) => new NativeHdrState(true, count == 1, false),
        };

        var snapshot = await CreateService(native).CaptureAsync();

        Assert.All(snapshot.ActivePaths, path => Assert.False(path.HdrEnabled));
        Assert.Equal(3, native.QueryCount);
    }

    [Fact]
    public async Task ModeArrayReorderingDoesNotInvalidateCoherentSnapshot()
    {
        var original = FakeDisplayNative.WithExtendedDesktop().Snapshot;
        var reordered = new NativeDisplaySnapshot(original.Paths.ToArray(), original.Modes.Reverse().ToArray());
        for (var index = 0; index < 2; index++)
        {
            reordered.Paths[index].SourceInfo.ModeInfoIndex = NativeDisplayConfigModeIndexes.EncodeVirtual(checked((uint)(3 - index)));
            reordered.Paths[index].TargetInfo.ModeInfoIndex = NativeDisplayConfigModeIndexes.EncodeVirtual(checked((uint)(1 - index)));
        }

        var native = new FakeDisplayNative { QueryHandler = count => count == 1 ? original : reordered };

        Assert.Equal(2, (await CreateService(native).CaptureAsync()).ActivePaths.Count);
        Assert.Equal(2, native.QueryCount);
    }

    [Fact]
    public async Task CapturesVirtualDesktopImageAndDynamicRefreshPolicy()
    {
        var original = FakeDisplayNative.WithExtendedDesktop().Snapshot;
        var desktop = new NativeModeInfo
        {
            InfoType = 3,
            Id = 0,
            AdapterId = original.Paths[0].TargetInfo.AdapterId,
            Mode = new NativeModeUnion
            {
                DesktopImageInfo = new NativeDesktopImageInfo
                {
                    PathSourceSize = new NativePoint(2560, 1440),
                    DesktopImageRegion = new NativeRect(0, 0, 2560, 1440),
                    DesktopImageClip = new NativeRect(0, 0, 2560, 1440),
                },
            },
        };
        var raw = new NativeDisplaySnapshot(original.Paths.ToArray(), [.. original.Modes, desktop]);
        raw.Paths[0].Flags |= NativeDisplayConfigPathFlags.SupportVirtualMode | NativeDisplayConfigPathFlags.BoostRefreshRate;
        raw.Paths[0].TargetInfo.ModeInfoIndex = (2u << 16) | 4u;

        var snapshot = await CreateService(new FakeDisplayNative { Snapshot = raw }).CaptureAsync();

        Assert.True(snapshot.ActivePaths[0].BoostRefreshRate);
        Assert.False(snapshot.ActivePaths[1].BoostRefreshRate);
        Assert.Equal(new DisplayDesktopImage(new DisplaySize(2560, 1440), new DisplayRectangle(0, 0, 2560, 1440), new DisplayRectangle(0, 0, 2560, 1440)), snapshot.ActivePaths[0].DesktopImage);
    }

    [Fact]
    public async Task IncompletePhysicalSignalIsNeverSaved()
    {
        var raw = FakeDisplayNative.WithExtendedDesktop().Snapshot;
        raw.Modes[2].Mode.TargetMode.TargetVideoSignalInfo.PixelRate = 0;

        var error = await Assert.ThrowsAsync<DisplayCaptureException>(() => CreateService(new FakeDisplayNative { Snapshot = raw }).CaptureAsync());

        Assert.True(error.IsTransient);
        Assert.Contains("physical signal", error.Message, StringComparison.Ordinal);
    }

    private static CcdCaptureService CreateService(FakeDisplayNative native) =>
        new(native, new FakeScaleService(), new FakeIdentityProvider(), new FrozenTimeProvider());

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

    internal int DpiQueryCount { get; private set; }

    internal int HdrQueryCount { get; private set; }

    internal NativeHdrState? HdrState { get; init; }

    internal Func<int, uint, NativeHdrState>? HdrHandler { get; init; }

    internal Func<int, NativeDisplaySnapshot>? QueryHandler { get; init; }

    internal Func<uint, int>? TargetNameError { get; init; }

    internal Func<uint, NativeTargetPreferredMode>? PreferredModeHandler { get; init; }

    internal int QueryCount { get; private set; }

    public NativeDisplaySnapshot Query(uint flags)
    {
        QueryCount++;
        return QueryHandler?.Invoke(QueryCount) ?? Snapshot;
    }

    public NativeTargetDeviceName GetTargetName(NativeAdapterId adapterId, uint targetId)
    {
        if (TargetNameError?.Invoke(targetId) is int error && error != 0)
        {
            throw new DisplayCaptureException(error, "Synthetic target-name failure.");
        }

        return new NativeTargetDeviceName
        {
            Header = new NativeDeviceInfoHeader { AdapterId = adapterId, Id = targetId },
            OutputTechnology = 10,
            ConnectorInstance = targetId,
        };
    }

    public string GetSourceName(NativeAdapterId adapterId, uint sourceId) => $"SYNTHETIC{sourceId}";

    public NativeTargetPreferredMode GetTargetPreferredMode(NativeAdapterId adapterId, uint targetId) => PreferredModeHandler?.Invoke(targetId) ?? new()
    {
        Width = 2560,
        Height = 1440,
    };

    public NativeHdrState GetHdrState(NativeAdapterId adapterId, uint targetId)
    {
        HdrQueryCount++;
        return HdrHandler?.Invoke(HdrQueryCount, targetId) ?? HdrState ??
            new NativeHdrState(IsSupported: true, IsEnabled: targetId == 0, IsLimitedByPolicy: false);
    }

    public (int ErrorCode, NativeDpiScaleGet Packet) TryGetDpiScale(NativeAdapterId adapterId, uint sourceId)
    {
        DpiQueryCount++;
        return (DpiErrorCode, DpiPacket);
    }

    public bool IsPrimaryGdiDevice(string gdiDeviceName) => gdiDeviceName == "SYNTHETIC0";

    internal static FakeDisplayNative WithExtendedDesktop()
    {
        var adapter = new NativeAdapterId(1, 0);
        var modes = new[]
        {
            SourceMode(adapter, sourceId: 0, modeIndex: 0, x: 0),
            SourceMode(adapter, sourceId: 1, modeIndex: 1, x: 2560),
            TargetMode(adapter, targetId: 0),
            TargetMode(adapter, targetId: 1),
        };
        var paths = new[]
        {
            Path(adapter, sourceId: 0, targetId: 0, sourceModeIndex: 0, targetModeIndex: 2, active: true),
            Path(adapter, sourceId: 1, targetId: 1, sourceModeIndex: 1, targetModeIndex: 3, active: true),
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

    private static NativeModeInfo TargetMode(NativeAdapterId adapter, uint targetId) => new()
    {
        InfoType = 2,
        Id = targetId,
        AdapterId = adapter,
        Mode = new NativeModeUnion
        {
            TargetMode = new NativeTargetMode
            {
                TargetVideoSignalInfo = new NativeVideoSignalInfo
                {
                    PixelRate = 241_500_000,
                    HorizontalSyncFrequency = new NativeRational(88_787, 1),
                    VerticalSyncFrequency = new NativeRational(60, 1),
                    ActiveSize = new NativeRegion(2560, 1440),
                    TotalSize = new NativeRegion(2720, 1481),
                    ScanLineOrdering = 1,
                },
            },
        },
    };

    private static NativePathInfo Path(
        NativeAdapterId adapter,
        uint sourceId,
        uint targetId,
        uint sourceModeIndex,
        bool active,
        uint targetModeIndex = 0) => new()
        {
            SourceInfo = new NativePathSourceInfo
            {
                AdapterId = adapter,
                Id = sourceId,
                ModeInfoIndex = (sourceModeIndex << 16) | 0xFFFF,
            },
            TargetInfo = new NativePathTargetInfo
            {
                AdapterId = adapter,
                Id = targetId,
                ModeInfoIndex = (targetModeIndex << 16) | 0xFFFF,
                OutputTechnology = 10,
                Rotation = 1,
                Scaling = 1,
                RefreshRate = new NativeRational(60, 1),
                TargetAvailable = 1,
            },
            Flags = active ? 1u : 0u,
        };
}
