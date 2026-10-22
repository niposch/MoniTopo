using System.Diagnostics;
using MoniTopo.Core.Activation;
using MoniTopo.Core.Recovery;

namespace MoniTopo.Windows.Display;

internal interface IRecoveryProcessLauncher
{
    Process? Start(string executablePath, string transactionDirectory);
}

internal sealed class RecoveryProcessLauncher : IRecoveryProcessLauncher
{
    public Process? Start(string executablePath, string transactionDirectory)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--recover-display-transaction");
        startInfo.ArgumentList.Add(transactionDirectory);
        return Process.Start(startInfo);
    }
}

public sealed class ProcessRecoveryCoordinator : IRecoveryCoordinator
{
    private readonly string _recoveryExecutablePath;
    private readonly TimeSpan _timeout;
    private readonly IRecoveryProcessLauncher _launcher;
    private readonly TimeProvider _timeProvider;

    public ProcessRecoveryCoordinator()
        : this(Path.Combine(AppContext.BaseDirectory, "MoniTopo.Recovery.exe"), TimeSpan.FromSeconds(60),
            new RecoveryProcessLauncher(), TimeProvider.System)
    { }

    internal ProcessRecoveryCoordinator(string recoveryExecutablePath, TimeSpan timeout,
        IRecoveryProcessLauncher launcher, TimeProvider timeProvider)
    {
        _recoveryExecutablePath = recoveryExecutablePath;
        _timeout = timeout;
        _launcher = launcher;
        _timeProvider = timeProvider;
    }

    public async Task<IRecoverySession> StartAsync(ActivationRollbackSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_recoveryExecutablePath))
        {
            throw new ActivationFailureException("activation.recovery.missing",
                "The display recovery companion is missing. Reinstall MoniTopo before activating profiles.");
        }

        var directory = Path.GetDirectoryName(snapshot.DataPath)
            ?? throw new ActivationFailureException("activation.recovery.invalid", "The display recovery path is invalid.");
        var root = Directory.GetParent(directory)?.FullName
            ?? throw new ActivationFailureException("activation.recovery.invalid", "The display recovery root is invalid.");
        var writerPath = Path.Combine(root, "display-writer.lock");
        var writerLease = DisplayWriterLease.TryAcquire(writerPath)
            ?? throw new ActivationFailureException("activation.recovery.busy", "Another display activation or recovery is still running.");
        if (Directory.EnumerateDirectories(root).Any(candidate =>
            !string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(candidate, "payload.json")) && !HasSafeCompletion(candidate) &&
            !File.Exists(Path.Combine(candidate, "result.json")) &&
            !File.Exists(Path.Combine(candidate, "result.acknowledged.json"))))
        {
            writerLease.Dispose();
            throw new ActivationFailureException("activation.recovery.pending", "A previous display transaction is awaiting recovery. Restart MoniTopo before activating another profile.");
        }
        var paths = new RecoveryTransactionPaths(directory);
        var prefix = $"Local\\MoniTopo.Recovery.{snapshot.TransactionId:N}";
        var completionEvent = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Completed");
        var takeoverEvent = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Takeover");
        var acknowledgedEvent = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Acknowledged");
        using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Ready");
        Process? process = null;
        try
        {
            using var mainProcess = Process.GetCurrentProcess();
            var payload = new RecoveryPayload(2, snapshot.TransactionId, Environment.ProcessId,
                prefix + ".Completed", prefix + ".Ready", _timeProvider.GetUtcNow() + _timeout,
                Path.GetFileName(snapshot.DataPath), writerPath, prefix + ".Takeover", prefix + ".Acknowledged",
                mainProcess.StartTime.ToUniversalTime().Ticks, checked((int)_timeout.TotalMilliseconds));
            await RecoveryFiles.WriteAsync(paths.PayloadPath, payload, cancellationToken).ConfigureAwait(false);
            process = _launcher.Start(_recoveryExecutablePath, directory)
                ?? throw new ActivationFailureException("activation.recovery.start-failed", "Windows could not start the display recovery companion.");
            var ready = await Task.Run(() => readyEvent.WaitOne(TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            if (!ready)
            {
                throw new ActivationFailureException("activation.recovery.not-ready",
                    "The display recovery companion did not become ready. No display changes were applied.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessRecoverySession(paths, completionEvent, takeoverEvent, acknowledgedEvent, writerLease, process);
        }
        catch (Exception exception)
        {
            // The writer lease is still held, so a late companion cannot begin rollback while
            // startup is being aborted. Publish the no-mutation terminal state before releasing it.
            await RecoveryFiles.WriteAsync(paths.CompletionPath,
                new RecoveryCompletion(RecoveryTerminalOutcome.AbortedBeforeMutation), CancellationToken.None).ConfigureAwait(false);
            completionEvent.Set();
            exception.Data["MoniTopo.PreserveRecovery"] = true;
            writerLease.Dispose();
            completionEvent.Dispose();
            takeoverEvent.Dispose();
            acknowledgedEvent.Dispose();
            process?.Dispose();
            throw;
        }
    }

    private static bool HasSafeCompletion(string directory)
    {
        try
        {
            var completion = RecoveryFiles.ReadAsync<RecoveryCompletion>(Path.Combine(directory, "completion.json"), CancellationToken.None).GetAwaiter().GetResult();
            return completion?.Outcome is RecoveryTerminalOutcome.ActivationSucceeded or RecoveryTerminalOutcome.RollbackRestored or
                RecoveryTerminalOutcome.EmergencyFallback or RecoveryTerminalOutcome.AbortedBeforeMutation;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException) { return false; }
    }

    private sealed class ProcessRecoverySession(
        RecoveryTransactionPaths paths, EventWaitHandle completionEvent, EventWaitHandle takeoverEvent,
        EventWaitHandle acknowledgedEvent, FileStream writerLease, Process process) : IRecoverySession
    {
        private bool _released;
        private bool _completed;
        private RecoveryTerminalOutcome? _publishedOutcome;
        private bool _acknowledgementFailed;

        public bool IsTerminalPublished => _publishedOutcome is not null;

        public Task EnsureOwnershipAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_released || takeoverEvent.WaitOne(TimeSpan.Zero))
            {
                throw new RecoveryOwnershipLostException();
            }

            return Task.CompletedTask;
        }

        public async Task SignalSuccessAsync(CancellationToken cancellationToken) =>
            _ = await CompleteAsync(RecoveryTerminalOutcome.ActivationSucceeded, cancellationToken).ConfigureAwait(false);

        public async Task SignalFailureHandledAsync(bool rollbackSucceeded, CancellationToken cancellationToken) =>
            _ = await CompleteAsync(rollbackSucceeded ? RecoveryTerminalOutcome.RollbackRestored : RecoveryTerminalOutcome.Unresolved,
                cancellationToken).ConfigureAwait(false);

        public async Task<RecoveryResult?> CompleteAsync(RecoveryTerminalOutcome outcome, CancellationToken cancellationToken)
        {
            if (_completed || _acknowledgementFailed)
            {
                return await ReadResultAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_publishedOutcome is null && outcome == RecoveryTerminalOutcome.ActivationSucceeded)
            {
                await EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_publishedOutcome is null)
            {
                await RecoveryFiles.WriteAsync(paths.CompletionPath, new RecoveryCompletion(outcome), cancellationToken).ConfigureAwait(false);
                _publishedOutcome = outcome;
            }
            // Release only after all main writes have returned and the terminal record is durable.
            ReleaseWriter();
            completionEvent.Set();
            var acknowledged = await Task.Run(() => acknowledgedEvent.WaitOne(TimeSpan.FromSeconds(45)), CancellationToken.None).ConfigureAwait(false);
            if (!acknowledged)
            {
                _acknowledgementFailed = true;
                throw new ActivationFailureException("activation.recovery.unacknowledged",
                    "Display recovery did not confirm the result. Open Windows Display Settings; recovery data has been retained.");
            }

            // An acknowledgement is emitted only after the companion stopped writing. Waiting
            // for its exit also prevents admission of a subsequent transaction before cleanup.
            if (process.Id != Environment.ProcessId)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            _completed = true;
            return await ReadResultAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            ReleaseWriter();
            completionEvent.Dispose();
            takeoverEvent.Dispose();
            acknowledgedEvent.Dispose();
            process.Dispose();
            return ValueTask.CompletedTask;
        }

        private void ReleaseWriter()
        {
            if (!_released)
            {
                writerLease.Dispose();
                _released = true;
            }
        }

        private async Task<RecoveryResult?> ReadResultAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await RecoveryFiles.ReadAsync<RecoveryResult>(paths.ResultPath, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
    }
}
