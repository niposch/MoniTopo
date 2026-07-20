namespace MoniTopo.App.Tests;

public sealed class ProjectHealthTests
{
    [Fact]
    public void AppAssemblyUsesWindowsExecutableOutput()
    {
        Assert.NotNull(typeof(App).Assembly.EntryPoint);
    }
}
