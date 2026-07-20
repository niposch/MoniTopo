using System.Runtime.InteropServices;
using MoniTopo.Core.Activation;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Models;
using MoniTopo.Core.Recovery;

namespace MoniTopo.Windows.Display;

public interface IInteractiveDisplaySessionGuard
{
    bool IsSupported { get; }
}

public sealed class InteractiveDisplaySessionGuard : IInteractiveDisplaySessionGuard
{
    private const int RemoteSessionMetric = 0x1000;

    public bool IsSupported => NativeMethods.GetSystemMetrics(RemoteSessionMetric) == 0;
}

public sealed class GuardedDisplayActivationBackend : IDisplayActivationBackend
{
    private const uint QueryAllPaths = 0x1;
    private const uint QueryOnlyActivePaths = 0x2;
    private const uint QueryVirtualModeAware = 0x10;
    private const uint QueryVirtualRefreshRateAware = 0x40;
    private const uint ActivePath = 0x1;
    private static readonly TimeSpan StableObservationInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StableTimeout = TimeSpan.FromSeconds(8);
    private readonly IDisplayCaptureService _captureService;
    private readonly IDisplayConfigNativeFacade _query;
    private readonly DisplayConfigMutationFacade _mutation;
    private readonly IInteractiveDisplaySessionGuard _sessionGuard;
    private readonly string _recoveryRoot;
    private readonly TimeProvider _timeProvider;
    private NativeDisplaySnapshot? _pendingPlan;

    public GuardedDisplayActivationBackend(DisplayMutationAuthorization authorization)
        : this(
            new CcdCaptureService(),
            new DisplayConfigNativeFacade(),
            new DisplayConfigMutationFacade(authorization),
            new InteractiveDisplaySessionGuard(),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MoniTopo",
                "recovery"),
            TimeProvider.System)
    {
    }

    internal GuardedDisplayActivationBackend(
        IDisplayCaptureService captureService,
        IDisplayConfigNativeFacade query,
        DisplayConfigMutationFacade mutation,
        IInteractiveDisplaySessionGuard sessionGuard,
        string recoveryRoot,
        TimeProvider timeProvider)
    {
        _captureService = captureService;
        _query = query;
        _mutation = mutation;
        _sessionGuard = sessionGuard;
        _recoveryRoot = recoveryRoot;
        _timeProvider = timeProvider;
    }

    public Task<CapturedDisplaySnapshot> QueryCurrentAsync(CancellationToken cancellationToken) =>
        _captureService.CaptureAsync(cancellationToken);

    public Task<ActivationRollbackSnapshot> CaptureRollbackSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInteractiveSession();
        var raw = QueryAll();
        var properties = raw.Paths
            .Where(path => (path.Flags & ActivePath) != 0)
            .DistinctBy(path => TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id))
            .Select(path => CaptureRollbackProperty(path))
            .ToArray();
        var transactionId = Guid.NewGuid();
        var directory = Path.Combine(_recoveryRoot, transactionId.ToString("N"));
        var dataPath = Path.Combine(directory, "rollback.bin");
        DisplayRollbackSnapshotSerializer.Write(dataPath, new RollbackDisplayState(raw, properties));
        return Task.FromResult(new ActivationRollbackSnapshot(transactionId, 1, dataPath));
    }

    public Task DiscardRollbackSnapshotAsync(
        ActivationRollbackSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(snapshot.DataPath);
        var root = Path.GetFullPath(_recoveryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (directory is not null)
        {
            var resolvedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolvedDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The rollback snapshot is outside the MoniTopo recovery directory.");
            }

            if (Directory.Exists(resolvedDirectory))
            {
                Directory.Delete(resolvedDirectory, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    public Task PreflightAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInteractiveSession();
        var plan = DisplayConfigurationPlanBuilder.Build(profile, bindings, QueryAll());
        foreach (var display in profile.Displays)
        {
            var plannedPath = FindPlannedPath(display.DisplayId, bindings, plan);
            _ = ResolveRelativeScale(plannedPath.SourceInfo, display.WindowsUiScalePercent);
            var color = _query.GetAdvancedColorInfo(plannedPath.TargetInfo.AdapterId, plannedPath.TargetInfo.Id);
            if (display.HdrEnabled && (!color.IsSupported || color.IsForceDisabled))
            {
                throw new ActivationFailureException(
                    "activation.hdr.unsupported",
                    $"HDR cannot be enabled on {display.FriendlyLabel} in the current Windows session.");
            }
        }

        _pendingPlan = plan;
        return Task.CompletedTask;
    }

    public Task ValidateCoreConfigurationAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _mutation.Validate(GetPendingPlan());
        return Task.CompletedTask;
    }

    public Task ApplyCoreTemporaryAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _mutation.ApplyTemporary(GetPendingPlan());
        return Task.CompletedTask;
    }

    public async Task WaitForStableTopologyAsync(CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + StableTimeout;
        string? previousSignature = null;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSignature = CreateCoreSignature(QueryActive());
            if (string.Equals(previousSignature, currentSignature, StringComparison.Ordinal))
            {
                return;
            }

            previousSignature = currentSignature;
            await Task.Delay(StableObservationInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        throw new ActivationFailureException(
            "activation.topology.unstable",
            "The displays did not settle after Windows changed the topology.");
    }

    public Task ApplyScalingAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = QueryActive();
        foreach (var display in profile.Displays)
        {
            var path = FindCurrentPath(display.DisplayId, bindings, active);
            var relativeScale = ResolveRelativeScale(path.SourceInfo, display.WindowsUiScalePercent);
            _mutation.SetDpiScale(path.SourceInfo.AdapterId, path.SourceInfo.Id, relativeScale);
        }

        return Task.CompletedTask;
    }

    public Task ApplyHdrAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = QueryActive();
        foreach (var display in profile.Displays)
        {
            var path = FindCurrentPath(display.DisplayId, bindings, active);
            var color = _query.GetAdvancedColorInfo(path.TargetInfo.AdapterId, path.TargetInfo.Id);
            if (color.IsSupported && !color.IsForceDisabled && color.IsEnabled != display.HdrEnabled)
            {
                _mutation.SetAdvancedColor(path.TargetInfo.AdapterId, path.TargetInfo.Id, display.HdrEnabled);
            }
        }

        return Task.CompletedTask;
    }

    public Task PersistCoreConfigurationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _mutation.Persist(GetPendingPlan());
        return Task.CompletedTask;
    }

    public Task<bool> RollbackAsync(ActivationRollbackSnapshot snapshot, CancellationToken cancellationToken) =>
        Task.FromResult(RestoreSnapshot(snapshot.DataPath, _query, _mutation, cancellationToken));

    public Task<bool> EmergencyFallbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_mutation.ApplyExtendedFallback());
    }

    internal static bool RestoreSnapshot(
        string dataPath,
        IDisplayConfigNativeFacade query,
        DisplayConfigMutationFacade mutation,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rollback = DisplayRollbackSnapshotSerializer.Read(dataPath);
            mutation.ApplyTemporary(rollback.CoreConfiguration);
            foreach (var property in rollback.Properties)
            {
                var relativeScale = ResolveRelativeScale(query, property.SourceAdapterId, property.SourceId, property.WindowsScalePercent);
                mutation.SetDpiScale(property.SourceAdapterId, property.SourceId, relativeScale);
                var color = query.GetAdvancedColorInfo(property.TargetAdapterId, property.TargetId);
                if (color.IsSupported && !color.IsForceDisabled && color.IsEnabled != property.HdrEnabled)
                {
                    mutation.SetAdvancedColor(property.TargetAdapterId, property.TargetId, property.HdrEnabled);
                }
            }

            mutation.Persist(rollback.CoreConfiguration);
            var current = query.Query(QueryOnlyActivePaths | QueryVirtualModeAware | QueryVirtualRefreshRateAware);
            if (!string.Equals(
                CreateCoreSignature(rollback.CoreConfiguration),
                CreateCoreSignature(current),
                StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var property in rollback.Properties)
            {
                var scale = new DisplayScaleService(query).Query(new DisplaySourceAddress(
                    property.SourceAdapterId.LowPart,
                    property.SourceAdapterId.HighPart,
                    property.SourceId));
                var color = query.GetAdvancedColorInfo(property.TargetAdapterId, property.TargetId);
                var hdrEnabled = color.IsSupported && color.IsEnabled && !color.IsForceDisabled;
                if (!scale.IsSupported || scale.CurrentPercent != property.WindowsScalePercent || hdrEnabled != property.HdrEnabled)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ActivationFailureException)
        {
            return false;
        }
    }

    private RollbackDisplayProperty CaptureRollbackProperty(NativePathInfo path)
    {
        var scale = QueryScale(path.SourceInfo);
        var color = _query.GetAdvancedColorInfo(path.TargetInfo.AdapterId, path.TargetInfo.Id);
        return new RollbackDisplayProperty(
            path.SourceInfo.AdapterId,
            path.SourceInfo.Id,
            path.TargetInfo.AdapterId,
            path.TargetInfo.Id,
            scale.CurrentPercent!.Value,
            color.IsSupported && color.IsEnabled && !color.IsForceDisabled);
    }

    private int ResolveRelativeScale(NativePathSourceInfo source, int desiredPercent) =>
        ResolveRelativeScale(_query, source.AdapterId, source.Id, desiredPercent);

    private DisplayScaleCapability QueryScale(NativePathSourceInfo source)
    {
        var scale = new DisplayScaleService(_query).Query(new DisplaySourceAddress(
            source.AdapterId.LowPart,
            source.AdapterId.HighPart,
            source.Id));
        if (!scale.IsSupported || scale.CurrentPercent is null)
        {
            throw new ActivationFailureException(
                "activation.scaling.unsupported",
                scale.ErrorMessage ?? "Windows display scaling is not supported on this Windows build.");
        }

        return scale;
    }

    private static int ResolveRelativeScale(
        IDisplayConfigNativeFacade query,
        NativeAdapterId adapterId,
        uint sourceId,
        int desiredPercent)
    {
        var (errorCode, packet) = query.TryGetDpiScale(adapterId, sourceId);
        if (errorCode != DisplayConfigNativeFacade.ErrorSuccess ||
            !DisplayScaleService.TryResolveRelativeScale(packet, desiredPercent, out var relativeScale))
        {
            throw new ActivationFailureException(
                "activation.scaling.unsupported",
                $"Windows cannot apply the saved {desiredPercent}% display scale on this display.");
        }

        return relativeScale;
    }

    private static NativePathInfo FindPlannedPath(
        string displayId,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        NativeDisplaySnapshot plan)
    {
        var binding = bindings.Single(item => string.Equals(item.DisplayId, displayId, StringComparison.OrdinalIgnoreCase));
        return plan.Paths.Single(path => string.Equals(
            TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id),
            binding.RuntimeId,
            StringComparison.Ordinal));
    }

    private static NativePathInfo FindCurrentPath(
        string displayId,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        NativeDisplaySnapshot active)
    {
        var binding = bindings.Single(item => string.Equals(item.DisplayId, displayId, StringComparison.OrdinalIgnoreCase));
        return active.Paths.FirstOrDefault(path => string.Equals(
            TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id),
            binding.RuntimeId,
            StringComparison.Ordinal)) is var result && (result.Flags & ActivePath) != 0
            ? result
            : throw new ActivationFailureException(
                "activation.topology.target-missing",
                "A required display disappeared while Windows was applying the profile.");
    }

    private NativeDisplaySnapshot QueryAll() =>
        _query.Query(QueryAllPaths | QueryVirtualModeAware | QueryVirtualRefreshRateAware);

    private NativeDisplaySnapshot QueryActive() =>
        _query.Query(QueryOnlyActivePaths | QueryVirtualModeAware | QueryVirtualRefreshRateAware);

    private NativeDisplaySnapshot GetPendingPlan() => _pendingPlan ?? throw new InvalidOperationException("Preflight has not built a display plan.");

    private void EnsureInteractiveSession()
    {
        if (!_sessionGuard.IsSupported)
        {
            throw new ActivationFailureException(
                "activation.session.unsupported",
                "Display profiles cannot be activated from a remote or noninteractive Windows session.");
        }
    }

    private static string CreateCoreSignature(NativeDisplaySnapshot snapshot)
    {
        var activePaths = snapshot.Paths.Where(path => (path.Flags & ActivePath) != 0);
        return string.Join(
            '|',
            activePaths.Select(path => CreatePathSignature(path, snapshot.Modes))
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    private static string CreatePathSignature(NativePathInfo path, NativeModeInfo[] modes)
    {
        var source = ModeAt(modes, path.SourceInfo.SourceModeInfoIndex, 1).Mode.SourceMode;
        var target = ModeAt(modes, path.TargetInfo.TargetModeInfoIndex, 2).Mode.TargetMode.TargetVideoSignalInfo;
        return $"{TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id)};" +
            $"{path.SourceInfo.AdapterId.StableKey}:{path.SourceInfo.Id}:{path.SourceInfo.CloneGroupId};" +
            $"{source.Width}x{source.Height}@{source.Position.X},{source.Position.Y};" +
            $"{path.TargetInfo.Rotation};{path.TargetInfo.Scaling};" +
            $"{path.TargetInfo.RefreshRate.Numerator}/{path.TargetInfo.RefreshRate.Denominator};" +
            $"{target.PixelRate}:{target.ActiveSize.Width}x{target.ActiveSize.Height}:" +
            $"{target.TotalSize.Width}x{target.TotalSize.Height}:{target.ScanLineOrdering}";
    }

    private static NativeModeInfo ModeAt(NativeModeInfo[] modes, uint index, uint expectedType)
    {
        if (index >= modes.Length || modes[index].InfoType != expectedType)
        {
            throw new InvalidDataException("Windows returned an incomplete active display mode during rollback verification.");
        }

        return modes[index];
    }

    private static string TargetKey(NativeAdapterId adapterId, uint targetId) => $"{adapterId.StableKey}:T{targetId}";
}

public sealed class DisplayRollbackExecutor : IRecoveryRollbackExecutor
{
    private readonly IDisplayConfigNativeFacade _query;
    private readonly DisplayConfigMutationFacade _mutation;

    public DisplayRollbackExecutor(DisplayMutationAuthorization authorization)
        : this(new DisplayConfigNativeFacade(), new DisplayConfigMutationFacade(authorization))
    {
    }

    internal DisplayRollbackExecutor(
        IDisplayConfigNativeFacade query,
        DisplayConfigMutationFacade mutation)
    {
        _query = query;
        _mutation = mutation;
    }

    public Task<bool> RollbackAsync(string rollbackDataPath, CancellationToken cancellationToken) =>
        Task.FromResult(RestoreWithFallback(rollbackDataPath, cancellationToken));

    private bool RestoreWithFallback(string rollbackDataPath, CancellationToken cancellationToken)
    {
        var restored = GuardedDisplayActivationBackend.RestoreSnapshot(
            rollbackDataPath,
            _query,
            _mutation,
            cancellationToken);
        if (!restored)
        {
            _ = _mutation.ApplyExtendedFallback();
        }

        return restored;
    }
}
