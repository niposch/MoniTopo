using System.Text.Json;
using MoniTopo.Core.Recovery;

namespace MoniTopo.Core.Tests;

public sealed class RecoveryMonitorTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SuccessSignalCleansTransientFilesWithoutRollback()
    {
        await InTransactionDirectory(async paths =>
        {
            await WritePayloadAsync(paths);
            var executor = new FakeRollbackExecutor();
            var monitor = new RecoveryMonitor(new FixedWaiter(RecoveryWaitOutcome.SuccessSignaled), executor);

            var result = await monitor.RunAsync(paths);

            Assert.Null(result);
            Assert.Equal(0, executor.CallCount);
            Assert.False(File.Exists(paths.PayloadPath));
            Assert.False(File.Exists(paths.RollbackPath("rollback.bin")));
            Assert.False(File.Exists(paths.ResultPath));
        });
    }

    [Theory]
    [InlineData(RecoveryWaitOutcome.TimedOut)]
    [InlineData(RecoveryWaitOutcome.MainProcessExited)]
    public async Task IncompleteActivationTriggersRollbackAndPersistsResult(RecoveryWaitOutcome outcome)
    {
        await InTransactionDirectory(async paths =>
        {
            await WritePayloadAsync(paths);
            var executor = new FakeRollbackExecutor { Result = true };
            var monitor = new RecoveryMonitor(new FixedWaiter(outcome), executor, new FrozenTimeProvider());

            var result = await monitor.RunAsync(paths);

            Assert.NotNull(result);
            Assert.True(result.RollbackAttempted);
            Assert.True(result.RollbackSucceeded);
            Assert.True(File.Exists(paths.ResultPath));
            Assert.False(File.Exists(paths.PayloadPath));
            Assert.False(File.Exists(paths.RollbackPath("rollback.bin")));
        });
    }

    [Fact]
    public async Task CorruptPayloadIsReportedWithoutRollback()
    {
        await InTransactionDirectory(async paths =>
        {
            await File.WriteAllTextAsync(paths.PayloadPath, "{broken");
            var executor = new FakeRollbackExecutor();
            var monitor = new RecoveryMonitor(new FixedWaiter(RecoveryWaitOutcome.TimedOut), executor);

            var result = await monitor.RunAsync(paths);

            Assert.NotNull(result);
            Assert.False(result.RollbackAttempted);
            Assert.Equal(0, executor.CallCount);
            Assert.True(File.Exists(paths.ResultPath));
        });
    }

    [Fact]
    public async Task TransactionLockPreventsTwoRecoveryProcessesFromFighting()
    {
        await InTransactionDirectory(async paths =>
        {
            await WritePayloadAsync(paths);
            var waiter = new BlockingWaiter();
            var executor = new FakeRollbackExecutor { Result = true };
            var firstMonitor = new RecoveryMonitor(waiter, executor);
            var first = firstMonitor.RunAsync(paths);
            await waiter.Entered.Task;

            var second = await new RecoveryMonitor(new FixedWaiter(RecoveryWaitOutcome.TimedOut), executor).RunAsync(paths);
            waiter.Release.SetResult(RecoveryWaitOutcome.TimedOut);
            await first;

            Assert.Null(second);
            Assert.Equal(1, executor.CallCount);
        });
    }

    private static async Task WritePayloadAsync(RecoveryTransactionPaths paths)
    {
        var payload = new RecoveryPayload(
            1,
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Environment.ProcessId,
            "synthetic-event",
            DateTimeOffset.UtcNow.AddSeconds(10),
            "rollback.bin");
        await File.WriteAllTextAsync(paths.PayloadPath, JsonSerializer.Serialize(payload, SerializerOptions));
        await File.WriteAllBytesAsync(paths.RollbackPath(payload.RollbackDataFileName), [1, 2, 3]);
    }

    private static async Task InTransactionDirectory(Func<RecoveryTransactionPaths, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MoniTopo.Recovery.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await action(new RecoveryTransactionPaths(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedWaiter(RecoveryWaitOutcome outcome) : IRecoveryWaiter
    {
        public Task<RecoveryWaitOutcome> WaitAsync(RecoveryPayload payload, CancellationToken cancellationToken) =>
            Task.FromResult(outcome);
    }

    private sealed class BlockingWaiter : IRecoveryWaiter
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<RecoveryWaitOutcome> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RecoveryWaitOutcome> WaitAsync(RecoveryPayload payload, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FakeRollbackExecutor : IRecoveryRollbackExecutor
    {
        internal int CallCount { get; private set; }

        internal bool Result { get; init; }

        public Task<bool> RollbackAsync(string rollbackDataPath, CancellationToken cancellationToken)
        {
            CallCount++;
            Assert.True(File.Exists(rollbackDataPath));
            return Task.FromResult(Result);
        }
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 20, 16, 0, 0, TimeSpan.Zero);
    }
}
