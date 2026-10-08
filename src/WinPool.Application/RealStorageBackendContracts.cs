using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Application;

/// <summary>
/// Internal Agent-to-Windows boundary. The caller must hold the real mutation
/// gate; the backend still reads fresh facts and rejects ambiguous targets.
/// </summary>
public interface IRealStorageBackend
{
    Task<RealStructureCreationSupport> ReadStructureCreationSupportAsync(StorageObjectId physicalTarget,
        bool tiered, TrustedRealSession session, CancellationToken cancellationToken) =>
        Task.FromException<RealStructureCreationSupport>(new NotSupportedException("Creation support queries are unavailable."));
    Task<RealVirtualDiskCreationRange> ReadVirtualDiskCreationRangeAsync(
        StorageObjectId target, TrustedRealSession session, CancellationToken cancellationToken) =>
        Task.FromException<RealVirtualDiskCreationRange>(new NotSupportedException("Creation size queries are unavailable."));
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

/// <summary>Fresh creation bounds for one exact pool or unused HDD template; never resize permission.</summary>
public sealed record RealVirtualDiskCreationRange(
    StorageObjectId Target, long MinimumBytes, long MaximumBytes, long DivisorBytes,
    long RangeOriginBytes, IReadOnlyList<long> EnumeratedSizes,
    string TargetFingerprint, DateTimeOffset CapturedAtUtc)
{
    public bool Supports(long bytes) => bytes > 0 && (EnumeratedSizes.Count > 0
        ? EnumeratedSizes.All(size => size > 0) && EnumeratedSizes.Contains(bytes)
        : MinimumBytes > 0 && MaximumBytes >= MinimumBytes && DivisorBytes > 0
          && RangeOriginBytes >= 0 && bytes >= MinimumBytes && bytes <= MaximumBytes
          && bytes >= RangeOriginBytes && (bytes - RangeOriginBytes) % DivisorBytes == 0);

    public long ResolveMaximum()
    {
        if (EnumeratedSizes.Count > 0)
            return EnumeratedSizes.All(size => size > 0) ? EnumeratedSizes.Max()
                : throw new InvalidDataException("The provider enumerated creation sizes are invalid.");
        if (MinimumBytes <= 0 || MaximumBytes < MinimumBytes || DivisorBytes <= 0
            || RangeOriginBytes < 0 || MaximumBytes < RangeOriginBytes)
            throw new InvalidDataException("The provider creation range is incomplete.");
        var result = MaximumBytes - ((MaximumBytes - RangeOriginBytes) % DivisorBytes);
        return Supports(result) ? result : throw new InvalidDataException("The provider range has no legal size.");
    }
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
