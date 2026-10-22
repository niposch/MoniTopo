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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditedModeRequestsSupplySourceAndRateButNeverStaleTargetTimings(bool virtualMode)
    {
        var adapter = new NativeAdapterId(1, 0);
        var available = AvailableConfiguration(adapter);
        available = available with { Paths = available.Paths.Select(path => { path.Flags = virtualMode ? 0x8u : 0; return path; }).ToArray() };
        var display = Display("display-1", "source-a", true, new DisplayPoint(0, 0), 0) with
        {
            SourceResolution = new DisplaySize(1920, 1080),
            RefreshRate = new RefreshRate(120000, 1001),
            TargetSignal = null,
            ModeRequest = new DisplayModeRequest(1),
            DesktopImage = null,
            BoostRefreshRate = false,
        };
        var plan = DisplayConfigurationPlanBuilder.Build(Profile([display]),
            [new ResolvedIdentityBinding(display.DisplayId, TargetKey(adapter, 0), 100)], available);
        var path = Assert.Single(plan.Paths);
        Assert.Equal(uint.MaxValue, path.TargetInfo.ModeInfoIndex);
        Assert.Equal(120000u, path.TargetInfo.RefreshRate.Numerator);
        Assert.Equal(1001u, path.TargetInfo.RefreshRate.Denominator);
        Assert.Equal(1u, path.TargetInfo.ScanLineOrdering);
        var source = Assert.Single(plan.Modes);
        Assert.Equal(1u, source.InfoType);
        Assert.Equal(1920u, source.Mode.SourceMode.Width);
        Assert.Equal(1080u, source.Mode.SourceMode.Height);
    }

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
    public void CoreMutationFailureRetainsNativeErrorForLocalDiagnostics()
    {
        var api = new FakeMutationApi { CoreResult = 87 };
        var facade = new DisplayConfigMutationFacade(Authorization(), api);

        var exception = Assert.Throws<DisplayActivationException>(() => facade.Validate(
            new NativeDisplaySnapshot([new NativePathInfo()], [new NativeModeInfo()])));

        Assert.Equal(87, exception.NativeErrorCode);
        Assert.Contains("native error 87", exception.TechnicalDetails, StringComparison.Ordinal);
        Assert.Contains("paths=1; modes=1", exception.TechnicalDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("adapter", exception.TechnicalDetails, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DpiAndHdrSettersBuildExpectedPacketsThroughFakeApi()
    {
        var api = new FakeMutationApi();
        var facade = new DisplayConfigMutationFacade(Authorization(), api);
        var adapter = new NativeAdapterId(7, 2);

        facade.SetDpiScale(adapter, 3, -1);
        facade.SetHdrState(adapter, 9, enabled: true);

        Assert.Equal(UndocumentedDpiScaleContract.SetRequestType, api.DpiPacket.Type);
        Assert.Equal(-1, api.DpiPacket.RelativeScale);
        Assert.Equal(16, api.HdrPacket.Type);
        Assert.Equal(1u, api.HdrPacket.Value);
        Assert.Equal(default, api.ColorPacket);
    }

    [Fact]
    public void HdrSetterFallsBackToLegacyAdvancedColorOnOlderWindows()
    {
        var api = new FakeMutationApi { HdrResult = DisplayConfigNativeFacade.ErrorInvalidParameter };
        var facade = new DisplayConfigMutationFacade(Authorization(), api);

        facade.SetHdrState(new NativeAdapterId(7, 2), 9, enabled: false);

        Assert.Equal(16, api.HdrPacket.Type);
        Assert.Equal(10, api.ColorPacket.Type);
        Assert.Equal(0u, api.ColorPacket.Value);
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
        Assert.Collection(
            plan.Paths,
            path => AssertVirtualPath(path, sourceModeIndex: 0, targetModeIndex: 2),
            path => AssertVirtualPath(path, sourceModeIndex: 1, targetModeIndex: 3));
    }

    [Fact]
    public void PlanUsesDirectIndexesWhenPathDoesNotAdvertiseVirtualModeSupport()
    {
        var adapter = new NativeAdapterId(1, 0);
        var first = Display("display-1", "source-a", true, new DisplayPoint(0, 0), 0);
        var second = Display("display-2", "source-b", false, new DisplayPoint(2560, 0), 1);
        var profile = Profile([first, second]);
        var bindings = new[]
        {
            new ResolvedIdentityBinding(first.DisplayId, TargetKey(adapter, 0), 100),
            new ResolvedIdentityBinding(second.DisplayId, TargetKey(adapter, 1), 100),
        };

        var plan = DisplayConfigurationPlanBuilder.Build(
            profile,
            bindings,
            AvailableConfiguration(adapter, supportsVirtualMode: false));

        Assert.Collection(
            plan.Paths,
            path => AssertDirectPath(path, sourceModeIndex: 0, targetModeIndex: 2),
            path => AssertDirectPath(path, sourceModeIndex: 1, targetModeIndex: 3));
    }

    [Fact]
    public void PlanPrefersTheSourceAddressCapturedWithEachSourceGroup()
    {
        var adapter = new NativeAdapterId(1, 0);
        var first = Display("display-1", $"{adapter.StableKey}:S1", true, new DisplayPoint(0, 0), 0);
        var second = Display("display-2", $"{adapter.StableKey}:S0", false, new DisplayPoint(2560, 0), 1);
        var available = new NativeDisplaySnapshot(
            [
                AvailablePath(adapter, 0, 0, supportsVirtualMode: false, boostRefreshRate: false),
                AvailablePath(adapter, 1, 0, supportsVirtualMode: false, boostRefreshRate: false),
                AvailablePath(adapter, 0, 1, supportsVirtualMode: false, boostRefreshRate: false),
                AvailablePath(adapter, 1, 1, supportsVirtualMode: false, boostRefreshRate: false),
            ],
            []);

        var plan = DisplayConfigurationPlanBuilder.Build(
            Profile([first, second]),
            [
                new ResolvedIdentityBinding(first.DisplayId, TargetKey(adapter, 0), 100),
                new ResolvedIdentityBinding(second.DisplayId, TargetKey(adapter, 1), 100),
            ],
            available);

        Assert.Collection(
            plan.Paths,
            path => Assert.Equal(1u, path.SourceInfo.Id),
            path => Assert.Equal(0u, path.SourceInfo.Id));
    }

    [Fact]
    public async Task ValidationRetriesPackedIndexesWhenDriverOmitsTheVirtualModeFlag()
    {
        var adapter = new NativeAdapterId(1, 0);
        var display = Display("display-1", $"{adapter.StableKey}:S0", true, new DisplayPoint(0, 0), 0);
        var native = new FakeDisplayNative
        {
            Snapshot = AvailableConfiguration(adapter, supportsVirtualMode: false),
        };
        var mutationApi = new FakeMutationApi();
        mutationApi.CoreResults.Enqueue(31);
        mutationApi.CoreResults.Enqueue(0);
        var backend = new GuardedDisplayActivationBackend(
            new UnusedCaptureService(),
            native,
            new DisplayConfigMutationFacade(Authorization(), mutationApi),
            new SupportedSessionGuard(),
            Path.Combine(Path.GetTempPath(), "MoniTopo.Windows.Tests", Guid.NewGuid().ToString("N")),
            TimeProvider.System);
        var profile = Profile([display]);
        var bindings = new[] { new ResolvedIdentityBinding(display.DisplayId, TargetKey(adapter, 0), 100) };

        await backend.PreflightAsync(profile, bindings, CancellationToken.None);
        await backend.ValidateCoreConfigurationAsync(profile, bindings, CancellationToken.None);
        await backend.ApplyCoreTemporaryAsync(profile, bindings, CancellationToken.None);

        Assert.Equal(3, mutationApi.CorePlans.Count);
        Assert.Equal(0u, mutationApi.CorePlans[0].Paths[0].SourceInfo.ModeInfoIndex);
        Assert.Equal(1u, mutationApi.CorePlans[0].Paths[0].TargetInfo.ModeInfoIndex);
        Assert.Equal(0x0000FFFFu, mutationApi.CorePlans[1].Paths[0].SourceInfo.ModeInfoIndex);
        Assert.Equal(0x0001FFFFu, mutationApi.CorePlans[1].Paths[0].TargetInfo.ModeInfoIndex);
        Assert.Equal(0x0000FFFFu, mutationApi.CorePlans[2].Paths[0].SourceInfo.ModeInfoIndex);
        Assert.Equal(0x0001FFFFu, mutationApi.CorePlans[2].Paths[0].TargetInfo.ModeInfoIndex);
    }

    [Fact]
    public void PlanRepresentsClonesBySharingSourceModeAndPreservesRefreshBoost()
    {
        var adapter = new NativeAdapterId(1, 0);
        var first = Display("display-1", "source-a", true, new DisplayPoint(0, 0), 0) with
        {
            CloneGroupId = "clone-a",
        };
        var second = Display("display-2", "source-a", false, new DisplayPoint(0, 0), 1) with
        {
            CloneGroupId = "clone-a",
        };
        var available = AvailableConfiguration(adapter, supportsVirtualMode: true, boostRefreshRate: true);
        available.Paths[1].SourceInfo = available.Paths[0].SourceInfo;
        var profile = Profile([first, second]);

        var plan = DisplayConfigurationPlanBuilder.Build(
            profile,
            [
                new ResolvedIdentityBinding(first.DisplayId, TargetKey(adapter, 0), 100),
                new ResolvedIdentityBinding(second.DisplayId, TargetKey(adapter, 1), 100),
            ],
            available);

        Assert.Single(plan.Modes, mode => mode.InfoType == 1);
        Assert.All(plan.Paths, path =>
        {
            Assert.Equal(0u, path.SourceInfo.SourceModeInfoIndex);
            Assert.Equal(0xFFFFu, path.SourceInfo.CloneGroupId);
            Assert.Equal(0x19u, path.Flags);
        });
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
    public void PlanRejectsProfilesCapturedBeforeHdrSemanticsWereCorrected()
    {
        var adapter = new NativeAdapterId(1, 0);
        var display = Display("display-1", "source-a", true, new DisplayPoint(0, 0), 0);
        var profile = Profile([display]) with { CaptureSchemaVersion = 2 };

        var exception = Assert.Throws<ActivationFailureException>(() => DisplayConfigurationPlanBuilder.Build(
            profile,
            [new ResolvedIdentityBinding(display.DisplayId, TargetKey(adapter, 0), 100)],
            AvailableConfiguration(adapter)));

        Assert.Equal("activation.profile.capture-outdated", exception.ErrorCode);
        Assert.Contains("Update the profile", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreflightDefersScaleAndHdrQueriesForDisplayThatIsCurrentlyInactive()
    {
        var adapter = new NativeAdapterId(1, 0);
        var display = Display("display-2", "source-b", true, new DisplayPoint(0, 0), 1);
        var native = new FakeDisplayNative
        {
            Snapshot = AvailableConfiguration(adapter),
            DpiErrorCode = DisplayConfigNativeFacade.ErrorNotSupported,
        };
        var backend = new GuardedDisplayActivationBackend(
            new UnusedCaptureService(),
            native,
            new DisplayConfigMutationFacade(Authorization(), new FakeMutationApi()),
            new SupportedSessionGuard(),
            Path.Combine(Path.GetTempPath(), "MoniTopo.Windows.Tests", Guid.NewGuid().ToString("N")),
            TimeProvider.System);

        await backend.PreflightAsync(
            Profile([display]),
            [new ResolvedIdentityBinding(display.DisplayId, TargetKey(adapter, 1), 100)],
            CancellationToken.None);

        Assert.Equal(0, native.DpiQueryCount);
        Assert.Equal(0, native.HdrQueryCount);
    }

    [Fact]
    public async Task PreflightStillValidatesHdrForDisplayThatIsCurrentlyActive()
    {
        var adapter = new NativeAdapterId(1, 0);
        var display = Display("display-1", "source-a", true, new DisplayPoint(0, 0), 0) with
        {
            HdrEnabled = true,
        };
        var snapshot = AvailableConfiguration(adapter);
        snapshot.Paths[0].Flags |= 0x1;
        var native = new FakeDisplayNative
        {
            Snapshot = snapshot,
            DpiPacket = new NativeDpiScaleGet
            {
                MinimumRelativeScale = 0,
                CurrentRelativeScale = 0,
                MaximumRelativeScale = 0,
            },
            HdrState = new NativeHdrState(IsSupported: false, IsEnabled: false, IsLimitedByPolicy: false),
        };
        var backend = new GuardedDisplayActivationBackend(
            new UnusedCaptureService(),
            native,
            new DisplayConfigMutationFacade(Authorization(), new FakeMutationApi()),
            new SupportedSessionGuard(),
            Path.Combine(Path.GetTempPath(), "MoniTopo.Windows.Tests", Guid.NewGuid().ToString("N")),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<ActivationFailureException>(() => backend.PreflightAsync(
            Profile([display]),
            [new ResolvedIdentityBinding(display.DisplayId, TargetKey(adapter, 0), 100)],
            CancellationToken.None));

        Assert.Equal("activation.hdr.unsupported", exception.ErrorCode);
        Assert.Equal(1, native.HdrQueryCount);
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

    private static NativeDisplaySnapshot AvailableConfiguration(
        NativeAdapterId adapter,
        bool supportsVirtualMode = true,
        bool boostRefreshRate = false)
    {
        var paths = new[]
        {
            AvailablePath(adapter, 0, 0, supportsVirtualMode, boostRefreshRate),
            AvailablePath(adapter, 1, 1, supportsVirtualMode, boostRefreshRate),
        };
        return new NativeDisplaySnapshot(paths, []);
    }

    private static NativePathInfo AvailablePath(
        NativeAdapterId adapter,
        uint sourceId,
        uint targetId,
        bool supportsVirtualMode,
        bool boostRefreshRate) => new()
        {
            SourceInfo = new NativePathSourceInfo { AdapterId = adapter, Id = sourceId },
            TargetInfo = new NativePathTargetInfo
            {
                AdapterId = adapter,
                Id = targetId,
                OutputTechnology = 10,
                TargetAvailable = 1,
            },
            Flags = (supportsVirtualMode ? 0x8u : 0u) | (boostRefreshRate ? 0x10u : 0u),
        };

    private static void AssertVirtualPath(
        NativePathInfo path,
        uint sourceModeIndex,
        uint targetModeIndex,
        uint expectedFlags = 0x9)
    {
        Assert.Equal(expectedFlags, path.Flags);
        Assert.Equal(0xFFFFu, path.SourceInfo.CloneGroupId);
        Assert.Equal(sourceModeIndex, path.SourceInfo.SourceModeInfoIndex);
        Assert.Equal(0xFFFFu, path.TargetInfo.DesktopModeInfoIndex);
        Assert.Equal(targetModeIndex, path.TargetInfo.TargetModeInfoIndex);
    }

    private static void AssertDirectPath(
        NativePathInfo path,
        uint sourceModeIndex,
        uint targetModeIndex)
    {
        Assert.Equal(0x1u, path.Flags);
        Assert.Equal(sourceModeIndex, path.SourceInfo.ModeInfoIndex);
        Assert.Equal(targetModeIndex, path.TargetInfo.ModeInfoIndex);
    }

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
        DisplayProfileCaptureSchema.Current,
        []);

    private static string TargetKey(NativeAdapterId adapter, uint targetId) => $"{adapter.StableKey}:T{targetId}";

    private sealed class FakeMutationApi : IDisplayConfigMutationApi
    {
        internal List<uint> CoreFlags { get; } = [];

        internal List<NativeDisplaySnapshot> CorePlans { get; } = [];

        internal Queue<int> CoreResults { get; } = [];

        internal (int Type, int RelativeScale) DpiPacket { get; private set; }

        internal (int Type, uint Value) ColorPacket { get; private set; }

        internal (int Type, uint Value) HdrPacket { get; private set; }

        internal int HdrResult { get; init; }

        internal int CoreResult { get; init; }

        public int SetDisplayConfig(NativePathInfo[] paths, NativeModeInfo[] modes, uint flags)
        {
            CoreFlags.Add(flags);
            CorePlans.Add(new NativeDisplaySnapshot([.. paths], [.. modes]));
            return CoreResults.TryDequeue(out var result) ? result : CoreResult;
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

        public int SetHdrState(ref NativeHdrStateSet packet)
        {
            HdrPacket = (packet.Header.Type, packet.Value);
            return HdrResult;
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
            _ = Task.Run(() =>
            {
                using var completed = EventWaitHandle.OpenExisting(payload.SuccessEventName);
                Assert.True(completed.WaitOne(TimeSpan.FromSeconds(5)));
                using var acknowledged = EventWaitHandle.OpenExisting(payload.AcknowledgedEventName);
                acknowledged.Set();
            });
            return Process.GetCurrentProcess();
        }
    }

    private sealed class UnusedCaptureService : IDisplayCaptureService
    {
        public Task<CapturedDisplaySnapshot> CaptureAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }

    private sealed class SupportedSessionGuard : IInteractiveDisplaySessionGuard
    {
        public bool IsSupported => true;
    }
}
