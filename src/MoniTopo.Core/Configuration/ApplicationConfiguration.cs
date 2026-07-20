using MoniTopo.Core.Models;

namespace MoniTopo.Core.Configuration;

public readonly record struct WindowBounds(double Left, double Top, double Width, double Height);

public sealed record ApplicationSettings(
    bool RunAtLogin,
    HotkeyBinding PopupHotkey,
    bool UpdateChecksEnabled,
    WindowBounds? LastMainWindowBounds,
    bool FirstRunCompleted,
    bool ShowMainWindowOnLaunch)
{
    public static ApplicationSettings Default { get; } = new(
        RunAtLogin: true,
        PopupHotkey: new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4D),
        UpdateChecksEnabled: true,
        LastMainWindowBounds: null,
        FirstRunCompleted: false,
        ShowMainWindowOnLaunch: false);
}

public sealed record ApplicationConfiguration(
    int SchemaVersion,
    ApplicationSettings ApplicationSettings,
    IReadOnlyList<DisplayProfile> Profiles,
    IReadOnlyList<Guid> ProfileOrder,
    Guid? LastActivatedProfileId,
    DateTimeOffset? LastUpdateCheckUtc)
{
    public const int CurrentSchemaVersion = 2;

    public static ApplicationConfiguration CreateDefault() => new(
        CurrentSchemaVersion,
        ApplicationSettings.Default,
        Array.Empty<DisplayProfile>(),
        Array.Empty<Guid>(),
        null,
        null);
}
