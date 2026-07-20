using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using MoniTopo.App.Dialogs;
using MoniTopo.App.Interaction;
using MoniTopo.App.Profiles;
using MoniTopo.App.State;
using MoniTopo.Core.Activation;
using MoniTopo.Core.Models;
using MoniTopo.Core.Validation;
using MoniTopo.Windows.Display;
using MoniTopo.Windows.Input;

namespace MoniTopo.App;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private MainWindowViewModel? _viewModel;
    private ProfileManagementService? _profiles;
    private ActivationInteractionController? _activation;
    private Func<Guid, HotkeyBinding?, HotkeyRegistrationResult>? _registerHotkey;

    public MainWindow()
    {
        InitializeComponent();
    }

    public void Initialize(
        ConfigurationSession configuration,
        ProfileManagementService profiles,
        ActivationInteractionController activation,
        Func<Guid, HotkeyBinding?, HotkeyRegistrationResult> registerHotkey)
    {
        _profiles = profiles;
        _activation = activation;
        _registerHotkey = registerHotkey;
        _viewModel = new MainWindowViewModel(configuration);
        DataContext = _viewModel;
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
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
        });
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

        var dialog = new HotkeyCaptureDialog(profile.DirectHotkey) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
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
            if (dialog.ShowDialog() == true && dialog.DisplayId is not null && dialog.RuntimeId is not null)
            {
                await _profiles.RememberBindingAsync(profile.Id, dialog.DisplayId, dialog.RuntimeId).ConfigureAwait(true);
                return "Display binding remembered";
            }

            return string.Empty;
        });
    }

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) =>
        _viewModel?.SetPreviewSize(Math.Max(100, e.NewSize.Width - 2), Math.Max(100, e.NewSize.Height - 2));

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PreviewHost.Height = e.NewSize.Height < 560 ? 190 : 280;
    }

    private void OnRepositoryNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async Task RunActionAsync(Func<Task<string>> action)
    {
        if (_viewModel is null || _viewModel.IsBusy)
        {
            return;
        }

        _viewModel.IsBusy = true;
        try
        {
            _viewModel.StatusMessage = await action().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is ConfigurationValidationException or DisplayCaptureException or InvalidOperationException or KeyNotFoundException or ActivationFailureException)
        {
            _viewModel.StatusMessage = exception.Message;
        }
        finally
        {
            _viewModel.IsBusy = false;
        }
    }
}
