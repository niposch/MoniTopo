using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoniTopo.Core.Recovery;

public enum RecoveryWaitOutcome
{
    SuccessSignaled,
    TimedOut,
    MainProcessExited,
}

public sealed record RecoveryPayload(
    int SchemaVersion,
    Guid TransactionId,
    int MainProcessId,
    string SuccessEventName,
    string ReadyEventName,
    DateTimeOffset ExpiresUtc,
    string RollbackDataFileName);

public sealed record RecoveryResult(
    int SchemaVersion,
    Guid? TransactionId,
    bool RollbackAttempted,
    bool RollbackSucceeded,
    string Message,
    DateTimeOffset CompletedUtc);

public sealed record RecoveryTransactionPaths(string DirectoryPath)
{
    public string PayloadPath => Path.Combine(DirectoryPath, "payload.json");

    public string ResultPath => Path.Combine(DirectoryPath, "result.json");

    public string LockPath => Path.Combine(DirectoryPath, "transaction.lock");

    public string RollbackPath(string fileName) => Path.Combine(DirectoryPath, Path.GetFileName(fileName));
}

public interface IRecoveryWaiter
{
    Task<RecoveryWaitOutcome> WaitAsync(RecoveryPayload payload, CancellationToken cancellationToken);
}

public interface IRecoveryRollbackExecutor
{
    Task<bool> RollbackAsync(string rollbackDataPath, CancellationToken cancellationToken);
}

public sealed class RecoveryMonitor(
    IRecoveryWaiter waiter,
    IRecoveryRollbackExecutor rollbackExecutor,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<RecoveryResult?> RunAsync(RecoveryTransactionPaths paths, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.DirectoryPath);
        await using var transactionLock = TryAcquireLock(paths.LockPath);
        if (transactionLock is null)
        {
            return null;
        }

        RecoveryPayload payload;
        try
        {
            await using var payloadStream = File.OpenRead(paths.PayloadPath);
            payload = await JsonSerializer.DeserializeAsync<RecoveryPayload>(payloadStream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new JsonException("The recovery payload is empty.");
            if (payload.SchemaVersion != 1 || payload.TransactionId == Guid.Empty)
            {
                throw new JsonException("The recovery payload version or transaction ID is invalid.");
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            var corruptResult = new RecoveryResult(
                1,
                null,
                false,
                false,
                "The recovery payload was invalid and no display rollback was attempted.",
                _timeProvider.GetUtcNow());
            await WriteResultAsync(paths.ResultPath, corruptResult, cancellationToken).ConfigureAwait(false);
            return corruptResult;
        }

        var outcome = await waiter.WaitAsync(payload, cancellationToken).ConfigureAwait(false);
        if (outcome == RecoveryWaitOutcome.SuccessSignaled)
        {
            DeleteIfExists(paths.PayloadPath);
            DeleteIfExists(paths.RollbackPath(payload.RollbackDataFileName));
            return null;
        }

        var rollbackSucceeded = false;
        try
        {
            rollbackSucceeded = await rollbackExecutor.RollbackAsync(
                paths.RollbackPath(payload.RollbackDataFileName),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            rollbackSucceeded = false;
        }

        var result = new RecoveryResult(
            1,
            payload.TransactionId,
            true,
            rollbackSucceeded,
            rollbackSucceeded
                ? "MoniTopo restored the previous display setup after activation did not complete."
                : "MoniTopo could not restore the previous display setup. Open Windows Display Settings.",
            _timeProvider.GetUtcNow());
        await WriteResultAsync(paths.ResultPath, result, cancellationToken).ConfigureAwait(false);
        DeleteIfExists(paths.PayloadPath);
        DeleteIfExists(paths.RollbackPath(payload.RollbackDataFileName));
        return result;
    }

    private static FileStream? TryAcquireLock(string path)
    {
        try
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task WriteResultAsync(string path, RecoveryResult result, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, result, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
