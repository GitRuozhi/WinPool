using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

public sealed partial class WindowsRealStorageBackend
{
    // A successful provider return with a lost/unverified postcondition can
    // establish an exact residual. It cannot establish MAX or permit replay.
    private static void RequireMaximumPendingOrdinaryCreation(OperationPlan plan, CreateVirtualDiskCommand macro,
        MaximumCapacityAttempt attempt, WindowsMaximumCapacityAttemptEvidence receipt,
        WindowsRealStorageTopology topology, RealTargetClosure closure)
    {
        var poolId = macro.Pool.Existing?.ProviderKey ?? throw new InvalidDataException("Exact pool absent.");
        var target = JsonSerializer.Deserialize<WindowsStorageCommandTarget>(attempt.TargetEvidenceJson)
            ?? throw new InvalidDataException("Exact pending target absent.");
        var before = receipt.Before;
        var physical = before.Objects.Single(item => item.ObjectType == FactObjectType.PhysicalDisk);
        var pool = before.Objects.Single(item => item.ObjectType == FactObjectType.StoragePool && item.Id == poolId);
        if (before.Objects.Count != 2 || topology.MachineBinding != plan.RealOperation!.MachineBinding
            || closure.PhysicalMemberFingerprint != plan.RealOperation.PhysicalMemberFingerprint
            || !target.MaximumCapacityAttempt || target.ExpectedFingerprint != attempt.BeforeFingerprint
            || target.UniqueId != Text(pool, "UniqueId") || target.ObjectId != Text(pool, "ObjectId")
            || target.PhysicalMemberUniqueId != Text(physical, "UniqueId")
            || target.PhysicalMemberObjectId != Text(physical, "ObjectId") || target.SerialNumber != Text(physical, "SerialNumber"))
            throw new InvalidDataException("Pending creation origin or hardware identity differs.");

        var vdObject = closure.Objects.Single(item => item.ObjectType == FactObjectType.VirtualDisk
            && Text(item, "UniqueId") == receipt.Provider.UniqueId && Text(item, "ObjectId") == receipt.Provider.ObjectId);
        var vd = topology.Snapshot.VirtualDisks.Single(item => item.StableId == vdObject.Id);
        var os = topology.Snapshot.OsDisks.Single(item => item.VirtualDiskStableId == vd.StableId);
        var osObject = closure.Objects.Single(item => item.Id == os.StableId && item.ObjectType == FactObjectType.Disk);
        if (closure.Objects.Count != 4 || vd.PoolStableId != poolId || vd.FriendlyName != macro.Name
            || vd.Size != attempt.CandidateBytes || vd.Size <= 0 || vd.Size % MaximumCapacityAlgorithm.GiB != 0
            || os.Size != vd.Size || vd.TierStableIds.Count != 0 || vd.ResiliencySettingName != "Simple"
            || vd.ProvisioningType != "Fixed" || vd.NumberOfColumns != macro.DataColumns || vd.Interleave != macro.InterleaveBytes
            || !ReturnedNumber(vdObject, "AllocatedSize", vd.Size)
            || !ReturnedNumber(vdObject, "FootprintOnPool", vd.Size)
            || string.IsNullOrWhiteSpace(Text(osObject, "UniqueId")) || string.IsNullOrWhiteSpace(Text(osObject, "ObjectId")))
            throw new InvalidDataException("Pending creation residual layout or capacity differs.");

        foreach (var old in before.Objects)
        {
            var current = closure.Objects.Single(item => item.Id == old.Id);
            if (old.SourceIdentity != current.SourceIdentity || old.ObjectType != current.ObjectType)
                throw new InvalidDataException("Pending creation changed an existing identity.");
            bool CapacityField(string name) => name is "SizeRemaining" or "AllocatedSize"
                || old.ObjectType == FactObjectType.PhysicalDisk && name == "VirtualDiskFootprint";
            var previousFields = old.Fields.Where(field => !CapacityField(field.Name)).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            var currentFields = current.Fields.Where(field => !CapacityField(field.Name)).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            if (previousFields.Length != currentFields.Length || previousFields.Where((field, index) =>
                    field.Name != currentFields[index].Name || field.ReadState != currentFields[index].ReadState
                    || field.ValueType != currentFields[index].ValueType
                    || RecordedFieldValue(field.Value) != RecordedFieldValue(currentFields[index].Value)).Any())
                throw new InvalidDataException("Pending creation changed an existing noncapacity field.");
        }
        var expectedEdges = before.Associations.Where(edge => !edge.IsRetained)
            .Select(edge => edge.FromId + "|" + edge.Kind + "|" + edge.ToId)
            .Append(poolId + "|pool-virtual-disk|" + vd.StableId)
            .Append(vd.StableId + "|same-device|" + os.StableId).Order(StringComparer.Ordinal).ToArray();
        var ids = closure.Objects.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var actualEdges = topology.Facts.Relationships.Where(edge => !edge.IsRetained && ids.Contains(edge.FromId) && ids.Contains(edge.ToId))
            .Select(edge => edge.FromId + "|" + edge.Kind + "|" + edge.ToId).Order(StringComparer.Ordinal).ToArray();
        if (!expectedEdges.SequenceEqual(actualEdges, StringComparer.Ordinal))
            throw new InvalidDataException("Pending creation residual associations differ.");
    }

    private static string? RecordedFieldValue(JsonElement? value) =>
        value is null || value.Value.ValueKind == JsonValueKind.Null ? null : JsonSerializer.Serialize(value.Value);
}
