using MoniTopo.Core.Updates;

namespace MoniTopo.Core.Tests;

public sealed class UpdateCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AutomaticCheckRunsAtMostOncePerDayAfterSuccess()
    {
        var client = new FakeClient();
        var settings = new FakeSettings();
        using var coordinator = new UpdateCoordinator(client, settings, new FrozenTimeProvider(Now));

        var first = await coordinator.CheckAutomaticallyAsync();
        var second = await coordinator.CheckAutomaticallyAsync();

        Assert.Equal(UpdateStatus.UpToDate, first.Status);
        Assert.Equal(first, second);
        Assert.Equal(1, client.CheckCount);
        Assert.Equal(Now, settings.LastSuccessfulUpdateCheckUtc);
    }

    [Fact]
    public async Task ManualCheckBypassesDailyLimit()
    {
        var client = new FakeClient();
        var settings = new FakeSettings { LastSuccessfulUpdateCheckUtc = Now };
        using var coordinator = new UpdateCoordinator(client, settings, new FrozenTimeProvider(Now));

        await coordinator.CheckNowAsync();

        Assert.Equal(1, client.CheckCount);
    }

    [Fact]
    public async Task AvailableUpdateDownloadsOnlyAfterExplicitAction()
    {
        var update = new AvailableUpdate("2026.720.1", "20.07.26.1", "Notes");
        var client = new FakeClient { Update = update };
        using var coordinator = new UpdateCoordinator(client, new FakeSettings(), new FrozenTimeProvider(Now));

        var available = await coordinator.CheckNowAsync();

        Assert.Equal(UpdateStatus.Available, available.Status);
        Assert.Equal(0, client.DownloadCount);

        var downloaded = await coordinator.DownloadAsync();

        Assert.Equal(UpdateStatus.ReadyToInstall, downloaded.Status);
        Assert.Equal(1, client.DownloadCount);
        Assert.Equal(100, downloaded.ProgressPercent);

        coordinator.InstallAndRestart();
        Assert.Equal(1, client.ApplyCount);
    }

    [Fact]
    public async Task DevelopmentRunReportsNotInstalledWithoutCallingSource()
    {
        var client = new FakeClient { IsInstalled = false };
        using var coordinator = new UpdateCoordinator(client, new FakeSettings(), new FrozenTimeProvider(Now));

        var state = await coordinator.CheckNowAsync();

        Assert.Equal(UpdateStatus.NotInstalled, state.Status);
        Assert.Equal(0, client.CheckCount);
    }

    [Fact]
    public async Task FailedCheckDoesNotAdvanceSuccessfulCheckTime()
    {
        var client = new FakeClient
        {
            Failure = new UpdateClientException(UpdateClientError.Network, "offline"),
        };
        var settings = new FakeSettings();
        using var coordinator = new UpdateCoordinator(client, settings, new FrozenTimeProvider(Now));

        var state = await coordinator.CheckAutomaticallyAsync();

        Assert.Equal(UpdateStatus.Error, state.Status);
        Assert.Null(settings.LastSuccessfulUpdateCheckUtc);
        Assert.Contains("network", state.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StagedUpdateSurvivesChecksAndRemainsInstallable()
    {
        var staged = new AvailableUpdate("2026.720.1", "20.07.26.1", null);
        var client = new FakeClient { PendingRestart = staged, Failure = new(UpdateClientError.Network, "offline") };
        using var coordinator = new UpdateCoordinator(client, new FakeSettings(), new FrozenTimeProvider(Now));
        Assert.Equal(UpdateStatus.ReadyToInstall, (await coordinator.CheckNowAsync()).Status);
        Assert.Equal(UpdateStatus.ReadyToInstall, (await coordinator.CheckAutomaticallyAsync()).Status);
        Assert.Equal(0, client.CheckCount);
        coordinator.InstallAndRestart();
        Assert.Equal(1, client.ApplyCount);
    }

    [Fact]
    public async Task SuccessfulDownloadRemainsStagedEvenIfClientTemporarilyOmitsPendingAsset()
    {
        var client = new FakeClient { Update = new("2026.720.1", "20.07.26.1", null) };
        using var coordinator = new UpdateCoordinator(client, new FakeSettings());
        await coordinator.CheckNowAsync();
        await coordinator.DownloadAsync();
        await coordinator.CheckNowAsync();
        Assert.Equal(UpdateStatus.ReadyToInstall, coordinator.Current.Status);
        Assert.Equal(1, client.CheckCount);
        Assert.Equal(1, client.DownloadCount);
    }

    [Fact]
    public async Task FailedDownloadKeepsAvailableUpdateForExplicitRetry()
    {
        var client = new FakeClient { Update = new("2026.720.1", "20.07.26.1", null) };
        using var coordinator = new UpdateCoordinator(client, new FakeSettings());
        await coordinator.CheckNowAsync();
        client.DownloadFailure = new UpdateClientException(UpdateClientError.Checksum, "mismatch");
        Assert.Equal(UpdateStatus.Available, (await coordinator.DownloadAsync()).Status);
        Assert.Contains("integrity", coordinator.Current.Message);
        client.DownloadFailure = null;
        Assert.Equal(UpdateStatus.ReadyToInstall, (await coordinator.DownloadAsync()).Status);
        Assert.Equal(2, client.DownloadCount);
    }

    [Fact]
    public async Task CheckCancellationRetainsPreviouslyAvailableUpdate()
    {
        var client = new FakeClient { Update = new("2026.720.1", "20.07.26.1", null) };
        using var coordinator = new UpdateCoordinator(client, new FakeSettings());
        await coordinator.CheckNowAsync();
        client.BlockCheck = true;
        using var cancellation = new CancellationTokenSource();
        var pending = coordinator.CheckNowAsync(cancellation.Token);
        await client.CheckStarted.Task;
        cancellation.Cancel();
        var result = await pending;
        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal(client.Update, result.Update);
    }

    [Fact]
    public async Task RestartFailureKeepsStagedUpdateForRetry()
    {
        var client = new FakeClient
        {
            PendingRestart = new("2026.720.1", "20.07.26.1", null),
            ApplyFailure = new UpdateClientException(UpdateClientError.Busy, "locked")
        };
        using var coordinator = new UpdateCoordinator(client, new FakeSettings());
        coordinator.InstallAndRestart();
        Assert.Equal(UpdateStatus.ReadyToInstall, coordinator.Current.Status);
        Assert.Contains("try again", coordinator.Current.Message);
        client.ApplyFailure = null;
        coordinator.InstallAndRestart();
        Assert.Equal(2, client.ApplyCount);
        await coordinator.CheckNowAsync();
    }

    [Fact]
    public async Task NotificationFailureCannotUndoSuccessfulUpdateDownload()
    {
        var client = new FakeClient { Update = new("2026.720.1", "20.07.26.1", null) };
        using var coordinator = new UpdateCoordinator(client, new FakeSettings());
        var failures = 0;
        coordinator.Changed += (_, _) => throw new InvalidOperationException("observer failed");
        coordinator.NotificationFailed += (_, _) => failures++;
        await coordinator.CheckNowAsync();
        await coordinator.DownloadAsync();
        Assert.Equal(UpdateStatus.ReadyToInstall, coordinator.Current.Status);
        Assert.True(failures > 0);
    }

    [Fact]
    public async Task CancellationRestoresAvailableUpdateAndDisposalDoesNotBreakGateRelease()
    {
        var client = new FakeClient { Update = new("2026.720.1", "20.07.26.1", null), BlockDownload = true };
        var coordinator = new UpdateCoordinator(client, new FakeSettings());
        await coordinator.CheckNowAsync();
        var operation = coordinator.DownloadAsync();
        await client.DownloadStarted.Task;
        coordinator.Dispose();
        Assert.Equal(UpdateStatus.Available, (await operation).Status);
    }

    [Fact]
    public async Task LifetimeSchedulerChecksAgainAfterDailyIntervalAndHonorsSettings()
    {
        var clock = new ManualTimeProvider(Now);
        var client = new FakeClient();
        var settings = new FakeSettings();
        using var coordinator = new UpdateCoordinator(client, settings, clock);
        using var cancellation = new CancellationTokenSource();
        var scheduler = coordinator.RunAutomaticChecksAsync(cancellation.Token);
        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => client.CheckCount == 1 && clock.TimerCount > 0);
        clock.Advance(TimeSpan.FromHours(23));
        await WaitUntilAsync(() => clock.TimerCount > 0);
        Assert.Equal(1, client.CheckCount);
        settings.AutomaticUpdateChecksEnabled = false;
        clock.Advance(TimeSpan.FromHours(1));
        await WaitUntilAsync(() => clock.TimerCount > 0);
        Assert.Equal(1, client.CheckCount);
        settings.AutomaticUpdateChecksEnabled = true;
        clock.Advance(TimeSpan.FromMinutes(15));
        await WaitUntilAsync(() => client.CheckCount == 2);
        cancellation.Cancel();
        await scheduler;
        Assert.Equal(0, client.DownloadCount);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(1, timeout.Token); }
    }

    private sealed class FakeClient : IUpdateClient
    {
        public bool IsInstalled { get; set; } = true;

        public string? CurrentPackageVersion => "2026.720.0";

        public AvailableUpdate? PendingRestart { get; set; }

        public AvailableUpdate? Update { get; set; }

        public UpdateClientException? Failure { get; set; }

        public int CheckCount { get; private set; }

        public int DownloadCount { get; private set; }

        public int ApplyCount { get; private set; }

        public Exception? DownloadFailure { get; set; }
        public bool BlockDownload { get; set; }
        public bool BlockCheck { get; set; }
        public Exception? ApplyFailure { get; set; }
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CheckStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCount++;
            CheckStarted.TrySetResult();
            if (BlockCheck) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Failure is { } failure) throw failure;
            return Update;
        }

        public async Task DownloadAsync(
            AvailableUpdate update,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            DownloadStarted.TrySetResult();
            if (BlockDownload) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (DownloadFailure is { } failure) throw failure;
            progress?.Report(52);
            progress?.Report(100);
        }

        public void ApplyAndRestart(AvailableUpdate update)
        {
            ApplyCount++;
            if (ApplyFailure is { } failure) throw failure;
        }
    }

    private sealed class FakeSettings : IUpdateSettingsStore
    {
        public bool AutomaticUpdateChecksEnabled { get; set; } = true;

        public DateTimeOffset? LastSuccessfulUpdateCheckUtc { get; set; }

        public Task SetAutomaticUpdateChecksEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            AutomaticUpdateChecksEnabled = enabled;
            return Task.CompletedTask;
        }

        public Task SetLastSuccessfulUpdateCheckUtcAsync(DateTimeOffset value, CancellationToken cancellationToken = default)
        {
            LastSuccessfulUpdateCheckUtc = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FrozenTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        public int TimerCount { get { lock (_timers) return _timers.Count; } }
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, now + dueTime);
            lock (_timers) _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            ManualTimer[] due;
            lock (_timers)
            {
                now += duration;
                due = _timers.Where(timer => timer.Due <= now).ToArray();
                foreach (var timer in due) _timers.Remove(timer);
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            internal DateTimeOffset Due { get; private set; } = due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = owner.GetUtcNow() + dueTime; return true; }
            internal void Fire() => callback(state);
            public void Dispose() { lock (owner._timers) owner._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
