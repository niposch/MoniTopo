using MoniTopo.Core.Recovery;
using MoniTopo.Recovery;
using MoniTopo.Windows.Display;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] arguments)
{
    if (arguments is not ["--recover-display-transaction", var transactionDirectory])
    {
        return 2;
    }

    if (!DisplayMutationAuthorization.TryCreateForExplicitManualCommand(
            explicitManualCommand: true,
            out var authorization))
    {
        return 3;
    }

    try
    {
        var monitor = new RecoveryMonitor(
            new ProcessAndEventRecoveryWaiter(),
            new DisplayRollbackExecutor(authorization));
        var result = await monitor.RunAsync(new RecoveryTransactionPaths(transactionDirectory)).ConfigureAwait(false);
        return result is null or { RollbackSucceeded: true } ? 0 : 4;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return 5;
    }
}
