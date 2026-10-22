using MoniTopo.Core.Activation;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;
using MoniTopo.Core.Normalization;
using MoniTopo.Core.Recovery;

namespace MoniTopo.Core.Tests;

public sealed class ProfileActivationServiceTests
{
    [Fact]
    public async Task AlreadyActiveProfileVerifiesWithoutRecoveryOrDisplayWrites()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile)) { InitialSnapshot = Snapshot(profile) };
        var recovery = new FakeRecoveryCoordinator();
        var state = new FakeActivationStateStore();
        using var service = CreateService(backend, recovery, state);

        var result = await service.ActivateAsync(profile);

        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.False(result.MutationBegan);
        Assert.Equal("preflight", Assert.Single(backend.Calls, call => call != "query"));
        Assert.Equal(3, backend.Calls.Count(call => call == "query"));
        Assert.Equal(0, recovery.Starts);
        Assert.Equal(profile.Id, state.LastActivatedProfileId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PropertyOnlyChangesSkipCoreApplyValidationAndPersistence(bool scale, bool hdr)
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        {
            InitialSnapshot = Snapshot(profile, path => path with
            { WindowsUiScalePercent = scale ? 100 : path.WindowsUiScalePercent, HdrEnabled = hdr || path.HdrEnabled }),
        };
        var recovery = new FakeRecoveryCoordinator();
        var result = await CreateService(backend, recovery).ActivateAsync(profile);

        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.True(result.MutationBegan);
        Assert.Equal(scale ? 1 : 0, backend.Calls.Count(call => call == "scale"));
        Assert.Equal(hdr ? 1 : 0, backend.Calls.Count(call => call == "hdr"));
        Assert.DoesNotContain("apply-core", backend.Calls);
        Assert.DoesNotContain("validate", backend.Calls);
        Assert.DoesNotContain("persist", backend.Calls);
        Assert.Equal(1, recovery.Starts);
        Assert.True(recovery.Session.SuccessSignaled);
    }

    [Fact]
    public async Task CoreOnlyChangeDoesNotRewriteAlreadyMatchingProperties()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        { InitialSnapshot = Snapshot(profile, path => path with { RefreshRate = new RefreshRate(75, 1) }) };
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Equal(1, backend.Calls.Count(call => call == "apply-core"));
        Assert.Equal(1, backend.Calls.Count(call => call == "persist"));
        Assert.DoesNotContain("scale", backend.Calls);
        Assert.DoesNotContain("hdr", backend.Calls);
    }

    [Fact]
    public async Task ModeSwitchThatRestoresPropertiesNeedsNoSeparatePropertyWrites()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile)) { RestorePropertiesDuringCore = true };
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Contains("apply-core", backend.Calls);
        Assert.DoesNotContain("scale", backend.Calls);
        Assert.DoesNotContain("hdr", backend.Calls);
    }

    [Fact]
    public async Task ModeSwitchResettingPreviouslyMatchingPropertiesIsRechecked()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        {
            InitialSnapshot = Snapshot(profile, path => path with { RefreshRate = new RefreshRate(75, 1) }),
            ResetPropertiesDuringCore = true,
        };
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Equal(1, backend.Calls.Count(call => call == "scale"));
        Assert.Equal(1, backend.Calls.Count(call => call == "hdr"));
        Assert.True(backend.Calls.IndexOf("apply-core") < backend.Calls.IndexOf("scale"));
    }

    [Fact]
    public async Task HdrTransitionResettingDpiRepairsOnlyThatPropertyWithoutCoreReapply()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        { InitialSnapshot = Snapshot(profile, path => path with { HdrEnabled = true }), ResetScaleOnHdr = true };
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Equal(1, backend.Calls.Count(call => call == "hdr"));
        Assert.Equal(1, backend.Calls.Count(call => call == "scale"));
        Assert.True(backend.Calls.IndexOf("hdr") < backend.Calls.IndexOf("scale"));
        Assert.DoesNotContain("apply-core", backend.Calls);
        Assert.DoesNotContain("persist", backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HdrRestoringPreviousModeRevalidatesAndRepairsCoreBeforePersistence(bool coreInitiallyChanged)
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        {
            InitialSnapshot = Snapshot(profile, path => path with
            { RefreshRate = coreInitiallyChanged ? new RefreshRate(75, 1) : path.RefreshRate, HdrEnabled = true }),
            ResetCoreOnHdr = true,
            ResetScaleOnHdr = true,
        };
        var recovery = new FakeRecoveryCoordinator();

        var result = await CreateService(backend, recovery).ActivateAsync(profile);

        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Equal(coreInitiallyChanged ? 2 : 1, backend.Calls.Count(call => call == "apply-core"));
        Assert.Equal(coreInitiallyChanged ? 3 : 1, backend.Calls.Count(call => call == "validate"));
        Assert.Equal(1, backend.Calls.Count(call => call == "hdr"));
        Assert.Equal(1, backend.Calls.Count(call => call == "scale"));
        Assert.Equal(1, backend.Calls.Count(call => call == "persist"));
        Assert.True(backend.Calls.IndexOf("hdr") < backend.Calls.LastIndexOf("apply-core"));
        Assert.True(backend.Calls.LastIndexOf("apply-core") < backend.Calls.IndexOf("persist"));
        Assert.True(recovery.Session.SuccessSignaled);
    }

    [Fact]
    public async Task ScaleRestoringPreviousModeRepairsOnlyCoreAfterScaleConverges()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        { InitialSnapshot = Snapshot(profile, path => path with { WindowsUiScalePercent = 100 }), ResetCoreOnScale = true };

        var result = await CreateService(backend).ActivateAsync(profile);

        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Equal(1, backend.Calls.Count(call => call == "scale"));
        Assert.Equal(1, backend.Calls.Count(call => call == "apply-core"));
        Assert.True(backend.Calls.IndexOf("scale") < backend.Calls.IndexOf("validate"));
        Assert.DoesNotContain("hdr", backend.Calls);
        Assert.Contains("persist", backend.Calls);
    }

    [Fact]
    public async Task CoreRepairValidationFailureAfterHdrStillRollsBack()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        {
            InitialSnapshot = Snapshot(profile, path => path with { HdrEnabled = true }),
            ResetCoreOnHdr = true,
            FailingCall = "validate",
        };
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Failed, result.Outcome);
        Assert.True(result.MutationBegan);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal("activation.validate", result.ErrorCode);
        Assert.DoesNotContain("apply-core", backend.Calls);
        Assert.DoesNotContain("persist", backend.Calls);
    }

    [Fact]
    public async Task RepeatedCoreAndHdrConflictIsBoundedAndRollsBackWithoutPersistence()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        {
            InitialSnapshot = Snapshot(profile, path => path with { HdrEnabled = true }),
            ResetCoreOnHdr = true,
            ResetPropertiesDuringCore = true,
        };
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Failed, result.Outcome);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal(2, backend.Calls.Count(call => call == "apply-core"));
        Assert.Equal(3, backend.Calls.Count(call => call == "hdr"));
        Assert.DoesNotContain("persist", backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistenceResettingPropertiesRepairsWithoutRedundantCoreWrite(bool hdr)
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        {
            InitialSnapshot = Snapshot(profile, path => path with { RefreshRate = new RefreshRate(75, 1) }),
            ResetScaleOnPersist = true,
            ResetHdrOnPersist = hdr,
        };
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Equal(1, backend.Calls.Count(call => call == "apply-core"));
        Assert.Equal(1, backend.Calls.Count(call => call == "persist"));
        Assert.Equal(1, backend.Calls.Count(call => call == "scale"));
        Assert.Equal(hdr ? 1 : 0, backend.Calls.Count(call => call == "hdr"));
        Assert.True(backend.Calls.IndexOf("persist") < backend.Calls.IndexOf("scale"));
    }

    [Fact]
    public async Task FreshPlanCatchesLayoutChangeBeforeTheFirstPropertyWrite()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile));
        backend.QueryResults.Enqueue(Snapshot(profile, path => path with { HdrEnabled = true }));
        backend.QueryResults.Enqueue(Snapshot(profile, path => path with { HdrEnabled = true, RefreshRate = new RefreshRate(75, 1) }));
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Equal(1, backend.Calls.Count(call => call == "validate"));
        Assert.Contains("apply-core", backend.Calls);
        Assert.Contains("persist", backend.Calls);
    }

    [Fact]
    public async Task FreshPlanSkipsLayoutThatHasAlreadyBeenChangedToTheDesiredState()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile));
        backend.QueryResults.Enqueue(Snapshot(profile, path => path with { HdrEnabled = true, RefreshRate = new RefreshRate(75, 1) }));
        backend.QueryResults.Enqueue(Snapshot(profile, path => path with { HdrEnabled = true }));
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Contains("hdr", backend.Calls);
        Assert.DoesNotContain("apply-core", backend.Calls);
        Assert.DoesNotContain("persist", backend.Calls);
    }

    [Fact]
    public async Task NoOpVerificationDriftDoesNotSilentlyStartMutating()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile));
        backend.QueryResults.Enqueue(Snapshot(profile));
        backend.QueryResults.Enqueue(Snapshot(profile, path => path with { HdrEnabled = true }));
        var recovery = new FakeRecoveryCoordinator();
        var state = new FakeActivationStateStore();
        var result = await CreateService(backend, recovery, state).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Failed, result.Outcome);
        Assert.False(result.MutationBegan);
        Assert.Equal(0, recovery.Starts);
        Assert.Null(state.LastActivatedProfileId);
        Assert.Equal("activation.state.changed", result.ErrorCode);
        Assert.Equal("preflight", Assert.Single(backend.Calls, call => call != "query"));
    }

    [Fact]
    public async Task HdrOnlyFailureStillHasRecoveryAndExactRollback()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        { InitialSnapshot = Snapshot(profile, path => path with { HdrEnabled = true }), FailingCall = "hdr" };
        var recovery = new FakeRecoveryCoordinator();
        var result = await CreateService(backend, recovery).ActivateAsync(profile);
        Assert.True(result.RollbackSucceeded);
        Assert.True(result.MutationBegan);
        Assert.Equal(1, recovery.Starts);
        Assert.DoesNotContain("apply-core", backend.Calls);
        Assert.DoesNotContain("persist", backend.Calls);
        Assert.Equal(RecoveryTerminalOutcome.RollbackRestored, recovery.Session.TerminalOutcome);
    }

    [Fact]
    public async Task SlowScaleObservationIsPolledWithoutRepeatingTheSetter()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        { InitialSnapshot = Snapshot(profile, path => path with { WindowsUiScalePercent = 100 }), ScaleObservationLag = 2 };
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.Equal(1, backend.Calls.Count(call => call == "scale"));
        Assert.DoesNotContain("apply-core", backend.Calls);
    }

    [Fact]
    public async Task FailedScaleConvergenceDoesNotHammerTheSetterAndRollsBack()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        { InitialSnapshot = Snapshot(profile, path => path with { WindowsUiScalePercent = 100 }), IgnoreScalingWrites = true };
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Failed, result.Outcome);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal(1, backend.Calls.Count(call => call == "scale"));
        Assert.DoesNotContain("apply-core", backend.Calls);
    }

    [Fact]
    public async Task StateAlreadyCorrectUnderOwnershipNeedsNoSetterOrPersistence()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile));
        backend.QueryResults.Enqueue(Snapshot(profile, path => path with { HdrEnabled = true }));
        backend.QueryResults.Enqueue(Snapshot(profile));
        var recovery = new FakeRecoveryCoordinator();
        var result = await CreateService(backend, recovery).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.False(result.MutationBegan);
        Assert.Equal(["preflight", "snapshot"], backend.Calls.Where(call => call != "query"));
        Assert.True(recovery.Session.SuccessSignaled);
    }

    [Fact]
    public async Task MissingTargetBeforeFirstWriteAbortsWithoutRollbackOrFallback()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile));
        backend.QueryResults.Enqueue(Snapshot(profile, path => path with { HdrEnabled = true }));
        backend.QueryResults.Enqueue(new CapturedDisplaySnapshot([], [], "", DateTimeOffset.UnixEpoch));
        var recovery = new FakeRecoveryCoordinator();
        var result = await CreateService(backend, recovery).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Failed, result.Outcome);
        Assert.False(result.MutationBegan);
        Assert.False(result.RollbackAttempted);
        Assert.DoesNotContain("fallback", backend.Calls);
        Assert.Equal(RecoveryTerminalOutcome.AbortedBeforeMutation, recovery.Session.TerminalOutcome);
    }

    [Fact]
    public async Task ConflictingDriverPropertySideEffectsAreBoundedThenRolledBack()
    {
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        {
            InitialSnapshot = Snapshot(profile, path => path with { WindowsUiScalePercent = 100, HdrEnabled = true }),
            ResetScaleOnHdr = true,
            ResetHdrOnScale = true,
        };
        var result = await CreateService(backend).ActivateAsync(profile);
        Assert.Equal(ActivationOutcome.Failed, result.Outcome);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal(3, backend.Calls.Count(call => call == "scale"));
        Assert.Equal(3, backend.Calls.Count(call => call == "hdr"));
        Assert.DoesNotContain("persist", backend.Calls);
    }

    [Fact]
    public async Task CancellationAfterPropertyWriteStillRollsBackUnderRecovery()
    {
        using var cancellation = new CancellationTokenSource();
        var profile = TestData.Profile();
        var backend = new FakeActivationBackend(Snapshot(profile))
        { InitialSnapshot = Snapshot(profile, path => path with { HdrEnabled = true }), OnHdr = cancellation.Cancel };
        var recovery = new FakeRecoveryCoordinator();
        var result = await CreateService(backend, recovery).ActivateAsync(profile, cancellationToken: cancellation.Token);
        Assert.True(result.MutationBegan);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal(RecoveryTerminalOutcome.RollbackRestored, recovery.Session.TerminalOutcome);
        Assert.DoesNotContain("persist", backend.Calls);
    }

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
            ["preflight", "snapshot", "validate", "validate", "apply-core", "settle", "scale", "hdr", "persist"],
            backend.Calls.Where(call => call != "query"));
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
        var backend = new FakeActivationBackend(mismatched) { IgnoreScalingWrites = true };
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
        await backend.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        await backend.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        Assert.True(backend.Calls.Count(call => call == "query") >= 7);
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
            verificationTimeout: TimeSpan.FromMilliseconds(1100));

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
        var paths = profile.Displays.Select(path => mutate?.Invoke(path) ?? path).ToArray();
        var connected = paths.Select((path, index) => new ConnectedDisplayState($"runtime-{index + 1}", path.Identity, true, path.FriendlyLabel, path)).ToArray();
        return new CapturedDisplaySnapshot(paths, connected, profile.PrimaryDisplayId, DateTimeOffset.UtcNow);
    }

    private static CapturedDisplaySnapshot WithPaths(CapturedDisplaySnapshot snapshot, Func<DesiredDisplayPath, DesiredDisplayPath> mutate)
    {
        var paths = snapshot.ActivePaths.Select(mutate).ToArray();
        return snapshot with
        {
            ActivePaths = paths,
            ConnectedDisplays = snapshot.ConnectedDisplays.Select(connected => connected.ActivePath is { } path
            ? connected with { ActivePath = paths.Single(changed => changed.DisplayId == path.DisplayId) } : connected).ToArray()
        };
    }

    private sealed class FakeActivationBackend(CapturedDisplaySnapshot defaultSnapshot) : IDisplayActivationBackend
    {
        internal List<string> Calls { get; } = [];

        internal Queue<CapturedDisplaySnapshot> QueryResults { get; } = new();
        internal Dictionary<int, Exception> QueryExceptionAtCounts { get; } = [];
        private int _queries;
        private CapturedDisplaySnapshot? _current;
        private CapturedDisplaySnapshot? _laggedScaleObservation;
        private int _remainingScaleLag;
        internal CapturedDisplaySnapshot? InitialSnapshot { get; init; }
        internal int ScaleObservationLag { get; init; }
        internal bool IgnoreScalingWrites { get; init; }
        internal bool ResetScaleOnHdr { get; init; }
        internal bool ResetHdrOnScale { get; init; }
        internal bool ResetCoreOnHdr { get; init; }
        internal bool ResetCoreOnScale { get; init; }
        internal bool ResetScaleOnPersist { get; init; }
        internal bool ResetHdrOnPersist { get; init; }
        internal Action? OnHdr { get; init; }
        internal bool RestorePropertiesDuringCore { get; init; }
        internal bool ResetPropertiesDuringCore { get; init; }

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
            _current ??= InitialSnapshot ?? WithPaths(defaultSnapshot, path => path with
            {
                SourceResolution = path.SourceResolution with { Width = path.SourceResolution.Width - 1 },
                WindowsUiScalePercent = path.WindowsUiScalePercent + 25,
                HdrEnabled = !path.HdrEnabled,
            });
            if (QueryResults.TryDequeue(out var result)) _current = result;
            if (_remainingScaleLag > 0)
            {
                _remainingScaleLag--;
                return Task.FromResult(_laggedScaleObservation!);
            }
            return Task.FromResult(_current);
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

        public Task PreflightAsync(DisplayProfile profile, IReadOnlyList<ResolvedIdentityBinding> bindings, CancellationToken cancellationToken)
        {
            return Invoke("preflight");
        }

        public Task ValidateCoreConfigurationAsync(DisplayProfile profile, IReadOnlyList<ResolvedIdentityBinding> bindings, CancellationToken cancellationToken) =>
            Invoke("validate");

        public async Task ApplyCoreTemporaryAsync(DisplayProfile profile, IReadOnlyList<ResolvedIdentityBinding> bindings, CancellationToken cancellationToken)
        {
            await Invoke("apply-core");
            _current = WithPaths(defaultSnapshot, observed =>
            {
                var before = _current!.ActivePaths.SingleOrDefault(path => path.DisplayId == observed.DisplayId) ?? observed;
                if (ResetPropertiesDuringCore) return observed with { WindowsUiScalePercent = 100, HdrEnabled = !observed.HdrEnabled };
                return RestorePropertiesDuringCore ? observed : observed with
                { WindowsUiScalePercent = before.WindowsUiScalePercent, HdrEnabled = before.HdrEnabled };
            });
            if (PauseDuringApply)
            {
                ApplyStarted.SetResult();
                await ContinueApply.Task.WaitAsync(cancellationToken);
            }
        }

        public Task WaitForStableTopologyAsync(CancellationToken cancellationToken) => Invoke("settle");

        public async Task ApplyScalingAsync(DisplayProfile profile, IReadOnlyList<ResolvedIdentityBinding> bindings, CancellationToken cancellationToken)
        {
            await Invoke("scale");
            _laggedScaleObservation = _current;
            _remainingScaleLag = ScaleObservationLag;
            if (!IgnoreScalingWrites) _current = WithPaths(_current!, path => path with
            {
                WindowsUiScalePercent = profile.Displays.Single(display => display.DisplayId == path.DisplayId).WindowsUiScalePercent,
                HdrEnabled = ResetHdrOnScale || path.HdrEnabled,
                RefreshRate = ResetCoreOnScale ? new RefreshRate(75, 1) : path.RefreshRate,
            });
        }

        public async Task ApplyHdrAsync(DisplayProfile profile, IReadOnlyList<ResolvedIdentityBinding> bindings, CancellationToken cancellationToken)
        {
            await Invoke("hdr");
            _current = WithPaths(_current!, path => path with
            {
                HdrEnabled = profile.Displays.Single(display => display.DisplayId == path.DisplayId).HdrEnabled,
                WindowsUiScalePercent = ResetScaleOnHdr ? 100 : path.WindowsUiScalePercent,
                RefreshRate = ResetCoreOnHdr ? new RefreshRate(75, 1) : path.RefreshRate,
            });
            OnHdr?.Invoke();
        }

        public async Task PersistCoreConfigurationAsync(CancellationToken cancellationToken)
        {
            await Invoke("persist");
            _current = WithPaths(_current!, path => path with
            {
                WindowsUiScalePercent = ResetScaleOnPersist ? 100 : path.WindowsUiScalePercent,
                HdrEnabled = ResetHdrOnPersist || path.HdrEnabled,
            });
        }

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
        internal int Starts { get; private set; }

        public Task<IRecoverySession> StartAsync(ActivationRollbackSnapshot snapshot, CancellationToken cancellationToken)
        {
            Starts++;
            return Task.FromResult<IRecoverySession>(Session);
        }
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
