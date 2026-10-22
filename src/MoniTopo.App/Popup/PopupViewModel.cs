using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MoniTopo.App.Interaction;
using MoniTopo.Core.Models;
using MoniTopo.Core.Updates;

namespace MoniTopo.App.Popup;

public sealed record PopupProfileItem(
    Guid Id,
    string Name,
    int ActiveDisplayCount,
    string HotkeyText,
    bool IsActive)
{
    public string DisplayCountText => $"{ActiveDisplayCount} display{(ActiveDisplayCount == 1 ? string.Empty : "s")}";
    public IReadOnlyList<SchematicMonitor> Monitors { get; init; } = [];
    public string AccessibleName => $"{Name}{(string.IsNullOrEmpty(HotkeyText) ? string.Empty : $", {HotkeyText}")}{(IsActive ? ", active" : string.Empty)}";
}

public sealed class PopupViewModel : INotifyPropertyChanged
{
    private int _selectedIndex = -1;
    private string _currentState = "Custom";
    private string? _statusMessage;
    private bool _isBusy;
    private UpdateState _updateState = UpdateState.Idle;

    public PopupViewModel(string versionText = "Development build")
    {
        VersionText = versionText;
    }

    public ObservableCollection<PopupProfileItem> Profiles { get; } = [];

    public string CurrentState
    {
        get => _currentState;
        private set => SetField(ref _currentState, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetField(ref _selectedIndex, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public string VersionText { get; }

    public AvailableUpdate? AvailableUpdate => _updateState.Update;

    public bool CanViewReleaseNotes => !IsBusy && AvailableUpdate is not null;

    public bool ShowUpdateAction => _updateState.Status is
        UpdateStatus.Available or UpdateStatus.Downloading or UpdateStatus.ReadyToInstall;

    public bool CanRunUpdateAction => !IsBusy && (_updateState.Status is UpdateStatus.Available or UpdateStatus.ReadyToInstall);

    public string UpdateActionText => _updateState.Status switch
    {
        UpdateStatus.Available => $"Download update · {_updateState.Update?.DisplayVersion}",
        UpdateStatus.Downloading => $"Downloading… {_updateState.ProgressPercent}%",
        UpdateStatus.ReadyToInstall => $"Restart now · {_updateState.Update?.DisplayVersion}",
        _ => string.Empty,
    };

    public PopupProfileItem? SelectedProfile =>
        SelectedIndex >= 0 && SelectedIndex < Profiles.Count ? Profiles[SelectedIndex] : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Load(IReadOnlyList<DisplayProfile> profiles, Guid? activeProfileId)
    {
        RefreshProfiles(profiles, activeProfileId, selectActive: true);
        if (!IsBusy)
        {
            StatusMessage = null;
        }
    }

    public void RefreshProfiles(IReadOnlyList<DisplayProfile> profiles, Guid? activeProfileId, bool selectActive = false)
    {
        var selectedId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(new PopupProfileItem(
                profile.Id,
                profile.Name,
                profile.Displays.Count,
                HotkeyDisplayText.Format(profile.DirectHotkey),
                profile.Id == activeProfileId)
            {
                Monitors = LayoutSchematic.Build(profile),
            });
        }

        CurrentState = Profiles.FirstOrDefault(profile => profile.IsActive)?.Name ?? "Custom";
        var selected = selectActive ? -1 : Profiles.ToList().FindIndex(profile => profile.Id == selectedId);
        SelectedIndex = Profiles.Count == 0 ? -1 : selected >= 0
            ? selected
            : Math.Max(0, Profiles.ToList().FindIndex(profile => profile.IsActive));
    }

    public void SetActivationBusy(bool busy) => IsBusy = busy;

    public void MoveSelection(int direction)
    {
        if (IsBusy || Profiles.Count == 0 || direction == 0)
        {
            return;
        }

        SelectedIndex = Math.Clamp(SelectedIndex + direction, 0, Profiles.Count - 1);
    }

    public void BeginActivation(string profileName)
    {
        IsBusy = true;
        StatusMessage = $"Activating {profileName}…";
    }

    public void ReportProgress(string message) => StatusMessage = message;

    public void ApplyUpdateState(UpdateState state)
    {
        _updateState = state;
        if (state.Status is UpdateStatus.Error or UpdateStatus.NotInstalled)
        {
            StatusMessage = state.Message;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowUpdateAction)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRunUpdateAction)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateActionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableUpdate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanViewReleaseNotes)));
    }

    public void CompleteActivation(bool succeeded, string message, Guid? activeProfileId)
    {
        IsBusy = false;
        StatusMessage = message;
        for (var index = 0; index < Profiles.Count; index++)
        {
            Profiles[index] = Profiles[index] with { IsActive = Profiles[index].Id == activeProfileId };
        }

        CurrentState = Profiles.FirstOrDefault(profile => profile.Id == activeProfileId)?.Name ?? "Custom";
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(SelectedIndex))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProfile)));
        }
        else if (propertyName == nameof(StatusMessage))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStatusMessage)));
        }
        else if (propertyName == nameof(IsBusy))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRunUpdateAction)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanViewReleaseNotes)));
        }
    }
}
