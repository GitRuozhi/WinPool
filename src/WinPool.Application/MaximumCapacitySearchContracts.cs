using WinPool.Execution;

namespace WinPool.Application;

/// <summary>The accepted parent step owns the search; Query/restart never invokes this writer.</summary>
public interface IMaximumCapacitySearchBackend
{
    Task<RealStepResult> ExecuteMaximumCapacitySearchAsync(OperationPlan plan, RealOperationStep step,
        RealStepPreflight preflight, IMaximumCapacityAttemptJournal journal, CancellationToken cancellationToken);

    // Snapshot only: recovery cannot acquire a journal writer or issue another attempt.
    Task<RealReconciliationResult> ReconcileMaximumCapacitySearchAsync(OperationPlan plan,
        RealOperationStep step, RealOperationStepProgress parent,
        IReadOnlyList<MaximumCapacityAttemptRecord> attempts, CancellationToken cancellationToken) =>
        Task.FromResult(new RealReconciliationResult(RealOperationState.OutcomeUnknown, [parent],
            "operation.maximum.recovery_requires_evidence", false));
}

public enum MaximumCapacityAttemptPhase { Create, Resize, Seed }
public enum MaximumCapacityAttemptState
{
    PreparingCall, CallIssued, Verified, CapacityRejectedUnchanged, FailedWithoutCall, OutcomeUnknown
}

/// <summary>Ordinal is global within one parent step; SearchTargetKey identifies a frozen layer/VD slot.</summary>
public sealed record MaximumCapacityAttempt(
    int Ordinal, string SearchTargetKey, MaximumCapacityAttemptPhase Phase,
    long CandidateBytes, long LastSuccessfulBytes, string TargetEvidenceJson,
    string BeforeFingerprint, string PhysicalMemberFingerprint, string BeforeEvidenceJson);

/// <summary>Provider errors alone cannot establish CapacityRejectedUnchanged; the backend supplies strict fresh proof.</summary>
public sealed record MaximumCapacityAttemptResult(
    MaximumCapacityAttemptState State, long LastSuccessfulBytes, string Code, string ResultEvidenceJson,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, long>? SeedSuccessfulBytes = null);

public sealed record MaximumCapacityAttemptRecord(
    MaximumCapacityAttempt Attempt, MaximumCapacityAttemptState State,
    MaximumCapacityAttemptResult? Result, DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Bound to one operation/parent step. Every mutation is a transactional compare-and-swap.
/// False means no permission to issue another Windows call. An issued attempt is never replayed.
/// </summary>
public interface IMaximumCapacityAttemptJournal
{
    bool IsStopRequested { get; }
    Task<IReadOnlyList<MaximumCapacityAttemptRecord>> ReadAsync(CancellationToken cancellationToken);
    Task<bool> PrepareAsync(MaximumCapacityAttempt attempt, CancellationToken cancellationToken);
    Task<bool> MarkCallIssuedAsync(int ordinal, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(int ordinal, MaximumCapacityAttemptResult result, CancellationToken cancellationToken);
}
