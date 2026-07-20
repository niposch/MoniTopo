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

        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCount++;
            return Failure is null ? Task.FromResult(Update) : Task.FromException<AvailableUpdate?>(Failure);
        }

        public Task DownloadAsync(
            AvailableUpdate update,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            progress?.Report(52);
            progress?.Report(100);
            return Task.CompletedTask;
        }

        public void ApplyAndRestart(AvailableUpdate update) => ApplyCount++;
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
}
