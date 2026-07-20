using MoniTopo.Core.Models;
using MoniTopo.Core.Validation;

namespace MoniTopo.Core.Normalization;

public static class ProfileNormalizer
{
    public static DisplayProfile Normalize(DisplayProfile profile)
    {
        var validationErrors = ProfileValidator.Validate(profile);
        if (validationErrors.Count > 0)
        {
            throw new ConfigurationValidationException(validationErrors);
        }

        var primaryPosition = profile.Displays.Single(display => display.IsPrimary).Position;
        var displays = profile.Displays
            .Select(display => display with
            {
                DisplayId = display.DisplayId.Trim(),
                SourceGroupId = display.SourceGroupId.Trim(),
                CloneGroupId = NormalizeOptional(display.CloneGroupId),
                Position = display.Position - primaryPosition,
                RefreshRate = display.RefreshRate.Normalize(),
                FriendlyLabel = display.FriendlyLabel.Trim(),
                Identity = NormalizeIdentity(display.Identity),
            })
            .ToArray();

        return profile with
        {
            Name = profile.Name.Trim(),
            Displays = displays,
            PrimaryDisplayId = profile.PrimaryDisplayId.Trim(),
            LastSuccessfulIdentityBindings = profile.LastSuccessfulIdentityBindings
                .Select(binding => binding with
                {
                    DisplayId = binding.DisplayId.Trim(),
                    RuntimeIdentityKey = binding.RuntimeIdentityKey.Trim(),
                })
                .ToArray(),
        };
    }

    private static MonitorIdentityFingerprint NormalizeIdentity(MonitorIdentityFingerprint identity) => identity with
    {
        MonitorDevicePath = NormalizeOptional(identity.MonitorDevicePath),
        DeviceInstanceId = NormalizeOptional(identity.DeviceInstanceId),
        DeviceContainerId = NormalizeOptional(identity.DeviceContainerId),
        EdidSerial = NormalizeOptional(identity.EdidSerial),
        EdidManufacturerId = NormalizeOptional(identity.EdidManufacturerId)?.ToUpperInvariant(),
        FriendlyModelName = NormalizeOptional(identity.FriendlyModelName),
        SupportedModeSignature = NormalizeOptional(identity.SupportedModeSignature),
    };

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
