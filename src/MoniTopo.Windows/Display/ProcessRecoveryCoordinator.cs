using System.Diagnostics;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly string _recoveryExecutablePath;
    private readonly TimeSpan _timeout;
    private readonly IRecoveryProcessLauncher _launcher;
    private readonly TimeProvider _timeProvider;

    public ProcessRecoveryCoordinator()
        : this(
            Path.Combine(AppContext.BaseDirectory, "MoniTopo.Recovery.exe"),
            TimeSpan.FromSeconds(20),
            new RecoveryProcessLauncher(),
            TimeProvider.System)
    {
    }

    internal ProcessRecoveryCoordinator(
        string recoveryExecutablePath,
        TimeSpan timeout,
        IRecoveryProcessLauncher launcher,
        TimeProvider timeProvider)
    {
        _recoveryExecutablePath = recoveryExecutablePath;
        _timeout = timeout;
        _launcher = launcher;
        _timeProvider = timeProvider;
    }

    public async Task<IRecoverySession> StartAsync(
        ActivationRollbackSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_recoveryExecutablePath))
        {
            throw new ActivationFailureException(
                "activation.recovery.missing",
                "The display recovery companion is missing. Reinstall MoniTopo before activating profiles.");
        }

        var directory = Path.GetDirectoryName(snapshot.DataPath)
            ?? throw new ActivationFailureException("activation.recovery.invalid", "The display recovery path is invalid.");
        var paths = new RecoveryTransactionPaths(directory);
        var eventName = $"Local\\MoniTopo.Recovery.{snapshot.TransactionId:N}";
        var readyEventName = $"Local\\MoniTopo.Recovery.Ready.{snapshot.TransactionId:N}";
        var successEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName);
        try
        {
            var payload = new RecoveryPayload(
                snapshot.SchemaVersion,
                snapshot.TransactionId,
                Environment.ProcessId,
                eventName,
                readyEventName,
                _timeProvider.GetUtcNow() + _timeout,
                Path.GetFileName(snapshot.DataPath));
            await WritePayloadAsync(paths.PayloadPath, payload, cancellationToken).ConfigureAwait(false);
            var process = _launcher.Start(_recoveryExecutablePath, directory);
            if (process is null)
            {
                throw new ActivationFailureException(
                    "activation.recovery.start-failed",
                    "Windows could not start the display recovery companion.");
            }

            var ready = await Task.Run(
                () => readyEvent.WaitOne(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (!ready)
            {
                successEvent.Set();
                process.Dispose();
                throw new ActivationFailureException(
                    "activation.recovery.not-ready",
                    "The display recovery companion did not become ready. No display changes were applied.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                successEvent.Set();
                process.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new ProcessRecoverySession(successEvent, process);
        }
        catch
        {
            successEvent.Dispose();
            throw;
        }
    }

    private static async Task WritePayloadAsync(
        string path,
        RecoveryPayload payload,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, payload, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed class ProcessRecoverySession(EventWaitHandle successEvent, Process process) : IRecoverySession
    {
        private bool _signaled;

        public Task SignalSuccessAsync(CancellationToken cancellationToken) => SignalAsync(cancellationToken);

        public Task SignalFailureHandledAsync(bool rollbackSucceeded, CancellationToken cancellationToken) =>
            SignalAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            successEvent.Dispose();
            process.Dispose();
            return ValueTask.CompletedTask;
        }

        private Task SignalAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_signaled)
            {
                successEvent.Set();
                _signaled = true;
            }

            return Task.CompletedTask;
        }
    }
}
