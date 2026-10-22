using MoniTopo.Core.Models;

namespace MoniTopo.Windows.Display;

public interface IDisplayCaptureService
{
    Task<CapturedDisplaySnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}

public sealed class CcdCaptureService : IDisplayCaptureService
{
    private const uint QueryAllPaths = 0x1;
    private const uint QueryVirtualModeAware = 0x10;
    private const uint QueryVirtualRefreshRateAware = 0x40;
    private const uint ActivePath = 0x1;
    private const int MaximumCaptureAttempts = 3;
    private static readonly TimeSpan CaptureRetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly IDisplayConfigNativeFacade _native;
    private readonly IDisplayScaleService _scaleService;
    private readonly IMonitorIdentityProvider _identityProvider;
    private readonly TimeProvider _timeProvider;

    public CcdCaptureService()
        : this(
            new DisplayConfigNativeFacade(),
            null,
            new SetupApiMonitorIdentityProvider(),
            TimeProvider.System)
    {
    }

    internal CcdCaptureService(
        IDisplayConfigNativeFacade native,
        IDisplayScaleService? scaleService,
        IMonitorIdentityProvider identityProvider,
        TimeProvider timeProvider)
    {
        _native = native;
        _scaleService = scaleService ?? new DisplayScaleService(native);
        _identityProvider = identityProvider;
        _timeProvider = timeProvider;
    }

    public Task<CapturedDisplaySnapshot> CaptureAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => CaptureWithRetryAsync(cancellationToken), cancellationToken);

    private async Task<CapturedDisplaySnapshot> CaptureWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var raw = QuerySnapshot();
                var snapshot = ConvertSnapshot(raw, cancellationToken);
                EnsureManagedPropertiesStable(raw, snapshot, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ActivePathsAreEquivalent(ObserveActivePaths(raw), ObserveActivePaths(QuerySnapshot())))
                {
                    throw new DisplayCaptureException(0,
                        "The display configuration changed while MoniTopo was reading it. Try again after the displays settle.",
                        isTransient: true);
                }

                return snapshot;
            }
            catch (DisplayCaptureException exception) when (exception.IsTransient && attempt + 1 < MaximumCaptureAttempts)
            {
                await Task.Delay(CaptureRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private NativeDisplaySnapshot QuerySnapshot()
    {
        try
        {
            return _native.Query(QueryAllPaths | QueryVirtualModeAware | QueryVirtualRefreshRateAware);
        }
        catch (DisplayCaptureException exception) when (DisplayConfigNativeFacade.IsUnsupportedRequest(exception.NativeErrorCode))
        {
            // Virtual refresh-rate awareness is Windows 11 only. Older Windows
            // versions can still return ordinary virtual-mode-aware snapshots.
            return _native.Query(QueryAllPaths | QueryVirtualModeAware);
        }
    }

    private void EnsureManagedPropertiesStable(
        NativeDisplaySnapshot raw, CapturedDisplaySnapshot snapshot, CancellationToken cancellationToken)
    {
        var captured = snapshot.ConnectedDisplays.Where(display => display.IsActive)
            .ToDictionary(display => display.RuntimeId, display => display.ActivePath!, StringComparer.Ordinal);
        foreach (var path in raw.Paths.Where(path => (path.Flags & ActivePath) != 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scale = _scaleService.Query(new DisplaySourceAddress(
                path.SourceInfo.AdapterId.LowPart, path.SourceInfo.AdapterId.HighPart, path.SourceInfo.Id));
            var hdr = _native.GetHdrState(path.TargetInfo.AdapterId, path.TargetInfo.Id);
            var original = captured[TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id)];
            if (!scale.IsSupported || scale.CurrentPercent != original.WindowsUiScalePercent ||
                (hdr.IsSupported && hdr.IsEnabled) != original.HdrEnabled)
            {
                throw new DisplayCaptureException(0,
                    "Display scaling or HDR changed while MoniTopo was reading it. Try again after the displays settle.",
                    isTransient: true);
            }
        }
    }

    private CapturedDisplaySnapshot ConvertSnapshot(NativeDisplaySnapshot raw, CancellationToken cancellationToken)
    {
        var activeNativePaths = raw.Paths.Where(path => (path.Flags & ActivePath) != 0).ToArray();
        if (activeNativePaths.Length == 0 || activeNativePaths.Any(path => path.TargetInfo.TargetAvailable == 0) ||
            activeNativePaths.DistinctBy(path => TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id)).Count() != activeNativePaths.Length)
        {
            throw new DisplayCaptureException(0,
                "Windows is changing the active display set. Try again after the displays settle.", isTransient: true);
        }

        var availablePaths = raw.Paths.Where(path => path.TargetInfo.TargetAvailable != 0).ToArray();
        var uniqueTargets = availablePaths
            .GroupBy(path => TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(path => (path.Flags & ActivePath) != 0).First())
            .ToArray();

        var targetData = new Dictionary<string, (NativeTargetDeviceName Name, MonitorIdentityFingerprint Identity)>(StringComparer.Ordinal);
        foreach (var path in uniqueTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var name = _native.GetTargetName(path.TargetInfo.AdapterId, path.TargetInfo.Id);
                var preferredSize = TryGetPreferredSize(path);
                targetData.Add(TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id),
                    (name, _identityProvider.Resolve(name, preferredSize)));
            }
            catch (DisplayCaptureException) when ((path.Flags & ActivePath) == 0)
            {
                // A sleeping or disappearing extra output must not make the active
                // desktop uncapturable. Without identity metadata it cannot safely
                // participate in resolution, so omit it until a later query succeeds.
            }
        }

        var primarySources = activeNativePaths
            .Select(path => path.SourceInfo)
            .DistinctBy(source => SourceKey(source.AdapterId, source.Id))
            .Where(source => _native.IsPrimaryGdiDevice(_native.GetSourceName(source.AdapterId, source.Id)))
            .Select(source => SourceKey(source.AdapterId, source.Id))
            .ToHashSet(StringComparer.Ordinal);
        if (primarySources.Count != 1)
        {
            throw new DisplayCaptureException(0, "Windows did not identify a primary display source.", isTransient: true);
        }

        var sourceCounts = activeNativePaths
            .GroupBy(path => SourceKey(path.SourceInfo.AdapterId, path.SourceInfo.Id), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var primaryAssigned = false;
        var activePaths = new List<DesiredDisplayPath>(activeNativePaths.Length);
        for (var index = 0; index < activeNativePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = activeNativePaths[index];
            var sourceKey = SourceKey(path.SourceInfo.AdapterId, path.SourceInfo.Id);
            if (!path.TryResolveSourceModeInfoIndex(raw.Modes, out var sourceModeIndex))
            {
                throw IncompleteModeException("source", path, raw.Modes.Length);
            }

            if (!path.TryResolveTargetModeInfoIndex(raw.Modes, out var targetModeIndex))
            {
                throw IncompleteModeException("target", path, raw.Modes.Length);
            }

            var scale = _scaleService.Query(new DisplaySourceAddress(
                path.SourceInfo.AdapterId.LowPart,
                path.SourceInfo.AdapterId.HighPart,
                path.SourceInfo.Id));
            if (!scale.IsSupported || scale.CurrentPercent is null)
            {
                throw new DisplayCaptureException(0, scale.ErrorMessage ?? "Windows display scaling could not be captured.");
            }

            var hdr = _native.GetHdrState(path.TargetInfo.AdapterId, path.TargetInfo.Id);
            var targetKey = TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id);
            var target = targetData[targetKey];
            var sourceMode = raw.Modes[sourceModeIndex].Mode.SourceMode;
            var isPrimary = !primaryAssigned && primarySources.Contains(sourceKey);
            primaryAssigned |= isPrimary;
            var displayId = $"display-{index + 1}";
            var targetSignal = raw.Modes[targetModeIndex].Mode.TargetMode.TargetVideoSignalInfo;
            var desktopImage = CaptureDesktopImage(path, raw.Modes);
            var signal = CaptureTargetSignal(targetSignal);
            if (!signal.IsValid || sourceMode.Width == 0 || sourceMode.Height == 0 ||
                path.TargetInfo.RefreshRate.Numerator == 0 || path.TargetInfo.RefreshRate.Denominator == 0)
            {
                throw IncompleteModeException("physical signal", path, raw.Modes.Length);
            }

            activePaths.Add(new DesiredDisplayPath(
                DisplayId: displayId,
                Identity: target.Identity,
                SourceGroupId: sourceKey,
                CloneGroupId: sourceCounts[sourceKey] > 1 ? $"clone-{sourceKey}" : null,
                Position: new DisplayPoint(sourceMode.Position.X, sourceMode.Position.Y),
                SourceResolution: ToSize(new NativeRegion(sourceMode.Width, sourceMode.Height)),
                RefreshRate: new RefreshRate(path.TargetInfo.RefreshRate.Numerator, path.TargetInfo.RefreshRate.Denominator),
                Orientation: MapOrientation(path.TargetInfo.Rotation),
                PathScaling: MapScaling(path.TargetInfo.Scaling),
                WindowsUiScalePercent: scale.CurrentPercent.Value,
                HdrEnabled: hdr.IsSupported && hdr.IsEnabled,
                IsPrimary: isPrimary,
                FriendlyLabel: string.IsNullOrWhiteSpace(target.Name.FriendlyName) ? $"Display {index + 1}" : target.Name.FriendlyName)
            {
                TargetSignal = signal,
                DesktopImage = desktopImage,
                BoostRefreshRate = (path.Flags & NativeDisplayConfigPathFlags.BoostRefreshRate) != 0,
            });
        }

        var activeByTarget = activeNativePaths
            .Select((path, index) => (Key: TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id), Path: activePaths[index]))
            .ToDictionary(item => item.Key, item => item.Path, StringComparer.Ordinal);
        var connected = uniqueTargets.Where(path => targetData.ContainsKey(TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id))).Select((path, index) =>
        {
            var key = TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id);
            var target = targetData[key];
            activeByTarget.TryGetValue(key, out var activePath);
            return new ConnectedDisplayState(
                RuntimeId: key,
                Identity: target.Identity,
                IsActive: activePath is not null,
                FriendlyLabel: string.IsNullOrWhiteSpace(target.Name.FriendlyName) ? $"Display {index + 1}" : target.Name.FriendlyName,
                ActivePath: activePath);
        }).ToArray();

        var primaryId = activePaths.Single(path => path.IsPrimary).DisplayId;
        return new CapturedDisplaySnapshot(activePaths, connected, primaryId, _timeProvider.GetUtcNow());
    }

    private DisplaySize? TryGetPreferredSize(NativePathInfo path)
    {
        try
        {
            var preferred = _native.GetTargetPreferredMode(path.TargetInfo.AdapterId, path.TargetInfo.Id);
            return preferred.Width is > 0 and <= int.MaxValue && preferred.Height is > 0 and <= int.MaxValue
                ? new DisplaySize((int)preferred.Width, (int)preferred.Height) : null;
        }
        catch (DisplayCaptureException)
        {
            // Preferred mode is an optional weak identity signal, never a required
            // active-state property. The current physical mode comes from CCD.
            return null;
        }
    }

    private static DisplayDesktopImage? CaptureDesktopImage(NativePathInfo path, NativeModeInfo[] modes)
    {
        if (!path.HasDesktopModeReference(modes))
        {
            return null;
        }

        if (!path.TryResolveDesktopModeInfoIndex(modes, out var index))
        {
            throw IncompleteModeException("desktop image", path, modes.Length);
        }

        var native = modes[index].Mode.DesktopImageInfo;
        var image = new DisplayDesktopImage(
            new DisplaySize(native.PathSourceSize.X, native.PathSourceSize.Y),
            ToRectangle(native.DesktopImageRegion), ToRectangle(native.DesktopImageClip));
        return image.IsValid ? image : throw IncompleteModeException("desktop image", path, modes.Length);
    }

    private static DisplayRectangle ToRectangle(NativeRect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static DisplayTargetSignal CaptureTargetSignal(NativeVideoSignalInfo signal) => new(
        signal.PixelRate,
        new RefreshRate(signal.HorizontalSyncFrequency.Numerator, signal.HorizontalSyncFrequency.Denominator),
        new RefreshRate(signal.VerticalSyncFrequency.Numerator, signal.VerticalSyncFrequency.Denominator),
        ToSize(signal.ActiveSize),
        ToSize(signal.TotalSize),
        signal.VideoStandard,
        signal.ScanLineOrdering);

    private static DisplaySize ToSize(NativeRegion size) =>
        size.Width is > 0 and <= int.MaxValue && size.Height is > 0 and <= int.MaxValue
            ? new DisplaySize((int)size.Width, (int)size.Height)
            : throw new DisplayCaptureException(0, "Windows returned an incomplete display size.", isTransient: true);

    private static bool ActivePathsAreEquivalent(ActivePathObservation[] left, ActivePathObservation[] right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Source == pair.Second.Source && pair.First.Target == pair.Second.Target &&
            pair.First.Available == pair.Second.Available && pair.First.Rotation == pair.Second.Rotation &&
            pair.First.Scaling == pair.Second.Scaling && pair.First.Boost == pair.Second.Boost &&
            pair.First.SourceMode.Equals(pair.Second.SourceMode) &&
            new RefreshRate(pair.First.Refresh.Numerator, pair.First.Refresh.Denominator).IsEquivalentTo(
                new RefreshRate(pair.Second.Refresh.Numerator, pair.Second.Refresh.Denominator)) &&
            pair.First.TargetSignal.IsEquivalentTo(pair.Second.TargetSignal) &&
            pair.First.DesktopImage == pair.Second.DesktopImage);

    private static ActivePathObservation[] ObserveActivePaths(NativeDisplaySnapshot snapshot) => snapshot.Paths
        .Where(path => (path.Flags & ActivePath) != 0)
        .OrderBy(path => TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id), StringComparer.Ordinal)
        .Select(path =>
        {
            if (!path.TryResolveSourceModeInfoIndex(snapshot.Modes, out var sourceIndex) ||
                !path.TryResolveTargetModeInfoIndex(snapshot.Modes, out var targetIndex))
            {
                throw IncompleteModeException("source or target", path, snapshot.Modes.Length);
            }

            return new ActivePathObservation(
                SourceKey(path.SourceInfo.AdapterId, path.SourceInfo.Id), TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id),
                path.TargetInfo.TargetAvailable != 0, path.TargetInfo.Rotation, path.TargetInfo.Scaling,
                path.TargetInfo.RefreshRate, path.Flags & NativeDisplayConfigPathFlags.BoostRefreshRate,
                snapshot.Modes[sourceIndex].Mode.SourceMode,
                CaptureTargetSignal(snapshot.Modes[targetIndex].Mode.TargetMode.TargetVideoSignalInfo),
                CaptureDesktopImage(path, snapshot.Modes));
        }).ToArray();

    private sealed record ActivePathObservation(
        string Source, string Target, bool Available, uint Rotation, uint Scaling,
        NativeRational Refresh, uint Boost, NativeSourceMode SourceMode, DisplayTargetSignal TargetSignal,
        DisplayDesktopImage? DesktopImage);

    internal static DisplayOutputTechnology MapOutputTechnology(uint value) => value switch
    {
        0 => DisplayOutputTechnology.Hd15,
        4 => DisplayOutputTechnology.Dvi,
        5 => DisplayOutputTechnology.Hdmi,
        6 or 11 or 13 or 17 => DisplayOutputTechnology.Internal,
        10 or 12 => DisplayOutputTechnology.DisplayPort,
        15 => DisplayOutputTechnology.Wireless,
        0xFFFFFFFF => DisplayOutputTechnology.Other,
        _ => DisplayOutputTechnology.Unknown,
    };

    private static DisplayOrientation MapOrientation(uint value) => value switch
    {
        1 => DisplayOrientation.Landscape,
        2 => DisplayOrientation.Portrait,
        3 => DisplayOrientation.LandscapeFlipped,
        4 => DisplayOrientation.PortraitFlipped,
        _ => throw new DisplayCaptureException(0, "Windows returned an unknown display orientation."),
    };

    private static DisplayPathScaling MapScaling(uint value) => value switch
    {
        1 => DisplayPathScaling.Identity,
        2 => DisplayPathScaling.Centered,
        3 => DisplayPathScaling.Stretched,
        4 => DisplayPathScaling.AspectRatioCenteredMax,
        5 => DisplayPathScaling.Custom,
        128 => DisplayPathScaling.Preferred,
        _ => throw new DisplayCaptureException(0, "Windows returned an unknown display path scaling value."),
    };

    private static string SourceKey(NativeAdapterId adapterId, uint sourceId) => $"{adapterId.StableKey}:S{sourceId}";

    private static string TargetKey(NativeAdapterId adapterId, uint targetId) => $"{adapterId.StableKey}:T{targetId}";

    private static DisplayCaptureException IncompleteModeException(
        string kind,
        NativePathInfo path,
        int modeCount) => new(
            0,
            $"Windows returned an incomplete {kind} mode for an active display.",
            $"Path flags=0x{path.Flags:X8}; source mode index=0x{path.SourceInfo.ModeInfoIndex:X8}; " +
            $"target mode index=0x{path.TargetInfo.ModeInfoIndex:X8}; mode count={modeCount}.", isTransient: true);
}
