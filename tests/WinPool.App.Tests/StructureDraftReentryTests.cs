using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Tests;

public sealed class StructureDraftReentryTests
{
    private static StorageSystemDocument Document(string id = "simulation:structure-reentry") => new(
        StorageSystemDocument.CurrentSchemaVersion, id, StorageSystemKind.Simulation, id,
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
}
