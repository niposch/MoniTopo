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
using MoniTopo.Core.Recovery;
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
    private PopupHotkeySettingsCoordinator? _popupHotkeySettings;
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
    private bool _openRequestedDuringStartup;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = SingleInstanceCoordinator.CreateForCurrentUser();
        if (!_singleInstance.IsPrimary)
        {
            if (!await _singleInstance.SignalPrimaryAsync().ConfigureAwait(true))
            {
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoniTopo");
                var build = ApplicationBuildInfo.Current(null, false);
                new LocalDiagnosticLog(Path.Combine(data, "logs"), startupMessage: build.DiagnosticText)
                    .Write("SingleInstance", "The running instance did not acknowledge the open-window request within the deadline.");
                System.Windows.MessageBox.Show(
                    "MoniTopo is already running, but did not respond to the request to open its window. Try its tray icon, or close the unresponsive instance in Task Manager and launch it again.",
                    "MoniTopo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        _singleInstance.OpenRequested += OnOpenRequested;
        _singleInstance.StartListening();

        var applicationDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MoniTopo");
        var updateClient = new VelopackUpdateClient();
        var buildIdentity = ApplicationBuildInfo.Current(updateClient.CurrentPackageVersion, updateClient.IsInstalled);
        _diagnosticLog = new LocalDiagnosticLog(
            Path.Combine(applicationDataPath, "logs"),
            startupMessage: buildIdentity.DiagnosticText);
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
            if (exception.ValidBackupPath is not null && System.Windows.MessageBox.Show(
                $"{exception.Message}\n\nA valid backup is available. Restore it? Choosing No closes MoniTopo and leaves your files intact.",
                "MoniTopo configuration", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try
                {
                    await store.RestoreBackupAsync(_shutdown.Token).ConfigureAwait(true);
                    _configuration = await ConfigurationSession.LoadAsync(store).ConfigureAwait(true);
                }
                catch (Exception recoveryException) when (recoveryException is ConfigurationLoadException or IOException or UnauthorizedAccessException)
                {
                    _diagnosticLog.Write("ConfigurationRecovery", recoveryException);
                    System.Windows.MessageBox.Show(recoveryException.Message, "MoniTopo configuration", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown();
                    return;
                }
            }
            else
            {
                if (exception.ValidBackupPath is null)
                {
                    System.Windows.MessageBox.Show($"{exception.Message}\n\nMoniTopo will close without changing your configuration. Restore or repair the configuration file before starting again.", "MoniTopo configuration", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                Shutdown();
                return;
            }
        }

        ApplicationTheme.Apply(this, _configuration.Current.ApplicationSettings.ThemePreference);
        var startupExecutable = StartupExecutable.Current();
        _configuration.NotificationFailed += (_, exception) => _diagnosticLog.Write("ConfigurationNotification", exception);
        var runAtLogin = new RunAtLoginService(
            startupExecutable.Path,
            startupExecutable.IsPortable,
            new CurrentUserRunRegistry());
        _startupSettings = new StartupSettingsCoordinator(_configuration, runAtLogin);
        _updates = new UpdateCoordinator(updateClient, _configuration);
        _updates.NotificationFailed += (_, exception) => _diagnosticLog.Write("UpdateNotification", exception);
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
        _popupWindow = new PopupWindow(buildIdentity.DisplayVersion);
        _popupWindow.MainWindowRequested += (_, _) => OpenMainWindow();
        _popupWindow.UpdateActionRequested += OnPopupUpdateActionRequested;
        _popupWindow.ReleaseNotesRequested += (_, update) =>
        {
            var notes = new MoniTopo.App.Dialogs.ReleaseNotesDialog(update);
            if (_mainWindow?.IsVisible == true) notes.Owner = _mainWindow;
            notes.ShowDialog();
        };
        _popupWindow.ActivateProfileRequested += OnPopupActivateRequested;
        _tray = new TrayIconService();
        _tray.TogglePopupRequested += (_, _) => TogglePopup();
        _tray.OpenMainWindowRequested += (_, _) => OpenMainWindow();
        _tray.ExitRequested += (_, _) => ExitApplication();

        _messageWindow = new ApplicationMessageWindow();
        _hotkeys = new GlobalHotkeyService(_messageWindow.Handle);
        _messageWindow.AttachHotkeys(_hotkeys);
        RegisterConfiguredHotkeys(_configuration.Current);

        _profileActivator = new GuardedProfileActivator(_configuration);
        _activationInteraction = new ActivationInteractionController(
            _profileActivator,
            _tray,
            SetProfileHotkeysEnabled,
            message => _diagnosticLog.Write("Activation", message),
            new WpfUiDispatcher(Dispatcher));
        _activationInteraction.BusyChanged += (_, busy) => _popupWindow.ViewModel.SetActivationBusy(busy);
        _activationInteraction.PopupProgressChanged += (_, progress) => _popupWindow.ViewModel.ReportProgress(progress.Message);
        _hotkeyRouter = new HotkeyCommandRouter(
            () => _configuration.Current.Profiles,
            _activationInteraction,
            new ApplicationSurfaces(this));
        _hotkeys.Pressed += OnHotkeyPressed;

        var resolver = new MonitorIdentityResolver();
        var captureService = new CcdCaptureService();
        var matcher = new ActiveProfileMatcher(resolver);
        var profileManagement = new ProfileManagementService(_configuration, captureService, matcher,
            capabilities: new DisplaySettingsCapabilities(captureService));
        _popupHotkeySettings = new PopupHotkeySettingsCoordinator(
            _configuration,
            binding => _hotkeys.RegisterPopup(binding),
            new WpfUiDispatcher(Dispatcher));
        _mainWindow.Initialize(
            _configuration,
            profileManagement,
            _activationInteraction,
            _startupSettings,
            _popupHotkeySettings,
            _updates,
            (area, exception) => _diagnosticLog.Write(area, exception),
            (profileId, hotkey) => _hotkeys.RegisterProfile(profileId, hotkey));
        _activeDisplayState = new ActiveDisplayStateCoordinator(
            captureService,
            matcher,
            () => _configuration.Current);
        _activeDisplayState.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var state = _activeDisplayState.Current;
            _tray.SetCurrentState(state.ProfileId is null ? null : state.DisplayName);
            RefreshVisiblePopup();
        });
        _configuration.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ApplicationTheme.Apply(this, _configuration.Current.ApplicationSettings.ThemePreference);
            RefreshVisiblePopup();
            if (_displayRefresh is not null)
            {
                _ = _displayRefresh.NotifyDisplayChangeAsync();
            }
        });
        _displayRefresh = new DisplayStateRefreshService(async cancellationToken =>
        {
            try
            {
                await _activeDisplayState.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DisplayCaptureException exception) when (exception.IsTransient)
            {
                // A transient read failure leaves the last derived state until the next event/poll.
            }
            catch (DisplayCaptureException exception)
            {
                _diagnosticLog.Write("DisplayRefresh", exception);
                _ = Dispatcher.BeginInvoke(() => _mainWindow?.ReportStatus(
                    $"MoniTopo could not refresh the current display state. {exception.Message}"));
            }
        });
        _messageWindow.DisplayStateChanged += (_, _) => _ = _displayRefresh.NotifyDisplayChangeAsync();
        _activationInteraction.ActivationCompleted += (_, _) => _ = _displayRefresh.NotifyDisplayChangeAsync();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        await _displayRefresh.RunConsistencyCheckAsync().ConfigureAwait(true);

        _popupWindow.ViewModel.ApplyUpdateState(_updates.Current);
        _updates.Changed += OnUpdateStateChanged;
        _updateCheckTask = CheckForUpdatesAfterStartupAsync();

        await ReportPendingRecoveryAsync(Path.Combine(applicationDataPath, "recovery")).ConfigureAwait(true);
        if (LaunchBehavior.ShouldShowMainWindow(e.Args, isFirstRun, _configuration.Current.ApplicationSettings.ShowMainWindowOnLaunch) || _openRequestedDuringStartup)
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
        _mainWindow?.Dispose();
        _tray?.Dispose();
        _profileActivator?.Dispose();
        _startupSettings?.Dispose();
        _popupHotkeySettings?.Dispose();
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

    private void SetProfileHotkeysEnabled(bool enabled)
    {
        foreach (var failure in _hotkeys!.SetProfileHotkeysEnabled(enabled).Where(result => !result.Succeeded))
        {
            var message = failure.Message ?? "A profile hotkey could not be registered. Reassign it in MoniTopo.";
            _diagnosticLog?.Write("Hotkeys", message);
            _tray?.ShowError(message);
            _mainWindow?.ReportStatus(message);
        }
    }

    private async void OnHotkeyPressed(object? sender, HotkeyCommand command)
    {
        if (_hotkeyRouter is not null && !_exiting)
        {
            await _hotkeyRouter.HandleAsync(command).ConfigureAwait(true);
        }
    }

    private void OnOpenRequested(object? sender, EventArgs eventArgs) => Dispatcher.BeginInvoke(() =>
    {
        _openRequestedDuringStartup = true;
        OpenMainWindow();
    });

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
            if (_popupWindow is not null && _updates is not null && !_disposed)
            {
                _popupWindow.ViewModel.ApplyUpdateState(_updates.Current);
            }
        });

    private async Task CheckForUpdatesAfterStartupAsync()
    {
        try
        {
            if (_updates is not null && !_exiting)
            {
                await _updates.RunAutomaticChecksAsync(_shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal application shutdown cancels the delayed/background check.
        }
        catch (Exception exception)
        {
            _diagnosticLog?.Write("AutomaticUpdates", exception);
            if (!_exiting)
            {
                _ = Dispatcher.BeginInvoke(() => _mainWindow?.ReportStatus(
                    "Automatic update checks stopped after an error. See the local diagnostic log or check manually in Settings."));
            }
        }
    }

    private void TogglePopup()
    {
        if (_popupWindow is null || _configuration is null)
        {
            return;
        }

        _popupWindow.Toggle(OrderedProfiles(), _activeDisplayState?.Current.ProfileId);
    }

    private MoniTopo.Core.Models.DisplayProfile[] OrderedProfiles()
    {
        var configuration = _configuration!.Current;
        var byId = configuration.Profiles.ToDictionary(profile => profile.Id);
        return configuration.ProfileOrder.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
    }

    private void RefreshVisiblePopup()
    {
        if (_popupWindow?.IsVisible == true && _configuration is not null)
        {
            _popupWindow.ViewModel.RefreshProfiles(OrderedProfiles(), _activeDisplayState?.Current.ProfileId);
            _popupWindow.RefreshPlacement();
        }
    }

    private async void OnPopupActivateRequested(object? sender, Guid profileId)
    {
        if (_configuration?.Current.Profiles.FirstOrDefault(profile => profile.Id == profileId) is not { } profile ||
            _activationInteraction is null ||
            _popupWindow is null || _activationInteraction.IsBusy || _exiting)
        {
            return;
        }

        _popupWindow.ViewModel.ReportProgress($"Activating {profile.Name}…");
        var result = await _activationInteraction.ActivateAsync(profile, ActivationOrigin.Popup).ConfigureAwait(true);
        if (result.Outcome == ActivationOutcome.Busy)
        {
            _popupWindow.ViewModel.ReportProgress(result.Message);
            return;
        }
        _popupWindow.ViewModel.CompleteActivation(
            result.Outcome == ActivationOutcome.Success,
            result.Message,
            result.Outcome == ActivationOutcome.Success ? profile.Id : _activeDisplayState?.Current.ProfileId);
        _popupWindow.ViewModel.SetActivationBusy(_activationInteraction.IsBusy);
        if (_displayRefresh is not null)
        {
            await _displayRefresh.RunConsistencyCheckAsync().ConfigureAwait(true);
        }
    }

    private async void OnPopupUpdateActionRequested(object? sender, EventArgs eventArgs)
    {
        if (_updates is null || _activationInteraction is null || _exiting)
        {
            return;
        }

        await using var interaction = await _activationInteraction.TryBeginInteractionAsync().ConfigureAwait(true);
        if (interaction is null)
        {
            return;
        }

        try
        {
            if (_updates.Current.Status == UpdateStatus.Available)
            {
                await _updates.DownloadAsync(_shutdown.Token).ConfigureAwait(true);
            }
            else if (_updates.Current.Status == UpdateStatus.ReadyToInstall)
            {
                _updates.InstallAndRestart();
            }
        }
        catch (Exception exception) when (exception is UpdateClientException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _diagnosticLog?.Write("PopupUpdate", exception);
            _popupWindow?.ViewModel.ReportProgress(exception.Message);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void OpenMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.ShowProfilesPage();
        ShowMainWindow();
    }

    private void OpenSettingsWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.ShowSettingsPage();
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        _mainWindow!.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private async void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _shutdown.Cancel();
        _mainWindow?.ReportStatus("Waiting for the current operation before exiting…");
        if (_activationInteraction is not null)
        {
            await _activationInteraction.BeginShutdownAsync().ConfigureAwait(true);
        }

        if (_updateCheckTask is not null)
        {
            await _updateCheckTask.ConfigureAwait(true);
        }

        if (_displayRefresh is not null)
        {
            await _displayRefresh.DisposeAsync().ConfigureAwait(true);
            _displayRefresh = null;
        }

        _mainWindow?.AllowClose();
        _mainWindow?.Close();
        Shutdown();
    }

    private async Task ReportPendingRecoveryAsync(string directory)
    {
        try
        {
            var store = new RecoveryResultStore(directory);
            foreach (var pending in await store.ReadPendingAsync(_shutdown.Token).ConfigureAwait(true))
            {
                _diagnosticLog?.Write("Recovery", $"{pending.Result.Outcome}: {pending.Result.Message}");
                System.Windows.MessageBox.Show(pending.Result.Message, "MoniTopo display recovery", MessageBoxButton.OK,
                    pending.Result.RollbackSucceeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
                await store.AcknowledgeAsync(pending, _shutdown.Token).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _diagnosticLog?.Write("RecoveryResults", exception);
            _mainWindow?.ReportStatus("MoniTopo could not read or acknowledge a previous recovery report. See the local diagnostic log.");
        }
    }

    private sealed class ApplicationSurfaces(App owner) : IApplicationSurfaces
    {
        public void TogglePopup() => owner.TogglePopup();

        public void OpenSettings() => owner.OpenSettingsWindow();
    }
}
