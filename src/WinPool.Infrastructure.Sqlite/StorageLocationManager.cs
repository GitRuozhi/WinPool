using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Sqlite;

/// <summary>
/// Stops new persistence writes, flushes outstanding writes, and keeps them
/// quiesced until the returned lease is disposed.
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
    private readonly SqliteMigrationAuditor migrationAuditor;
    private readonly SemaphoreSlim switchGate = new(1, 1);
    private readonly Dictionary<StorageLocationSwitchPlan, SourceSnapshot> issuedPlans = [];

    public StorageLocationManager(
        string standardRoot,
        string portableRoot,
        IStorageWriteQuiescenceCoordinator writeCoordinator,
        IStorageLocationPointerCommitter? pointerCommitter = null,
        SqliteMigrationAuditor? migrationAuditor = null)
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
            if (!CanWriteTarget(targetRoot))
            {
                return Result<StorageLocationSwitchPlan>(
                    ApplicationStatus.Rejected,
                    correlationId,
                    "storage.location.target_not_writable",
                    "The requested data root is not writable.");
            }

            var snapshot = await SnapshotSourceAsync(sourceRoot, cancellationToken);
            var plan = new StorageLocationSwitchPlan(
                sourceMode,
                targetMode,
                sourceRoot,
                targetRoot,
                snapshot.Count,
                snapshot.TotalBytes,
                snapshot.ManifestSha256,
                DateTimeOffset.UtcNow);
            issuedPlans[plan] = snapshot;
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
            if (!issuedPlans.TryGetValue(plan, out var plannedSnapshot)
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

            await using var writeLease = await writeCoordinator.QuiesceAndFlushAsync(
                correlationId,
                cancellationToken);

            // Flush happens while acquiring the lease. Re-snapshot afterwards so
            // the immutable plan cannot silently omit writes made since planning.
            var sourceSnapshot = await SnapshotSourceAsync(
                plan.SourceRoot,
                cancellationToken);
            if (!plannedSnapshot.Matches(sourceSnapshot))
            {
                return Result<StorageLocationState>(
                    ApplicationStatus.Rejected,
                    correlationId,
                    "storage.location.plan_stale",
                    "The source data changed after planning; create a new plan.");
            }

            var sourceDatabasePath = Path.Combine(
                plan.SourceRoot,
                DatabaseFileName);
            var sourceDatabaseAudit = IsSqliteDatabase(sourceDatabasePath)
                ? await migrationAuditor.CaptureAsync(
                    sourceDatabasePath,
                    cancellationToken)
                : null;
            ValidateTargetDatabaseFamily(sourceSnapshot, plan.TargetRoot);
            await CopySnapshotAsync(
                sourceSnapshot,
                plan.TargetRoot,
                cancellationToken);
            if (sourceDatabaseAudit is not null)
            {
                var targetDatabaseAudit = await migrationAuditor.CaptureAsync(
                    Path.Combine(plan.TargetRoot, DatabaseFileName),
                    cancellationToken);
                if (!sourceDatabaseAudit.HasSameLogicalIdentity(targetDatabaseAudit))
                {
                    throw new IOException(
                        "The migrated SQLite database failed schema, row-count, or primary-key verification.");
                }
            }

            await pointerCommitter.CommitAsync(pointerPath, plan.TargetMode, cancellationToken);

            issuedPlans.Remove(plan);
            return ApplicationResult<StorageLocationState>.Succeeded(
                CreateState(plan.TargetMode, plan.TargetRoot),
                correlationId);
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
            if (IsManagerFile(file))
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

    private static void ValidateTargetDatabaseFamily(
        SourceSnapshot sourceSnapshot,
        string targetRoot)
    {
        var sourceFiles = sourceSnapshot.Files
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] databaseFamily =
        [
            DatabaseFileName,
            DatabaseFileName + "-wal",
            DatabaseFileName + "-shm",
            DatabaseFileName + "-journal"
        ];
        foreach (var relativePath in databaseFamily)
        {
            if (!sourceFiles.Contains(relativePath)
                && File.Exists(Path.Combine(targetRoot, relativePath)))
            {
                throw new IOException(
                    "The target contains a stale SQLite database or sidecar file.");
            }
        }
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

    private bool IsManagerFile(string file)
    {
        if (!string.Equals(
                Path.GetDirectoryName(file),
                standardRoot,
                PathComparison))
        {
            return false;
        }

        var name = Path.GetFileName(file);
        return string.Equals(name, PointerFileName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(PointerFileName + ".tmp-", StringComparison.OrdinalIgnoreCase);
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

    private sealed class ReparsePointException : IOException;
}
