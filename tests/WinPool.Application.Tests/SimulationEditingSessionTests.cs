namespace WinPool.Application.Tests;

public sealed class SimulationEditingSessionTests
{
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
}
