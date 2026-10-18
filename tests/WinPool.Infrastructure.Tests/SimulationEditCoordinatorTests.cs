using WinPool.Application;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class SimulationEditCoordinatorTests
{
    [Fact]
    public async Task FrozenPreviewMatchesAgentCandidateAndCommitReusesPreviewWithoutRunningRulesAgain()
    {
        var snapshot = SimulationLayouts.PoolWithoutVirtualDisk();
        var document = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion,
            "simulation:deterministic", StorageSystemKind.Simulation, "Deterministic", snapshot, [], DateTimeOffset.UnixEpoch);
        var pool = document.Snapshot.StoragePools.First(item => !item.IsPrimordial);
        var plan = SimulationDraftPlanner.Precheck(document,
            [new SimulationEditRequest(SimulationEditKind.CreateVirtualDisk, pool.StableId,
                Name: "Space", SizeBytes: 8L << 30, CreatePartition: true, CreateMsr: true,
                FileSystem: "NTFS", AllocationUnitSize: 65536)]);
        Assert.NotNull(plan.PreviewDocument);
        var remoteBaseline = SimulationDocumentCodec.Decode(SimulationDocumentCodec.Encode(document));
        var remote = new SimulationOperationService().ReducePlan(remoteBaseline, plan);
        Assert.True(remote.Succeeded, remote.Error);
        var candidate = remote.Document with { Revision = document.Revision + 1 };
        Assert.Equal(SimulationDocumentCodec.Encode(plan.PreviewDocument!).Sha256,
            SimulationDocumentCodec.Encode(candidate).Sha256);
        SimulationEditCommit? committed = null;
        var coordinator = new SimulationEditCoordinator(() => document,
            (commit, _) => { committed = commit; return Task.CompletedTask; }, new ThrowingSimulationEditor());
        var result = await coordinator.ExecutePlanAsync(plan, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(SimulationDocumentCodec.Encode(plan.PreviewDocument!).Sha256, SimulationDocumentCodec.Encode(committed!.Document).Sha256);
    }

    [Fact]
    public async Task TypedEditIsReducedAndCommittedWithoutAnExecutionPlan()
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
        Assert.Equal(result.Value!.PlanHash, structuredCommit.Intent.IntentHash);
        Assert.Single(structuredCommit.Intent.Edits);
        Assert.Equal(
            "Renamed disk",
            committed.Snapshot.PhysicalDisks.Single().FriendlyName);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.PlanHash));
        Assert.Equal("physical:p1", result.Value.Target.ProviderKey);
        Assert.Equal(result.Value.BeforeRevision + 1, result.Value.AfterRevision);
        Assert.Contains("Set-PhysicalDisk -InputObject $targetPhysicalDisk -NewFriendlyName", Assert.Single(result.Value.SimulatedCommands));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FormatModeIsBoundIntoTypedPlanAndPreview(
        bool quickFormat,
        bool expectFullSwitch)
    {
        var active = CreateFormattedDocument();
        var originalFileSystem = Assert.Single(active.Snapshot.Volumes).FileSystem;
        SimulationEditCommit? committed = null;
        var coordinator = new SimulationEditCoordinator(
            () => active,
            (commit, _) =>
            {
                committed = commit;
                active = commit.Document;
                return Task.CompletedTask;
            },
            new SimulationOperationService());

        var partition = Assert.Single(active.Snapshot.Partitions);
        var result = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.FormatPartition,
                partition.StableId,
                FileSystem: "NTFS",
                AllocationUnitSize: 65536,
                QuickFormat: quickFormat),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(committed);
        Assert.Equal(quickFormat, committed!.Intent.Edits.Single().QuickFormat);
        Assert.Equal(
            expectFullSwitch,
            string.Join(Environment.NewLine, result.Value!.SimulatedCommands)
                .Contains(" -Full", StringComparison.Ordinal));
        Assert.Equal(originalFileSystem, Assert.Single(active.Snapshot.Volumes).FileSystem);
    }

    [Fact]
    public async Task LegacyFormatRequestDefaultsToQuickAndOtherActionsOmitFormatMode()
    {
        var active = CreateFormattedDocument();
        SimulationEditCommit? committed = null;
        var coordinator = new SimulationEditCoordinator(
            () => active,
            (commit, _) =>
            {
                committed = commit;
                active = commit.Document;
                return Task.CompletedTask;
            },
            new SimulationOperationService());
        var partition = Assert.Single(active.Snapshot.Partitions);

        var format = await coordinator.ExecuteAsync(
            new SimulationEditRequest(
                SimulationEditKind.FormatPartition,
                partition.StableId,
                FileSystem: "NTFS"),
            CancellationToken.None);

        Assert.True(format.IsSuccess);
        Assert.True(committed!.Intent.Edits.Single().QuickFormat ?? true);
        Assert.DoesNotContain(
            " -Full",
            string.Join(Environment.NewLine, format.Value!.SimulatedCommands),
            StringComparison.Ordinal);

        var rename = await coordinator.ExecuteAsync(
            new SimulationEditRequest(SimulationEditKind.Rename, "physical:p1", Name: "Renamed"),
            CancellationToken.None);

        Assert.True(rename.IsSuccess);
        Assert.Null(committed!.Intent.Edits.Single().QuickFormat);
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

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.False(committed);
        Assert.Equal("simulation.plan-rejected", Assert.Single(result.Messages).Code);
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
    public async Task BuiltInResetUsesTheSameDeterministicReducerAsNormalEdits()
    {
        var active = SimulationCatalog.CreateDocuments()[0] with { DisplayName = "Changed" };
        var coordinator = new SimulationEditCoordinator(() => active,
            (commit, _) => { active = commit.Document; return Task.CompletedTask; }, new SimulationOperationService());
        var result = await coordinator.ExecuteAsync(new(SimulationEditKind.ResetDocument,
            active.Snapshot.Computer.StableId), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal("DESKTOP-PL96UKD", active.DisplayName);
        Assert.Contains("restore the built-in simulation", Assert.Single(result.Value!.SimulatedCommands));
    }

    [Fact]
    public async Task ImportedSimulationCannotUseBuiltInResetContract()
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        var coordinator = new SimulationEditCoordinator(() => active,
            (_, _) => throw new InvalidOperationException("Must not commit"), new SimulationOperationService());
        var result = await coordinator.ExecuteAsync(new(SimulationEditKind.ResetDocument,
            active.Snapshot.Computer.StableId), CancellationToken.None);
        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal("simulation.reset-built-in-only", Assert.Single(result.Messages).Code);
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
                active = await repository.SaveEditAsync(commit.Document, commit.Intent, token);
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
        Assert.Equal(firstCommit.Intent.ExpectedRevision + 1, secondCommit.Intent.ExpectedRevision);
        Assert.False(string.IsNullOrWhiteSpace(firstCommit.Intent.CommitId));
        Assert.NotEqual(firstCommit.Intent.CommitId, secondCommit.Intent.CommitId);
    }

    [Theory]
    [InlineData("not-found", false)]
    [InlineData("not-found", true)]
    [InlineData("unavailable", false)]
    [InlineData("commit-id", false)]
    [InlineData("before-hash", false)]
    [InlineData("intent-hash", false)]
    [InlineData("document-id", false)]
    [InlineData("revision", false)]
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
                active = await repository.SaveEditAsync(commit.Document, commit.Intent, token);
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

    [Fact]
    public async Task LostCommitRetryUsesOriginalBeforeHashAfterCommittedDocumentWasLoaded()
    {
        var original = CreateDocument(StorageSystemKind.Simulation);
        var connection = new LostCommitReplyConnection("not-found");
        var repository = new AgentBackedStorageSystemRepository(connection);
        await repository.SaveSimulationAsync(original);
        const string commitId = "lost-receipt-reload";
        var frozen = SimulationEditIntent.Freeze(new SimulationEditRequest(
            SimulationEditKind.Rename, "physical:p1", Name: "Saved remotely"), commitId, original.UpdatedAt.AddSeconds(1));
        var reduced = new SimulationOperationService().ReducePlan(original, new(commitId, [frozen]));
        Assert.True(reduced.Succeeded, reduced.Error);
        var candidate = reduced.Document with { Revision = original.Revision + 1 };
        var intent = new SimulationCommitIntent(original.Id, original.Revision, commitId, [frozen]);
        await Assert.ThrowsAsync<SimulationCommitOutcomeUnknownException>(
            () => repository.SaveEditAsync(candidate, intent));

        Assert.Equal(candidate.Revision, (await repository.LoadSimulationAsync(original.Id))!.Revision);
        connection.LookupFault = "none";
        Assert.Same(candidate, await repository.SaveEditAsync(candidate, intent));
        Assert.Single(connection.Requests.OfType<CommitAgentSimulationEditRequest>());
        Assert.Equal(2, connection.Requests.OfType<LookupAgentSimulationCommitRequest>().Count());
    }

    [Theory]
    [InlineData(ApplicationStatus.Rejected, "agent.persistence.simulation_commit_conflict", ApplicationStatus.Rejected, "simulation.commit.conflict", false)]
    [InlineData(ApplicationStatus.Rejected, "agent.persistence.simulation_commit_conflict", ApplicationStatus.Rejected, "simulation.commit.conflict", true)]
    [InlineData(ApplicationStatus.Failed, "agent.persistence.simulation_commit_failed", ApplicationStatus.Failed, "simulation.commit.storage_failed", false)]
    [InlineData(ApplicationStatus.Failed, "agent.persistence.simulation_commit_failed", ApplicationStatus.Failed, "simulation.commit.storage_failed", true)]
    [InlineData(ApplicationStatus.Cancelled, "agent.persistence.simulation_commit_cancelled", ApplicationStatus.Cancelled, "simulation.cancelled", false)]
    [InlineData(ApplicationStatus.Cancelled, "agent.persistence.simulation_commit_cancelled", ApplicationStatus.Cancelled, "simulation.cancelled", true)]
    public async Task ConfirmedCommitFailureKeepsItsDistinctResultAndDoesNotPublishCandidate(
        ApplicationStatus agentStatus, string agentCode,
        ApplicationStatus expectedStatus, string expectedCode, bool useDraftPlan)
    {
        var original = CreateDocument(StorageSystemKind.Simulation);
        var active = original;
        var connection = new LostCommitReplyConnection(commitStatus: agentStatus, commitCode: agentCode);
        var repository = new AgentBackedStorageSystemRepository(connection);
        await repository.SaveSimulationAsync(active);
        var coordinator = new SimulationEditCoordinator(
            () => active,
            async (commit, token) =>
            {
                active = await repository.SaveEditAsync(commit.Document, commit.Intent, token);
            },
            new SimulationOperationService());

        var result = await ExecuteRenameAsync(coordinator, "Not saved", useDraftPlan);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedCode, Assert.Single(result.Messages).Code);
        Assert.Same(original, active);
        Assert.Equal(2, connection.Requests.Count);
    }

    [Fact]
    public async Task DraftWithStaleRevisionIsRejectedBeforeExecutionOrCommit()
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        var committed = false;
        var coordinator = new SimulationEditCoordinator(
            () => active,
            (_, _) => { committed = true; return Task.CompletedTask; },
            new SimulationOperationService());
        var plan = new SimulationDraftPlan("stale", [new SimulationEditRequest(
            SimulationEditKind.Rename, "physical:p1", Name: "Old draft")])
        {
            BaselineRevision = active.Revision + 1
        };

        var result = await coordinator.ExecutePlanAsync(plan, CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Equal("simulation.plan.revision_conflict", Assert.Single(result.Messages).Code);
        Assert.False(committed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NumericOverflowCannotEscapeEitherSimulationEntryPoint(bool useDraftPlan)
    {
        var active = CreateDocument(StorageSystemKind.Simulation);
        var committed = false;
        var coordinator = new SimulationEditCoordinator(
            () => active,
            (_, _) => { committed = true; return Task.CompletedTask; },
            new OverflowSimulationEditor());

        var result = await ExecuteRenameAsync(coordinator, "Too large", useDraftPlan);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.False(committed);
    }

    private static Task<ApplicationResult<SimulationEditReceipt>> ExecuteRenameAsync(
        SimulationEditCoordinator coordinator, string name, bool useDraftPlan)
    {
        var request = new SimulationEditRequest(SimulationEditKind.Rename, "physical:p1", Name: name);
        return useDraftPlan
            ? coordinator.ExecutePlanAsync(new SimulationDraftPlan("rename", [request]), CancellationToken.None)
            : coordinator.ExecuteAsync(request, CancellationToken.None);
    }

    private static StorageSystemDocument CreateFormattedDocument()
    {
        var editor = new SimulationOperationService();
        var initialized = editor.Apply(
            CreateDocument(StorageSystemKind.Simulation),
            new SimulationEditRequest(
                SimulationEditKind.InitializeDisk,
                "osdisk:5",
                PartitionStyle: "GPT",
                CreateMsr: false));
        Assert.True(initialized.Succeeded, initialized.Error);
        var created = editor.Apply(
            initialized.Document,
            new SimulationEditRequest(
                SimulationEditKind.CreatePartition,
                "osdisk:5",
                FileSystem: "NTFS",
                AllocationUnitSize: 4096,
                SizeBytes: 500L * 1024 * 1024));
        Assert.True(created.Succeeded, created.Error);
        return created.Document;
    }

    // The fake Agent runs the production reducer; the first persisted reply is lost.
    private sealed class LostCommitReplyConnection(string lookupFault = "none",
        ApplicationStatus? commitStatus = null, string? commitCode = null) : IAgentConnection
    {
        private SimulationCommitReceipt? firstReceipt;
        public string LookupFault { get; set; } = lookupFault;
        public SimulationDocumentPayload? Current { get; private set; }
        public List<AgentRequest> Requests { get; } = [];
        public Task<ApplicationResult<AgentHandshake>> ConnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentEvent> WatchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApplicationResult<AgentResponse>> SendAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            AgentResponse response;
            switch (request)
            {
                case SaveAgentSimulationDocumentRequest save:
                    Assert.Equal(Current?.Sha256, save.ExpectedPreviousSha256);
                    Current = save.Document;
                    response = new SimulationDocumentSavedResponse(new(Current.DocumentId, Current.DocumentSchemaVersion, Current.DisplayName, Current.Sha256, Current.Revision, Current.UpdatedAtUtc));
                    break;
                case LoadAgentSimulationDocumentRequest:
                    response = new SimulationDocumentLoadedResponse(Current);
                    break;
                case CommitAgentSimulationEditRequest commit:
                    Assert.Equal(Current!.Revision, commit.Intent.ExpectedRevision);
                    if (commitStatus is { } status)
                        return Task.FromResult(ApplicationResult<AgentResponse>.FromStatus(status, request.CorrelationId,
                            new ApplicationMessage(commitCode!, commitCode!, string.Empty, ApplicationMessageSeverity.Error, [])));
                    var before = Current;
                    var reduced = new SimulationOperationService().ReducePlan(SimulationDocumentCodec.Decode(before),
                        new SimulationDraftPlan(commit.Intent.CommitId, commit.Intent.Edits));
                    Assert.True(reduced.Succeeded, reduced.Error);
                    Current = SimulationDocumentCodec.Encode(reduced.Document with { Revision = before.Revision + 1 });
                    var receipt = new SimulationCommitReceipt(commit.Intent.CommitId, Current.DocumentId,
                        before.Revision, Current.Revision, before.Sha256, Current.Sha256,
                        commit.Intent.IntentHash, DateTimeOffset.UtcNow);
                    if (firstReceipt is null)
                    {
                        firstReceipt = receipt;
                        return Task.FromResult(ApplicationResult<AgentResponse>.FromStatus(ApplicationStatus.OutcomeUnknown, request.CorrelationId));
                    }
                    response = new SimulationEditCommittedResponse(receipt);
                    break;
                case LookupAgentSimulationCommitRequest lookup:
                    Assert.NotNull(firstReceipt);
                    Assert.Equal(firstReceipt.CommitId, lookup.CommitId);
                    Assert.Equal(firstReceipt.DocumentId, lookup.DocumentId);
                    Assert.Equal(firstReceipt.IntentHash, lookup.IntentHash);
                    if (LookupFault == "unavailable")
                        return Task.FromResult(ApplicationResult<AgentResponse>.FromStatus(ApplicationStatus.Failed, request.CorrelationId));
                    response = LookupFault == "not-found" ? new SimulationCommitLookupResponse(false, null)
                        : new SimulationCommitLookupResponse(true, LookupFault switch
                        {
                            "none" => firstReceipt,
                            "commit-id" => firstReceipt with { CommitId = "other" },
                            "before-hash" => firstReceipt with { BeforeSha256 = "other" },
                            "intent-hash" => firstReceipt with { IntentHash = "other" },
                            "document-id" => firstReceipt with { DocumentId = "simulation:other" },
                            "revision" => firstReceipt with { AfterRevision = firstReceipt.AfterRevision + 1 },
                            _ => throw new NotSupportedException(LookupFault)
                        });
                    break;
                default: throw new NotSupportedException(request.GetType().Name);
            }
            return Task.FromResult(ApplicationResult<AgentResponse>.Succeeded(response, request.CorrelationId));
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

    private sealed class OverflowSimulationEditor : ISimulationOperationService
    {
        public SimulationOperationResult Apply(
            StorageSystemDocument document,
            SimulationEditRequest request) => throw new OverflowException();

        public SimulationOperationResult ApplyPlan(
            StorageSystemDocument document,
            SimulationDraftPlan plan) => throw new OverflowException();
    }
}
