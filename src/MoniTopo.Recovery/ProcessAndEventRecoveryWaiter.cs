using System.Diagnostics;
using MoniTopo.Core.Recovery;

namespace MoniTopo.Recovery;

internal interface IRecoveryMainProcess : IDisposable
{
    long StartUtcTicks { get; }
    bool HasExited { get; }
    void Terminate();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal interface IRecoveryProcessAccess
{
    IRecoveryMainProcess? Open(int processId);
}

internal sealed class RecoveryProcessAccess : IRecoveryProcessAccess
{
    public IRecoveryMainProcess? Open(int processId)
    {
        try
        {
            return new MainProcess(Process.GetProcessById(processId));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed class MainProcess(Process process) : IRecoveryMainProcess
    {
        public long StartUtcTicks => process.StartTime.ToUniversalTime().Ticks;
        public bool HasExited => process.HasExited;
        public void Terminate() => process.Kill();
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Dispose() => process.Dispose();
    }
}

internal sealed class ProcessAndEventRecoveryWaiter : IRecoveryWaiter
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private readonly TimeProvider _timeProvider;
    private readonly IRecoveryProcessAccess _processAccess;
    private readonly TimeSpan _takeoverGrace;

    internal ProcessAndEventRecoveryWaiter(TimeProvider? timeProvider = null,
        IRecoveryProcessAccess? processAccess = null, TimeSpan? takeoverGrace = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _processAccess = processAccess ?? new RecoveryProcessAccess();
        _takeoverGrace = takeoverGrace ?? TimeSpan.FromSeconds(5);
    }

    public async Task<RecoveryWaitOutcome> WaitAsync(RecoveryPayload payload, CancellationToken cancellationToken)
    {
        Validate(payload);
        using var completion = OpenEvent(payload.SuccessEventName);
        using var ready = OpenEvent(payload.ReadyEventName);
        using var main = _processAccess.Open(payload.MainProcessId);
        ready?.Set();
        var started = _timeProvider.GetTimestamp();
        var timeout = TimeSpan.FromMilliseconds(payload.TimeoutMilliseconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Observe completion before expiry, including after the final polling delay.
            if (completion?.WaitOne(TimeSpan.Zero) == true)
            {
                return RecoveryWaitOutcome.SuccessSignaled;
            }

            if (main is null || main.HasExited || main.StartUtcTicks != payload.MainProcessStartUtcTicks)
            {
                return RecoveryWaitOutcome.MainProcessExited;
            }

            if (_timeProvider.GetElapsedTime(started) >= timeout)
            {
                return RecoveryWaitOutcome.TimedOut;
            }

            await Task.Delay(PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IAsyncDisposable?> AcquireWriterAsync(RecoveryPayload payload, CancellationToken cancellationToken)
    {
        Validate(payload);
        using var takeover = OpenEvent(payload.TakeoverEventName);
        takeover?.Set();
        using var main = _processAccess.Open(payload.MainProcessId);
        var started = _timeProvider.GetTimestamp();
        while (_timeProvider.GetElapsedTime(started) < _takeoverGrace)
        {
            if (DisplayWriterLease.TryAcquire(payload.WriterLockPath) is { } lease)
            {
                return lease;
            }

            await Task.Delay(PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        // A blocked native call cannot be safely interrupted by cancellation. End the exact
        // original process, confirm exit, then obtain the same lease it held. Never race its call.
        if (main is { HasExited: false } && main.StartUtcTicks == payload.MainProcessStartUtcTicks)
        {
            main.Terminate();
            using var exitDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exitDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            await main.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
            if (!main.HasExited)
            {
                throw new IOException("The original display writer did not exit; recovery cannot safely write.");
            }
        }

        started = _timeProvider.GetTimestamp();
        while (_timeProvider.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
        {
            if (DisplayWriterLease.TryAcquire(payload.WriterLockPath) is { } lease)
            {
                return lease;
            }

            await Task.Delay(PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        throw new IOException("The display writer lease is still held; recovery did not issue competing writes.");
    }

    public void Acknowledge(RecoveryPayload payload)
    {
        using var acknowledged = OpenEvent(payload.AcknowledgedEventName);
        acknowledged?.Set();
    }

    private static EventWaitHandle? OpenEvent(string name)
    {
        try { return EventWaitHandle.OpenExisting(name); }
        catch (WaitHandleCannotBeOpenedException) { return null; }
    }

    private static void Validate(RecoveryPayload payload)
    {
        if (payload.SchemaVersion != 2 || string.IsNullOrWhiteSpace(payload.WriterLockPath) ||
            string.IsNullOrWhiteSpace(payload.TakeoverEventName) || string.IsNullOrWhiteSpace(payload.AcknowledgedEventName) ||
            payload.MainProcessStartUtcTicks <= 0 || payload.TimeoutMilliseconds is <= 0 or > 300000)
        {
            throw new InvalidDataException("The display recovery ownership protocol is invalid or obsolete.");
        }
    }
}
