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

        Assert.Equal("21.07.26", identity.DisplayVersion);
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

        Assert.Equal("08.08.26 dev", identity.DisplayVersion);
        Assert.Equal("2026.808.0", identity.PackageVersion);
        Assert.Equal("development", identity.Channel);
        Assert.Contains("file=2026.808.0.0", identity.DiagnosticText, StringComparison.Ordinal);
    }
}
