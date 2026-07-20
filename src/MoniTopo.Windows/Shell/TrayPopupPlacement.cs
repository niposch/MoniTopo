namespace MoniTopo.Windows.Shell;

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelSize(int Width, int Height);

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

public readonly record struct PopupPlacement(double LeftDip, double TopDip);

public static class TrayPopupPlacement
{
    public static PopupPlacement Calculate(
        PixelRect anchor,
        PixelRect workArea,
        PixelSize popupSize,
        uint dpi,
        int marginPixels = 8)
    {
        if (dpi == 0 || popupSize.Width <= 0 || popupSize.Height <= 0 || workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), "Placement requires positive dimensions and DPI.");
        }

        var left = anchor.Right - popupSize.Width;
        var minimumLeft = workArea.Left + marginPixels;
        var maximumLeft = Math.Max(minimumLeft, workArea.Right - popupSize.Width - marginPixels);
        left = Math.Clamp(left, minimumLeft, maximumLeft);
        var above = anchor.Top - popupSize.Height - marginPixels;
        var below = anchor.Bottom + marginPixels;
        var top = above >= workArea.Top + marginPixels ? above : below;
        var minimumTop = workArea.Top + marginPixels;
        var maximumTop = Math.Max(minimumTop, workArea.Bottom - popupSize.Height - marginPixels);
        top = Math.Clamp(top, minimumTop, maximumTop);
        var scale = 96d / dpi;
        return new PopupPlacement(left * scale, top * scale);
    }

    public static PixelRect DefaultNotificationAnchor(PixelRect screenBounds, PixelRect workArea)
    {
        const int anchorSize = 2;
        if (workArea.Bottom < screenBounds.Bottom)
        {
            return new PixelRect(workArea.Right - anchorSize, workArea.Bottom, workArea.Right, workArea.Bottom + anchorSize);
        }

        if (workArea.Top > screenBounds.Top)
        {
            return new PixelRect(workArea.Right - anchorSize, workArea.Top - anchorSize, workArea.Right, workArea.Top);
        }

        if (workArea.Right < screenBounds.Right)
        {
            return new PixelRect(workArea.Right, workArea.Bottom - anchorSize, workArea.Right + anchorSize, workArea.Bottom);
        }

        return new PixelRect(workArea.Left - anchorSize, workArea.Bottom - anchorSize, workArea.Left, workArea.Bottom);
    }
}
