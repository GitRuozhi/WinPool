using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Tests;

public sealed class RealStructurePoolabilityObservationTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void EligibilityAndItsReasonRemainObservationsWhenBindingTheOriginalGoal(bool beforeCanPool, bool afterCanPool)
    {
        var system = SystemId.New();
        var snapshot = Snapshot(beforeCanPool);
        var original = Draft(Document(snapshot, system));
        var originalPlan = RealStructureDraftPlanner.Build(original);
        Assert.True(originalPlan.Preview.CanApply, string.Join("; ", originalPlan.Preview.BlockingReasons));
        var goal = RealStructureJobGoal.Create(original, originalPlan);
        var refreshed = Document(snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks.Select(item => item with { CanPool = afterCanPool }).ToArray()
        }, system);
        var rebound = Rebind(goal, refreshed);
        Assert.NotEqual(original.Baseline.PhysicalDisks.Single().CannotPoolReason, rebound.Baseline.PhysicalDisks.Single().CannotPoolReason);
        var plan = RealStructureDraftPlanner.Build(rebound);
        Assert.True(plan.Preview.CanApply, string.Join("; ", plan.Preview.BlockingReasons));
        Assert.Equal(goal.DraftIntentHash, RealStructureJobGoal.ComputeDraftIntentHash(rebound, plan));
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("size")]
    [InlineData("sector")]
    [InlineData("protected-role")]
    [InlineData("media")]
    [InlineData("os-geometry")]
    public void EligibilityRefreshCannotHideChangedHardwareOrStructure(string change)
    {
        var system = SystemId.New();
        var snapshot = Snapshot(false);
        var original = Draft(Document(snapshot, system));
        var goal = RealStructureJobGoal.Create(original);
        var changed = snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks.Select(item => change switch
            {
                "serial" => item with { CanPool = true, SerialNumber = "replacement" },
                "size" => item with { CanPool = true, Size = item.Size - 4096 },
                "sector" => item with { CanPool = true, PhysicalSectorSize = 8192 },
                "protected-role" => item with { CanPool = true, IsBoot = true },
                "media" => item with { CanPool = true, MediaType = "SSD" },
                _ => item with { CanPool = true }
            }).ToArray(),
            OsDisks = snapshot.OsDisks.Select(item => change switch
            {
                "protected-role" => item with { IsBoot = true },
                "os-geometry" => item with { Size = item.Size - 4096 },
                _ => item
            }).ToArray()
        };
        var rebound = Rebind(goal, Document(changed, system));
        var plan = RealStructureDraftPlanner.Build(rebound);
        Assert.False(plan.Preview.CanApply);
        Assert.NotEqual(goal.DraftIntentHash, RealStructureJobGoal.ComputeDraftIntentHash(rebound, plan));
    }

    private static SimulationEditingSession Draft(StorageSystemDocument document)
    {
        const string poolId = EditWorkspace.DraftPrefix + "poolability";
        var snapshot = document.Snapshot;
        var editing = new SimulationEditingSession();
        editing.Bind(document, snapshot with
        {
            PhysicalDisks = snapshot.PhysicalDisks.Select(item => item with { PoolStableId = poolId }).ToArray(),
            StoragePools = snapshot.StoragePools.Append(new(poolId, false, "Requested pool", false, "", "", 0, 0,
                "subsystem", ["physical"])).ToArray()
        });
        editing.PoolIntents.Add(poolId, new(false, false, "NTFS", 65536, "Unused"));
        return editing;
    }

    private static SimulationEditingSession Rebind(RealStructureJobGoal goal, StorageSystemDocument document)
    {
        var editing = new SimulationEditingSession();
        editing.Bind(document, goal.Working);
        foreach (var item in goal.PoolIntents) editing.PoolIntents.Add(item.PoolId, item.Intent);
        return editing;
    }

    private static StorageSystemDocument Document(StorageSnapshot snapshot, SystemId system) =>
        new(StorageSystemDocument.CurrentSchemaVersion, "local:poolability-tests", StorageSystemKind.Local,
            "Poolability", WinPoolSimulationFacts.Create(snapshot, system) with { IsSimulation = false }, [], snapshot.ScannedAt)
        { SystemId = system };

    private static StorageSnapshot Snapshot(bool canPool) => StorageSnapshot.Empty(Environment.MachineName) with
    {
        StorageSubsystems = [new("subsystem", "Storage Spaces", "Healthy", "OK")],
        PhysicalDisks = [new("physical", true, "Fixture", "Model", "SERIAL", "SATA", "HDD", 64L << 30,
            512, 4096, "Healthy", "OK", canPool, canPool ? "" : "Insufficient Capacity", 7,
            false, false, false, false, null, Usage: PhysicalDiskUsage.AutoSelect)],
        StoragePools = [new("primordial", true, "Primordial", true, "Healthy", "OK", 64L << 30, 0, "subsystem", ["physical"])],
        OsDisks = [new("disk", "Fixture", 7, "RAW", 64L << 30, false, false, false, "physical", null)]
    };
}
