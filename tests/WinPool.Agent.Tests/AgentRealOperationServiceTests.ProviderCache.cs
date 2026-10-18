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
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task StructurePreparationDiscoversOnlyNonPoolableRawMembersOnceAndFreezesTheSubsequentFullRead(
        bool initiallyPoolable, bool poolableAfterDiscovery)
    {
        var system = InternalStableIdentity.SystemFromDocumentId("local:structure-job");
        var before = ProviderCacheSnapshot(initiallyPoolable);
        var baseline = ProviderCacheDocument(before, system);
        var after = before with
        {
            SnapshotVersion = "full-after-discovery",
            PhysicalDisks = before.PhysicalDisks.Select(item => item with { CanPool = poolableAfterDiscovery }).ToArray()
        };
        var fresh = baseline;
        var backend = new RecordingBackend();
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var jobId = Guid.NewGuid();
        var fullReads = 0;
        var discoveryReads = 0;
        fixture.Service.AttachStructureInventory(_ =>
        {
            fullReads++;
            return Task.FromResult(fresh);
        }, _ => Task.FromResult<StorageSystemDocument?>(fresh), (scope, _) =>
        {
            discoveryReads++;
            Assert.True(scope.DiscoverProviderCache);
            Assert.Null(scope.EvidenceProfile);
            Assert.Equal(jobId, scope.OperationId.Value);
            Assert.Equal("poolability-discovery:prepare", scope.StepId);
            Assert.Equal(new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "physical:cache"), Assert.Single(scope.Targets));
            fresh = ProviderCacheDocument(after, system);
            return Task.FromResult(ProviderCacheDocument(after with { SnapshotVersion = "scoped-discovery-only" }, system, scope));
        });
        var result = await fixture.Service.PrepareStructureJobAsync(new(ProviderCacheGoal(baseline), jobId,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default);

        var diagnostics = string.Join("; ", result.Messages.Select(item => item.Code + ": " + item.DiagnosticText));
        Assert.True(discoveryReads == (initiallyPoolable ? 0 : 1), $"discoveryReads={discoveryReads}; {diagnostics}");
        Assert.True(fullReads == (initiallyPoolable ? 1 : 2), $"fullReads={fullReads}; {diagnostics}");
        Assert.True(result.IsSuccess == (initiallyPoolable || poolableAfterDiscovery), diagnostics);
        if (result.IsSuccess)
        {
            Assert.Equal(RealOperationState.Prepared, Assert.IsType<AgentRealStructureJobResponse>(result.Value).State);
            using var saved = JsonDocument.Parse((await fixture.Plans.GetStructureJobAsync(jobId))!.GoalJson);
            var facts = saved.RootElement.GetProperty("Baseline").GetProperty("SourceFacts");
            Assert.False(facts.GetProperty("IsMerged").GetBoolean());
            Assert.Equal(JsonValueKind.Null, facts.GetProperty("ScopedCollection").ValueKind);
            Assert.Equal(initiallyPoolable ? before.SnapshotVersion : "full-after-discovery",
                facts.GetProperty("InventoryVersion").GetString());
        }
        else
        {
            Assert.Null(await fixture.Plans.GetStructureJobAsync(jobId));
            Assert.False(await fixture.Plans.HasStructureWriteBarrierAsync());
        }
        Assert.Equal(0, backend.PreflightCalls);
        Assert.Equal(0, backend.ExecuteCalls);
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("role")]
    [InlineData("size")]
    [InlineData("sector")]
    [InlineData("member-set")]
    [InlineData("output")]
    [InlineData("merged")]
    [InlineData("narrow")]
    public async Task StructurePreparationRejectsChangedMembersOrIncompleteFullEvidenceAfterDiscovery(string change)
    {
        var system = InternalStableIdentity.SystemFromDocumentId("local:structure-job");
        var before = ProviderCacheSnapshot(false);
        var baseline = ProviderCacheDocument(before, system);
        var after = MutateProviderCacheSnapshot(before with
        {
            PhysicalDisks = before.PhysicalDisks.Select(item => item with { CanPool = true }).ToArray()
        }, change);
        var fresh = baseline;
        var backend = new RecordingBackend();
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var discoveryReads = 0;
        fixture.Service.AttachStructureInventory(_ => Task.FromResult(fresh),
            _ => Task.FromResult<StorageSystemDocument?>(fresh), (scope, _) =>
        {
            Assert.True(scope.DiscoverProviderCache);
            discoveryReads++;
            fresh = ProviderCacheDocument(after, system);
            if (change == "merged") fresh = fresh with { SourceFacts = fresh.SourceFacts! with { IsMerged = true } };
            if (change == "narrow") fresh = ProviderCacheDocument(after, system, scope with
                { DiscoverProviderCache = false, EvidenceProfile = WindowsRealStorageReadProfile.Required });
            return Task.FromResult(ProviderCacheDocument(after, system, scope));
        });
        var jobId = Guid.NewGuid();
        var result = await fixture.Service.PrepareStructureJobAsync(new(ProviderCacheGoal(baseline), jobId,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, discoveryReads);
        Assert.Null(await fixture.Plans.GetStructureJobAsync(jobId));
        Assert.Equal(0, backend.PreflightCalls);
        Assert.Equal(0, backend.ExecuteCalls);
        Assert.False(await fixture.Plans.HasStructureWriteBarrierAsync());
    }

    [Theory]
    [InlineData("poolable")]
    [InlineData("refreshed")]
    [InlineData("still-false")]
    [InlineData("serial")]
    [InlineData("role")]
    [InlineData("size")]
    [InlineData("sector")]
    [InlineData("member-set")]
    [InlineData("output")]
    public async Task StructureJobDiscoversAfterVerifiedClearWithoutDiscardingTheReceiptOrMemberProof(string scenario)
    {
        var system = InternalStableIdentity.SystemFromDocumentId("local:structure-job");
        var before = ProviderCacheSnapshot(false, raw: false);
        var baseline = ProviderCacheDocument(before, system);
        var current = baseline;
        var backend = new RecordingBackend();
        var fixture = await CreateAcceptedLifecycleFixtureAsync(backend, TimeProvider.System,
            new InMemoryOperationAuthority(new OperationPolicyEvaluator()));
        await using var lease = fixture.Lease;
        var commands = new List<Type>();
        var scopes = new List<StorageInventoryScope>();
        backend.PreparationOverride = (proposal, trusted, id) =>
        {
            var now = DateTimeOffset.UtcNow;
            var profile = WindowsRealStorageReadProfile.ForSteps(proposal.Steps)?.Id;
            var closure = ProviderCacheClosure(current, proposal.Targets, profile,
                requirePoolable: proposal.Steps.Any(step => step.Command is CreatePoolCommand));
            var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine,
                "machine-binding-test", ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now);
            return RealOperationPlanFactory.Create(proposal, id, environment, trusted, current.SourceFacts!.InventoryVersion,
                closure.Fingerprint, closure.PhysicalMemberFingerprint, "fixture positive support", now, now.AddMinutes(2), profile);
        };
        backend.ExecuteOverride = (plan, step) =>
        {
            commands.Add(step.Command.GetType());
            var snapshot = current.Snapshot;
            var created = step.Command is CreatePoolCommand ? "pool:cache-created" : null;
            snapshot = step.Command switch
            {
                ClearDiskCommand => snapshot with
                {
                    OsDisks = snapshot.OsDisks.Select(item => item with { PartitionStyle = "RAW" }).ToArray(),
                    Partitions = [], Volumes = [],
                    PhysicalDisks = snapshot.PhysicalDisks.Select(item => item with { CanPool = scenario == "poolable" }).ToArray()
                },
                CreatePoolCommand => snapshot with
                {
                    PhysicalDisks = snapshot.PhysicalDisks.Select(item => item with { CanPool = false, PoolStableId = created }).ToArray(),
                    StoragePools = [snapshot.StoragePools.Single() with { MemberPhysicalDiskIds = [] },
                        new(created!, true, "Requested cache pool", false, "Healthy", "OK", 64L << 30, 0,
                            "subsystem:cache", ["physical:cache"], 512, 4096, "Fixed")]
                },
                _ => throw new InvalidDataException("Unexpected fixture command: " + step.Command.GetType().Name)
            };
            var scope = StorageInventoryScopeFactory.Create(current.SourceFacts!, plan.OperationId, step.Id, plan.Targets)
                with { EvidenceProfile = plan.RealOperation!.EvidenceProfile };
            current = ProviderCacheDocument(snapshot, system, scope);
            var post = ProviderCacheClosure(current, plan.Targets, plan.RealOperation.EvidenceProfile);
            return Task.FromResult(new RealStepResult(RealStepOutcome.Verified, "fixture.cache.verified", JsonSerializer.Serialize(new
            {
                PostFingerprint = post.Fingerprint, PhysicalMemberFingerprint = post.PhysicalMemberFingerprint, CreatedObjectId = created
            })) { VerifiedPostState = new(current, scope, current.UpdatedAt) });
        };
        fixture.Service.AttachStructureInventory(_ => Task.FromResult(current),
            _ => Task.FromResult<StorageSystemDocument?>(current), (scope, _) =>
        {
            scopes.Add(scope);
            Assert.Contains(typeof(ClearDiskCommand), commands);
            Assert.DoesNotContain(typeof(CreatePoolCommand), commands);
            if (scope.DiscoverProviderCache)
            {
                Assert.Equal(StorageObjectKind.PhysicalDisk, Assert.Single(scope.Targets).Kind);
                var changed = current.Snapshot with
                {
                    PhysicalDisks = current.Snapshot.PhysicalDisks.Select(item => item with { CanPool = scenario != "still-false" }).ToArray()
                };
                current = ProviderCacheDocument(MutateProviderCacheSnapshot(changed, scenario), system, scope);
            }
            else current = ProviderCacheDocument(current.Snapshot, system, scope);
            return Task.FromResult(current);
        });
        var jobId = Guid.NewGuid();
        var prepared = await fixture.Service.PrepareStructureJobAsync(new(ProviderCacheGoal(baseline), jobId,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default);
        Assert.True(prepared.IsSuccess, string.Join("; ", prepared.Messages.Select(item => item.DiagnosticText)));
        var confirmation = Assert.IsType<AgentRealStructureJobResponse>(prepared.Value);
        Assert.True((await fixture.Service.AcceptStructureJobAsync(new(jobId, confirmation.GoalHash,
            fixture.Session.ProductSessionId, CorrelationId.New()), fixture.Session, default)).IsSuccess);
        var terminal = await AwaitProviderCacheStructureTerminalAsync(fixture.Service, fixture.Session, jobId);

        var succeeds = scenario is "poolable" or "refreshed";
        var durable = (await fixture.Plans.GetStructureJobAsync(jobId))!;
        using var durableResult = JsonDocument.Parse(durable.ResultJson ?? "{}");
        var durableCode = durableResult.RootElement.TryGetProperty("Code", out var code) ? code.GetString() : null;
        var diagnostics = $"scenario={scenario}; state={terminal.State}; code={terminal.Code}; durableResult.Code={durableCode}";
        Assert.True(terminal.State == (succeeds ? RealOperationState.Succeeded : RealOperationState.PartiallyCompleted), diagnostics);
        Assert.False(terminal.RequiresReconciliation);
        Assert.Equal(scenario == "poolable" ? 0 : 1, scopes.Count(scope => scope.DiscoverProviderCache));
        Assert.Equal(1, scopes.Count(scope => !scope.DiscoverProviderCache));
        var expectedCommands = succeeds ? new[] { typeof(ClearDiskCommand), typeof(CreatePoolCommand) } : new[] { typeof(ClearDiskCommand) };
        Assert.Equal(expectedCommands, commands);
        Assert.Equal(succeeds ? 2 : 1, terminal.Children.Count);
        Assert.Equal(succeeds ? 2 : 1, terminal.Completed.Count);
        var clear = (await fixture.Plans.GetAsync(terminal.Children[0]))!;
        Assert.Equal(PersistedOperationState.Completed, clear.State);
        Assert.All(await fixture.Plans.GetStepsAsync(clear.Plan.OperationId), step => Assert.Equal(PersistedOperationStepState.Verified, step.State));
        Assert.False(await fixture.Plans.HasStructureWriteBarrierAsync());
        Assert.False(await fixture.Plans.HasRealWriteBarrierAsync());
    }

    private static StorageSnapshot ProviderCacheSnapshot(bool canPool, bool raw = true) =>
        StorageSnapshot.Empty(Environment.MachineName) with
        {
            SnapshotVersion = "provider-cache-before", ScannedAt = DateTimeOffset.UtcNow,
            StorageSubsystems = [new("subsystem:cache", "Storage Spaces", "Healthy", "OK")],
            PhysicalDisks = [new("physical:cache", true, "Cache fixture", "Fixture", "SERIAL-CACHE", "SATA", "HDD",
                64L << 30, 512, 4096, "Healthy", "OK", canPool, canPool ? "" : "Insufficient Capacity", 7,
                false, false, false, false, null, Usage: PhysicalDiskUsage.AutoSelect)],
            StoragePools = [new("pool:cache-primordial", true, "Primordial", true, "Healthy", "OK", 0, 0,
                "subsystem:cache", ["physical:cache"])],
            OsDisks = [new("disk:cache", "Cache fixture", 7, raw ? "RAW" : "GPT", 64L << 30,
                false, false, false, "physical:cache", null)],
            Partitions = raw ? [] : [new("partition:cache", true, 7, 1, "BasicData", 1L << 20, 32L << 30,
                false, false, "E", "Cache volume", "NTFS", 65536, 16L << 30, "Healthy", "OK", "E:\\", "disk:cache",
                PartitionTypeId: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", Guid: "45935a83-1eb8-490a-b7d1-dad5f2275803",
                GptType: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7")],
            Volumes = raw ? [] : [new("volume:cache", true, "partition:cache", "NTFS", "Cache volume",
                32L << 30, 16L << 30, 65536, "Healthy", "OK", ["E:\\"])]
        };

    private static StorageSnapshot MutateProviderCacheSnapshot(StorageSnapshot snapshot, string change)
    {
        if (change == "member-set") return snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks.Select(item => item with { StableId = "physical:cache-replacement" }).ToArray(),
            StoragePools = snapshot.StoragePools.Select(item => item with { MemberPhysicalDiskIds = ["physical:cache-replacement"] }).ToArray(),
            OsDisks = snapshot.OsDisks.Select(item => item with { PhysicalDiskStableId = "physical:cache-replacement" }).ToArray()
        };
        if (change == "output") return snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks.Select(item => item with { PoolStableId = "pool:cache-external", CanPool = false }).ToArray(),
            StoragePools = [snapshot.StoragePools.Single() with { MemberPhysicalDiskIds = [] },
                new("pool:cache-external", true, "External pool", false, "Healthy", "OK", 64L << 30, 32L << 30,
                    "subsystem:cache", ["physical:cache"], 512, 4096, "Fixed")],
            VirtualDisks = [new("vd:cache-external", true, "External VD", "Healthy", "OK", "Simple", "Fixed", 1,
                65536, 32L << 30, 32L << 30, "pool:cache-external", [], [])]
        };
        return snapshot with
        {
            OsDisks = snapshot.OsDisks.Select(item => change == "role" ? item with { IsBoot = true } : item).ToArray(),
            PhysicalDisks = snapshot.PhysicalDisks.Select(item => change switch
            {
                "serial" => item with { SerialNumber = "CHANGED-SERIAL" },
                "role" => item with { IsBoot = true },
                "size" => item with { Size = item.Size - 4096 },
                "sector" => item with { PhysicalSectorSize = 8192 },
                _ => item
            }).ToArray()
        };
    }

    private static StorageSystemDocument ProviderCacheDocument(StorageSnapshot snapshot, SystemId system, StorageInventoryScope? scope = null)
    {
        var document = JobDocument(snapshot, system, scope);
        var roleId = snapshot.PhysicalDisks.Single().StableId + ":disk-roles";
        return document with { SourceFacts = document.SourceFacts! with
        {
            Objects = document.SourceFacts.Objects.Select(item => item.ObjectType switch
            {
                FactObjectType.PhysicalDisk => item with { Fields = item.Fields.Add(WinPoolSourceField.Returned("AllocatedSize",
                    snapshot.PhysicalDisks.Single().CanPool ? 0UL : (ulong)((64L << 30) - 4096), FactValueType.UInt64, item.SourceRef)) },
                FactObjectType.Disk => item with { Fields = item.Fields.Add(WinPoolSourceField.Returned("Path",
                    @"\\.\PHYSICALDRIVE7", FactValueType.String, item.SourceRef)) },
                _ => item
            }).ToImmutableArray(),
            Relationships = document.SourceFacts.Relationships.Add(new("disk:cache", roleId, "disk-supplement", document.UpdatedAt))
        } };
    }

    private static RealStructureJobGoal ProviderCacheGoal(StorageSystemDocument baseline)
    {
        var poolId = EditWorkspace.DraftPrefix + "cache-pool";
        var working = baseline.Snapshot with
        {
            PhysicalDisks = baseline.Snapshot.PhysicalDisks.Select(item => item with { PoolStableId = poolId }).ToArray(),
            StoragePools = baseline.Snapshot.StoragePools.Append(new(poolId, false, "Requested cache pool", false,
                "", "", 0, 0, "subsystem:cache", ["physical:cache"])).ToArray()
        };
        var editing = new SimulationEditingSession();
        editing.Bind(baseline, working);
        editing.PoolIntents.Add(poolId, new(false, false, "NTFS", 65536, "Unused"));
        var draft = RealStructureDraftPlanner.Build(editing);
        Assert.True(draft.Preview.CanApply, string.Join("; ", draft.Preview.BlockingReasons));
        return RealStructureJobGoal.Create(editing, draft);
    }

    private static RealExactPhysicalMemberSetClosure ProviderCacheClosure(StorageSystemDocument document,
        IReadOnlyList<StorageObjectId> targets, string? profile, bool requirePoolable = false)
    {
        var topology = new WindowsRealStorageTopology(document, "machine-binding-test", DateTimeOffset.UtcNow, profile);
        var members = targets.Where(target => target.Kind == StorageObjectKind.PhysicalDisk).ToArray();
        var pool = document.Snapshot.StoragePools.SingleOrDefault(item => !item.IsPrimordial);
        return pool is null ? topology.RequireSelectedPhysicalMemberSet(members, requirePoolable)
            : topology.RequireExactPhysicalMemberSet(new(document.SystemId, StorageObjectKind.StoragePool, pool.StableId), members);
    }

    private static async Task<AgentRealStructureJobResponse> AwaitProviderCacheStructureTerminalAsync(
        AgentRealOperationService service, TrustedRealSession session, Guid jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var result = Assert.IsType<AgentRealStructureJobResponse>((await service.QueryStructureJobAsync(
                new(jobId, CorrelationId.New()), session, timeout.Token)).Value);
            if (result.State is not (RealOperationState.Accepted or RealOperationState.Running)) return result;
            await Task.Delay(10, timeout.Token);
        }
    }
}
