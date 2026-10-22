using MoniTopo.Core.Versioning;

namespace MoniTopo.Core.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("2026.1022.0", "2026.10.22")]
    [InlineData("2026.1022.1", "2026.10.22.1")]
    [InlineData("2026.720.0", "2026.07.20")]
    [InlineData("2026.720.1", "2026.07.20.1")]
    [InlineData("2027.205.0", "2027.02.05")]
    [InlineData("2027.1205.2", "2027.12.05.2")]
    [InlineData("2028.229.0", "2028.02.29")]
    public void PackageVersionFormatsForDisplay(string packageVersion, string displayVersion)
    {
        var parsed = ReleaseVersion.Parse(packageVersion);

        Assert.Equal(packageVersion, parsed.PackageVersion);
        Assert.Equal(displayVersion, parsed.DisplayVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026.720")]
    [InlineData("2026.0720.0")]
    [InlineData("2026.231.0")]
    [InlineData("1999.101.0")]
    [InlineData("2026.720.-1")]
    [InlineData("v2026.720.0")]
    [InlineData("2026.10.22")]
    [InlineData("2026.10.22.1")]
    [InlineData("2027.229.0")]
    public void InvalidPackageVersionsAreRejected(string value)
    {
        Assert.False(ReleaseVersion.TryParse(value, out _));
    }

    [Fact]
    public void OrderingUsesDateThenSameDayRevision()
    {
        var versions = new[]
        {
            ReleaseVersion.Parse("2027.101.0"),
            ReleaseVersion.Parse("2026.1231.0"),
            ReleaseVersion.Parse("2026.720.1"),
            ReleaseVersion.Parse("2026.720.0"),
        };

        Array.Sort(versions);

        Assert.Equal("2026.720.0", versions[0].PackageVersion);
        Assert.Equal("2026.720.1", versions[1].PackageVersion);
        Assert.Equal("2026.1231.0", versions[2].PackageVersion);
        Assert.Equal("2027.101.0", versions[3].PackageVersion);
    }
}
