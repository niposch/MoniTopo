using System.Diagnostics;
using MoniTopo.Core.Recovery;

namespace MoniTopo.Recovery;

internal sealed class ProcessAndEventRecoveryWaiter(TimeProvider? timeProvider = null) : IRecoveryWaiter
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<RecoveryWaitOutcome> WaitAsync(RecoveryPayload payload, CancellationToken cancellationToken)
    {
        using var successEvent = EventWaitHandle.OpenExisting(payload.SuccessEventName);
        Process? mainProcess;
        try
        {
            mainProcess = Process.GetProcessById(payload.MainProcessId);
        }
        catch (ArgumentException)
        {
            return RecoveryWaitOutcome.MainProcessExited;
        }

        using (mainProcess)
        {
            while (_timeProvider.GetUtcNow() < payload.ExpiresUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (successEvent.WaitOne(TimeSpan.Zero))
                {
                    return RecoveryWaitOutcome.SuccessSignaled;
                }

                if (mainProcess.HasExited)
                {
                    return RecoveryWaitOutcome.MainProcessExited;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        return RecoveryWaitOutcome.TimedOut;
    }
}
