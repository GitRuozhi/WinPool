namespace WinPool.Application.Tests;

public sealed class SimulationEditingSessionTests
{
    [Fact]
    public void BaselineSourceFactsFollowVerifiedRebaseRenameAndPreviewWithoutReseeding()
    {
        var document = Document();
        var session = new SimulationEditingSession();
        session.Bind(document, document.Snapshot);
        Assert.Same(document.SourceFacts, session.BaselineSourceFacts);
        var refreshed = document with { SourceFacts = document.SourceFacts! with { InventoryVersion = "verified-refresh" } };
        session.RebaseVerified(refreshed, session.Capture());
        Assert.Same(refreshed.SourceFacts, session.BaselineSourceFacts);
        Assert.Same(refreshed.SourceFacts, session.ForkPreview(session.Capture()).BaselineSourceFacts);
        var renamed = refreshed with { SourceFacts = refreshed.SourceFacts! with { InventoryVersion = "verified-rename" } };
        session.AcceptRename(renamed, renamed.Snapshot.StoragePools[0].StableId);
        Assert.Same(renamed.SourceFacts, session.BaselineSourceFacts);
        session.Discard();
        Assert.Same(renamed.SourceFacts, session.BaselineSourceFacts);
    }

    private static StorageSystemDocument Document() => new(StorageSystemDocument.CurrentSchemaVersion,
        "simulation:session", StorageSystemKind.Simulation, "Session", TestSnapshotFactory.Create(),
        [], DateTimeOffset.Now);

    [Fact]
    public void ImmediateRenameSurvivesStructuralUndoRedoAndDiscard()
    {
        var document = Document();
        var session = new SimulationEditingSession();
        session.Bind(document, document.Snapshot);
        var poolId = document.Snapshot.StoragePools[0].StableId;
        session.UndoStack.Push(session.Capture());
        session.MaximumSizeFields.Add(poolId + ":SSD");
        session.PoolIntents[poolId] = new(true, true, "NTFS", 65536, "New volume");
        session.Working = session.Working with { SnapshotVersion = "structural-draft" };
        var rename = new SimulationOperationService().Apply(document, new(SimulationEditKind.Rename, poolId, Name: "Committed name"));
        Assert.True(rename.Succeeded);
        session.AcceptRename(rename.Document with { Revision = document.Revision + 1 }, poolId);
        Assert.True(session.Undo());
        Assert.Empty(session.MaximumSizeFields);
        Assert.Equal("Committed name", session.Working.StoragePools[0].FriendlyName);
        Assert.True(session.Redo());
        Assert.Contains(poolId + ":SSD", session.MaximumSizeFields);
        Assert.Equal("New volume", session.PoolIntents[poolId].VolumeName);
        Assert.Equal("Committed name", session.Working.StoragePools[0].FriendlyName);
        session.Discard();
        Assert.Equal("Committed name", session.Working.StoragePools[0].FriendlyName);
        Assert.Empty(session.UndoStack);
        Assert.Empty(session.RedoStack);
    }

    [Fact]
    public void PreparedPlanRejectsChangedRevisionOrAnotherSystem()
    {
        var document = Document();
        var session = new SimulationEditingSession();
        session.Bind(document, document.Snapshot);
        var plan = session.Prepare(new("test", [new(SimulationEditKind.Rename,
            document.Snapshot.StoragePools[0].StableId, Name: "New name")]));
        var service = new SimulationOperationService();
        Assert.False(service.ApplyPlan(document with { Revision = document.Revision + 1 }, plan).Succeeded);
        Assert.False(service.ApplyPlan(document.AsImportedSimulation(), plan).Succeeded);
        Assert.True(service.ApplyPlan(document, plan).Succeeded);
    }

    [Fact]
    public void AFailedLaterStepReturnsTheOriginalDocumentAndNoCommands()
    {
        var document = Document();
        var plan = new SimulationDraftPlan("two", [
            new(SimulationEditKind.Rename, document.Snapshot.StoragePools[0].StableId, Name: "Candidate"),
            new(SimulationEditKind.Rename, "missing-object", Name: "Rejected")]);
        var result = new SimulationOperationService().ApplyPlan(document, plan);
        Assert.False(result.Succeeded);
        Assert.Same(document, result.Document);
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void PendingPreviewAndExecutionUseTheSameTypedStepsAndQuotedNames()
    {
        var document = Document();
        var plan = SimulationDraftPlanner.Precheck(document.Snapshot,
            [new(SimulationEditKind.Rename, document.Snapshot.StoragePools[0].StableId, Name: "O'Brien 中文")]);
        var result = new SimulationOperationService().ApplyPlan(document, plan);
        Assert.True(result.Succeeded);
        Assert.Equal(plan.DisplayItems.SelectMany(x => x.CommandPreview), result.Commands);
        Assert.Contains("'O''Brien 中文'", Assert.Single(result.Commands));
        Assert.DoesNotContain(document.Snapshot.StoragePools[0].StableId, result.Commands[0]);
    }

    [Fact]
    public void CopyCountChangeCannotBePresentedAsAnInPlaceResize()
    {
        var before = SimulationLayouts.StandardTiered();
        var tier = before.StorageTiers.First();
        var after = before with
        {
            StorageTiers = before.StorageTiers.Select(x => x.StableId == tier.StableId
                ? x with { Size = x.Size + 4294967296, NumberOfDataCopies = (x.NumberOfDataCopies ?? 1) + 1 } : x).ToArray(),
            VirtualDisks = before.VirtualDisks.Select(x => x.TierStableIds.Contains(tier.StableId)
                ? x with { Size = x.Size + 4294967296 } : x).ToArray()
        };
        var preview = Assert.Single(SimulationCommandPreview.Build(
            new(SimulationEditKind.UpdateStoragePool, tier.PoolStableId!), before, after));
        Assert.Contains("requires recreation", preview);
        Assert.DoesNotContain("Resize-StorageTier", preview);
        Assert.DoesNotContain("Resize-VirtualDisk", preview);
    }

    [Fact]
    public void VerifiedStageAdvancesBaselineAndPreservesTheUnexecutedCreationIntent()
    {
        var document = Document();
        var session = new SimulationEditingSession();
        session.Bind(document, document.Snapshot);
        session.UndoStack.Push(session.Capture());
        session.PoolIntents["draft:replacement"] = new(true, true, "NTFS", 65536, "User label",
            PoolVirtualDiskLayout.HddTiered, "User VD", 32L * 1024 * 1024 * 1024, false, false, 'E');
        session.MaximumSizeFields.Add("draft:replacement|HDD");
        session.Working = session.Working with { SnapshotVersion = "remaining-target" };
        var remaining = session.Capture();
        var verified = document.WithCandidate(document.Snapshot with { SnapshotVersion = "verified-delete" })
            with { Revision = document.Revision + 1 };
        session.MarkBaselineConflict(verified);

        session.RebaseVerified(verified, remaining);

        Assert.Equal(verified.Revision, session.BaselineRevision);
        Assert.Equal("verified-delete", session.Baseline.SnapshotVersion);
        Assert.Equal("remaining-target", session.Working.SnapshotVersion);
        var intent = session.PoolIntents["draft:replacement"];
        Assert.Equal(PoolVirtualDiskLayout.HddTiered, intent.Layout);
        Assert.Equal("User VD", intent.VirtualDiskName);
        Assert.Equal(32L * 1024 * 1024 * 1024, intent.VirtualDiskSizeBytes);
        Assert.False(intent.VirtualDiskUseMaximum);
        Assert.False(intent.CreateMsr);
        Assert.Equal('E', intent.DriveLetter);
        Assert.False(session.HasBaselineConflict);
        Assert.Empty(session.UndoStack);
        Assert.Empty(session.RedoStack);
        Assert.Contains("draft:replacement|HDD", session.MaximumSizeFields);
    }

    [Fact]
    public void ExternalRefreshKeepsDraftAndHistoryButBlocksItsOldBaseline()
    {
        var document = Document();
        var session = new SimulationEditingSession();
        session.Bind(document, document.Snapshot);
        session.UndoStack.Push(session.Capture());
        session.Working = session.Working with { SnapshotVersion = "unsaved-target" };
        session.PoolIntents["draft:pool"] = new(true, false, "NTFS", 65536, "Name");
        var prior = session.Capture();

        session.MarkBaselineConflict(document with { Revision = document.Revision + 1 });

        Assert.True(session.HasBaselineConflict);
        Assert.NotEmpty(session.ConflictReason);
        Assert.Same(prior.Snapshot, session.Working);
        Assert.Single(session.UndoStack);
        Assert.Equal(prior.PoolIntents["draft:pool"], session.PoolIntents["draft:pool"]);
        Assert.Equal(document.Revision, session.BaselineRevision);
    }

    [Fact]
    public void VerifiedRebaseCannotCrossSystemOwnershipOrClearTheUnknownWriteBarrier()
    {
        var document = Document();
        var session = new SimulationEditingSession();
        session.Bind(document, document.Snapshot);
        session.OutcomeUnknown = true;
        var remaining = session.Capture();
        Assert.Throws<InvalidOperationException>(() => session.RebaseVerified(document.AsImportedSimulation(), remaining));
        session.RebaseVerified(document, remaining);
        Assert.True(session.OutcomeUnknown);
    }

    [Fact]
    public void RealMemberDragRequiresExplicitDissolutionInTheSameDraft()
    {
        var snapshot = SimulationLayouts.StandardTiered();
        var original = snapshot.StoragePools.First(pool => !pool.IsPrimordial && pool.MemberPhysicalDiskIds.Count > 0);
        var member = original.MemberPhysicalDiskIds.First();
        var document = Document().WithCandidate(snapshot);
        var session = new SimulationEditingSession();
        session.Bind(document, snapshot);
        session.Working = EditWorkspace.InsertDraftPool(snapshot, "Replacement");
        var replacement = session.Working.StoragePools.Single(pool => EditWorkspace.IsDraftPool(pool.StableId));
        Assert.False(session.CanAssignRealDraftMember(member, replacement.StableId));

        session.Working = EditWorkspace.DissolvePoolInWorking(session.Working, original.StableId);
        Assert.True(session.CanAssignRealDraftMember(member, replacement.StableId));
        session.Working = EditWorkspace.MoveDiskToPool(session.Working, member, replacement.StableId);
        var primordial = session.Working.StoragePools.Single(pool => pool.IsPrimordial);
        Assert.True(session.CanAssignRealDraftMember(member, primordial.StableId));
        var other = session.Working.PhysicalDisks.FirstOrDefault(disk => disk.StableId != member);
        if (other is not null) Assert.False(session.CanAssignRealDraftMember(other.StableId, replacement.StableId));
    }

    [Fact]
    public void NewCreationDefaultsRemainOrdinaryAndMaximumWithIndependentPartitionPreference()
    {
        var intent = new PoolEditIntent(true, false, "NTFS", 65536, "Original");
        Assert.Equal(PoolVirtualDiskLayout.Ordinary, intent.Layout);
        Assert.True(intent.VirtualDiskUseMaximum);
        Assert.Null(intent.VirtualDiskSizeBytes);
        Assert.True(intent.AutoCreateVirtualDisk);
        Assert.False(intent.AutoCreatePartition);
    }

    [Fact]
    public void VerifiedVdContinuationMarkerSurvivesRemainingDraftAndDiscardClearsIt()
    {
        var document = Document();
        var session = new SimulationEditingSession();
        session.Bind(document, document.Snapshot);
        session.PoolIntents["pool:1"] = new(true, true, "NTFS", 65536, "User label",
            VirtualDiskName: "User VD", VirtualDiskSizeBytes: 32L * 1024 * 1024 * 1024,
            VirtualDiskUseMaximum: false, VerifiedVirtualDiskId: "verified:new-vd", PendingAutomaticLayout: true);
        session.RebaseVerified(document with { Revision = document.Revision + 1 }, session.Capture());
        Assert.Equal("verified:new-vd", session.PoolIntents["pool:1"].VerifiedVirtualDiskId);
        Assert.True(session.PoolIntents["pool:1"].PendingAutomaticLayout);
        Assert.Equal("User label", session.PoolIntents["pool:1"].VolumeName);
        session.Discard();
        Assert.Empty(session.PoolIntents);
    }

    [Fact]
    public void RealVdPlaceholderDoesNotRequireOrInventTemplateCapacity()
    {
        var snapshot = SimulationLayouts.SingleDiskPool();
        var pool = snapshot.StoragePools.Single(item => !item.IsPrimordial);
        snapshot = snapshot with
        {
            StorageTiers = [new StorageTierInfo("template:unknown-size", true, "HDD template", "HDD", "Simple", 0, 0,
                pool.StableId, null, pool.MemberPhysicalDiskIds, 1, 65536, 1, 0)]
        };
        var draft = SimulationEditingSession.InsertRealDraftVirtualDisk(snapshot, pool.StableId, "Original input");
        var vd = Assert.Single(draft.VirtualDisks);
        Assert.True(EditWorkspace.IsDraftVirtualDisk(vd.StableId));
        Assert.False(vd.IsStable);
        Assert.Equal(0, vd.Size);
        Assert.Equal("Original input", vd.FriendlyName);
        Assert.Contains(draft.FieldIssues, issue => issue.ObjectId == vd.StableId
            && issue.FieldName == nameof(VirtualDiskInfo.Size) && issue.State == FieldReadState.NotCollected);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealDraftRemovalDropsOnlyTheExactVirtualDiskDescendants(bool dissolvePool)
    {
        var source = SimulationLayouts.StandardTiered();
        var virtualDisk = source.VirtualDisks.First();
        var diskIds = source.OsDisks.Where(disk => disk.VirtualDiskStableId == virtualDisk.StableId)
            .Select(disk => disk.StableId).ToHashSet();
        var partitionIds = source.Partitions.Where(partition => diskIds.Contains(partition.OsDiskStableId ?? ""))
            .Select(partition => partition.StableId).ToHashSet();
        Assert.NotEmpty(diskIds);
        Assert.NotEmpty(partitionIds);
        var result = dissolvePool
            ? SimulationEditingSession.RemoveRealPoolDraft(source, virtualDisk.PoolStableId!)
            : SimulationEditingSession.RemoveRealVirtualDiskDraft(source, virtualDisk.StableId);
        Assert.DoesNotContain(result.VirtualDisks, item => item.StableId == virtualDisk.StableId);
        Assert.DoesNotContain(result.OsDisks, item => diskIds.Contains(item.StableId));
        Assert.DoesNotContain(result.Partitions, item => partitionIds.Contains(item.StableId));
        Assert.DoesNotContain(result.Volumes, item => partitionIds.Contains(item.PartitionStableId ?? ""));
        Assert.Equal(source.OsDisks.Where(item => !diskIds.Contains(item.StableId)), result.OsDisks);
        Assert.Equal(source.Partitions.Where(item => !partitionIds.Contains(item.StableId)), result.Partitions);
        Assert.Equal(source.Volumes.Where(item => !partitionIds.Contains(item.PartitionStableId ?? "")), result.Volumes);
        if (dissolvePool)
            Assert.DoesNotContain(result.StoragePools, pool => pool.StableId == virtualDisk.PoolStableId);
        else Assert.Contains(result.StoragePools, pool => pool.StableId == virtualDisk.PoolStableId);
    }

    [Fact]
    public void BindingGenerationSeparatesDiscardedGoalsFromVerifiedContinuation()
    {
        var document = Document();
        var session = new SimulationEditingSession();
        session.Bind(document, document.Snapshot);
        var generation = session.BindingGeneration;
        session.RebaseVerified(document with { Revision = document.Revision + 1 }, session.Capture());
        Assert.Equal(generation, session.BindingGeneration);
        session.Discard();
        Assert.True(session.BindingGeneration > generation);
        generation = session.BindingGeneration;
        session.Bind(document, document.Snapshot);
        Assert.True(session.BindingGeneration > generation);
    }

}
