using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Application;

/// <summary>
/// Internal Agent-to-Windows boundary. The caller must hold the real mutation
/// gate; the backend still reads fresh facts and rejects ambiguous targets.
/// </summary>
public interface IRealStorageBackend
{
    Task<OperationPlan> PrepareAsync(
        RealOperationIntentRequest proposal,
        TrustedRealSession session,
        OperationId operationId,
        CancellationToken cancellationToken);

    Task<RealPartitionResizeRange> ReadPartitionResizeRangeAsync(
        StorageObjectId partition,
        TrustedRealSession session,
        CancellationToken cancellationToken);

    Task<RealStepPreflight> PreflightStepAsync(
        OperationPlan plan,
        RealOperationStep step,
        IReadOnlyDictionary<string, string> verifiedStepOutputs,
        CancellationToken cancellationToken);

    Task<RealStepResult> ExecuteStepAsync(
        OperationPlan plan,
        RealOperationStep step,
        RealStepPreflight preflight,
        CancellationToken cancellationToken);

    Task<RealReconciliationResult> ReconcileAsync(
        OperationPlan plan,
        IReadOnlyList<RealOperationStepProgress> persistedSteps,
        CancellationToken cancellationToken);
}

/// <summary>Fresh provider limits and the narrower supported geometric intersection.</summary>
public sealed record RealPartitionResizeRange(
    StorageObjectId Partition,
    long CurrentSizeBytes,
    long ProviderMinBytes,
    long ProviderMaxBytes,
    long AllowedMinBytes,
    long AllowedMaxBytes,
    string TargetFingerprint,
    DateTimeOffset CapturedAtUtc,
    string Code);

/// <summary>Reads the current OS identity independently of persisted plans.</summary>
public interface IRealMachineIdentityProvider
{
    Task<string> ReadBindingAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Opaque evidence from a fresh pre-call capture. It is persisted before any
/// Windows invocation and checked again by the fixed adapter before dispatch.
/// </summary>
public sealed record RealStepPreflight(
    string TargetEvidenceJson,
    string InventoryVersion,
    string TargetFingerprint,
    string PhysicalMemberFingerprint);

public enum RealStepOutcome
{
    Verified,
    FailedWithoutEffect,
    OutcomeUnknown
}

public sealed record RealStepResult(
    RealStepOutcome Outcome,
    string Code,
    string ResultEvidenceJson,
    string? CreatedObjectId = null);

public sealed record RealReconciliationResult(
    RealOperationState State,
    IReadOnlyList<RealOperationStepProgress> Steps,
    string Code,
    bool CanReleaseWriteBarrier);
