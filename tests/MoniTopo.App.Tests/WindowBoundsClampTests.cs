using MoniTopo.App.Profiles;
using MoniTopo.Core.Configuration;

namespace MoniTopo.App.Tests;

public sealed class WindowBoundsClampTests
{
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
}
