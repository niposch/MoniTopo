using MoniTopo.App.Profiles;
using MoniTopo.Core.Configuration;

namespace MoniTopo.App.Tests;

public sealed class WindowBoundsClampTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(10, 20, -1, 620)]
    [InlineData(double.NaN, 20, 980, 620)]
    [InlineData(10, double.PositiveInfinity, 980, 620)]
    [InlineData(10, 20, double.NaN, 620)]
    public void InvalidSavedBoundsFallBackToCenteredDefault(double left, double top, double width, double height)
    {
        var result = WindowBoundsClamp.Clamp(new WindowBounds(left, top, width, height),
            [new WindowBounds(0, 0, 1280, 720)]);
        Assert.Equal(new WindowBounds(150, 50, 980, 620), result);
    }

    [Fact]
    public void OffscreenWindowMovesInsideAvailableWorkArea()
    {
        var result = WindowBoundsClamp.Clamp(
            new WindowBounds(4000, -900, 980, 620),
            [new WindowBounds(0, 0, 1280, 720)]);

        Assert.Equal(new WindowBounds(300, 0, 980, 620), result);
    }

    [Fact]
    public void OversizedWindowShrinksToEffectiveDesktop()
    {
        var result = WindowBoundsClamp.Clamp(
            new WindowBounds(-100, -100, 1600, 900),
            [new WindowBounds(0, 0, 1280, 720)]);

        Assert.Equal(new WindowBounds(0, 0, 1280, 720), result);
    }

    [Fact]
    public void IntersectingSecondaryWorkAreaIsRetained()
    {
        var result = WindowBoundsClamp.Clamp(
            new WindowBounds(-1700, 100, 900, 600),
            [new WindowBounds(0, 0, 1920, 1040), new WindowBounds(-1920, 0, 1920, 1040)]);

        Assert.Equal(new WindowBounds(-1700, 100, 900, 600), result);
    }

    [Fact]
    public void FullyOffscreenBoundsChooseNearestMonitorRatherThanEnumerationOrder()
    {
        var result = WindowBoundsClamp.Clamp(new WindowBounds(-3000, 80, 900, 600),
            [new WindowBounds(0, 0, 1920, 1040), new WindowBounds(-1920, 0, 1920, 1040)]);

        Assert.Equal(new WindowBounds(-1920, 80, 900, 600), result);
    }
}
