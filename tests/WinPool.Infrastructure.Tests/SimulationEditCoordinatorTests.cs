using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class SimulationEditCoordinatorTests
{
    [Fact]
    public async Task EditPassesThroughPlanAuthorizationAndSimulationExecutorBeforeCommit()
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        StorageSystemDocument? committed = null;
        SimulationEditCommit? structuredCommit = null;
        var coordinator = new SimulationEditCoordinator(
            () => active,
            (commit, _) =>
            {
                structuredCommit = commit;
                committed = commit.Document;
                active = commit.Document;
                return Task.CompletedTask;
            },
            new SimulationOperationService());

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.Rename,
                "physical:p1",
                Name: "Renamed disk"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(committed);
        Assert.NotNull(structuredCommit);
        Assert.Equal(result.Value!.PlanHash, structuredCommit.Plan.PlanHash);
        Assert.Equal(
            WinPool.Execution.ExecutionEventKind.Completed,
            structuredCommit.Events[^1].Kind);
        Assert.Equal(
            "Renamed disk",
            committed.Snapshot.PhysicalDisks.Single().FriendlyName);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.PlanHash));
        Assert.Equal("physical:p1", result.Value.Target.ProviderKey);
        Assert.Equal(result.Value.BeforeRevision + 1, result.Value.AfterRevision);
        Assert.Contains("Set-PhysicalDisk -InputObject $targetPhysicalDisk -NewFriendlyName", Assert.Single(result.Value.SimulatedCommands));
    }

    [Fact]
    public async Task LocalDocumentIsRejectedBeforeSimulationEditorOrCommit()
    {
        var committed = false;
        var coordinator = new SimulationEditCoordinator(
            () => CreateDocument(StorageSystemKind.Local),
            (_, _) =>
            {
                committed = true;
                return Task.CompletedTask;
            },
            new ThrowingSimulationEditor());

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.Rename,
                "physical:p1",
                Name: "Forbidden"),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal("simulation.local-read-only", Assert.Single(result.Messages).Code);
        Assert.False(committed);
    }

    [Fact]
    public async Task InvalidSimulationEditDoesNotCommitPartialDocument()
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        var committed = false;
        var coordinator = new SimulationEditCoordinator(
            () => active,
            (_, _) =>
            {
                committed = true;
                return Task.CompletedTask;
            },
            new SimulationOperationService());

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.FormatPartition,
                "physical:p1",
                FileSystem: "NTFS"),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Failed, result.Status);
        Assert.False(committed);
        Assert.Equal("simulation.failed", Assert.Single(result.Messages).Code);
    }

    [Fact]
    public async Task SyntheticUnifiedTargetIsRejectedBeforeSimulationEditorOrCommit()
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        var managedPool = new StoragePoolInfo(
            "pool:managed", true, "Managed", false, "Healthy", "OK",
            1_000_000_000, 0, "subsystem:1", ["physical:p1"]);
        active = active.WithCandidate(active.Snapshot with
        {
            PhysicalDisks =
            [
                active.Snapshot.PhysicalDisks.Single() with
                {
                    PoolStableId = managedPool.StableId,
                    Usage = "HotSpare"
                }
            ],
            StoragePools = [active.Snapshot.StoragePools.Single(), managedPool]
        });
        var syntheticTarget = SyntheticStorageProjection.TierStableId(
            managedPool.StableId,
            SyntheticStorageName.HotSpareLayer);
        var committed = false;
        var coordinator = new SimulationEditCoordinator(
            () => active,
            (_, _) =>
            {
                committed = true;
                return Task.CompletedTask;
            },
            new ThrowingSimulationEditor());

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.Rename,
                syntheticTarget,
                Name: "Must not mutate"),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal("simulation.target-missing", Assert.Single(result.Messages).Code);
        Assert.False(committed);
    }

    [Fact]
    public async Task PrimordialUiAliasResolvesToStablePoolAndBindsMemberDisk()
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        var coordinator = new SimulationEditCoordinator(
            () => active,
            (commit, _) =>
            {
                active = commit.Document;
                return Task.CompletedTask;
            },
            new SimulationOperationService());

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.CreateStoragePool,
                "primordial",
                Name: "Pool03",
                MemberDiskIds: ["physical:p1"]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("pool:primordial", result.Value!.Target.ProviderKey);
        Assert.Contains(active.Snapshot.StoragePools, pool => pool.FriendlyName == "Pool03");
        Assert.DoesNotContain(
            "physical:p1",
            active.Snapshot.StoragePools.Single(pool => pool.IsPrimordial).MemberPhysicalDiskIds);
    }

    [Fact]
    public async Task BuiltInResetAlsoUsesTheAuthorizedSimulationExecutionChain()
    {
        var active = CreateDocument(StorageSystemKind.Simulation) with
        {
            Id = "simulation:builtin:test",
            SystemId = InternalStableIdentity.SystemFromDocumentId("simulation:builtin:test")
        };
        var coordinator = new SimulationEditCoordinator(
            () => active,
            (commit, _) =>
            {
                active = commit.Document;
                return Task.CompletedTask;
            },
            new SimulationOperationService(),
            document => new SimulationOperationResult(
                true,
                document with { DisplayName = "Reset document" },
                string.Empty,
                ["Reset-SimulationDocument -BuiltIn"]));

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.ResetDocument,
                active.Snapshot.Computer.StableId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Reset document", active.DisplayName);
        Assert.Equal(
            "Reset-SimulationDocument -BuiltIn",
            Assert.Single(result.Value!.SimulatedCommands));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.PlanHash));
    }

    [Fact]
    public async Task ImportedSimulationCannotUseBuiltInResetContract()
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        var resetCalled = false;
        var coordinator = new SimulationEditCoordinator(
            () => active,
            (_, _) => Task.CompletedTask,
            new SimulationOperationService(),
            document =>
            {
                resetCalled = true;
                return SimulationOperationResult.Failure(document, "Unexpected reset.");
            });

        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.ResetDocument,
                active.Snapshot.Computer.StableId),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal("simulation.reset-built-in-only", Assert.Single(result.Messages).Code);
        Assert.False(resetCalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostCommitReplyIsReconciledWithoutResubmittingAndAdvancesNextEditBaseline(bool useDraftPlan)
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        var connection = new LostCommitReplyConnection();
        var repository = new AgentBackedStorageSystemRepository(connection);
        await repository.SaveSimulationAsync(active);
        var coordinator = new SimulationEditCoordinator(
            () => active,
            async (commit, token) =>
            {
                await repository.SaveEditAsync(
                    commit.Document, commit.Plan, commit.Events, token, commit.CommitId);
                active = commit.Document;
            },
            new SimulationOperationService());

        var first = await ExecuteRenameAsync(coordinator, "First rename", useDraftPlan);

        Assert.True(first.IsSuccess);
        Assert.Equal("First rename", active.Snapshot.PhysicalDisks.Single().FriendlyName);
        Assert.Collection(connection.Requests,
            request => Assert.IsType<SaveAgentSimulationDocumentRequest>(request),
            request => Assert.IsType<CommitAgentSimulationEditRequest>(request),
            request => Assert.IsType<LookupAgentSimulationCommitRequest>(request));

        var second = await ExecuteRenameAsync(coordinator, "Second rename", useDraftPlan);

        Assert.True(second.IsSuccess);
        Assert.Equal("Second rename", active.Snapshot.PhysicalDisks.Single().FriendlyName);
        Assert.Equal(first.Value!.AfterRevision, second.Value!.BeforeRevision);
        Assert.Equal(second.Value.BeforeRevision + 1, second.Value.AfterRevision);
        Assert.Equal(4, connection.Requests.Count);
        var firstCommit = Assert.IsType<CommitAgentSimulationEditRequest>(connection.Requests[1]);
        var secondCommit = Assert.IsType<CommitAgentSimulationEditRequest>(connection.Requests[3]);
        Assert.Equal(firstCommit.Document.Sha256, secondCommit.ExpectedPreviousSha256);
        Assert.False(string.IsNullOrWhiteSpace(firstCommit.CommitId));
        Assert.NotEqual(firstCommit.CommitId, secondCommit.CommitId);
    }

    [Theory]
    [InlineData("not-found", false)]
    [InlineData("not-found", true)]
    [InlineData("unavailable", false)]
    [InlineData("commit-id", false)]
    [InlineData("before-hash", false)]
    [InlineData("operation-id", false)]
    [InlineData("plan-hash", false)]
    [InlineData("document-id", false)]
    [InlineData("schema", false)]
    [InlineData("display-name", false)]
    [InlineData("json", false)]
    [InlineData("after-hash", false)]
    [InlineData("revision", false)]
    [InlineData("updated-at", false)]
    public async Task UnconfirmedCommitRemainsUnknownWithoutResubmittingOrPublishingCandidate(
        string lookupFault, bool useDraftPlan)
    {
        var original = CreateDocument(StorageSystemKind.Simulation);
        var active = original;
        var connection = new LostCommitReplyConnection(lookupFault);
        var repository = new AgentBackedStorageSystemRepository(connection);
        await repository.SaveSimulationAsync(active);
        var coordinator = new SimulationEditCoordinator(
            () => active,
            async (commit, token) =>
            {
                await repository.SaveEditAsync(
                    commit.Document, commit.Plan, commit.Events, token, commit.CommitId);
                active = commit.Document;
            },
            new SimulationOperationService());

        var result = await ExecuteRenameAsync(coordinator, "Committed remotely", useDraftPlan);

        Assert.Equal(ApplicationStatus.OutcomeUnknown, result.Status);
        Assert.Equal("simulation.commit.outcome_unknown", Assert.Single(result.Messages).Code);
        Assert.Null(result.Value);
        Assert.Same(original, active);
        Assert.Equal("Committed remotely",
            SimulationDocumentCodec.Decode(connection.Current!).Snapshot.PhysicalDisks.Single().FriendlyName);
        Assert.Collection(connection.Requests,
            request => Assert.IsType<SaveAgentSimulationDocumentRequest>(request),
            request => Assert.IsType<CommitAgentSimulationEditRequest>(request),
            request => Assert.IsType<LookupAgentSimulationCommitRequest>(request));
    }

    private static Task<ApplicationResult<SimulationEditReceipt>> ExecuteRenameAsync(
        SimulationEditCoordinator coordinator, string name, bool useDraftPlan)
    {
        var request = new SimulationEditRequest(SimulationEditKind.Rename, "physical:p1", Name: name);
        return useDraftPlan
            ? coordinator.ExecutePlanAsync(new SimulationDraftPlan("rename", [request]), CancellationToken.None)
            : coordinator.ExecuteAsync(request, CancellationToken.None);
    }

    // Simulate Agent persistence and transport replies; client planning, execution and reconciliation are real.
    private sealed class LostCommitReplyConnection(string lookupFault = "none") : IAgentConnection
    {
        private CommitAgentSimulationEditRequest? firstCommit;
        public SimulationDocumentPayload? Current { get; private set; }
        public List<AgentRequest> Requests { get; } = [];

        public Task<ApplicationResult<AgentHandshake>> ConnectAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<AgentEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApplicationResult<AgentResponse>> SendAsync(
            AgentRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            AgentResponse response;
            switch (request)
            {
                case SaveAgentSimulationDocumentRequest save:
                    Assert.Equal(Current?.Sha256, save.ExpectedPreviousSha256);
                    Current = save.Document;
                    response = new SimulationDocumentSavedResponse(Current);
                    break;
                case CommitAgentSimulationEditRequest commit:
                    Assert.Equal(Current!.Sha256, commit.ExpectedPreviousSha256);
                    Assert.Equal(Current.Revision + 1, commit.Document.Revision);
                    Current = commit.Document;
                    if (firstCommit is null)
                    {
                        firstCommit = commit;
                        // The Agent persisted the edit, but its reply never reached the client.
                        return Task.FromResult(ApplicationResult<AgentResponse>.FromStatus(
                            ApplicationStatus.OutcomeUnknown, request.CorrelationId));
                    }
                    response = new SimulationDocumentSavedResponse(Current);
                    break;
                case LookupAgentSimulationCommitRequest lookup:
                    Assert.NotNull(firstCommit);
                    Assert.Equal(firstCommit.CommitId, lookup.CommitId);
                    Assert.Equal(firstCommit.Document.DocumentId, lookup.DocumentId);
                    Assert.Equal(firstCommit.ExpectedPreviousSha256, lookup.ExpectedBeforeSha256);
                    Assert.Equal(firstCommit.Document.Sha256, lookup.ExpectedAfterSha256);
                    Assert.Equal(firstCommit.Document.Revision, lookup.ExpectedRevision);
                    Assert.Equal(firstCommit.Plan.OperationId.Value.ToString("N"), lookup.OperationId);
                    Assert.Equal(firstCommit.Plan.PlanHash, lookup.PlanHash);
                    if (lookupFault == "unavailable")
                    {
                        return Task.FromResult(ApplicationResult<AgentResponse>.FromStatus(
                            ApplicationStatus.Failed, request.CorrelationId));
                    }
                    response = lookupFault == "not-found"
                        ? new SimulationCommitLookupResponse(false, null)
                        : new SimulationCommitLookupResponse(true, CreateReceipt(firstCommit));
                    break;
                default:
                    throw new NotSupportedException($"Unexpected request: {request.GetType().Name}");
            }
            return Task.FromResult(ApplicationResult<AgentResponse>.Succeeded(response, request.CorrelationId));
        }

        private SimulationCommitReceipt CreateReceipt(CommitAgentSimulationEditRequest commit)
        {
            var receipt = new SimulationCommitReceipt(
                commit.CommitId, commit.Plan.OperationId.Value.ToString("N"),
                commit.ExpectedPreviousSha256, commit.Plan.PlanHash, commit.Document,
                DateTimeOffset.FromUnixTimeSeconds(1_800_000_100));
            return lookupFault switch
            {
                "none" => receipt,
                "commit-id" => receipt with { CommitId = "other-commit" },
                "before-hash" => receipt with { BeforeSha256 = "other-before-hash" },
                "operation-id" => receipt with { OperationId = Guid.Empty.ToString("N") },
                "plan-hash" => receipt with { PlanHash = "other-plan-hash" },
                "document-id" => receipt with { Document = commit.Document with { DocumentId = "simulation:other" } },
                "schema" => receipt with { Document = commit.Document with { DocumentSchemaVersion = commit.Document.DocumentSchemaVersion + 1 } },
                "display-name" => receipt with { Document = commit.Document with { DisplayName = "Other" } },
                "json" => receipt with { Document = commit.Document with { Json = "{}" } },
                "after-hash" => receipt with { Document = commit.Document with { Sha256 = "other-after-hash" } },
                "revision" => receipt with { Document = commit.Document with { Revision = commit.Document.Revision + 1 } },
                "updated-at" => receipt with { Document = commit.Document with { UpdatedAtUtc = commit.Document.UpdatedAtUtc.AddSeconds(1) } },
                _ => throw new NotSupportedException(lookupFault)
            };
        }
    }

    private static StorageSystemDocument CreateDocument(StorageSystemKind kind)
    {
        var snapshot = new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion,
            "test",
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            new ComputerInfo(
                "system:test",
                "TEST-PC",
                "Windows",
                "10.0",
                "19045",
                DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)),
            [new StorageSubsystemInfo("subsystem:1", "Storage Spaces", "Healthy", "OK")],
            [
                new PhysicalDiskInfo(
                    "physical:p1", true, "Disk One", "Model", "SERIAL-123", "SATA", "SSD",
                    1_000_000_000, 512, 4096, "Healthy", "OK", true, string.Empty, 5,
                    false, false, false, false, "pool:primordial")
            ],
            [
                new StoragePoolInfo(
                    "pool:primordial", true, "Primordial", true, "Healthy", "OK",
                    1_000_000_000, 0, "subsystem:1", ["physical:p1"])
            ],
            [],
            [],
            [
                new OsDiskInfo(
                    "osdisk:5", "Disk One", 5, "RAW", 1_000_000_000,
                    false, false, false, "physical:p1", null)
            ],
            [],
            [],
            [],
            [],
            []);
        return new StorageSystemDocument(
            StorageSystemDocument.CurrentSchemaVersion,
            kind == StorageSystemKind.Local ? "local:test" : "simulation:test",
            kind,
            "Test",
            snapshot,
            [],
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_001));
    }

    private sealed class ThrowingSimulationEditor : ISimulationOperationService
    {
        public SimulationOperationResult Apply(
            StorageSystemDocument document,
            SimulationEditRequest request) =>
            throw new InvalidOperationException("The simulation editor must not be invoked.");

        public SimulationOperationResult ApplyPlan(
            StorageSystemDocument document,
            SimulationDraftPlan plan) =>
            throw new InvalidOperationException("The simulation editor must not be invoked.");
    }
}
