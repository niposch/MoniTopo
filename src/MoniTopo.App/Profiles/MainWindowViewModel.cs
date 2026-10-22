using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using MoniTopo.App.Diagnostics;
using MoniTopo.App.Interaction;
using MoniTopo.App.State;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Models;
using MoniTopo.Core.Updates;

namespace MoniTopo.App.Profiles;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ConfigurationSession _configuration;
    private readonly UpdateCoordinator _updates;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;
    private DisplayProfile? _selectedProfile;
    private string? _statusMessage;
    private bool _isBusy;
    private bool _activationBusy;
    private double _previewWidth = 560;
    private double _previewHeight = 260;
    private UpdateState _updateState;

    public MainWindowViewModel(ConfigurationSession configuration, UpdateCoordinator updates, IUiDispatcher? dispatcher = null)
    {
        _configuration = configuration;
        _updates = updates;
        _dispatcher = dispatcher ?? new ImmediateUiDispatcher();
        _updateState = updates.Current;
        _configuration.Changed += OnConfigurationChanged;
        _updates.Changed += OnUpdateChanged;
        Reload(configuration.Current);
    }

    public ObservableCollection<DisplayProfile> Profiles { get; } = [];

    public ObservableCollection<DisplayPreviewItem> PreviewItems { get; } = [];

    public DisplayProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetField(ref _selectedProfile, value))
            {
                RebuildPreview();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelection)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProfileSummary)));
            }
        }
    }

    public bool HasSelection => SelectedProfile is not null;

    public string SelectedProfileSummary => SelectedProfile is null
        ? "Select a profile to inspect its saved display setup."
        : $"{SelectedProfile.Displays.Count} active display{(SelectedProfile.Displays.Count == 1 ? string.Empty : "s")} · " +
          $"Primary: {SelectedProfile.Displays.Single(display => display.IsPrimary).FriendlyLabel}";

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy || _activationBusy;
        set
        {
            if (SetField(ref _isBusy, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInteract)));
            }
        }
    }

    public bool CanInteract => !IsBusy;

    public void SetActivationBusy(bool busy)
    {
        _activationBusy = busy;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInteract)));
    }

    public bool RunAtLogin => _configuration.Current.ApplicationSettings.RunAtLogin;

    public bool UpdateChecksEnabled => _configuration.Current.ApplicationSettings.UpdateChecksEnabled;

    public bool ShowMainWindowOnLaunch => _configuration.Current.ApplicationSettings.ShowMainWindowOnLaunch;

    public ThemePreference ThemePreference => _configuration.Current.ApplicationSettings.ThemePreference;

    public string PopupHotkeyText => HotkeyDisplayText.Format(_configuration.Current.ApplicationSettings.PopupHotkey);

    public string CurrentVersionText =>
        ApplicationBuildInfo.Current(_updates.CurrentPackageVersion, _updates.IsInstalled).DisplayVersion;

    public string UpdateStatusMessage => _updateState.Message;

    public AvailableUpdate? AvailableUpdate => _updateState.Update;

    public bool CanViewReleaseNotes => AvailableUpdate is not null;

    public int UpdateProgressPercent => _updateState.ProgressPercent;

    public bool CanCheckForUpdates => _updateState.Status is not UpdateStatus.Checking and not UpdateStatus.Downloading;

    public bool CanDownloadUpdate => _updateState.Status == UpdateStatus.Available;

    public bool CanInstallUpdate => _updateState.Status == UpdateStatus.ReadyToInstall;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        _disposed = true;
        _configuration.Changed -= OnConfigurationChanged;
        _updates.Changed -= OnUpdateChanged;
    }

    public void SetPreviewSize(double width, double height)
    {
        _previewWidth = Math.Max(100, width);
        _previewHeight = Math.Max(100, height);
        RebuildPreview();
    }

    private void OnConfigurationChanged(object? sender, ApplicationConfiguration configuration)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (!_disposed) Reload(_configuration.Current);
        });
    }

    private void OnUpdateChanged(object? sender, UpdateState state)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (!_disposed) ApplyUpdateState(_updates.Current);
        });
    }

    private void ApplyUpdateState(UpdateState state)
    {
        _updateState = state;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateStatusMessage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableUpdate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanViewReleaseNotes)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateProgressPercent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCheckForUpdates)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDownloadUpdate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInstallUpdate)));
    }

    private void Reload(ApplicationConfiguration configuration)
    {
        var selectedId = SelectedProfile?.Id;
        var byId = configuration.Profiles.ToDictionary(profile => profile.Id);
        Profiles.Clear();
        foreach (var id in configuration.ProfileOrder)
        {
            if (byId.TryGetValue(id, out var profile))
            {
                Profiles.Add(profile);
            }
        }

        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == selectedId) ?? Profiles.FirstOrDefault();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunAtLogin)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateChecksEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowMainWindowOnLaunch)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThemePreference)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PopupHotkeyText)));
    }

    private void RebuildPreview()
    {
        PreviewItems.Clear();
        if (SelectedProfile is null)
        {
            return;
        }

        foreach (var item in DisplayLayoutPreview.Build(SelectedProfile, _previewWidth, _previewHeight))
        {
            PreviewItems.Add(item);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
