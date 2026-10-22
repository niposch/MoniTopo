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
        var query = new FakeDisplayNative { QueryHandler = _ => api.DpiSources.Count == 0 ? initial : changed };
        var capture = new SnapshotCapture(() => api.DpiSources.Count == 0 ? initial : changed);
        var backend = Backend(capture, query, api);
        var profile = Profile(initial);

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
        current = changed;

        await backend.PersistCoreConfigurationAsync(CancellationToken.None);

        Assert.Equal(new uint[] { 0, 7 }, api.AppliedSources);
        Assert.Equal(1, api.ValidationCount);
        Assert.Equal(1, api.PersistCount);
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
        public int SetDisplayConfig(NativePathInfo[] paths, NativeModeInfo[] modes, uint flags)
        {
            Assert.Equal(0u, flags & 0x400u);
            if ((flags & SetDisplayConfigFlags.TopologyExtend) != 0) FallbackCount++;
            if ((flags & SetDisplayConfigFlags.Validate) != 0 && ++ValidationCount == 1 && RejectFirstValidation) return 31;
            if ((flags & SetDisplayConfigFlags.Apply) != 0) AppliedSources = paths.Select(path => path.SourceInfo.Id).ToArray();
            if ((flags & SetDisplayConfigFlags.SaveToDatabase) != 0) { BeforePersist?.Invoke(); PersistCount++; }
            return 0;
        }
        public int SetDpiScale(ref NativeDpiScaleSet packet) { DpiSources.Add(packet.Header.Id); return 0; }
        public int SetHdrState(ref NativeHdrStateSet packet) { HdrTargets.Add(packet.Header.Id); return 0; }
        public int SetAdvancedColorState(ref NativeAdvancedColorStateSet packet) => throw new InvalidOperationException("Unexpected legacy setter.");
    }
}
