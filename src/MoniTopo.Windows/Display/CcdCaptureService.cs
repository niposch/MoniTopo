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
    private const uint SourceModeType = 1;
    private const uint TargetModeType = 2;
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

    public Task<CapturedDisplaySnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = _native.Query(QueryAllPaths | QueryVirtualModeAware | QueryVirtualRefreshRateAware);
        var availablePaths = raw.Paths.Where(path => path.TargetInfo.TargetAvailable != 0).ToArray();
        var uniqueTargets = availablePaths
            .GroupBy(path => TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        var targetData = uniqueTargets.ToDictionary(
            path => TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id),
            path =>
            {
                var name = _native.GetTargetName(path.TargetInfo.AdapterId, path.TargetInfo.Id);
                var preferred = _native.GetTargetPreferredMode(path.TargetInfo.AdapterId, path.TargetInfo.Id);
                var preferredSize = preferred.Width > 0 && preferred.Height > 0
                    ? new DisplaySize(checked((int)preferred.Width), checked((int)preferred.Height))
                    : (DisplaySize?)null;
                return (Name: name, Identity: _identityProvider.Resolve(name, preferredSize));
            },
            StringComparer.Ordinal);

        var activeNativePaths = raw.Paths.Where(path => (path.Flags & ActivePath) != 0).ToArray();
        if (activeNativePaths.Length == 0)
        {
            throw new DisplayCaptureException(0, "No active desktop displays were found in the interactive session.");
        }

        var primarySources = activeNativePaths
            .Select(path => path.SourceInfo)
            .DistinctBy(source => SourceKey(source.AdapterId, source.Id))
            .Where(source => _native.IsPrimaryGdiDevice(_native.GetSourceName(source.AdapterId, source.Id)))
            .Select(source => SourceKey(source.AdapterId, source.Id))
            .ToHashSet(StringComparer.Ordinal);
        if (primarySources.Count == 0)
        {
            throw new DisplayCaptureException(0, "Windows did not identify a primary display source.");
        }

        var sourceCounts = activeNativePaths
            .GroupBy(path => SourceKey(path.SourceInfo.AdapterId, path.SourceInfo.Id), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var primaryAssigned = false;
        var activePaths = new List<DesiredDisplayPath>(activeNativePaths.Length);
        for (var index = 0; index < activeNativePaths.Length; index++)
        {
            var path = activeNativePaths[index];
            var sourceKey = SourceKey(path.SourceInfo.AdapterId, path.SourceInfo.Id);
            var sourceModeIndex = checked((int)path.SourceInfo.SourceModeInfoIndex);
            if (sourceModeIndex < 0 || sourceModeIndex >= raw.Modes.Length || raw.Modes[sourceModeIndex].InfoType != SourceModeType)
            {
                throw new DisplayCaptureException(0, "Windows returned an incomplete source mode for an active display.");
            }

            var targetModeIndex = checked((int)path.TargetInfo.TargetModeInfoIndex);
            if (targetModeIndex < 0 || targetModeIndex >= raw.Modes.Length || raw.Modes[targetModeIndex].InfoType != TargetModeType)
            {
                throw new DisplayCaptureException(0, "Windows returned an incomplete target signal for an active display.");
            }

            var scale = _scaleService.Query(new DisplaySourceAddress(
                path.SourceInfo.AdapterId.LowPart,
                path.SourceInfo.AdapterId.HighPart,
                path.SourceInfo.Id));
            if (!scale.IsSupported || scale.CurrentPercent is null)
            {
                throw new DisplayCaptureException(0, scale.ErrorMessage ?? "Windows display scaling could not be captured.");
            }

            var color = _native.GetAdvancedColorInfo(path.TargetInfo.AdapterId, path.TargetInfo.Id);
            var targetKey = TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id);
            var target = targetData[targetKey];
            var sourceMode = raw.Modes[sourceModeIndex].Mode.SourceMode;
            var isPrimary = !primaryAssigned && primarySources.Contains(sourceKey);
            primaryAssigned |= isPrimary;
            var displayId = $"display-{index + 1}";
            var targetSignal = raw.Modes[targetModeIndex].Mode.TargetMode.TargetVideoSignalInfo;
            activePaths.Add(new DesiredDisplayPath(
                DisplayId: displayId,
                Identity: target.Identity,
                SourceGroupId: sourceKey,
                CloneGroupId: sourceCounts[sourceKey] > 1 ? $"clone-{sourceKey}" : null,
                Position: new DisplayPoint(sourceMode.Position.X, sourceMode.Position.Y),
                SourceResolution: new DisplaySize(checked((int)sourceMode.Width), checked((int)sourceMode.Height)),
                RefreshRate: new RefreshRate(path.TargetInfo.RefreshRate.Numerator, path.TargetInfo.RefreshRate.Denominator),
                Orientation: MapOrientation(path.TargetInfo.Rotation),
                PathScaling: MapScaling(path.TargetInfo.Scaling),
                WindowsUiScalePercent: scale.CurrentPercent.Value,
                HdrEnabled: color.IsSupported && color.IsEnabled && !color.IsForceDisabled,
                IsPrimary: isPrimary,
                FriendlyLabel: string.IsNullOrWhiteSpace(target.Name.FriendlyName) ? $"Display {index + 1}" : target.Name.FriendlyName)
            {
                TargetSignal = new DisplayTargetSignal(
                    targetSignal.PixelRate,
                    new RefreshRate(targetSignal.HorizontalSyncFrequency.Numerator, targetSignal.HorizontalSyncFrequency.Denominator),
                    new RefreshRate(targetSignal.VerticalSyncFrequency.Numerator, targetSignal.VerticalSyncFrequency.Denominator),
                    new DisplaySize(checked((int)targetSignal.ActiveSize.Width), checked((int)targetSignal.ActiveSize.Height)),
                    new DisplaySize(checked((int)targetSignal.TotalSize.Width), checked((int)targetSignal.TotalSize.Height)),
                    targetSignal.VideoStandard,
                    targetSignal.ScanLineOrdering),
            });
        }

        var activeByTarget = activeNativePaths
            .Select((path, index) => (Key: TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id), Path: activePaths[index]))
            .ToDictionary(item => item.Key, item => item.Path, StringComparer.Ordinal);
        var connected = uniqueTargets.Select((path, index) =>
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
        return Task.FromResult(new CapturedDisplaySnapshot(activePaths, connected, primaryId, _timeProvider.GetUtcNow()));
    }

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
}
