using MoniTopo.Core.Configuration;

namespace MoniTopo.App.Profiles;

public static class WindowBoundsClamp
{
    public static WindowBounds Clamp(WindowBounds saved, IReadOnlyList<WindowBounds> workAreas)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(saved.Width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(saved.Height, 0);
        if (workAreas.Count == 0)
        {
            throw new ArgumentException("At least one work area is required.", nameof(workAreas));
        }

        var workArea = workAreas
            .OrderByDescending(area => IntersectionArea(saved, area))
            .First();
        var width = Math.Min(saved.Width, workArea.Width);
        var height = Math.Min(saved.Height, workArea.Height);
        var left = Math.Clamp(saved.Left, workArea.Left, workArea.Left + workArea.Width - width);
        var top = Math.Clamp(saved.Top, workArea.Top, workArea.Top + workArea.Height - height);
        return new WindowBounds(left, top, width, height);
    }

    private static double IntersectionArea(WindowBounds left, WindowBounds right)
    {
        var width = Math.Max(0, Math.Min(left.Left + left.Width, right.Left + right.Width) - Math.Max(left.Left, right.Left));
        var height = Math.Max(0, Math.Min(left.Top + left.Height, right.Top + right.Height) - Math.Max(left.Top, right.Top));
        return width * height;
    }
}
