using WinPool.Agent;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Agent.Tests;

public sealed class AgentRealOperationServiceTests
{
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
        var backend = new RecordingBackend { UnknownStepId = "partition" };
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
        var physical = new StorageObjectId(
            system, StorageObjectKind.PhysicalDisk, "physical-disk-unique-id");
        var disk = new StorageObjectId(
            system, StorageObjectKind.OsDisk, "os-disk-unique-id");
        var proposal = new RealOperationIntentRequest(
            OperationIntent.InitializeDisk, system, [physical, disk],
            [
                new RealOperationStep("gpt",
                    new InitializeGptCommand(RealTargetReference.ForExisting(disk)),
                    [], "RAW", "GPT", "GPT metadata", "fake capability evidence"),
                new RealOperationStep("partition",
                    new CreatePartitionCommand(
                        RealTargetReference.ForExisting(disk),
                        RealPartitionRole.BasicData,
                        1024 * 1024, 1024L * 1024 * 1024),
                    ["gpt"], "free GPT range", "BasicData partition",
                    "partition table changes", "fake capability evidence")
            ], "GPT with BasicData partition");
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
        Assert.Equal(2, backend.ExecuteCalls);
        Assert.True(await repository.HasRealWriteBarrierAsync());
        Assert.Equal(RealOperationStepState.Verified, status!.Steps[0].State);
        Assert.Equal(RealOperationStepState.OutcomeUnknown, status.Steps[1].State);
        Assert.Equal("fake.unknown", status.Steps[1].Code);
        Assert.Equal("fake.reconciled", status.Code);
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

    private sealed class FixedMachineIdentity : IRealMachineIdentityProvider
    {
        public Task<string> ReadBindingAsync(CancellationToken cancellationToken) =>
            Task.FromResult("machine-binding-test");
    }

    private sealed class RecordingBackend : IRealStorageBackend
    {
        private int executeCalls;
        private int preflightCalls;
        private int rangeReadCalls;
        public int ExecuteCalls => Volatile.Read(ref executeCalls);
        public int RangeReadCalls => Volatile.Read(ref rangeReadCalls);
        public string? UnknownStepId { get; init; }
        public bool PauseRunnerPreflight { get; init; }
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
            var now = DateTimeOffset.UtcNow;
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
            if (PauseRunnerPreflight && Interlocked.Increment(ref preflightCalls) == 2)
            {
                RunnerPreflightEntered.TrySetResult();
                await ReleaseRunnerPreflight.Task.WaitAsync(cancellationToken);
            }
            return new RealStepPreflight(
                "{\"disk\":\"physical-disk-unique-id\"}",
                "inventory-v1", "target-fingerprint-v1", "physical-members-v1");
        }

        public Task<RealStepResult> ExecuteStepAsync(
            OperationPlan plan,
            RealOperationStep step,
            RealStepPreflight preflight,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executeCalls);
            if (StringComparer.Ordinal.Equals(step.Id, UnknownStepId))
            {
                return Task.FromResult(new RealStepResult(
                    RealStepOutcome.OutcomeUnknown, "fake.unknown",
                    "{\"provider\":\"result-unavailable\"}"));
            }
            return Task.FromResult(new RealStepResult(
                RealStepOutcome.Verified, "fake.verified",
                "{\"partitionStyle\":\"GPT\"}"));
        }

        public Task<RealReconciliationResult> ReconcileAsync(
            OperationPlan plan,
            IReadOnlyList<RealOperationStepProgress> persistedSteps,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RealReconciliationResult(
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
