namespace MoniTopo.Core.Tests;

public sealed class ProjectHealthTests
{
    [Fact]
    public void CoreAssemblyLoadsWithoutWindowsDesktopDependency()
    {
        var assembly = typeof(ProjectHealthTests).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(assembly, item => item.Name is "PresentationFramework");
    }
}
