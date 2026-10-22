using MoniTopo.App.State;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Persistence;

namespace MoniTopo.App.Tests;

public sealed class ConfigurationSessionTests
{
    [Fact]
    public async Task NotificationCanSaveAgainWithoutHoldingPersistenceGate()
    {
        using var session = new ConfigurationSession(new MemoryStore(), ApplicationConfiguration.CreateDefault());
        var nested = false;
        session.Changed += (_, _) =>
        {
            if (nested) return;
            nested = true;
            session.SetAutomaticUpdateChecksEnabledAsync(false).GetAwaiter().GetResult();
        };
        await session.UpdateAsync(current => current with { LastUpdateCheckUtc = DateTimeOffset.UtcNow }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(nested);
        Assert.False(session.Current.ApplicationSettings.UpdateChecksEnabled);
    }

    [Fact]
    public async Task FailingSubscriberCannotTurnCommittedSaveIntoFailureOrSkipOtherSubscribers()
    {
        var store = new MemoryStore();
        using var session = new ConfigurationSession(store, ApplicationConfiguration.CreateDefault());
        var observed = false;
        Exception? notificationFailure = null;
        session.Changed += (_, _) => throw new InvalidOperationException("observer failed");
        session.Changed += (_, _) => observed = true;
        session.NotificationFailed += (_, failure) => notificationFailure = failure;
        await session.SetAutomaticUpdateChecksEnabledAsync(false);
        Assert.False(session.Current.ApplicationSettings.UpdateChecksEnabled);
        Assert.False(store.Saved!.ApplicationSettings.UpdateChecksEnabled);
        Assert.True(observed);
        Assert.IsType<InvalidOperationException>(notificationFailure);
    }

    [Fact]
    public async Task DisposalDuringSaveDoesNotDisposeGateBeforeRelease()
    {
        var store = new MemoryStore { Block = true };
        var session = new ConfigurationSession(store, ApplicationConfiguration.CreateDefault());
        var save = session.SetAutomaticUpdateChecksEnabledAsync(false);
        await store.Started.Task;
        session.Dispose();
        store.Complete.TrySetResult();
        await save;
        Assert.False(session.Current.ApplicationSettings.UpdateChecksEnabled);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetAutomaticUpdateChecksEnabledAsync(true));
    }

    private sealed class MemoryStore : IConfigurationStore
    {
        internal ApplicationConfiguration? Saved { get; private set; }
        internal bool Block { get; init; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            if (Block) await Complete.Task.WaitAsync(cancellationToken);
            Saved = configuration;
        }
    }
}
