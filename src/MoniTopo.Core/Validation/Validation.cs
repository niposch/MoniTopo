using MoniTopo.Core.Configuration;
using MoniTopo.Core.Models;

namespace MoniTopo.Core.Validation;

public sealed record ValidationError(string Code, string Message);

public sealed class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException(IReadOnlyList<ValidationError> errors)
        : base(string.Join(" ", errors.Select(error => error.Message)))
    {
        Errors = errors;
    }

    public IReadOnlyList<ValidationError> Errors { get; }
}

public static class ProfileValidator
{
    public static IReadOnlyList<ValidationError> Validate(DisplayProfile profile)
    {
        var errors = new List<ValidationError>();
        var trimmedName = profile.Name.Trim();
        if (trimmedName.Length is < 1 or > 64)
        {
            errors.Add(new ValidationError("profile.name.length", "Profile names must contain 1 to 64 characters."));
        }

        if (profile.Displays.Count == 0)
        {
            errors.Add(new ValidationError("profile.displays.empty", "A profile must contain at least one active display."));
            return errors;
        }

        var duplicateDisplayId = profile.Displays
            .GroupBy(display => display.DisplayId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDisplayId is not null)
        {
            errors.Add(new ValidationError("profile.display.duplicate", $"Display ID '{duplicateDisplayId.Key}' is used more than once."));
        }

        var primaryDisplays = profile.Displays.Where(display => display.IsPrimary).ToArray();
        if (primaryDisplays.Length != 1 ||
            !string.Equals(primaryDisplays.FirstOrDefault()?.DisplayId, profile.PrimaryDisplayId, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new ValidationError("profile.primary.invalid", "A profile must identify exactly one active primary display."));
        }

        foreach (var display in profile.Displays)
        {
            if (string.IsNullOrWhiteSpace(display.DisplayId) || string.IsNullOrWhiteSpace(display.SourceGroupId))
            {
                errors.Add(new ValidationError("profile.display.id", "Every display requires stable profile-local and source IDs."));
            }

            if (!display.SourceResolution.IsValid)
            {
                errors.Add(new ValidationError("profile.display.resolution", $"{display.FriendlyLabel} has an invalid resolution."));
            }

            if (!display.RefreshRate.IsValid)
            {
                errors.Add(new ValidationError("profile.display.refresh", $"{display.FriendlyLabel} has an invalid refresh rate."));
            }

            if (display.WindowsUiScalePercent is < 50 or > 500)
            {
                errors.Add(new ValidationError("profile.display.scale", $"{display.FriendlyLabel} has an unsupported Windows scale value."));
            }

            if (!display.Identity.HasAnySignal)
            {
                errors.Add(new ValidationError("profile.display.identity", $"{display.FriendlyLabel} has no usable identity information."));
            }
        }

        return errors;
    }
}

public static class ConfigurationValidator
{
    private const HotkeyModifiers KnownModifiers =
        HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows;

    public static IReadOnlyList<ValidationError> Validate(ApplicationConfiguration configuration)
    {
        var errors = new List<ValidationError>();
        if (configuration.SchemaVersion != ApplicationConfiguration.CurrentSchemaVersion)
        {
            errors.Add(new ValidationError("configuration.schema", "The configuration schema is not supported."));
        }

        foreach (var profile in configuration.Profiles)
        {
            errors.AddRange(ProfileValidator.Validate(profile));
        }

        var duplicateName = configuration.Profiles
            .GroupBy(profile => profile.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateName is not null)
        {
            errors.Add(new ValidationError("profile.name.duplicate", $"Profile name '{duplicateName.Key}' is used more than once."));
        }

        var profileIds = configuration.Profiles.Select(profile => profile.Id).ToArray();
        if (configuration.ProfileOrder.Count != profileIds.Length ||
            configuration.ProfileOrder.Distinct().Count() != configuration.ProfileOrder.Count ||
            configuration.ProfileOrder.Any(id => !profileIds.Contains(id)))
        {
            errors.Add(new ValidationError("profile.order.invalid", "Profile order must contain every profile exactly once."));
        }

        var hotkeys = new List<(string Owner, HotkeyBinding Binding)>
        {
            ("the popup", configuration.ApplicationSettings.PopupHotkey),
        };
        hotkeys.AddRange(configuration.Profiles
            .Where(profile => profile.DirectHotkey is not null)
            .Select(profile => (profile.Name, profile.DirectHotkey!)));

        foreach (var (owner, binding) in hotkeys)
        {
            if (binding.VirtualKey is < 1 or > 0xFE ||
                binding.Modifiers == HotkeyModifiers.None ||
                (binding.Modifiers & ~KnownModifiers) != 0)
            {
                errors.Add(new ValidationError("hotkey.invalid", $"The hotkey for {owner} is invalid."));
            }
        }

        var duplicateHotkey = hotkeys
            .GroupBy(item => item.Binding)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateHotkey is not null)
        {
            errors.Add(new ValidationError(
                "hotkey.conflict",
                $"The same hotkey is assigned to {string.Join(" and ", duplicateHotkey.Select(item => item.Owner))}."));
        }

        if (configuration.LastActivatedProfileId is Guid activeId && !profileIds.Contains(activeId))
        {
            errors.Add(new ValidationError("profile.lastActivated.missing", "The last activated profile no longer exists."));
        }

        return errors;
    }

    public static void EnsureValid(ApplicationConfiguration configuration)
    {
        var errors = Validate(configuration);
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }
    }
}
