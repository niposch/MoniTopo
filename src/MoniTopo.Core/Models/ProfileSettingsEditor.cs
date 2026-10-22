namespace MoniTopo.Core.Models;

public sealed record DisplaySettingsEdit(string DisplayId, AvailableDisplayMode Mode, int ScalePercent, bool HdrEnabled);

public static class ProfileSettingsEditor
{
    public static DisplayProfile Apply(DisplayProfile profile, IReadOnlyList<DisplaySettingsEdit> edits,
        IReadOnlyList<DisplaySettingsCapability> capabilities)
    {
        if (edits.Count != profile.Displays.Count || edits.Select(edit => edit.DisplayId).Distinct(StringComparer.Ordinal).Count() != edits.Count)
            throw new InvalidOperationException("The display settings no longer match the profile. Reopen the editor.");
        var result = new List<DesiredDisplayPath>();
        foreach (var saved in profile.Displays)
        {
            var edit = edits.SingleOrDefault(edit => edit.DisplayId == saved.DisplayId)
                ?? throw new InvalidOperationException("A saved display is missing from the editor.");
            var available = capabilities.SingleOrDefault(capability => capability.DisplayId == saved.DisplayId);
            var oldMode = ModeOf(saved);
            var modeChanged = edit.Mode != oldMode;
            if (modeChanged && (available is null || !available.Modes.Contains(edit.Mode)))
                throw new InvalidOperationException($"The selected resolution/refresh rate is no longer available for {saved.FriendlyLabel}. Reopen the editor.");
            if (edit.ScalePercent != saved.WindowsUiScalePercent && (available is null || !available.ScalePercentages.Contains(edit.ScalePercent)))
                throw new InvalidOperationException($"The selected scale is no longer available for {saved.FriendlyLabel}. Reopen the editor.");
            if (edit.HdrEnabled != saved.HdrEnabled && (available is null || !available.HdrStates.Contains(edit.HdrEnabled)))
                throw new InvalidOperationException($"The selected HDR state is no longer available for {saved.FriendlyLabel}. Reopen the editor.");
            if ((modeChanged || edit.ScalePercent != saved.WindowsUiScalePercent) &&
                profile.Displays.Count(other => other.SourceGroupId == saved.SourceGroupId) > 1)
                throw new InvalidOperationException("Change cloned display modes and scale in Windows, then update this profile from the current setup.");
            var updated = saved with { WindowsUiScalePercent = edit.ScalePercent, HdrEnabled = edit.HdrEnabled };
            if (modeChanged)
            {
                updated = updated with
                {
                    SourceResolution = edit.Mode.Resolution,
                    RefreshRate = edit.Mode.RefreshRate.Normalize(),
                    ModeRequest = new DisplayModeRequest(edit.Mode.ScanLineOrdering),
                    TargetSignal = null,
                    DesktopImage = null,
                    BoostRefreshRate = false,
                    PathScaling = DisplayPathScaling.Preferred,
                };
            }
            result.Add(updated);
        }
        var arranged = PreserveArrangement(profile.Displays, result);
        return profile with
        {
            Displays = arranged,
            CaptureSchemaVersion = result.Any(display => display.ModeRequest is not null) ? Math.Max(5, profile.CaptureSchemaVersion) : profile.CaptureSchemaVersion,
        };
    }

    public static AvailableDisplayMode ModeOf(DesiredDisplayPath display) => new(display.SourceResolution,
        display.RefreshRate.Normalize(), display.ModeRequest?.ScanLineOrdering ?? display.TargetSignal?.ScanLineOrdering ?? 1);

    private static DesiredDisplayPath[] PreserveArrangement(IReadOnlyList<DesiredDisplayPath> original, List<DesiredDisplayPath> edited)
    {
        if (original.Zip(edited).All(pair => pair.First.SourceResolution == pair.Second.SourceResolution)) return edited.ToArray();
        var old = original.GroupBy(display => display.SourceGroupId).Select(group => group.FirstOrDefault(display => display.IsPrimary) ?? group.First()).ToArray();
        var sizes = edited.GroupBy(display => display.SourceGroupId).ToDictionary(group => group.Key, group => group.First().SourceResolution);
        var primary = old.Single(display => display.IsPrimary);
        var positions = new Dictionary<string, DisplayPoint>(StringComparer.Ordinal) { [primary.SourceGroupId] = primary.Position };
        var pending = new Queue<DesiredDisplayPath>();
        pending.Enqueue(primary);
        while (pending.TryDequeue(out var anchor))
        {
            foreach (var neighbor in old.Where(display => !positions.ContainsKey(display.SourceGroupId)))
            {
                if (PlaceNeighbor(anchor, neighbor, positions[anchor.SourceGroupId], sizes[anchor.SourceGroupId], sizes[neighbor.SourceGroupId]) is not { } position) continue;
                positions.Add(neighbor.SourceGroupId, position);
                pending.Enqueue(neighbor);
            }
        }
        foreach (var display in old) positions.TryAdd(display.SourceGroupId, display.Position);
        var arranged = edited.Select(display => display with { Position = positions[display.SourceGroupId] }).ToArray();
        for (var left = 0; left < old.Length; left++)
        {
            for (var right = left + 1; right < old.Length; right++)
            {
                var a = old[left]; var b = old[right];
                var pointA = positions[a.SourceGroupId]; var pointB = positions[b.SourceGroupId];
                var sizeA = sizes[a.SourceGroupId]; var sizeB = sizes[b.SourceGroupId];
                var overlaps = pointA.X < (long)pointB.X + sizeB.Width && (long)pointA.X + sizeA.Width > pointB.X &&
                    pointA.Y < (long)pointB.Y + sizeB.Height && (long)pointA.Y + sizeA.Height > pointB.Y;
                var expected = PlaceNeighbor(a, b, pointA, sizeA, sizeB);
                if (overlaps || expected is { } expectedPoint && expectedPoint != pointB)
                    throw new InvalidOperationException("That resolution cannot preserve this monitor arrangement. Configure it in Windows, then update the profile from the current setup.");
            }
        }
        return arranged;
    }

    private static DisplayPoint? PlaceNeighbor(DesiredDisplayPath a, DesiredDisplayPath b, DisplayPoint point, DisplaySize sizeA, DisplaySize sizeB)
    {
        var offsetX = b.Position.X - a.Position.X;
        var offsetY = b.Position.Y - a.Position.Y;
        var alignedX = a.Position.X == b.Position.X ? point.X :
            (long)a.Position.X + a.SourceResolution.Width == (long)b.Position.X + b.SourceResolution.Width ? checked(point.X + sizeA.Width - sizeB.Width) : checked(point.X + offsetX);
        var alignedY = a.Position.Y == b.Position.Y ? point.Y :
            (long)a.Position.Y + a.SourceResolution.Height == (long)b.Position.Y + b.SourceResolution.Height ? checked(point.Y + sizeA.Height - sizeB.Height) : checked(point.Y + offsetY);
        if (a.Position.Y < (long)b.Position.Y + b.SourceResolution.Height && (long)a.Position.Y + a.SourceResolution.Height > b.Position.Y)
        {
            if ((long)a.Position.X + a.SourceResolution.Width == b.Position.X) return new DisplayPoint(checked(point.X + sizeA.Width), alignedY);
            if ((long)b.Position.X + b.SourceResolution.Width == a.Position.X) return new DisplayPoint(checked(point.X - sizeB.Width), alignedY);
        }
        if (a.Position.X < (long)b.Position.X + b.SourceResolution.Width && (long)a.Position.X + a.SourceResolution.Width > b.Position.X)
        {
            if ((long)a.Position.Y + a.SourceResolution.Height == b.Position.Y) return new DisplayPoint(alignedX, checked(point.Y + sizeA.Height));
            if ((long)b.Position.Y + b.SourceResolution.Height == a.Position.Y) return new DisplayPoint(alignedX, checked(point.Y - sizeB.Height));
        }
        return null;
    }
}
