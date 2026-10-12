using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Tests;

public sealed class EditorInventoryRefreshStateTests
{
    [Fact]
    public void BackgroundBatchAdvancesAnIdleEditorBeforeItStartsAnotherDraft()
    {
        var (before, fresh) = Documents();
        var session = new SimulationEditingSession();
        session.Bind(before, before.Snapshot);
        var refresh = new EditorInventoryRefreshState();
        refresh.Remember(before);

        Assert.True(refresh.NotifyChangedSameSystem(fresh, () => session.Bind(fresh, fresh.Snapshot)));
        Assert.Equal(fresh.InventoryVersion, session.Baseline.SnapshotVersion);
        Assert.False(session.HasBaselineConflict);
        Assert.False(refresh.HasChanged(fresh));
    }

    [Fact]
    public void BackgroundBatchPreservesADirtyDraftAndUsesTheExistingConflictGate()
    {
        var (before, fresh) = Documents();
        var session = new SimulationEditingSession();
        session.Bind(before, before.Snapshot);
        session.Working = session.Working with { StoragePools = [] };
        var draft = session.Working;
        var refresh = new EditorInventoryRefreshState();
        refresh.Remember(before);

        Assert.True(refresh.NotifyChangedSameSystem(fresh, () => session.MarkBaselineConflict(fresh)));
        Assert.Same(draft, session.Working);
        Assert.True(session.HasBaselineConflict);
    }

    [Fact]
    public void WriteRefreshAndDuplicateOrReentrantObserverReportsNotifyOnlyOnce()
    {
        var (before, fresh) = Documents();
        var refresh = new EditorInventoryRefreshState();
        refresh.Remember(before);
        var calls = 0;
        Assert.True(refresh.NotifyChangedSameSystem(fresh, () =>
        {
            calls++;
            Assert.False(refresh.NotifyChangedSameSystem(fresh, () => calls++));
        }));
        Assert.False(refresh.NotifyChangedSameSystem(fresh, () => calls++));
        Assert.Equal(1, calls);

        // An explicit editor write refresh may consume the batch first instead.
        refresh.Remember(before);
        refresh.Remember(fresh);
        Assert.False(refresh.NotifyChangedSameSystem(fresh, () => calls++));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AnotherSystemRequiresNavigationInsteadOfUpdatingTheOldEditor()
    {
        var (before, fresh) = Documents();
        var refresh = new EditorInventoryRefreshState();
        refresh.Remember(before);
        var other = fresh with { SystemId = SystemId.New() };

        Assert.True(refresh.HasChanged(other));
        Assert.False(refresh.NotifyChangedSameSystem(other, () => Assert.Fail("Wrong editor target.")));
        Assert.True(refresh.IsSameSystem(before));
    }

    private static (StorageSystemDocument Before, StorageSystemDocument Fresh) Documents()
    {
        var system = SystemId.New();
        var snapshot = SimulationLayouts.StandardTiered();
        var facts = WinPoolSimulationFacts.Create(snapshot, system) with { IsSimulation = false };
        var before = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion,
            "local:editor-refresh", StorageSystemKind.Local, "Local", facts, [], facts.InventoryCapturedAt)
            { SystemId = system };
        // Local document Revision can remain zero while a new capture changes
        // InventoryVersion. It must still reach the editor's baseline.
        var newFacts = facts with { Revision = facts.Revision + 1,
            InventoryVersion = facts.InventoryVersion + ":new-capture",
            InventoryCapturedAt = facts.InventoryCapturedAt.AddSeconds(1) };
        var fresh = before with { SourceFacts = newFacts, UpdatedAt = before.UpdatedAt.AddSeconds(1) };
        return (before, fresh);
    }
}
