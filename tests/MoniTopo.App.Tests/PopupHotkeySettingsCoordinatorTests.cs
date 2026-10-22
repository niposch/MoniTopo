using MoniTopo.App.Interaction;
using MoniTopo.App.Settings;
using MoniTopo.App.State;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Models;
using MoniTopo.Core.Persistence;
using MoniTopo.Windows.Input;

namespace MoniTopo.App.Tests;

public sealed class PopupHotkeySettingsCoordinatorTests
{
    [Fact]
    public async Task WorkingRegistrationIsPersisted()
    {
        using var session = new ConfigurationSession(new MemoryStore(), ApplicationConfiguration.CreateDefault());
        var replacement = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x50);
        var coordinator = new PopupHotkeySettingsCoordinator(
            session,
            _ => new HotkeyRegistrationResult(true, null, false));

        await coordinator.SetAsync(replacement);

        Assert.Equal(replacement, session.Current.ApplicationSettings.PopupHotkey);
    }

    [Fact]
    public async Task WindowsConflictRetainsPersistedBinding()
    {
        using var session = new ConfigurationSession(new MemoryStore(), ApplicationConfiguration.CreateDefault());
        var previous = session.Current.ApplicationSettings.PopupHotkey;
        var coordinator = new PopupHotkeySettingsCoordinator(
            session,
            _ => new HotkeyRegistrationResult(false, "Already used", true));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SetAsync(
            new HotkeyBinding(HotkeyModifiers.Alt, 0x50)));

        Assert.Contains("Already used", exception.Message, StringComparison.Ordinal);
        Assert.Equal(previous, session.Current.ApplicationSettings.PopupHotkey);
    }

    [Fact]
    public async Task SaveFailureRestoresPreviousWorkingRegistration()
    {
        using var session = new ConfigurationSession(
            new MemoryStore { Failure = new IOException("disk full") },
            ApplicationConfiguration.CreateDefault());
        var previous = session.Current.ApplicationSettings.PopupHotkey;
        var registrations = new List<HotkeyBinding>();
        var coordinator = new PopupHotkeySettingsCoordinator(session, binding =>
        {
            registrations.Add(binding);
            return new HotkeyRegistrationResult(true, null, false);
        });

        await Assert.ThrowsAsync<IOException>(() => coordinator.SetAsync(
            new HotkeyBinding(HotkeyModifiers.Shift, 0x50)));

        Assert.Equal(previous, registrations[1]);
        Assert.Equal(previous, session.Current.ApplicationSettings.PopupHotkey);
    }

    [Fact]
    public async Task DelayedSaveFailureRestoresRegistrationThroughOwningDispatcher()
    {
        var store = new DelayedFailureStore();
        using var session = new ConfigurationSession(store, ApplicationConfiguration.CreateDefault());
        var previous = session.Current.ApplicationSettings.PopupHotkey;
        var dispatcher = new CheckingDispatcher();
        var registrations = new List<HotkeyBinding>();
        using var coordinator = new PopupHotkeySettingsCoordinator(session, binding =>
        {
            Assert.True(dispatcher.IsInvoking);
            registrations.Add(binding);
            return new(true, null, false);
        }, dispatcher);
        var pending = coordinator.SetAsync(new(HotkeyModifiers.Shift, 0x50));
        await store.Started.Task;
        store.Failure.SetException(new IOException("disk full"));
        await Assert.ThrowsAsync<IOException>(() => pending);
        Assert.Equal(2, registrations.Count);
        Assert.Equal(previous, registrations[1]);
        Assert.Equal(2, dispatcher.CallCount);
    }

    private sealed class CheckingDispatcher : IUiDispatcher
    {
        internal bool IsInvoking { get; private set; }
        internal int CallCount { get; private set; }
        public Task InvokeAsync(Action action)
        {
            CallCount++;
            IsInvoking = true;
            try { action(); }
            finally { IsInvoking = false; }
            return Task.CompletedTask;
        }
    }

    private sealed class DelayedFailureStore : IConfigurationStore
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return Failure.Task;
        }
    }

    private sealed class MemoryStore : IConfigurationStore
    {
        public Exception? Failure { get; init; }

        public Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ApplicationConfiguration.CreateDefault());

        public Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default) =>
            Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
