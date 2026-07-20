using System.IO;
using System.Windows;
using Microsoft.Win32;
using MoniTopo.App.Diagnostics;
using MoniTopo.App.Interaction;
using MoniTopo.App.Lifecycle;
using MoniTopo.App.Popup;
using MoniTopo.App.Profiles;
using MoniTopo.App.Settings;
using MoniTopo.App.State;
using MoniTopo.App.Tray;
using MoniTopo.App.Updates;
using MoniTopo.Core.Activation;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Persistence;
using MoniTopo.Core.Updates;
using MoniTopo.Windows.Display;
using MoniTopo.Windows.Input;
using MoniTopo.Windows.Startup;

namespace MoniTopo.App;

public partial class App : System.Windows.Application, IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private SingleInstanceCoordinator? _singleInstance;
    private ApplicationMessageWindow? _messageWindow;
    private GlobalHotkeyService? _hotkeys;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;
    private PopupWindow? _popupWindow;
    private ConfigurationSession? _configuration;
    private StartupSettingsCoordinator? _startupSettings;
    private GuardedProfileActivator? _profileActivator;
    private DisplayStateRefreshService? _displayRefresh;
    private HotkeyCommandRouter? _hotkeyRouter;
    private ActivationInteractionController? _activationInteraction;
    private ActiveDisplayStateCoordinator? _activeDisplayState;
    private UpdateCoordinator? _updates;
    private Task? _updateCheckTask;
    private LocalDiagnosticLog? _diagnosticLog;
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

        var applicationDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MoniTopo");
        _diagnosticLog = new LocalDiagnosticLog(Path.Combine(applicationDataPath, "logs"));
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        var configurationPath = Path.Combine(applicationDataPath, "config.json");
        var store = new JsonConfigurationStore(configurationPath);
        try
        {
            _configuration = await ConfigurationSession.LoadAsync(store).ConfigureAwait(true);
        }
        catch (ConfigurationLoadException exception)
        {
            _diagnosticLog.Write("Configuration", exception);
            _configuration = new ConfigurationSession(store, ApplicationConfiguration.CreateDefault());
            System.Windows.MessageBox.Show(exception.Message, "MoniTopo", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        var startupExecutable = StartupExecutable.Current();
        var runAtLogin = new RunAtLoginService(
            startupExecutable.Path,
            startupExecutable.IsPortable,
            new CurrentUserRunRegistry());
        _startupSettings = new StartupSettingsCoordinator(_configuration, runAtLogin);
        _updates = new UpdateCoordinator(new VelopackUpdateClient(), _configuration);
        var isFirstRun = !_configuration.Current.ApplicationSettings.FirstRunCompleted;
        if (isFirstRun)
        {
            var firstRunWindow = new FirstRunWindow(runAtLogin.Warning);
            if (firstRunWindow.ShowDialog() == true)
            {
                try
                {
                    await _startupSettings.CompleteFirstRunAsync(firstRunWindow.StartAtLogin).ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _diagnosticLog.Write("FirstRun", exception);
                    System.Windows.MessageBox.Show(
                        $"MoniTopo could not save the sign-in preference. {exception.Message}",
                        "MoniTopo",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        _mainWindow = new MainWindow();
        _popupWindow = new PopupWindow();
        _popupWindow.SettingsRequested += (_, _) => OpenMainWindow();
        _popupWindow.ActivateProfileRequested += OnPopupActivateRequested;
        _tray = new TrayIconService();
        _tray.TogglePopupRequested += (_, _) => TogglePopup();
        _tray.OpenSettingsRequested += (_, _) => OpenMainWindow();
        _tray.ExitRequested += (_, _) => ExitApplication();

        _messageWindow = new ApplicationMessageWindow();
        _hotkeys = new GlobalHotkeyService(_messageWindow.Handle);
        _messageWindow.AttachHotkeys(_hotkeys);
        RegisterConfiguredHotkeys(_configuration.Current);

        _profileActivator = new GuardedProfileActivator(_configuration);
        _activationInteraction = new ActivationInteractionController(
            _profileActivator,
            _tray,
            enabled => _ = _hotkeys.SetProfileHotkeysEnabled(enabled));
        _activationInteraction.PopupProgressChanged += (_, progress) => _popupWindow.ViewModel.ReportProgress(progress.Message);
        _hotkeyRouter = new HotkeyCommandRouter(
            () => _configuration.Current.Profiles,
            _activationInteraction,
            new ApplicationSurfaces(this));
        _hotkeys.Pressed += OnHotkeyPressed;

        var resolver = new MonitorIdentityResolver();
        var captureService = new CcdCaptureService();
        var matcher = new ActiveProfileMatcher(resolver);
        var profileManagement = new ProfileManagementService(_configuration, captureService, matcher);
        _mainWindow.Initialize(
            _configuration,
            profileManagement,
            _activationInteraction,
            _startupSettings,
            _updates,
            (area, exception) => _diagnosticLog.Write(area, exception),
            (profileId, hotkey) => _hotkeys.RegisterProfile(profileId, hotkey));
        _activeDisplayState = new ActiveDisplayStateCoordinator(
            captureService,
            matcher,
            () => _configuration.Current);
        _activeDisplayState.Changed += (_, state) => Dispatcher.BeginInvoke(() => _tray.SetCurrentState(state.ProfileId is null ? null : state.DisplayName));
        _displayRefresh = new DisplayStateRefreshService(async cancellationToken =>
        {
            try
            {
                await _activeDisplayState.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DisplayCaptureException)
            {
                // A transient read failure leaves the last derived state until the next event/poll.
            }
        });
        _messageWindow.DisplayStateChanged += (_, _) => _ = _displayRefresh.NotifyDisplayChangeAsync();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        await _displayRefresh.RunConsistencyCheckAsync().ConfigureAwait(true);

        _popupWindow.ViewModel.UpdateAvailable = _updates.Current.Status is UpdateStatus.Available or UpdateStatus.ReadyToInstall;
        _updates.Changed += OnUpdateStateChanged;
        _updateCheckTask = CheckForUpdatesAfterStartupAsync();

        if (isFirstRun || _configuration.Current.ApplicationSettings.ShowMainWindowOnLaunch)
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
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _shutdown.Cancel();
        _updateCheckTask?.GetAwaiter().GetResult();
        _displayRefresh?.BeginShutdown();
        _hotkeys?.Dispose();
        _messageWindow?.Dispose();
        _popupWindow?.Close();
        _tray?.Dispose();
        _profileActivator?.Dispose();
        _updates?.Dispose();
        _configuration?.Dispose();
        _shutdown.Dispose();
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

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs eventArgs) =>
        _diagnosticLog?.Write("Dispatcher", eventArgs.Exception);

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        if (eventArgs.ExceptionObject is Exception exception)
        {
            _diagnosticLog?.Write("Process", exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        _diagnosticLog?.Write("Task", eventArgs.Exception);
        eventArgs.SetObserved();
    }

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

    private void OnUpdateStateChanged(object? sender, UpdateState state) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_popupWindow is not null)
            {
                _popupWindow.ViewModel.UpdateAvailable = state.Status is UpdateStatus.Available or UpdateStatus.ReadyToInstall;
            }
        });

    private async Task CheckForUpdatesAfterStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _shutdown.Token).ConfigureAwait(false);
            if (_updates is not null && !_exiting)
            {
                await _updates.CheckAutomaticallyAsync(_shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal application shutdown cancels the delayed/background check.
        }
    }

    private void TogglePopup()
    {
        if (_popupWindow is null || _configuration is null)
        {
            return;
        }

        _popupWindow.Toggle(_configuration.Current.Profiles, _activeDisplayState?.Current.ProfileId);
    }

    private async void OnPopupActivateRequested(object? sender, Guid profileId)
    {
        if (_configuration?.Current.Profiles.FirstOrDefault(profile => profile.Id == profileId) is not { } profile ||
            _activationInteraction is null ||
            _popupWindow is null)
        {
            return;
        }

        _popupWindow.ViewModel.BeginActivation(profile.Name);
        var result = await _activationInteraction.ActivateAsync(profile, ActivationOrigin.Popup).ConfigureAwait(true);
        _popupWindow.ViewModel.CompleteActivation(
            result.Outcome == ActivationOutcome.Success,
            result.Message,
            result.Outcome == ActivationOutcome.Success ? profile.Id : _activeDisplayState?.Current.ProfileId);
        if (_displayRefresh is not null)
        {
            await _displayRefresh.RunConsistencyCheckAsync().ConfigureAwait(true);
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
        public void TogglePopup() => owner.TogglePopup();

        public void OpenSettings() => owner.OpenMainWindow();
    }
}
