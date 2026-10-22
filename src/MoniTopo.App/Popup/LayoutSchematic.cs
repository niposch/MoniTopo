using MoniTopo.Core.Models;

namespace MoniTopo.App.Popup;

public sealed record SchematicMonitor(double Left, double Top, double Width, double Height, bool IsPrimary)
{
    public double StrokeThickness => IsPrimary ? 2 : 1;
}

public static class LayoutSchematic
{
    public static IReadOnlyList<SchematicMonitor> Build(DisplayProfile profile, double width = 90, double height = 46)
    {
        const double padding = 3;
        if (profile.Displays.Count == 0 || width <= padding * 2 || height <= padding * 2) return [];
        var rectangles = profile.Displays.Select((display, index) =>
        {
            // Stagger cloned members so overlap is distinguishable without labels.
            var cloneIndex = profile.Displays.Take(index).Count(previous => previous.SourceGroupId == display.SourceGroupId);
            var offset = cloneIndex * Math.Min(display.SourceResolution.Width, display.SourceResolution.Height) * 0.07;
            return new SchematicMonitor(display.Position.X + offset, display.Position.Y + offset,
                display.SourceResolution.Width, display.SourceResolution.Height, display.IsPrimary);
        }).ToArray();
        var left = rectangles.Min(rectangle => rectangle.Left);
        var top = rectangles.Min(rectangle => rectangle.Top);
        var spanWidth = rectangles.Max(rectangle => rectangle.Left + rectangle.Width) - left;
        var spanHeight = rectangles.Max(rectangle => rectangle.Top + rectangle.Height) - top;
        if (spanWidth <= 0 || spanHeight <= 0) return [];
        var scale = Math.Min((width - padding * 2) / spanWidth, (height - padding * 2) / spanHeight);
        var originX = (width - spanWidth * scale) / 2;
        var originY = (height - spanHeight * scale) / 2;
        return rectangles.Select(rectangle => rectangle with
        {
            Left = originX + (rectangle.Left - left) * scale,
            Top = originY + (rectangle.Top - top) * scale,
            Width = rectangle.Width * scale,
            Height = rectangle.Height * scale,
        }).ToArray();
    }
}
