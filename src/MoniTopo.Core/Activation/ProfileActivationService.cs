using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;
using MoniTopo.Core.Recovery;
using MoniTopo.Core.Validation;

namespace MoniTopo.Core.Activation;

public enum ActivationPhase
{
    Preflight,
    Validating,
    ApplyingTopology,
    WaitingForDisplays,
    ApplyingScaling,
    ApplyingHdr,
    Verifying,
    Persisting,
    RollingBack,
    Completed,
}

public enum ActivationOutcome
{
    Success,
    Failed,
    Busy,
}

public sealed record ActivationProgress(ActivationPhase Phase, string Message);

public sealed record ActivationResult(
    ActivationOutcome Outcome,
    string Message,
    string? ErrorCode,
    bool MutationBegan,
    bool RollbackAttempted,
    bool RollbackSucceeded,
    bool EmergencyFallbackUsed,
    string? TechnicalDetails = null)
{
    public static ActivationResult Busy { get; } = new(
        ActivationOutcome.Busy,
        "Another display profile is already being activated.",
        "activation.busy",
        false,
        false,
        false,
        false);
}

public sealed record ActivationRollbackSnapshot(Guid TransactionId, int SchemaVersion, string DataPath);

public class ActivationFailureException : Exception
{
    public ActivationFailureException(
        string errorCode,
        string message,
        Exception? innerException = null,
        string? technicalDetails = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        TechnicalDetails = technicalDetails;
    }

    public string ErrorCode { get; }

    public string? TechnicalDetails { get; }
}

public interface IDisplayActivationBackend
{
    void SetMutationGuard(Action? ensureWriterOwnership) { }

    Task<CapturedDisplaySnapshot> QueryCurrentAsync(CancellationToken cancellationToken);

    Task<ActivationRollbackSnapshot> CaptureRollbackSnapshotAsync(CancellationToken cancellationToken);

    Task DiscardRollbackSnapshotAsync(ActivationRollbackSnapshot snapshot, CancellationToken cancellationToken);

    Task PreflightAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken);

    Task ValidateCoreConfigurationAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken);

    Task ApplyCoreTemporaryAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken);

    Task WaitForStableTopologyAsync(CancellationToken cancellationToken);

    Task ApplyScalingAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken);

    Task ApplyHdrAsync(
        DisplayProfile profile,
        IReadOnlyList<ResolvedIdentityBinding> bindings,
        CancellationToken cancellationToken);

    Task PersistCoreConfigurationAsync(CancellationToken cancellationToken);

    Task<bool> RollbackAsync(ActivationRollbackSnapshot snapshot, CancellationToken cancellationToken);

    Task<bool> EmergencyFallbackAsync(CancellationToken cancellationToken);
}

public interface IRecoverySession : IAsyncDisposable
{
    bool IsTerminalPublished => false;
    Task EnsureOwnershipAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task SignalSuccessAsync(CancellationToken cancellationToken);

    Task SignalFailureHandledAsync(bool rollbackSucceeded, CancellationToken cancellationToken);

    async Task<RecoveryResult?> CompleteAsync(RecoveryTerminalOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome == RecoveryTerminalOutcome.ActivationSucceeded)
        {
            await SignalSuccessAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SignalFailureHandledAsync(outcome == RecoveryTerminalOutcome.RollbackRestored, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}

public sealed class RecoveryOwnershipLostException() : ActivationFailureException(
    "activation.recovery.takeover", "The display recovery companion took over this activation.");

public sealed class TransientDisplayReadException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public interface IRecoveryCoordinator
{
    Task<IRecoverySession> StartAsync(ActivationRollbackSnapshot snapshot, CancellationToken cancellationToken);
}

public interface IActivationStateStore
{
    Task SetLastActivatedProfileAsync(Guid profileId, CancellationToken cancellationToken);
}

public sealed class ProfileActivationService(
    IDisplayActivationBackend backend,
    IRecoveryCoordinator recoveryCoordinator,
    IActivationStateStore stateStore,
    MonitorIdentityResolver identityResolver,
    ActiveProfileMatcher profileMatcher,
    TimeProvider? timeProvider = null,
    TimeSpan? verificationTimeout = null) : IDisposable
{
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _verificationTimeout = verificationTimeout ?? TimeSpan.FromSeconds(8);

    public async Task<ActivationResult> ActivateAsync(
        DisplayProfile profile,
        IProgress<ActivationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _activationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ActivationResult.Busy;
        }

        var mutationBegan = false;
        var rollbackAttempted = false;
        var rollbackSucceeded = false;
        var fallbackUsed = false;
        ActivationRollbackSnapshot? rollbackSnapshot = null;
        IRecoverySession? recoverySession = null;
        try
        {
            Report(progress, ActivationPhase.Preflight, "Checking connected displays and capabilities…");
            var validationErrors = ProfileValidator.Validate(profile);
            if (validationErrors.Count > 0)
            {
                throw new ActivationFailureException("activation.profile.invalid", validationErrors[0].Message);
            }

            var current = await backend.QueryCurrentAsync(cancellationToken).ConfigureAwait(false);
            var resolution = identityResolver.Resolve(profile, current.ConnectedDisplays);
            if (resolution.Status != IdentityResolutionStatus.Success)
            {
                throw new ActivationFailureException(
                    $"activation.identity.{resolution.Status.ToString().ToLowerInvariant()}",
                    resolution.Message ?? "The required displays could not be resolved.");
            }

            await backend.PreflightAsync(profile, resolution.Bindings, cancellationToken).ConfigureAwait(false);
            var plan = DisplayActivationPlan.Create(profile, current, profileMatcher);
            if (!plan.HasChanges)
            {
                // Confirm a sustained match, not just one sample. No rollback payload,
                // companion, or native setter is needed for an already-active profile.
                Report(progress, ActivationPhase.Verifying, "Checking the current display setup…");
                try
                {
                    await VerifyAsync(profile, cancellationToken).ConfigureAwait(false);
                }
                catch (ActivationFailureException exception) when (exception.ErrorCode == "activation.verification.failed")
                {
                    throw new ActivationFailureException("activation.state.changed",
                        "The display setup changed while MoniTopo was checking it. Try activating the profile again.", exception);
                }
                await stateStore.SetLastActivatedProfileAsync(profile.Id, cancellationToken).ConfigureAwait(false);
                Report(progress, ActivationPhase.Completed, $"{profile.Name} is already active");
                return Success(profile, mutationBegan: false);
            }

            rollbackSnapshot = await backend.CaptureRollbackSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (plan.CoreConfigurationRequired)
            {
                Report(progress, ActivationPhase.Validating, "Asking Windows to validate the display layout and modes…");
                await backend.ValidateCoreConfigurationAsync(profile, resolution.Bindings, cancellationToken).ConfigureAwait(false);
            }
            recoverySession = await recoveryCoordinator.StartAsync(rollbackSnapshot, cancellationToken).ConfigureAwait(false);
            backend.SetMutationGuard(() => recoverySession.EnsureOwnershipAsync(CancellationToken.None).GetAwaiter().GetResult());
            await recoverySession.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);

            var coreNeedsPersistence = false;
            // Re-plan under writer ownership, including core changes on every pass:
            // HDR can restore a previous Windows mode as well as resetting DPI.
            // Revalidate fresh routing before each necessary core repair. Conflicting
            // driver side effects remain bounded and require exact final verification.
            for (var pass = 0; pass < 3; pass++)
            {
                plan = await ReadPlanAsync(profile, cancellationToken).ConfigureAwait(false);
                if (!plan.HasChanges && !coreNeedsPersistence) break;
                if (plan.CoreConfigurationRequired)
                {
                    await recoverySession.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
                    await backend.ValidateCoreConfigurationAsync(profile, plan.IdentityResolution.Bindings, cancellationToken).ConfigureAwait(false);
                    await recoverySession.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
                    Report(progress, ActivationPhase.ApplyingTopology, "Applying display layout and mode changes…");
                    mutationBegan = true;
                    await backend.ApplyCoreTemporaryAsync(profile, plan.IdentityResolution.Bindings, cancellationToken).ConfigureAwait(false);
                    coreNeedsPersistence = true;
                    await recoverySession.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
                    Report(progress, ActivationPhase.WaitingForDisplays, "Waiting for displays to settle…");
                    await backend.WaitForStableTopologyAsync(cancellationToken).ConfigureAwait(false);
                    await recoverySession.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
                }

                plan = await ReadPlanAsync(profile, cancellationToken).ConfigureAwait(false);
                if (plan.ScalingRequired)
                {
                    await recoverySession.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
                    Report(progress, ActivationPhase.ApplyingScaling, "Applying Windows display scale changes…");
                    mutationBegan = true;
                    await backend.ApplyScalingAsync(profile, plan.IdentityResolution.Bindings, cancellationToken).ConfigureAwait(false);
                    await recoverySession.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
                    await WaitForPropertyAsync(profile, scaling: true, cancellationToken).ConfigureAwait(false);
                }

                plan = await ReadPlanAsync(profile, cancellationToken).ConfigureAwait(false);
                if (plan.HdrRequired)
                {
                    await recoverySession.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
                    Report(progress, ActivationPhase.ApplyingHdr, "Applying HDR changes…");
                    mutationBegan = true;
                    await backend.ApplyHdrAsync(profile, plan.IdentityResolution.Bindings, cancellationToken).ConfigureAwait(false);
                    await recoverySession.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
                    await WaitForPropertyAsync(profile, scaling: false, cancellationToken).ConfigureAwait(false);
                }

                plan = await ReadPlanAsync(profile, cancellationToken).ConfigureAwait(false);
                if (!plan.HasChanges)
                {
                    if (!coreNeedsPersistence) break;
                    Report(progress, ActivationPhase.Verifying, "Verifying the complete display setup…");
                    await VerifyAsync(profile, cancellationToken).ConfigureAwait(false);
                    await recoverySession.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
                    Report(progress, ActivationPhase.Persisting, "Saving the verified layout and modes in Windows…");
                    await backend.PersistCoreConfigurationAsync(cancellationToken).ConfigureAwait(false);
                    coreNeedsPersistence = false;
                    await recoverySession.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
                    // SDC_APPLY | SDC_SAVE_TO_DATABASE can itself reset DPI/HDR.
                    // Reconcile those side effects under the same writer lease.
                }
            }

            Report(progress, ActivationPhase.Verifying, "Verifying the complete display setup…");
            await VerifyAsync(profile, cancellationToken).ConfigureAwait(false);
            await recoverySession.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);

            if (coreNeedsPersistence)
                throw new ActivationFailureException("activation.verification.failed", "Windows did not apply every saved display setting.");
            await stateStore.SetLastActivatedProfileAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            await recoverySession.CompleteAsync(RecoveryTerminalOutcome.ActivationSucceeded, cancellationToken).ConfigureAwait(false);

            var success = Success(profile, mutationBegan);
            Report(progress, ActivationPhase.Completed, success.Message);
            return success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || mutationBegan)
        {
            var failure = exception as ActivationFailureException ?? new ActivationFailureException(
                "activation.unexpected",
                "MoniTopo could not activate the display profile.",
                exception,
                exception.Data["MoniTopo.TechnicalDetails"] as string ?? exception.ToString());
            if (mutationBegan && rollbackSnapshot is not null && recoverySession?.IsTerminalPublished != true)
            {
                rollbackAttempted = true;
                Report(progress, ActivationPhase.RollingBack, "Restoring the previous display setup…");
                try
                {
                    if (recoverySession is not null)
                    {
                        await recoverySession.EnsureOwnershipAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    rollbackSucceeded = await backend.RollbackAsync(rollbackSnapshot, CancellationToken.None).ConfigureAwait(false);
                    if (recoverySession is not null)
                    {
                        await recoverySession.EnsureOwnershipAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    if (!rollbackSucceeded)
                    {
                        fallbackUsed = await backend.EmergencyFallbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (RecoveryOwnershipLostException)
                {
                    // The watchdog is waiting for the main writer lease. Do not issue any more
                    // writes; CompleteAsync releases it and waits for the recovery outcome.
                }
                catch
                {
                    try
                    {
                        if (recoverySession is not null)
                        {
                            await recoverySession.EnsureOwnershipAsync(CancellationToken.None).ConfigureAwait(false);
                        }

                        fallbackUsed = await TryEmergencyFallbackAsync().ConfigureAwait(false);
                    }
                    catch (RecoveryOwnershipLostException) { }
                }

                if (recoverySession is not null)
                {
                    try
                    {
                        var recovered = await recoverySession.CompleteAsync(
                            rollbackSucceeded ? RecoveryTerminalOutcome.RollbackRestored :
                            fallbackUsed ? RecoveryTerminalOutcome.EmergencyFallback : RecoveryTerminalOutcome.Unresolved,
                            CancellationToken.None).ConfigureAwait(false);
                        if (recovered is not null)
                        {
                            rollbackSucceeded = recovered.RollbackSucceeded;
                            fallbackUsed = recovered.EmergencyFallbackUsed;
                        }
                    }
                    catch
                    {
                        // The independent recovery timeout remains the final safety net.
                    }
                }
            }
            else if (!mutationBegan && rollbackSnapshot is not null)
            {
                if (recoverySession is not null)
                {
                    await recoverySession.CompleteAsync(RecoveryTerminalOutcome.AbortedBeforeMutation, CancellationToken.None).ConfigureAwait(false);
                }
                else if (exception.Data["MoniTopo.PreserveRecovery"] is not true)
                {
                    await backend.DiscardRollbackSnapshotAsync(rollbackSnapshot, CancellationToken.None).ConfigureAwait(false);
                }
            }

            var message = rollbackAttempted && rollbackSucceeded
                ? $"MoniTopo restored the previous display setup. {failure.Message}"
                : fallbackUsed ? $"Windows enabled a fallback display setup. {failure.Message}" :
                    rollbackAttempted ? $"{failure.Message} Recovery could not verify a usable setup. Open Windows Display Settings." : failure.Message;
            return new ActivationResult(
                ActivationOutcome.Failed,
                message,
                failure.ErrorCode,
                mutationBegan,
                rollbackAttempted,
                rollbackSucceeded,
                fallbackUsed,
                failure.TechnicalDetails ?? failure.InnerException?.ToString());
        }
        finally
        {
            if (!mutationBegan && recoverySession is not null && !recoverySession.IsTerminalPublished)
            {
                try
                {
                    await recoverySession.CompleteAsync(RecoveryTerminalOutcome.AbortedBeforeMutation, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the payload if companion acknowledgement was unavailable.
                }
            }

            backend.SetMutationGuard(null);
            if (recoverySession is not null)
            {
                try
                {
                    await recoverySession.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Activation has already reached a terminal result.
                }
            }

            _activationGate.Release();
        }
    }

    private async Task<DisplayActivationPlan> ReadPlanAsync(DisplayProfile profile, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        while (_timeProvider.GetElapsedTime(started) < _verificationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var current = await backend.QueryCurrentAsync(cancellationToken).ConfigureAwait(false);
                var plan = DisplayActivationPlan.Create(profile, current, profileMatcher);
                if (plan.IdentityResolution.Status != IdentityResolutionStatus.Success)
                {
                    throw new ActivationFailureException("activation.identity.changed",
                        "The required displays could not be identified while applying the profile.");
                }
                return plan;
            }
            catch (TransientDisplayReadException) { }
            await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        throw new ActivationFailureException("activation.read.unstable", "Windows did not provide a stable display state before the operation timed out.");
    }

    private static ActivationResult Success(DisplayProfile profile, bool mutationBegan) => new(
        ActivationOutcome.Success, mutationBegan ? $"{profile.Name} activated" : $"{profile.Name} is already active",
        null, mutationBegan, false, false, false);

    private async Task WaitForPropertyAsync(DisplayProfile profile, bool scaling, CancellationToken cancellationToken)
    {
        // Setters can return before the driver exposes the new value. Poll rather
        // than issuing the same write again while the first transition is pending.
        var started = _timeProvider.GetTimestamp();
        while (_timeProvider.GetElapsedTime(started) < _verificationTimeout)
        {
            var plan = await ReadPlanAsync(profile, cancellationToken).ConfigureAwait(false);
            if (!(scaling ? plan.ScalingRequired : plan.HdrRequired)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        throw new ActivationFailureException("activation.verification.failed", "Windows did not apply every saved display setting.");
    }

    private async Task VerifyAsync(DisplayProfile profile, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        var matched = false;
        while (_timeProvider.GetElapsedTime(started) < _verificationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var current = await backend.QueryCurrentAsync(cancellationToken).ConfigureAwait(false);
                if (profileMatcher.Match(profile, current).IsMatch)
                {
                    if (matched)
                    {
                        return;
                    }

                    matched = true;
                }
                else
                {
                    matched = false;
                }
            }
            catch (TransientDisplayReadException)
            {
                matched = false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        throw new ActivationFailureException("activation.verification.failed", "Windows did not apply every saved display setting.");
    }

    private async Task<bool> TryEmergencyFallbackAsync()
    {
        try
        {
            return await backend.EmergencyFallbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static void Report(IProgress<ActivationProgress>? progress, ActivationPhase phase, string message) =>
        progress?.Report(new ActivationProgress(phase, message));

    public void Dispose()
    {
        _activationGate.Dispose();
    }
}
