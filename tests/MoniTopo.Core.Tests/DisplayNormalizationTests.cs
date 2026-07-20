using MoniTopo.Core.Models;
using MoniTopo.Core.Normalization;

namespace MoniTopo.Core.Tests;

public sealed class DisplayNormalizationTests
{
    [Fact]
    public void RefreshRateReducesToLowestTerms()
    {
        Assert.Equal(new RefreshRate(60, 1), new RefreshRate(60000, 1000).Normalize());
        Assert.Equal(new RefreshRate(60000, 1001), new RefreshRate(60000, 1001).Normalize());
    }

    [Fact]
    public void CommonFractionalAndRoundedRatesAreEquivalent()
    {
        Assert.True(new RefreshRate(60000, 1001).IsEquivalentTo(new RefreshRate(60, 1)));
        Assert.False(new RefreshRate(50, 1).IsEquivalentTo(new RefreshRate(60, 1)));
    }

    [Fact]
    public void ProfileCoordinatesAreCanonicalizedAroundPrimary()
    {
        var displays = new[]
        {
            TestData.Display("left", primary: false, new DisplayPoint(-1920, 200)),
            TestData.Display("primary", primary: true, new DisplayPoint(100, 300)),
        };
        var profile = TestData.Profile(displays: displays, primaryDisplayId: "primary") with { Name = "  Desk  " };

        var normalized = ProfileNormalizer.Normalize(profile);

        Assert.Equal("Desk", normalized.Name);
        Assert.Equal(new DisplayPoint(-2020, -100), normalized.Displays[0].Position);
        Assert.Equal(new DisplayPoint(0, 0), normalized.Displays[1].Position);
        Assert.Equal(new RefreshRate(60, 1), normalized.Displays[1].RefreshRate);
    }
}
