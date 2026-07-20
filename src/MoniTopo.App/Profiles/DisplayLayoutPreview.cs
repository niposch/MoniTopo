using MoniTopo.Core.Models;

namespace MoniTopo.App.Profiles;

public sealed record DisplayPreviewItem(
    string DisplayId,
    string Label,
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsPrimary,
    string Details,
    int ZIndex);

public static class DisplayLayoutPreview
{
    public static IReadOnlyList<DisplayPreviewItem> Build(
        DisplayProfile profile,
        double availableWidth,
        double availableHeight,
        double padding = 12)
    {
        if (availableWidth <= padding * 2 || availableHeight <= padding * 2 || profile.Displays.Count == 0)
        {
            return [];
        }

        var left = profile.Displays.Min(display => display.Position.X);
        var top = profile.Displays.Min(display => display.Position.Y);
        var right = profile.Displays.Max(display => display.Position.X + display.SourceResolution.Width);
        var bottom = profile.Displays.Max(display => display.Position.Y + display.SourceResolution.Height);
        var contentWidth = Math.Max(1, right - left);
        var contentHeight = Math.Max(1, bottom - top);
        var scale = Math.Min(
            (availableWidth - padding * 2) / contentWidth,
            (availableHeight - padding * 2) / contentHeight);

        return profile.Displays.Select((display, index) => new DisplayPreviewItem(
            display.DisplayId,
            display.FriendlyLabel,
            padding + (display.Position.X - left) * scale + CloneOffset(profile, display, index),
            padding + (display.Position.Y - top) * scale + CloneOffset(profile, display, index),
            Math.Max(36, display.SourceResolution.Width * scale),
            Math.Max(24, display.SourceResolution.Height * scale),
            display.IsPrimary,
            $"{display.SourceResolution.Width} × {display.SourceResolution.Height}  {display.RefreshRate.Hertz:0.##} Hz  {display.WindowsUiScalePercent}%  HDR {(display.HdrEnabled ? "On" : "Off")}",
            index)).ToArray();
    }

    private static double CloneOffset(DisplayProfile profile, DesiredDisplayPath display, int index) =>
        display.CloneGroupId is null
            ? 0
            : profile.Displays.Take(index).Count(item => string.Equals(item.CloneGroupId, display.CloneGroupId, StringComparison.Ordinal)) * 5;
}
