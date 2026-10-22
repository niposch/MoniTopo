using MoniTopo.Core.Activation;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Persistence;
using MoniTopo.Core.Updates;
using MoniTopo.Core.Validation;

namespace MoniTopo.App.State;

public sealed class ConfigurationSession(IConfigurationStore store, ApplicationConfiguration current)
    : IActivationStateStore, IUpdateSettingsStore, IDisposable
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private int _disposed;

    public ApplicationConfiguration Current { get; private set; } = current;

    public event EventHandler<ApplicationConfiguration>? Changed;
    public event EventHandler<Exception>? NotificationFailed;

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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var next = update(Current);
            ConfigurationValidator.EnsureValid(next);
            if (ReferenceEquals(next, Current)) return;
            await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            Current = next;
        }
        finally
        {
            _saveGate.Release();
        }

        // Subscribers may save again, so no observer runs under the persistence gate.
        // UI subscribers must read Current when their dispatcher callback executes.
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
        {
            try { ((EventHandler<ApplicationConfiguration>)subscriber)(this, Current); }
            catch (Exception exception)
            {
                foreach (var observer in NotificationFailed?.GetInvocationList() ?? [])
                {
                    try { ((EventHandler<Exception>)observer)(this, exception); }
                    catch (Exception notificationException) { System.Diagnostics.Trace.TraceError("Configuration notification failed: {0}", notificationException); }
                }
            }
        }
    }

    // Do not dispose a gate that a pending save still needs to release.
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
