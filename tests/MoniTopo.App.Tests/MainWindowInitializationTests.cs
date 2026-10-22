using MoniTopo.App.Interaction;
using MoniTopo.App.Profiles;
using MoniTopo.App.Settings;
using MoniTopo.App.State;
using MoniTopo.App.Tray;
using MoniTopo.Core.Activation;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;
using MoniTopo.Core.Persistence;
using MoniTopo.Core.Updates;
using MoniTopo.Windows.Display;
using MoniTopo.Windows.Input;
using MoniTopo.Windows.Startup;

namespace MoniTopo.App.Tests;

[Collection(WpfUiTestCollectionDefinition.Name)]
public sealed class MainWindowInitializationTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 620)]
    [InlineData(980, 0)]
    public void CompleteInitializationAndShowTolerateUnusablePersistedDimensions(double width, double height)
    {
        StaDispatcherTest.Run(() =>
        {
            using var fixture = new Fixture(new WindowBounds(0, 0, width, height));
            var window = fixture.CreateWindow();
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.True(double.IsFinite(window.ActualWidth) && window.ActualWidth > 0);
                Assert.True(double.IsFinite(window.ActualHeight) && window.ActualHeight > 0);
                window.ShowSettingsPage();
            }
            finally { window.AllowClose(); window.Close(); }
            Assert.True(WindowBoundsClamp.IsUsable(fixture.Session.Current.ApplicationSettings.LastMainWindowBounds!.Value));
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrayOnlyExitNeverPersistsUnmeasuredZeroDimensions(bool hasPreviousPlacement)
    {
        StaDispatcherTest.Run(() =>
        {
            WindowBounds? previous = hasPreviousPlacement ? new WindowBounds(80, 90, 900, 600) : null;
            using var fixture = new Fixture(previous);
            var window = fixture.CreateWindow();
            Assert.Equal(0, window.ActualWidth);
            window.AllowClose();
            window.Close();
            Assert.Equal(previous, fixture.Session.Current.ApplicationSettings.LastMainWindowBounds);
            Assert.Equal(0, fixture.Store.SaveCount);
            return Task.CompletedTask;
        });
    }

    private sealed class Fixture : IDisposable
    {
        internal MemoryStore Store { get; } = new();
        internal ConfigurationSession Session { get; }
        private readonly UpdateCoordinator _updates;
        private readonly StartupSettingsCoordinator _startup;
        private readonly PopupHotkeySettingsCoordinator _popup;
        private readonly ActivationInteractionController _interaction = new(new UnusedActivator(), new NoNotifications(), _ => { });

        internal Fixture(WindowBounds? bounds)
        {
            Session = new ConfigurationSession(Store, ApplicationConfiguration.CreateDefault() with
            {
                ApplicationSettings = ApplicationSettings.Default with { LastMainWindowBounds = bounds, FirstRunCompleted = true },
            });
            _updates = new UpdateCoordinator(new NoUpdates(), Session);
            _startup = new StartupSettingsCoordinator(Session, new NoStartupWrites());
            _popup = new PopupHotkeySettingsCoordinator(Session, _ => new HotkeyRegistrationResult(true, null, false));
        }

        internal MainWindow CreateWindow()
        {
            var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
            var profiles = new ProfileManagementService(Session, new UnusedCapture(), new ActiveProfileMatcher(new MonitorIdentityResolver()));
            window.Initialize(Session, profiles, _interaction, _startup, _popup, _updates, (_, _) => { },
                (_, _) => new HotkeyRegistrationResult(true, null, false));
            return window;
        }

        public void Dispose() { _popup.Dispose(); _startup.Dispose(); _updates.Dispose(); Session.Dispose(); }
    }

    private sealed class MemoryStore : IConfigurationStore
    {
        internal int SaveCount { get; private set; }
        public Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default) { SaveCount++; return Task.CompletedTask; }
    }
    private sealed class UnusedCapture : IDisplayCaptureService
    {
        public Task<CapturedDisplaySnapshot> CaptureAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No display capture should occur during window initialization.");
    }
    private sealed class UnusedActivator : IProfileActivator
    {
        public Task<ActivationResult> ActivateAsync(DisplayProfile profile, IProgress<ActivationProgress>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException("No display activation should occur during window initialization.");
    }
    private sealed class NoNotifications : IUserNotificationService
    {
        public void ShowInformation(string message) { }
        public void ShowError(string message) { }
    }
    private sealed class NoStartupWrites : IRunAtLoginService
    {
        public bool IsEnabled => false;
        public bool IsPortable => true;
        public string? Warning => null;
        public void SetEnabled(bool enabled) => throw new InvalidOperationException("No registry writes during startup smoke test.");
    }
    private sealed class NoUpdates : IUpdateClient
    {
        public bool IsInstalled => false;
        public string? CurrentPackageVersion => null;
        public AvailableUpdate? PendingRestart => null;
        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task DownloadAsync(AvailableUpdate update, IProgress<int>? progress = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void ApplyAndRestart(AvailableUpdate update) => throw new InvalidOperationException();
    }
}
