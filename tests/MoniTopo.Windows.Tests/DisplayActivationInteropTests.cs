using System.Diagnostics;
using System.Text.Json;
using MoniTopo.Core.Activation;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Models;
using MoniTopo.Core.Recovery;
using MoniTopo.Windows.Display;

namespace MoniTopo.Windows.Tests;

public sealed class DisplayActivationInteropTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AuthorizationRequiresExplicitCommandAndExactOptIn()
    {
        Assert.False(DisplayMutationAuthorization.TryCreate(false, _ => "1", out _));
        Assert.False(DisplayMutationAuthorization.TryCreate(true, _ => null, out _));
        Assert.False(DisplayMutationAuthorization.TryCreate(true, _ => "true", out _));
        Assert.True(DisplayMutationAuthorization.TryCreate(true, _ => "1", out var authorization));
        Assert.NotNull(authorization);
    }

    [Fact]
    public void CoreMutationUsesExactFlagsWithoutAllowChanges()
    {
        var api = new FakeMutationApi();
        var facade = new DisplayConfigMutationFacade(Authorization(), api);
        var plan = new NativeDisplaySnapshot([new NativePathInfo()], [new NativeModeInfo()]);

        facade.Validate(plan);
        facade.ApplyTemporary(plan);
        facade.Persist(plan);

        Assert.Equal(
            [
                SetDisplayConfigFlags.UseSuppliedDisplayConfig | SetDisplayConfigFlags.Validate | SetDisplayConfigFlags.VirtualModeAware | SetDisplayConfigFlags.VirtualRefreshRateAware,
                SetDisplayConfigFlags.UseSuppliedDisplayConfig | SetDisplayConfigFlags.Apply | SetDisplayConfigFlags.VirtualModeAware | SetDisplayConfigFlags.VirtualRefreshRateAware,
                SetDisplayConfigFlags.UseSuppliedDisplayConfig | SetDisplayConfigFlags.Apply | SetDisplayConfigFlags.SaveToDatabase | SetDisplayConfigFlags.VirtualModeAware | SetDisplayConfigFlags.VirtualRefreshRateAware,
            ],
            api.CoreFlags);
        Assert.All(api.CoreFlags, flags => Assert.Equal(0u, flags & 0x400u));
    }

    [Fact]
    public void DpiAndHdrSettersBuildExpectedPacketsThroughFakeApi()
    {
        var api = new FakeMutationApi();
        var facade = new DisplayConfigMutationFacade(Authorization(), api);
        var adapter = new NativeAdapterId(7, 2);

        facade.SetDpiScale(adapter, 3, -1);
        facade.SetAdvancedColor(adapter, 9, enabled: true);

        Assert.Equal(UndocumentedDpiScaleContract.SetRequestType, api.DpiPacket.Type);
        Assert.Equal(-1, api.DpiPacket.RelativeScale);
        Assert.Equal(10, api.ColorPacket.Type);
        Assert.Equal(1u, api.ColorPacket.Value);
    }

    [Fact]
    public void PlanReconstructsExactSourceAndTargetModes()
    {
        var adapter = new NativeAdapterId(1, 0);
        var available = AvailableConfiguration(adapter);
        var first = Display("display-1", "source-a", true, new DisplayPoint(0, 0), 0);
        var second = Display("display-2", "source-b", false, new DisplayPoint(2560, 0), 1);
        var profile = Profile([first, second]);
        var bindings = new[]
        {
            new ResolvedIdentityBinding(first.DisplayId, TargetKey(adapter, 0), 100),
            new ResolvedIdentityBinding(second.DisplayId, TargetKey(adapter, 1), 100),
        };

        var plan = DisplayConfigurationPlanBuilder.Build(profile, bindings, available);

        Assert.Equal(2, plan.Paths.Length);
        Assert.Equal(4, plan.Modes.Length);
        Assert.Equal(0, plan.Modes[0].Mode.SourceMode.Position.X);
        Assert.Equal(2560, plan.Modes[1].Mode.SourceMode.Position.X);
        Assert.Equal(241_500_000UL, plan.Modes[2].Mode.TargetMode.TargetVideoSignalInfo.PixelRate);
        Assert.All(plan.Paths, path => Assert.Equal(1u, path.Flags));
    }

    [Fact]
    public void PlanRejectsLegacyProfileWithoutTargetSignal()
    {
        var adapter = new NativeAdapterId(1, 0);
        var legacy = Display("display-1", "source-a", true, new DisplayPoint(0, 0), 0) with { TargetSignal = null };
        var profile = Profile([legacy]);

        var exception = Assert.Throws<ActivationFailureException>(() => DisplayConfigurationPlanBuilder.Build(
            profile,
            [new ResolvedIdentityBinding(legacy.DisplayId, TargetKey(adapter, 0), 100)],
            AvailableConfiguration(adapter)));

        Assert.Equal("activation.profile.target-signal-missing", exception.ErrorCode);
    }

    [Fact]
    public void RollbackSnapshotRoundTripsNativeDataAndProperties()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MoniTopo.Windows.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "rollback.bin");
        try
        {
            var adapter = new NativeAdapterId(3, -1);
            var raw = AvailableConfiguration(adapter);
            var state = new RollbackDisplayState(
                raw,
                [new RollbackDisplayProperty(adapter, 4, adapter, 5, 150, true)]);

            DisplayRollbackSnapshotSerializer.Write(path, state);
            var restored = DisplayRollbackSnapshotSerializer.Read(path);

            Assert.Equal(raw.Paths.Length, restored.CoreConfiguration.Paths.Length);
            Assert.Equal(raw.Modes.Length, restored.CoreConfiguration.Modes.Length);
            Assert.Equal(150, Assert.Single(restored.Properties).WindowsScalePercent);
            Assert.True(restored.Properties[0].HdrEnabled);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecoveryCoordinatorWaitsForCompanionReadiness()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MoniTopo.Windows.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var rollbackPath = Path.Combine(directory, "rollback.bin");
        await File.WriteAllBytesAsync(rollbackPath, [1, 2, 3]);
        try
        {
            var launcher = new ReadyRecoveryLauncher();
            var coordinator = new ProcessRecoveryCoordinator(
                typeof(DisplayActivationInteropTests).Assembly.Location,
                TimeSpan.FromSeconds(10),
                launcher,
                TimeProvider.System);
            var snapshot = new ActivationRollbackSnapshot(Guid.NewGuid(), 1, rollbackPath);

            await using var session = await coordinator.StartAsync(snapshot, CancellationToken.None);
            await session.SignalSuccessAsync(CancellationToken.None);

            Assert.True(launcher.Started);
            var payload = JsonSerializer.Deserialize<RecoveryPayload>(
                await File.ReadAllTextAsync(Path.Combine(directory, "payload.json")),
                SerializerOptions);
            Assert.Equal(snapshot.TransactionId, payload?.TransactionId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DisplayMutationAuthorization Authorization()
    {
        Assert.True(DisplayMutationAuthorization.TryCreate(true, _ => "1", out var authorization));
        return authorization;
    }

    private static NativeDisplaySnapshot AvailableConfiguration(NativeAdapterId adapter)
    {
        var paths = new[]
        {
            AvailablePath(adapter, 0, 0),
            AvailablePath(adapter, 1, 1),
        };
        return new NativeDisplaySnapshot(paths, []);
    }

    private static NativePathInfo AvailablePath(NativeAdapterId adapter, uint sourceId, uint targetId) => new()
    {
        SourceInfo = new NativePathSourceInfo { AdapterId = adapter, Id = sourceId },
        TargetInfo = new NativePathTargetInfo
        {
            AdapterId = adapter,
            Id = targetId,
            OutputTechnology = 10,
            TargetAvailable = 1,
        },
    };

    private static DesiredDisplayPath Display(
        string displayId,
        string sourceGroup,
        bool primary,
        DisplayPoint position,
        uint connector) => new(
            displayId,
            Identity(connector),
            sourceGroup,
            null,
            position,
            new DisplaySize(2560, 1440),
            new RefreshRate(60, 1),
            DisplayOrientation.Landscape,
            DisplayPathScaling.Identity,
            100,
            false,
            primary,
            $"Synthetic {displayId}")
        {
            TargetSignal = new DisplayTargetSignal(
                241_500_000,
                new RefreshRate(88_787, 1),
                new RefreshRate(60, 1),
                new DisplaySize(2560, 1440),
                new DisplaySize(2720, 1481),
                0,
                1),
        };

    private static MonitorIdentityFingerprint Identity(uint connector) => new(
        $"synthetic://target/{connector}",
        $"SYNTHETIC\\TARGET{connector}",
        null,
        $"SERIAL-{connector}",
        "TST",
        checked((int)connector),
        "Synthetic panel",
        600,
        340,
        DisplayOutputTechnology.DisplayPort,
        connector,
        new DisplaySize(2560, 1440),
        "synthetic");

    private static DisplayProfile Profile(IReadOnlyList<DesiredDisplayPath> displays) => new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        "Synthetic",
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        null,
        displays,
        displays.Single(display => display.IsPrimary).DisplayId,
        2,
        []);

    private static string TargetKey(NativeAdapterId adapter, uint targetId) => $"{adapter.StableKey}:T{targetId}";

    private sealed class FakeMutationApi : IDisplayConfigMutationApi
    {
        internal List<uint> CoreFlags { get; } = [];

        internal (int Type, int RelativeScale) DpiPacket { get; private set; }

        internal (int Type, uint Value) ColorPacket { get; private set; }

        public int SetDisplayConfig(NativePathInfo[] paths, NativeModeInfo[] modes, uint flags)
        {
            CoreFlags.Add(flags);
            return 0;
        }

        public int SetDpiScale(ref NativeDpiScaleSet packet)
        {
            DpiPacket = (packet.Header.Type, packet.RelativeScale);
            return 0;
        }

        public int SetAdvancedColorState(ref NativeAdvancedColorStateSet packet)
        {
            ColorPacket = (packet.Header.Type, packet.Value);
            return 0;
        }
    }

    private sealed class ReadyRecoveryLauncher : IRecoveryProcessLauncher
    {
        internal bool Started { get; private set; }

        public Process Start(string executablePath, string transactionDirectory)
        {
            Started = true;
            var payload = JsonSerializer.Deserialize<RecoveryPayload>(
                File.ReadAllText(Path.Combine(transactionDirectory, "payload.json")),
                SerializerOptions) ?? throw new InvalidDataException();
            using var readyEvent = EventWaitHandle.OpenExisting(payload.ReadyEventName);
            readyEvent.Set();
            return Process.GetCurrentProcess();
        }
    }
}
