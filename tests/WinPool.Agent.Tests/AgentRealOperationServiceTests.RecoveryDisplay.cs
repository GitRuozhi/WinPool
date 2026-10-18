using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Agent;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent.Tests;

public sealed partial class AgentRealOperationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingCreationRecoveryUsesTheFullPublishedDisplayWithoutDroppingUnqueriedDisks(bool externalObjects)
    {
        var system = InternalStableIdentity.SystemFromDocumentId("local:structure-job");
        var baseline = RecoveryDisplayDocument(RecoveryDisplaySnapshot(), system);
        var original = RecoveryDisplayGoal(baseline);
        var actual = RecoveryDisplayAfterCreation(baseline.Snapshot, externalObjects);
        actual = actual with { OsDisks = actual.OsDisks.Where(item => item.StableId != "disk:job").ToArray() };
        var backend = new RecordingBackend();
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var seed = await SeedRecoveryDisplayJobAsync(fixture.Service, fixture.Plans, fixture.Session, baseline, original);
        var full = RecoveryDisplayDocument(actual, system);
        StorageInventoryScope? observedScope = null;
        var scopedReads = 0;
        var publishedReads = 0;
        var restarted = new AgentRealOperationService(fixture.Plans, fixture.Events, backend, new FixedMachineIdentity(),
            authority: null, timeProvider: null, isSessionStillArmed: _ => true, isAdministrator: () => true);
        restarted.AttachStructureInventory(_ => Task.FromResult(full), _ =>
        {
            publishedReads++;
            var publishedScope = Assert.IsType<StorageInventoryScope>(observedScope);
            return Task.FromResult<StorageSystemDocument?>(full with
            {
                SourceFacts = full.SourceFacts! with
                { IsMerged = true, ScopedCollection = new(publishedScope, full.UpdatedAt, DateTimeOffset.UtcNow, true) }
            });
        }, (scope, _) =>
        {
            scopedReads++;
            observedScope = scope;
            Assert.Equal(seed.ChildId, scope.OperationId);
            Assert.Equal("physical:job", Assert.Single(scope.Targets).ProviderKey);
            var raw = RecoveryDisplayDocument(RecoveryDisplayWdcOnly(actual), system, scope);
            Assert.Single(raw.Snapshot.PhysicalDisks);
            Assert.All(raw.SourceFacts!.Sources.Where(source => source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)),
                source => Assert.Equal(scope.Key, source.Coverage!.ScopeKey));
            return Task.FromResult(raw);
        });
        await restarted.InitializeRecoveryAsync();
        var recovered = await AwaitRecoveryDisplayTerminalAsync(restarted, fixture.Session, seed.JobId);

        Assert.True(recovered.State == RealOperationState.PartiallyCompleted, recovered.Code);
        Assert.False(recovered.RequiresReconciliation);
        Assert.Equal("Verified requested pool", Assert.Single(recovered.Completed));
        AssertRecoveryDisplayGoal(full.Snapshot, original, recovered.RemainingGoal!, seed.JobId);
        Assert.Equal(1, scopedReads);
        Assert.Equal(1, publishedReads);
        await AssertRecoveryDisplayPersistedAsync(fixture.Plans, seed.JobId, full.Snapshot);
        var nextId = Guid.NewGuid();
        var next = await restarted.PrepareStructureJobAsync(new(recovered.RemainingGoal!, nextId,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default);
        Assert.True(next.IsSuccess, string.Join("; ", next.Messages.Select(item => item.Code + ": " + item.DiagnosticText)));
        Assert.Equal(RealOperationState.Prepared, Assert.IsType<AgentRealStructureJobResponse>(next.Value).State);
        await restarted.StopStructureJobAsync(new(nextId, Assert.IsType<AgentRealStructureJobResponse>(next.Value).GoalHash,
            CorrelationId.New()), fixture.Session, default);
        Assert.Equal(0, backend.ExecuteCalls);
        Assert.Equal(0, backend.PreflightCalls);
        Assert.Equal(0, backend.ReconciliationCalls);
    }

    [Fact]
    public async Task NormalFailedStageMapsScopedNoEffectFactsOntoTheCompleteDisplay()
    {
        var system = InternalStableIdentity.SystemFromDocumentId("local:structure-job");
        var baseline = RecoveryDisplayDocument(RecoveryDisplaySnapshot(), system);
        var goal = RecoveryDisplayGoal(baseline);
        var backend = new RecordingBackend { PreflightFailureStepId = "create-pool", PreflightFailureCall = 1 };
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        backend.PreparationOverride = (proposal, trusted, id) =>
        {
            var now = DateTimeOffset.UtcNow;
            var profile = WindowsRealStorageReadProfile.ForSteps(proposal.Steps)?.Id;
            var topology = new WindowsRealStorageTopology(baseline, "machine-binding-test", now, profile);
            var closure = topology.RequireSelectedPhysicalMemberSet(proposal.Targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).ToArray(),
                requirePoolable: true);
            var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine, "machine-binding-test",
                ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now);
            return RealOperationPlanFactory.Create(proposal, id, environment, trusted, baseline.SourceFacts!.InventoryVersion,
                closure.Fingerprint, closure.PhysicalMemberFingerprint, "fixture positive support", now, now.AddMinutes(2), profile);
        };
        StorageInventoryScope? observedScope = null;
        var scopedReads = 0;
        var publishedReads = 0;
        fixture.Service.AttachStructureInventory(_ => Task.FromResult(baseline), _ =>
        {
            publishedReads++;
            return Task.FromResult<StorageSystemDocument?>(baseline with { SourceFacts = baseline.SourceFacts! with
            {
                IsMerged = true, ScopedCollection = new(observedScope!, baseline.UpdatedAt, DateTimeOffset.UtcNow, true)
            } });
        }, (scope, _) =>
        {
            scopedReads++;
            observedScope = scope;
            var selected = RecoveryDisplayWdcOnly(baseline.Snapshot);
            selected = selected with { StoragePools = selected.StoragePools.Select(item => item.IsPrimordial
                ? item with { MemberPhysicalDiskIds = ["physical:job"] } : item).ToArray() };
            return Task.FromResult(RecoveryDisplayDocument(selected, system, scope));
        });
        var jobId = Guid.NewGuid();
        var preparation = await fixture.Service.PrepareStructureJobAsync(new(goal, jobId,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default);
        Assert.True(preparation.IsSuccess, string.Join("; ", preparation.Messages.Select(item => item.DiagnosticText)));
        var prepared = Assert.IsType<AgentRealStructureJobResponse>(preparation.Value);
        Assert.True((await fixture.Service.AcceptStructureJobAsync(new(jobId, prepared.GoalHash,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default)).IsSuccess);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        AgentRealStructureJobResponse terminal;
        do
        {
            terminal = Assert.IsType<AgentRealStructureJobResponse>((await fixture.Service.QueryStructureJobAsync(
                new(jobId, CorrelationId.New()), fixture.Session, timeout.Token)).Value);
            if (terminal.State is RealOperationState.Accepted or RealOperationState.Running) await Task.Delay(10, timeout.Token);
        } while (terminal.State is RealOperationState.Accepted or RealOperationState.Running);
        Assert.Equal(RealOperationState.Failed, terminal.State);
        Assert.False(terminal.RequiresReconciliation);
        var remaining = terminal.RemainingGoal!;
        Assert.Equal(jobId, remaining.ResumeFromJobId);
        AssertRecoveryDisplayObjects(baseline.Snapshot.PhysicalDisks.Where(item => item.StableId != "physical:job").ToArray(),
            remaining.Working.PhysicalDisks, item => item.StableId);
        Assert.Equal(3, remaining.Working.PhysicalDisks.Count);
        AssertRecoveryDisplayObjects(baseline.Snapshot.OsDisks, remaining.Working.OsDisks, item => item.StableId);
        AssertRecoveryDisplayObjects(baseline.Snapshot.Partitions, remaining.Working.Partitions, item => item.StableId);
        AssertRecoveryDisplayObjects(baseline.Snapshot.Volumes, remaining.Working.Volumes, item => item.StableId);
        AssertRecoveryDisplayObjects(baseline.Snapshot.VirtualDisks, remaining.Working.VirtualDisks, item => item.StableId);
        AssertRecoveryDisplayObjects(baseline.Snapshot.StoragePools.Select(item => item with
                { MemberPhysicalDiskIds = item.MemberPhysicalDiskIds.Order(StringComparer.Ordinal).ToArray() }).ToArray(),
            remaining.Working.StoragePools.Select(item => item with
                { MemberPhysicalDiskIds = item.MemberPhysicalDiskIds.Order(StringComparer.Ordinal).ToArray() }).ToArray(), item => item.StableId);
        Assert.Equal(JsonSerializer.Serialize(Assert.Single(goal.PoolIntents)), JsonSerializer.Serialize(Assert.Single(remaining.PoolIntents)));
        Assert.Equal(JsonSerializer.Serialize(Assert.Single(goal.Working.VirtualDisks, item => EditWorkspace.IsDraftVirtualDisk(item.StableId))),
            JsonSerializer.Serialize(Assert.Single(remaining.Working.VirtualDisks, item => EditWorkspace.IsDraftVirtualDisk(item.StableId))));
        await AssertRecoveryDisplayPersistedAsync(fixture.Plans, jobId, baseline.Snapshot);
        Assert.Equal(1, scopedReads);
        Assert.Equal(1, publishedReads);
        Assert.Equal(1, backend.PreflightCalls);
        Assert.Equal(0, backend.ExecuteCalls);
        Assert.False(await fixture.Plans.HasStructureWriteBarrierAsync());
        Assert.False(await fixture.Plans.HasRealWriteBarrierAsync());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("serial")]
    [InlineData("role")]
    public async Task QueryRepairsAnOldTruncatedRemainingDisplayOnceWithoutAuthorizingChangedTargets(string targetChange)
    {
        var system = InternalStableIdentity.SystemFromDocumentId("local:structure-job");
        var baseline = RecoveryDisplayDocument(RecoveryDisplaySnapshot(), system);
        var original = RecoveryDisplayGoal(baseline);
        var actual = RecoveryDisplayAfterCreation(baseline.Snapshot, externalObjects: true);
        var backend = new RecordingBackend();
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var seed = await SeedRecoveryDisplayJobAsync(fixture.Service, fixture.Plans, fixture.Session, baseline, original);
        var rawScope = StorageInventoryScopeFactory.Create(baseline.SourceFacts!, seed.ChildId, null,
            [new(system, StorageObjectKind.PhysicalDisk, "physical:job")]);
        var raw = RecoveryDisplayDocument(RecoveryDisplayWdcOnly(actual), system, rawScope);
        var requestedDisk = original.Working.VirtualDisks.Single(item => EditWorkspace.IsDraftVirtualDisk(item.StableId));
        var truncated = RecoveryDisplayGoal(raw, "pool:created", requestedDisk) with { ResumeFromJobId = seed.JobId };
        var badResult = JsonSerializer.Serialize(new { Completed = new[] { "Verified requested pool" }, Remaining = truncated,
            Code = "agent.structure.recovered_read_only", Baseline = raw, Pending = (object?)null, Layout = (RealAutomaticLayoutSnapshot?)null });
        Assert.True(await fixture.Plans.TransitionStructureJobAsync(seed.JobId, RealOperationState.Running,
            RealOperationState.PartiallyCompleted, badResult, DateTimeOffset.UtcNow));
        var beforeRepair = (await fixture.Plans.GetStructureJobAsync(seed.JobId))!;
        var published = RecoveryDisplayDocument(MutateRecoveryDisplayTarget(actual, targetChange), system);
        var publishedReads = 0;
        var restarted = new AgentRealOperationService(fixture.Plans, fixture.Events, backend, new FixedMachineIdentity(),
            authority: null, timeProvider: null, isSessionStillArmed: _ => true, isAdministrator: () => true);
        restarted.AttachStructureInventory(_ => Task.FromResult(published), _ =>
        {
            publishedReads++;
            return Task.FromResult<StorageSystemDocument?>(published with { SourceFacts = published.SourceFacts! with { IsMerged = true } });
        },
            (_, _) => throw new InvalidOperationException("Repairing a terminal display must not issue another provider query."));
        await restarted.InitializeRecoveryAsync();
        var response = await restarted.QueryStructureJobAsync(new(seed.JobId, CorrelationId.New()), fixture.Session, default);
        Assert.True(response.IsSuccess, string.Join("; ", response.Messages.Select(item => item.Code + ": " + item.DiagnosticText)));
        var repaired = Assert.IsType<AgentRealStructureJobResponse>(response.Value);
        Assert.Equal(RealOperationState.PartiallyCompleted, repaired.State);
        AssertRecoveryDisplayGoal(published.Snapshot, original, repaired.RemainingGoal!, seed.JobId);
        var desiredPhysical = repaired.RemainingGoal!.Working.PhysicalDisks.Single(item => item.StableId == "physical:job");
        Assert.Equal("SERIAL-JOB", desiredPhysical.SerialNumber);
        Assert.False(desiredPhysical.IsBoot);
        var afterRepair = (await fixture.Plans.GetStructureJobAsync(seed.JobId))!;
        Assert.NotEqual(badResult, afterRepair.ResultJson);
        Assert.Equal(beforeRepair.GoalJson, afterRepair.GoalJson);
        Assert.Equal(beforeRepair.IntentHash, afterRepair.IntentHash);
        Assert.Equal(beforeRepair.Children, afterRepair.Children);
        await AssertRecoveryDisplayPersistedAsync(fixture.Plans, seed.JobId, published.Snapshot);
        await restarted.QueryStructureJobAsync(new(seed.JobId, CorrelationId.New()), fixture.Session, default);
        var repeated = (await fixture.Plans.GetStructureJobAsync(seed.JobId))!;
        Assert.Equal(afterRepair.ResultJson, repeated.ResultJson);
        Assert.Equal(afterRepair.UpdatedAt, repeated.UpdatedAt);
        Assert.Equal(1, publishedReads);

        var nextId = Guid.NewGuid();
        var next = await restarted.PrepareStructureJobAsync(new(repaired.RemainingGoal!, nextId,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default);
        Assert.True(next.IsSuccess == (targetChange == "none"),
            string.Join("; ", next.Messages.Select(item => item.Code + ": " + item.DiagnosticText)));
        if (targetChange == "none")
        {
            var prepared = Assert.IsType<AgentRealStructureJobResponse>(next.Value);
            Assert.Equal(RealOperationState.Prepared, prepared.State);
            await restarted.StopStructureJobAsync(new(nextId, prepared.GoalHash, CorrelationId.New()), fixture.Session, default);
        }
        else Assert.Null(await fixture.Plans.GetStructureJobAsync(nextId));
        Assert.Equal(0, backend.ExecuteCalls);
        Assert.Equal(0, backend.PreflightCalls);
        Assert.Equal(0, backend.ReconciliationCalls);
        Assert.False(await fixture.Plans.HasStructureWriteBarrierAsync());
        Assert.False(await fixture.Plans.HasRealWriteBarrierAsync());
    }

    private static async Task<(Guid JobId, OperationId ChildId)> SeedRecoveryDisplayJobAsync(AgentRealOperationService service,
        OperationPlanRepository plans, TrustedRealSession session, StorageSystemDocument baseline, RealStructureJobGoal goal)
    {
        var proposal = RealOperationProposalFactory.CreatePoolWithMembers(baseline.SystemId,
            [new(baseline.SystemId, StorageObjectKind.PhysicalDisk, "physical:job")], "Requested pool");
        var prepared = await service.PrepareAsync(new(proposal, Guid.NewGuid(), session.ProductSessionId,
            CorrelationId.New()), session, default);
        Assert.True(prepared.IsSuccess, string.Join("; ", prepared.Messages.Select(item => item.DiagnosticText)));
        var child = Assert.IsType<AgentRealOperationResponse>(prepared.Value).Plan;
        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid();
        var pending = new { OperationId = child.OperationId, Proposal = proposal,
            Mapping = new RealStructureResultSynchronization.OutputMapping(StorageObjectKind.StoragePool,
                Assert.Single(goal.PoolIntents).PoolId, "create-pool"), Description = "Verified requested pool",
            Layout = (RealAutomaticLayoutSnapshot?)null };
        await plans.SaveStructureJobAsync(new(jobId, new string('b', 64),
            JsonSerializer.Serialize(new { Goal = goal, Baseline = baseline, Layout = (RealAutomaticLayoutSnapshot?)null }),
            "machine-binding-test", session.Binding, RealOperationState.Running, now, now,
            JsonSerializer.Serialize(new { Completed = Array.Empty<string>(), Remaining = goal, Code = (string?)null,
                Baseline = baseline, Pending = pending, Layout = (RealAutomaticLayoutSnapshot?)null }), []));
        await plans.LinkStructureChildAsync(jobId, child.OperationId, 0);
        await plans.AcceptAsync(child.OperationId, child.PlanHash, new string('a', 64), now);
        ExecutionEvent Progress(string code) => new(child.OperationId, ExecutionEventKind.Progress, now, code, code);
        Assert.True(await plans.TransitionAsync(child.OperationId, PersistedOperationState.Accepted,
            PersistedOperationState.Running, Progress("fixture.running")));
        Assert.True(await plans.TransitionStepAsync(child.OperationId, "create-pool", PersistedOperationStepState.NotStarted,
            PersistedOperationStepState.PreparingCall, "exact WDC fixture member", null, Progress("fixture.preparing")));
        Assert.True(await plans.TransitionStepAsync(child.OperationId, "create-pool", PersistedOperationStepState.PreparingCall,
            PersistedOperationStepState.CallIssued, "exact WDC fixture member", null, Progress("fixture.issued")));
        Assert.True(await plans.TransitionStepAsync(child.OperationId, "create-pool", PersistedOperationStepState.CallIssued,
            PersistedOperationStepState.Verified, null, "{\"CreatedObjectId\":\"pool:created\"}", Progress("fixture.verified")));
        Assert.True(await plans.TransitionAsync(child.OperationId, PersistedOperationState.Running,
            PersistedOperationState.Completed, Progress("fixture.completed")));
        return (jobId, child.OperationId);
    }

    private static RealStructureJobGoal RecoveryDisplayGoal(StorageSystemDocument baseline, string? existingPool = null,
        VirtualDiskInfo? requestedDisk = null)
    {
        var poolId = existingPool ?? EditWorkspace.DraftPrefix + "recovery-display-pool";
        var working = baseline.Snapshot;
        if (existingPool is null) working = working with
        {
            PhysicalDisks = working.PhysicalDisks.Select(item => item.StableId == "physical:job" ? item with { PoolStableId = poolId } : item).ToArray(),
            StoragePools = working.StoragePools.Append(new(poolId, false, "Requested pool", false, "", "", 0, 0,
                "subsystem:job", ["physical:job"])).ToArray()
        };
        working = requestedDisk is null ? SimulationEditingSession.InsertRealDraftVirtualDisk(working, poolId, "Remaining MAX VD")
            : working with { VirtualDisks = working.VirtualDisks.Append(requestedDisk with { PoolStableId = poolId }).ToArray() };
        var editing = new SimulationEditingSession();
        editing.Bind(baseline, working);
        editing.PoolIntents.Add(poolId, new(true, true, "NTFS", 65536, "WinPool_Test", VirtualDiskName: "Remaining MAX VD",
            VirtualDiskUseMaximum: true, CreateMsr: true, DriveLetter: 'E'));
        var draft = RealStructureDraftPlanner.Build(editing);
        Assert.True(draft.Preview.CanApply, string.Join("; ", draft.Preview.BlockingReasons));
        return RealStructureJobGoal.Create(editing, draft);
    }

    private static StorageSnapshot RecoveryDisplaySnapshot()
    {
        var snapshot = StorageSnapshot.Empty(Environment.MachineName) with
        {
            SnapshotVersion = "recovery-full-before", ScannedAt = DateTimeOffset.UtcNow,
            StorageSubsystems = [new("subsystem:job", "Storage Spaces", "Healthy", "OK")],
            PhysicalDisks = [
                new("physical:job", true, "WDC fixture", "WDC fixture", "SERIAL-JOB", "SATA", "HDD", 64L << 30,
                    512, 4096, "Healthy", "OK", true, "", 7, false, false, false, false, null, Usage: PhysicalDiskUsage.AutoSelect),
                new("physical:samsung-system", true, "Samsung system fixture", "Samsung", "SAMSUNG-SYSTEM", "NVMe", "SSD", 256L << 30,
                    512, 4096, "Healthy", "OK", false, "Insufficient Capacity", 8, true, true, true, true, null, Usage: PhysicalDiskUsage.AutoSelect),
                new("physical:samsung-data", true, "Samsung data fixture", "Samsung", "SAMSUNG-DATA", "NVMe", "SSD", 128L << 30,
                    512, 4096, "Healthy", "OK", false, "In a pool", 9, false, false, false, false, "pool:samsung-data", Usage: PhysicalDiskUsage.AutoSelect)],
            StoragePools = [
                new("pool:primordial", true, "Primordial", true, "Healthy", "OK", 0, 0, "subsystem:job", ["physical:job", "physical:samsung-system"]),
                new("pool:samsung-data", true, "Existing data pool", false, "Healthy", "OK", 128L << 30, 32L << 30,
                    "subsystem:job", ["physical:samsung-data"], 512, 4096, "Fixed")],
            VirtualDisks = [new("vd:samsung-data", true, "Existing Samsung VD", "Healthy", "OK", "Simple", "Fixed", 1, 65536,
                32L << 30, 32L << 30, "pool:samsung-data", [], [9])],
            OsDisks = [
                new("disk:job", "WDC fixture", 7, "RAW", 64L << 30, false, false, false, "physical:job", null),
                new("disk:samsung-system", "Samsung system", 8, "GPT", 256L << 30, true, true, false, "physical:samsung-system", null),
                new("disk:samsung-data", "Samsung data", 9, "GPT", 32L << 30, false, false, false, null, "vd:samsung-data")],
            Partitions = [RecoveryDisplayPartition("system", 8, "C", "disk:samsung-system", true),
                RecoveryDisplayPartition("data", 9, "D", "disk:samsung-data", false)],
            Volumes = [RecoveryDisplayVolume("system", "C"), RecoveryDisplayVolume("data", "D")]
        };
        return snapshot;
    }

    private static PartitionInfo RecoveryDisplayPartition(string suffix, int number, string letter, string disk, bool system) =>
        new("partition:" + suffix, true, number, 1, "BasicData", 1L << 20, 16L << 30, system, system,
            letter, "Preserve " + suffix, "NTFS", 65536, 8L << 30, "Healthy", "OK", letter + ":\\", disk,
            PartitionTypeId: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", Guid: suffix == "system"
                ? "bd9750d7-fd0f-46ee-b0f7-dea27611dbe1" : suffix == "data" ? "a212a174-eb91-49dd-9291-35919a6865a3" : "d1d92aae-dd66-47b2-baca-a05ea7b520b0",
            GptType: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");

    private static VolumeInfo RecoveryDisplayVolume(string suffix, string letter) => new("volume:" + suffix, true,
        "partition:" + suffix, "NTFS", "Preserve " + suffix, 16L << 30, 8L << 30, 65536, "Healthy", "OK", [letter + ":\\"]);

    private static StorageSnapshot RecoveryDisplayAfterCreation(StorageSnapshot before, bool externalObjects)
    {
        var actual = before with
        {
            SnapshotVersion = "recovery-full-after",
            PhysicalDisks = before.PhysicalDisks.Select(item => item.StableId == "physical:job"
                ? item with { PoolStableId = "pool:created", CanPool = false } : item).ToArray(),
            StoragePools = before.StoragePools.Select(item => item.IsPrimordial ? item with
                { MemberPhysicalDiskIds = item.MemberPhysicalDiskIds.Where(id => id != "physical:job").ToArray() } : item)
                .Append(new("pool:created", true, "Requested pool", false, "Healthy", "OK", 64L << 30, 0,
                    "subsystem:job", ["physical:job"], 512, 4096, "Fixed")).ToArray()
        };
        if (!externalObjects) return actual;
        return actual with
        {
            VirtualDisks = actual.VirtualDisks.Append(new("vd:external", true, "New external VD", "Healthy", "OK", "Simple", "Fixed", 1,
                65536, 32L << 30, 32L << 30, "pool:samsung-data", [], [17])).ToArray(),
            OsDisks = actual.OsDisks.Append(new("disk:external", "External VD disk", 17, "GPT", 32L << 30,
                false, false, false, null, "vd:external")).ToArray(),
            Partitions = actual.Partitions.Append(RecoveryDisplayPartition("external", 17, "G", "disk:external", false)).ToArray(),
            Volumes = actual.Volumes.Append(RecoveryDisplayVolume("external", "G")).ToArray()
        };
    }

    private static StorageSnapshot RecoveryDisplayWdcOnly(StorageSnapshot full) => full with
    {
        PhysicalDisks = full.PhysicalDisks.Where(item => item.StableId == "physical:job").ToArray(),
        StoragePools = full.StoragePools.Where(item => item.IsPrimordial || item.StableId == "pool:created")
            .Select(item => item.IsPrimordial ? item with { MemberPhysicalDiskIds = [] } : item).ToArray(),
        VirtualDisks = [], OsDisks = full.OsDisks.Where(item => item.StableId == "disk:job").ToArray(), Partitions = [], Volumes = []
    };

    private static StorageSnapshot MutateRecoveryDisplayTarget(StorageSnapshot full, string change) => full with
    {
        PhysicalDisks = full.PhysicalDisks.Select(item => item.StableId != "physical:job" ? item : change switch
        {
            "serial" => item with { SerialNumber = "REPLACED-WDC" }, "role" => item with { IsBoot = true }, _ => item
        }).ToArray(),
        OsDisks = full.OsDisks.Select(item => item.StableId == "disk:job" && change == "role" ? item with { IsBoot = true } : item).ToArray()
    };

    private static StorageSystemDocument RecoveryDisplayDocument(StorageSnapshot snapshot, SystemId system, StorageInventoryScope? scope = null)
    {
        var document = JobDocument(snapshot with { SnapshotVersion = snapshot.SnapshotVersion + ":" + Guid.NewGuid().ToString("N") }, system, scope);
        var facts = document.SourceFacts!;
        facts = facts with
        {
            Objects = facts.Objects.Select(item => item.ObjectType != FactObjectType.Disk ? item : item with
            {
                Fields = item.Fields.Add(WinPoolSourceField.Returned("Path", @"\\.\PHYSICALDRIVE" + snapshot.OsDisks.Single(disk => disk.StableId == item.Id).Number,
                    FactValueType.String, item.SourceRef))
            }).ToImmutableArray(),
            Relationships = facts.Relationships.AddRange(snapshot.OsDisks.Where(disk => disk.PhysicalDiskStableId is not null).Select(disk =>
                new WinPoolFactRelationship(disk.StableId, disk.PhysicalDiskStableId + ":disk-roles", "disk-supplement", document.UpdatedAt)))
        };
        if (scope is not null)
        {
            var covered = scope.BeforeObjectIds.Concat(facts.Objects.Select(item => item.Id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            facts = facts with { Sources = facts.Sources.Select(source => source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                || source.ClassName is "Windows.DiskRoles" or "Win32_DiskDrive" ? source with
                { Coverage = new(scope.Key, covered, true, scope.Generation) } : source).ToImmutableArray() };
        }
        return document with { SourceFacts = facts };
    }

    private static void AssertRecoveryDisplayGoal(StorageSnapshot expected, RealStructureJobGoal original,
        RealStructureJobGoal recovered, Guid jobId)
    {
        var actual = recovered.Working;
        Assert.Equal(jobId, recovered.ResumeFromJobId);
        Assert.Equal(expected.PhysicalDisks.Select(item => item.StableId).Order(), actual.PhysicalDisks.Select(item => item.StableId).Order());
        AssertRecoveryDisplayObjects(expected.OsDisks, actual.OsDisks, item => item.StableId);
        AssertRecoveryDisplayObjects(expected.Partitions, actual.Partitions, item => item.StableId);
        AssertRecoveryDisplayObjects(expected.Volumes, actual.Volumes, item => item.StableId);
        AssertRecoveryDisplayObjects(expected.VirtualDisks, actual.VirtualDisks, item => item.StableId);
        AssertRecoveryDisplayObjects(expected.StoragePools, actual.StoragePools, item => item.StableId);
        foreach (var physical in expected.PhysicalDisks.Where(item => item.StableId != "physical:job"))
            Assert.Equal(JsonSerializer.Serialize(physical), JsonSerializer.Serialize(actual.PhysicalDisks.Single(item => item.StableId == physical.StableId)));
        Assert.Equal(expected.StoragePools.Single(item => item.IsPrimordial).MemberPhysicalDiskIds.Order(),
            actual.StoragePools.Single(item => item.IsPrimordial).MemberPhysicalDiskIds.Order());
        var expectedRelationships = StorageRelationshipProjector.Project(actual);
        Assert.Equal(expectedRelationships.Count, actual.Relationships.Count);
        Assert.All(expectedRelationships, relationship => Assert.Contains(relationship, actual.Relationships));
        var remaining = Assert.Single(recovered.PoolIntents);
        Assert.Equal("pool:created", remaining.PoolId);
        Assert.Equal(Assert.Single(original.PoolIntents).Intent, remaining.Intent);
        var draft = Assert.Single(actual.VirtualDisks, item => EditWorkspace.IsDraftVirtualDisk(item.StableId));
        Assert.Equal(original.Working.VirtualDisks.Single(item => EditWorkspace.IsDraftVirtualDisk(item.StableId)).StableId, draft.StableId);
        Assert.Equal("pool:created", draft.PoolStableId);
        Assert.Equal("Remaining MAX VD", draft.FriendlyName);
        Assert.True(remaining.Intent.VirtualDiskUseMaximum);
        Assert.Equal("WinPool_Test", remaining.Intent.VolumeName);
        Assert.Equal('E', remaining.Intent.DriveLetter);
    }

    private static void AssertRecoveryDisplayObjects<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, Func<T, string> id)
    {
        foreach (var item in expected)
            Assert.Equal(JsonSerializer.Serialize(item), JsonSerializer.Serialize(Assert.Single(actual, value => id(value) == id(item))));
    }

    private static async Task AssertRecoveryDisplayPersistedAsync(OperationPlanRepository plans, Guid jobId, StorageSnapshot expected)
    {
        using var saved = JsonDocument.Parse((await plans.GetStructureJobAsync(jobId))!.ResultJson!);
        var facts = JsonSerializer.Deserialize<WinPoolFacts>(saved.RootElement.GetProperty("Baseline").GetProperty("SourceFacts"))!;
        Assert.Equal(expected.PhysicalDisks.Count, facts.Objects.Count(item => item.ObjectType == FactObjectType.PhysicalDisk));
        var baseline = WinPoolStorageProjection.Project(facts);
        var expectedTarget = expected.PhysicalDisks.Single(item => item.StableId == "physical:job");
        var baselineTarget = baseline.PhysicalDisks.Single(item => item.StableId == "physical:job");
        Assert.Equal(expectedTarget.SerialNumber, baselineTarget.SerialNumber);
        Assert.Equal(expectedTarget.IsBoot, baselineTarget.IsBoot);
        Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("Pending").ValueKind);
        var remaining = JsonSerializer.Deserialize<RealStructureJobGoal>(saved.RootElement.GetProperty("Remaining"))!;
        Assert.Equal(expected.PhysicalDisks.Count, remaining.Working.PhysicalDisks.Count);
        AssertRecoveryDisplayObjects(expected.Partitions, remaining.Working.Partitions, item => item.StableId);
        AssertRecoveryDisplayObjects(expected.Volumes, remaining.Working.Volumes, item => item.StableId);
    }

    private static async Task<AgentRealStructureJobResponse> AwaitRecoveryDisplayTerminalAsync(AgentRealOperationService service,
        TrustedRealSession session, Guid jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var response = Assert.IsType<AgentRealStructureJobResponse>((await service.QueryStructureJobAsync(
                new(jobId, CorrelationId.New()), session, timeout.Token)).Value);
            if (!response.RequiresReconciliation) return response;
            await Task.Delay(10, timeout.Token);
        }
    }
}
