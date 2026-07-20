using MoniTopo.App.Settings;
using MoniTopo.App.State;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Persistence;
using MoniTopo.Windows.Startup;

namespace MoniTopo.App.Tests;

public sealed class StartupSettingsCoordinatorTests
{
    [Fact]
    public async Task FirstRunPersistsChoiceAndCompletion()
    {
        var store = new MemoryStore();
        using var session = new ConfigurationSession(store, ApplicationConfiguration.CreateDefault());
        var startup = new FakeRunAtLogin();
        var coordinator = new StartupSettingsCoordinator(session, startup);

        await coordinator.CompleteFirstRunAsync(false);

        Assert.False(startup.IsEnabled);
        Assert.False(session.Current.ApplicationSettings.RunAtLogin);
        Assert.True(session.Current.ApplicationSettings.FirstRunCompleted);
    }

    [Fact]
    public async Task PersistenceFailureRestoresWorkingRegistryChoice()
    {
        var store = new MemoryStore { Failure = new IOException("disk full") };
        using var session = new ConfigurationSession(store, ApplicationConfiguration.CreateDefault());
        var startup = new FakeRunAtLogin { IsEnabled = true };
        var coordinator = new StartupSettingsCoordinator(session, startup);

        await Assert.ThrowsAsync<IOException>(() => coordinator.SetRunAtLoginAsync(false));

        Assert.True(startup.IsEnabled);
        Assert.True(session.Current.ApplicationSettings.RunAtLogin);
    }

    [Fact]
    public async Task ShowWindowPreferenceDefaultsOffAndCanBeEnabled()
    {
        var store = new MemoryStore();
        using var session = new ConfigurationSession(store, ApplicationConfiguration.CreateDefault());
        var coordinator = new StartupSettingsCoordinator(session, new FakeRunAtLogin());

        Assert.False(session.Current.ApplicationSettings.ShowMainWindowOnLaunch);
        await coordinator.SetShowMainWindowOnLaunchAsync(true);

        Assert.True(session.Current.ApplicationSettings.ShowMainWindowOnLaunch);
    }

    [Fact]
    public void InstalledStartupUsesStableVelopackRootStub()
    {
        var executable = StartupExecutable.Resolve(
            @"C:\Users\Example\AppData\Local\MoniTopo\current\MoniTopo.App.exe",
            isPortable: false,
            isInstalled: true,
            @"C:\Users\Example\AppData\Local\MoniTopo");

        Assert.Equal(@"C:\Users\Example\AppData\Local\MoniTopo\MoniTopo.App.exe", executable.Path);
        Assert.False(executable.IsPortable);
    }

    [Fact]
    public void PortableStartupKeepsCurrentExecutablePath()
    {
        var executable = StartupExecutable.Resolve(
            @"D:\Tools\MoniTopo.App.exe",
            isPortable: true,
            isInstalled: true,
            @"C:\Ignored");

        Assert.Equal(@"D:\Tools\MoniTopo.App.exe", executable.Path);
        Assert.True(executable.IsPortable);
    }

    private sealed class FakeRunAtLogin : IRunAtLoginService
    {
        public bool IsEnabled { get; set; }

        public bool IsPortable => false;

        public string? Warning => null;

        public void SetEnabled(bool enabled) => IsEnabled = enabled;
    }

    private sealed class MemoryStore : IConfigurationStore
    {
        public Exception? Failure { get; init; }

        public Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ApplicationConfiguration.CreateDefault());

        public Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default) =>
            Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
