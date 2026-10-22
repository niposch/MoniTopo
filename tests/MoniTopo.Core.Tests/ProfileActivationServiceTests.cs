using MoniTopo.Core.Activation;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;
using MoniTopo.Core.Normalization;
using MoniTopo.Core.Recovery;

namespace MoniTopo.Core.Tests;

public sealed class ProfileActivationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedModeUsesNormalVerificationAndRollsBackIfWindowsChoosesAnotherRate(bool wrongRate)
    {
        var requested = TestData.Profile() with
        {
            CaptureSchemaVersion = 5,
            Displays = [TestData.Display() with { TargetSignal = null, ModeRequest = new DisplayModeRequest(1),
                RefreshRate = new RefreshRate(120, 1), BoostRefreshRate = false }],
        };
        var observed = Snapshot(requested, display => display with
        {
            ModeRequest = null,
            RefreshRate = new RefreshRate(wrongRate ? 60 : 120, 1),
            TargetSignal = new DisplayTargetSignal(241500000, new RefreshRate(90000, 1), new RefreshRate(120, 1),
                display.SourceResolution, new DisplaySize(2720, 1480), 0, 1)
        });
        var backend = new FakeActivationBackend(observed);
        var state = new FakeActivationStateStore();
        using var service = CreateService(backend, state: state);
        var result = await service.ActivateAsync(requested);
        Assert.Equal(wrongRate ? ActivationOutcome.Failed : ActivationOutcome.Success, result.Outcome);
        Assert.Equal(wrongRate, result.RollbackSucceeded);
        if (wrongRate)
        {
            Assert.Contains("rollback", backend.Calls);
            Assert.DoesNotContain("persist", backend.Calls);
            Assert.Null(state.LastActivatedProfileId);
        }
        else Assert.Equal(requested.Id, state.LastActivatedProfileId);
    }

    [Fact]
    public async Task SuccessfulActivationRunsValidatedTwoPhaseSequence()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var backend = new FakeActivationBackend(Snapshot(profile));
        var recovery = new FakeRecoveryCoordinator();
        var state = new FakeActivationStateStore();
        var service = CreateService(backend, recovery, state);

        var result = await service.ActivateAsync(profile);

        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Equal(
            ["query", "snapshot", "preflight", "validate", "apply-core", "settle", "query", "scale", "hdr", "query", "query", "persist", "query", "query"],
            backend.Calls);
        Assert.Equal(profile.Id, state.LastActivatedProfileId);
        Assert.True(recovery.Session.SuccessSignaled);
        Assert.DoesNotContain("rollback", backend.Calls);
    }

    [Fact]
    public async Task MissingRequiredDisplayFailsBeforeSnapshotOrMutation()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile(name: "Movie"));
        var backend = new FakeActivationBackend(new CapturedDisplaySnapshot([], [], "", DateTimeOffset.UtcNow));
        var service = CreateService(backend);

        var result = await service.ActivateAsync(profile);

        Assert.Equal(ActivationOutcome.Failed, result.Outcome);
        Assert.Equal("activation.identity.missing", result.ErrorCode);
        Assert.Equal(["query"], backend.Calls);
        Assert.False(result.MutationBegan);
    }

    [Fact]
    public async Task ValidationFailureDiscardsUnusedRollbackSnapshot()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var backend = new FakeActivationBackend(Snapshot(profile)) { FailingCall = "validate" };
        var service = CreateService(backend);

        var result = await service.ActivateAsync(profile);

        Assert.Equal(ActivationOutcome.Failed, result.Outcome);
        Assert.False(result.MutationBegan);
        Assert.Contains("discard-snapshot", backend.Calls);
        Assert.DoesNotContain("apply-core", backend.Calls);
    }

    [Fact]
    public async Task ValidationFailurePreservesTechnicalDetailsForDiagnostics()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var backend = new FakeActivationBackend(Snapshot(profile))
        {
            FailingCall = "validate",
            FailingTechnicalDetails = "Synthetic native error 87.",
        };
        var service = CreateService(backend);

        var result = await service.ActivateAsync(profile);

        Assert.Equal("Synthetic native error 87.", result.TechnicalDetails);
    }

    [Theory]
    [InlineData("scale")]
    [InlineData("hdr")]
    [InlineData("persist")]
    public async Task FailureAfterMutationRollsBack(string failingCall)
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var backend = new FakeActivationBackend(Snapshot(profile)) { FailingCall = failingCall };
        var recovery = new FakeRecoveryCoordinator();
        var service = CreateService(backend, recovery);

        var result = await service.ActivateAsync(profile);

        Assert.Equal(ActivationOutcome.Failed, result.Outcome);
        Assert.True(result.RollbackAttempted);
        Assert.True(result.RollbackSucceeded);
        Assert.Contains("restored", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(recovery.Session.FailureHandled);
    }

    [Fact]
    public async Task VerificationFailureRollsBack()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var mismatched = Snapshot(profile, path => path with { WindowsUiScalePercent = 175 });
        var backend = new FakeActivationBackend(mismatched);
        backend.QueryResults.Enqueue(Snapshot(profile));
        backend.QueryResults.Enqueue(Snapshot(profile));
        var service = CreateService(backend);

        var result = await service.ActivateAsync(profile);

        Assert.Equal("activation.verification.failed", result.ErrorCode);
        Assert.True(result.RollbackSucceeded);
    }

    [Fact]
    public async Task RollbackFailureUsesEmergencyFallback()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var backend = new FakeActivationBackend(Snapshot(profile))
        {
            FailingCall = "hdr",
            RollbackResult = false,
            FallbackResult = true,
        };
        var service = CreateService(backend);

        var result = await service.ActivateAsync(profile);

        Assert.False(result.RollbackSucceeded);
        Assert.True(result.EmergencyFallbackUsed);
        Assert.Contains("fallback", backend.Calls);
    }

    [Fact]
    public async Task ConcurrentActivationIsRejectedWithoutSecondMutation()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var backend = new FakeActivationBackend(Snapshot(profile)) { PauseDuringApply = true };
        var service = CreateService(backend);

        var first = service.ActivateAsync(profile);
        await backend.ApplyStarted.Task;
        var second = await service.ActivateAsync(profile);
        backend.ContinueApply.SetResult();
        var firstResult = await first;

        Assert.Equal(ActivationOutcome.Busy, second.Outcome);
        Assert.Equal(ActivationOutcome.Success, firstResult.Outcome);
        Assert.Equal(1, backend.Calls.Count(call => call == "apply-core"));
    }

    [Fact]
    public async Task RecoveryTakeoverDuringPendingApplyPreventsEverySubsequentMainWrite()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var backend = new FakeActivationBackend(Snapshot(profile)) { PauseDuringApply = true };
        var recovery = new FakeRecoveryCoordinator();
        recovery.Session.CompanionResult = new RecoveryResult(2, Guid.NewGuid(), true, true, "Restored.", DateTimeOffset.UtcNow,
            Outcome: RecoveryTerminalOutcome.RollbackRestored);
        var service = CreateService(backend, recovery);
        var activation = service.ActivateAsync(profile);
        await backend.ApplyStarted.Task;
        recovery.Session.OwnershipLost = true;
        backend.ContinueApply.SetResult();

        var result = await activation;

        Assert.Equal(ActivationOutcome.Failed, result.Outcome);
        Assert.True(result.RollbackSucceeded);
        Assert.DoesNotContain("scale", backend.Calls);
        Assert.DoesNotContain("hdr", backend.Calls);
        Assert.DoesNotContain("rollback", backend.Calls);
        Assert.DoesNotContain("fallback", backend.Calls);
        Assert.DoesNotContain("persist", backend.Calls);
        Assert.Equal(RecoveryTerminalOutcome.Unresolved, recovery.Session.TerminalOutcome);
    }

    [Fact]
    public async Task OwnershipLostBeforeMutationPublishesAbortWithoutDeletingLiveRecoveryPayload()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var backend = new FakeActivationBackend(Snapshot(profile));
        var recovery = new FakeRecoveryCoordinator();
        recovery.Session.OwnershipLost = true;
        var result = await CreateService(backend, recovery).ActivateAsync(profile);
        Assert.False(result.MutationBegan);
        Assert.Equal(RecoveryTerminalOutcome.AbortedBeforeMutation, recovery.Session.TerminalOutcome);
        Assert.DoesNotContain("discard-snapshot", backend.Calls);
        Assert.DoesNotContain("apply-core", backend.Calls);
    }

    [Fact]
    public async Task FailedMainRollbackAndFallbackRemainUnresolvedForIndependentRecovery()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var backend = new FakeActivationBackend(Snapshot(profile)) { FailingCall = "hdr", RollbackResult = false };
        var recovery = new FakeRecoveryCoordinator();
        var result = await CreateService(backend, recovery).ActivateAsync(profile);
        Assert.False(result.RollbackSucceeded);
        Assert.False(result.EmergencyFallbackUsed);
        Assert.Equal(RecoveryTerminalOutcome.Unresolved, recovery.Session.TerminalOutcome);
        Assert.Contains("Open Windows Display Settings", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PropertyVerificationRetriesTransientReadsUntilTwoMatchingObservations()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var backend = new FakeActivationBackend(Snapshot(profile));
        backend.QueryExceptionAtCounts[3] = new TransientDisplayReadException("Synthetic incomplete mode.");
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Equal(7, backend.Calls.Count(call => call == "query"));
    }

    private static ProfileActivationService CreateService(
        FakeActivationBackend backend,
        FakeRecoveryCoordinator? recovery = null,
        FakeActivationStateStore? state = null) => new(
            backend,
            recovery ?? new FakeRecoveryCoordinator(),
            state ?? new FakeActivationStateStore(),
            new MonitorIdentityResolver(),
            new ActiveProfileMatcher(new MonitorIdentityResolver()),
            verificationTimeout: TimeSpan.FromMilliseconds(650));

    [Fact]
    public async Task PublishedSuccessCannotBeOverwrittenOrRolledBackWhenAcknowledgementFails()
    {
        var profile = ProfileNormalizer.Normalize(TestData.Profile());
        var backend = new FakeActivationBackend(Snapshot(profile));
        var recovery = new FakeRecoveryCoordinator();
        recovery.Session.FailAfterPublication = true;

        var result = await CreateService(backend, recovery).ActivateAsync(profile);

        Assert.Equal(ActivationOutcome.Failed, result.Outcome);
        Assert.Equal(RecoveryTerminalOutcome.ActivationSucceeded, recovery.Session.TerminalOutcome);
        Assert.DoesNotContain("rollback", backend.Calls);
        Assert.DoesNotContain("fallback", backend.Calls);
    }

    private static CapturedDisplaySnapshot Snapshot(
        DisplayProfile profile,
        Func<DesiredDisplayPath, DesiredDisplayPath>? mutate = null)
    {
        var path = mutate?.Invoke(profile.Displays[0]) ?? profile.Displays[0];
        var connected = new ConnectedDisplayState("runtime-1", profile.Displays[0].Identity, true, path.FriendlyLabel, path);
        return new CapturedDisplaySnapshot([path], [connected], profile.PrimaryDisplayId, DateTimeOffset.UtcNow);
    }

    private sealed class FakeActivationBackend(CapturedDisplaySnapshot defaultSnapshot) : IDisplayActivationBackend
    {
        internal List<string> Calls { get; } = [];

        internal Queue<CapturedDisplaySnapshot> QueryResults { get; } = new();
        internal Dictionary<int, Exception> QueryExceptionAtCounts { get; } = [];
        private int _queries;

        internal string? FailingCall { get; init; }

        internal string? FailingTechnicalDetails { get; init; }

        internal bool RollbackResult { get; init; } = true;

        internal bool FallbackResult { get; init; }

        internal bool PauseDuringApply { get; init; }

        internal TaskCompletionSource ApplyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ContinueApply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CapturedDisplaySnapshot> QueryCurrentAsync(CancellationToken cancellationToken)
        {
            Calls.Add("query");
            if (QueryExceptionAtCounts.TryGetValue(++_queries, out var failure))
            {
                return Task.FromException<CapturedDisplaySnapshot>(failure);
            }
            return Task.FromResult(QueryResults.TryDequeue(out var result) ? result : defaultSnapshot);
        }

        public Task<ActivationRollbackSnapshot> CaptureRollbackSnapshotAsync(CancellationToken cancellationToken)
        {
            Calls.Add("snapshot");
            return Task.FromResult(new ActivationRollbackSnapshot(Guid.NewGuid(), 1, "synthetic.rollback"));
        }

        public Task DiscardRollbackSnapshotAsync(ActivationRollbackSnapshot snapshot, CancellationToken cancellationToken)
        {
            Calls.Add("discard-snapshot");
            return Task.CompletedTask;
        }

        public Task PreflightAsync(DisplayProfile profile, IReadOnlyList<ResolvedIdentityBinding> bindings, CancellationToken cancellationToken) =>
            Invoke("preflight");

        public Task ValidateCoreConfigurationAsync(DisplayProfile profile, IReadOnlyList<ResolvedIdentityBinding> bindings, CancellationToken cancellationToken) =>
            Invoke("validate");

        public async Task ApplyCoreTemporaryAsync(DisplayProfile profile, IReadOnlyList<ResolvedIdentityBinding> bindings, CancellationToken cancellationToken)
        {
            await Invoke("apply-core");
            if (PauseDuringApply)
            {
                ApplyStarted.SetResult();
                await ContinueApply.Task.WaitAsync(cancellationToken);
            }
        }

        public Task WaitForStableTopologyAsync(CancellationToken cancellationToken) => Invoke("settle");

        public Task ApplyScalingAsync(DisplayProfile profile, IReadOnlyList<ResolvedIdentityBinding> bindings, CancellationToken cancellationToken) =>
            Invoke("scale");

        public Task ApplyHdrAsync(DisplayProfile profile, IReadOnlyList<ResolvedIdentityBinding> bindings, CancellationToken cancellationToken) =>
            Invoke("hdr");

        public Task PersistCoreConfigurationAsync(CancellationToken cancellationToken) => Invoke("persist");

        public Task<bool> RollbackAsync(ActivationRollbackSnapshot snapshot, CancellationToken cancellationToken)
        {
            Calls.Add("rollback");
            return Task.FromResult(RollbackResult);
        }

        public Task<bool> EmergencyFallbackAsync(CancellationToken cancellationToken)
        {
            Calls.Add("fallback");
            return Task.FromResult(FallbackResult);
        }

        private Task Invoke(string call)
        {
            Calls.Add(call);
            return FailingCall == call
                ? Task.FromException(new ActivationFailureException(
                    $"activation.{call}",
                    $"Synthetic {call} failure.",
                    technicalDetails: FailingTechnicalDetails))
                : Task.CompletedTask;
        }
    }

    private sealed class FakeRecoveryCoordinator : IRecoveryCoordinator
    {
        internal FakeRecoverySession Session { get; } = new();

        public Task<IRecoverySession> StartAsync(ActivationRollbackSnapshot snapshot, CancellationToken cancellationToken) =>
            Task.FromResult<IRecoverySession>(Session);
    }

    private sealed class FakeRecoverySession : IRecoverySession
    {
        internal bool FailAfterPublication { get; set; }
        internal bool OwnershipLost { get; set; }
        internal RecoveryResult? CompanionResult { get; set; }
        internal RecoveryTerminalOutcome? TerminalOutcome { get; private set; }
        public bool IsTerminalPublished => TerminalOutcome is not null;

        public Task EnsureOwnershipAsync(CancellationToken cancellationToken) => OwnershipLost
            ? Task.FromException(new RecoveryOwnershipLostException()) : Task.CompletedTask;

        public async Task<RecoveryResult?> CompleteAsync(RecoveryTerminalOutcome outcome, CancellationToken cancellationToken)
        {
            TerminalOutcome ??= outcome;
            if (FailAfterPublication) throw new ActivationFailureException("activation.recovery.unacknowledged", "Synthetic acknowledgement failure.");
            if (outcome == RecoveryTerminalOutcome.ActivationSucceeded)
            {
                await SignalSuccessAsync(cancellationToken);
            }
            else
            {
                await SignalFailureHandledAsync(outcome == RecoveryTerminalOutcome.RollbackRestored, cancellationToken);
            }

            return CompanionResult;
        }
        internal bool SuccessSignaled { get; private set; }

        internal bool FailureHandled { get; private set; }

        public Task SignalSuccessAsync(CancellationToken cancellationToken)
        {
            SuccessSignaled = true;
            return Task.CompletedTask;
        }

        public Task SignalFailureHandledAsync(bool rollbackSucceeded, CancellationToken cancellationToken)
        {
            FailureHandled = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeActivationStateStore : IActivationStateStore
    {
        internal Guid? LastActivatedProfileId { get; private set; }

        public Task SetLastActivatedProfileAsync(Guid profileId, CancellationToken cancellationToken)
        {
            LastActivatedProfileId = profileId;
            return Task.CompletedTask;
        }
    }
}
