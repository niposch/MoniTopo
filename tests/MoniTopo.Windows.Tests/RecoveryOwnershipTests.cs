using System.Diagnostics;
using MoniTopo.Core.Activation;
using MoniTopo.Core.Recovery;
using MoniTopo.Recovery;
using MoniTopo.Windows.Display;

namespace MoniTopo.Windows.Tests;

public sealed class RecoveryOwnershipTests
{
    [Fact]
    public async Task WatchdogWaitsForPendingMainWriteBeforeIssuingRecoveryWrites()
    {
        await using var fixture = new ProtocolFixture();
        var mainLease = DisplayWriterLease.TryAcquire(fixture.Payload.WriterLockPath)!;
        var process = new FakeMainProcess();
        var executor = new AssertExclusiveExecutor(fixture.Payload.WriterLockPath);
        var monitor = new RecoveryMonitor(new ProcessAndEventRecoveryWaiter(processAccess: new FakeProcessAccess(process)), executor);
        var running = monitor.RunAsync(fixture.Paths);
        Assert.True(await WaitAsync(fixture.Ready));
        Assert.True(await WaitAsync(fixture.Takeover));
        Assert.Equal(0, executor.CallCount);

        await RecoveryFiles.WriteAsync(fixture.Paths.CompletionPath, new RecoveryCompletion(RecoveryTerminalOutcome.Unresolved), CancellationToken.None);
        await mainLease.DisposeAsync();
        fixture.Completed.Set();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result!.RollbackSucceeded);
        Assert.Equal(1, executor.CallCount);
        Assert.False(process.Terminated);
        Assert.True(fixture.Acknowledged.WaitOne(0));
        using var nextTransaction = DisplayWriterLease.TryAcquire(fixture.Payload.WriterLockPath);
        Assert.NotNull(nextTransaction);
    }

    [Fact]
    public async Task HungMainIsTerminatedAndItsExitConfirmedBeforeRecoveryObtainsLease()
    {
        await using var fixture = new ProtocolFixture();
        var mainLease = DisplayWriterLease.TryAcquire(fixture.Payload.WriterLockPath)!;
        var process = new FakeMainProcess { OnExit = mainLease.Dispose };
        var executor = new AssertExclusiveExecutor(fixture.Payload.WriterLockPath, () => Assert.True(process.ExitConfirmed));
        var waiter = new ProcessAndEventRecoveryWaiter(processAccess: new FakeProcessAccess(process), takeoverGrace: TimeSpan.FromMilliseconds(20));

        var result = await new RecoveryMonitor(waiter, executor).RunAsync(fixture.Paths).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(process.Terminated);
        Assert.True(process.ExitConfirmed);
        Assert.True(result!.RollbackSucceeded);
    }

    [Fact]
    public async Task ReusedPidIsNeverTerminated()
    {
        await using var fixture = new ProtocolFixture();
        var process = new FakeMainProcess { StartUtcTicks = 999 };
        var waiter = new ProcessAndEventRecoveryWaiter(processAccess: new FakeProcessAccess(process), takeoverGrace: TimeSpan.Zero);
        var result = await new RecoveryMonitor(waiter, new AssertExclusiveExecutor(fixture.Payload.WriterLockPath)).RunAsync(fixture.Paths);
        Assert.False(process.Terminated);
        Assert.True(result!.RollbackSucceeded);
    }

    [Fact]
    public async Task CompletionIsObservedBeforeExpiredDeadline()
    {
        await using var fixture = new ProtocolFixture();
        fixture.Completed.Set();
        var waiter = new ProcessAndEventRecoveryWaiter(processAccess: new FakeProcessAccess(new FakeMainProcess()));
        var result = await waiter.WaitAsync(fixture.Payload with { TimeoutMilliseconds = 1, ExpiresUtc = DateTimeOffset.MinValue }, CancellationToken.None);
        Assert.Equal(RecoveryWaitOutcome.SuccessSignaled, result);
    }

    [Fact]
    public async Task SuccessAtTakeoverBoundaryIsAcceptedBeforeAnyRollback()
    {
        await using var fixture = new ProtocolFixture();
        var mainLease = DisplayWriterLease.TryAcquire(fixture.Payload.WriterLockPath)!;
        var executor = new AssertExclusiveExecutor(fixture.Payload.WriterLockPath);
        var monitor = new RecoveryMonitor(new ProcessAndEventRecoveryWaiter(processAccess: new FakeProcessAccess(new FakeMainProcess())), executor);
        var running = monitor.RunAsync(fixture.Paths);
        Assert.True(await WaitAsync(fixture.Takeover));
        await RecoveryFiles.WriteAsync(fixture.Paths.CompletionPath,
            new RecoveryCompletion(RecoveryTerminalOutcome.ActivationSucceeded), CancellationToken.None);
        await mainLease.DisposeAsync();
        fixture.Completed.Set();

        Assert.Null(await running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task DistinctTransactionsShareWriterLeaseAndPendingRecoveryBlocksNewStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "MoniTopo.Protocol.Tests", Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var second = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            var coordinator = new ProcessRecoveryCoordinator(typeof(RecoveryOwnershipTests).Assembly.Location,
                TimeSpan.FromSeconds(5), new NeverLaunch(), TimeProvider.System);
            var secondSnapshot = new ActivationRollbackSnapshot(Guid.NewGuid(), 1, Path.Combine(second, "rollback.bin"));
            using (var firstWriter = DisplayWriterLease.TryAcquire(Path.Combine(root, "display-writer.lock")))
            {
                var busy = await Assert.ThrowsAsync<ActivationFailureException>(() => coordinator.StartAsync(secondSnapshot, CancellationToken.None));
                Assert.Equal("activation.recovery.busy", busy.ErrorCode);
            }

            await File.WriteAllTextAsync(Path.Combine(first, "payload.json"), "pending");
            var pending = await Assert.ThrowsAsync<ActivationFailureException>(() => coordinator.StartAsync(secondSnapshot, CancellationToken.None));
            Assert.Equal("activation.recovery.pending", pending.ErrorCode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task<bool> WaitAsync(EventWaitHandle handle) => Task.Run(() => handle.WaitOne(TimeSpan.FromSeconds(4)));

    [Fact]
    public async Task FailedLaunchPublishesAbortAndDoesNotPermanentlyBlockNextTransaction()
    {
        var root = Path.Combine(Path.GetTempPath(), "MoniTopo.Protocol.Tests", Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            var launcher = new NeverLaunch();
            var coordinator = new ProcessRecoveryCoordinator(typeof(RecoveryOwnershipTests).Assembly.Location,
                TimeSpan.FromSeconds(5), launcher, TimeProvider.System);
            foreach (var directory in new[] { first, second })
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(
                    new ActivationRollbackSnapshot(Guid.NewGuid(), 1, Path.Combine(directory, "rollback.bin")), CancellationToken.None));
                var completion = await RecoveryFiles.ReadAsync<RecoveryCompletion>(
                    Path.Combine(directory, "completion.json"), CancellationToken.None);
                Assert.Equal(RecoveryTerminalOutcome.AbortedBeforeMutation, completion!.Outcome);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ProtocolFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "MoniTopo.Protocol.Tests", Guid.NewGuid().ToString("N"));
        internal RecoveryTransactionPaths Paths { get; }
        internal RecoveryPayload Payload { get; }
        internal EventWaitHandle Completed { get; }
        internal EventWaitHandle Ready { get; }
        internal EventWaitHandle Takeover { get; }
        internal EventWaitHandle Acknowledged { get; }

        internal ProtocolFixture()
        {
            var id = Guid.NewGuid();
            Paths = new RecoveryTransactionPaths(Path.Combine(_root, id.ToString("N")));
            Directory.CreateDirectory(Paths.DirectoryPath);
            var prefix = "Local\\MoniTopo.SyntheticRecovery." + id.ToString("N");
            Payload = new RecoveryPayload(2, id, 1234, prefix + ".completed", prefix + ".ready",
                DateTimeOffset.UtcNow.AddMilliseconds(100), "rollback.bin", Path.Combine(_root, "display-writer.lock"),
                prefix + ".takeover", prefix + ".acknowledged", 1, 100);
            Completed = new EventWaitHandle(false, EventResetMode.ManualReset, Payload.SuccessEventName);
            Ready = new EventWaitHandle(false, EventResetMode.ManualReset, Payload.ReadyEventName);
            Takeover = new EventWaitHandle(false, EventResetMode.ManualReset, Payload.TakeoverEventName);
            Acknowledged = new EventWaitHandle(false, EventResetMode.ManualReset, Payload.AcknowledgedEventName);
            RecoveryFiles.WriteAsync(Paths.PayloadPath, Payload, CancellationToken.None).GetAwaiter().GetResult();
            File.WriteAllBytes(Paths.RollbackPath("rollback.bin"), [1, 2, 3]);
        }

        public ValueTask DisposeAsync()
        {
            Completed.Dispose(); Ready.Dispose(); Takeover.Dispose(); Acknowledged.Dispose();
            Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProcessAccess(FakeMainProcess process) : IRecoveryProcessAccess
    {
        public IRecoveryMainProcess Open(int processId) => process;
    }

    private sealed class FakeMainProcess : IRecoveryMainProcess
    {
        public long StartUtcTicks { get; init; } = 1;
        public bool HasExited { get; private set; }
        internal bool Terminated { get; private set; }
        internal bool ExitConfirmed { get; private set; }
        internal Action? OnExit { get; init; }
        public void Terminate() => Terminated = true;
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            Assert.True(Terminated);
            OnExit?.Invoke();
            HasExited = true;
            ExitConfirmed = true;
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    private sealed class AssertExclusiveExecutor(string writerPath, Action? beforeWrite = null) : IRecoveryRollbackExecutor
    {
        internal int CallCount { get; private set; }
        public Task<bool> RollbackAsync(string rollbackDataPath, CancellationToken cancellationToken)
        {
            beforeWrite?.Invoke();
            using var competing = DisplayWriterLease.TryAcquire(writerPath);
            Assert.Null(competing);
            CallCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class NeverLaunch : IRecoveryProcessLauncher
    {
        public Process Start(string executablePath, string transactionDirectory) => throw new InvalidOperationException("Must not launch without writer ownership.");
    }
}
