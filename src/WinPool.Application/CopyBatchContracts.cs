using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinPool.Domain;

namespace WinPool.Application;

public enum CopyBatchState
{
    Pending,
    Running,
    Completed,
    Interrupted,
    Failed
}

public enum CopyBatchEntryState
{
    Pending,
    Copying,
    Completed,
    Conflict,
    Failed,
    FailedFinal
}

public enum CopyBatchRecoveryDecision
{
    Pending,
    AcceptCompletedTarget,
    Conflict
}

public sealed record CopyBatchManifestEntry(
    int Ordinal,
    int BatchNumber,
    string RelativePath,
    long Length,
    long LastWriteTimeUtcTicks,
    FileAttributes Attributes,
    string? Sha256);

public sealed record CopyBatchSegment(
    int BatchNumber,
    long PlannedBytes,
    int PlannedFileCount);

public sealed record CopyBatchManifest(
    TestRunId RunId,
    string StepId,
    string PlanHash,
    string SourceDirectoryIdentity,
    string DestinationDirectoryIdentity,
    long BatchThresholdBytes,
    int MaximumFilesPerBatch,
    IReadOnlyList<CopyBatchManifestEntry> Entries,
    IReadOnlyList<CopyBatchSegment> Batches,
    AlgorithmIdentity Algorithm,
    DateTimeOffset CreatedAtUtc,
    string ManifestHash);

public sealed record CopyBatchEntryCheckpoint(
    TestRunId RunId,
    string StepId,
    int Ordinal,
    CopyBatchEntryState State,
    int Attempts,
    int? LastExitCode,
    string? DiagnosticCode,
    DateTimeOffset UpdatedAtUtc);

public sealed record CopyBatchRecoveryItem(
    int Ordinal,
    string RelativePath,
    CopyBatchRecoveryDecision Decision,
    string Code);

public sealed record CopyBatchRecoveryReport(
    string ManifestHash,
    int AcceptedCompletedCount,
    int PendingCount,
    int ConflictCount,
    IReadOnlyList<CopyBatchRecoveryItem> Items);

public static class CopyBatchManifestHash
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static string Compute(CopyBatchManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var material = manifest with { ManifestHash = string.Empty };
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(material, JsonOptions))))
            .ToLowerInvariant();
    }

    public static bool IsValid(CopyBatchManifest manifest) =>
        manifest is not null
        && manifest.ManifestHash.Length == 64
        && StringComparer.Ordinal.Equals(
            manifest.ManifestHash,
            Compute(manifest));
}

public interface ICopyBatchCheckpointStore
{
    Task<bool> SaveManifestAsync(
        CopyBatchManifest manifest,
        CancellationToken cancellationToken);

    Task<CopyBatchManifest?> GetManifestAsync(
        TestRunId runId,
        string stepId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CopyBatchEntryCheckpoint>> ListEntryCheckpointsAsync(
        TestRunId runId,
        string stepId,
        CancellationToken cancellationToken);

    Task UpdateEntryCheckpointAsync(
        CopyBatchEntryCheckpoint checkpoint,
        CancellationToken cancellationToken);

    Task MarkPendingEntriesCopyingAsync(
        TestRunId runId,
        string stepId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken);

    Task MarkEntriesCopyingAsync(
        TestRunId runId,
        string stepId,
        IReadOnlyCollection<int> ordinals,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken);

    Task ApplyRecoveryReportAsync(
        TestRunId runId,
        string stepId,
        CopyBatchRecoveryReport report,
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken);

    Task MarkOpenBatchInterruptedAsync(
        TestRunId runId,
        string stepId,
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken);
}
