using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;
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
    bool EmergencyFallbackUsed)
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

public sealed class ActivationFailureException(string errorCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string ErrorCode { get; } = errorCode;
}

public interface IDisplayActivationBackend
{
    Task<CapturedDisplaySnapshot> QueryCurrentAsync(CancellationToken cancellationToken);

    Task<ActivationRollbackSnapshot> CaptureRollbackSnapshotAsync(CancellationToken cancellationToken);

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
    Task SignalSuccessAsync(CancellationToken cancellationToken);

    Task SignalFailureHandledAsync(bool rollbackSucceeded, CancellationToken cancellationToken);
}

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
    ActiveProfileMatcher profileMatcher) : IDisposable
{
    private readonly SemaphoreSlim _activationGate = new(1, 1);

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

            rollbackSnapshot = await backend.CaptureRollbackSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await backend.PreflightAsync(profile, resolution.Bindings, cancellationToken).ConfigureAwait(false);

            Report(progress, ActivationPhase.Validating, "Asking Windows to validate the complete display setup…");
            await backend.ValidateCoreConfigurationAsync(profile, resolution.Bindings, cancellationToken).ConfigureAwait(false);
            recoverySession = await recoveryCoordinator.StartAsync(rollbackSnapshot, cancellationToken).ConfigureAwait(false);

            Report(progress, ActivationPhase.ApplyingTopology, "Applying display topology and modes…");
            mutationBegan = true;
            await backend.ApplyCoreTemporaryAsync(profile, resolution.Bindings, cancellationToken).ConfigureAwait(false);

            Report(progress, ActivationPhase.WaitingForDisplays, "Waiting for displays to settle…");
            await backend.WaitForStableTopologyAsync(cancellationToken).ConfigureAwait(false);
            var afterTopology = await backend.QueryCurrentAsync(cancellationToken).ConfigureAwait(false);
            var rebound = identityResolver.Resolve(profile, afterTopology.ConnectedDisplays);
            if (rebound.Status != IdentityResolutionStatus.Success)
            {
                throw new ActivationFailureException(
                    "activation.identity.changed",
                    "The displays could not be identified after Windows changed the topology.");
            }

            Report(progress, ActivationPhase.ApplyingScaling, "Applying Windows display scaling…");
            await backend.ApplyScalingAsync(profile, rebound.Bindings, cancellationToken).ConfigureAwait(false);

            Report(progress, ActivationPhase.ApplyingHdr, "Applying HDR state…");
            await backend.ApplyHdrAsync(profile, rebound.Bindings, cancellationToken).ConfigureAwait(false);

            Report(progress, ActivationPhase.Verifying, "Verifying the complete display setup…");
            await VerifyAsync(profile, cancellationToken).ConfigureAwait(false);

            Report(progress, ActivationPhase.Persisting, "Saving the verified topology in Windows…");
            await backend.PersistCoreConfigurationAsync(cancellationToken).ConfigureAwait(false);
            await VerifyAsync(profile, cancellationToken).ConfigureAwait(false);
            await stateStore.SetLastActivatedProfileAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            await recoverySession.SignalSuccessAsync(cancellationToken).ConfigureAwait(false);

            Report(progress, ActivationPhase.Completed, $"{profile.Name} activated");
            return new ActivationResult(
                ActivationOutcome.Success,
                $"{profile.Name} activated",
                null,
                true,
                false,
                false,
                false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || mutationBegan)
        {
            var failure = exception as ActivationFailureException ?? new ActivationFailureException(
                "activation.unexpected",
                "MoniTopo could not activate the display profile.",
                exception);
            if (mutationBegan && rollbackSnapshot is not null)
            {
                rollbackAttempted = true;
                Report(progress, ActivationPhase.RollingBack, "Restoring the previous display setup…");
                try
                {
                    rollbackSucceeded = await backend.RollbackAsync(rollbackSnapshot, CancellationToken.None).ConfigureAwait(false);
                    if (!rollbackSucceeded)
                    {
                        fallbackUsed = await backend.EmergencyFallbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                    fallbackUsed = await TryEmergencyFallbackAsync().ConfigureAwait(false);
                }

                if (recoverySession is not null)
                {
                    await recoverySession.SignalFailureHandledAsync(rollbackSucceeded, CancellationToken.None).ConfigureAwait(false);
                }
            }

            var message = rollbackAttempted && rollbackSucceeded
                ? $"MoniTopo restored the previous display setup. {failure.Message}"
                : failure.Message;
            return new ActivationResult(
                ActivationOutcome.Failed,
                message,
                failure.ErrorCode,
                mutationBegan,
                rollbackAttempted,
                rollbackSucceeded,
                fallbackUsed);
        }
        finally
        {
            if (recoverySession is not null)
            {
                await recoverySession.DisposeAsync().ConfigureAwait(false);
            }

            _activationGate.Release();
        }
    }

    private async Task VerifyAsync(DisplayProfile profile, CancellationToken cancellationToken)
    {
        var current = await backend.QueryCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!profileMatcher.Match(profile, current).IsMatch)
        {
            throw new ActivationFailureException(
                "activation.verification.failed",
                "Windows did not apply every saved display setting.");
        }
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
