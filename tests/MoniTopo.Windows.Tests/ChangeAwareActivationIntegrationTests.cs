using MoniTopo.Core.Activation;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;
using MoniTopo.Windows.Display;

namespace MoniTopo.Windows.Tests;

public sealed class ChangeAwareActivationIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RealOrchestrationCaptureAndBackendOnlyWriteChangedNativeProperties(bool scale, bool hdr)
    {
        // Every platform interface is fake; this uses the real capture/conversion,
        // planner, orchestration and guarded mutation facade together, not a real desktop.
        var scales = new Dictionary<uint, int>();
        var colors = new Dictionary<uint, bool>();
        var native = new FakeDisplayNative
        {
            Snapshot = FakeDisplayNative.WithExtendedDesktop().Snapshot,
            DpiHandler = source => (0, new NativeDpiScaleGet
            { CurrentRelativeScale = scales.GetValueOrDefault(source), MaximumRelativeScale = 2 }),
            HdrHandler = (_, target) => new NativeHdrState(true, colors.GetValueOrDefault(target), false),
        };
        var capture = new CcdCaptureService(native, null, new SyntheticIdentityProvider(), TimeProvider.System);
        var snapshot = await capture.CaptureAsync();
        var paths = snapshot.ActivePaths.Select((path, index) => index == 0
            ? path with { WindowsUiScalePercent = scale ? 125 : 100, HdrEnabled = hdr } : path).ToArray();
        var profile = new DisplayProfile(Guid.NewGuid(), "Synthetic", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            null, paths, snapshot.PrimaryDisplayId, DisplayProfileCaptureSchema.Current, []);
        var mutations = new PropertyMutationApi(scales, colors);
        Assert.True(DisplayMutationAuthorization.TryCreate(true, _ => "1", out var authorization));
        var root = Path.Combine(Path.GetTempPath(), "MoniTopo.SyntheticChangeAware", Guid.NewGuid().ToString("N"));
        var backend = new GuardedDisplayActivationBackend(capture, native, new DisplayConfigMutationFacade(authorization, mutations),
            new SupportedSession(), root, TimeProvider.System);
        var recovery = new SyntheticRecovery(backend);
        var state = new ActivationState();
        using var service = new ProfileActivationService(backend, recovery, state,
            new MonitorIdentityResolver(), new ActiveProfileMatcher(new MonitorIdentityResolver()));
        try
        {
            var result = await service.ActivateAsync(profile);
            Assert.Equal(ActivationOutcome.Success, result.Outcome);
            Assert.Equal(scale || hdr, result.MutationBegan);
            Assert.Equal(scale ? 1 : 0, mutations.ScaleWrites);
            Assert.Equal(hdr ? 1 : 0, mutations.HdrWrites);
            Assert.Empty(mutations.CoreFlags); // Neither SDC_VALIDATE nor either SDC_APPLY.
            Assert.Equal(scale || hdr ? 1 : 0, recovery.Starts);
            Assert.Equal(profile.Id, state.LastProfile);
            Assert.True(new ActiveProfileMatcher(new MonitorIdentityResolver()).Match(profile, await capture.CaptureAsync()).IsMatch);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PropertyMutationApi(Dictionary<uint, int> scales, Dictionary<uint, bool> colors) : IDisplayConfigMutationApi
    {
        internal List<uint> CoreFlags { get; } = [];
        internal int ScaleWrites { get; private set; }
        internal int HdrWrites { get; private set; }
        public int SetDisplayConfig(NativePathInfo[] paths, NativeModeInfo[] modes, uint flags)
        {
            CoreFlags.Add(flags);
            throw new InvalidOperationException("Property-only activation must not call SetDisplayConfig.");
        }
        public int SetDpiScale(ref NativeDpiScaleSet packet)
        {
            ScaleWrites++;
            scales[packet.Header.Id] = packet.RelativeScale;
            return 0;
        }
        public int SetHdrState(ref NativeHdrStateSet packet)
        {
            HdrWrites++;
            colors[packet.Header.Id] = packet.Value != 0;
            return 0;
        }
        public int SetAdvancedColorState(ref NativeAdvancedColorStateSet packet) =>
            throw new InvalidOperationException("Unexpected legacy fallback.");
    }

    private sealed class SyntheticIdentityProvider : IMonitorIdentityProvider
    {
        public MonitorIdentityFingerprint Resolve(NativeTargetDeviceName name, DisplaySize? preferredMode) => new(
            $"synthetic://panel/{name.Header.Id}", null, null, $"SYNTHETIC-{name.Header.Id}", "TST", (int)name.Header.Id,
            "Synthetic panel", null, null, DisplayOutputTechnology.DisplayPort, name.Header.Id, preferredMode, null);
    }
    private sealed class SupportedSession : IInteractiveDisplaySessionGuard { public bool IsSupported => true; }
    private sealed class ActivationState : IActivationStateStore
    {
        internal Guid? LastProfile { get; private set; }
        public Task SetLastActivatedProfileAsync(Guid profileId, CancellationToken cancellationToken)
        { LastProfile = profileId; return Task.CompletedTask; }
    }
    private sealed class SyntheticRecovery(GuardedDisplayActivationBackend backend) : IRecoveryCoordinator
    {
        internal int Starts { get; private set; }
        public Task<IRecoverySession> StartAsync(ActivationRollbackSnapshot snapshot, CancellationToken cancellationToken)
        {
            Starts++;
            return Task.FromResult<IRecoverySession>(new Session(backend, snapshot));
        }
        private sealed class Session(GuardedDisplayActivationBackend backend, ActivationRollbackSnapshot snapshot) : IRecoverySession
        {
            public Task SignalSuccessAsync(CancellationToken cancellationToken) => backend.DiscardRollbackSnapshotAsync(snapshot, cancellationToken);
            public Task SignalFailureHandledAsync(bool rollbackSucceeded, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
