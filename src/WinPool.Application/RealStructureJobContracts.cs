using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Application;

public sealed record RealStructurePoolGoal(string PoolId, PoolEditIntent Intent);
public sealed record RealStructureJobGoal(SystemId SystemId, StorageSnapshot Working,
    IReadOnlyList<RealStructurePoolGoal> PoolIntents, Guid? ResumeFromJobId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DraftIntentHash = null)
{
    public static RealStructureJobGoal Create(SimulationEditingSession session, RealStructureDraftPlan? draft = null,
        Guid? resumeFromJobId = null) => new(session.SystemId, session.Working,
        session.PoolIntents.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new RealStructurePoolGoal(item.Key, item.Value)).ToArray(), resumeFromJobId,
        ComputeDraftIntentHash(session, draft ?? RealStructureDraftPlanner.Build(session)));

    // Bind the user's original actions and their existing component, not a diff
    // against a later global inventory. No source-fact report crosses IPC here.
    public static string ComputeDraftIntentHash(SimulationEditingSession session, RealStructureDraftPlan draft)
    {
        var baseline = session.Baseline;
        var pools = draft.RemovedPools.Select(item => item.StableId)
            .Concat(draft.RemovedVirtualDisks.Select(item => item.PoolStableId))
            .Concat(draft.RemovedTiers.Select(item => item.PoolStableId))
            .Concat(draft.Creations.Select(item => item.Pool.StableId)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var members = baseline.StoragePools.Where(item => pools.Contains(item.StableId)).SelectMany(item => item.MemberPhysicalDiskIds)
            .Concat(draft.Creations.SelectMany(item => item.PhysicalDiskIds)).ToHashSet(StringComparer.Ordinal);
        var subsystems = baseline.StoragePools.Where(item => pools.Contains(item.StableId)).Select(item => item.SubsystemStableId)
            .Concat(draft.Creations.Select(item => item.Pool.SubsystemStableId)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var vds = baseline.VirtualDisks.Where(item => pools.Contains(item.PoolStableId ?? "")).Select(item => item.StableId).ToHashSet(StringComparer.Ordinal);
        var disks = baseline.OsDisks.Where(item => members.Contains(item.PhysicalDiskStableId ?? "") || vds.Contains(item.VirtualDiskStableId ?? ""))
            .Select(item => item.StableId).ToHashSet(StringComparer.Ordinal);
        var partitions = baseline.Partitions.Where(item => disks.Contains(item.OsDiskStableId ?? ""))
            .Select(item => item.StableId).ToHashSet(StringComparer.Ordinal);
        var content = new
        {
            session.SystemId,
            Subsystems = baseline.StorageSubsystems.Where(item => subsystems.Contains(item.StableId)).OrderBy(item => item.StableId, StringComparer.Ordinal),
            RemovedPools = draft.RemovedPools.Select(item => item.StableId).Order(StringComparer.Ordinal),
            RemovedVirtualDisks = draft.RemovedVirtualDisks.Select(item => item.StableId).Order(StringComparer.Ordinal),
            RemovedTiers = draft.RemovedTiers.Select(item => item.StableId).Order(StringComparer.Ordinal),
            Creations = draft.Creations.OrderBy(item => item.Pool.StableId, StringComparer.Ordinal).Select(item => new
            {
                Pool = item.Pool with { MemberPhysicalDiskIds = item.PhysicalDiskIds.Order(StringComparer.Ordinal).ToArray(),
                    AllocatedSize = 0, HealthStatus = "", OperationalStatus = "" },
                Intent = item.Intent with
                {
                    Tiers = item.Intent.Tiers?.OrderBy(tier => tier.MediaType, StringComparer.Ordinal).ToArray(),
                    MemberUsages = item.Intent.MemberUsages?.OrderBy(member => member.PhysicalDiskId, StringComparer.Ordinal).ToArray()
                },
                NewVirtualDisks = session.Working.VirtualDisks.Where(vd => vd.PoolStableId == item.Pool.StableId
                    && baseline.VirtualDisks.All(existing => existing.StableId != vd.StableId))
                    .OrderBy(vd => vd.StableId, StringComparer.Ordinal).Select(vd => new
                    {
                        vd.StableId, vd.PoolStableId, vd.FriendlyName, vd.ProvisioningType,
                        vd.ResiliencySettingName, vd.NumberOfColumns, vd.Interleave,
                        NumberOfDataCopies = vd.NumberOfDataCopies ?? 1,
                        PhysicalDiskRedundancy = vd.PhysicalDiskRedundancy ?? 0,
                        TierStableIds = vd.TierStableIds.Order(StringComparer.Ordinal)
                    }),
                item.CreatePool, item.CreateVirtualDisk, item.CreateTier, item.ContinueLayout
            }),
            Physical = baseline.PhysicalDisks.Where(item => members.Contains(item.StableId)).OrderBy(item => item.StableId, StringComparer.Ordinal)
                .Select(item => new { item.StableId, item.IsStable, item.FriendlyName, item.Model, item.SerialNumber,
                    item.Size, item.LogicalSectorSize, item.PhysicalSectorSize, item.MediaType, item.PoolStableId,
                    item.IsBoot, item.IsSystem, item.IsPageFile, item.IsCrashDump, Usage = PhysicalDiskUsage.Normalize(item.Usage) }),
            Pools = baseline.StoragePools.Where(item => pools.Contains(item.StableId)).OrderBy(item => item.StableId, StringComparer.Ordinal)
                .Select(item => item with { MemberPhysicalDiskIds = item.MemberPhysicalDiskIds.Order(StringComparer.Ordinal).ToArray(),
                    AllocatedSize = 0, HealthStatus = "", OperationalStatus = "" }),
            Tiers = baseline.StorageTiers.Where(item => pools.Contains(item.PoolStableId ?? "")).OrderBy(item => item.StableId, StringComparer.Ordinal)
                .Select(item => item with { MemberPhysicalDiskIds = item.MemberPhysicalDiskIds.Order(StringComparer.Ordinal).ToArray() }),
            VirtualDisks = baseline.VirtualDisks.Where(item => vds.Contains(item.StableId)).OrderBy(item => item.StableId, StringComparer.Ordinal)
                .Select(item => item with { TierStableIds = item.TierStableIds.Order(StringComparer.Ordinal).ToArray(),
                    OsDiskNumbers = item.OsDiskNumbers.Order().ToArray(), HealthStatus = "", OperationalStatus = "" }),
            Disks = baseline.OsDisks.Where(item => disks.Contains(item.StableId)).OrderBy(item => item.StableId, StringComparer.Ordinal),
            Partitions = baseline.Partitions.Where(item => partitions.Contains(item.StableId)).OrderBy(item => item.StableId, StringComparer.Ordinal)
                .Select(item => item with { SizeRemaining = 0, HealthStatus = "", OperationalStatus = "" }),
            Volumes = baseline.Volumes.Where(item => partitions.Contains(item.PartitionStableId ?? "")).OrderBy(item => item.StableId, StringComparer.Ordinal)
                .Select(item => item with { SizeRemaining = 0, AccessPaths = item.AccessPaths.Order(StringComparer.Ordinal).ToArray(),
                    HealthStatus = "", OperationalStatus = "" })
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content))).ToLowerInvariant();
    }
}

public sealed record PrepareAgentRealStructureJobRequest(RealStructureJobGoal Goal, Guid JobId,
    string ProductSessionId, CorrelationId CorrelationId) : AgentRequest(CorrelationId);
public sealed record AcceptAgentRealStructureJobRequest(Guid JobId, string GoalHash,
    string ProductSessionId, CorrelationId CorrelationId) : AgentRequest(CorrelationId);
public sealed record QueryAgentRealStructureJobRequest(Guid JobId,
    CorrelationId CorrelationId) : AgentRequest(CorrelationId);
public sealed record StopAgentRealStructureJobRequest(Guid JobId, string GoalHash,
    CorrelationId CorrelationId) : AgentRequest(CorrelationId);

public sealed record AgentRealStructureJobResponse(Guid JobId, string GoalHash, RealOperationState State,
    long Revision, IReadOnlyList<OperationId> Children, IReadOnlyList<string> Completed,
    string? Code, bool RequiresReconciliation, RealStructureJobGoal? RemainingGoal = null, SystemId? SystemId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DraftIntentHash = null) : AgentResponse;
public sealed record AgentRealStructureJobStatusEvent(AgentRealStructureJobResponse Status,
    DateTimeOffset OccurredAtUtc) : AgentEvent(OccurredAtUtc);

public partial interface IRealOperationService
{
    Task<ApplicationResult<AgentResponse>> PrepareStructureJobAsync(PrepareAgentRealStructureJobRequest request,
        TrustedRealSession session, CancellationToken cancellationToken) =>
        Task.FromException<ApplicationResult<AgentResponse>>(new NotSupportedException());
    Task<ApplicationResult<AgentResponse>> AcceptStructureJobAsync(AcceptAgentRealStructureJobRequest request,
        TrustedRealSession session, CancellationToken cancellationToken) =>
        Task.FromException<ApplicationResult<AgentResponse>>(new NotSupportedException());
    Task<ApplicationResult<AgentResponse>> QueryStructureJobAsync(QueryAgentRealStructureJobRequest request,
        TrustedRealSession session, CancellationToken cancellationToken) =>
        Task.FromException<ApplicationResult<AgentResponse>>(new NotSupportedException());
    Task<ApplicationResult<AgentResponse>> StopStructureJobAsync(StopAgentRealStructureJobRequest request,
        TrustedRealSession session, CancellationToken cancellationToken) =>
        Task.FromException<ApplicationResult<AgentResponse>>(new NotSupportedException());
}
