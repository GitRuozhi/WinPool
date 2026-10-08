using WinPool.Domain;

namespace WinPool.Application;

public enum MonitorCounterSource { PhysicalDisk, StorageSpacesVirtualDisk }
public enum MonitorEditTargetStatus { Editing, PendingVerification, Restored, RemovedByEdit, NeedsSelection }

public sealed record MonitorEditTargetState(SystemId SystemId, StorageObjectId TargetId, OperationId OperationId,
    string StepId, MonitorEditTargetStatus Status, string ReasonCode, DateTimeOffset ObservedAtUtc,
    string? CounterIdentity = null, StorageObjectId? SuccessorId = null);

public sealed record MonitorEditGap(string GapId, SessionId SessionId, SystemId SystemId, StorageObjectId TargetId,
    string CounterIdentity, OperationId OperationId, string StepId, DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc, MonitorEditTargetStatus Status, string ReasonCode);

public sealed record MonitorTargetAvailability(SessionId SessionId, StorageObjectId TargetId,
    string CounterIdentity, bool Available, DateTimeOffset ObservedAtUtc);

public sealed record MonitorTargetResolution(SystemId SystemId, WinPoolFacts Facts,
    IReadOnlyList<MonitorTarget> Targets, IReadOnlyList<MonitorEditTargetState> UnresolvedTargets);

/// <summary>Called only from durable Agent operation state; App intent is not an edit notification.</summary>
public interface IRealStorageEditObserver
{
    Task ObserveAsync(AgentRealOperationResponse response, CancellationToken cancellationToken);
}

public interface IMonitorTargetIdentityResolver
{
    Task<MonitorTargetResolution> ResolveAsync(MonitorRequest request, StorageInventoryScope? scope, CancellationToken cancellationToken);
}

public interface IRebindableMonitorSource
{
    void SetTargets(SessionId sessionId, IReadOnlyList<MonitorTarget> targets);
    void ReleaseTargets(SessionId sessionId);
    void SetAvailabilityObserver(Func<MonitorTargetAvailability, CancellationToken, Task> observer);
}

public interface IMonitorEditGapPersistence
{
    Task SaveEditGapAsync(MonitorEditGap gap, CancellationToken cancellationToken);
}

public interface IMonitorEditGapHistory
{
    Task<IReadOnlyList<MonitorEditGap>> LoadOpenEditGapsAsync(CancellationToken cancellationToken);
}
