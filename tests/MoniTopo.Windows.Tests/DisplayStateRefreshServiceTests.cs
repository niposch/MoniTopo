using MoniTopo.Windows.Display;

namespace MoniTopo.Windows.Tests;

public sealed class DisplayStateRefreshServiceTests
{
    [Fact]
    public async Task BurstyDisplayEventsAreDebounced()
    {
        var refreshCount = 0;
        await using var service = CreateService(_ =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.CompletedTask;
        });

        var first = service.NotifyDisplayChangeAsync();
        var second = service.NotifyDisplayChangeAsync();
        await Task.WhenAll(first, second);

        Assert.Equal(1, refreshCount);
    }

    [Fact]
    public async Task ConsistencyCheckIsSuspendedWhileSessionIsLocked()
    {
        var refreshCount = 0;
        await using var service = CreateService(_ =>
        {
            refreshCount++;
            return Task.CompletedTask;
        });
        service.SetSessionLocked(true);

        await service.RunConsistencyCheckAsync();
        service.SetSessionLocked(false);
        await service.RunConsistencyCheckAsync();

        Assert.Equal(1, refreshCount);
    }

    [Fact]
    public async Task ShutdownSuppressesPendingAndFallbackRefreshes()
    {
        var refreshCount = 0;
        await using var service = CreateService(_ =>
        {
            refreshCount++;
            return Task.CompletedTask;
        });

        service.BeginShutdown();
        await service.NotifyDisplayChangeAsync();
        await service.RunConsistencyCheckAsync();

        Assert.Equal(0, refreshCount);
    }

    private static DisplayStateRefreshService CreateService(Func<CancellationToken, Task> refresh) => new(
        refresh,
        TimeProvider.System,
        debounceInterval: TimeSpan.FromMilliseconds(20),
        fallbackInterval: Timeout.InfiniteTimeSpan);
}
