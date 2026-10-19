using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Tests;

public sealed class StructureDraftReentryTests
{
    private static StorageSystemDocument Document(string id = "simulation:structure-reentry",
        StorageSystemKind kind = StorageSystemKind.Simulation) => new(
        StorageSystemDocument.CurrentSchemaVersion, id, kind, id,
        StorageSnapshot.Empty(id), [], DateTimeOffset.UtcNow);

    [Fact]
    public void SameSystemReentryRetainsDraftMaximumIntentAndBothHistoryStacks()
    {
        var document = Document();
        var session = StructureDraftReentry.Enter(null, document, document.Snapshot, false, out _);
        session.UndoStack.Push(session.Capture());
        session.RedoStack.Push(session.Capture());
        session.MaximumSizeFields.Add("draft:pool|HDD");
        session.PoolIntents["draft:pool"] = new(true, true, "NTFS", 65536, "Draft");
        session.Working = EditWorkspace.InsertDraftPool(session.Working, "Draft");
        var actual = StructureDraftReentry.Enter(session, document, document.Snapshot, true, out var rebound);
        Assert.Same(session, actual);
        Assert.False(rebound);
        Assert.Single(actual.UndoStack);
        Assert.Single(actual.RedoStack);
        Assert.Contains("draft:pool|HDD", actual.MaximumSizeFields);
        Assert.Equal("Draft", actual.PoolIntents["draft:pool"].VolumeName);
        Assert.Contains(actual.Working.StoragePools, pool => EditWorkspace.IsDraftPool(pool.StableId));
    }

    [Fact]
    public void OtherSystemGetsAnIsolatedSessionWithoutClearingThePreviousDraft()
    {
        var first = Document();
        var original = StructureDraftReentry.Enter(null, first, first.Snapshot, false, out _);
        original.MaximumSizeFields.Add("pool|HDD");
        var second = Document("simulation:another-system");
        var actual = StructureDraftReentry.Enter(original, second, second.Snapshot, true, out var rebound);
        Assert.NotSame(original, actual);
        Assert.True(rebound);
        Assert.Empty(actual.MaximumSizeFields);
        Assert.Single(original.MaximumSizeFields);
    }

    [Fact]
    public void ChangedSourcePreservesDirtySessionAndMarksConflictButRebindsCleanSession()
    {
        var first = Document();
        var session = StructureDraftReentry.Enter(null, first, first.Snapshot, false, out _);
        session.MaximumSizeFields.Add("pool|HDD");
        var changed = first with { Revision = first.Revision + 1 };
        var actual = StructureDraftReentry.Enter(session, changed, changed.Snapshot, true, out var rebound);
        Assert.Same(session, actual);
        Assert.False(rebound);
        Assert.True(actual.HasBaselineConflict);
        Assert.Single(actual.MaximumSizeFields);
        StructureDraftReentry.Enter(session, changed, changed.Snapshot, false, out rebound);
        Assert.True(rebound);
        Assert.False(session.HasBaselineConflict);
        Assert.Empty(session.MaximumSizeFields);
    }
    [Fact]
    public void HiddenDiscardInvalidatesSavedDirtyFlagAndRebindsNewSourceFacts()
    {
        var original = Document();
        var session = StructureDraftReentry.Enter(null, original, original.Snapshot, false, out _);
        session.Working = EditWorkspace.InsertDraftPool(session.Working, "Discard after navigation");
        var capturedRevision = session.ContentRevision;
        var capturedBinding = session.BindingGeneration;

        // The confirmation finishes after navigation captured a dirty form.
        session.Bind(original, original.Snapshot);
        var refreshed = original with { Revision = original.Revision + 1 };
        var freshWorking = StorageSnapshot.Empty("Fresh source after discard");
        var actual = StructureDraftReentry.Enter(session, refreshed, freshWorking, true,
            out var rebound, capturedRevision, capturedBinding);

        Assert.Same(session, actual);
        Assert.True(rebound);
        Assert.False(actual.HasBaselineConflict);
        Assert.Same(freshWorking, actual.Working);
        Assert.Empty(actual.Working.StoragePools);
        Assert.False(StructureDraftReentry.HasDraftContent(actual));
    }

    [Fact]
    public void HiddenSessionChangesRecomputeRemainingDraftInsteadOfTrustingSavedCleanFlag()
    {
        var original = Document();
        var session = StructureDraftReentry.Enter(null, original, original.Snapshot, false, out _);
        var capturedRevision = session.ContentRevision;
        var capturedBinding = session.BindingGeneration;
        session.Working = EditWorkspace.InsertDraftPool(session.Working, "Remaining draft");
        var refreshed = original with { Revision = original.Revision + 1 };
        var remaining = session.Working;

        var actual = StructureDraftReentry.Enter(session, refreshed, refreshed.Snapshot, false,
            out var rebound, capturedRevision, capturedBinding);

        Assert.Same(session, actual);
        Assert.False(rebound);
        Assert.True(actual.HasBaselineConflict);
        Assert.Same(remaining, actual.Working);
        Assert.True(StructureDraftReentry.HasDraftContent(actual));
    }

    [Fact]
    public void SuccessfulHiddenJobClearsItsSubmittedDraftWithoutChangingTheActiveSimulationDraft()
    {
        var original = Document("local:submitted-job", StorageSystemKind.Local);
        var submitted = StructureDraftReentry.Enter(null, original, original.Snapshot, false, out _);
        submitted.UndoStack.Push(submitted.Capture());
        submitted.Working = EditWorkspace.InsertDraftPool(submitted.Working, "Submitted pool");
        var binding = submitted.BindingGeneration;
        var content = submitted.ContentRevision;
        var jobId = Guid.NewGuid();
        submitted.RealStructureResumeJobId = jobId;

        var selected = Document("simulation:active-during-job");
        var active = StructureDraftReentry.Enter(null, selected, selected.Snapshot, false, out _);
        active.Working = EditWorkspace.InsertDraftPool(active.Working, "Unrelated simulated pool");
        active.UndoStack.Push(active.Capture());
        active.CurrentPlan = new("Unrelated preview", []);
        var activeWorking = active.Working;
        var activeContent = active.ContentRevision;
        var activePlan = active.CurrentPlan;
        var catalog = new StorageSystemCatalog();
        catalog.ReplaceLocal(original);
        catalog.AddSimulation(selected);
        var fresh = original.WithCandidate(StorageSnapshot.Empty("Verified native result")) with
        { Revision = original.Revision + 1, UpdatedAt = original.UpdatedAt.AddSeconds(1) };
        Assert.True(catalog.TryReplaceLocalReport(fresh));
        var status = new AgentRealStructureJobResponse(jobId, "frozen-goal", RealOperationState.Succeeded,
            2, [], ["Verified native result"], null, false, SystemId: original.SystemId);

        Assert.True(submitted.TryRestoreStructureJob(status,
            catalog.Systems.Single(document => document.IsLocal && document.SystemId == submitted.SystemId),
            binding, content, emptyDraft: false));
        // Navigation captured a dirty form before the job completed. Its old
        // capture must not resurrect already executed changes on reentry.
        var restored = StructureDraftReentry.Enter(submitted, fresh, fresh.Snapshot, true,
            out _, content, binding);
        Assert.Same(fresh.Snapshot, restored.Baseline);
        Assert.Same(fresh.Snapshot, restored.Working);
        Assert.False(StructureDraftReentry.HasDraftContent(restored));
        Assert.False(restored.HasBaselineConflict);
        Assert.Null(restored.RealStructureResumeJobId);
        Assert.Empty(restored.UndoStack);
        Assert.Same(selected, catalog.Find(selected.Id));
        Assert.Same(activeWorking, active.Working);
        Assert.Equal(activeContent, active.ContentRevision);
        Assert.Same(activePlan, active.CurrentPlan);
        Assert.Single(active.UndoStack);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("content")]
    [InlineData("job")]
    [InlineData("unreconciled")]
    public void OldSuccessfulJobCannotReplaceANewerOrUnreconciledLocalDraft(string change)
    {
        var original = Document("local:retained-job", StorageSystemKind.Local);
        var session = StructureDraftReentry.Enter(null, original, original.Snapshot, false, out _);
        session.Working = EditWorkspace.InsertDraftPool(session.Working, "Submitted pool");
        var binding = session.BindingGeneration;
        var content = session.ContentRevision;
        var jobId = Guid.NewGuid();
        session.RealStructureResumeJobId = jobId;
        var fresh = original.WithCandidate(StorageSnapshot.Empty("Verified older goal")) with
        { Revision = original.Revision + 1 };
        var status = new AgentRealStructureJobResponse(jobId, "frozen-goal", RealOperationState.Succeeded,
            2, [], [], null, change == "unreconciled", SystemId: original.SystemId);
        if (change == "binding") session.Bind(fresh, fresh.Snapshot);
        if (change is "binding" or "content")
            session.Working = EditWorkspace.InsertDraftPool(session.Working, "Newer user draft");
        if (change == "job") session.RealStructureResumeJobId = Guid.NewGuid();
        if (change == "unreconciled") session.OutcomeUnknown = true;
        var preservedWorking = session.Working;
        var preservedBaseline = session.Baseline;
        var preservedContent = session.ContentRevision;
        var preservedJob = session.RealStructureResumeJobId;

        Assert.False(session.TryRestoreStructureJob(status, fresh, binding, content, emptyDraft: false));
        Assert.Same(preservedWorking, session.Working);
        Assert.Same(preservedBaseline, session.Baseline);
        Assert.Equal(preservedContent, session.ContentRevision);
        Assert.Equal(preservedJob, session.RealStructureResumeJobId);
        Assert.Equal(change == "unreconciled", session.OutcomeUnknown);
    }
}
