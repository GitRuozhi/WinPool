using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Sqlite;

/// <summary>
/// Stops new persistence writes and connections, waits for outstanding work,
/// flushes it, and keeps storage access quiesced until the returned lease is
/// disposed.
/// </summary>
public interface IStorageWriteQuiescenceCoordinator
{
    Task<IAsyncDisposable> QuiesceAndFlushAsync(
        CorrelationId correlationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Commits the small location pointer. The production implementation replaces
/// an existing pointer atomically from a temporary file in the same directory.
/// </summary>
public interface IStorageLocationPointerCommitter
{
    Task CommitAsync(
        string pointerPath,
        StorageLocationMode mode,
        CancellationToken cancellationToken);
}

public sealed class AtomicStorageLocationPointerCommitter : IStorageLocationPointerCommitter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task CommitAsync(
        string pointerPath,
        StorageLocationMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pointerPath);
        var fullPointerPath = Path.GetFullPath(pointerPath);
        var parent = Path.GetDirectoryName(fullPointerPath)
            ?? throw new IOException("The storage location pointer has no parent directory.");
        Directory.CreateDirectory(parent);

        var temporaryPath = fullPointerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new StorageLocationPointer(mode),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPointerPath))
            {
                File.Replace(temporaryPath, fullPointerPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, fullPointerPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal sealed record StorageLocationPointer(StorageLocationMode Mode);

    internal static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>
/// Plans and applies a whole-root data relocation. The source is copied and
/// deliberately retained; only the pointer changes which copy is active.
/// </summary>
public sealed class StorageLocationManager : IStorageLocationManager
{
    public const string DatabaseFileName = "winpool.db";
    public const string PointerFileName = "storage-location.json";

    private static readonly JsonSerializerOptions JsonOptions =
        AtomicStorageLocationPointerCommitter.CreateJsonOptions();

    private readonly string standardRoot;
    private readonly string portableRoot;
    private readonly string pointerPath;
    private readonly IStorageWriteQuiescenceCoordinator writeCoordinator;
    private readonly IStorageLocationPointerCommitter pointerCommitter;
    private readonly ISqliteMigrationAuditor migrationAuditor;
    private readonly Action<string> deleteDirectoryTree;
    private readonly SemaphoreSlim switchGate = new(1, 1);
    private readonly Dictionary<StorageLocationSwitchPlan, PlannedSwitch> issuedPlans = [];

    public StorageLocationManager(
        string standardRoot,
        string portableRoot,
        IStorageWriteQuiescenceCoordinator writeCoordinator,
        IStorageLocationPointerCommitter? pointerCommitter = null,
        ISqliteMigrationAuditor? migrationAuditor = null,
        Action<string>? deleteDirectoryTree = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(standardRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(portableRoot);
        ArgumentNullException.ThrowIfNull(writeCoordinator);

        this.standardRoot = NormalizeRoot(standardRoot);
        this.portableRoot = NormalizeRoot(portableRoot);
        EnsureRootsAreIndependent(this.standardRoot, this.portableRoot);
        pointerPath = Path.Combine(this.standardRoot, PointerFileName);
        this.writeCoordinator = writeCoordinator;
        this.pointerCommitter = pointerCommitter
            ?? new AtomicStorageLocationPointerCommitter();
        this.migrationAuditor = migrationAuditor ?? new SqliteMigrationAuditor();
        this.deleteDirectoryTree = deleteDirectoryTree ?? DeleteDirectoryTree;
    }

    public async Task<ApplicationResult<StorageLocationState>> GetCurrentAsync(
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var mode = await ReadModeAsync(cancellationToken);
            var root = GetRoot(mode);
            return ApplicationResult<StorageLocationState>.Succeeded(
                CreateState(mode, root),
                correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<StorageLocationState>(
                ApplicationStatus.Cancelled,
                correlationId,
                "storage.location.cancelled",
                "Storage location lookup was cancelled.");
        }
        catch (Exception ex) when (IsExpectedStorageException(ex))
        {
            return Result<StorageLocationState>(
                ApplicationStatus.Failed,
                correlationId,
                "storage.location.pointer_read_failed",
                "The storage location pointer could not be read.");
        }
    }

    public async Task<ApplicationResult<StorageLocationSwitchPlan>> PlanSwitchAsync(
        StorageLocationMode targetMode,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(targetMode))
        {
            return Result<StorageLocationSwitchPlan>(
                ApplicationStatus.Rejected,
                correlationId,
                "storage.location.mode_invalid",
                "The requested storage location mode is invalid.");
        }

        var enteredGate = false;
        try
        {
            await switchGate.WaitAsync(cancellationToken);
            enteredGate = true;
            var sourceMode = await ReadModeAsync(cancellationToken);
            var sourceRoot = GetRoot(sourceMode);
            var targetRoot = GetRoot(targetMode);

            ValidateTreeHasNoReparsePoints(sourceRoot);
            ValidateTreeHasNoReparsePoints(targetRoot);
            CleanupOwnedTransactionRoots(sourceRoot);
            CleanupOwnedTransactionRoots(targetRoot);
            if (!CanWriteTarget(targetRoot))
            {
                return Result<StorageLocationSwitchPlan>(
                    ApplicationStatus.Rejected,
                    correlationId,
                    "storage.location.target_not_writable",
                    "The requested data root is not writable.");
            }

            var snapshot = await SnapshotSourceAsync(sourceRoot, cancellationToken);
            var targetSnapshot = await SnapshotSourceAsync(targetRoot, cancellationToken);
            var plan = new StorageLocationSwitchPlan(
                sourceMode,
                targetMode,
                sourceRoot,
                targetRoot,
                snapshot.Count,
                snapshot.TotalBytes,
                snapshot.ManifestSha256,
                DateTimeOffset.UtcNow);
            issuedPlans[plan] = new PlannedSwitch(snapshot, targetSnapshot);
            return ApplicationResult<StorageLocationSwitchPlan>.Succeeded(plan, correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<StorageLocationSwitchPlan>(
                ApplicationStatus.Cancelled,
                correlationId,
                "storage.location.cancelled",
                "Storage location planning was cancelled.");
        }
        catch (ReparsePointException)
        {
            return Result<StorageLocationSwitchPlan>(
                ApplicationStatus.Rejected,
                correlationId,
                "storage.location.reparse_path_rejected",
                "A storage location path contains a reparse point.");
        }
        catch (Exception ex) when (IsExpectedStorageException(ex))
        {
            return Result<StorageLocationSwitchPlan>(
                ApplicationStatus.Failed,
                correlationId,
                "storage.location.plan_failed",
                "The storage location switch could not be planned.");
        }
        finally
        {
            if (enteredGate)
            {
                switchGate.Release();
            }
        }
    }

    public async Task<ApplicationResult<StorageLocationState>> ApplySwitchAsync(
        StorageLocationSwitchPlan plan,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var enteredGate = false;
        try
        {
            await switchGate.WaitAsync(cancellationToken);
            enteredGate = true;
            if (!issuedPlans.TryGetValue(plan, out var plannedSwitch)
                || !PlanUsesConfiguredRoots(plan))
            {
                return Result<StorageLocationState>(
                    ApplicationStatus.Rejected,
                    correlationId,
                    "storage.location.plan_not_issued",
                    "The switch plan was not issued by this manager.");
            }

            var currentMode = await ReadModeAsync(cancellationToken);
            if (currentMode != plan.SourceMode)
            {
                return Result<StorageLocationState>(
                    ApplicationStatus.Rejected,
                    correlationId,
                    "storage.location.plan_stale",
                    "The active storage location changed after planning.");
            }

            if (plan.SourceMode == plan.TargetMode)
            {
                issuedPlans.Remove(plan);
                return ApplicationResult<StorageLocationState>.Succeeded(
                    CreateState(currentMode, GetRoot(currentMode)),
                    correlationId);
            }

            ValidateTreeHasNoReparsePoints(plan.SourceRoot);
            ValidateTreeHasNoReparsePoints(plan.TargetRoot);
            if (!CanWriteTarget(plan.TargetRoot))
            {
                return Result<StorageLocationState>(
                    ApplicationStatus.Rejected,
                    correlationId,
                    "storage.location.target_not_writable",
                    "The requested data root is not writable.");
            }

            var currentSourceSnapshot = await SnapshotSourceAsync(
                plan.SourceRoot,
                cancellationToken);
            if (!plannedSwitch.Source.Matches(currentSourceSnapshot))
            {
                return Result<StorageLocationState>(
                    ApplicationStatus.Rejected,
                    correlationId,
                    "storage.location.plan_stale",
                    "The source data changed after planning; create a new plan.");
            }

            var currentTargetSnapshot = await SnapshotSourceAsync(
                plan.TargetRoot,
                cancellationToken);
            if (!plannedSwitch.Target.Matches(currentTargetSnapshot))
            {
                return Result<StorageLocationState>(
                    ApplicationStatus.Rejected,
                    correlationId,
                    "storage.location.target_changed",
                    "The target data changed after planning; create a new plan.");
            }

            var stagingRoot = CreateSiblingTransactionRoot(plan.TargetRoot, "stage");
            var rollbackRoot = CreateSiblingTransactionRoot(plan.TargetRoot, "rollback");
            var targetWasReplaced = false;
            var pointerCommitted = false;
            ApplicationResult<StorageLocationState>? committedResult = null;
            Exception? postCommitCleanupFailure = null;
            try
            {
                await CopySnapshotAsync(
                    plannedSwitch.Source,
                    stagingRoot,
                    cancellationToken);
                var stagedSnapshot = await SnapshotSourceAsync(
                    stagingRoot,
                    cancellationToken);
                EnsureSameManifest(plannedSwitch.Source, stagedSnapshot, "staging");

                var sourceDatabasePath = Path.Combine(
                    plan.SourceRoot,
                    DatabaseFileName);
                var stagedSourceDatabaseAudit = IsSqliteDatabase(sourceDatabasePath)
                    ? await migrationAuditor.CaptureAsync(
                        sourceDatabasePath,
                        cancellationToken)
                    : null;
                if (stagedSourceDatabaseAudit is not null)
                {
                    await EnsureDatabaseIdentityAsync(
                        stagedSourceDatabaseAudit,
                        stagingRoot,
                        cancellationToken);
                }

                await using var writeLease = await writeCoordinator.QuiesceAndFlushAsync(
                    correlationId,
                    cancellationToken);

                DrainSourceDatabaseHandles(plan.SourceRoot);

            // Flush happens while acquiring the lease. Re-snapshot afterwards so
            // the immutable plan cannot silently omit writes made since planning.
                var sourceSnapshot = await SnapshotSourceAsync(
                    plan.SourceRoot,
                    cancellationToken);
                if (!plannedSwitch.Source.Matches(sourceSnapshot))
                {
                    return Result<StorageLocationState>(
                        ApplicationStatus.Rejected,
                        correlationId,
                        "storage.location.plan_stale",
                        "The source data changed after planning; create a new plan.");
                }

                EnsureSameManifest(sourceSnapshot, stagedSnapshot, "staging");

                var sourceDatabaseAudit = IsSqliteDatabase(sourceDatabasePath)
                    ? await migrationAuditor.CaptureAsync(
                        sourceDatabasePath,
                        cancellationToken)
                    : null;
                if (stagedSourceDatabaseAudit is not null
                    && (sourceDatabaseAudit is null
                        || !stagedSourceDatabaseAudit.HasSameLogicalIdentity(sourceDatabaseAudit)))
                {
                    return Result<StorageLocationState>(
                        ApplicationStatus.Rejected,
                        correlationId,
                        "storage.location.plan_stale",
                        "The source database changed after staging; create a new plan.");
                }

                if (sourceDatabaseAudit is not null)
                {
                    await EnsureDatabaseIdentityAsync(
                        sourceDatabaseAudit,
                        stagingRoot,
                        cancellationToken);
                }

                currentTargetSnapshot = await SnapshotSourceAsync(
                    plan.TargetRoot,
                    cancellationToken);
                if (!plannedSwitch.Target.Matches(currentTargetSnapshot))
                {
                    return Result<StorageLocationState>(
                        ApplicationStatus.Rejected,
                        correlationId,
                        "storage.location.target_changed",
                        "The target data changed after staging; create a new plan.");
                }

                ReplaceTargetWithStaging(
                    plan.TargetRoot,
                    stagingRoot,
                    rollbackRoot);
                targetWasReplaced = true;

                var targetSnapshot = await SnapshotSourceAsync(
                    plan.TargetRoot,
                    cancellationToken);
                EnsureSameManifest(sourceSnapshot, targetSnapshot, "target");
                if (sourceDatabaseAudit is not null)
                {
                    await EnsureDatabaseIdentityAsync(
                        sourceDatabaseAudit,
                        plan.TargetRoot,
                        cancellationToken);
                }

                targetSnapshot = await SnapshotSourceAsync(
                    plan.TargetRoot,
                    cancellationToken);
                EnsureSameManifest(sourceSnapshot, targetSnapshot, "target");

                await pointerCommitter.CommitAsync(
                    pointerPath,
                    plan.TargetMode,
                    cancellationToken);
                pointerCommitted = true;

                issuedPlans.Remove(plan);
                committedResult = ApplicationResult<StorageLocationState>.Succeeded(
                    CreateState(plan.TargetMode, plan.TargetRoot),
                    correlationId);
            }
            finally
            {
                if (targetWasReplaced && !pointerCommitted)
                {
                    RestorePreviousTarget(plan.TargetRoot, rollbackRoot);
                }

                if (pointerCommitted)
                {
                    TryPostCommitCleanup(stagingRoot, ref postCommitCleanupFailure);
                    TryPostCommitCleanup(rollbackRoot, ref postCommitCleanupFailure);
                }
                else
                {
                    deleteDirectoryTree(stagingRoot);
                    deleteDirectoryTree(rollbackRoot);
                }
            }

            if (pointerCommitted)
            {
                if (postCommitCleanupFailure is not null)
                {
                    return new ApplicationResult<StorageLocationState>(
                        ApplicationStatus.PartiallyCompleted,
                        CreateState(plan.TargetMode, plan.TargetRoot),
                        [new ApplicationMessage(
                            "storage.location.cleanup_pending",
                            "storage.location.cleanup_pending",
                            "The active data location was switched, but transaction cleanup is pending.",
                            ApplicationMessageSeverity.Warning,
                            [])],
                        correlationId);
                }

                return committedResult
                    ?? throw new InvalidOperationException(
                        "A committed storage-location switch has no result.");
            }

            throw new InvalidOperationException(
                "A storage-location switch exited without committing or returning a result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<StorageLocationState>(
                ApplicationStatus.Cancelled,
                correlationId,
                "storage.location.cancelled",
                "Storage location switching was cancelled.");
        }
        catch (ReparsePointException)
        {
            return Result<StorageLocationState>(
                ApplicationStatus.Rejected,
                correlationId,
                "storage.location.reparse_path_rejected",
                "A storage location path contains a reparse point.");
        }
        catch (Exception ex) when (IsExpectedStorageException(ex))
        {
            return Result<StorageLocationState>(
                ApplicationStatus.Failed,
                correlationId,
                "storage.location.apply_failed",
                "The storage location switch failed before its pointer was committed.");
        }
        finally
        {
            if (enteredGate)
            {
                switchGate.Release();
            }
        }
    }

    private async Task<StorageLocationMode> ReadModeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(pointerPath))
        {
            return StorageLocationMode.Standard;
        }

        await using var stream = new FileStream(
            pointerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var pointer = await JsonSerializer.DeserializeAsync<
            AtomicStorageLocationPointerCommitter.StorageLocationPointer>(
            stream,
            JsonOptions,
            cancellationToken);
        if (pointer is null || !Enum.IsDefined(pointer.Mode))
        {
            throw new JsonException("The storage location pointer is invalid.");
        }

        return pointer.Mode;
    }

    private StorageLocationState CreateState(StorageLocationMode mode, string root) =>
        new(
            mode,
            root,
            Path.Combine(root, DatabaseFileName),
            CanWriteTarget(root));

    private string GetRoot(StorageLocationMode mode) =>
        mode == StorageLocationMode.Portable ? portableRoot : standardRoot;

    private bool PlanUsesConfiguredRoots(StorageLocationSwitchPlan plan) =>
        string.Equals(plan.SourceRoot, GetRoot(plan.SourceMode), PathComparison)
        && string.Equals(plan.TargetRoot, GetRoot(plan.TargetMode), PathComparison);

    private Task<SourceSnapshot> SnapshotSourceAsync(
        string sourceRoot,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => SnapshotSourceCore(sourceRoot, cancellationToken),
            cancellationToken);

    private SourceSnapshot SnapshotSourceCore(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return new SourceSnapshot([], 0, HashManifest([]));
        }

        var files = new List<SourceFile>();
        long totalBytes = 0;
        foreach (var file in EnumerateFilesWithoutReparsePoints(sourceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsControlOrTransientFile(file, sourceRoot))
            {
                continue;
            }

            var info = new FileInfo(file);
            var relativePath = Path.GetRelativePath(sourceRoot, file);
            var length = info.Length;
            var lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            var sha256 = HashFile(file, cancellationToken);
            info.Refresh();
            if (info.Length != length
                || info.LastWriteTimeUtc.Ticks != lastWriteUtcTicks)
            {
                throw new IOException(
                    "A source file changed while its migration manifest was being hashed.");
            }

            files.Add(new SourceFile(
                file,
                relativePath,
                length,
                lastWriteUtcTicks,
                sha256));
            checked
            {
                totalBytes += length;
            }
        }

        return new SourceSnapshot(files, totalBytes, HashManifest(files));
    }

    private Task CopySnapshotAsync(
        SourceSnapshot snapshot,
        string targetRoot,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => CopySnapshotCore(snapshot, targetRoot, cancellationToken),
            cancellationToken);

    private void CopySnapshotCore(
        SourceSnapshot snapshot,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetRoot);
        ValidateTreeHasNoReparsePoints(targetRoot);

        foreach (var file in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(targetRoot, file.RelativePath));
            if (!IsWithinRoot(destination, targetRoot))
            {
                throw new IOException("A source relative path escaped the target root.");
            }

            var parent = Path.GetDirectoryName(destination)
                ?? throw new IOException("A migrated file has no parent directory.");
            Directory.CreateDirectory(parent);
            EnsureExistingPathHasNoReparsePoint(parent);
            if (File.Exists(destination)
                && File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ReparsePointException();
            }

            File.Copy(file.FullPath, destination, overwrite: true);
            var destinationInfo = new FileInfo(destination);
            if (destinationInfo.Length != file.Length
                || !StringComparer.Ordinal.Equals(
                    HashFile(destination, cancellationToken),
                    file.Sha256))
            {
                throw new IOException(
                    "A migrated file failed size or SHA-256 verification.");
            }
        }
    }

    private static string HashFile(
        string path,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsSqliteDatabase(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 16)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return stream.Read(header) == header.Length
            && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private async Task EnsureDatabaseIdentityAsync(
        SqliteMigrationAuditReport sourceAudit,
        string root,
        CancellationToken cancellationToken)
    {
        var targetAudit = await migrationAuditor.CaptureAsync(
            Path.Combine(root, DatabaseFileName),
            cancellationToken);
        if (!sourceAudit.HasSameLogicalIdentity(targetAudit))
        {
            throw new IOException(
                "The migrated SQLite database failed schema, row-count, or primary-key verification.");
        }
    }

    private static void EnsureSameManifest(
        SourceSnapshot source,
        SourceSnapshot candidate,
        string candidateName)
    {
        if (!source.HasSameManifest(candidate))
        {
            throw new IOException(
                $"The {candidateName} payload manifest does not exactly match the source manifest.");
        }
    }

    private static string CreateSiblingTransactionRoot(string targetRoot, string role)
    {
        var parent = Path.GetDirectoryName(targetRoot)
            ?? throw new IOException("The target data root has no parent directory.");
        var name = Path.GetFileName(targetRoot);
        return Path.Combine(
            parent,
            $".{name}.winpool-{role}-{Guid.NewGuid():N}");
    }

    private static void ReplaceTargetWithStaging(
        string targetRoot,
        string stagingRoot,
        string rollbackRoot)
    {
        if (Directory.Exists(targetRoot))
        {
            Directory.Move(targetRoot, rollbackRoot);
        }

        try
        {
            Directory.Move(stagingRoot, targetRoot);
        }
        catch
        {
            if (Directory.Exists(rollbackRoot) && !Directory.Exists(targetRoot))
            {
                Directory.Move(rollbackRoot, targetRoot);
            }

            throw;
        }
    }

    private static void RestorePreviousTarget(string targetRoot, string rollbackRoot)
    {
        DeleteDirectoryTree(targetRoot);
        if (Directory.Exists(rollbackRoot))
        {
            Directory.Move(rollbackRoot, targetRoot);
        }
    }

    private static void DeleteDirectoryTree(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private void TryPostCommitCleanup(string path, ref Exception? failure)
    {
        try
        {
            deleteDirectoryTree(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure ??= exception;
        }
    }

    private void CleanupOwnedTransactionRoots(string targetRoot)
    {
        var normalizedTarget = NormalizeRoot(targetRoot);
        var parent = Path.GetDirectoryName(normalizedTarget)
            ?? throw new IOException("The target data root has no parent directory.");
        EnsureExistingPathHasNoReparsePoint(parent);
        if (!Directory.Exists(parent))
        {
            return;
        }

        var targetName = Path.GetFileName(normalizedTarget);
        foreach (var candidate in Directory.EnumerateDirectories(parent))
        {
            if (!IsOwnedTransactionRoot(candidate, parent, targetName))
            {
                continue;
            }

            if (File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ReparsePointException();
            }

            deleteDirectoryTree(candidate);
        }
    }

    private static bool IsOwnedTransactionRoot(
        string candidate,
        string expectedParent,
        string targetName)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        if (!string.Equals(
                Path.GetDirectoryName(fullCandidate),
                Path.GetFullPath(expectedParent),
                PathComparison))
        {
            return false;
        }

        var name = Path.GetFileName(fullCandidate);
        foreach (var role in new[] { "stage", "rollback" })
        {
            var prefix = $".{targetName}.winpool-{role}-";
            if (name.StartsWith(prefix, StringComparison.Ordinal)
                && Guid.TryParseExact(name[prefix.Length..], "N", out _))
            {
                return true;
            }
        }

        return false;
    }

    private static string HashManifest(IReadOnlyList<SourceFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            var normalizedRelativePath = file.RelativePath.Replace('\\', '/');
            var line = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{normalizedRelativePath}\0{file.Length}\0{file.Sha256}\n");
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void DrainSourceDatabaseHandles(string sourceRoot)
    {
        var databasePath = Path.Combine(sourceRoot, DatabaseFileName);
        if (!IsSqliteDatabase(databasePath))
        {
            return;
        }

        WinPoolSqliteStore.DrainConnectionPool(databasePath);
        VerifyExclusiveRead(databasePath);
    }

    private static void VerifyExclusiveRead(string databasePath)
    {
        using var stream = new FileStream(
            databasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            bufferSize: 1,
            FileOptions.RandomAccess);
        _ = stream.ReadByte();
    }

    private bool IsControlOrTransientFile(string file, string root)
    {
        if (!string.Equals(
                Path.GetDirectoryName(file),
                root,
                PathComparison))
        {
            return false;
        }

        var name = Path.GetFileName(file);
        var isPointer = string.Equals(root, standardRoot, PathComparison)
            && (string.Equals(name, PointerFileName, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(
                    PointerFileName + ".tmp-",
                    StringComparison.OrdinalIgnoreCase));
        return isPointer
            || string.Equals(name, DatabaseFileName + "-wal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, DatabaseFileName + "-shm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, DatabaseFileName + "-journal", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ReparsePointException();
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        files.Sort(PathComparer);
        return files;
    }

    private static void ValidateTreeHasNoReparsePoints(string root)
    {
        EnsureExistingPathHasNoReparsePoint(root);
        if (Directory.Exists(root))
        {
            _ = EnumerateFilesWithoutReparsePoints(root);
        }
    }

    private static void EnsureExistingPathHasNoReparsePoint(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((Directory.Exists(current) || File.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ReparsePointException();
            }

            var parent = Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(current));
            if (string.IsNullOrEmpty(parent)
                || string.Equals(parent, current, PathComparison))
            {
                break;
            }

            current = parent;
        }
    }

    private static bool CanWriteTarget(string targetRoot)
    {
        string? probe = null;
        try
        {
            if (File.Exists(targetRoot))
            {
                return false;
            }

            EnsureExistingPathHasNoReparsePoint(targetRoot);
            var probeParent = Directory.Exists(targetRoot)
                ? targetRoot
                : FindNearestExistingParent(targetRoot);
            if (probeParent is null)
            {
                return false;
            }

            probe = Path.Combine(
                probeParent,
                ".winpool-write-probe-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(
                       probe,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 1,
                       FileOptions.WriteThrough))
            {
            }

            File.Delete(probe);
            probe = null;
            return true;
        }
        catch (Exception ex) when (IsExpectedStorageException(ex)
                                   || ex is ReparsePointException)
        {
            return false;
        }
        finally
        {
            if (probe is not null)
            {
                try
                {
                    File.Delete(probe);
                }
                catch (Exception ex) when (IsExpectedStorageException(ex))
                {
                }
            }
        }
    }

    private static string? FindNearestExistingParent(string path)
    {
        var current = Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }

            if (File.Exists(current))
            {
                return null;
            }

            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }

        return null;
    }

    private static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private static void EnsureRootsAreIndependent(string first, string second)
    {
        if (string.Equals(first, second, PathComparison)
            || IsWithinRoot(first, second)
            || IsWithinRoot(second, first))
        {
            throw new ArgumentException(
                "Standard and portable data roots must be separate, non-nested directories.");
        }
    }

    private static bool IsWithinRoot(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool IsExpectedStorageException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException;

    private static ApplicationResult<T> Result<T>(
        ApplicationStatus status,
        CorrelationId correlationId,
        string code,
        string diagnostic) =>
        ApplicationResult<T>.FromStatus(
            status,
            correlationId,
            new ApplicationMessage(
                code,
                code,
                diagnostic,
                status == ApplicationStatus.Cancelled
                    ? ApplicationMessageSeverity.Information
                    : ApplicationMessageSeverity.Error,
                []));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record SourceFile(
        string FullPath,
        string RelativePath,
        long Length,
        long LastWriteUtcTicks,
        string Sha256);

    private sealed record SourceSnapshot(
        IReadOnlyList<SourceFile> Files,
        long TotalBytes,
        string ManifestSha256)
    {
        public long Count => Files.Count;

        public bool HasSameManifest(SourceSnapshot other) =>
            TotalBytes == other.TotalBytes
            && Files.Count == other.Files.Count
            && StringComparer.Ordinal.Equals(ManifestSha256, other.ManifestSha256);

        public bool Matches(SourceSnapshot other)
        {
            if (TotalBytes != other.TotalBytes
                || Files.Count != other.Files.Count
                || !StringComparer.Ordinal.Equals(
                    ManifestSha256,
                    other.ManifestSha256))
            {
                return false;
            }

            for (var index = 0; index < Files.Count; index++)
            {
                var expected = Files[index];
                var actual = other.Files[index];
                if (!string.Equals(
                        expected.RelativePath,
                        actual.RelativePath,
                        PathComparison)
                    || expected.Length != actual.Length
                    || expected.LastWriteUtcTicks != actual.LastWriteUtcTicks
                    || !StringComparer.Ordinal.Equals(
                        expected.Sha256,
                        actual.Sha256))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private sealed record PlannedSwitch(
        SourceSnapshot Source,
        SourceSnapshot Target);

    private sealed class ReparsePointException : IOException;
}
