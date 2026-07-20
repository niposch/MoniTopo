using MoniTopo.App.Lifecycle;

namespace MoniTopo.App.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task SecondaryInstanceSignalsPrimaryThroughUserOnlyPipe()
    {
        await using var primary = SingleInstanceCoordinator.CreateForCurrentUser();
        Assert.True(primary.IsPrimary);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.OpenRequested += (_, _) => opened.TrySetResult();
        primary.StartListening();
        await using var secondary = SingleInstanceCoordinator.CreateForCurrentUser();

        var signaled = await secondary.SignalPrimaryAsync();
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(secondary.IsPrimary);
        Assert.True(signaled);
    }
}
