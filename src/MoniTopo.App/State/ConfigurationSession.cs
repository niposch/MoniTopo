using MoniTopo.Core.Activation;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Persistence;
using MoniTopo.Core.Updates;

namespace MoniTopo.App.State;

public sealed class ConfigurationSession(IConfigurationStore store, ApplicationConfiguration current)
    : IActivationStateStore, IUpdateSettingsStore, IDisposable
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public ApplicationConfiguration Current { get; private set; } = current;

    public event EventHandler<ApplicationConfiguration>? Changed;

    public bool AutomaticUpdateChecksEnabled => Current.ApplicationSettings.UpdateChecksEnabled;

    public DateTimeOffset? LastSuccessfulUpdateCheckUtc => Current.LastUpdateCheckUtc;

    public static async Task<ConfigurationSession> LoadAsync(
        IConfigurationStore store,
        CancellationToken cancellationToken = default) =>
        new(store, await store.LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task SetLastActivatedProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await UpdateAsync(configuration => configuration with { LastActivatedProfileId = profileId }, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SetAutomaticUpdateChecksEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        UpdateAsync(current => current with
        {
            ApplicationSettings = current.ApplicationSettings with { UpdateChecksEnabled = enabled },
        }, cancellationToken);

    public Task SetLastSuccessfulUpdateCheckUtcAsync(DateTimeOffset value, CancellationToken cancellationToken = default) =>
        UpdateAsync(current => current with { LastUpdateCheckUtc = value }, cancellationToken);

    public async Task UpdateAsync(
        Func<ApplicationConfiguration, ApplicationConfiguration> update,
        CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = update(Current);
            await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            Current = next;
            Changed?.Invoke(this, next);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Dispose() => _saveGate.Dispose();
}
