using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Tests;

public sealed class RealOperationConfirmationFormatterTests
{
    [Theory]
    [InlineData(false, PhysicalDiskUsage.AutoSelect, PhysicalDiskUsage.AutoSelect)]
    [InlineData(false, PhysicalDiskUsage.HotSpare, PhysicalDiskUsage.HotSpare)]
    [InlineData(false, PhysicalDiskUsage.Retired, PhysicalDiskUsage.Retired)]
    [InlineData(true, PhysicalDiskUsage.AutoSelect, "自动选择")]
    [InlineData(true, PhysicalDiskUsage.HotSpare, "热备")]
    [InlineData(true, PhysicalDiskUsage.Retired, "已停用")]
    public void MemberRoleConfirmationAndVerifiedFeedbackNameTheExactMember(bool chinese, string usage, string expectedUsage)
    {
        var system = SystemId.New();
        var snapshot = SimulationLayouts.StandardTiered();
        var pool = snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var selected = snapshot.PhysicalDisks.Single(item => item.StableId == pool.MemberPhysicalDiskIds[^1]);
        var members = pool.MemberPhysicalDiskIds.Select(id => new StorageObjectId(system, StorageObjectKind.PhysicalDisk, id)).ToArray();
        var facts = WinPoolSimulationFacts.Create(snapshot, system);
        var proposal = RealOperationProposalFactory.SetPhysicalDiskUsage(system, members[^1],
            new(system, StorageObjectKind.StoragePool, pool.StableId), members, usage);
        var plan = Freeze(proposal);
        var step = plan.RealOperation!.Steps.Single();
        var status = new AgentRealOperationResponse(plan, RealOperationState.Succeeded,
            [new(step.Id, RealOperationStepState.Verified, "real.step_verified", null, "{}")], null, false);
        var confirmation = RealOperationConfirmationFormatter.Format(plan, chinese, facts);
        var result = RealOperationConfirmationFormatter.FormatStatus(status, chinese, facts);
        Assert.Contains(expectedUsage, confirmation);
        Assert.Contains(selected.SerialNumber, confirmation);
        Assert.Contains(expectedUsage, result);
        Assert.Contains(selected.SerialNumber, result);
        Assert.Contains(chinese ? "已核实完成" : "Verified", result);
        var feedback = RealOperationFeedback.Recovery(status, chinese, facts);
        Assert.Contains(expectedUsage, feedback.Options.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PoolConfirmationNamesEveryMember(bool chinese)
    {
        var system = SystemId.New();
        var snapshot = SimulationLayouts.StandardTiered();
        var pool = snapshot.StoragePools.Single(item => !item.IsPrimordial);
        var members = pool.MemberPhysicalDiskIds.Select(id => new StorageObjectId(system, StorageObjectKind.PhysicalDisk, id)).ToArray();
        var text = RealOperationConfirmationFormatter.Format(Freeze(RealOperationProposalFactory.CreatePoolWithMembers(system, members, "New pool")),
            chinese, WinPoolSimulationFacts.Create(snapshot, system));
        foreach (var member in snapshot.PhysicalDisks.Where(item => pool.MemberPhysicalDiskIds.Contains(item.StableId)))
            Assert.Contains(member.SerialNumber, text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleAndMixedTierConfirmationsShowActualLayoutsAndEachCapacity(bool mixed)
    {
        var system = SystemId.New();
        var pool = new StorageObjectId(system, StorageObjectKind.StoragePool, "pool");
        var members = Enumerable.Range(0, 6).Select(index => new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "member:" + index)).ToArray();
        var mirror = new StorageLayoutParameters("Mirror", 1, 16384, 2, 1);
        var parity = new StorageLayoutParameters("Parity", 3, 32768, 1, 1);
        var first = new MaximumCapacityTier(RealTargetReference.ForExisting(new StorageObjectId(system, StorageObjectKind.StorageTier, "tier:ssd")),
            null, mixed ? null : MaximumCapacityAlgorithm.GiB, mixed, mirror, "SSD");
        MaximumCapacityTier[] tiers = mixed ? [first, new(RealTargetReference.ForExisting(new StorageObjectId(system, StorageObjectKind.StorageTier, "tier:hdd")),
            null, 2 * MaximumCapacityAlgorithm.GiB, false, parity, "HDD")] : [first];
        var plan = Freeze(RealOperationProposalFactory.CreateTieredVirtualDisk(system, pool, members, tiers, "VD"));
        foreach (var chinese in new[] { false, true })
        {
            var text = RealOperationConfirmationFormatter.Format(plan, chinese);
            Assert.Contains("SSD", text);
            Assert.Contains("Mirror/Fixed", text);
            Assert.Contains("16,384 bytes", text);
            Assert.DoesNotContain("Simple/Fixed", text);
            if (mixed)
            {
                Assert.Contains("HDD", text);
                Assert.Contains("Parity/Fixed", text);
                Assert.Contains("32,768 bytes", text);
                Assert.Contains("2,147,483,648 bytes", text);
                Assert.Contains(chinese ? "最大可用容量" : "maximum available capacity", text);
            }
            else Assert.Contains("1,073,741,824 bytes", text);
            var status = new AgentRealOperationResponse(plan, RealOperationState.Succeeded, [], null, false);
            Assert.Contains("Mirror/Fixed", RealOperationConfirmationFormatter.FormatStatus(status, chinese));
        }
    }

    private static OperationPlan Freeze(RealOperationIntentRequest proposal)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new TrustedRealSession(SessionId.New(), "product", Guid.NewGuid().ToString("D"), 42, now, Path.GetFullPath("WinPool.App.exe"), true);
        var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine, "machine",
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now);
        return RealOperationPlanFactory.Create(proposal, OperationId.New(), environment, session, "inventory", "target", "members", "support", now, now.AddMinutes(2));
    }
}
