using MoniTopo.App.Interaction;
using MoniTopo.App.Profiles;
using MoniTopo.App.State;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Models;
using MoniTopo.Core.Persistence;
using MoniTopo.Core.Updates;

namespace MoniTopo.App.Tests;

public sealed class MainWindowQueuedStateTests
{
    [Fact]
    public async Task NotesAreAvailableBeforeDownloadAndRemainAvailableAfterStaging()
    {
        using var session = new ConfigurationSession(new MemoryStore(), ApplicationConfiguration.CreateDefault());
        using var updates = new UpdateCoordinator(new FakeUpdateClient(), session);
        using var viewModel = new MainWindowViewModel(session, updates);
        Assert.False(viewModel.CanViewReleaseNotes);
        await updates.CheckNowAsync();
        Assert.True(viewModel.CanViewReleaseNotes);
        Assert.Equal("# Fixes\n- Synthetic notes", viewModel.AvailableUpdate?.ReleaseNotes);
        Assert.True(viewModel.CanDownloadUpdate);
        Assert.False(viewModel.CanInstallUpdate);
        await updates.DownloadAsync();
        Assert.True(viewModel.CanViewReleaseNotes);
        Assert.Equal("# Fixes\n- Synthetic notes", viewModel.AvailableUpdate?.ReleaseNotes);
    }

    [Fact]
    public async Task QueuedCallbacksReadLatestConfigurationAndUpdateStateEvenWhenDeliveredOutOfOrder()
    {
        var profile = Profile();
        using var session = new ConfigurationSession(new MemoryStore(), ApplicationConfiguration.CreateDefault() with
        {
            Profiles = [profile],
            ProfileOrder = [profile.Id],
        });
        using var updates = new UpdateCoordinator(new FakeUpdateClient(), session);
        var dispatcher = new QueuedDispatcher();
        using var viewModel = new MainWindowViewModel(session, updates, dispatcher);
        await session.UpdateAsync(current => current with { Profiles = [profile with { Name = "First rename" }] });
        await session.UpdateAsync(current => current with { Profiles = [profile with { Name = "Latest rename" }] });
        await updates.CheckNowAsync();
        await updates.DownloadAsync();

        dispatcher.DeliverInReverseOrder();

        Assert.Equal("Latest rename", Assert.Single(viewModel.Profiles).Name);
        Assert.True(viewModel.CanInstallUpdate);
        Assert.False(viewModel.CanDownloadUpdate);
        Assert.Equal(updates.Current.Message, viewModel.UpdateStatusMessage);
    }

    private static DisplayProfile Profile()
    {
        var display = new DesiredDisplayPath("display", new MonitorIdentityFingerprint("synthetic://panel", null,
            null, "SYNTHETIC", "TST", 1, "Synthetic panel", null, null, DisplayOutputTechnology.DisplayPort,
            1, new DisplaySize(1920, 1080), null), "source", null, new DisplayPoint(0, 0), new DisplaySize(1920, 1080),
            new RefreshRate(60, 1), DisplayOrientation.Landscape, DisplayPathScaling.Identity, 100, false, true, "Synthetic panel")
        {
            TargetSignal = new DisplayTargetSignal(148_500_000, new RefreshRate(67_500, 1), new RefreshRate(60, 1),
                new DisplaySize(1920, 1080), new DisplaySize(2200, 1125), 0, 1),
            BoostRefreshRate = false,
        };
        return new DisplayProfile(Guid.NewGuid(), "Desktop", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            null, [display], display.DisplayId, DisplayProfileCaptureSchema.Current, []);
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly List<(Action Action, TaskCompletionSource Completion)> _pending = [];

        public Task InvokeAsync(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add((action, completion));
            return completion.Task;
        }

        internal void DeliverInReverseOrder()
        {
            foreach (var (action, completion) in _pending.AsEnumerable().Reverse().ToArray())
            {
                action();
                completion.SetResult();
            }
            _pending.Clear();
        }
    }

    private sealed class MemoryStore : IConfigurationStore
    {
        public Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ApplicationConfiguration.CreateDefault());

        public Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeUpdateClient : IUpdateClient
    {
        public bool IsInstalled => true;
        public string? CurrentPackageVersion => "2026.808.0";
        public AvailableUpdate? PendingRestart { get; private set; }
        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AvailableUpdate?>(new AvailableUpdate("2026.1022.0", "22.10.26", "# Fixes\n- Synthetic notes"));
        public Task DownloadAsync(AvailableUpdate update, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            PendingRestart = update;
            return Task.CompletedTask;
        }
        public void ApplyAndRestart(AvailableUpdate update) { }
    }
}
