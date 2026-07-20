using MoniTopo.Core.Configuration;
using MoniTopo.Core.Models;

namespace MoniTopo.Core.Tests;

internal static class TestData
{
    public static MonitorIdentityFingerprint Identity(string serial = "SYNTHETIC-1") => new(
        MonitorDevicePath: $"synthetic://monitor/{serial}",
        DeviceInstanceId: $"SYNTHETIC\\{serial}",
        DeviceContainerId: null,
        EdidSerial: serial,
        EdidManufacturerId: "TST",
        EdidProductCode: 42,
        FriendlyModelName: "Synthetic Panel",
        PhysicalWidthMillimeters: 600,
        PhysicalHeightMillimeters: 340,
        OutputTechnology: DisplayOutputTechnology.DisplayPort,
        ConnectorInstance: 1,
        PreferredMode: new DisplaySize(2560, 1440),
        SupportedModeSignature: "synthetic-modes-v1");

    public static DesiredDisplayPath Display(
        string id = "display-1",
        bool primary = true,
        DisplayPoint? position = null,
        string? serial = null) => new(
            DisplayId: id,
            Identity: Identity(serial ?? id),
            SourceGroupId: $"source-{id}",
            CloneGroupId: null,
            Position: position ?? new DisplayPoint(0, 0),
            SourceResolution: new DisplaySize(2560, 1440),
            RefreshRate: new RefreshRate(60000, 1000),
            Orientation: DisplayOrientation.Landscape,
            PathScaling: DisplayPathScaling.Identity,
            WindowsUiScalePercent: 150,
            HdrEnabled: false,
            IsPrimary: primary,
            FriendlyLabel: $"Synthetic {id}");

    public static DisplayProfile Profile(
        string name = "Desktop",
        Guid? id = null,
        HotkeyBinding? hotkey = null,
        IReadOnlyList<DesiredDisplayPath>? displays = null,
        string primaryDisplayId = "display-1") => new(
            Id: id ?? Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Name: name,
            CreatedUtc: new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
            UpdatedUtc: new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
            DirectHotkey: hotkey,
            Displays: displays ?? [Display()],
            PrimaryDisplayId: primaryDisplayId,
            CaptureSchemaVersion: 1,
            LastSuccessfulIdentityBindings: Array.Empty<IdentityBinding>());

    public static ApplicationConfiguration Configuration(params DisplayProfile[] profiles) => new(
        SchemaVersion: ApplicationConfiguration.CurrentSchemaVersion,
        ApplicationSettings: ApplicationSettings.Default,
        Profiles: profiles,
        ProfileOrder: profiles.Select(profile => profile.Id).ToArray(),
        LastActivatedProfileId: null,
        LastUpdateCheckUtc: null);
}
