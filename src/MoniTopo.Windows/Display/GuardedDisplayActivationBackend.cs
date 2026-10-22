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
    private readonly List<NativeDisplaySnapshot> _candidatePlans = [];

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
        QueryCurrentWithClassificationAsync(cancellationToken);

    public void SetMutationGuard(Action? ensureWriterOwnership) => _mutation.SetMutationGuard(ensureWriterOwnership);

    private async Task<CapturedDisplaySnapshot> QueryCurrentWithClassificationAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _captureService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DisplayCaptureException exception) when (exception.IsTransient)
        {
            throw new TransientDisplayReadException(exception.Message, exception);
        }
    }

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
        DisplayConfigurationPlanBuilder.ValidateProfileCapture(profile);
        var available = QueryAll();
        _pendingPlan = null;
        _candidatePlans.Clear();
        foreach (var display in profile.Displays)
        {
            ValidateScaleForPreflight(display, bindings, available);
            var activePath = FindAvailableActivePath(display.DisplayId, bindings, available);
            if (activePath is { } path)
            {
                ValidateHdrForPreflight(display, path);
            }
        }

        return Task.CompletedTask;
    }

    private void BuildCorePlans(DisplayProfile profile, IReadOnlyList<ResolvedIdentityBinding> bindings)
    {
        // Property-only activation must not build/validate/reapply a CCD mode plan.
        // When needed, build from fresh routing immediately before validation.
        var available = QueryAll();
        _pendingPlan = null;
        _candidatePlans.Clear();
        var plan = DisplayConfigurationPlanBuilder.Build(profile, bindings, available);
        for (var routingIndex = 0; routingIndex < 32; routingIndex++)
        {
            try
            {
                var candidate = routingIndex == 0 ? plan : DisplayConfigurationPlanBuilder.Build(
                    profile, bindings, available, sourceRoutingIndex: routingIndex);
                _candidatePlans.Add(candidate);
                if (candidate.Paths.Any(path => !path.SupportsVirtualMode) && profile.Displays.Any(display => display.DesktopImage is null))
                {
                    _candidatePlans.Add(DisplayConfigurationPlanBuilder.Build(
                        profile, bindings, available, forceVirtualModeIndexes: true, sourceRoutingIndex: routingIndex));
                }
            }
            catch (ActivationFailureException exception) when (routingIndex > 0 && exception.ErrorCode == "activation.topology.sources-unavailable")
            {
                break;
            }
        }
    }

    public Task ValidateCoreConfigurationAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BuildCorePlans(profile, bindings);
        var failures = new List<DisplayActivationException>();
        foreach (var candidate in _candidatePlans)
        {
            try
            {
                _mutation.Validate(candidate);
                _pendingPlan = candidate;
                return Task.CompletedTask;
            }
            catch (DisplayActivationException failure)
            {
                failures.Add(failure);
            }
        }

        var firstFailure = failures[0];
        throw new ActivationFailureException(firstFailure.ErrorCode, firstFailure.Message, firstFailure,
            string.Join(" ", failures.Select((failure, index) => $"Candidate {index + 1}: {failure.TechnicalDetails}")));
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
        _ = await WaitForStableCoreAsync(_query, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<NativeDisplaySnapshot> WaitForStableCoreAsync(
        IDisplayConfigNativeFacade query, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        string? previousSignature = null;
        while (timeProvider.GetElapsedTime(started) < StableTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var current = query.Query(QueryOnlyActivePaths | QueryVirtualModeAware | QueryVirtualRefreshRateAware);
                var currentSignature = CreateCoreSignature(current);
                if (current.Paths.Any(path => (path.Flags & ActivePath) != 0) &&
                    string.Equals(previousSignature, currentSignature, StringComparison.Ordinal))
                {
                    return current;
                }

                previousSignature = currentSignature;
            }
            catch (Exception exception) when (IsTransientTopologyRead(exception))
            {
                previousSignature = null;
            }
            await Task.Delay(StableObservationInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }

        throw new ActivationFailureException(
            "activation.topology.unstable",
            "The displays did not settle after Windows changed the topology.");
    }

    public async Task ApplyScalingAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var group in profile.Displays.GroupBy(display => display.SourceGroupId, StringComparer.Ordinal))
        {
            var display = group.First();
            if (group.Any(path => path.WindowsUiScalePercent != display.WindowsUiScalePercent))
            {
                throw new ActivationFailureException("activation.scaling.clone-conflict", "Cloned displays must use the same Windows display scale.");
            }
            var setting = await ReadWithRetryAsync(async () =>
            {
                var path = await ResolveCurrentPropertyPathAsync(profile, display.DisplayId, cancellationToken).ConfigureAwait(false);
                return (Path: path, Scale: ReadScaleSetting(_query, path.SourceInfo.AdapterId, path.SourceInfo.Id, display.WindowsUiScalePercent));
            }, _timeProvider, cancellationToken).ConfigureAwait(false);
            if (setting.Scale.Desired != setting.Scale.Current)
            {
                _mutation.SetDpiScale(setting.Path.SourceInfo.AdapterId, setting.Path.SourceInfo.Id, setting.Scale.Desired);
            }
        }
    }

    public async Task ApplyHdrAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var display in profile.Displays)
        {
            var setting = await ReadWithRetryAsync(async () =>
            {
                var path = await ResolveCurrentPropertyPathAsync(profile, display.DisplayId, cancellationToken).ConfigureAwait(false);
                return (Path: path, Hdr: _query.GetHdrState(path.TargetInfo.AdapterId, path.TargetInfo.Id));
            }, _timeProvider, cancellationToken).ConfigureAwait(false);
            var path = setting.Path;
            var hdr = setting.Hdr;
            if (display.HdrEnabled && (!hdr.IsSupported || hdr.IsLimitedByPolicy))
            {
                throw new ActivationFailureException(
                    "activation.hdr.unsupported",
                    $"HDR cannot be enabled on {display.FriendlyLabel} in the current Windows session.");
            }

            if (hdr.IsSupported && !hdr.IsLimitedByPolicy && hdr.IsEnabled != display.HdrEnabled)
            {
                _mutation.SetHdrState(path.TargetInfo.AdapterId, path.TargetInfo.Id, display.HdrEnabled);
            }
        }

    }

    private async Task<NativePathInfo> ResolveCurrentPropertyPathAsync(DisplayProfile profile, string displayId,
        CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        while (_timeProvider.GetElapsedTime(started) < StableTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var current = await QueryCurrentAsync(cancellationToken).ConfigureAwait(false);
                var resolution = new MonitorIdentityResolver().Resolve(profile, current.ConnectedDisplays);
                if (resolution.Status == IdentityResolutionStatus.Success)
                {
                    return FindCurrentPath(displayId, resolution.Bindings, QueryActive());
                }
            }
            catch (Exception exception) when (exception is TransientDisplayReadException || IsTransientTopologyRead(exception) ||
                exception is ActivationFailureException { ErrorCode: "activation.topology.target-missing" })
            { }

            await Task.Delay(StableObservationInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        throw new ActivationFailureException("activation.identity.changed", "The displays could not be identified while Windows changed their settings.");
    }

    public Task PersistCoreConfigurationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = GetPendingPlan();
        // Scale/HDR transitions can reassign source addresses. The preceding complete
        // profile verification established the desired state; persist that observed
        // routing instead of applying the pre-transition plan for a second time.
        var observed = QueryActive();
        _mutation.Validate(observed);
        _mutation.Persist(observed);
        return Task.CompletedTask;
    }

    public Task<bool> RollbackAsync(ActivationRollbackSnapshot snapshot, CancellationToken cancellationToken) =>
        RestoreSnapshotAsync(snapshot.DataPath, _query, _mutation, cancellationToken, _timeProvider);

    public async Task<bool> EmergencyFallbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_mutation.ApplyExtendedFallback())
        {
            return false;
        }

        return await VerifyFallbackAsync(_query, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    internal static bool RestoreSnapshot(
        string dataPath,
        IDisplayConfigNativeFacade query,
        DisplayConfigMutationFacade mutation,
        CancellationToken cancellationToken) =>
        RestoreSnapshotAsync(dataPath, query, mutation, cancellationToken).GetAwaiter().GetResult();

    internal static async Task<bool> RestoreSnapshotAsync(
        string dataPath, IDisplayConfigNativeFacade query, DisplayConfigMutationFacade mutation,
        CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rollback = DisplayRollbackSnapshotSerializer.Read(dataPath);
            mutation.ApplyTemporary(rollback.CoreConfiguration);
            _ = await WaitForStableCoreAsync(query, clock, cancellationToken).ConfigureAwait(false);
            foreach (var property in rollback.Properties)
            {
                var scaling = await ReadWithRetryAsync(() =>
                {
                    var current = query.Query(QueryOnlyActivePaths | QueryVirtualModeAware | QueryVirtualRefreshRateAware);
                    var path = ResolveRollbackPath(property, current, query);
                    return Task.FromResult((Path: path, Relative: ResolveRelativeScale(query, path.SourceInfo.AdapterId, path.SourceInfo.Id, property.WindowsScalePercent)));
                }, clock, cancellationToken).ConfigureAwait(false);
                mutation.SetDpiScale(scaling.Path.SourceInfo.AdapterId, scaling.Path.SourceInfo.Id, scaling.Relative);
                // HDR can itself trigger source reassignment. Never carry a saved source address
                // from before the topology change into a device-info mutation.
                var color = await ReadWithRetryAsync(() =>
                {
                    var active = query.Query(QueryOnlyActivePaths | QueryVirtualModeAware | QueryVirtualRefreshRateAware);
                    var path = ResolveRollbackPath(property, active, query);
                    return Task.FromResult((Path: path, Hdr: query.GetHdrState(path.TargetInfo.AdapterId, path.TargetInfo.Id)));
                }, clock, cancellationToken).ConfigureAwait(false);
                var path = color.Path;
                var hdr = color.Hdr;
                if (hdr.IsSupported && !hdr.IsLimitedByPolicy && hdr.IsEnabled != property.HdrEnabled)
                {
                    mutation.SetHdrState(path.TargetInfo.AdapterId, path.TargetInfo.Id, property.HdrEnabled);
                }
            }

            var verified = await WaitForRollbackMatchAsync(rollback, query, clock, cancellationToken).ConfigureAwait(false);
            // Persist the observed, verified routing, not the pre-change source addresses.
            mutation.Validate(verified);
            mutation.Persist(verified);
            _ = await WaitForRollbackMatchAsync(rollback, query, clock, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RecoveryOwnershipLostException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ActivationFailureException or DisplayCaptureException)
        {
            return false;
        }
    }

    private RollbackDisplayProperty CaptureRollbackProperty(NativePathInfo path)
    {
        var scale = QueryScale(path.SourceInfo);
        var hdr = _query.GetHdrState(path.TargetInfo.AdapterId, path.TargetInfo.Id);
        return new RollbackDisplayProperty(
            path.SourceInfo.AdapterId,
            path.SourceInfo.Id,
            path.TargetInfo.AdapterId,
            path.TargetInfo.Id,
            scale.CurrentPercent!.Value,
            hdr.IsSupported && hdr.IsEnabled,
            _query.GetTargetName(path.TargetInfo.AdapterId, path.TargetInfo.Id).DevicePath);
    }

    private void ValidateScaleForPreflight(
        DesiredDisplayPath display,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        NativeDisplaySnapshot available)
    {
        if (!UndocumentedDpiScaleContract.ScalePercentages.Contains(display.WindowsUiScalePercent))
        {
            throw new ActivationFailureException(
                "activation.scaling.unsupported",
                $"Windows cannot apply the saved {display.WindowsUiScalePercent}% display scale on this display.");
        }

        var activePath = FindAvailableActivePath(display.DisplayId, bindings, available);
        if (activePath is { } path)
        {
            _ = ResolveRelativeScale(path.SourceInfo, display.WindowsUiScalePercent);
        }
    }

    private void ValidateHdrForPreflight(DesiredDisplayPath display, NativePathInfo activePath)
    {
        var hdr = _query.GetHdrState(activePath.TargetInfo.AdapterId, activePath.TargetInfo.Id);
        if (display.HdrEnabled && (!hdr.IsSupported || hdr.IsLimitedByPolicy))
        {
            throw new ActivationFailureException(
                "activation.hdr.unsupported",
                $"HDR cannot be enabled on {display.FriendlyLabel} in the current Windows session.");
        }
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
        int desiredPercent) => ReadScaleSetting(query, adapterId, sourceId, desiredPercent).Desired;

    private static (int Desired, int Current) ReadScaleSetting(
        IDisplayConfigNativeFacade query, NativeAdapterId adapterId, uint sourceId, int desiredPercent)
    {
        var (errorCode, packet) = query.TryGetDpiScale(adapterId, sourceId);
        if (errorCode != DisplayConfigNativeFacade.ErrorSuccess)
        {
            var failure = new DisplayCaptureException(errorCode, "Windows could not read the current display scaling capability.");
            if (failure.IsTransient)
            {
                throw failure;
            }
        }
        if (errorCode != DisplayConfigNativeFacade.ErrorSuccess ||
            !DisplayScaleService.TryResolveRelativeScale(packet, desiredPercent, out var relativeScale))
        {
            throw new ActivationFailureException(
                "activation.scaling.unsupported",
                $"Windows cannot apply the saved {desiredPercent}% display scale on this display.");
        }

        return (relativeScale, packet.CurrentRelativeScale);
    }

    private static NativePathInfo? FindAvailableActivePath(
        string displayId,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        NativeDisplaySnapshot available)
    {
        var binding = bindings.Single(item => string.Equals(item.DisplayId, displayId, StringComparison.OrdinalIgnoreCase));
        foreach (var path in available.Paths)
        {
            if ((path.Flags & ActivePath) != 0 &&
                string.Equals(
                    TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id),
                    binding.RuntimeId,
                    StringComparison.Ordinal))
            {
                return path;
            }
        }

        return null;
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

    private static bool IsTransientTopologyRead(Exception exception) =>
        exception is InvalidDataException or DisplayCaptureException { IsTransient: true };

    private static async Task<T> ReadWithRetryAsync<T>(Func<Task<T>> read, TimeProvider clock, CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        while (clock.GetElapsedTime(started) < StableTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await read().ConfigureAwait(false); }
            catch (Exception exception) when (IsTransientTopologyRead(exception) || exception is TransientDisplayReadException) { }
            await Task.Delay(StableObservationInterval, clock, cancellationToken).ConfigureAwait(false);
        }

        throw new ActivationFailureException("activation.read.unstable", "Windows did not provide a stable display state before the operation timed out.");
    }

    private static NativePathInfo ResolveRollbackPath(RollbackDisplayProperty property,
        NativeDisplaySnapshot active, IDisplayConfigNativeFacade query)
    {
        var paths = active.Paths.Where(path => (path.Flags & ActivePath) != 0).ToArray();
        if (!string.IsNullOrWhiteSpace(property.TargetDevicePath))
        {
            var matches = paths.Where(path => string.Equals(
                query.GetTargetName(path.TargetInfo.AdapterId, path.TargetInfo.Id).DevicePath,
                property.TargetDevicePath, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }

            throw new InvalidDataException("The rollback target could not be identified uniquely after the topology change.");
        }

        var matched = paths.Where(path => path.TargetInfo.AdapterId == property.TargetAdapterId && path.TargetInfo.Id == property.TargetId).ToArray();
        return matched.Length == 1 ? matched[0] : throw new InvalidDataException("The rollback target is temporarily unavailable.");
    }

    private static async Task<NativeDisplaySnapshot> WaitForRollbackMatchAsync(RollbackDisplayState rollback,
        IDisplayConfigNativeFacade query, TimeProvider clock, CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        var matched = false;
        while (clock.GetElapsedTime(started) < StableTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var current = query.Query(QueryOnlyActivePaths | QueryVirtualModeAware | QueryVirtualRefreshRateAware);
                var canonicalTargets = new Dictionary<string, string>(StringComparer.Ordinal);
                var propertiesMatch = true;
                foreach (var property in rollback.Properties)
                {
                    var path = ResolveRollbackPath(property, current, query);
                    canonicalTargets[TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id)] = TargetKey(property.TargetAdapterId, property.TargetId);
                    var scale = new DisplayScaleService(query).Query(new DisplaySourceAddress(
                        path.SourceInfo.AdapterId.LowPart, path.SourceInfo.AdapterId.HighPart, path.SourceInfo.Id));
                    var hdr = query.GetHdrState(path.TargetInfo.AdapterId, path.TargetInfo.Id);
                    propertiesMatch &= scale.IsSupported && scale.CurrentPercent == property.WindowsScalePercent &&
                        (hdr.IsSupported && hdr.IsEnabled) == property.HdrEnabled;
                }

                var equal = propertiesMatch && string.Equals(CreateCoreSignature(rollback.CoreConfiguration),
                    CreateCoreSignature(current, canonicalTargets), StringComparison.Ordinal);
                if (equal && matched)
                {
                    return current;
                }

                matched = equal;
            }
            catch (Exception exception) when (IsTransientTopologyRead(exception))
            {
                matched = false;
            }

            await Task.Delay(StableObservationInterval, clock, cancellationToken).ConfigureAwait(false);
        }

        throw new ActivationFailureException("activation.rollback.verification", "Windows did not restore every previous display setting.");
    }

    internal static async Task<bool> VerifyFallbackAsync(IDisplayConfigNativeFacade query, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await WaitForStableCoreAsync(query, clock, cancellationToken).ConfigureAwait(false);
            return snapshot.Paths.Any(path => (path.Flags & ActivePath) != 0 &&
                path.TryResolveSourceModeInfoIndex(snapshot.Modes, out var index) &&
                snapshot.Modes[index].Mode.SourceMode.Width > 0 && snapshot.Modes[index].Mode.SourceMode.Height > 0);
        }
        catch (Exception exception) when (exception is DisplayCaptureException or ActivationFailureException or InvalidDataException)
        {
            return false;
        }
    }

    private static string CreateCoreSignature(NativeDisplaySnapshot snapshot,
        IReadOnlyDictionary<string, string>? canonicalTargets = null)
    {
        var activePaths = snapshot.Paths.Where(path => (path.Flags & ActivePath) != 0).ToArray();
        string Target(NativePathInfo path)
        {
            var key = TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id);
            return canonicalTargets?.GetValueOrDefault(key) ?? key;
        }

        var sourceGroups = activePaths.GroupBy(path => (path.SourceInfo.AdapterId, path.SourceInfo.Id))
            .ToDictionary(group => group.Key, group => string.Join(',', group.Select(Target).OrderBy(key => key, StringComparer.Ordinal)));
        return string.Join(
            '|',
            activePaths.Select(path => CreatePathSignature(path, snapshot.Modes, Target(path),
                sourceGroups[(path.SourceInfo.AdapterId, path.SourceInfo.Id)]))
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    private static string CreatePathSignature(NativePathInfo path, NativeModeInfo[] modes, string targetKey, string sourceGroup)
    {
        if (!path.TryResolveSourceModeInfoIndex(modes, out var sourceModeIndex) ||
            !path.TryResolveTargetModeInfoIndex(modes, out var targetModeIndex))
        {
            throw new InvalidDataException("Windows returned an incomplete active display mode during rollback verification.");
        }

        var source = modes[sourceModeIndex].Mode.SourceMode;
        var target = modes[targetModeIndex].Mode.TargetMode.TargetVideoSignalInfo;
        var desktop = path.TryResolveDesktopModeInfoIndex(modes, out var desktopIndex)
            ? modes[desktopIndex].Mode.DesktopImageInfo : (NativeDesktopImageInfo?)null;
        return $"{targetKey};{sourceGroup};" +
            $"{source.Width}x{source.Height}@{source.Position.X},{source.Position.Y};" +
            $"{path.TargetInfo.Rotation};{path.TargetInfo.Scaling};" +
            $"{path.TargetInfo.RefreshRate.Numerator}/{path.TargetInfo.RefreshRate.Denominator};" +
            $"{target.PixelRate}:{target.ActiveSize.Width}x{target.ActiveSize.Height}:" +
            $"{target.TotalSize.Width}x{target.TotalSize.Height}:{target.ScanLineOrdering};" +
            $"{path.Flags & NativeDisplayConfigPathFlags.BoostRefreshRate};{desktop?.PathSourceSize}:{desktop?.DesktopImageRegion}:{desktop?.DesktopImageClip}";
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

    public async Task<bool> RollbackAsync(string rollbackDataPath, CancellationToken cancellationToken) =>
        (await RecoverAsync(rollbackDataPath, cancellationToken).ConfigureAwait(false)).Restored;

    public async Task<RecoveryRollbackResult> RecoverAsync(string rollbackDataPath, CancellationToken cancellationToken)
    {
        var restored = false;
        try
        {
            restored = await GuardedDisplayActivationBackend.RestoreSnapshotAsync(
                rollbackDataPath, _query, _mutation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No read/parse/driver exception can bypass the deliberate fallback decision.
        }

        if (restored)
        {
            return new(true);
        }

        var fallback = false;
        try
        {
            fallback = _mutation.ApplyExtendedFallback() && await GuardedDisplayActivationBackend.VerifyFallbackAsync(
                _query, TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { }

        return new(false, fallback);
    }
}
