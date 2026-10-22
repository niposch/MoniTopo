using MoniTopo.App.Diagnostics;

namespace MoniTopo.App.Tests;

public sealed class ApplicationBuildInfoTests
{
    [Fact]
    public void InstalledBuildUsesVelopackPackageVersion()
    {
        var identity = ApplicationBuildInfo.Resolve(
            "2026.721.0",
            isInstalled: true,
            "2026.808.0",
            "1.0.0.0",
            "2026.808.0.0");

        Assert.Equal("2026.07.21", identity.DisplayVersion);
        Assert.Equal("2026.721.0", identity.PackageVersion);
        Assert.Equal("installed", identity.Channel);
    }

    [Fact]
    public void DevelopmentBuildUsesAssemblyPackageVersionWithoutCommitSuffix()
    {
        var identity = ApplicationBuildInfo.Resolve(
            installedPackageVersion: null,
            isInstalled: false,
            "2026.808.0+abcdef",
            "1.0.0.0",
            "2026.808.0.0");

        Assert.Equal("2026.08.08 dev", identity.DisplayVersion);
        Assert.Equal("2026.808.0", identity.PackageVersion);
        Assert.Equal("development", identity.Channel);
        Assert.Contains("file=2026.808.0.0", identity.DiagnosticText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "2026.10.22")]
    [InlineData(false, "2026.10.22 dev")]
    public void FirstReleaseLabelDoesNotChangePackageOrFileVersion(bool installed, string displayVersion)
    {
        var identity = ApplicationBuildInfo.Resolve(
            installed ? "2026.1022.0" : null, installed, "2026.1022.0", "1.0.0.0", "2026.1022.0.0");

        Assert.Equal(displayVersion, identity.DisplayVersion);
        Assert.Equal("2026.1022.0", identity.PackageVersion);
        Assert.Equal("2026.1022.0.0", identity.FileVersion);
    }
}
