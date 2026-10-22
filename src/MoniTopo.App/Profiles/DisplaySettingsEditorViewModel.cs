using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MoniTopo.Core.Models;

namespace MoniTopo.App.Profiles;

public sealed record ResolutionOption(DisplaySize Value)
{
    public override string ToString() => $"{Value.Width} × {Value.Height}";
}

public sealed record RefreshOption(AvailableDisplayMode Value)
{
    public override string ToString() => $"{Value.RefreshRate.Hertz:0.##} Hz{(Value.ScanLineOrdering > 1 ? " (interlaced)" : string.Empty)}";
}

public sealed record ScaleOption(int Value)
{
    public override string ToString() => $"{Value}%";
}

public sealed record HdrOption(bool Value)
{
    public override string ToString() => Value ? "On" : "Off";
}

public sealed class DisplaySettingsEditorViewModel : INotifyPropertyChanged
{
    public DisplaySettingsEditorViewModel(DisplayProfile profile, IReadOnlyList<DisplaySettingsCapability> capabilities)
    {
        Original = profile;
        Displays = profile.Displays.Select(display => new DisplaySettingsEditorItem(display,
            capabilities.SingleOrDefault(capability => capability.DisplayId == display.DisplayId) ??
                new DisplaySettingsCapability(display.DisplayId, [], [], [], "Available options could not be read. Reopen the editor."))).ToArray();
        foreach (var display in Displays) display.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasChanges)));
    }

    public DisplayProfile Original { get; }
    public IReadOnlyList<DisplaySettingsEditorItem> Displays { get; }
    public bool HasChanges => Displays.Any(display => display.HasChanges);
    public IReadOnlyList<DisplaySettingsEdit> GetEdits() => Displays.Select(display => display.GetEdit()).ToArray();
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DisplaySettingsEditorItem : INotifyPropertyChanged
{
    private readonly DesiredDisplayPath _original;
    private readonly DisplaySettingsCapability _capabilities;
    private ResolutionOption _resolution;
    private RefreshOption _refresh;
    private ScaleOption _scale;
    private HdrOption _hdr;

    public DisplaySettingsEditorItem(DesiredDisplayPath display, DisplaySettingsCapability capabilities)
    {
        _original = display;
        _capabilities = capabilities;
        var mode = ProfileSettingsEditor.ModeOf(display);
        Resolutions = capabilities.Modes.Select(mode => mode.Resolution).Append(mode.Resolution).Distinct()
            .OrderByDescending(size => (long)size.Width * size.Height).ThenByDescending(size => size.Width)
            .Select(size => new ResolutionOption(size)).ToArray();
        Scales = capabilities.ScalePercentages.Append(display.WindowsUiScalePercent).Distinct().Order().Select(value => new ScaleOption(value)).ToArray();
        HdrStates = capabilities.HdrStates.Append(display.HdrEnabled).Distinct().Order().Select(value => new HdrOption(value)).ToArray();
        _resolution = Resolutions.Single(option => option.Value == mode.Resolution);
        _refresh = new RefreshOption(mode);
        _scale = Scales.Single(option => option.Value == display.WindowsUiScalePercent);
        _hdr = HdrStates.Single(option => option.Value == display.HdrEnabled);
        RebuildRefreshRates(mode);
    }

    public string Name => _original.FriendlyLabel;
    public string? AvailabilityMessage
    {
        get
        {
            var unavailable = _capabilities.Modes.Count > 0 && !_capabilities.Modes.Contains(ProfileSettingsEditor.ModeOf(_original)) ||
                _capabilities.ScalePercentages.Count > 0 && !_capabilities.ScalePercentages.Contains(_original.WindowsUiScalePercent) ||
                _capabilities.HdrStates.Count > 0 && !_capabilities.HdrStates.Contains(_original.HdrEnabled);
            return unavailable ? string.Join(" ", new[] { _capabilities.Message,
                "Some saved values are not currently available. They are kept until you select a supported replacement." }.Where(message => !string.IsNullOrWhiteSpace(message))) : _capabilities.Message;
        }
    }
    public bool CanEditMode => _capabilities.Modes.Count > 0;
    public bool CanEditScale => _capabilities.ScalePercentages.Count > 0;
    public bool CanEditHdr => _capabilities.HdrStates.Count > 0;
    public IReadOnlyList<ResolutionOption> Resolutions { get; }
    public ObservableCollection<RefreshOption> RefreshRates { get; } = [];
    public IReadOnlyList<ScaleOption> Scales { get; }
    public IReadOnlyList<HdrOption> HdrStates { get; }

    public ResolutionOption SelectedResolution
    {
        get => _resolution;
        set
        {
            if (value is null || value == _resolution) return;
            _resolution = value;
            RebuildRefreshRates(_refresh.Value);
            Changed();
        }
    }

    public RefreshOption SelectedRefreshRate { get => _refresh; set { if (value is not null) { _refresh = value; Changed(); } } }
    public ScaleOption SelectedScale { get => _scale; set { if (value is not null) { _scale = value; Changed(); } } }
    public HdrOption SelectedHdr { get => _hdr; set { if (value is not null) { _hdr = value; Changed(); } } }
    public bool HasChanges => GetEdit() != new DisplaySettingsEdit(_original.DisplayId, ProfileSettingsEditor.ModeOf(_original), _original.WindowsUiScalePercent, _original.HdrEnabled);
    public DisplaySettingsEdit GetEdit() => new(_original.DisplayId, _refresh.Value, _scale.Value, _hdr.Value);
    public event PropertyChangedEventHandler? PropertyChanged;

    private void RebuildRefreshRates(AvailableDisplayMode preferred)
    {
        var options = _capabilities.Modes.Where(mode => mode.Resolution == _resolution.Value).ToList();
        var saved = ProfileSettingsEditor.ModeOf(_original);
        if (saved.Resolution == _resolution.Value) options.Add(saved);
        RefreshRates.Clear();
        foreach (var mode in options.Distinct().OrderByDescending(mode => mode.RefreshRate.Hertz)) RefreshRates.Add(new RefreshOption(mode));
        _refresh = RefreshRates.FirstOrDefault(option => option.Value == preferred) ??
            RefreshRates.MinBy(option => Math.Abs(option.Value.RefreshRate.Hertz - preferred.RefreshRate.Hertz))!;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRefreshRate)));
    }

    private void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasChanges)));
    }
}
