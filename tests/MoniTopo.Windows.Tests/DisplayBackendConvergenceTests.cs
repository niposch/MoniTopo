using MoniTopo.Core.Identity;
using MoniTopo.Core.Models;
using MoniTopo.Core.Recovery;
using MoniTopo.Windows.Display;

namespace MoniTopo.Windows.Tests;

public sealed class DisplayBackendConvergenceTests
{
    [Fact]
    public async Task EachScalingWriteResolvesTheCurrentPhysicalPanelAndSourceAgain()
    {
        var initial = ActiveSnapshot();
        var changed = ReassignSecondSource(initial);
        var api = new RecordingMutation();
        var query = new FakeDisplayNative
        {
            QueryHandler = _ => api.DpiSources.Count == 0 ? initial : changed,
            DpiPacket = new NativeDpiScaleGet { MaximumRelativeScale = 1 },
        };
        var capture = new SnapshotCapture(() => api.DpiSources.Count == 0 ? initial : changed);
        var backend = Backend(capture, query, api);
        var profile = Profile(initial) with { Displays = Profile(initial).Displays.Select(path => path with { WindowsUiScalePercent = 125 }).ToArray() };

        await backend.ApplyScalingAsync(profile, [], CancellationToken.None);

        Assert.Equal(new uint[] { 0, 7 }, api.DpiSources);
        Assert.Equal(2, capture.Calls);
    }

    [Fact]
    public async Task EachHdrWriteResolvesChangedTargetAddressesInsteadOfReusingPhaseBindings()
    {
        var initial = ActiveSnapshot();
        var changed = new NativeDisplaySnapshot(initial.Paths.ToArray(), initial.Modes.ToArray());
        changed.Paths[1].TargetInfo.Id = 9;
        changed.Modes[3].Id = 9;
        var api = new RecordingMutation();
        var query = new FakeDisplayNative
        {
            QueryHandler = _ => api.HdrTargets.Count == 0 ? initial : changed,
            HdrState = new NativeHdrState(true, false, false),
        };
        var capture = new SnapshotCapture(() => api.HdrTargets.Count == 0 ? initial : changed);
        var backend = Backend(capture, query, api);
        var profile = Profile(initial) with { Displays = Profile(initial).Displays.Select(path => path with { HdrEnabled = true }).ToArray() };

        await backend.ApplyHdrAsync(profile, [], CancellationToken.None);

        Assert.Equal(new uint[] { 0, 9 }, api.HdrTargets);
        Assert.Equal(2, capture.Calls);
    }

    [Fact]
    public async Task RollbackRequeriesSourcesAndVerifiesBeforePersistence()
    {
        var initial = ActiveSnapshot();
        var changed = ReassignSecondSource(initial);
        var api = new RecordingMutation();
        var query = new FakeDisplayNative
        {
            QueryHandler = _ => api.DpiSources.Count == 0 ? initial : changed,
            HdrState = new NativeHdrState(true, false, false),
            DpiHandler = source => (0, new NativeDpiScaleGet
            { CurrentRelativeScale = api.DpiSources.Contains(source) ? 0 : 1, MaximumRelativeScale = 1 }),
        };
        api.BeforePersist = () =>
        {
            Assert.Equal(new uint[] { 0, 7 }, api.DpiSources);
            Assert.True(query.HdrQueryCount >= 6, "Persistence must follow consecutive complete property verification.");
        };
        await InRollback(initial, async path =>
        {
            var restored = await GuardedDisplayActivationBackend.RestoreSnapshotAsync(path, query, Facade(api), CancellationToken.None);
            Assert.True(restored);
            Assert.Equal(1, api.PersistCount);
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RollbackRepairsHdrModeResetAndPersistenceScaleReset(bool resetCoreOnHdr, bool resetScaleOnPersist)
    {
        var original = ActiveSnapshot();
        var wrong = new NativeDisplaySnapshot(original.Paths.ToArray(), original.Modes.ToArray());
        wrong.Modes[0].Mode.SourceMode.Width--;
        var current = wrong;
        var hdr = true;
        var scales = new Dictionary<uint, int> { [0] = 1, [1] = 0 };
        var api = new RecordingMutation
        {
            OnTemporaryApply = snapshot => current = snapshot,
            OnHdr = (_, enabled) => { hdr = enabled; if (resetCoreOnHdr) current = wrong; },
            OnDpi = (source, scale) => scales[source] = scale,
            BeforePersist = () =>
            {
                Assert.False(hdr);
                Assert.Equal(0, scales[0]);
                Assert.Equal(2560u, current.Modes[0].Mode.SourceMode.Width);
                if (resetScaleOnPersist) scales[0] = 1;
            },
        };
        var query = new FakeDisplayNative
        {
            QueryHandler = _ => current,
            HdrHandler = (_, target) => new NativeHdrState(true, target == 0 && hdr, false),
            DpiHandler = source => (0, new NativeDpiScaleGet { CurrentRelativeScale = scales[source], MaximumRelativeScale = 1 }),
        };
        await InRollback(original, async path =>
        {
            Assert.True(await GuardedDisplayActivationBackend.RestoreSnapshotAsync(path, query, Facade(api), CancellationToken.None));
            Assert.Equal(resetCoreOnHdr ? 2 : 1, api.TemporaryApplyCount);
            Assert.Equal(1, api.PersistCount);
            Assert.Equal(new uint[] { 0 }, api.HdrTargets);
            Assert.Equal(resetScaleOnPersist ? 2 : 1, api.DpiSources.Count);
            Assert.Equal(0, scales[0]);
            Assert.False(hdr);
            Assert.Equal(0, api.FallbackCount);
        });
    }

    [Fact]
    public async Task RollbackHdrCaptureFailureStillInvokesAndVerifiesEmergencyFallback()
    {
        var initial = ActiveSnapshot();
        var api = new RecordingMutation();
        var reads = 0;
        var query = new FakeDisplayNative
        {
            Snapshot = initial,
            HdrHandler = (_, _) => ++reads == 1
                ? throw new DisplayCaptureException(5, "Synthetic HDR read failure.")
                : new NativeHdrState(true, false, false),
        };
        await InRollback(initial, async path =>
        {
            var result = await new DisplayRollbackExecutor(query, Facade(api)).RecoverAsync(path, CancellationToken.None);
            Assert.False(result.Restored);
            Assert.True(result.EmergencyFallbackUsed);
            Assert.Equal(1, api.FallbackCount);
            Assert.Equal(0, api.PersistCount);
        });
    }

    [Fact]
    public async Task RollbackPollsPendingScaleInsteadOfRepeatingItsSetter()
    {
        var raw = ActiveSnapshot();
        var api = new RecordingMutation();
        var remainingLag = 2;
        var query = new FakeDisplayNative
        {
            Snapshot = raw,
            HdrState = new NativeHdrState(true, false, false),
            DpiHandler = source => (0, new NativeDpiScaleGet
            {
                CurrentRelativeScale = source == 0 && (api.DpiSources.Count == 0 || remainingLag-- > 0) ? 1 : 0,
                MaximumRelativeScale = 1,
            }),
        };
        await InRollback(raw, async path =>
        {
            Assert.True(await GuardedDisplayActivationBackend.RestoreSnapshotAsync(path, query, Facade(api), CancellationToken.None));
            Assert.Equal(new uint[] { 0 }, api.DpiSources);
            Assert.Equal(1, api.PersistCount);
        });
    }

    [Fact]
    public async Task ValidationTriesAlternativeSourceRoutesWithoutAllowingModeSubstitution()
    {
        var initial = ActiveSnapshot();
        for (var index = 0; index < initial.Paths.Length; index++) initial.Paths[index].Flags |= NativeDisplayConfigPathFlags.SupportVirtualMode;
        var paths = initial.Paths.Concat(initial.Paths.Select(path =>
        {
            path.SourceInfo.Id = 1 - path.SourceInfo.Id;
            return path;
        })).ToArray();
        var api = new RecordingMutation { RejectFirstValidation = true };
        var backend = Backend(new SnapshotCapture(() => initial), new FakeDisplayNative
        {
            Snapshot = new NativeDisplaySnapshot(paths, initial.Modes),
            HdrState = new NativeHdrState(true, false, false),
        }, api);
        var profile = Profile(initial);
        var bindings = initial.Paths.Select((path, index) => new ResolvedIdentityBinding(
            profile.Displays[index].DisplayId, TargetKey(path), 100)).ToArray();

        await backend.PreflightAsync(profile, bindings, CancellationToken.None);
        await backend.ValidateCoreConfigurationAsync(profile, bindings, CancellationToken.None);
        await backend.ApplyCoreTemporaryAsync(profile, bindings, CancellationToken.None);

        Assert.Equal(2, api.ValidationCount);
        Assert.Equal(new uint[] { 1, 0 }, api.AppliedSources);
    }

    private static GuardedDisplayActivationBackend Backend(IDisplayCaptureService capture,
        IDisplayConfigNativeFacade query, RecordingMutation api) => new(capture, query, Facade(api),
            new SupportedSession(), Path.Combine(Path.GetTempPath(), "MoniTopo.Synthetic", Guid.NewGuid().ToString("N")), TimeProvider.System);

    [Fact]
    public async Task PersistenceUsesVerifiedCurrentRoutingRatherThanThePreTransitionPlan()
    {
        var initial = ActiveSnapshot();
        var changed = ReassignSecondSource(initial);
        var current = initial;
        var api = new RecordingMutation();
        var backend = Backend(new SnapshotCapture(() => current), new FakeDisplayNative
        {
            QueryHandler = _ => current,
            HdrState = new NativeHdrState(true, false, false),
        }, api);
        var profile = Profile(initial);
        var bindings = initial.Paths.Select((path, index) => new ResolvedIdentityBinding(profile.Displays[index].DisplayId, TargetKey(path), 100)).ToArray();
        await backend.PreflightAsync(profile, bindings, CancellationToken.None);
        await backend.ValidateCoreConfigurationAsync(profile, bindings, CancellationToken.None);
        current = changed;

        await backend.PersistCoreConfigurationAsync(CancellationToken.None);

        Assert.Equal(new uint[] { 0, 7 }, api.AppliedSources);
        Assert.Equal(2, api.ValidationCount);
        Assert.Equal(1, api.PersistCount);
    }

    [Fact]
    public async Task UnchangedScaleDoesNotInvokeAnySetter()
    {
        var raw = ActiveSnapshot();
        var api = new RecordingMutation();
        var backend = Backend(new SnapshotCapture(() => raw), new FakeDisplayNative { Snapshot = raw }, api);
        await backend.ApplyScalingAsync(Profile(raw), [], CancellationToken.None);
        Assert.Empty(api.DpiSources);
        Assert.Empty(api.AppliedSources);
        Assert.Empty(api.HdrTargets);
    }

    [Fact]
    public async Task MixedScaleChangesWriteOnlyTheSourceWhoseLiveValueDiffers()
    {
        var raw = ActiveSnapshot();
        var api = new RecordingMutation();
        var native = new FakeDisplayNative
        {
            Snapshot = raw,
            DpiHandler = source => (0, new NativeDpiScaleGet { CurrentRelativeScale = source == 0 ? 0 : 1, MaximumRelativeScale = 2 }),
        };
        var backend = Backend(new SnapshotCapture(() => raw), native, api);
        var profile = Profile(raw) with { Displays = Profile(raw).Displays.Select(path => path with { WindowsUiScalePercent = 125 }).ToArray() };
        await backend.ApplyScalingAsync(profile, [], CancellationToken.None);
        Assert.Equal(new uint[] { 0 }, api.DpiSources);
        Assert.Empty(api.AppliedSources);
    }

    [Fact]
    public async Task SharedCloneSourceReceivesOnlyOneScaleWrite()
    {
        var raw = ActiveSnapshot();
        raw.Paths[1].SourceInfo = raw.Paths[0].SourceInfo;
        var api = new RecordingMutation();
        var native = new FakeDisplayNative { Snapshot = raw, DpiPacket = new NativeDpiScaleGet { MaximumRelativeScale = 2 } };
        var backend = Backend(new SnapshotCapture(() => raw), native, api);
        var profile = Profile(raw) with
        {
            Displays = Profile(raw).Displays.Select(path => path with
            { WindowsUiScalePercent = 125, Position = new DisplayPoint(0, 0), SourceGroupId = "shared" }).ToArray()
        };
        await backend.ApplyScalingAsync(profile, [], CancellationToken.None);
        Assert.Equal(new uint[] { 0 }, api.DpiSources);
    }

    [Fact]
    public async Task MixedHdrChangesWriteOnlyTheTargetWhoseLiveValueDiffers()
    {
        var raw = ActiveSnapshot();
        var api = new RecordingMutation();
        var native = new FakeDisplayNative { Snapshot = raw, HdrHandler = (_, target) => new NativeHdrState(true, target == 0, false) };
        var backend = Backend(new SnapshotCapture(() => raw), native, api);
        await backend.ApplyHdrAsync(Profile(raw), [], CancellationToken.None);
        Assert.Equal(new uint[] { 0 }, api.HdrTargets);
        Assert.Empty(api.DpiSources);
        Assert.Empty(api.AppliedSources);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task PropertyOnlyPreflightStillRejectsOldOrIncompleteCaptures(int schema)
    {
        var raw = ActiveSnapshot();
        var profile = Profile(raw) with
        { CaptureSchemaVersion = schema, Displays = Profile(raw).Displays.Select(path => path with { TargetSignal = null }).ToArray() };
        var api = new RecordingMutation();
        var backend = Backend(new SnapshotCapture(() => raw), new FakeDisplayNative { Snapshot = raw }, api);
        var error = await Assert.ThrowsAsync<MoniTopo.Core.Activation.ActivationFailureException>(() =>
            backend.PreflightAsync(profile, [], CancellationToken.None));
        Assert.Equal(schema == 1 ? "activation.profile.capture-outdated" : "activation.profile.target-signal-missing", error.ErrorCode);
        Assert.Empty(api.AppliedSources);
        Assert.Empty(api.DpiSources);
        Assert.Empty(api.HdrTargets);
    }

    private static DisplayConfigMutationFacade Facade(RecordingMutation api)
    {
        Assert.True(DisplayMutationAuthorization.TryCreate(true, _ => "1", out var authorization));
        return new DisplayConfigMutationFacade(authorization, api);
    }

    private static NativeDisplaySnapshot ActiveSnapshot()
    {
        var raw = FakeDisplayNative.WithExtendedDesktop().Snapshot;
        return new NativeDisplaySnapshot(raw.Paths.Where(path => (path.Flags & 1) != 0).ToArray(), raw.Modes);
    }

    private static NativeDisplaySnapshot ReassignSecondSource(NativeDisplaySnapshot raw)
    {
        var changed = new NativeDisplaySnapshot(raw.Paths.ToArray(), raw.Modes.ToArray());
        changed.Paths[1].SourceInfo.Id = 7;
        changed.Modes[1].Id = 7;
        return changed;
    }

    private static DisplayProfile Profile(NativeDisplaySnapshot raw) => new(Guid.NewGuid(), "Synthetic",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, Capture(raw).ActivePaths, "display-0", 3, []);

    private static CapturedDisplaySnapshot Capture(NativeDisplaySnapshot raw)
    {
        var paths = raw.Paths.Select((path, index) => new DesiredDisplayPath($"display-{index}",
            new MonitorIdentityFingerprint($"synthetic://physical/{index}", null, null, $"SYNTHETIC-{index}",
                "TST", index + 1, "Synthetic panel", null, null, DisplayOutputTechnology.DisplayPort, (uint)index, null, null),
            $"{path.SourceInfo.AdapterId.StableKey}:S{path.SourceInfo.Id}", null,
            new DisplayPoint(index * 2560, 0), new DisplaySize(2560, 1440), new RefreshRate(60, 1),
            DisplayOrientation.Landscape, DisplayPathScaling.Identity, 100, false, index == 0, "Synthetic panel")
        {
            TargetSignal = new DisplayTargetSignal(241_500_000, new RefreshRate(88_787, 1), new RefreshRate(60, 1),
                new DisplaySize(2560, 1440), new DisplaySize(2720, 1481), 0, 1),
        }).ToArray();
        return new CapturedDisplaySnapshot(paths, raw.Paths.Select((path, index) =>
            new ConnectedDisplayState(TargetKey(path), paths[index].Identity, true, "Synthetic panel", paths[index])).ToArray(),
            paths[0].DisplayId, DateTimeOffset.UnixEpoch);
    }

    private static string TargetKey(NativePathInfo path) => $"{path.TargetInfo.AdapterId.StableKey}:T{path.TargetInfo.Id}";

    private static async Task InRollback(NativeDisplaySnapshot raw, Func<string, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MoniTopo.SyntheticRollback", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "rollback.bin");
        try
        {
            DisplayRollbackSnapshotSerializer.Write(path, new RollbackDisplayState(raw, raw.Paths.Select(item =>
                new RollbackDisplayProperty(item.SourceInfo.AdapterId, item.SourceInfo.Id,
                    item.TargetInfo.AdapterId, item.TargetInfo.Id, 100, false)).ToArray()));
            await test(path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class SnapshotCapture(Func<NativeDisplaySnapshot> snapshot) : IDisplayCaptureService
    {
        internal int Calls { get; private set; }
        public Task<CapturedDisplaySnapshot> CaptureAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Capture(snapshot()));
        }
    }

    private sealed class SupportedSession : IInteractiveDisplaySessionGuard { public bool IsSupported => true; }

    private sealed class RecordingMutation : IDisplayConfigMutationApi
    {
        internal List<uint> DpiSources { get; } = [];
        internal List<uint> HdrTargets { get; } = [];
        internal uint[] AppliedSources { get; private set; } = [];
        internal int PersistCount { get; private set; }
        internal int FallbackCount { get; private set; }
        internal int ValidationCount { get; private set; }
        internal bool RejectFirstValidation { get; init; }
        internal Action? BeforePersist { get; set; }
        internal Action<NativeDisplaySnapshot>? OnTemporaryApply { get; init; }
        internal Action<uint, int>? OnDpi { get; init; }
        internal Action<uint, bool>? OnHdr { get; init; }
        internal int TemporaryApplyCount { get; private set; }
        public int SetDisplayConfig(NativePathInfo[] paths, NativeModeInfo[] modes, uint flags)
        {
            Assert.Equal(0u, flags & 0x400u);
            if ((flags & SetDisplayConfigFlags.TopologyExtend) != 0) FallbackCount++;
            if ((flags & SetDisplayConfigFlags.Validate) != 0 && ++ValidationCount == 1 && RejectFirstValidation) return 31;
            if ((flags & SetDisplayConfigFlags.Apply) != 0) AppliedSources = paths.Select(path => path.SourceInfo.Id).ToArray();
            if ((flags & SetDisplayConfigFlags.Apply) != 0 && (flags & SetDisplayConfigFlags.SaveToDatabase) == 0)
            { TemporaryApplyCount++; OnTemporaryApply?.Invoke(new NativeDisplaySnapshot(paths, modes)); }
            if ((flags & SetDisplayConfigFlags.SaveToDatabase) != 0) { BeforePersist?.Invoke(); PersistCount++; }
            return 0;
        }
        public int SetDpiScale(ref NativeDpiScaleSet packet) { DpiSources.Add(packet.Header.Id); OnDpi?.Invoke(packet.Header.Id, packet.RelativeScale); return 0; }
        public int SetHdrState(ref NativeHdrStateSet packet) { HdrTargets.Add(packet.Header.Id); OnHdr?.Invoke(packet.Header.Id, packet.Value != 0); return 0; }
        public int SetAdvancedColorState(ref NativeAdvancedColorStateSet packet) => throw new InvalidOperationException("Unexpected legacy setter.");
    }
}
