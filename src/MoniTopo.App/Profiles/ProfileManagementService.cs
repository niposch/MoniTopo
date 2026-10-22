using MoniTopo.App.State;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;
using MoniTopo.Core.Normalization;
using MoniTopo.Core.Validation;
using MoniTopo.Windows.Display;

namespace MoniTopo.App.Profiles;

public sealed record ProfileCaptureResult(DisplayProfile Profile, DisplayProfile? ExistingMatch);

public sealed class ProfileManagementService(
    ConfigurationSession configuration,
    IDisplayCaptureService captureService,
    ActiveProfileMatcher matcher,
    TimeProvider? timeProvider = null,
    IDisplaySettingsCapabilities? capabilities = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<DisplaySettingsCapability>> GetSettingsCapabilitiesAsync(DisplayProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.CaptureSchemaVersion < 4)
            throw new InvalidOperationException("Update this profile from the current setup before editing its display settings.");
        return capabilities is null ? throw new InvalidOperationException("Display settings enumeration is unavailable.") :
            await capabilities.QueryAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DisplayProfile> SaveDisplaySettingsAsync(DisplayProfile original, IReadOnlyList<DisplaySettingsEdit> edits, CancellationToken cancellationToken = default)
    {
        var available = await GetSettingsCapabilitiesAsync(original, cancellationToken).ConfigureAwait(false);
        return await ReplaceAsync(original.Id, latest =>
        {
            if (!latest.Displays.SequenceEqual(original.Displays) || latest.PrimaryDisplayId != original.PrimaryDisplayId || latest.CaptureSchemaVersion != original.CaptureSchemaVersion)
                throw new InvalidOperationException("The saved display setup changed while the editor was open. Reopen it before saving.");
            return ProfileNormalizer.Normalize(ProfileSettingsEditor.Apply(latest, edits, available) with { UpdatedUtc = _timeProvider.GetUtcNow() });
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<CapturedDisplaySnapshot> CaptureCurrentAsync(CancellationToken cancellationToken = default) =>
        captureService.CaptureAsync(cancellationToken);

    public async Task<ProfileCaptureResult> SaveCurrentAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await captureService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var profile = ProfileNormalizer.Normalize(new DisplayProfile(
            Guid.NewGuid(),
            name,
            now,
            now,
            null,
            snapshot.ActivePaths,
            snapshot.PrimaryDisplayId,
            DisplayProfileCaptureSchema.Current,
            []));
        DisplayProfile? duplicate = null;
        await configuration.UpdateAsync(current =>
        {
            EnsureUniqueName(current, name, exceptProfileId: null);
            duplicate = current.Profiles.FirstOrDefault(existing => matcher.Match(existing, snapshot).IsMatch);
            return duplicate is not null ? current : current with
            {
                Profiles = [.. current.Profiles, profile],
                ProfileOrder = [.. current.ProfileOrder, profile.Id],
            };
        }, cancellationToken).ConfigureAwait(false);

        return new ProfileCaptureResult(profile, duplicate);
    }

    public async Task<DisplayProfile> UpdateFromCurrentAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await captureService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        return await ReplaceAsync(profileId, existing => ProfileNormalizer.Normalize(existing with
        {
            UpdatedUtc = _timeProvider.GetUtcNow(),
            Displays = snapshot.ActivePaths,
            PrimaryDisplayId = snapshot.PrimaryDisplayId,
            CaptureSchemaVersion = DisplayProfileCaptureSchema.Current,
            LastSuccessfulIdentityBindings = [],
        }), cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAsync(Guid profileId, string name, CancellationToken cancellationToken = default)
    {
        await ReplaceAsync(profileId, profile => ProfileNormalizer.Normalize(profile with
        { Name = name, UpdatedUtc = _timeProvider.GetUtcNow() }), cancellationToken).ConfigureAwait(false);
    }

    public Task SetHotkeyAsync(
        Guid profileId,
        HotkeyBinding? hotkey,
        CancellationToken cancellationToken = default)
    {
        return ReplaceAsync(profileId, current => current with
        { DirectHotkey = hotkey, UpdatedUtc = _timeProvider.GetUtcNow() }, cancellationToken);
    }

    public DisplayProfile ValidateHotkey(Guid profileId, HotkeyBinding? hotkey)
    {
        var updated = Find(profileId) with { DirectHotkey = hotkey, UpdatedUtc = _timeProvider.GetUtcNow() };
        var candidate = configuration.Current with
        {
            Profiles = configuration.Current.Profiles.Select(profile => profile.Id == updated.Id ? updated : profile).ToArray(),
        };
        var errors = ConfigurationValidator.Validate(candidate);
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }

        return updated;
    }

    public async Task RememberBindingAsync(
        Guid profileId,
        string displayId,
        ConnectedDisplayState selected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var snapshot = await captureService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var connected = snapshot.ConnectedDisplays.SingleOrDefault(display =>
            string.Equals(display.RuntimeId, selected.RuntimeId, StringComparison.Ordinal) && display.Identity == selected.Identity)
            ?? throw new InvalidOperationException("The selected display is no longer connected. Refresh the display list and try again.");
        await ReplaceAsync(profileId, profile =>
        {
            if (!profile.Displays.Any(display => string.Equals(display.DisplayId, displayId, StringComparison.OrdinalIgnoreCase)))
                throw new KeyNotFoundException("The saved display does not exist.");
            var bindings = profile.LastSuccessfulIdentityBindings
                .Where(binding => !string.Equals(binding.DisplayId, displayId, StringComparison.OrdinalIgnoreCase))
                .Append(new IdentityBinding(displayId, selected.RuntimeId) { Identity = connected.Identity }).ToArray();
            return profile with { LastSuccessfulIdentityBindings = bindings };
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        configuration.UpdateAsync(current => current with
        {
            Profiles = current.Profiles.Where(profile => profile.Id != profileId).ToArray(),
            ProfileOrder = current.ProfileOrder.Where(id => id != profileId).ToArray(),
            LastActivatedProfileId = current.LastActivatedProfileId == profileId ? null : current.LastActivatedProfileId,
        }, cancellationToken);

    public Task MoveAsync(Guid profileId, int direction, CancellationToken cancellationToken = default)
    {
        if (direction == 0)
        {
            return Task.CompletedTask;
        }

        return configuration.UpdateAsync(current =>
        {
            var order = current.ProfileOrder.ToList();
            var oldIndex = order.IndexOf(profileId);
            if (oldIndex < 0)
            {
                throw new KeyNotFoundException("The profile is not in the configured order.");
            }

            var newIndex = Math.Clamp(oldIndex + Math.Sign(direction), 0, order.Count - 1);
            order.RemoveAt(oldIndex);
            order.Insert(newIndex, profileId);
            return current with { ProfileOrder = order };
        }, cancellationToken);
    }

    private async Task<DisplayProfile> ReplaceAsync(Guid profileId, Func<DisplayProfile, DisplayProfile> update, CancellationToken cancellationToken)
    {
        DisplayProfile? updated = null;
        await configuration.UpdateAsync(current =>
        {
            var existing = current.Profiles.FirstOrDefault(profile => profile.Id == profileId)
                ?? throw new KeyNotFoundException("The profile does not exist.");
            updated = update(existing);
            return current with { Profiles = current.Profiles.Select(profile => profile.Id == profileId ? updated : profile).ToArray() };
        }, cancellationToken).ConfigureAwait(false);
        return updated!;
    }

    private DisplayProfile Find(Guid profileId) =>
        configuration.Current.Profiles.FirstOrDefault(profile => profile.Id == profileId)
        ?? throw new KeyNotFoundException("The profile does not exist.");

    private static void EnsureUniqueName(MoniTopo.Core.Configuration.ApplicationConfiguration current, string name, Guid? exceptProfileId)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is < 1 or > 64)
        {
            throw new ConfigurationValidationException(
                [new ValidationError("profile.name.length", "Profile names must contain 1 to 64 characters.")]);
        }

        if (current.Profiles.Any(profile =>
                profile.Id != exceptProfileId && string.Equals(profile.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConfigurationValidationException(
                [new ValidationError("profile.name.duplicate", $"Profile name '{trimmed}' is already used.")]);
        }
    }
}
