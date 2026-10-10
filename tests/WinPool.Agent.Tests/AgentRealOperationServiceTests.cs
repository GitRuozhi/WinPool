using System.Text.Json;
using WinPool.Agent;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent.Tests;

public sealed class AgentRealOperationServiceTests
{
    [Fact]
    public async Task DurableUnknownAllowsShutdownWithoutChangingEvidenceOrReplayingOnRestart()
    {
        var backend = new RecordingBackend { UnknownStepId = "initialize-gpt" };
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var plan = await PrepareAndAcceptAsync(fixture.Service, fixture.Proposal, fixture.Session);
        await AwaitDurableStateAsync(fixture.Plans, plan.OperationId, PersistedOperationState.OutcomeUnknown);
        var before = await fixture.Plans.GetAsync(plan.OperationId);
        var beforeSteps = await fixture.Plans.GetStepsAsync(plan.OperationId);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (!await fixture.Service.TryCloseAdmissionForShutdownAsync())
                await Task.Delay(10, timeout.Token);

        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await fixture.Plans.GetAsync(plan.OperationId)));
        Assert.Equal(beforeSteps, await fixture.Plans.GetStepsAsync(plan.OperationId));
        Assert.True(await fixture.Plans.HasRealWriteBarrierAsync());
        Assert.False((await fixture.Service.PrepareAsync(new(fixture.Proposal, Guid.NewGuid(),
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default)).IsSuccess);
        Assert.Equal(1, backend.ExecuteCalls);

        var reconciled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartedBackend = new RecordingBackend { Reconciliation = (_, steps) =>
        {
            reconciled.TrySetResult();
            return Task.FromResult(new RealReconciliationResult(RealOperationState.OutcomeUnknown,
                steps, "fake.readonly_recovery", false));
        }};
        var restarted = new AgentRealOperationService(fixture.Plans, fixture.Events, restartedBackend,
            new FixedMachineIdentity(), authority: null, timeProvider: null,
            isSessionStillArmed: _ => true, isAdministrator: () => true);
        await restarted.InitializeRecoveryAsync();
        await reconciled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await fixture.Plans.HasRealWriteBarrierAsync());
        Assert.Equal(0, restartedBackend.ExecuteCalls);
        Assert.False((await restarted.EnterModeAsync(new(fixture.Session.ProductSessionId,
            CorrelationId.New()), fixture.Session, default)).IsSuccess);
    }

    [Fact]
    public async Task ShutdownClosesNewWriteAdmissionButDoesNotCancelAnActiveProvider()
    {
        var backend = new RecordingBackend { PauseExecution = true };
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        try
        {
            var plan = await PrepareAndAcceptAsync(fixture.Service, fixture.Proposal, fixture.Session);
            await backend.ExecutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await fixture.Service.TryCloseAdmissionForShutdownAsync());
            Assert.False(backend.ExecutionToken.IsCancellationRequested);
            Assert.Equal(PersistedOperationState.Running, (await fixture.Plans.GetAsync(plan.OperationId))!.State);
            backend.ReleaseExecution.TrySetResult();
            await AwaitSucceededAsync(fixture.Service, plan.OperationId, fixture.Session);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (!await fixture.Service.TryCloseAdmissionForShutdownAsync())
                    await Task.Delay(10, timeout.Token);
            Assert.False((await fixture.Service.PrepareAsync(new(fixture.Proposal, Guid.NewGuid(),
                fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default)).IsSuccess);
            Assert.Equal(1, backend.ExecuteCalls);
        }
        finally { backend.ReleaseExecution.TrySetResult(); }
    }

    [Fact]
    public async Task CreationBoundsAreReadOnlyAndRequireTheArmedProductSession()
    {
        var backend = new RecordingBackend();
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var pool = new StorageObjectId(fixture.Proposal.SystemId, StorageObjectKind.StoragePool, "exact-pool");
        var request = new QueryAgentRealVirtualDiskCreationRangeRequest(pool, fixture.Session.ProductSessionId, CorrelationId.New());
        var wrongSession = await fixture.Service.QueryVirtualDiskCreationRangeAsync(
            request with { ProductSessionId = "other" }, fixture.Session, CancellationToken.None);
        Assert.False(wrongSession.IsSuccess);
        var range = await fixture.Service.QueryVirtualDiskCreationRangeAsync(request, fixture.Session, CancellationToken.None);
        Assert.Equal(pool, Assert.IsType<AgentRealVirtualDiskCreationRangeResponse>(range.Value).Range.Target);
        Assert.Equal(0, backend.ExecuteCalls);
        Assert.Empty(await fixture.Plans.ListUnfinishedAsync());
    }

    [Fact]
    public async Task RecoveryEnumerationReturnsDurableIdentityWithoutAcceptingOrExecuting()
    {
        var backend = new RecordingBackend();
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var prepared = await fixture.Service.PrepareAsync(new PrepareAgentRealOperationRequest(
            fixture.Proposal, Guid.NewGuid(), fixture.Session.ProductSessionId, CorrelationId.New()),
            fixture.Session, CancellationToken.None);
        var original = Assert.IsType<AgentRealOperationResponse>(prepared.Value);
        var result = await fixture.Service.ListRecoverableAsync(new(CorrelationId.New()),
            fixture.Session with { IsArmed = false }, CancellationToken.None);
        var recovered = Assert.Single(Assert.IsType<AgentRecoverableRealOperationsResponse>(result.Value).Operations);
        Assert.Equal(original.Plan.OperationId, recovered.Plan.OperationId);
        Assert.Equal(RealOperationState.Prepared, recovered.State);
        Assert.Equal(0, backend.ExecuteCalls);
    }

    [Fact]
    public async Task MonitorObserverFailureCannotTurnVerifiedWriteIntoAnUnknownOutcome()
    {
        var backend = new RecordingBackend();
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        fixture.Service.AttachEditObserver(new FailingEditObserver());
        var prepared = Assert.IsType<AgentRealOperationResponse>((await fixture.Service.PrepareAsync(
            new(fixture.Proposal, Guid.NewGuid(), fixture.Session.ProductSessionId, CorrelationId.New()),
            fixture.Session, CancellationToken.None)).Value);
        var accepted = await fixture.Service.AcceptAsync(new(prepared.Plan.OperationId, prepared.Plan.PlanHash,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, CancellationToken.None);
        Assert.True(accepted.IsSuccess);
        var completed = await AwaitSucceededAsync(fixture.Service, prepared.Plan.OperationId, fixture.Session);
        Assert.False(completed.RequiresReconciliation);
        Assert.Equal(1, backend.ExecuteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveProviderErrorPersistsReconciledNoEffectBeforeTerminalOperation(bool verifiedPrefix)
    {
        var backend = new RecordingBackend
        {
            UnknownStepId = verifiedPrefix ? "format" : "initialize-gpt",
            UnknownCode = "provider.error-outcome-unknown",
            Reconciliation = (plan, steps) => Task.FromResult(ObservedNoEffect(plan, steps))
        };
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var proposal = verifiedPrefix
            ? CreatePartitionFormatLetterProposal(fixture.Proposal.SystemId,
                fixture.Proposal.Targets.Single(target => target.Kind == StorageObjectKind.OsDisk))
            : fixture.Proposal;
        var prepared = Assert.IsType<AgentRealOperationResponse>((await fixture.Service.PrepareAsync(
            new(proposal, Guid.NewGuid(), fixture.Session.ProductSessionId, CorrelationId.New()),
            fixture.Session, CancellationToken.None)).Value);
        await fixture.Service.AcceptAsync(new(prepared.Plan.OperationId, prepared.Plan.PlanHash,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, CancellationToken.None);
        var terminal = verifiedPrefix ? PersistedOperationState.PartiallyCompleted : PersistedOperationState.Failed;
        await AwaitDurableStateAsync(fixture.Plans, prepared.Plan.OperationId, terminal);

        var steps = await fixture.Plans.GetStepsAsync(prepared.Plan.OperationId);
        var failed = Assert.Single(steps, step => step.State == PersistedOperationStepState.Failed);
        var evidence = JsonSerializer.Deserialize<WindowsObservedNoEffectStepEvidence>(failed.EvidenceJson!)!;
        Assert.True(evidence.WindowsCallIssued);
        Assert.Equal("provider.error-outcome-unknown", evidence.ProviderResult.Code);
        Assert.NotNull(evidence.ProviderResult.ProviderError);
        Assert.Equal(prepared.Plan.RealOperation!.PhysicalMemberFingerprint, evidence.PhysicalMemberFingerprint);
        Assert.Equal(evidence.BeforeFingerprint, evidence.FirstObservedFingerprint);
        Assert.Equal(evidence.BeforeFingerprint, evidence.SecondObservedFingerprint);
        Assert.Equal(2, evidence.StorageJobQueries.Count);
        Assert.False(await fixture.Plans.HasRealWriteBarrierAsync());
        Assert.Equal(verifiedPrefix ? 2 : 1, backend.ExecuteCalls);
        var history = await fixture.Events.ListAsync(prepared.Plan.OperationId);
        var stepEvent = Assert.Single(history, item => item.Event.Code == "fake.observed_no_effect_step");
        var terminalEvent = Assert.Single(history, item => item.Event.Code == "fake.observed_no_effect");
        Assert.True(stepEvent.EventId < terminalEvent.EventId);
        var status = Assert.IsType<AgentRealOperationResponse>((await fixture.Service.QueryAsync(
            new(prepared.Plan.OperationId, CorrelationId.New()), fixture.Session, CancellationToken.None)).Value);
        Assert.Equal(verifiedPrefix ? RealOperationState.PartiallyCompleted : RealOperationState.Failed, status.State);
        Assert.Equal("fake.observed_no_effect_step", status.Steps.Single(step => step.StepId == failed.StepId).Code);
    }

    [Fact]
    public async Task ExplicitQueryPersistsLaterObservedNoEffectWithoutRepeatingProviderCall()
    {
        var reconcileCalls = 0;
        var backend = new RecordingBackend
        {
            UnknownStepId = "initialize-gpt",
            UnknownCode = "provider.error-outcome-unknown",
            Reconciliation = (plan, steps) => Task.FromResult(
                Interlocked.Increment(ref reconcileCalls) == 1
                    ? new RealReconciliationResult(RealOperationState.OutcomeUnknown, steps, "fake.not_yet_known", false)
                    : ObservedNoEffect(plan, steps))
        };
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var plan = await PrepareAndAcceptAsync(fixture.Service, fixture.Proposal, fixture.Session);
        await AwaitDurableStateAsync(fixture.Plans, plan.OperationId, PersistedOperationState.OutcomeUnknown);
        Assert.True(await fixture.Plans.HasRealWriteBarrierAsync());
        var pending = Assert.IsType<AgentRealOperationResponse>((await fixture.Service.QueryAsync(
            new(plan.OperationId, CorrelationId.New()), fixture.Session, CancellationToken.None)).Value);
        Assert.Equal(RealOperationState.OutcomeUnknown, pending.State);
        // A query can observe Unknown while the original worker is finishing;
        // polling may schedule recovery only after that worker has stopped.
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while ((await fixture.Plans.GetAsync(plan.OperationId, timeout.Token))!.State != PersistedOperationState.Failed)
            {
                await fixture.Service.QueryAsync(new(plan.OperationId, CorrelationId.New()), fixture.Session, timeout.Token);
                await Task.Delay(10, timeout.Token);
            }
        var step = Assert.Single(await fixture.Plans.GetStepsAsync(plan.OperationId));
        Assert.Equal(PersistedOperationStepState.Failed, step.State);
        Assert.True(JsonSerializer.Deserialize<WindowsObservedNoEffectStepEvidence>(step.EvidenceJson!)!.WindowsCallIssued);
        Assert.False(await fixture.Plans.HasRealWriteBarrierAsync());
        Assert.Equal(2, Volatile.Read(ref reconcileCalls));
        Assert.Equal(1, backend.ExecuteCalls);
    }

    [Fact]
    public async Task AdapterExceptionDeletePersistsObservedUnchangedFailureWithoutClaimingVerifiedOrNoCall()
    {
        var backend = new RecordingBackend
        {
            ExecutionException = new TimeoutException("read-only capture timed out before adapter"),
            Reconciliation = (plan, steps) =>
            {
                Assert.IsType<DeleteVirtualDiskCommand>(Assert.Single(plan.RealOperation!.Steps).Command);
                Assert.Equal("adapter_exception", Assert.Single(steps).ResultEvidence);
                // This fake supplies the backend's typed conclusion. The exact
                // two-capture/storage proof is tested at the Windows boundary.
                var target = new WindowsStorageCommandTarget(StorageObjectKind.VirtualDisk, "vd-uid", "vd-oid",
                    "serial", "os-uid", "os-path", null, null, "", null, null, "pool-uid", "physical-uid",
                    "subsystem-uid", plan.RealOperation.TargetFingerprint);
                var evidence = new WindowsObservedUnchangedDeleteStepEvidence(true, true, true, false, false,
                    "real.reconciliation_observed_unchanged_delete_not_verified", steps[0].ResultEvidence!, target,
                    plan.RealOperation.PhysicalMemberFingerprint, plan.RealOperation.TargetFingerprint,
                    plan.RealOperation.TargetFingerprint, plan.RealOperation.TargetFingerprint, [], [], [], null);
                return Task.FromResult(new RealReconciliationResult(RealOperationState.Failed,
                    [steps[0] with { State = RealOperationStepState.Failed, Code = evidence.Code,
                        ResultEvidence = JsonSerializer.Serialize(evidence) }], evidence.Code, true));
            }
        };
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var physical = Assert.Single(fixture.Proposal.Targets, target => target.Kind == StorageObjectKind.PhysicalDisk);
        var vd = new StorageObjectId(physical.System, StorageObjectKind.VirtualDisk, "vd-uid");
        var proposal = new RealOperationIntentRequest(OperationIntent.DeleteVirtualDisk, physical.System, [physical, vd],
            [new RealOperationStep("delete-vd", new DeleteVirtualDiskCommand(RealTargetReference.ForExisting(vd)),
                [], "VD present", "VD absent", "VD deletion", "fake capability")], "VD absent");
        var plan = await PrepareAndAcceptAsync(fixture.Service, proposal, fixture.Session);
        await AwaitDurableStateAsync(fixture.Plans, plan.OperationId, PersistedOperationState.Failed);
        var step = Assert.Single(await fixture.Plans.GetStepsAsync(plan.OperationId));
        Assert.Equal(PersistedOperationStepState.Failed, step.State);
        var observed = JsonSerializer.Deserialize<WindowsObservedUnchangedDeleteStepEvidence>(step.EvidenceJson!)!;
        Assert.True(observed.WindowsCallIssued);
        Assert.True(observed.NotVerified);
        Assert.True(observed.TargetStillPresent);
        Assert.False(observed.NoWindowsCall);
        Assert.False(observed.NoEffect);
        Assert.Equal("adapter_exception", observed.OriginalResultEvidence);
        Assert.Single(await fixture.Events.ListAsync(plan.OperationId), item => item.Event.Code == "operation.step.call_issued");
        Assert.DoesNotContain(await fixture.Events.ListAsync(plan.OperationId), item => item.Event.Kind == ExecutionEventKind.Completed);
        Assert.Equal(1, backend.ExecuteCalls);
        Assert.False(await fixture.Plans.HasRealWriteBarrierAsync());
    }

    [Fact]
    public async Task UnknownQueryCannotReconcileWhileTheOriginalWorkerIsStillRunning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationPlanRepository? repository = null;
        var reconcileCalls = 0;
        var backend = new RecordingBackend
        {
            UnknownStepId = "initialize-gpt",
            Reconciliation = async (plan, steps) =>
            {
                if (Interlocked.Increment(ref reconcileCalls) > 1) return ObservedNoEffect(plan, steps);
                Assert.True(await repository!.TransitionAsync(plan.OperationId, PersistedOperationState.Running,
                    PersistedOperationState.OutcomeUnknown, new ExecutionEvent(plan.OperationId,
                        ExecutionEventKind.Failed, DateTimeOffset.UtcNow, "test.unknown_while_worker_active", "worker still running")));
                entered.TrySetResult();
                await release.Task;
                return new(RealOperationState.OutcomeUnknown, steps, "fake.unknown", false);
            }
        };
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        repository = fixture.Plans;
        try
        {
            var plan = await PrepareAndAcceptAsync(fixture.Service, fixture.Proposal, fixture.Session);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
                Assert.IsType<AgentRealOperationResponse>((await fixture.Service.QueryAsync(
                    new(plan.OperationId, CorrelationId.New()), fixture.Session, default)).Value)));
            Assert.All(responses, response => Assert.Equal(RealOperationState.OutcomeUnknown, response.State));
            await Task.Delay(50); // Let a mistakenly scheduled background query enter the fake backend.
            Assert.Equal(1, Volatile.Read(ref reconcileCalls));
            Assert.True(await fixture.Plans.HasRealWriteBarrierAsync());
            release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await fixture.Plans.GetAsync(plan.OperationId, timeout.Token))!.State != PersistedOperationState.Failed)
            {
                await fixture.Service.QueryAsync(new(plan.OperationId, CorrelationId.New()), fixture.Session, timeout.Token);
                await Task.Delay(10, timeout.Token);
            }
            Assert.Equal(2, Volatile.Read(ref reconcileCalls));
            Assert.Equal(1, backend.ExecuteCalls);
            Assert.False(await fixture.Plans.HasRealWriteBarrierAsync());
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("missing-evidence")]
    [InlineData("duplicate-step")]
    [InlineData("contradict-terminal-step")]
    public async Task InvalidReconciledStepEvidenceCannotReleaseLiveBarrier(string invalid)
    {
        var backend = new RecordingBackend
        {
            UnknownStepId = invalid == "contradict-terminal-step" ? null : "initialize-gpt",
            Reconciliation = (plan, steps) =>
            {
                var result = ObservedNoEffect(plan, steps);
                var observed = result.Steps[0] with
                {
                    State = RealOperationStepState.Failed,
                    ResultEvidence = invalid == "missing-evidence" ? null : "contradictory evidence"
                };
                return Task.FromResult(result with
                {
                    State = RealOperationState.Failed,
                    Steps = invalid == "duplicate-step" ? [observed, observed] : [observed]
                });
            }
        };
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var plan = await PrepareAndAcceptAsync(fixture.Service, fixture.Proposal, fixture.Session);
        await AwaitDurableStateAsync(fixture.Plans, plan.OperationId, PersistedOperationState.OutcomeUnknown);
        Assert.True(await fixture.Plans.HasRealWriteBarrierAsync());
        Assert.Equal(1, backend.ExecuteCalls);
        Assert.DoesNotContain(await fixture.Events.ListAsync(plan.OperationId), item =>
            item.Event.Kind == ExecutionEventKind.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciledStepOrOperationCasConflictKeepsUnknownAndConcurrentQueriesNeverReplay(bool stepConflict)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationPlanRepository? repository = null;
        var reconciliationCalls = 0;
        var backend = new RecordingBackend
        {
            UnknownStepId = "initialize-gpt",
            UnknownCode = "provider.error-outcome-unknown",
            Reconciliation = async (plan, steps) =>
            {
                if (Interlocked.Increment(ref reconciliationCalls) > 1)
                {
                    recoveryEntered.TrySetResult();
                    await releaseRecovery.Task;
                    return new(RealOperationState.OutcomeUnknown, steps, "fake.keep_unknown", false);
                }
                entered.TrySetResult();
                await release.Task;
                var executionEvent = new ExecutionEvent(plan.OperationId, ExecutionEventKind.Progress,
                    DateTimeOffset.UtcNow, "test.concurrent_transition", "exact concurrent durable CAS");
                // Use the actual repository's legal transition API to make the
                // runner's previously read step/op state stale. No SQL edits.
                var transitioned = stepConflict
                    ? await repository!.TransitionStepAsync(plan.OperationId, steps[0].StepId,
                        PersistedOperationStepState.OutcomeUnknown, PersistedOperationStepState.Verified,
                        null, "concurrent verified evidence", executionEvent)
                    : await repository!.TransitionAsync(plan.OperationId, PersistedOperationState.Running,
                        PersistedOperationState.OutcomeUnknown, executionEvent);
                Assert.True(transitioned);
                return ObservedNoEffect(plan, steps);
            }
        };
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        repository = fixture.Plans;
        try
        {
            var plan = await PrepareAndAcceptAsync(fixture.Service, fixture.Proposal, fixture.Session);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
                Assert.IsType<AgentRealOperationResponse>((await fixture.Service.QueryAsync(
                    new(plan.OperationId, CorrelationId.New()), fixture.Session, CancellationToken.None)).Value)));
            Assert.All(running, status => Assert.Equal(RealOperationState.Running, status.State));
            Assert.Equal(1, Volatile.Read(ref reconciliationCalls));
            release.TrySetResult();
            await AwaitDurableStateAsync(repository, plan.OperationId, PersistedOperationState.OutcomeUnknown);
            if (!stepConflict)
            {
                // The competing actor changes the operation before the runner
                // persists its observed step; wait for that durable write too.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (Assert.Single(await repository.GetStepsAsync(plan.OperationId, timeout.Token)).State
                    != PersistedOperationStepState.Failed)
                    await Task.Delay(10, timeout.Token);
            }
            var unknown = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
                Assert.IsType<AgentRealOperationResponse>((await fixture.Service.QueryAsync(
                    new(plan.OperationId, CorrelationId.New()), fixture.Session, CancellationToken.None)).Value)));
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (!recoveryEntered.Task.IsCompleted)
                {
                    await fixture.Service.QueryAsync(new(plan.OperationId, CorrelationId.New()), fixture.Session, timeout.Token);
                    await Task.Delay(10, timeout.Token);
                }
            Assert.All(unknown, status => Assert.Equal(RealOperationState.OutcomeUnknown, status.State));
            Assert.Equal(2, Volatile.Read(ref reconciliationCalls));
            Assert.True(await repository.HasRealWriteBarrierAsync());
            Assert.Equal(1, backend.ExecuteCalls);
            var step = Assert.Single(await repository.GetStepsAsync(plan.OperationId));
            Assert.Equal(stepConflict ? PersistedOperationStepState.Verified : PersistedOperationStepState.Failed,
                step.State);
            Assert.DoesNotContain(await fixture.Events.ListAsync(plan.OperationId), item =>
                item.Event.Code == "fake.observed_no_effect");
        }
        finally
        {
            release.TrySetResult();
            releaseRecovery.TrySetResult();
        }
    }

    private static RealReconciliationResult ObservedNoEffect(OperationPlan plan,
        IReadOnlyList<RealOperationStepProgress> steps)
    {
        var provider = new WindowsStorageCommandResult(true, "provider.error-outcome-unknown",
            null, null, null, null, null, null, "Not Supported: insufficient eligible resources");
        var evidence = JsonSerializer.Serialize(new WindowsObservedNoEffectStepEvidence(true,
            "fake.observed_no_effect_step", plan.RealOperation!.PhysicalMemberFingerprint,
            "unchanged-whole-fingerprint", "unchanged-whole-fingerprint", "unchanged-whole-fingerprint",
            provider, [new(DateTimeOffset.UtcNow, []), new(DateTimeOffset.UtcNow.AddMilliseconds(1), [])]));
        var observed = steps.Select(step => step.State == RealOperationStepState.OutcomeUnknown
            ? step with { State = RealOperationStepState.Failed, Code = "fake.observed_no_effect_step",
                ResultEvidence = evidence }
            : step).ToArray();
        return new(observed.Any(step => step.State == RealOperationStepState.Verified)
            ? RealOperationState.PartiallyCompleted : RealOperationState.Failed,
            observed, "fake.observed_no_effect", true);
    }

    private static async Task<OperationPlan> PrepareAndAcceptAsync(AgentRealOperationService service,
        RealOperationIntentRequest proposal, TrustedRealSession session)
    {
        var prepared = Assert.IsType<AgentRealOperationResponse>((await service.PrepareAsync(
            new(proposal, Guid.NewGuid(), session.ProductSessionId, CorrelationId.New()), session,
            CancellationToken.None)).Value);
        Assert.True((await service.AcceptAsync(new(prepared.Plan.OperationId, prepared.Plan.PlanHash,
            session.ProductSessionId, CorrelationId.New()), session, CancellationToken.None)).IsSuccess);
        return prepared.Plan;
    }

    private static async Task AwaitDurableStateAsync(OperationPlanRepository repository, OperationId operationId,
        PersistedOperationState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await repository.GetAsync(operationId, timeout.Token))!.State != expected)
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FailingEditObserver : IRealStorageEditObserver
    {
        public Task ObserveAsync(AgentRealOperationResponse response, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("monitor persistence unavailable"));
    }

    [Theory]
    [InlineData(PersistedOperationState.Accepted)]
    [InlineData(PersistedOperationState.Running)]
    public async Task RecoveredUnfinishedWritePreventsArmingNewAppSession(PersistedOperationState interruptedState)
    {
        var path = Path.Combine(Path.GetTempPath(), "WinPool.Agent.RealOperation.Tests",
            Guid.NewGuid().ToString("N"), "winpool.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new WinPoolSqliteStore(path);
        await store.InitializeAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(store, "agent-real-recovery-mode-test");
        var repository = new OperationPlanRepository(store, lease);
        var backend = new RecordingBackend();
        var session = new TrustedRealSession(SessionId.New(), "old-product-session",
            Guid.NewGuid().ToString("D"), 42, DateTimeOffset.UtcNow,
            Path.GetFullPath("WinPool.App.exe"), true);
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "os-disk-unique-id");
        var proposal = new RealOperationIntentRequest(OperationIntent.InitializeDisk, system, [disk],
            [new RealOperationStep("gpt", new InitializeGptCommand(RealTargetReference.ForExisting(disk)),
                [], "RAW", "GPT", "GPT metadata", "fake capability evidence")], "GPT disk");
        var plan = await backend.PrepareAsync(proposal, session, OperationId.New(), CancellationToken.None);
        await repository.PrepareAsync(plan, Guid.NewGuid(), "intent-test");
        await repository.AcceptAsync(plan.OperationId, plan.PlanHash, new string('a', 64), DateTimeOffset.UtcNow);
        if (interruptedState == PersistedOperationState.Running)
            Assert.True(await repository.TransitionAsync(plan.OperationId, PersistedOperationState.Accepted,
                PersistedOperationState.Running, new ExecutionEvent(plan.OperationId,
                    ExecutionEventKind.Started, DateTimeOffset.UtcNow, "test.running", "test")));
        var service = new AgentRealOperationService(repository, new ExecutionEventRepository(store, lease),
            backend, new FixedMachineIdentity(), authority: null, timeProvider: null,
            isSessionStillArmed: _ => true, isAdministrator: () => true);
        await service.InitializeRecoveryAsync();
        var nextSession = session with { ProductSessionId = "new-product-session" };
        ApplicationResult<AgentResponse>? entered = null;
        // Recovery can briefly hold the mutation gate while reconciling fake
        // pending steps. Once released, the durable barrier must still reject.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            entered = await service.EnterModeAsync(new EnterAgentRealModeRequest(
                nextSession.ProductSessionId, CorrelationId.New()), nextSession, CancellationToken.None);
            Assert.False(entered.IsSuccess);
            if (entered.Messages.Single().Code != "agent.real_operation.busy")
                break;
            await Task.Delay(10);
        }
        Assert.Equal("agent.real_operation.write_barrier", Assert.Single(entered!.Messages).Code);
        Assert.True(await repository.HasRealWriteBarrierAsync());
        Assert.Equal(PersistedOperationState.OutcomeUnknown, (await repository.GetAsync(plan.OperationId))!.State);
        Assert.Equal(0, backend.ExecuteCalls);
    }

    [Fact]
    public async Task EnterModeCancelsOldPreparedPlanAndArmsWithoutAnyWindowsCall()
    {
        var path = Path.Combine(Path.GetTempPath(), "WinPool.Agent.RealOperation.Tests",
            Guid.NewGuid().ToString("N"), "winpool.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new WinPoolSqliteStore(path);
        await store.InitializeAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(store, "agent-real-prepared-mode-test");
        var repository = new OperationPlanRepository(store, lease);
        var backend = new RecordingBackend();
        var service = new AgentRealOperationService(repository, new ExecutionEventRepository(store, lease),
            backend, new FixedMachineIdentity(), authority: null, timeProvider: null,
            isSessionStillArmed: _ => true, isAdministrator: () => true);
        await service.InitializeRecoveryAsync();
        var session = new TrustedRealSession(SessionId.New(), "old-product-session",
            Guid.NewGuid().ToString("D"), 42, DateTimeOffset.UtcNow,
            Path.GetFullPath("WinPool.App.exe"), true);
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "os-disk-unique-id");
        var proposal = new RealOperationIntentRequest(OperationIntent.InitializeDisk, system, [disk],
            [new RealOperationStep("gpt", new InitializeGptCommand(RealTargetReference.ForExisting(disk)),
                [], "RAW", "GPT", "GPT metadata", "fake capability evidence")], "GPT disk");
        var prepared = await service.PrepareAsync(new PrepareAgentRealOperationRequest(proposal,
            Guid.NewGuid(), session.ProductSessionId, CorrelationId.New()), session, CancellationToken.None);
        var frozen = Assert.IsType<AgentRealOperationResponse>(prepared.Value);
        var nextSession = session with { ProductSessionId = "new-product-session" };
        var entered = await service.EnterModeAsync(new EnterAgentRealModeRequest(
            nextSession.ProductSessionId, CorrelationId.New()), nextSession, CancellationToken.None);
        Assert.True(Assert.IsType<AgentRealModeResponse>(entered.Value).IsArmed);
        Assert.Equal(PersistedOperationState.Cancelled, (await repository.GetAsync(frozen.Plan.OperationId))!.State);
        Assert.False(await repository.HasRealWriteBarrierAsync());
        Assert.Equal(0, backend.ExecuteCalls);
    }

    [Fact]
    public async Task LiveResizeRangeRequiresArmedVerifiedSessionAndExactPartition()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "WinPool.Agent.RealOperation.Tests", Guid.NewGuid().ToString("N"),
            "winpool.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new WinPoolSqliteStore(path);
        await store.InitializeAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(store, "agent-real-range-test");
        var backend = new RecordingBackend();
        var service = new AgentRealOperationService(
            new OperationPlanRepository(store, lease),
            new ExecutionEventRepository(store, lease), backend,
            new FixedMachineIdentity(), authority: null, timeProvider: null,
            isSessionStillArmed: session => session.IsArmed,
            isAdministrator: () => true);
        await service.InitializeRecoveryAsync();
        var session = new TrustedRealSession(SessionId.New(), "product-session",
            Guid.NewGuid().ToString("D"), 42, DateTimeOffset.UtcNow,
            Path.GetFullPath("WinPool.App.exe"), true);
        var system = SystemId.New();
        var partition = new StorageObjectId(system, StorageObjectKind.Partition,
            "partition-id");
        var request = new QueryAgentRealPartitionResizeRangeRequest(
            partition, session.ProductSessionId, CorrelationId.New());
        var result = await service.QueryPartitionResizeRangeAsync(
            request, session, CancellationToken.None);
        var response = Assert.IsType<AgentRealPartitionResizeRangeResponse>(result.Value);
        Assert.Equal(partition, response.Range.Partition);
        Assert.Equal(1, backend.RangeReadCalls);
        var unarmed = await service.QueryPartitionResizeRangeAsync(
            request with { CorrelationId = CorrelationId.New() },
            session with { IsArmed = false }, CancellationToken.None);
        Assert.False(unarmed.IsSuccess);
        var wrongSession = await service.QueryPartitionResizeRangeAsync(
            request with { ProductSessionId = "other", CorrelationId = CorrelationId.New() },
            session, CancellationToken.None);
        Assert.False(wrongSession.IsSuccess);
        Assert.Equal(1, backend.RangeReadCalls);
    }

    [Fact]
    public async Task StopBeforeFirstWindowsCallPersistsCancelledAndNeverExecutes()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "WinPool.Agent.RealOperation.Tests", Guid.NewGuid().ToString("N"),
            "winpool.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new WinPoolSqliteStore(path);
        await store.InitializeAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(store, "agent-real-stop-test");
        var repository = new OperationPlanRepository(store, lease);
        var backend = new RecordingBackend { PauseRunnerPreflight = true };
        var service = new AgentRealOperationService(
            repository, new ExecutionEventRepository(store, lease), backend, new FixedMachineIdentity(),
            authority: null, timeProvider: null,
            isSessionStillArmed: _ => true,
            isAdministrator: () => true);
        await service.InitializeRecoveryAsync();
        var session = new TrustedRealSession(SessionId.New(), "product-session",
            Guid.NewGuid().ToString("D"), 42, DateTimeOffset.UtcNow,
            Path.GetFullPath("WinPool.App.exe"), true);
        var system = SystemId.New();
        var physical = new StorageObjectId(system, StorageObjectKind.PhysicalDisk,
            "physical-disk-unique-id");
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk,
            "os-disk-unique-id");
        var proposal = new RealOperationIntentRequest(
            OperationIntent.InitializeDisk, system, [physical, disk],
            [new RealOperationStep("gpt",
                new InitializeGptCommand(RealTargetReference.ForExisting(disk)),
                [], "RAW", "GPT", "GPT metadata", "fake capability evidence")],
            "GPT disk");
        var prepared = await service.PrepareAsync(
            new PrepareAgentRealOperationRequest(proposal, Guid.NewGuid(),
                session.ProductSessionId, CorrelationId.New()),
            session, CancellationToken.None);
        var plan = Assert.IsType<AgentRealOperationResponse>(prepared.Value).Plan;
        await service.AcceptAsync(new AcceptAgentRealOperationRequest(
            plan.OperationId, plan.PlanHash, session.ProductSessionId,
            CorrelationId.New()), session, CancellationToken.None);
        await backend.RunnerPreflightEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopFollowingStepsAsync(
            new StopAgentRealOperationFollowingStepsRequest(plan.OperationId,
                plan.PlanHash, session.ProductSessionId, CorrelationId.New()),
            session, CancellationToken.None);
        backend.ReleaseRunnerPreflight.TrySetResult();

        AgentRealOperationResponse? status = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            status = (await service.QueryAsync(new QueryAgentRealOperationRequest(
                plan.OperationId, CorrelationId.New()),
                session, CancellationToken.None)).Value as AgentRealOperationResponse;
            if (status?.State == RealOperationState.Cancelled)
                break;
            await Task.Delay(20);
        }
        Assert.Equal(RealOperationState.Cancelled, status?.State);
        Assert.Equal(0, backend.ExecuteCalls);
        Assert.False(await repository.HasRealWriteBarrierAsync());
        Assert.All(status!.Steps,
            step => Assert.Equal(RealOperationStepState.StoppedBeforeCall, step.State));
        Assert.All(status.Steps,
            step => Assert.Equal("operation.step.skipped", step.Code));
    }

    [Fact]
    public async Task UnknownSecondStepStopsBatchAndKeepsDurableWriteBarrier()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "WinPool.Agent.RealOperation.Tests", Guid.NewGuid().ToString("N"),
            "winpool.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new WinPoolSqliteStore(path);
        await store.InitializeAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(store, "agent-real-unknown-test");
        var repository = new OperationPlanRepository(store, lease);
        var backend = new RecordingBackend { UnknownStepId = "format" };
        var service = new AgentRealOperationService(
            repository, new ExecutionEventRepository(store, lease), backend, new FixedMachineIdentity(),
            authority: null, timeProvider: null,
            isSessionStillArmed: _ => true,
            isAdministrator: () => true);
        await service.InitializeRecoveryAsync();
        var session = new TrustedRealSession(
            SessionId.New(), "product-session", Guid.NewGuid().ToString("D"),
            42, DateTimeOffset.UtcNow, Path.GetFullPath("WinPool.App.exe"), true);
        var system = SystemId.New();
        var disk = new StorageObjectId(
            system, StorageObjectKind.OsDisk, "os-disk-unique-id");
        var proposal = new RealOperationIntentRequest(
            OperationIntent.CreatePartition, system, [disk],
            [
                new RealOperationStep("partition",
                    new CreatePartitionCommand(
                        RealTargetReference.ForExisting(disk),
                        RealPartitionRole.BasicData,
                        1024 * 1024, 1024L * 1024 * 1024),
                    [], "free GPT range", "BasicData partition",
                    "partition table changes", "fake capability evidence"),
                new RealOperationStep("format",
                    new FormatVolumeCommand(
                        RealTargetReference.FromStep(StorageObjectKind.Partition, "partition"),
                        RealFileSystem.Ntfs, 65536, false, "Test"),
                    ["partition"], "new BasicData partition", "quick NTFS volume",
                    "volume format", "fake capability evidence"),
                new RealOperationStep("letter",
                    new SetDriveLetterCommand(
                        RealTargetReference.FromStep(StorageObjectKind.Partition, "partition"),
                        null, 'E'),
                    ["partition", "format"], "new NTFS volume", "volume at E:",
                    "drive-letter assignment", "fake capability evidence")
            ], "GPT BasicData partition with quick NTFS formatting and drive letter");
        var prepared = await service.PrepareAsync(
            new PrepareAgentRealOperationRequest(
                proposal, Guid.NewGuid(), session.ProductSessionId,
                CorrelationId.New()),
            session, CancellationToken.None);
        var plan = Assert.IsType<AgentRealOperationResponse>(prepared.Value).Plan;
        var accepted = await service.AcceptAsync(
            new AcceptAgentRealOperationRequest(
                plan.OperationId, plan.PlanHash, session.ProductSessionId,
                CorrelationId.New()),
            session, CancellationToken.None);
        Assert.NotNull(accepted.Value);

        AgentRealOperationResponse? status = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            status = (await service.QueryAsync(
                new QueryAgentRealOperationRequest(
                    plan.OperationId, CorrelationId.New()),
                session, CancellationToken.None)).Value as AgentRealOperationResponse;
            if (status?.State == RealOperationState.OutcomeUnknown)
            {
                break;
            }
            await Task.Delay(20);
        }

        Assert.Equal(RealOperationState.OutcomeUnknown, status?.State);
        Assert.Equal(plan.OperationId, status!.Plan.OperationId);
        Assert.Equal(2, backend.ExecuteCalls);
        Assert.True(await repository.HasRealWriteBarrierAsync());
        Assert.Equal(3, status.Steps.Count);
        Assert.Equal(RealOperationStepState.Verified, status.Steps[0].State);
        Assert.Equal(RealOperationStepState.OutcomeUnknown, status.Steps[1].State);
        Assert.Equal("fake.unknown", status.Steps[1].Code);
        Assert.Equal(RealOperationStepState.StoppedBeforeCall, status.Steps[2].State);
        Assert.Equal("operation.step.skipped", status.Steps[2].Code);
        Assert.Equal("fake.reconciled", status.Code);

        var queriedAgain = Assert.IsType<AgentRealOperationResponse>(
            (await service.QueryAsync(
                new QueryAgentRealOperationRequest(plan.OperationId, CorrelationId.New()),
                session, CancellationToken.None)).Value);
        Assert.Equal(plan.OperationId, queriedAgain.Plan.OperationId);
        Assert.Equal(RealOperationState.OutcomeUnknown, queriedAgain.State);
        Assert.Equal(2, backend.ExecuteCalls);
        Assert.True(await repository.HasRealWriteBarrierAsync());
    }

    [Fact]
    public async Task RunnerPreflightFailurePersistsBoundedNoCallDiagnosticBeforeAnyExecution()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "WinPool.Agent.RealOperation.Tests", Guid.NewGuid().ToString("N"),
            "winpool.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new WinPoolSqliteStore(path);
        await store.InitializeAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(store, "agent-real-preflight-failure-test");
        var repository = new OperationPlanRepository(store, lease);
        var longMessage = "live EFI preflight diagnostic " + new string('x', 3000);
        var backend = new RecordingBackend
        {
            PreflightFailureStepId = "partition",
            PreflightFailureCall = 2,
            PreflightException = new InvalidDataException(longMessage)
        };
        var service = new AgentRealOperationService(
            repository, new ExecutionEventRepository(store, lease), backend,
            new FixedMachineIdentity(), authority: null, timeProvider: null,
            isSessionStillArmed: _ => true, isAdministrator: () => true);
        await service.InitializeRecoveryAsync();
        var session = new TrustedRealSession(
            SessionId.New(), "product-session", Guid.NewGuid().ToString("D"),
            42, DateTimeOffset.UtcNow, Path.GetFullPath("WinPool.App.exe"), true);
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "os-disk-unique-id");
        var proposal = CreatePartitionFormatLetterProposal(system, disk);
        var prepared = await service.PrepareAsync(
            new PrepareAgentRealOperationRequest(
                proposal, Guid.NewGuid(), session.ProductSessionId, CorrelationId.New()),
            session, CancellationToken.None);
        var plan = Assert.IsType<AgentRealOperationResponse>(prepared.Value).Plan;
        var accepted = await service.AcceptAsync(
            new AcceptAgentRealOperationRequest(
                plan.OperationId, plan.PlanHash, session.ProductSessionId, CorrelationId.New()),
            session, CancellationToken.None);
        Assert.IsType<AgentRealOperationResponse>(accepted.Value);

        AgentRealOperationResponse? status = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            status = (await service.QueryAsync(
                new QueryAgentRealOperationRequest(plan.OperationId, CorrelationId.New()),
                session, CancellationToken.None)).Value as AgentRealOperationResponse;
            if (status?.State == RealOperationState.Failed)
                break;
            await Task.Delay(20);
        }

        Assert.Equal(RealOperationState.Failed, status?.State);
        Assert.Equal(0, backend.ExecuteCalls);
        Assert.Equal(2, backend.PreflightCalls);
        Assert.False(await repository.HasRealWriteBarrierAsync());
        var persistedSteps = await repository.GetStepsAsync(plan.OperationId);
        Assert.Equal(3, persistedSteps.Count);
        Assert.Equal(PersistedOperationStepState.Failed, persistedSteps[0].State);
        Assert.Equal(PersistedOperationStepState.Skipped, persistedSteps[1].State);
        Assert.Equal(PersistedOperationStepState.Skipped, persistedSteps[2].State);

        var evidence = JsonSerializer.Deserialize<WindowsNoEffectStepEvidence>(
            Assert.IsType<string>(persistedSteps[0].EvidenceJson));
        Assert.NotNull(evidence);
        Assert.True(evidence.NoWindowsCall);
        Assert.Equal("operation.preflight_failed", evidence.Code);
        Assert.Equal(plan.RealOperation!.PhysicalMemberFingerprint,
            evidence.PhysicalMemberFingerprint);
        Assert.NotNull(evidence.Diagnostic);
        Assert.Equal(2048, evidence.Diagnostic.Length);
        Assert.Contains(nameof(InvalidDataException), evidence.Diagnostic);
        Assert.Contains("live EFI preflight diagnostic", evidence.Diagnostic);
    }

    [Fact]
    public async Task PreflightFailureAfterVerifiedPrefixStillReconcilesAsPartial()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "WinPool.Agent.RealOperation.Tests", Guid.NewGuid().ToString("N"),
            "winpool.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new WinPoolSqliteStore(path);
        await store.InitializeAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(store, "agent-real-preflight-partial-test");
        var repository = new OperationPlanRepository(store, lease);
        var backend = new RecordingBackend
        {
            PreflightFailureStepId = "format",
            PreflightFailureCall = 3,
            PreflightException = new InvalidOperationException("volume preflight changed")
        };
        var service = new AgentRealOperationService(
            repository, new ExecutionEventRepository(store, lease), backend,
            new FixedMachineIdentity(), authority: null, timeProvider: null,
            isSessionStillArmed: _ => true, isAdministrator: () => true);
        await service.InitializeRecoveryAsync();
        var session = new TrustedRealSession(
            SessionId.New(), "product-session", Guid.NewGuid().ToString("D"),
            42, DateTimeOffset.UtcNow, Path.GetFullPath("WinPool.App.exe"), true);
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "os-disk-unique-id");
        var proposal = CreatePartitionFormatLetterProposal(system, disk);
        var prepared = await service.PrepareAsync(
            new PrepareAgentRealOperationRequest(
                proposal, Guid.NewGuid(), session.ProductSessionId, CorrelationId.New()),
            session, CancellationToken.None);
        var plan = Assert.IsType<AgentRealOperationResponse>(prepared.Value).Plan;
        var accepted = await service.AcceptAsync(
            new AcceptAgentRealOperationRequest(
                plan.OperationId, plan.PlanHash, session.ProductSessionId, CorrelationId.New()),
            session, CancellationToken.None);
        Assert.IsType<AgentRealOperationResponse>(accepted.Value);

        AgentRealOperationResponse? status = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            status = (await service.QueryAsync(
                new QueryAgentRealOperationRequest(plan.OperationId, CorrelationId.New()),
                session, CancellationToken.None)).Value as AgentRealOperationResponse;
            if (status?.State == RealOperationState.PartiallyCompleted)
                break;
            await Task.Delay(20);
        }

        Assert.Equal(RealOperationState.PartiallyCompleted, status?.State);
        Assert.Equal(1, backend.ExecuteCalls);
        Assert.Equal(3, backend.PreflightCalls);
        Assert.False(await repository.HasRealWriteBarrierAsync());
        var persistedSteps = await repository.GetStepsAsync(plan.OperationId);
        Assert.Equal(PersistedOperationStepState.Verified, persistedSteps[0].State);
        Assert.Equal(PersistedOperationStepState.Failed, persistedSteps[1].State);
        Assert.Equal(PersistedOperationStepState.Skipped, persistedSteps[2].State);
        var evidence = JsonSerializer.Deserialize<WindowsNoEffectStepEvidence>(
            Assert.IsType<string>(persistedSteps[1].EvidenceJson));
        Assert.NotNull(evidence);
        Assert.True(evidence.NoWindowsCall);
        Assert.Equal("operation.preflight_failed", evidence.Code);
        Assert.Equal(plan.RealOperation!.PhysicalMemberFingerprint,
            evidence.PhysicalMemberFingerprint);
    }

    [Fact]
    public async Task AcceptedPlanRunsOnceAndDuplicateAcceptReturnsPersistedStatus()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "WinPool.Agent.RealOperation.Tests", Guid.NewGuid().ToString("N"),
            "winpool.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new WinPoolSqliteStore(path);
        await store.InitializeAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(store, "agent-real-test");
        var repository = new OperationPlanRepository(store, lease);
        var backend = new RecordingBackend();
        var service = new AgentRealOperationService(
            repository, new ExecutionEventRepository(store, lease), backend, new FixedMachineIdentity(),
            authority: null, timeProvider: null,
            isSessionStillArmed: _ => true,
            isAdministrator: () => true);
        await service.InitializeRecoveryAsync();
        var session = new TrustedRealSession(
            SessionId.New(), "product-session", Guid.NewGuid().ToString("D"),
            42, DateTimeOffset.UtcNow, Path.GetFullPath("WinPool.App.exe"), true);
        var system = SystemId.New();
        var disk = new StorageObjectId(system, StorageObjectKind.PhysicalDisk,
            "physical-disk-unique-id");
        var osDisk = new StorageObjectId(system, StorageObjectKind.OsDisk,
            "os-disk-unique-id");
        var proposal = new RealOperationIntentRequest(
            OperationIntent.InitializeDisk, system, [disk, osDisk],
            [new RealOperationStep(
                "initialize-gpt",
                new InitializeGptCommand(RealTargetReference.ForExisting(osDisk)),
                [], "RAW with no partition", "GPT disk", "GPT metadata changes",
                "fake capability evidence")],
            "GPT disk with no partition");
        var preparationId = Guid.NewGuid();

        var prepared = await service.PrepareAsync(
            new PrepareAgentRealOperationRequest(
                proposal, preparationId, session.ProductSessionId, CorrelationId.New()),
            session, CancellationToken.None);
        var preparedResponse = Assert.IsType<AgentRealOperationResponse>(prepared.Value);
        Assert.Equal(RealOperationState.Prepared, preparedResponse.State);
        Assert.Equal(0, backend.ExecuteCalls);

        var acceptedRequest = new AcceptAgentRealOperationRequest(
            preparedResponse.Plan.OperationId, preparedResponse.Plan.PlanHash,
            session.ProductSessionId, CorrelationId.New());
        var accepted = await service.AcceptAsync(
            acceptedRequest, session, CancellationToken.None);
        Assert.IsType<AgentRealOperationResponse>(accepted.Value);

        AgentRealOperationResponse? status = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            status = (await service.QueryAsync(
                new QueryAgentRealOperationRequest(
                    preparedResponse.Plan.OperationId, CorrelationId.New()),
                session, CancellationToken.None)).Value as AgentRealOperationResponse;
            if (status?.State == RealOperationState.Succeeded)
            {
                break;
            }
            await Task.Delay(20);
        }
        Assert.Equal(RealOperationState.Succeeded, status?.State);
        Assert.Equal(1, backend.ExecuteCalls);
        Assert.False(await repository.HasRealWriteBarrierAsync());

        var repeated = await service.AcceptAsync(
            acceptedRequest with { CorrelationId = CorrelationId.New() },
            session with { IsArmed = false }, CancellationToken.None);
        Assert.Equal(RealOperationState.Succeeded,
            Assert.IsType<AgentRealOperationResponse>(repeated.Value).State);
        Assert.Equal(1, backend.ExecuteCalls);

        var repeatedPrepare = await service.PrepareAsync(
            new PrepareAgentRealOperationRequest(
                proposal, preparationId, session.ProductSessionId, CorrelationId.New()),
            session, CancellationToken.None);
        Assert.Equal(preparedResponse.Plan.OperationId,
            Assert.IsType<AgentRealOperationResponse>(repeatedPrepare.Value).Plan.OperationId);
    }

    [Fact]
    public async Task UnsupportedPreparationReturnsSerializableRejectionAndAllowsFreshPreparationWithoutDispatch()
    {
        const string diagnostic = "The current Windows SKU has no verified ReFS creation applicability.";
        var backend = new RecordingBackend { PreparationException = new NotSupportedException(diagnostic) };
        var clock = TimeProvider.System;
        var authority = new ObservedAuthority(clock);
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, clock, authority);
        await using var lease = fixture.Lease;
        var preparationId = Guid.NewGuid();
        var correlation = CorrelationId.New();
        var request = new PrepareAgentRealOperationRequest(fixture.Proposal, preparationId,
            fixture.Session.ProductSessionId, correlation);

        var rejected = await fixture.Service.PrepareAsync(request, fixture.Session, CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, rejected.Status);
        Assert.Equal(correlation, rejected.CorrelationId);
        Assert.Null(rejected.Value);
        var message = Assert.Single(rejected.Messages);
        Assert.Equal("agent.real_operation.prepare_failed", message.Code);
        Assert.Equal("NotSupportedException: " + diagnostic, message.DiagnosticText);
        var wire = new AgentControlProtocolCodec().EncodeResponse(rejected, clock.GetUtcNow());
        var payload = wire.Payload.Deserialize<AgentControlResponsePayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(ApplicationStatus.Rejected, payload.Status);
        Assert.Equal(correlation, payload.CorrelationId);
        var wireMessage = Assert.Single(payload.Messages);
        Assert.Equal(message.Code, wireMessage.Code);
        Assert.Equal(message.DiagnosticText, wireMessage.DiagnosticText);
        Assert.Null(payload.Response);
        Assert.Null(await fixture.Plans.GetByPreparationIdAsync(preparationId));
        Assert.Empty(await fixture.Plans.ListUnfinishedAsync());
        Assert.False(await fixture.Plans.HasRealWriteBarrierAsync());
        Assert.Equal(0, backend.PreflightCalls);
        Assert.Equal(0, backend.ExecuteCalls);
        Assert.Equal(0, authority.ConsumeCalls);

        backend.PreparationException = null;
        var prepared = await fixture.Service.PrepareAsync(request with { CorrelationId = CorrelationId.New() },
            fixture.Session, CancellationToken.None);
        Assert.True(prepared.IsSuccess);
        var response = Assert.IsType<AgentRealOperationResponse>(prepared.Value);
        Assert.Equal(RealOperationState.Prepared, response.State);
        Assert.NotNull(await fixture.Plans.GetByPreparationIdAsync(preparationId));
        Assert.DoesNotContain(await fixture.Events.ListAsync(response.Plan.OperationId),
            item => item.Event.Code is "operation.accepted" or "operation.step.call_issued");
        Assert.Equal(0, backend.PreflightCalls);
        Assert.Equal(0, backend.ExecuteCalls);
        Assert.Equal(0, authority.ConsumeCalls);
    }

    [Fact]
    public async Task ConsumedAuthorizationExpiryDuringAwaitedProviderCallDoesNotAbortAcceptedOperation()
    {
        var clock = new AdvancingTimeProvider(DateTimeOffset.UtcNow);
        var authority = new ObservedAuthority(clock);
        var backend = new RecordingBackend { Clock = clock, PauseExecution = true };
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, clock, authority);
        await using var lease = fixture.Lease;
        try
        {
            var prepared = await fixture.Service.PrepareAsync(new PrepareAgentRealOperationRequest(
                fixture.Proposal, Guid.NewGuid(), fixture.Session.ProductSessionId, CorrelationId.New()),
                fixture.Session, CancellationToken.None);
            var plan = Assert.IsType<AgentRealOperationResponse>(prepared.Value).Plan;
            var accepted = await fixture.Service.AcceptAsync(new AcceptAgentRealOperationRequest(
                plan.OperationId, plan.PlanHash, fixture.Session.ProductSessionId, CorrelationId.New()),
                fixture.Session, CancellationToken.None);
            Assert.Equal(plan.OperationId, Assert.IsType<AgentRealOperationResponse>(accepted.Value).Plan.OperationId);
            await backend.ExecutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(authority.LastConsumption!.IsValid);
            Assert.Equal(1, authority.ConsumeCalls);
            Assert.Equal(1, backend.ExecuteCalls);
            Assert.Equal(PersistedOperationState.Running, (await fixture.Plans.GetAsync(plan.OperationId))!.State);
            Assert.Single(await fixture.Plans.GetStepsAsync(plan.OperationId), step => step.State == PersistedOperationStepState.CallIssued);

            clock.Advance(InMemoryOperationAuthority.MaximumLifetime + TimeSpan.FromMinutes(1));
            Assert.True(clock.GetUtcNow() > authority.LastToken!.ExpiresAt);
            Assert.True(clock.GetUtcNow() > plan.RealOperation!.ExpiresAt);
            backend.ReleaseExecution.TrySetResult();
            var completed = await AwaitSucceededAsync(fixture.Service, plan.OperationId, fixture.Session);

            Assert.Equal(plan.PlanHash, completed.Plan.PlanHash);
            Assert.Equal(RealOperationStepState.Verified, Assert.Single(completed.Steps).State);
            Assert.False(await fixture.Plans.HasRealWriteBarrierAsync());
            Assert.Equal(1, backend.ExecuteCalls);
            Assert.Equal(1, authority.ConsumeCalls);
            var history = await fixture.Events.ListAsync(plan.OperationId);
            Assert.Single(history, item => item.Event.Code == "operation.accepted");
            Assert.Single(history, item => item.Event.Code == "operation.step.call_issued");
            Assert.DoesNotContain(history, item => item.Event.Code.Contains("expired", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            backend.ReleaseExecution.TrySetResult();
        }
    }

    [Fact]
    public async Task OverlappingAcceptRequestsKeepOneAcceptanceAndExecutionAndRejectDifferentHash()
    {
        var clock = new AdvancingTimeProvider(DateTimeOffset.UtcNow);
        var authority = new ObservedAuthority(clock);
        var backend = new RecordingBackend { Clock = clock, PauseAcceptPreflight = true, PauseExecution = true };
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, clock, authority);
        await using var lease = fixture.Lease;
        try
        {
            var prepared = await fixture.Service.PrepareAsync(new PrepareAgentRealOperationRequest(
                fixture.Proposal, Guid.NewGuid(), fixture.Session.ProductSessionId, CorrelationId.New()),
                fixture.Session, CancellationToken.None);
            var plan = Assert.IsType<AgentRealOperationResponse>(prepared.Value).Plan;
            var request = new AcceptAgentRealOperationRequest(plan.OperationId, plan.PlanHash,
                fixture.Session.ProductSessionId, CorrelationId.New());
            var first = fixture.Service.AcceptAsync(request, fixture.Session, CancellationToken.None);
            await backend.AcceptPreflightEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);

            // Start the second request while the first is inside its authoritative
            // preflight. Before durable acceptance, the defined reply is busy.
            var second = fixture.Service.AcceptAsync(request with { CorrelationId = CorrelationId.New() },
                fixture.Session, CancellationToken.None);
            var overlapping = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
            Assert.Equal(ApplicationStatus.Rejected, overlapping.Status);
            Assert.Equal("agent.real_operation.busy", Assert.Single(overlapping.Messages).Code);
            Assert.Equal(PersistedOperationState.Prepared, (await fixture.Plans.GetAsync(plan.OperationId))!.State);
            Assert.Equal(0, authority.ConsumeCalls);
            Assert.Equal(0, backend.ExecuteCalls);
            Assert.DoesNotContain(await fixture.Events.ListAsync(plan.OperationId), item => item.Event.Code == "operation.accepted");

            backend.ReleaseAcceptPreflight.TrySetResult();
            var firstReply = Assert.IsType<AgentRealOperationResponse>((await first.WaitAsync(TimeSpan.FromSeconds(5))).Value);
            await backend.ExecutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var duplicate = await fixture.Service.AcceptAsync(request with { CorrelationId = CorrelationId.New() },
                fixture.Session, CancellationToken.None);
            var duplicateReply = Assert.IsType<AgentRealOperationResponse>(duplicate.Value);
            Assert.Equal(firstReply.Plan.OperationId, duplicateReply.Plan.OperationId);
            Assert.Equal(firstReply.Plan.PlanHash, duplicateReply.Plan.PlanHash);
            Assert.Equal(RealOperationState.Running, duplicateReply.State);
            Assert.Equal(1, authority.ConsumeCalls);
            Assert.Equal(1, backend.ExecuteCalls);

            var differentHash = (plan.PlanHash[0] == '0' ? "1" : "0") + plan.PlanHash[1..];
            var conflict = await fixture.Service.AcceptAsync(request with { PlanHash = differentHash, CorrelationId = CorrelationId.New() },
                fixture.Session, CancellationToken.None);
            Assert.Equal(ApplicationStatus.Rejected, conflict.Status);
            Assert.Equal("agent.real_operation.plan_mismatch", Assert.Single(conflict.Messages).Code);
            backend.ReleaseExecution.TrySetResult();
            var completed = await AwaitSucceededAsync(fixture.Service, plan.OperationId, fixture.Session);
            var terminalDuplicate = Assert.IsType<AgentRealOperationResponse>((await fixture.Service.AcceptAsync(
                request with { CorrelationId = CorrelationId.New() }, fixture.Session with { IsArmed = false }, CancellationToken.None)).Value);
            Assert.Equal(completed.State, terminalDuplicate.State);
            Assert.Equal(completed.Plan.OperationId, terminalDuplicate.Plan.OperationId);
            Assert.Equal(completed.Plan.PlanHash, terminalDuplicate.Plan.PlanHash);
            Assert.Equal(1, authority.ConsumeCalls);
            Assert.Equal(1, backend.ExecuteCalls);
            var history = await fixture.Events.ListAsync(plan.OperationId);
            Assert.Single(history, item => item.Event.Code == "operation.accepted");
            Assert.Single(history, item => item.Event.Code == "operation.step.call_issued");
            Assert.False(await fixture.Plans.HasRealWriteBarrierAsync());
        }
        finally
        {
            backend.ReleaseAcceptPreflight.TrySetResult();
            backend.ReleaseExecution.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaximumMacroUsesOneAcceptedPlanAndDurableAttemptsIncludingBoundaryFailure(bool descend)
    {
        var backend = new RecordingBackend { MaximumSearch = async (plan, step, journal) =>
        {
            Assert.True(await journal.PrepareAsync(MacroAttempt(1, 4, 0), default));
            Assert.True(await journal.MarkCallIssuedAsync(1, default));
            Assert.True(await journal.CompleteAsync(1, new(descend ? MaximumCapacityAttemptState.CapacityRejectedUnchanged
                : MaximumCapacityAttemptState.Verified, descend ? 0 : 4 * MaximumCapacityAlgorithm.GiB,
                "first_observation", "{\"strictProof\":true}"), default));
            Assert.True(await journal.PrepareAsync(descend ? MacroAttempt(2, 3, 0) : MacroAttempt(2, 5, 4), default));
            Assert.True(await journal.MarkCallIssuedAsync(2, default));
            Assert.True(await journal.CompleteAsync(2, new(descend ? MaximumCapacityAttemptState.Verified
                : MaximumCapacityAttemptState.CapacityRejectedUnchanged, (descend ? 3 : 4) * MaximumCapacityAlgorithm.GiB,
                "search_boundary", "{\"strictProof\":true}"), default));
            return new(RealStepOutcome.Verified, "maximum_found",
                JsonSerializer.Serialize(new { maximumGiB = descend ? 3 : 4 }), "actual-vd-id");
        }};
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var proposal = MacroProposal(fixture.Proposal);
        var prepared = Assert.IsType<AgentRealOperationResponse>((await fixture.Service.PrepareAsync(
            new(proposal, Guid.NewGuid(), fixture.Session.ProductSessionId, CorrelationId.New()),
            fixture.Session, default)).Value);
        Assert.True((await fixture.Service.AcceptAsync(new(prepared.Plan.OperationId, prepared.Plan.PlanHash,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default)).IsSuccess);
        await AwaitSucceededAsync(fixture.Service, prepared.Plan.OperationId, fixture.Session);
        var attempts = await fixture.Plans.ReadMaximumCapacityAttemptsAsync(prepared.Plan.OperationId, "maximum");
        Assert.Equal(2, attempts.Count);
        Assert.Equal(descend ? MaximumCapacityAttemptState.Verified
            : MaximumCapacityAttemptState.CapacityRejectedUnchanged, attempts[1].State);
        Assert.Equal(1, backend.MaximumSearchCalls);
        Assert.Equal(0, backend.ExecuteCalls);
        Assert.Equal(PersistedOperationStepState.Verified, Assert.Single(await fixture.Plans.GetStepsAsync(prepared.Plan.OperationId)).State);
        var history = await fixture.Events.ListAsync(prepared.Plan.OperationId);
        Assert.Equal(2, history.Count(e => e.Event.Code == "operation.maximum.attempt_call_issued"));
        Assert.Single(history, e => e.Event.Code == "operation.accepted");
    }

    [Theory]
    [InlineData("cas")]
    [InlineData("crash")]
    [InlineData("missing-boundary")]
    public async Task RejectedAttemptCasOrCrashCannotProduceMaximumAndRestartOnlyReads(string scenario)
    {
        var backend = new RecordingBackend { MaximumSearch = async (plan, step, journal) =>
        {
            Assert.True(await journal.PrepareAsync(MacroAttempt(1, 4, 0), default));
            Assert.True(await journal.MarkCallIssuedAsync(1, default));
            if (scenario == "crash") throw new IOException("crash after issued, before receipt");
            Assert.True(await journal.CompleteAsync(1, new(MaximumCapacityAttemptState.Verified,
                4 * MaximumCapacityAlgorithm.GiB, "verified", "proof"), default));
            if (scenario == "cas") Assert.False(await journal.MarkCallIssuedAsync(1, default));
            return new(RealStepOutcome.Verified, "untrusted_success_after_cas_rejected", "proof");
        }};
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var prepared = Assert.IsType<AgentRealOperationResponse>((await fixture.Service.PrepareAsync(
            new(MacroProposal(fixture.Proposal), Guid.NewGuid(), fixture.Session.ProductSessionId, CorrelationId.New()),
            fixture.Session, default)).Value);
        await fixture.Service.AcceptAsync(new(prepared.Plan.OperationId, prepared.Plan.PlanHash,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default);
        await AwaitDurableStateAsync(fixture.Plans, prepared.Plan.OperationId, PersistedOperationState.OutcomeUnknown);
        await fixture.Service.InitializeRecoveryAsync();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (backend.MaximumRecoveryCalls < 1)
            {
                await fixture.Service.QueryAsync(new(prepared.Plan.OperationId, CorrelationId.New()), fixture.Session, timeout.Token);
                await Task.Delay(10, timeout.Token);
            }
        Assert.Equal(1, backend.MaximumSearchCalls);
        Assert.Equal(0, backend.ExecuteCalls);
        Assert.True(await fixture.Plans.HasRealWriteBarrierAsync());
        Assert.Equal(scenario == "crash" ? MaximumCapacityAttemptState.CallIssued : MaximumCapacityAttemptState.Verified,
            Assert.Single(await fixture.Plans.ReadMaximumCapacityAttemptsAsync(prepared.Plan.OperationId, "maximum")).State);
    }

    [Fact]
    public async Task MultiTierMacroPersistsOneSeedAndBothOrderedSearchBoundaries()
    {
        var backend = new RecordingBackend { MaximumSearch = async (plan, step, journal) =>
        {
            Assert.True(await journal.PrepareAsync(MacroAttempt(1, 5, 0) with
                { SearchTargetKey = "seed", Phase = MaximumCapacityAttemptPhase.Seed }, default));
            Assert.True(await journal.MarkCallIssuedAsync(1, default));
            Assert.True(await journal.CompleteAsync(1, new(MaximumCapacityAttemptState.Verified,
                5 * MaximumCapacityAlgorithm.GiB, "seed_verified", "all_layers_exact", new Dictionary<string,long>
                    { ["template-first"] = 2 * MaximumCapacityAlgorithm.GiB,
                      ["template-second"] = 3 * MaximumCapacityAlgorithm.GiB }), default));
            var ordinal = 1;
            foreach (var (key, candidate, seed) in new[] { ("template-first", 4L, 2L), ("template-second", 6L, 3L) })
            {
                Assert.True(await journal.PrepareAsync(MacroAttempt(++ordinal, candidate, seed) with
                    { SearchTargetKey = key }, default));
                Assert.True(await journal.MarkCallIssuedAsync(ordinal, default));
                Assert.True(await journal.CompleteAsync(ordinal, new(MaximumCapacityAttemptState.Verified,
                    candidate * MaximumCapacityAlgorithm.GiB, "layer_verified", "proof"), default));
                Assert.True(await journal.PrepareAsync(MacroAttempt(++ordinal, candidate + 1, candidate) with
                    { SearchTargetKey = key }, default));
                Assert.True(await journal.MarkCallIssuedAsync(ordinal, default));
                Assert.True(await journal.CompleteAsync(ordinal, new(MaximumCapacityAttemptState.CapacityRejectedUnchanged,
                    candidate * MaximumCapacityAlgorithm.GiB, "capacity_unchanged", "proof"), default));
            }
            return new(RealStepOutcome.Verified, "maximum_found", "{\"twoLayersMaximum\":true}", "actual-vd-id");
        }};
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var ordinary = MacroProposal(fixture.Proposal);
        var pool = RealTargetReference.ForExisting(ordinary.Targets[1]);
        var first = RealTargetReference.ForExisting(new(ordinary.SystemId, StorageObjectKind.StorageTier, "template-first"));
        var second = RealTargetReference.ForExisting(new(ordinary.SystemId, StorageObjectKind.StorageTier, "template-second"));
        var firstPolicy = ((CreateVirtualDiskCommand)ordinary.Steps[0].Command).MaximumCapacity!;
        var upper = 6 * MaximumCapacityAlgorithm.GiB + MaximumCapacityAlgorithm.ReserveBytes + 1;
        var secondPolicy = firstPolicy with { UpperBoundBytes = upper,
            InitialCandidateBytes = MaximumCapacityAlgorithm.InitialCandidateBytes(upper) };
        var proposal = ordinary with { Targets = [.. ordinary.Targets, first.Existing!.Value, second.Existing!.Value],
            Steps = [ordinary.Steps[0] with { Command = new CreateTieredVirtualDiskCommand(pool, first,
                "multi", 0, true, MaximumCapacity: firstPolicy,
                CapacityTiers: [new(first, firstPolicy), new(second, secondPolicy)]) }] };
        var prepared = Assert.IsType<AgentRealOperationResponse>((await fixture.Service.PrepareAsync(
            new(proposal, Guid.NewGuid(), fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default)).Value);
        Assert.True((await fixture.Service.AcceptAsync(new(prepared.Plan.OperationId, prepared.Plan.PlanHash,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default)).IsSuccess);
        await AwaitSucceededAsync(fixture.Service, prepared.Plan.OperationId, fixture.Session);
        Assert.Equal(5, (await fixture.Plans.ReadMaximumCapacityAttemptsAsync(prepared.Plan.OperationId, "maximum")).Count);
        Assert.Equal(1, backend.MaximumSearchCalls);
        Assert.Equal(0, backend.ExecuteCalls);
    }

    private static MaximumCapacityAttempt MacroAttempt(int ordinal, long candidateGiB, long lastGiB) =>
        new(ordinal, "virtual-disk", lastGiB == 0 ? MaximumCapacityAttemptPhase.Create : MaximumCapacityAttemptPhase.Resize,
            candidateGiB * MaximumCapacityAlgorithm.GiB, lastGiB * MaximumCapacityAlgorithm.GiB,
            "{\"exactTarget\":true}", "before-fingerprint", "physical-members-v1", "{\"fresh\":true}");

    private static RealOperationIntentRequest MacroProposal(RealOperationIntentRequest baseline)
    {
        var pool = new StorageObjectId(baseline.SystemId, StorageObjectKind.StoragePool, "exact-pool");
        var upper = 4 * MaximumCapacityAlgorithm.GiB + MaximumCapacityAlgorithm.ReserveBytes + 1;
        var policy = new MaximumCapacityPolicy(MaximumCapacityAlgorithm.Version, upper,
            MaximumCapacityAlgorithm.InitialCandidateBytes(upper), 10 * MaximumCapacityAlgorithm.GiB,
            20, "fresh-provider", "source-fingerprint", DateTimeOffset.UtcNow);
        return new(OperationIntent.CreateVirtualDisk, baseline.SystemId, [baseline.Targets[0], pool],
            [new("maximum", new CreateVirtualDiskCommand(RealTargetReference.ForExisting(pool),
                "maximum", 0, 65536, 1, true, policy), [], "exact empty pool", "verified maximum",
                "capacity search writes", "frozen bounded policy")], "maximum virtual disk");
    }

    private static async Task<(AgentRealOperationService Service, OperationPlanRepository Plans,
        ExecutionEventRepository Events, TrustedRealSession Session, RealOperationIntentRequest Proposal,
        AgentWriteOwnerLease Lease)> CreateAcceptedLifecycleFixtureAsync(RecordingBackend backend,
        TimeProvider clock, IOperationAuthority authority)
    {
        var path = Path.Combine(Path.GetTempPath(), "WinPool.Agent.RealOperation.Tests", Guid.NewGuid().ToString("N"), "winpool.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new WinPoolSqliteStore(path);
        await store.InitializeAsync();
        var lease = AgentWriteOwnerLease.Acquire(store, "agent-real-lifecycle-test");
        var plans = new OperationPlanRepository(store, lease);
        var events = new ExecutionEventRepository(store, lease);
        var service = new AgentRealOperationService(plans, events, backend, new FixedMachineIdentity(),
            authority, clock, _ => true, () => true);
        await service.InitializeRecoveryAsync();
        var session = new TrustedRealSession(SessionId.New(), "product-session", Guid.NewGuid().ToString("D"),
            42, clock.GetUtcNow(), Path.GetFullPath("WinPool.App.exe"), true);
        var system = SystemId.New();
        var physical = new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "physical-disk-unique-id");
        var disk = new StorageObjectId(system, StorageObjectKind.OsDisk, "os-disk-unique-id");
        var proposal = new RealOperationIntentRequest(OperationIntent.InitializeDisk, system, [physical, disk],
            [new RealOperationStep("initialize-gpt", new InitializeGptCommand(RealTargetReference.ForExisting(disk)),
                [], "Exact RAW disk", "GPT initialized", "Metadata changed", "fake positive capability")], "Exact GPT disk");
        return (service, plans, events, session, proposal, lease);
    }

    private static async Task<AgentRealOperationResponse> AwaitSucceededAsync(AgentRealOperationService service,
        OperationId operationId, TrustedRealSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var status = Assert.IsType<AgentRealOperationResponse>((await service.QueryAsync(
                new QueryAgentRealOperationRequest(operationId, CorrelationId.New()), session, timeout.Token)).Value);
            if (status.State == RealOperationState.Succeeded) return status;
            Assert.Contains(status.State, new[] { RealOperationState.Accepted, RealOperationState.Running });
            await Task.Yield();
        }
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private long elapsedTicks;
        public override DateTimeOffset GetUtcNow() => initial.AddTicks(Interlocked.Read(ref elapsedTicks));
        public void Advance(TimeSpan duration) => Interlocked.Add(ref elapsedTicks, duration.Ticks);
    }

    private sealed class ObservedAuthority(TimeProvider clock) : IOperationAuthority
    {
        private readonly InMemoryOperationAuthority inner = new(new OperationPolicyEvaluator(), clock);
        private int consumeCalls;
        public int ConsumeCalls => Volatile.Read(ref consumeCalls);
        public OperationAuthorizationToken? LastToken { get; private set; }
        public AuthorizationValidationResult? LastConsumption { get; private set; }
        public Task<AuthorizationIssueResult> AuthorizeAsync(OperationPlan plan, WinPool.Execution.ExecutionContext context,
            bool userConfirmed, CancellationToken cancellationToken) => inner.AuthorizeAsync(plan, context, userConfirmed, cancellationToken);
        public async Task<AuthorizationIssueResult> AuthorizeConfirmedRealAsync(OperationPlan plan,
            WinPool.Execution.ExecutionContext context, string confirmedPlanHash, CancellationToken cancellationToken)
        {
            var result = await inner.AuthorizeConfirmedRealAsync(plan, context, confirmedPlanHash, cancellationToken);
            LastToken = result.Token;
            return result;
        }
        public AuthorizationValidationResult Consume(OperationAuthorizationToken token, OperationPlan plan,
            WinPool.Execution.ExecutionContext context)
        {
            Interlocked.Increment(ref consumeCalls);
            LastConsumption = inner.Consume(token, plan, context);
            return LastConsumption;
        }
    }

    private static RealOperationIntentRequest CreatePartitionFormatLetterProposal(
        SystemId system,
        StorageObjectId disk) => new(
        OperationIntent.CreatePartition, system, [disk],
        [
            new RealOperationStep("partition",
                new CreatePartitionCommand(
                    RealTargetReference.ForExisting(disk), RealPartitionRole.BasicData,
                    1024 * 1024, 1024L * 1024 * 1024),
                [], "free GPT range", "BasicData partition",
                "partition table changes", "fake capability evidence"),
            new RealOperationStep("format",
                new FormatVolumeCommand(
                    RealTargetReference.FromStep(StorageObjectKind.Partition, "partition"),
                    RealFileSystem.Ntfs, 65536, false, "Test"),
                ["partition"], "new BasicData partition", "quick NTFS volume",
                "volume format", "fake capability evidence"),
            new RealOperationStep("letter",
                new SetDriveLetterCommand(
                    RealTargetReference.FromStep(StorageObjectKind.Partition, "partition"),
                    null, 'E'),
                ["partition", "format"], "new NTFS volume", "volume at E:",
                "drive-letter assignment", "fake capability evidence")
        ], "GPT BasicData partition with quick NTFS formatting and drive letter");

    private sealed class FixedMachineIdentity : IRealMachineIdentityProvider
    {
        public Task<string> ReadBindingAsync(CancellationToken cancellationToken) =>
            Task.FromResult("machine-binding-test");
    }

    private sealed class RecordingBackend : IRealStorageBackend, IMaximumCapacitySearchBackend
    {
        public Func<OperationPlan, RealOperationStep, IMaximumCapacityAttemptJournal, Task<RealStepResult>>? MaximumSearch { get; init; }
        public int MaximumSearchCalls { get; private set; }
        public int MaximumRecoveryCalls { get; private set; }
        public async Task<RealStepResult> ExecuteMaximumCapacitySearchAsync(OperationPlan plan, RealOperationStep step,
            RealStepPreflight preflight, IMaximumCapacityAttemptJournal journal, CancellationToken cancellationToken)
        {
            MaximumSearchCalls++;
            return await MaximumSearch!(plan, step, journal);
        }
        public Task<RealReconciliationResult> ReconcileMaximumCapacitySearchAsync(OperationPlan plan,
            RealOperationStep step, RealOperationStepProgress parent, IReadOnlyList<MaximumCapacityAttemptRecord> attempts,
            CancellationToken cancellationToken)
        {
            MaximumRecoveryCalls++;
            var success = parent.State == RealOperationStepState.Verified
                && attempts.Count > 0 && attempts.All(a => a.State is MaximumCapacityAttemptState.Verified
                    or MaximumCapacityAttemptState.CapacityRejectedUnchanged);
            return Task.FromResult(new RealReconciliationResult(success ? RealOperationState.Succeeded
                : RealOperationState.OutcomeUnknown, [parent], "fake.maximum.readonly_reconcile", success));
        }

        public Task<RealVirtualDiskCreationRange> ReadVirtualDiskCreationRangeAsync(StorageObjectId target,
            TrustedRealSession session, CancellationToken cancellationToken) => Task.FromResult(
                new RealVirtualDiskCreationRange(target, 1048576, 1073741824, 1048576, 0, [], "fresh-fingerprint", DateTimeOffset.UtcNow));
        private int executeCalls;
        private int preflightCalls;
        private int rangeReadCalls;
        public int ExecuteCalls => Volatile.Read(ref executeCalls);
        public int PreflightCalls => Volatile.Read(ref preflightCalls);
        public int RangeReadCalls => Volatile.Read(ref rangeReadCalls);
        public string? UnknownStepId { get; init; }
        public string UnknownCode { get; init; } = "fake.unknown";
        public Exception? ExecutionException { get; init; }
        public Func<OperationPlan, IReadOnlyList<RealOperationStepProgress>, Task<RealReconciliationResult>>?
            Reconciliation { get; init; }
        public string? PreflightFailureStepId { get; init; }
        public int PreflightFailureCall { get; init; }
        public Exception? PreflightException { get; init; }
        public Exception? PreparationException { get; set; }
        public bool PauseRunnerPreflight { get; init; }
        public bool PauseAcceptPreflight { get; init; }
        public bool PauseExecution { get; init; }
        public CancellationToken ExecutionToken { get; private set; }
        public TimeProvider Clock { get; init; } = TimeProvider.System;
        public TaskCompletionSource AcceptPreflightEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAcceptPreflight { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExecutionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseExecution { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RunnerPreflightEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRunnerPreflight { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OperationPlan> PrepareAsync(
            RealOperationIntentRequest proposal,
            TrustedRealSession session,
            OperationId operationId,
            CancellationToken cancellationToken)
        {
            if (PreparationException is { } exception)
                throw exception;
            var now = Clock.GetUtcNow();
            var environment = new EnvironmentProfile(
                EnvironmentId.New(), EnvironmentKind.LocalMachine,
                "machine-binding-test",
                ExecutionCapability.ReadInventory
                    | ExecutionCapability.MutateStorageStructure,
                false, now);
            return Task.FromResult(RealOperationPlanFactory.Create(
                proposal, operationId, environment, session,
                "inventory-v1", "target-fingerprint-v1",
                "physical-members-v1", "fake capability evidence",
                now, now.AddMinutes(2)));
        }

        public Task<RealPartitionResizeRange> ReadPartitionResizeRangeAsync(
            StorageObjectId partition,
            TrustedRealSession session,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref rangeReadCalls);
            return Task.FromResult(new RealPartitionResizeRange(
                partition, 1024L * 1024 * 1024,
                512L * 1024 * 1024, 4L * 1024 * 1024 * 1024,
                768L * 1024 * 1024, 2L * 1024 * 1024 * 1024,
                "target-fingerprint-v1", DateTimeOffset.UtcNow,
                "real.resize.range.current"));
        }

        public async Task<RealStepPreflight> PreflightStepAsync(
            OperationPlan plan,
            RealOperationStep step,
            IReadOnlyDictionary<string, string> verifiedStepOutputs,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref preflightCalls);
            if (PauseAcceptPreflight && call == 1)
            {
                AcceptPreflightEntered.TrySetResult();
                await ReleaseAcceptPreflight.Task.WaitAsync(cancellationToken);
            }
            if (call == PreflightFailureCall
                && StringComparer.Ordinal.Equals(step.Id, PreflightFailureStepId))
            {
                throw PreflightException ?? new InvalidOperationException("fake preflight failure");
            }
            if (PauseRunnerPreflight && call == 2)
            {
                RunnerPreflightEntered.TrySetResult();
                await ReleaseRunnerPreflight.Task.WaitAsync(cancellationToken);
            }
            return new RealStepPreflight(
                "{\"disk\":\"physical-disk-unique-id\"}",
                "inventory-v1", "target-fingerprint-v1", "physical-members-v1");
        }

        public async Task<RealStepResult> ExecuteStepAsync(
            OperationPlan plan,
            RealOperationStep step,
            RealStepPreflight preflight,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executeCalls);
            ExecutionToken = cancellationToken;
            if (ExecutionException is { } failure) throw failure;
            if (PauseExecution)
            {
                ExecutionEntered.TrySetResult();
                await ReleaseExecution.Task.WaitAsync(cancellationToken);
            }
            if (StringComparer.Ordinal.Equals(step.Id, UnknownStepId))
            {
                return new RealStepResult(
                    RealStepOutcome.OutcomeUnknown, UnknownCode,
                    JsonSerializer.Serialize(new WindowsStorageCommandResult(true, UnknownCode,
                        null, null, null, null, null, null, "Insufficient eligible resources")));
            }
            return new RealStepResult(
                RealStepOutcome.Verified, "fake.verified",
                "{\"partitionStyle\":\"GPT\"}");
        }

        public Task<RealReconciliationResult> ReconcileAsync(
            OperationPlan plan,
            IReadOnlyList<RealOperationStepProgress> persistedSteps,
            CancellationToken cancellationToken)
        {
            if (Reconciliation is not null)
                return Reconciliation(plan, persistedSteps);
            var failedStep = persistedSteps.FirstOrDefault(step =>
                step.State == RealOperationStepState.Failed);
            if (failedStep?.ResultEvidence is { } failureEvidence
                && plan.RealOperation is { } real)
            {
                try
                {
                    var noEffect = JsonSerializer.Deserialize<WindowsNoEffectStepEvidence>(
                        failureEvidence);
                    if (noEffect is { NoWindowsCall: true, Code: "operation.preflight_failed" }
                        && StringComparer.Ordinal.Equals(
                            noEffect.PhysicalMemberFingerprint,
                            real.PhysicalMemberFingerprint))
                    {
                        var verified = persistedSteps.Count(step =>
                            step.State == RealOperationStepState.Verified);
                        var canRelease = persistedSteps.All(step =>
                            step.State is RealOperationStepState.Verified
                                or RealOperationStepState.Failed
                                or RealOperationStepState.StoppedBeforeCall);
                        return Task.FromResult(new RealReconciliationResult(
                            verified == 0
                                ? RealOperationState.Failed
                                : RealOperationState.PartiallyCompleted,
                            persistedSteps, "fake.preflight_no_effect", canRelease));
                    }
                }
                catch (JsonException)
                {
                    // Keep the existing unknown-outcome fallback for invalid evidence.
                }
            }

            return Task.FromResult(new RealReconciliationResult(
                persistedSteps.All(step => step.State == RealOperationStepState.Verified)
                    ? RealOperationState.Succeeded
                    : persistedSteps.All(step => step.State == RealOperationStepState.StoppedBeforeCall)
                        ? RealOperationState.Cancelled
                        : RealOperationState.OutcomeUnknown,
                persistedSteps, "fake.reconciled",
                persistedSteps.All(step => step.State is RealOperationStepState.Verified
                    or RealOperationStepState.StoppedBeforeCall)));
        }
    }
}
