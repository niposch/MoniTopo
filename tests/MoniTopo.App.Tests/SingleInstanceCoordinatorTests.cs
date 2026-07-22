using MoniTopo.App.Lifecycle;

namespace MoniTopo.App.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task SecondaryInstanceSignalsPrimaryThroughUserOnlyPipe()
    {
        var applicationId = $"MoniTopo.Tests.{Guid.NewGuid():N}";
        await using var primary = SingleInstanceCoordinator.CreateForCurrentUser(applicationId);
        Assert.True(primary.IsPrimary);
        using var opened = new SemaphoreSlim(0);
        primary.OpenRequested += (_, _) => opened.Release();
        primary.StartListening();
        for (var index = 0; index < 6; index++)
        {
            await using var secondary = SingleInstanceCoordinator.CreateForCurrentUser(applicationId);

            var signaled = await secondary.SignalPrimaryAsync();

            Assert.False(secondary.IsPrimary);
            Assert.True(signaled);
            Assert.True(await opened.WaitAsync(TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public async Task PipeProtocolWaitsForACompleteFragmentedCommand()
    {
        await using var stream = new FragmentedReadStream("op", "en", "\n");

        var command = await SingleInstancePipeProtocol.ReadCommandAsync(stream, CancellationToken.None);

        Assert.Equal("open", command);
    }

    private sealed class FragmentedReadStream(params string[] fragments) : MemoryStream(
        fragments.SelectMany(fragment => System.Text.Encoding.UTF8.GetBytes(fragment)).ToArray())
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
