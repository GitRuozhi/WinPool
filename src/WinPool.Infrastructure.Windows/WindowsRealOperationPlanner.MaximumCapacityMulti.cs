using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed partial class WindowsRealOperationPlanner
{
    private async Task<OperationPlan> PrepareMultiMaximumCapacityAsync(RealOperationIntentRequest proposal,
        TrustedRealSession session, OperationId operationId, WindowsRealStorageTopology topology, CancellationToken ct)
    {
        var step = proposal.Steps.Single();
        var command = (CreateTieredVirtualDiskCommand)step.Command;
        var closure = RequireMultiMaximumClosure(topology, proposal.Targets, command);
        var tiers = new List<MaximumCapacityTier>();
        var media = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in command.CapacityTiers!)
        {
            var id = item.Tier.Existing ?? throw new InvalidDataException("MAX needs an existing exact template.");
            var tier = topology.Snapshot.StorageTiers.Single(value => value.StableId == id.ProviderKey);
            if (!media.Add(tier.MediaType) || tier.PoolStableId != closure.PoolId || tier.VirtualDiskStableId is not null
                || tier.Size != 0 || tier.ResiliencySettingName != "Simple" || tier.NumberOfColumns != 1 || tier.Interleave != 65536
                || tier.MediaType is not ("HDD" or "SSD")) throw new InvalidDataException("Multi MAX needs one exact unused Simple/1col/64K template per media type.");
            var eligible = closure.Members.Where(member => topology.Snapshot.PhysicalDisks.Single(value => value.StableId == member.PhysicalDiskId).MediaType == tier.MediaType).ToArray();
            if (eligible.Length == 0) throw new InvalidDataException("Template has no approved physical member of its exact media type.");
            var physicalBytes = eligible.Aggregate(0L, (sum, member) => checked(sum + member.SizeBytes));
            var range = await capabilities.ReadTierCreationSizeAsync(topology, id, ct).ConfigureAwait(false);
            var singleView = new RealTargetClosure(eligible[0].PhysicalDiskId, closure.Objects, closure.Fingerprint, closure.PhysicalMemberFingerprint);
            var policy = FreezeMaximumCapacityPolicy(range, physicalBytes, topology, singleView, "MSFT_StorageTier.GetSupportedSize(Simple); exact-approved-media-member-set");
            tiers.Add(new(item.Tier, policy));
        }
        if (topology.Snapshot.VirtualDisks.Any(item => item.PoolStableId == closure.PoolId)
            || topology.Snapshot.StorageTiers.Count(item => item.PoolStableId == closure.PoolId) != tiers.Count)
            throw new InvalidDataException("Multi MAX requires only the complete frozen template set and no virtual disks.");
        var normalized = command with { SizeBytes = 0, MaximumCapacity = tiers[0].MaximumCapacity, CapacityTiers = tiers };
        _ = await ValidateMultiMaximumSafetyAsync(topology, closure, normalized, ct).ConfigureAwait(false);
        var normalizedStep = step with { Command = normalized,
            BeforeCondition = "Exact concrete pool and complete approved physical-member/template set, no virtual disks.",
            AfterCondition = "One Simple/Fixed/1col/64K VD; each actual tier has its own observed integer-GiB unchanged-capacity boundary; OS bytes equal sum of actual tiers.",
            DataLoss = "No existing partition or volume is changed; failed or interrupted search can retain its exact RAW VD for read-only reconciliation.",
            SupportEvidence = JsonSerializer.Serialize(new { Algorithm = MaximumCapacityAlgorithm.Version, closure.PhysicalMemberFingerprint, CapacityTiers = tiers }) };
        var trusted = new RealOperationIntentRequest(proposal.Intent, topology.SystemId, proposal.Targets, [normalizedStep], normalizedStep.AfterCondition);
        RealOperationValidator.Validate(trusted);
        var now = timeProvider.GetUtcNow();
        var environment = new EnvironmentProfile(EnvironmentId.New(), EnvironmentKind.LocalMachine, topology.MachineBinding,
            ExecutionCapability.ReadInventory | ExecutionCapability.MutateStorageStructure, false, now);
        return RealOperationPlanFactory.Create(trusted, operationId, environment, session, closure.Fingerprint, closure.Fingerprint,
            closure.PhysicalMemberFingerprint, "Windows exact approved physical-member-set integer-GiB MAX", now,
            now.Add(InMemoryOperationAuthority.DefaultLifetime));
    }

    internal static RealExactPhysicalMemberSetClosure RequireMultiMaximumClosure(WindowsRealStorageTopology topology,
        IReadOnlyList<StorageObjectId> targets, CreateTieredVirtualDiskCommand command)
    {
        if (command is not { UseMaximumSize: true, CreationMechanism: TieredVirtualDiskCreationMechanism.ExactTemplate, CapacityTiers.Count: > 1 }
            || command.Pool.Existing is not { Kind: StorageObjectKind.StoragePool } pool
            || command.CapacityTiers.Any(item => item.Tier.Existing is not { Kind: StorageObjectKind.StorageTier }))
            throw new InvalidDataException("A multi MAX plan requires exact existing pool/templates.");
        var members = targets.Where(item => item.Kind == StorageObjectKind.PhysicalDisk).ToArray();
        if (members.Length == 0 || targets.Any(item => item.System != topology.SystemId)) throw new InvalidDataException("Approved member identities missing.");
        var closure = topology.RequireExactPhysicalMemberSet(pool, members);
        if (closure.Objects.Count(item => item.ObjectType == FactObjectType.StorageTier) < 2
            || command.CapacityTiers.Any(tier => !closure.Objects.Any(item => item.Id == tier.Tier.Existing!.Value.ProviderKey && item.ObjectType == FactObjectType.StorageTier)))
            throw new InvalidDataException("The complete frozen multi-template set is absent from the exact closure.");
        if (targets.Any(target => !closure.Objects.Any(item => item.Id == target.ProviderKey)))
            throw new InvalidDataException("A declared target is outside the exact approved pool component.");
        return closure;
    }

    internal async Task<WindowsRealStorageSafetyEvidence?> ValidateMultiMaximumSafetyAsync(WindowsRealStorageTopology topology,
        RealExactPhysicalMemberSetClosure closure, RealStorageCommand command, CancellationToken ct)
    {
        var safety = await safetyInspector.InspectAsync(topology, closure, command, ct).ConfigureAwait(false);
        if (safety?.PhysicalMemberRoleEvidence is not { } roles || roles.Count != closure.Members.Count
            || !roles.Select(item => item.PhysicalStableId).Order(StringComparer.Ordinal)
                .SequenceEqual(closure.Members.Select(item => item.PhysicalDiskId).Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Complete exact physical-member role proof missing.");
        foreach (var member in closure.Members)
        {
            var expected = member.PoolMemberRoleEvidence;
            var role = roles.Single(item => item.PhysicalStableId == member.PhysicalDiskId);
            if (role.InventoryVersion != topology.InventoryVersion || role.InventoryVersion != expected.InventoryVersion
                || role.PoolStableId != closure.PoolId || role.PoolStableId != expected.PoolStableId
                || role.VerificationMethod != "CompleteCurrentPoolAndOsDiskAssociations"
                || role.IsBoot || role.IsSystem || role.IsPageFile || role.IsCrashDump
                || expected.IsBoot || expected.IsSystem || expected.IsPageFile || expected.IsCrashDump
                || !role.AssociatedOsDiskIds.Order(StringComparer.Ordinal).SequenceEqual(expected.AssociatedOsDiskIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new InvalidDataException("Exact current per-member Windows role proof differs from source closure.");
        }
        return safety;
    }
}

