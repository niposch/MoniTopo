using MoniTopo.Core.Activation;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Persistence;

namespace MoniTopo.App.State;

public sealed class ConfigurationSession(IConfigurationStore store, ApplicationConfiguration current)
    : IActivationStateStore, IDisposable
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public ApplicationConfiguration Current { get; private set; } = current;

    public event EventHandler<ApplicationConfiguration>? Changed;

    public static async Task<ConfigurationSession> LoadAsync(
        IConfigurationStore store,
        CancellationToken cancellationToken = default) =>
        new(store, await store.LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task SetLastActivatedProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await UpdateAsync(configuration => configuration with { LastActivatedProfileId = profileId }, cancellationToken)
            .ConfigureAwait(false);
    }

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
