using System.Runtime.InteropServices;

namespace MoniTopo.Windows.Shell;

public static class DesktopWindowPlacement
{
    public static uint GetDpi(PixelPoint point)
    {
        var monitor = PlacementNative.MonitorFromPoint(point, 2);
        return PlacementNative.GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x != 0 ? x : 96;
    }

    public static void MoveWindow(nint window, PixelPoint position) =>
        PlacementNative.SetWindowPos(window, nint.Zero, position.X, position.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
}

internal static partial class PlacementNative
{
    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(PixelPoint point, uint flags);

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
