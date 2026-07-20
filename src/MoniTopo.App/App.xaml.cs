using System.IO;
using System.Windows;
using Microsoft.Win32;
using MoniTopo.App.Interaction;
using MoniTopo.App.Lifecycle;
using MoniTopo.App.State;
using MoniTopo.App.Tray;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Persistence;
using MoniTopo.Windows.Display;
using MoniTopo.Windows.Input;

namespace MoniTopo.App;

public partial class App : System.Windows.Application, IDisposable
{
    private SingleInstanceCoordinator? _singleInstance;
    private ApplicationMessageWindow? _messageWindow;
    private GlobalHotkeyService? _hotkeys;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;
    private ConfigurationSession? _configuration;
    private GuardedProfileActivator? _profileActivator;
    private DisplayStateRefreshService? _displayRefresh;
    private HotkeyCommandRouter? _hotkeyRouter;
    private bool _exiting;
    private bool _disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = SingleInstanceCoordinator.CreateForCurrentUser();
        if (!_singleInstance.IsPrimary)
        {
            _ = await _singleInstance.SignalPrimaryAsync().ConfigureAwait(true);
            Shutdown();
            return;
        }

        _singleInstance.OpenRequested += OnOpenRequested;
        _singleInstance.StartListening();

        var configurationPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MoniTopo",
            "config.json");
        var store = new JsonConfigurationStore(configurationPath);
        try
        {
            _configuration = await ConfigurationSession.LoadAsync(store).ConfigureAwait(true);
        }
        catch (ConfigurationLoadException exception)
        {
            _configuration = new ConfigurationSession(store, ApplicationConfiguration.CreateDefault());
            System.Windows.MessageBox.Show(exception.Message, "MoniTopo", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        _mainWindow = new MainWindow();
        _tray = new TrayIconService();
        _tray.TogglePopupRequested += (_, _) => ToggleCurrentSurface();
        _tray.OpenSettingsRequested += (_, _) => OpenMainWindow();
        _tray.ExitRequested += (_, _) => ExitApplication();

        _messageWindow = new ApplicationMessageWindow();
        _hotkeys = new GlobalHotkeyService(_messageWindow.Handle);
        _messageWindow.AttachHotkeys(_hotkeys);
        RegisterConfiguredHotkeys(_configuration.Current);

        _profileActivator = new GuardedProfileActivator(_configuration);
        var activationInteraction = new ActivationInteractionController(
            _profileActivator,
            _tray,
            enabled => _ = _hotkeys.SetProfileHotkeysEnabled(enabled));
        _hotkeyRouter = new HotkeyCommandRouter(
            () => _configuration.Current.Profiles,
            activationInteraction,
            new ApplicationSurfaces(this));
        _hotkeys.Pressed += OnHotkeyPressed;

        var resolver = new MonitorIdentityResolver();
        var activeState = new ActiveDisplayStateCoordinator(
            new CcdCaptureService(),
            new ActiveProfileMatcher(resolver),
            () => _configuration.Current);
        activeState.Changed += (_, state) => Dispatcher.BeginInvoke(() => _tray.SetCurrentState(state.ProfileId is null ? null : state.DisplayName));
        _displayRefresh = new DisplayStateRefreshService(async cancellationToken =>
        {
            try
            {
                await activeState.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DisplayCaptureException)
            {
                // A transient read failure leaves the last derived state until the next event/poll.
            }
        });
        _messageWindow.DisplayStateChanged += (_, _) => _ = _displayRefresh.NotifyDisplayChangeAsync();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        await _displayRefresh.RunConsistencyCheckAsync().ConfigureAwait(true);

        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        if (!background)
        {
            OpenMainWindow();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _displayRefresh?.BeginShutdown();
        _hotkeys?.Dispose();
        _messageWindow?.Dispose();
        _tray?.Dispose();
        _profileActivator?.Dispose();
        _configuration?.Dispose();
        if (_displayRefresh is not null)
        {
            _displayRefresh.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_singleInstance is not null)
        {
            _singleInstance.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void RegisterConfiguredHotkeys(ApplicationConfiguration configuration)
    {
        var popupResult = _hotkeys!.RegisterPopup(configuration.ApplicationSettings.PopupHotkey);
        if (!popupResult.Succeeded)
        {
            _tray!.ShowError(popupResult.Message!);
        }

        foreach (var profile in configuration.Profiles.Where(profile => profile.DirectHotkey is not null))
        {
            var result = _hotkeys.RegisterProfile(profile.Id, profile.DirectHotkey);
            if (!result.Succeeded)
            {
                _tray!.ShowError($"{profile.Name}: {result.Message}");
            }
        }
    }

    private async void OnHotkeyPressed(object? sender, HotkeyCommand command)
    {
        if (_hotkeyRouter is not null)
        {
            await _hotkeyRouter.HandleAsync(command).ConfigureAwait(true);
        }
    }

    private void OnOpenRequested(object? sender, EventArgs eventArgs) => Dispatcher.BeginInvoke(OpenMainWindow);

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        if (eventArgs.Reason == SessionSwitchReason.SessionLock)
        {
            _displayRefresh?.SetSessionLocked(true);
        }
        else if (eventArgs.Reason == SessionSwitchReason.SessionUnlock)
        {
            _displayRefresh?.SetSessionLocked(false);
            if (_displayRefresh is not null)
            {
                _ = _displayRefresh.RunConsistencyCheckAsync();
            }
        }
    }

    private void ToggleCurrentSurface()
    {
        if (_mainWindow?.IsVisible == true)
        {
            _mainWindow.Hide();
        }
        else
        {
            OpenMainWindow();
        }
    }

    private void OpenMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _mainWindow?.AllowClose();
        _mainWindow?.Close();
        Shutdown();
    }

    private sealed class ApplicationSurfaces(App owner) : IApplicationSurfaces
    {
        public void TogglePopup() => owner.ToggleCurrentSurface();

        public void OpenSettings() => owner.OpenMainWindow();
    }
}
