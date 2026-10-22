using MoniTopo.Core.Activation;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Models;

namespace MoniTopo.Windows.Display;

internal static class DisplayConfigurationPlanBuilder
{
    private const uint SourceModeType = 1;
    private const uint TargetModeType = 2;

    internal static void ValidateProfileCapture(DisplayProfile profile)
    {
        if (profile.CaptureSchemaVersion < DisplayProfileCaptureSchema.MinimumActivatable)
        {
            throw new ActivationFailureException("activation.profile.capture-outdated",
                $"{profile.Name} was captured before MoniTopo could distinguish the Windows HDR setting reliably. Update the profile from the current setup before activating it.");
        }
        foreach (var display in profile.Displays)
        {
            if (display.TargetSignal is null && display.ModeRequest is null)
            {
                throw new ActivationFailureException("activation.profile.target-signal-missing",
                    $"{display.FriendlyLabel} was captured by an older MoniTopo version. Update the profile from the current setup before activating it.");
            }
        }
        foreach (var group in profile.Displays.GroupBy(display => display.SourceGroupId, StringComparer.Ordinal))
        {
            EnsureConsistentSourceMode(group.ToArray());
            if (group.Any(display => display.WindowsUiScalePercent != group.First().WindowsUiScalePercent))
            {
                throw new ActivationFailureException("activation.scaling.clone-conflict", "Cloned displays must use the same Windows display scale.");
            }
        }
    }

    internal static NativeDisplaySnapshot Build(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        NativeDisplaySnapshot availableConfiguration,
        bool forceVirtualModeIndexes = false,
        int sourceRoutingIndex = 0)
    {
        ValidateProfileCapture(profile);

        var bindingByDisplay = bindings.ToDictionary(binding => binding.DisplayId, StringComparer.OrdinalIgnoreCase);
        var targetPaths = availableConfiguration.Paths
            .Where(path => path.TargetInfo.TargetAvailable != 0)
            .GroupBy(path => TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var groups = profile.Displays
            .GroupBy(display => display.SourceGroupId, StringComparer.Ordinal)
            .OrderByDescending(group => group.Any(display => display.IsPrimary))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => CreateSourceGroup(group, bindingByDisplay, targetPaths))
            .ToArray();
        var sourceAssignments = AssignUniqueSources(groups, sourceRoutingIndex);

        var modes = new List<NativeModeInfo>(groups.Length + profile.Displays.Count);
        var sourceModeIndexes = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var representative = group.Displays[0];
            EnsureConsistentSourceMode(group.Displays);
            var source = sourceAssignments[group.SourceGroupId];
            sourceModeIndexes[group.SourceGroupId] = checked((uint)modes.Count);
            modes.Add(new NativeModeInfo
            {
                InfoType = SourceModeType,
                Id = source.Id,
                AdapterId = source.AdapterId,
                Mode = new NativeModeUnion
                {
                    SourceMode = new NativeSourceMode
                    {
                        Width = checked((uint)representative.SourceResolution.Width),
                        Height = checked((uint)representative.SourceResolution.Height),
                        PixelFormat = 4,
                        Position = new NativePoint(representative.Position.X, representative.Position.Y),
                    },
                },
            });
        }

        var paths = new List<NativePathInfo>(profile.Displays.Count);
        foreach (var display in profile.Displays)
        {
            var binding = bindingByDisplay[display.DisplayId];
            var source = sourceAssignments[display.SourceGroupId];
            var availablePath = targetPaths[binding.RuntimeId]
                .First(path => SameSource(path.SourceInfo, source));
            // A mode request supplies exact source size/rate, not fabricated target
            // blanking/pixel clocks. Windows completes only this missing timing.
            var targetModeIndex = uint.MaxValue;
            if (display.TargetSignal is { } targetSignal)
            {
                targetModeIndex = checked((uint)modes.Count);
                modes.Add(new NativeModeInfo
                {
                    InfoType = TargetModeType,
                    Id = availablePath.TargetInfo.Id,
                    AdapterId = availablePath.TargetInfo.AdapterId,
                    Mode = new NativeModeUnion
                    {
                        TargetMode = new NativeTargetMode
                        {
                            TargetVideoSignalInfo = new NativeVideoSignalInfo
                            {
                                PixelRate = targetSignal.PixelRate,
                                HorizontalSyncFrequency = ToNative(targetSignal.HorizontalSyncFrequency),
                                VerticalSyncFrequency = ToNative(targetSignal.VerticalSyncFrequency),
                                ActiveSize = ToNative(targetSignal.ActiveSize),
                                TotalSize = ToNative(targetSignal.TotalSize),
                                VideoStandard = targetSignal.VideoStandard,
                                ScanLineOrdering = targetSignal.ScanLineOrdering,
                            },
                        },
                    },
                });
            }

            var sourceModeIndex = sourceModeIndexes[display.SourceGroupId];
            var useVirtualModeIndexes = forceVirtualModeIndexes || availablePath.SupportsVirtualMode || display.DesktopImage is not null;
            var desktopModeIndex = NativeDisplayConfigModeIndexes.InvalidVirtualIndex;
            if (display.DesktopImage is { } desktop)
            {
                desktopModeIndex = checked((uint)modes.Count);
                modes.Add(new NativeModeInfo
                {
                    InfoType = 3,
                    Id = availablePath.TargetInfo.Id,
                    AdapterId = availablePath.TargetInfo.AdapterId,
                    Mode = new NativeModeUnion
                    {
                        DesktopImageInfo = new NativeDesktopImageInfo
                        {
                            PathSourceSize = new NativePoint(desktop.PathSourceSize.Width, desktop.PathSourceSize.Height),
                            DesktopImageRegion = new NativeRect(desktop.Region.Left, desktop.Region.Top, desktop.Region.Right, desktop.Region.Bottom),
                            DesktopImageClip = new NativeRect(desktop.Clip.Left, desktop.Clip.Top, desktop.Clip.Right, desktop.Clip.Bottom),
                        },
                    },
                });
            }

            paths.Add(new NativePathInfo
            {
                SourceInfo = new NativePathSourceInfo
                {
                    AdapterId = source.AdapterId,
                    Id = source.Id,
                    ModeInfoIndex = useVirtualModeIndexes
                        ? NativeDisplayConfigModeIndexes.EncodeVirtual(sourceModeIndex)
                        : sourceModeIndex,
                },
                TargetInfo = new NativePathTargetInfo
                {
                    AdapterId = availablePath.TargetInfo.AdapterId,
                    Id = availablePath.TargetInfo.Id,
                    ModeInfoIndex = useVirtualModeIndexes
                        ? (targetModeIndex == uint.MaxValue ? 0xFFFF0000u : NativeDisplayConfigModeIndexes.EncodeVirtual(targetModeIndex) & 0xFFFF0000u) | desktopModeIndex
                        : targetModeIndex,
                    OutputTechnology = availablePath.TargetInfo.OutputTechnology,
                    Rotation = ToNative(display.Orientation),
                    Scaling = ToNative(display.PathScaling),
                    RefreshRate = ToNative(display.RefreshRate),
                    ScanLineOrdering = display.ModeRequest?.ScanLineOrdering ?? display.TargetSignal!.ScanLineOrdering,
                    TargetAvailable = 1,
                },
                Flags = NativeDisplayConfigPathFlags.Active |
                    (availablePath.Flags & NativeDisplayConfigPathFlags.SupportVirtualMode) |
                    ((display.BoostRefreshRate ?? (availablePath.Flags & NativeDisplayConfigPathFlags.BoostRefreshRate) != 0)
                        ? NativeDisplayConfigPathFlags.BoostRefreshRate : 0),
            });
        }

        return new NativeDisplaySnapshot(paths.ToArray(), modes.ToArray());
    }

    private static SourceGroup CreateSourceGroup(
        IGrouping<string, DesiredDisplayPath> displayGroup,
        Dictionary<string, ResolvedIdentityBinding> bindingByDisplay,
        Dictionary<string, NativePathInfo[]> targetPaths)
    {
        var displays = displayGroup.ToArray();
        HashSet<string>? commonSources = null;
        var sourceByKey = new Dictionary<string, NativePathSourceInfo>(StringComparer.Ordinal);
        foreach (var display in displays)
        {
            if (!bindingByDisplay.TryGetValue(display.DisplayId, out var binding) ||
                !targetPaths.TryGetValue(binding.RuntimeId, out var paths))
            {
                throw new ActivationFailureException(
                    "activation.topology.target-unavailable",
                    $"Windows no longer exposes a usable path to {display.FriendlyLabel}.");
            }

            var displaySources = paths
                .Select(path => path.SourceInfo)
                .DistinctBy(SourceKey)
                .ToArray();
            foreach (var source in displaySources)
            {
                sourceByKey[SourceKey(source)] = source;
            }

            var keys = displaySources.Select(SourceKey).ToHashSet(StringComparer.Ordinal);
            if (commonSources is null)
            {
                commonSources = keys;
            }
            else
            {
                commonSources.IntersectWith(keys);
            }
        }

        var savedSourceId = TryParseSavedSourceId(displayGroup.Key);
        var candidates = commonSources?
            .Select(key => sourceByKey[key])
            .OrderByDescending(source => string.Equals(SourceKey(source), displayGroup.Key, StringComparison.Ordinal))
            .ThenByDescending(source => savedSourceId is uint id && source.Id == id)
            .ThenBy(SourceKey, StringComparer.Ordinal)
            .ToArray() ?? [];
        if (candidates.Length == 0)
        {
            throw new ActivationFailureException(
                "activation.topology.no-common-source",
                $"Windows cannot construct the saved clone/source relationship for {displays[0].FriendlyLabel}.");
        }

        return new SourceGroup(displayGroup.Key, displays, candidates);
    }

    private static Dictionary<string, NativePathSourceInfo> AssignUniqueSources(SourceGroup[] groups, int routingIndex)
    {
        var assignments = new Dictionary<string, NativePathSourceInfo>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var visited = 0;
        if (!Assign(0))
        {
            throw new ActivationFailureException(
                "activation.topology.sources-unavailable",
                "Windows does not expose enough independent display sources for this profile.");
        }

        return assignments;

        bool Assign(int index)
        {
            if (++visited > 4096)
            {
                return false;
            }

            if (index == groups.Length)
            {
                return routingIndex-- == 0;
            }

            var group = groups[index];
            foreach (var candidate in group.Candidates)
            {
                var key = SourceKey(candidate);
                if (!used.Add(key))
                {
                    continue;
                }

                assignments[group.SourceGroupId] = candidate;
                if (Assign(index + 1))
                {
                    return true;
                }

                assignments.Remove(group.SourceGroupId);
                used.Remove(key);
            }

            return false;
        }
    }

    private static void EnsureConsistentSourceMode(DesiredDisplayPath[] displays)
    {
        var first = displays[0];
        if (displays.Skip(1).Any(display =>
                display.SourceResolution != first.SourceResolution ||
                display.Position != first.Position))
        {
            throw new ActivationFailureException(
                "activation.profile.clone-invalid",
                $"The saved clone group containing {first.FriendlyLabel} has inconsistent source settings.");
        }
    }

    private static bool SameSource(NativePathSourceInfo left, NativePathSourceInfo right) =>
        left.AdapterId == right.AdapterId && left.Id == right.Id;

    private static string SourceKey(NativePathSourceInfo source) => $"{source.AdapterId.StableKey}:S{source.Id}";

    private static uint? TryParseSavedSourceId(string sourceGroupId)
    {
        var separator = sourceGroupId.LastIndexOf(":S", StringComparison.Ordinal);
        return separator >= 0 &&
            uint.TryParse(
                sourceGroupId.AsSpan(separator + 2),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var sourceId)
            ? sourceId
            : null;
    }

    private static string TargetKey(NativeAdapterId adapterId, uint targetId) => $"{adapterId.StableKey}:T{targetId}";

    private static NativeRational ToNative(RefreshRate value) => new(
        checked((uint)value.Numerator),
        checked((uint)value.Denominator));

    private static NativeRegion ToNative(DisplaySize value) => new(
        checked((uint)value.Width),
        checked((uint)value.Height));

    private static uint ToNative(DisplayOrientation value) => checked((uint)value + 1);

    private static uint ToNative(DisplayPathScaling value) => value switch
    {
        DisplayPathScaling.Identity => 1,
        DisplayPathScaling.Centered => 2,
        DisplayPathScaling.Stretched => 3,
        DisplayPathScaling.AspectRatioCenteredMax => 4,
        DisplayPathScaling.Custom => 5,
        DisplayPathScaling.Preferred => 128,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private sealed record SourceGroup(
        string SourceGroupId,
        DesiredDisplayPath[] Displays,
        NativePathSourceInfo[] Candidates);
}
