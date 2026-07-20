using MoniTopo.Windows.Shell;

namespace MoniTopo.Windows.Tests;

public sealed class TrayPopupPlacementTests
{
    [Theory]
    [InlineData(96u, 1582d, 692d)]
    [InlineData(144u, 1054.6666666666667d, 461.3333333333333d)]
    [InlineData(288u, 527.3333333333334d, 230.66666666666666d)]
    public void BottomTaskbarPlacementScalesPhysicalPixelsToDips(uint dpi, double expectedLeft, double expectedTop)
    {
        var placement = TrayPopupPlacement.Calculate(
            new PixelRect(1918, 1040, 1920, 1042),
            new PixelRect(0, 0, 1920, 1040),
            new PixelSize(330, 340),
            dpi);

        Assert.Equal(expectedLeft, placement.LeftDip, 8);
        Assert.Equal(expectedTop, placement.TopDip, 8);
    }

    [Fact]
    public void PlacementClampsToNegativeCoordinateWorkArea()
    {
        var placement = TrayPopupPlacement.Calculate(
            new PixelRect(-1918, 500, -1916, 502),
            new PixelRect(-1920, 0, 0, 1040),
            new PixelSize(500, 600),
            96);

        Assert.Equal(-1912, placement.LeftDip);
        Assert.Equal(432, placement.TopDip);
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1920, 1040, 1918, 1040)]
    [InlineData(0, 0, 1920, 1080, 0, 40, 1920, 1080, 1918, 38)]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1880, 1080, 1880, 1078)]
    [InlineData(0, 0, 1920, 1080, 40, 0, 1920, 1080, 38, 1078)]
    public void DefaultAnchorTracksTaskbarEdge(
        int screenLeft,
        int screenTop,
        int screenRight,
        int screenBottom,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        int expectedLeft,
        int expectedTop)
    {
        var anchor = TrayPopupPlacement.DefaultNotificationAnchor(
            new PixelRect(screenLeft, screenTop, screenRight, screenBottom),
            new PixelRect(workLeft, workTop, workRight, workBottom));

        Assert.Equal(expectedLeft, anchor.Left);
        Assert.Equal(expectedTop, anchor.Top);
    }
}
