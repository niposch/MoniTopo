namespace MoniTopo.Core.Models;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

public sealed record HotkeyBinding(HotkeyModifiers Modifiers, int VirtualKey);

public sealed record IdentityBinding(string DisplayId, string RuntimeIdentityKey);

public sealed record DisplayProfile(
    Guid Id,
    string Name,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    HotkeyBinding? DirectHotkey,
    IReadOnlyList<DesiredDisplayPath> Displays,
    string PrimaryDisplayId,
    int CaptureSchemaVersion,
    IReadOnlyList<IdentityBinding> LastSuccessfulIdentityBindings);
