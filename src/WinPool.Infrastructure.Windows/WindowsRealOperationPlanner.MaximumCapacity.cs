using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed partial class WindowsRealOperationPlanner
{
    private async Task<RealOperationStep> FreezeMaximumCapacityStepAsync(WindowsRealStorageTopology topology,
        RealTargetClosure closure, RealOperationStep step, CancellationToken cancellationToken)
    {
        if (step.Command is not (CreateVirtualDiskCommand { UseMaximumSize: true }
            or CreateTieredVirtualDiskCommand { UseMaximumSize: true })) return step;
        var physical = topology.Snapshot.PhysicalDisks.Single(item => item.StableId == closure.PhysicalDiskId);
        var physicalBytes = physical.Size;
        VirtualDiskCreationSize range;
        RealStorageCommand normalized;
        if (step.Command is CreateVirtualDiskCommand ordinary)
        {
            if (ordinary.Pool.Existing is null)
                throw new NotSupportedException("MAX search must prepare against an existing fresh concrete pool.");
            var target = WindowsRealStorageTargetBuilder.Build(topology, ordinary.Pool, new Dictionary<string, string>());
            range = await virtualDiskSizes.ReadAsync(target, cancellationToken).ConfigureAwait(false);
            normalized = ordinary with { MaximumCapacity = FreezeMaximumCapacityPolicy(range, physicalBytes, topology, closure, "MSFT_StoragePool.GetSupportedSize(Simple)") };
        }
        else
        {
            var tiered = (CreateTieredVirtualDiskCommand)step.Command;
            if (tiered.Pool.Existing is null || tiered.Tier.Existing is null
                || tiered.CreationMechanism != TieredVirtualDiskCreationMechanism.ExactTemplate)
                throw new NotSupportedException("MAX search requires exact existing provider templates and pool.");
            if (tiered.CapacityTiers is { Count: > 1 })
                throw new NotSupportedException("Multi-tier MAX requires its exact approved physical-member-set preflight.");
            range = await capabilities.ReadTierCreationSizeAsync(topology, tiered.Tier.Existing.Value, cancellationToken).ConfigureAwait(false);
            var policy = FreezeMaximumCapacityPolicy(range, physicalBytes, topology, closure, "MSFT_StorageTier.GetSupportedSize(Simple)");
            // An App-supplied policy is never authority. Fresh Agent facts replace it.
            normalized = tiered with { MaximumCapacity = policy,
                CapacityTiers = tiered.CapacityTiers is null ? null : [new(tiered.Tier, policy)] };
        }
        return step with { Command = normalized };
    }

    private MaximumCapacityPolicy FreezeMaximumCapacityPolicy(VirtualDiskCreationSize range, long physicalBytes,
        WindowsRealStorageTopology topology, RealTargetClosure closure, string source)
    {
        const long gib = MaximumCapacityAlgorithm.GiB;
        if (physicalBytes <= gib || range.MaximumBytes <= MaximumCapacityAlgorithm.ReserveBytes
            || range.DivisorBytes <= 0 || gib % range.DivisorBytes != 0
            || range.RangeOriginBytes % range.DivisorBytes != 0)
            throw new NotSupportedException("The fresh provider does not prove the fixed integer-GiB candidate grid.");
        var a = Math.Min(range.MaximumBytes, physicalBytes);
        var initial = MaximumCapacityAlgorithm.InitialCandidateBytes(a);
        if (initial <= 0) throw new NotSupportedException("No positive integer-GiB MAX candidate exists.");
        var units = checked(physicalBytes / gib + (physicalBytes % gib == 0 ? 0 : 1));
        var attempts = checked((int)checked(2 * units + 4));
        return new(MaximumCapacityAlgorithm.Version, a, initial, physicalBytes, attempts,
            JsonSerializer.Serialize(new { Method = source, RawProviderRange = range, InitialUpperBoundBytes = a,
                PhysicalCapacityBytes = physicalBytes, PhysicalStableId = closure.PhysicalDiskId,
                Note = "Creation estimate is the search origin, not an existing-object resize ceiling." }),
            closure.Fingerprint, timeProvider.GetUtcNow());
    }

    private void ValidateMaximumCapacityPolicy(MaximumCapacityPolicy policy,
        WindowsRealStorageTopology topology, RealTargetClosure closure)
    {
        if (policy.AlgorithmVersion != MaximumCapacityAlgorithm.Version || policy.SourceFingerprint != closure.Fingerprint
            || policy.CapturedAtUtc == default || policy.CapturedAtUtc > timeProvider.GetUtcNow().AddSeconds(10)
            || string.IsNullOrWhiteSpace(policy.Source) || policy.PhysicalCapacityBytes !=
                topology.Snapshot.PhysicalDisks.Single(item => item.StableId == closure.PhysicalDiskId).Size
            || policy.UpperBoundBytes > policy.PhysicalCapacityBytes
            || policy.InitialCandidateBytes != MaximumCapacityAlgorithm.InitialCandidateBytes(policy.UpperBoundBytes)
            || policy.InitialCandidateBytes <= 0 || policy.MaximumAttempts <= 0)
            throw new InvalidDataException("Frozen maximum-capacity policy no longer matches the exact initial closure.");
    }
}
