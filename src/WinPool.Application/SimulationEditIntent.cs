using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinPool.Application;

/// <summary>The accepted typed intent. IDs and time are frozen before preview or transport.</summary>
public sealed record SimulationCommitIntent(
    string DocumentId,
    long ExpectedRevision,
    string CommitId,
    IReadOnlyList<SimulationEditRequest> Edits)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string IntentHash => SimulationEditIntent.Hash(this);
}

public static class SimulationEditIntent
{
    public static SimulationEditRequest Freeze(SimulationEditRequest edit, string contextId, DateTimeOffset at) => edit with
    {
        FrozenAtUtc = edit.FrozenAtUtc ?? at,
        AllocatedPoolId = edit.AllocatedPoolId ?? StableId("sim:pool", contextId),
        AllocatedVirtualDiskId = edit.AllocatedVirtualDiskId ?? StableId("sim:vdisk", contextId),
        AllocatedOsDiskId = edit.AllocatedOsDiskId ?? StableId("sim:osdisk", contextId),
        AllocatedPartitionId = edit.AllocatedPartitionId ?? StableId("sim:partition", contextId),
        AllocatedVolumeId = edit.AllocatedVolumeId ?? StableId("sim:volume", contextId),
        AllocatedJobId = edit.AllocatedJobId ?? StableId("job", contextId),
        AllocatedMsrId = edit.AllocatedMsrId ?? StableId("sim:msr", contextId)
    };

    public static IReadOnlyList<SimulationEditRequest> Freeze(
        IReadOnlyList<SimulationEditRequest> edits, string contextId, DateTimeOffset at) =>
        edits.Select((edit, index) => Freeze(edit, $"{contextId}:{index}", at)).ToArray();

    public static string StableId(string prefix, string contextId) =>
        $"{prefix}:{Hash(new[] { prefix, contextId })[..32]}";

    public static string Hash<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
}
