using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoniTopo.Core.Recovery;

public enum RecoveryWaitOutcome { SuccessSignaled, TimedOut, MainProcessExited }

public enum RecoveryTerminalOutcome
{
    ActivationSucceeded,
    RollbackRestored,
    EmergencyFallback,
    Unresolved,
    AbortedBeforeMutation,
}

public sealed record RecoveryPayload(
    int SchemaVersion, Guid TransactionId, int MainProcessId, string SuccessEventName,
    string ReadyEventName, DateTimeOffset ExpiresUtc, string RollbackDataFileName,
    string WriterLockPath = "", string TakeoverEventName = "", string AcknowledgedEventName = "",
    long MainProcessStartUtcTicks = 0, int TimeoutMilliseconds = 20000);

public sealed record RecoveryCompletion(RecoveryTerminalOutcome Outcome);

public sealed record RecoveryResult(
    int SchemaVersion, Guid? TransactionId, bool RollbackAttempted, bool RollbackSucceeded,
    string Message, DateTimeOffset CompletedUtc, bool EmergencyFallbackUsed = false,
    RecoveryTerminalOutcome Outcome = RecoveryTerminalOutcome.Unresolved);

public sealed record RecoveryRollbackResult(bool Restored, bool EmergencyFallbackUsed = false);

public sealed record RecoveryTransactionPaths(string DirectoryPath)
{
    public string PayloadPath => Path.Combine(DirectoryPath, "payload.json");
    public string ResultPath => Path.Combine(DirectoryPath, "result.json");
    public string CompletionPath => Path.Combine(DirectoryPath, "completion.json");
    public string LockPath => Path.Combine(DirectoryPath, "transaction.lock");
    public string RollbackPath(string fileName) => Path.Combine(DirectoryPath, Path.GetFileName(fileName));
}

public interface IRecoveryWaiter
{
    Task<RecoveryWaitOutcome> WaitAsync(RecoveryPayload payload, CancellationToken cancellationToken);

    // Implementations must exclude every main-process writer before returning this lease,
    // including a process blocked inside a native call. A timeout alone never grants ownership.
    Task<IAsyncDisposable?> AcquireWriterAsync(RecoveryPayload payload, CancellationToken cancellationToken);

    void Acknowledge(RecoveryPayload payload) { }
}

public interface IRecoveryRollbackExecutor
{
    Task<bool> RollbackAsync(string rollbackDataPath, CancellationToken cancellationToken);

    async Task<RecoveryRollbackResult> RecoverAsync(string rollbackDataPath, CancellationToken cancellationToken) =>
        new(await RollbackAsync(rollbackDataPath, cancellationToken).ConfigureAwait(false));
}

/// <summary>An exclusive process-scoped lease, deliberately not a thread-affine mutex.</summary>
public static class DisplayWriterLease
{
    public static FileStream? TryAcquire(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
        }
        catch (IOException)
        {
            return null;
        }
    }
}

public static class RecoveryFiles
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    internal static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

public sealed class RecoveryMonitor(
    IRecoveryWaiter waiter, IRecoveryRollbackExecutor rollbackExecutor, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<RecoveryResult?> RunAsync(RecoveryTransactionPaths paths, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.DirectoryPath);
        await using var transactionLock = DisplayWriterLease.TryAcquire(paths.LockPath);
        if (transactionLock is null)
        {
            return null;
        }

        RecoveryPayload payload;
        try
        {
            payload = await RecoveryFiles.ReadAsync<RecoveryPayload>(paths.PayloadPath, cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("The recovery payload is empty.");
            if (payload.SchemaVersion != 2 || payload.TransactionId == Guid.Empty ||
                string.IsNullOrWhiteSpace(payload.RollbackDataFileName) ||
                Path.GetFileName(payload.RollbackDataFileName) != payload.RollbackDataFileName ||
                !string.Equals(Path.GetFullPath(payload.WriterLockPath),
                    Path.Combine(Directory.GetParent(paths.DirectoryPath)!.FullName, "display-writer.lock"), StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException("The recovery payload version or transaction ID is invalid.");
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            var corruptResult = new RecoveryResult(2, null, false, false,
                "The recovery payload was invalid and no display rollback was attempted. Open Windows Display Settings.",
                _timeProvider.GetUtcNow());
            await RecoveryFiles.WriteAsync(paths.ResultPath, corruptResult, cancellationToken).ConfigureAwait(false);
            return corruptResult;
        }

        _ = await waiter.WaitAsync(payload, cancellationToken).ConfigureAwait(false);
        // A completion arriving at the deadline wins while no recovery writes have begun.
        var completion = await ReadCompletionAsync(paths, cancellationToken).ConfigureAwait(false);
        if (completion is not null && completion.Outcome != RecoveryTerminalOutcome.Unresolved)
        {
            var completed = await HandleCompletionAsync(paths, payload, completion, cancellationToken).ConfigureAwait(false);
            waiter.Acknowledge(payload);
            return completed;
        }

        IAsyncDisposable? writer;
        try
        {
            writer = await waiter.AcquireWriterAsync(payload, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Recovery did not acquire exclusive display ownership.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var blocked = CreateResult(payload, RecoveryTerminalOutcome.Unresolved) with { RollbackAttempted = false };
            await RecoveryFiles.WriteAsync(paths.ResultPath, blocked, cancellationToken).ConfigureAwait(false);
            waiter.Acknowledge(payload);
            return blocked;
        }

        await using var writerScope = writer;
        completion = await ReadCompletionAsync(paths, cancellationToken).ConfigureAwait(false);
        if (completion is not null && completion.Outcome != RecoveryTerminalOutcome.Unresolved)
        {
            var completed = await HandleCompletionAsync(paths, payload, completion, cancellationToken).ConfigureAwait(false);
            await writer.DisposeAsync().ConfigureAwait(false);
            waiter.Acknowledge(payload);
            return completed;
        }

        RecoveryRollbackResult recovered;
        try
        {
            recovered = await rollbackExecutor.RecoverAsync(paths.RollbackPath(payload.RollbackDataFileName), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            recovered = new(false);
        }

        var terminal = recovered.Restored ? RecoveryTerminalOutcome.RollbackRestored :
            recovered.EmergencyFallbackUsed ? RecoveryTerminalOutcome.EmergencyFallback : RecoveryTerminalOutcome.Unresolved;
        var result = CreateResult(payload, terminal);
        await RecoveryFiles.WriteAsync(paths.ResultPath, result, cancellationToken).ConfigureAwait(false);
        if (recovered.Restored || recovered.EmergencyFallbackUsed)
        {
            CleanupSuccessful(paths, payload);
        }

        await writer.DisposeAsync().ConfigureAwait(false);
        waiter.Acknowledge(payload);
        return result;
    }

    private async Task<RecoveryResult?> HandleCompletionAsync(RecoveryTransactionPaths paths, RecoveryPayload payload,
        RecoveryCompletion completion, CancellationToken cancellationToken)
    {
        RecoveryResult? result = null;
        if (completion.Outcome is RecoveryTerminalOutcome.RollbackRestored or RecoveryTerminalOutcome.EmergencyFallback)
        {
            result = CreateResult(payload, completion.Outcome);
            await RecoveryFiles.WriteAsync(paths.ResultPath, result, cancellationToken).ConfigureAwait(false);
        }

        CleanupSuccessful(paths, payload);
        return result;
    }

    private RecoveryResult CreateResult(RecoveryPayload payload, RecoveryTerminalOutcome terminal) =>
        new(2, payload.TransactionId, true, terminal == RecoveryTerminalOutcome.RollbackRestored,
            terminal switch
            {
                RecoveryTerminalOutcome.RollbackRestored => "MoniTopo restored the previous display setup after activation did not complete.",
                RecoveryTerminalOutcome.EmergencyFallback => "MoniTopo could not restore the previous setup. Windows enabled a fallback display setup; check Windows Display Settings.",
                _ => "MoniTopo could not restore a verified display setup. Open Windows Display Settings. Recovery data has been retained.",
            }, _timeProvider.GetUtcNow(), terminal == RecoveryTerminalOutcome.EmergencyFallback, terminal);

    private static async Task<RecoveryCompletion?> ReadCompletionAsync(RecoveryTransactionPaths paths, CancellationToken cancellationToken)
    {
        try
        {
            var completion = await RecoveryFiles.ReadAsync<RecoveryCompletion>(paths.CompletionPath, cancellationToken).ConfigureAwait(false);
            return completion is not null && Enum.IsDefined(completion.Outcome) ? completion : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private static void CleanupSuccessful(RecoveryTransactionPaths paths, RecoveryPayload payload)
    {
        RecoveryFiles.DeleteIfExists(paths.PayloadPath);
        RecoveryFiles.DeleteIfExists(paths.RollbackPath(payload.RollbackDataFileName));
        RecoveryFiles.DeleteIfExists(paths.CompletionPath);
    }
}
