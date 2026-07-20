using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MoniTopo.Core.Models;

namespace MoniTopo.App.Popup;

public sealed record PopupProfileItem(
    Guid Id,
    string Name,
    int ActiveDisplayCount,
    string HotkeyText,
    bool IsActive);

public sealed class PopupViewModel : INotifyPropertyChanged
{
    private int _selectedIndex = -1;
    private string _currentState = "Custom";
    private string? _statusMessage;
    private bool _isBusy;
    private bool _updateAvailable;

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

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        set => SetField(ref _updateAvailable, value);
    }

    public PopupProfileItem? SelectedProfile =>
        SelectedIndex >= 0 && SelectedIndex < Profiles.Count ? Profiles[SelectedIndex] : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Load(IReadOnlyList<DisplayProfile> profiles, Guid? activeProfileId)
    {
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(new PopupProfileItem(
                profile.Id,
                profile.Name,
                profile.Displays.Count,
                FormatHotkey(profile.DirectHotkey),
                profile.Id == activeProfileId));
        }

        CurrentState = Profiles.FirstOrDefault(profile => profile.IsActive)?.Name ?? "Custom";
        SelectedIndex = Profiles.Count == 0
            ? -1
            : Math.Max(0, Profiles.ToList().FindIndex(profile => profile.IsActive));
        StatusMessage = null;
        IsBusy = false;
    }

    public void MoveSelection(int direction)
    {
        if (IsBusy || Profiles.Count == 0 || direction == 0)
        {
            return;
        }

        SelectedIndex = Math.Clamp(SelectedIndex + Math.Sign(direction), 0, Profiles.Count - 1);
    }

    public void BeginActivation(string profileName)
    {
        IsBusy = true;
        StatusMessage = $"Activating {profileName}…";
    }

    public void ReportProgress(string message) => StatusMessage = message;

    public void CompleteActivation(bool succeeded, string message, Guid? activeProfileId)
    {
        IsBusy = false;
        StatusMessage = message;
        for (var index = 0; index < Profiles.Count; index++)
        {
            Profiles[index] = Profiles[index] with { IsActive = succeeded && Profiles[index].Id == activeProfileId };
        }

        CurrentState = succeeded
            ? Profiles.FirstOrDefault(profile => profile.Id == activeProfileId)?.Name ?? "Custom"
            : CurrentState;
    }

    private static string FormatHotkey(HotkeyBinding? binding)
    {
        if (binding is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (binding.Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(binding.VirtualKey is >= 0x30 and <= 0x5A ? ((char)binding.VirtualKey).ToString() : $"VK {binding.VirtualKey:X2}");
        return string.Join('+', parts);
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
    }
}
