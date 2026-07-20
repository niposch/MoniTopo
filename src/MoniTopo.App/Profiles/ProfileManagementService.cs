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
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<CapturedDisplaySnapshot> CaptureCurrentAsync(CancellationToken cancellationToken = default) =>
        captureService.CaptureAsync(cancellationToken);

    public async Task<ProfileCaptureResult> SaveCurrentAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureUniqueName(name, exceptProfileId: null);
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
            2,
            []));
        var duplicate = configuration.Current.Profiles.FirstOrDefault(existing => matcher.Match(existing, snapshot).IsMatch);
        if (duplicate is null)
        {
            await configuration.UpdateAsync(current => current with
            {
                Profiles = [.. current.Profiles, profile],
                ProfileOrder = [.. current.ProfileOrder, profile.Id],
            }, cancellationToken).ConfigureAwait(false);
        }

        return new ProfileCaptureResult(profile, duplicate);
    }

    public async Task<DisplayProfile> UpdateFromCurrentAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var existing = Find(profileId);
        var snapshot = await captureService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var updated = ProfileNormalizer.Normalize(existing with
        {
            UpdatedUtc = _timeProvider.GetUtcNow(),
            Displays = snapshot.ActivePaths,
            PrimaryDisplayId = snapshot.PrimaryDisplayId,
            CaptureSchemaVersion = 2,
            LastSuccessfulIdentityBindings = [],
        });
        await ReplaceAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task RenameAsync(Guid profileId, string name, CancellationToken cancellationToken = default)
    {
        EnsureUniqueName(name, profileId);
        var profile = Find(profileId) with { Name = name, UpdatedUtc = _timeProvider.GetUtcNow() };
        await ReplaceAsync(ProfileNormalizer.Normalize(profile), cancellationToken).ConfigureAwait(false);
    }

    public Task SetHotkeyAsync(
        Guid profileId,
        HotkeyBinding? hotkey,
        CancellationToken cancellationToken = default)
    {
        var updated = ValidateHotkey(profileId, hotkey);
        return ReplaceAsync(updated, cancellationToken);
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

    public Task RememberBindingAsync(
        Guid profileId,
        string displayId,
        string runtimeId,
        CancellationToken cancellationToken = default)
    {
        var profile = Find(profileId);
        var bindings = profile.LastSuccessfulIdentityBindings
            .Where(binding => !string.Equals(binding.DisplayId, displayId, StringComparison.OrdinalIgnoreCase))
            .Append(new IdentityBinding(displayId, runtimeId))
            .ToArray();
        return ReplaceAsync(profile with { LastSuccessfulIdentityBindings = bindings }, cancellationToken);
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

    private Task ReplaceAsync(DisplayProfile updated, CancellationToken cancellationToken)
    {
        var candidate = configuration.Current with
        {
            Profiles = configuration.Current.Profiles.Select(profile => profile.Id == updated.Id ? updated : profile).ToArray(),
        };
        var errors = ConfigurationValidator.Validate(candidate);
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }

        return configuration.UpdateAsync(_ => candidate, cancellationToken);
    }

    private DisplayProfile Find(Guid profileId) =>
        configuration.Current.Profiles.FirstOrDefault(profile => profile.Id == profileId)
        ?? throw new KeyNotFoundException("The profile does not exist.");

    private void EnsureUniqueName(string name, Guid? exceptProfileId)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is < 1 or > 64)
        {
            throw new ConfigurationValidationException(
                [new ValidationError("profile.name.length", "Profile names must contain 1 to 64 characters.")]);
        }

        if (configuration.Current.Profiles.Any(profile =>
                profile.Id != exceptProfileId && string.Equals(profile.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConfigurationValidationException(
                [new ValidationError("profile.name.duplicate", $"Profile name '{trimmed}' is already used.")]);
        }
    }
}
