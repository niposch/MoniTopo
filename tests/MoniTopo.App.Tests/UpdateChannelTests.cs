using MoniTopo.App.Updates;

namespace MoniTopo.App.Tests;

public sealed class UpdateChannelTests
{
    [Fact]
    public void ProductionUpdateSourceExcludesPrereleases()
    {
        var source = VelopackUpdateClient.CreateUpdateSource();
        Assert.False(source.Prerelease);
    }
}
