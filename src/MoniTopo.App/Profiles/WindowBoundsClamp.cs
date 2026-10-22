using MoniTopo.Core.Configuration;

namespace MoniTopo.App.Profiles;

public static class WindowBoundsClamp
{
    public static WindowBounds Clamp(WindowBounds saved, IReadOnlyList<WindowBounds> workAreas)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        var available = workAreas.Where(IsUsable).ToArray();
        if (available.Length == 0)
        {
            throw new ArgumentException("At least one work area is required.", nameof(workAreas));
        }

        if (!IsUsable(saved))
        {
            // Older tray-only shutdowns could persist ActualWidth/Height before
            // the window was ever shown. Window placement is optional state, not
            // a reason to reject a valid configuration or fail startup.
            var primary = available[0];
            var fallbackWidth = Math.Min(980, primary.Width);
            var fallbackHeight = Math.Min(620, primary.Height);
            saved = new WindowBounds(primary.Left + (primary.Width - fallbackWidth) / 2,
                primary.Top + (primary.Height - fallbackHeight) / 2, fallbackWidth, fallbackHeight);
        }

        var workArea = available
            .OrderByDescending(area => IntersectionArea(saved, area))
            .ThenBy(area => DistanceSquared(saved, area))
            .First();
        var width = Math.Min(saved.Width, workArea.Width);
        var height = Math.Min(saved.Height, workArea.Height);
        var left = Math.Clamp(saved.Left, workArea.Left, workArea.Left + workArea.Width - width);
        var top = Math.Clamp(saved.Top, workArea.Top, workArea.Top + workArea.Height - height);
        return new WindowBounds(left, top, width, height);
    }

    public static bool IsUsable(WindowBounds bounds) =>
        double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        bounds.Width > 0 && bounds.Height > 0;

    private static double IntersectionArea(WindowBounds left, WindowBounds right)
    {
        var width = Math.Max(0, Math.Min(left.Left + left.Width, right.Left + right.Width) - Math.Max(left.Left, right.Left));
        var height = Math.Max(0, Math.Min(left.Top + left.Height, right.Top + right.Height) - Math.Max(left.Top, right.Top));
        return width * height;
    }

    private static double DistanceSquared(WindowBounds saved, WindowBounds area)
    {
        var x = Math.Max(0, Math.Max(area.Left - saved.Left - saved.Width, saved.Left - area.Left - area.Width));
        var y = Math.Max(0, Math.Max(area.Top - saved.Top - saved.Height, saved.Top - area.Top - area.Height));
        return x * x + y * y;
    }
}
