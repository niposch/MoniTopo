namespace MoniTopo.Core.Models;

public static class DisplayProfileCaptureSchema
{
    public const int Current = 4;
    public const int MinimumActivatable = 3;
}

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

public sealed record IdentityBinding(string DisplayId, string RuntimeIdentityKey)
{
    // Older bindings only contain a transient address. Keep them readable, but never
    // use that address alone as evidence of a physical monitor's identity.
    public MonitorIdentityFingerprint? Identity { get; init; }
}

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
