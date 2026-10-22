using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using MoniTopo.App.Dialogs;
using MoniTopo.App.Interaction;
using MoniTopo.App.Profiles;
using MoniTopo.App.Settings;
using MoniTopo.App.State;
using MoniTopo.Core.Activation;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Models;
using MoniTopo.Core.Updates;
using MoniTopo.Core.Validation;
using MoniTopo.Windows.Display;
using MoniTopo.Windows.Input;
using MoniTopo.Windows.Shell;

namespace MoniTopo.App;

public partial class MainWindow : Window, IDisposable
{
    private bool _allowClose;
    private MainWindowViewModel? _viewModel;
    private ConfigurationSession? _configuration;
    private ProfileManagementService? _profiles;
    private ActivationInteractionController? _activation;
    private StartupSettingsCoordinator? _startupSettings;
    private PopupHotkeySettingsCoordinator? _popupHotkeySettings;
    private UpdateCoordinator? _updates;
    private Action<string, Exception>? _logError;
    private Func<Guid, HotkeyBinding?, HotkeyRegistrationResult>? _registerHotkey;

    public MainWindow()
    {
        InitializeComponent();
        Icon = MoniTopo.App.Tray.ApplicationIcon.WindowIcon;
    }

    public void Initialize(
        ConfigurationSession configuration,
        ProfileManagementService profiles,
        ActivationInteractionController activation,
        StartupSettingsCoordinator startupSettings,
        PopupHotkeySettingsCoordinator popupHotkeySettings,
        UpdateCoordinator updates,
        Action<string, Exception> logError,
        Func<Guid, HotkeyBinding?, HotkeyRegistrationResult> registerHotkey)
    {
        _profiles = profiles;
        _configuration = configuration;
        _activation = activation;
        _startupSettings = startupSettings;
        _popupHotkeySettings = popupHotkeySettings;
        _updates = updates;
        _logError = logError;
        _registerHotkey = registerHotkey;
        _viewModel = new MainWindowViewModel(configuration, updates, new WpfUiDispatcher(Dispatcher));
        DataContext = _viewModel;
        activation.BusyChanged += (_, busy) => _viewModel.SetActivationBusy(busy);
        RestoreSavedBounds(configuration.Current.ApplicationSettings.LastMainWindowBounds);
    }

    public void AllowClose() => _allowClose = true;

    public void ShowProfilesPage() => MainNavigation.SelectedIndex = 0;

    public void ShowSettingsPage() => MainNavigation.SelectedIndex = 1;

    private void OnReleaseNotesClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.AvailableUpdate is { } update)
        {
            new ReleaseNotesDialog(update) { Owner = this }.ShowDialog();
        }
    }

    public void ReportStatus(string message)
    {
        if (_viewModel is not null)
        {
            _viewModel.StatusMessage = message;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveCurrentBounds();
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        _viewModel?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RestoreSavedBounds(WindowBounds? saved)
    {
        var workAreas = System.Windows.Forms.Screen.AllScreens.Select(screen =>
        {
            var work = screen.WorkingArea;
            var scale = DesktopWindowPlacement.GetDpi(new PixelPoint(work.Left, work.Top)) / 96d;
            return new WindowBounds(work.Left / scale, work.Top / scale, work.Width / scale, work.Height / scale);
        }).ToArray();
        var primaryIndex = Array.FindIndex(System.Windows.Forms.Screen.AllScreens, screen => screen.Primary);
        var initialArea = workAreas[Math.Max(0, primaryIndex)];
        var bounds = saved ?? new WindowBounds(
            initialArea.Left + Math.Max(0, (initialArea.Width - Width) / 2),
            initialArea.Top + Math.Max(0, (initialArea.Height - Height) / 2),
            Width, Height);
        var clamped = WindowBoundsClamp.Clamp(
            bounds,
            workAreas);
        Left = clamped.Left;
        Top = clamped.Top;
        MinWidth = Math.Min(MinWidth, clamped.Width);
        MinHeight = Math.Min(MinHeight, clamped.Height);
        Width = clamped.Width;
        Height = clamped.Height;
    }

    private void SaveCurrentBounds()
    {
        if (_configuration is null || !IsLoaded)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal
            ? new WindowBounds(Left, Top, ActualWidth, ActualHeight)
            : new WindowBounds(RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height);
        if (!WindowBoundsClamp.IsUsable(bounds))
        {
            return;
        }
        try
        {
            _configuration.UpdateAsync(current => current with
            {
                ApplicationSettings = current.ApplicationSettings with { LastMainWindowBounds = bounds },
            }).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logError?.Invoke("WindowBounds", exception);
        }
    }

    private async void OnSaveCurrentClick(object sender, RoutedEventArgs e)
    {
        var dialog = new TextEntryDialog("Save current setup", "Profile name", $"Profile {(_viewModel?.Profiles.Count ?? 0) + 1}") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            var captured = await _profiles!.SaveCurrentAsync(dialog.Value).ConfigureAwait(true);
            if (captured.ExistingMatch is { } duplicate)
            {
                var update = System.Windows.MessageBox.Show(
                    $"The current setup already matches {duplicate.Name}. Update that profile instead?",
                    "Matching profile",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (update == MessageBoxResult.Yes)
                {
                    await _profiles.UpdateFromCurrentAsync(duplicate.Id).ConfigureAwait(true);
                    return $"{duplicate.Name} updated";
                }

                return "No profile was added because the current setup already exists.";
            }

            return $"{captured.Profile.Name} saved";
        });
    }

    private async void OnActivateClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedProfile is not { } profile)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            var result = await _activation!.ActivateAsync(profile, ActivationOrigin.MainWindow).ConfigureAwait(true);
            return result.Message;
        }, isActivation: true);
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedProfile is not { } profile ||
            System.Windows.MessageBox.Show(
                $"Replace {profile.Name} with the display setup currently configured in Windows?",
                "Update profile",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            await _profiles!.UpdateFromCurrentAsync(profile.Id).ConfigureAwait(true);
            return $"{profile.Name} updated";
        });
    }

    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedProfile is not { } profile)
        {
            return;
        }

        var dialog = new TextEntryDialog("Rename profile", "Profile name", profile.Name) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await RunActionAsync(async () =>
            {
                await _profiles!.RenameAsync(profile.Id, dialog.Value).ConfigureAwait(true);
                return $"Profile renamed to {dialog.Value.Trim()}";
            });
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedProfile is not { } profile ||
            System.Windows.MessageBox.Show(
                $"Delete {profile.Name}?",
                "Delete profile",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            await _profiles!.DeleteAsync(profile.Id).ConfigureAwait(true);
            _ = _registerHotkey!(profile.Id, null);
            return $"{profile.Name} deleted";
        });
    }

    private async void OnMoveUpClick(object sender, RoutedEventArgs e) => await MoveSelectedAsync(-1);

    private async void OnMoveDownClick(object sender, RoutedEventArgs e) => await MoveSelectedAsync(1);

    private async Task MoveSelectedAsync(int direction)
    {
        if (_viewModel?.SelectedProfile is not { } profile)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            await _profiles!.MoveAsync(profile.Id, direction).ConfigureAwait(true);
            return string.Empty;
        });
    }

    private async void OnHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedProfile is not { } profile)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            var dialog = new HotkeyCaptureDialog(profile.DirectHotkey) { Owner = this };
            if (dialog.ShowDialog() != true) return string.Empty;
            _ = _profiles!.ValidateHotkey(profile.Id, dialog.SelectedBinding);
            var registration = _registerHotkey!(profile.Id, dialog.SelectedBinding);
            if (!registration.Succeeded)
            {
                throw new InvalidOperationException(registration.Message);
            }

            try
            {
                await _profiles.SetHotkeyAsync(profile.Id, dialog.SelectedBinding).ConfigureAwait(true);
            }
            catch
            {
                _ = _registerHotkey(profile.Id, profile.DirectHotkey);
                throw;
            }

            return dialog.SelectedBinding is null ? "Profile hotkey removed" : "Profile hotkey assigned";
        });
    }

    private async void OnResolveBindingClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedProfile is not { } profile)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            var snapshot = await _profiles!.CaptureCurrentAsync().ConfigureAwait(true);
            var dialog = new BindingResolutionDialog(profile, snapshot) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.DisplayId is not null && dialog.SelectedDisplay is not null)
            {
                await _profiles.RememberBindingAsync(profile.Id, dialog.DisplayId, dialog.SelectedDisplay).ConfigureAwait(true);
                return "Display binding remembered";
            }

            return string.Empty;
        });
    }

    private async void OnRunAtLoginClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox checkBox || _viewModel is null)
        {
            return;
        }

        var requested = checkBox.IsChecked == true;
        await RunActionAsync(async () =>
        {
            await _startupSettings!.SetRunAtLoginAsync(requested).ConfigureAwait(true);
            return requested ? "MoniTopo will start when you sign in." : "Sign-in startup disabled.";
        });
        checkBox.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, _viewModel.RunAtLogin);
    }

    private async void OnThemeSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _startupSettings is null || sender is not System.Windows.Controls.ComboBox combo ||
            combo.SelectedValue is not ThemePreference requested || requested == _viewModel.ThemePreference) return;

        await RunActionAsync(async () =>
        {
            await _startupSettings.SetThemePreferenceAsync(requested).ConfigureAwait(true);
            return $"Theme: {requested}";
        });
        combo.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, _viewModel.ThemePreference);
    }

    private async void OnShowWindowOnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox checkBox || _viewModel is null)
        {
            return;
        }

        var requested = checkBox.IsChecked == true;
        await RunActionAsync(async () =>
        {
            await _startupSettings!.SetShowMainWindowOnLaunchAsync(requested).ConfigureAwait(true);
            return requested
                ? "The main window will open when MoniTopo starts."
                : "MoniTopo will start in the tray.";
        });
        checkBox.SetCurrentValue(
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            _viewModel.ShowMainWindowOnLaunch);
    }

    private async void OnAutomaticUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox checkBox || _viewModel is null)
        {
            return;
        }

        var requested = checkBox.IsChecked == true;
        await RunActionAsync(async () =>
        {
            await _updates!.SetAutomaticChecksEnabledAsync(requested).ConfigureAwait(true);
            return requested ? "Daily update checks enabled." : "Automatic update checks disabled.";
        });
        checkBox.SetCurrentValue(
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            _viewModel.UpdateChecksEnabled);
    }

    private async void OnPopupHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            var current = _configuration!.Current.ApplicationSettings.PopupHotkey;
            var dialog = new HotkeyCaptureDialog(current, "Open tray popup hotkey", allowRemove: false) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedBinding is not { } binding) return string.Empty;
            await _popupHotkeySettings!.SetAsync(binding).ConfigureAwait(true);
            return $"Popup hotkey changed to {HotkeyDisplayText.Format(binding)}.";
        });
    }

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e) =>
        await RunActionAsync(async () => (await _updates!.CheckNowAsync().ConfigureAwait(true)).Message);

    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e) =>
        await RunActionAsync(async () => (await _updates!.DownloadAsync().ConfigureAwait(true)).Message);

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e) =>
        await RunActionAsync(() =>
        {
            _updates!.InstallAndRestart();
            return Task.FromResult("Starting the installer…");
        });

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) =>
        _viewModel?.SetPreviewSize(Math.Max(100, e.NewSize.Width - 2), Math.Max(100, e.NewSize.Height - 2));

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PreviewHost.Height = e.NewSize.Height < 560 ? 190 : 280;
        var stacked = e.NewSize.Width < 820;
        ProfileBody.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(240);
        ProfileBody.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 18);
        ProfileBody.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ProfileBody.RowDefinitions[0].Height = stacked ? new GridLength(150) : new GridLength(1, GridUnitType.Star);
        ProfileBody.RowDefinitions[1].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        System.Windows.Controls.Grid.SetColumn(ProfileDetails, stacked ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(ProfileDetails, stacked ? 1 : 0);
    }

    private void OnRepositoryNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async Task RunActionAsync(Func<Task<string>> action, bool isActivation = false)
    {
        if (_viewModel is null || _viewModel.IsBusy)
        {
            return;
        }

        await using var interaction = !isActivation && _activation is not null
            ? await _activation.TryBeginInteractionAsync().ConfigureAwait(true)
            : null;
        if (!isActivation && _activation is not null && interaction is null)
        {
            return;
        }

        _viewModel.IsBusy = true;
        try
        {
            _viewModel.StatusMessage = await action().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is ConfigurationValidationException or DisplayCaptureException or InvalidOperationException or KeyNotFoundException or ActivationFailureException or UpdateClientException or IOException or UnauthorizedAccessException)
        {
            _logError?.Invoke("MainWindow", exception);
            _viewModel.StatusMessage = exception.Message;
        }
        finally
        {
            _viewModel.IsBusy = false;
        }
    }
}
