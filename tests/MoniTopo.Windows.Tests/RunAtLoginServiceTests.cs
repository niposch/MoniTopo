using MoniTopo.Windows.Startup;

namespace MoniTopo.Windows.Tests;

public sealed class RunAtLoginServiceTests
{
    [Fact]
    public void EnablingWritesQuotedBackgroundCommand()
    {
        var registry = new FakeRegistry();
        var service = new RunAtLoginService(@"C:\Apps with spaces\MoniTopo.App.exe", false, registry);

        service.SetEnabled(true);

        Assert.Equal("\"C:\\Apps with spaces\\MoniTopo.App.exe\" --background", registry.Value);
        Assert.True(service.IsEnabled);
        Assert.Null(service.Warning);
    }

    [Fact]
    public void DisablingDeletesOnlyMoniTopoValue()
    {
        var registry = new FakeRegistry { Value = "old command" };
        var service = new RunAtLoginService(@"C:\Apps\MoniTopo.App.exe", false, registry);

        service.SetEnabled(false);

        Assert.Null(registry.Value);
        Assert.Equal(RunAtLoginService.ValueName, registry.LastDeletedName);
    }

    [Fact]
    public void PortableInstallReportsMoveWarning()
    {
        var service = new RunAtLoginService(@"C:\Portable\MoniTopo.App.exe", true, new FakeRegistry());

        Assert.True(service.IsPortable);
        Assert.Contains("same path", service.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignCommandIsNotReportedAsEnabled()
    {
        var registry = new FakeRegistry { Value = "\"C:\\Old\\MoniTopo.App.exe\" --background" };
        var service = new RunAtLoginService(@"C:\New\MoniTopo.App.exe", false, registry);

        Assert.False(service.IsEnabled);
    }

    private sealed class FakeRegistry : IRunAtLoginRegistry
    {
        public string? Value { get; set; }

        public string? LastDeletedName { get; private set; }

        public string? Read(string valueName) => Value;

        public void Write(string valueName, string command) => Value = command;

        public void Delete(string valueName)
        {
            LastDeletedName = valueName;
            Value = null;
        }
    }
}
