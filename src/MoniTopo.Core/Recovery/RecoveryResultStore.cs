using System.Text.Json;

namespace MoniTopo.Core.Recovery;

public sealed record PendingRecoveryResult(string DirectoryPath, RecoveryResult Result);

public sealed class RecoveryResultStore(string rootDirectory)
{
    public async Task<IReadOnlyList<PendingRecoveryResult>> ReadPendingAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<PendingRecoveryResult>();
        if (!Directory.Exists(rootDirectory))
        {
            return results;
        }

        foreach (var directory in Directory.EnumerateDirectories(rootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var transactionLease = DisplayWriterLease.TryAcquire(new RecoveryTransactionPaths(directory).LockPath);
                if (transactionLease is null)
                {
                    continue;
                }

                var result = await RecoveryFiles.ReadAsync<RecoveryResult>(new RecoveryTransactionPaths(directory).ResultPath, cancellationToken).ConfigureAwait(false);
                if (result is { SchemaVersion: 1 or 2 } && !string.IsNullOrWhiteSpace(result.Message))
                {
                    results.Add(new PendingRecoveryResult(directory, result));
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // One damaged result must not hide other completed recovery outcomes.
            }
        }

        return results.OrderBy(item => item.Result.CompletedUtc).ToArray();
    }

    public Task AcknowledgeAsync(PendingRecoveryResult pending, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var directory = Path.GetFullPath(pending.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The recovery result is outside the recovery directory.", nameof(pending));
        }

        var paths = new RecoveryTransactionPaths(pending.DirectoryPath);
        if (File.Exists(paths.ResultPath))
        {
            // Acknowledgement suppresses repeated startup messages but retains failed rollback bytes.
            File.Move(paths.ResultPath, Path.Combine(pending.DirectoryPath, "result.acknowledged.json"), overwrite: true);
        }

        return Task.CompletedTask;
    }
}
