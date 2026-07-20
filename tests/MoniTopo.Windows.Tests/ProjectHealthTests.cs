namespace MoniTopo.Windows.Tests;

public sealed class ProjectHealthTests
{
    [Fact]
    public void RealDisplayMutationOptInIsNotSetInTests()
    {
        Assert.NotEqual("1", Environment.GetEnvironmentVariable("MONITOPO_ALLOW_REAL_DISPLAY_CHANGES"));
    }
}
