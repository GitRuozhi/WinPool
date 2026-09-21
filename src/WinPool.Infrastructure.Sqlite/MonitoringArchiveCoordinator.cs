using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using WinPool.Domain;

namespace WinPool.Infrastructure.Sqlite;

/// <summary>
/// The durable lifecycle of one self-contained monitoring database. Records
/// deliberately describe files rather than an abstract job: recovery must be
/// able to cross-check every state transition against the actual raw database
/// and archive without relying on an in-memory task queue.
/// </summary>
public enum MonitoringArchiveStage
{
    PendingSeal,
    Sealed,
    Compressing,
    Verifying,
    ArchiveCompleted,
    SourceReleased,
    RotationAborted
}

public sealed record MonitoringArchiveRecord(
    string GenerationId,
    MonitoringArchiveStage Stage,
    string RawDatabasePath,
    string ManifestPath,
    string TemporaryArchivePath,
    string ArchivePath,
    string SessionId,
    long? FirstSampleUtcMs,
    long? LastSampleUtcMs,
    long SampleCount,
    long DatabaseBytes,
    string DatabaseSha256,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? RetryAfterUtc = null,
    string? LastError = null)
{
    /// <summary>
    /// Every monitoring session actually present in this sealed database.
    /// <see cref="SessionId"/> remains the session that caused the rotation;
    /// it is not allowed to misdescribe a database that spans prior sessions.
    /// </summary>
    public IReadOnlyList<string> SessionIds { get; init; } = [];

    /// <summary>
    /// Pending-seal records deliberately avoid a large COUNT/hash operation on
    /// the rotation critical path. This turns true only after the background
    /// archive worker has read the sealed database and durably recorded its
    /// metadata.
    /// </summary>
    public bool MetadataComplete { get; init; }
}

public sealed record MonitoringArchiveDiagnostics(
    int PendingArchives,
    int FailedArchives,
    string? LastError,
    bool ArchiveRawDatabaseRetained = false,
    string? FailureOccurrenceId = null);

/// <summary>
/// Serializes monitoring archive work on a bounded in-memory dispatch queue.
/// The ledger is the source of truth, so a full queue merely delays an item and
/// never blocks new monitoring writes or loses the work description.
/// </summary>
public sealed class MonitoringArchiveCoordinator : IAsyncDisposable
{
    public const string ArchiveDirectoryName = "MonitoringArchives";
    public const string SealedDirectoryName = "sealed";
    public const string PackageDirectoryName = "packages";
    public const string LedgerFileName = "archive-ledger.json";

    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMinutes(1);
    private readonly string dataRoot;
    private readonly string archiveRoot;
    private readonly string sealedRoot;
    private readonly string packageRoot;
    private readonly string applicationDirectory;
    private readonly Func<string?> customExecutablePath;
    private readonly SevenZipArchiveAdapter sevenZip;
    private readonly TimeSpan retryDelay;
    private readonly MonitoringArchiveLedger ledger;
    private readonly Channel<string> dispatch = Channel.CreateBounded<string>(
        new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private readonly object dispatchGate = new();
    private readonly HashSet<string> scheduled = new(StringComparer.Ordinal);
    // Ledger persistence can itself fail (for example while the data root is
    // temporarily full). Keep the failure visible but retry the same durable
    // record with bounded backoff instead of permanently faulting the worker.
    private readonly Dictionary<string, DateTimeOffset> transientRetryAfter = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private Task? worker;
    private bool initialized;
    private Exception? workerFailure;
    private readonly Dictionary<string, TransientWorkerFailure> transientFailures = new(StringComparer.Ordinal);
    private long nextTransientFailureOccurrence;
    private int activeWork;

    public MonitoringArchiveCoordinator(
        string dataRoot,
        string applicationDirectory,
        Func<string?> customExecutablePath,
        SevenZipArchiveAdapter? sevenZip = null,
        TimeSpan? retryDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        ArgumentNullException.ThrowIfNull(customExecutablePath);
        this.dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        archiveRoot = Path.Combine(this.dataRoot, ArchiveDirectoryName);
        sealedRoot = Path.Combine(archiveRoot, SealedDirectoryName);
        packageRoot = Path.Combine(archiveRoot, PackageDirectoryName);
        this.applicationDirectory = Path.GetFullPath(applicationDirectory);
        this.customExecutablePath = customExecutablePath;
        this.sevenZip = sevenZip ?? new SevenZipArchiveAdapter();
        this.retryDelay = retryDelay ?? DefaultRetryDelay;
        if (this.retryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }
        ledger = new MonitoringArchiveLedger(Path.Combine(archiveRoot, LedgerFileName), archiveRoot);
    }

    public string ArchiveRoot => archiveRoot;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                return;
            }

            Directory.CreateDirectory(sealedRoot);
            Directory.CreateDirectory(packageRoot);
            await ledger.LoadAsync(cancellationToken);
            await RecoverAsync(cancellationToken);
            worker = Task.Run(WorkerAsync);
            initialized = true;
            await ScheduleEligibleAsync(cancellationToken);
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public async Task<MonitoringArchiveRecord> RegisterSealedDatabaseAsync(
        string rawDatabasePath,
        SessionId sessionId,
        string generationId,
        bool activateImmediately = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        var rawPath = Path.GetFullPath(rawDatabasePath);
        EnsureWithinSealedRoot(rawPath);
        EnsureSelfContainedSealedDatabase(rawPath);
        if (!File.Exists(rawPath))
        {
            throw new FileNotFoundException("The sealed monitoring database does not exist.", rawPath);
        }

        var metadata = await ReadDatabaseMetadataAsync(rawPath, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var archiveStem = BuildArchiveStem(
            metadata.FirstSampleUtcMs,
            metadata.LastSampleUtcMs,
            generationId,
            now);
        var record = new MonitoringArchiveRecord(
            generationId,
            activateImmediately
                ? MonitoringArchiveStage.Sealed
                : MonitoringArchiveStage.PendingSeal,
            rawPath,
            rawPath + ".manifest.json",
            Path.Combine(packageRoot, archiveStem + ".tmp.7z"),
            Path.Combine(packageRoot, archiveStem + ".7z"),
            sessionId.Value.ToString("N"),
            metadata.FirstSampleUtcMs,
            metadata.LastSampleUtcMs,
            metadata.SampleCount,
            new FileInfo(rawPath).Length,
            await HashFileAsync(rawPath, cancellationToken),
            now,
            now)
        {
            SessionIds = metadata.SessionIds,
            MetadataComplete = true
        };
        await ledger.UpsertAsync(record, cancellationToken);
        if (activateImmediately)
        {
            await ScheduleEligibleAsync(cancellationToken);
        }
        return record;
    }

    public async Task ActivateSealedArchiveAsync(
        string generationId,
        CancellationToken cancellationToken = default)
    {
        var record = ledger.Find(generationId)
            ?? throw new IOException("The monitoring archive generation is not present in its ledger.");
        EnsureRecordPaths(record);
        if (!File.Exists(record.RawDatabasePath))
        {
            throw new IOException("The sealed monitoring database is missing before archive activation.");
        }
        EnsureSelfContainedSealedDatabase(record.RawDatabasePath);

        await ledger.UpsertAsync(record with
        {
            Stage = MonitoringArchiveStage.Sealed,
            LastError = null,
            RetryAfterUtc = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
        await ScheduleEligibleAsync(cancellationToken);
    }

    public async Task MarkRotationAbortedAsync(
        string generationId,
        string error,
        CancellationToken cancellationToken = default)
    {
        var record = ledger.Find(generationId);
        if (record is null)
        {
            return;
        }

        await ledger.UpsertAsync(record with
        {
            Stage = MonitoringArchiveStage.RotationAborted,
            LastError = error,
            RetryAfterUtc = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task RecordPendingSealAsync(
        MonitoringArchiveRecord record,
        CancellationToken cancellationToken = default)
    {
        EnsureRecordPaths(record);
        await ledger.UpsertAsync(record with
        {
            Stage = MonitoringArchiveStage.PendingSeal,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task<MonitoringArchiveRecord> CreatePendingSealAsync(
        string rawDatabasePath,
        SessionId sessionId,
        string generationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        var rawPath = Path.GetFullPath(rawDatabasePath);
        EnsureWithinSealedRoot(rawPath);
        var now = DateTimeOffset.UtcNow;
        var stem = $"monitoring-pending-{generationId.Replace("-", string.Empty, StringComparison.Ordinal)}";
        var record = new MonitoringArchiveRecord(
            generationId,
            MonitoringArchiveStage.PendingSeal,
            rawPath,
            rawPath + ".manifest.json",
            Path.Combine(packageRoot, stem + ".tmp.7z"),
            Path.Combine(packageRoot, stem + ".7z"),
            sessionId.Value.ToString("N"),
            null,
            null,
            0,
            0,
            string.Empty,
            now,
            now);
        await RecordPendingSealAsync(record, cancellationToken);
        return record;
    }

    public IReadOnlyList<MonitoringArchiveRecord> Snapshot() => ledger.Snapshot();

    public MonitoringArchiveDiagnostics GetDiagnostics()
    {
        var records = ledger.Snapshot();
        var pending = records.Count(record => record.Stage is not (
            MonitoringArchiveStage.SourceReleased or MonitoringArchiveStage.RotationAborted));
        var lastFailed = records
            .Where(record => !string.IsNullOrWhiteSpace(record.LastError))
            .OrderByDescending(record => record.UpdatedAtUtc)
            .FirstOrDefault();
        var currentWorkerFailure = Volatile.Read(ref workerFailure);
        TransientWorkerFailure? transientFailure;
        lock (dispatchGate)
        {
            transientFailure = transientFailures.Values
                .OrderByDescending(value => value.Occurrence)
                .FirstOrDefault();
        }

        var transientRecord = transientFailure is null
            ? null
            : records.FirstOrDefault(record => string.Equals(
                record.GenerationId,
                transientFailure.GenerationId,
                StringComparison.Ordinal));
        var displayedFailure = currentWorkerFailure is null ? lastFailed : transientRecord;
        var failedCount = records.Count(record => !string.IsNullOrWhiteSpace(record.LastError));
        if (currentWorkerFailure is not null
            && (transientRecord is null || string.IsNullOrWhiteSpace(transientRecord.LastError)))
        {
            failedCount++;
        }
        return new(
            pending,
            failedCount,
            currentWorkerFailure?.Message ?? lastFailed?.LastError,
            displayedFailure is not null && File.Exists(displayedFailure.RawDatabasePath),
            currentWorkerFailure is not null && transientFailure is not null
                ? $"ledger:{transientFailure.GenerationId}:{transientFailure.Occurrence}"
                : lastFailed is null || string.IsNullOrWhiteSpace(lastFailed.LastError)
                    ? null
                    : $"{lastFailed.GenerationId}:{lastFailed.UpdatedAtUtc.UtcTicks}");
    }

    /// <summary>Used by isolated tests and orderly Agent shutdown evidence.</summary>
    public async Task WaitForIdleAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (dispatchGate)
            {
                if (scheduled.Count == 0 && Volatile.Read(ref activeWork) == 0)
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        if (Volatile.Read(ref workerFailure) is { } failure)
        {
            throw new IOException("Monitoring archive work remained unable to record its retry state.", failure);
        }

        throw new TimeoutException("Monitoring archive work did not become idle in time.");
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        dispatch.Writer.TryComplete();
        try
        {
            if (worker is not null)
            {
                await worker;
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            shutdown.Dispose();
            initializationGate.Dispose();
        }
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                if (!dispatch.Reader.TryRead(out var generationId))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), shutdown.Token);
                    await ScheduleEligibleAsync(shutdown.Token);
                    continue;
                }

                Interlocked.Increment(ref activeWork);
                try
                {
                    var record = ledger.Find(generationId);
                    if (record is not null)
                    {
                        await ProcessAsync(record, shutdown.Token);
                        ClearTransientWorkerFailure(generationId);
                    }
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var record = ledger.Find(generationId);
                    if (record is not null)
                    {
                        try
                        {
                            await ledger.UpsertAsync(record with
                            {
                                Stage = record.Stage == MonitoringArchiveStage.PendingSeal
                                    ? MonitoringArchiveStage.PendingSeal
                                    : MonitoringArchiveStage.Sealed,
                                LastError = exception.Message,
                                RetryAfterUtc = DateTimeOffset.UtcNow + retryDelay,
                                UpdatedAtUtc = DateTimeOffset.UtcNow
                            }, CancellationToken.None);
                            ClearTransientWorkerFailure(generationId);
                        }
                        catch (Exception ledgerException)
                        {
                            // The source is still present and the immutable
                            // ledger record will be redispatched after bounded
                            // backoff. Do not let this transient write failure
                            // permanently disable ScheduleEligibleAsync.
                            RecordTransientWorkerFailure(generationId, ledgerException);
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref activeWork);
                    lock (dispatchGate)
                    {
                        scheduled.Remove(generationId);
                    }
                }

                await ScheduleEligibleAsync(shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(
        MonitoringArchiveRecord record,
        CancellationToken cancellationToken)
    {
        EnsureRecordPaths(record);
        if (record.Stage == MonitoringArchiveStage.RotationAborted)
        {
            return;
        }

        if (record.Stage == MonitoringArchiveStage.SourceReleased)
        {
            // A completed archive was fully tested and content-hashed before
            // SourceReleased was durably written. Startup and normal worker
            // scans only need to notice a missing package; repeatedly
            // decompressing every historical database delays new sampling.
            if (!File.Exists(record.ArchivePath))
            {
                throw new IOException("A released monitoring archive is missing its completed package.");
            }
            return;
        }

        if (!File.Exists(record.RawDatabasePath))
        {
            if (File.Exists(record.ArchivePath))
            {
                var recoveredExecutable = SevenZipArchiveAdapter.ResolveExecutablePath(
                    customExecutablePath(),
                    applicationDirectory);
                await VerifyArchiveAsync(
                    record,
                    record.ArchivePath,
                    recoveredExecutable,
                    cancellationToken);
                await ledger.UpsertAsync(record with
                {
                    Stage = MonitoringArchiveStage.SourceReleased,
                    LastError = null,
                    RetryAfterUtc = null,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                }, cancellationToken);
                return;
            }

            throw new IOException("Neither the sealed monitoring database nor its completed archive is available.");
        }

        EnsureSelfContainedSealedDatabase(record.RawDatabasePath);
        record = await EnsureSealedMetadataAsync(record, cancellationToken);
        // One archive attempt captures exactly one configured executable. The
        // create, test, extract/hash, and release verification below must use
        // this same path even if the user edits settings while it is running.
        var executable = SevenZipArchiveAdapter.ResolveExecutablePath(
            customExecutablePath(),
            applicationDirectory);

        if (File.Exists(record.ArchivePath))
        {
            await VerifyAndReleaseAsync(record, executable, cancellationToken);
            return;
        }

        await CreateManifestAsync(record, cancellationToken);
        var temporaryArchiveVerified = await TryVerifyOrResetTemporaryArchiveAsync(
            record,
            executable,
            cancellationToken);
        record = record with
        {
            Stage = MonitoringArchiveStage.Compressing,
            LastError = null,
            RetryAfterUtc = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await ledger.UpsertAsync(record, cancellationToken);

        if (!temporaryArchiveVerified)
        {
            var workingDirectory = Path.GetDirectoryName(record.RawDatabasePath)
                ?? throw new IOException("The sealed monitoring database has no directory.");
            await sevenZip.CreateArchiveAsync(
                executable,
                record.TemporaryArchivePath,
                workingDirectory,
                [Path.GetFileName(record.RawDatabasePath), Path.GetFileName(record.ManifestPath)],
                cancellationToken);
        }

        record = record with
        {
            Stage = MonitoringArchiveStage.Verifying,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await ledger.UpsertAsync(record, cancellationToken);
        await VerifyArchiveAsync(record, record.TemporaryArchivePath, executable, cancellationToken);

        if (File.Exists(record.ArchivePath))
        {
            throw new IOException("The completed monitoring archive path already exists.");
        }

        File.Move(record.TemporaryArchivePath, record.ArchivePath);
        record = record with
        {
            Stage = MonitoringArchiveStage.ArchiveCompleted,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await ledger.UpsertAsync(record, cancellationToken);
        await VerifyAndReleaseAsync(record, executable, cancellationToken);
    }

    private async Task VerifyAndReleaseAsync(
        MonitoringArchiveRecord record,
        string executable,
        CancellationToken cancellationToken)
    {
        EnsureRecordPaths(record);
        EnsureSelfContainedSealedDatabase(record.RawDatabasePath);
        var sourceHash = await HashFileAsync(record.RawDatabasePath, cancellationToken);
        if (!string.Equals(sourceHash, record.DatabaseSha256, StringComparison.Ordinal))
        {
            throw new IOException("The sealed monitoring database changed after its archive metadata was recorded.");
        }

        await VerifyArchiveAsync(record, record.ArchivePath, executable, cancellationToken);
        EnsureWithinSealedRoot(record.RawDatabasePath);
        File.Delete(record.RawDatabasePath);
        TryRemoveOwnManifest(record.ManifestPath);
        await ledger.UpsertAsync(record with
        {
            Stage = MonitoringArchiveStage.SourceReleased,
            LastError = null,
            RetryAfterUtc = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    /// <summary>
    /// A failed create can leave a partial .tmp.7z.  The sealed source remains
    /// the authoritative copy, so a corrupt temporary package is safe to
    /// reuse its one path after the source hash has been rechecked.  A fully
    /// verified temporary package is retained and published instead of being
    /// recompressed.  Launch/configuration failures deliberately do not
    /// delete it because they say nothing about the package's content.
    /// </summary>
    private async Task<bool> TryVerifyOrResetTemporaryArchiveAsync(
        MonitoringArchiveRecord record,
        string executable,
        CancellationToken cancellationToken)
    {
        EnsureRecordPaths(record);
        if (!File.Exists(record.TemporaryArchivePath))
        {
            return false;
        }

        try
        {
            await VerifyArchiveAsync(record, record.TemporaryArchivePath, executable, cancellationToken);
            return true;
        }
        catch (SevenZipOperationException)
        {
            // A test/extract command reached 7-Zip and rejected the package.
        }
        catch (IOException exception) when (
            exception.Message.StartsWith("The monitoring archive", StringComparison.Ordinal))
        {
            // The internal content hash/manifest checks rejected the package.
            _ = exception;
        }

        EnsureSelfContainedSealedDatabase(record.RawDatabasePath);
        var sourceHash = await HashFileAsync(record.RawDatabasePath, cancellationToken);
        if (!string.Equals(sourceHash, record.DatabaseSha256, StringComparison.Ordinal))
        {
            throw new IOException("The sealed monitoring database changed after its archive metadata was recorded.");
        }

        EnsureWithinPackageRoot(record.TemporaryArchivePath);
        File.Delete(record.TemporaryArchivePath);
        return false;
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var record in ledger.Snapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRecordPaths(record);
            if (record.Stage == MonitoringArchiveStage.PendingSeal)
            {
                // A crash can happen either side of the source-db rename. Do
                // not infer deletion from this marker: preserve whichever file
                // exists and let the rotating writer repair the active name.
                continue;
            }

            if (record.Stage == MonitoringArchiveStage.RotationAborted)
            {
                continue;
            }

            if (record.Stage == MonitoringArchiveStage.SourceReleased)
            {
                if (!File.Exists(record.ArchivePath))
                {
                    await ledger.UpsertAsync(record with
                    {
                        LastError = "A released monitoring archive is missing its completed package.",
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    }, cancellationToken);
                }

                continue;
            }
        }
    }

    private async Task ScheduleEligibleAsync(CancellationToken cancellationToken)
    {
        if (!initialized)
        {
            return;
        }

        foreach (var record in ledger.Snapshot()
                     .OrderBy(item => item.CreatedAtUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Stage is MonitoringArchiveStage.PendingSeal
                or MonitoringArchiveStage.SourceReleased
                or MonitoringArchiveStage.RotationAborted
                || record.RetryAfterUtc > DateTimeOffset.UtcNow)
            {
                continue;
            }

            lock (dispatchGate)
            {
                if (transientRetryAfter.TryGetValue(record.GenerationId, out var transientRetry)
                    && transientRetry > DateTimeOffset.UtcNow)
                {
                    continue;
                }
                if (scheduled.Contains(record.GenerationId)
                    || !dispatch.Writer.TryWrite(record.GenerationId))
                {
                    continue;
                }

                scheduled.Add(record.GenerationId);
            }
        }
    }

    private async Task CreateManifestAsync(
        MonitoringArchiveRecord record,
        CancellationToken cancellationToken)
    {
        EnsureRecordPaths(record);
        var expected = BuildManifestBytes(record);
        if (File.Exists(record.ManifestPath))
        {
            await VerifyManifestFileAsync(record.ManifestPath, expected, cancellationToken);
            return;
        }

        var temporary = record.ManifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(expected, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, record.ManifestPath);
            }
            catch (IOException) when (File.Exists(record.ManifestPath))
            {
                await VerifyManifestFileAsync(record.ManifestPath, expected, cancellationToken);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void RecordTransientWorkerFailure(string generationId, Exception exception)
    {
        var failure = new IOException(
            "Monitoring archive failure could not be recorded in its ledger.",
            exception);
        lock (dispatchGate)
        {
            transientRetryAfter[generationId] = DateTimeOffset.UtcNow + retryDelay;
            transientFailures[generationId] = new(
                generationId,
                failure,
                checked(nextTransientFailureOccurrence + 1));
            nextTransientFailureOccurrence++;
        }

        Interlocked.Exchange(ref workerFailure, failure);
    }

    private void ClearTransientWorkerFailure(string generationId)
    {
        lock (dispatchGate)
        {
            transientRetryAfter.Remove(generationId);
            transientFailures.Remove(generationId);
            if (transientFailures.Count != 0)
            {
                var latest = transientFailures.Values
                    .OrderByDescending(value => value.Occurrence)
                    .First();
                Interlocked.Exchange(ref workerFailure, latest.Failure);
                return;
            }
        }

        Interlocked.Exchange(ref workerFailure, null);
    }

    private sealed record TransientWorkerFailure(
        string GenerationId,
        Exception Failure,
        long Occurrence);

    private static async Task<DatabaseMetadata> ReadDatabaseMetadataAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"file:{databasePath.Replace('\\', '/')}?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), MIN(timestamp_utc_ms), MAX(timestamp_utc_ms)
            FROM monitor_samples;
            """;
        long sampleCount;
        long? firstSampleUtcMs;
        long? lastSampleUtcMs;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new IOException("The sealed monitoring database did not return metadata.");
            }

            sampleCount = reader.GetInt64(0);
            firstSampleUtcMs = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            lastSampleUtcMs = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        }

        var sessions = new List<string>();
        await using (var sessionCommand = connection.CreateCommand())
        {
            sessionCommand.CommandText = """
                SELECT session_id
                FROM monitor_sessions
                ORDER BY session_id;
                """;
            await using var sessionReader = await sessionCommand.ExecuteReaderAsync(cancellationToken);
            while (await sessionReader.ReadAsync(cancellationToken))
            {
                sessions.Add(sessionReader.GetString(0));
            }
        }

        return new DatabaseMetadata(
            sampleCount,
            firstSampleUtcMs,
            lastSampleUtcMs,
            sessions);
    }

    private async Task<MonitoringArchiveRecord> EnsureSealedMetadataAsync(
        MonitoringArchiveRecord record,
        CancellationToken cancellationToken)
    {
        if (record.MetadataComplete)
        {
            return record;
        }

        EnsureRecordPaths(record);
        EnsureSelfContainedSealedDatabase(record.RawDatabasePath);
        var metadata = await ReadDatabaseMetadataAsync(record.RawDatabasePath, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var archiveStem = BuildArchiveStem(
            metadata.FirstSampleUtcMs,
            metadata.LastSampleUtcMs,
            record.GenerationId,
            record.CreatedAtUtc);
        var temporary = record.TemporaryArchivePath;
        var archivePath = record.ArchivePath;
        if (!File.Exists(temporary) && !File.Exists(archivePath))
        {
            temporary = Path.Combine(packageRoot, archiveStem + ".tmp.7z");
            archivePath = Path.Combine(packageRoot, archiveStem + ".7z");
            if (File.Exists(temporary) || File.Exists(archivePath))
            {
                throw new IOException("The generated monitoring archive path already exists.");
            }
        }

        var hydrated = record with
        {
            TemporaryArchivePath = temporary,
            ArchivePath = archivePath,
            FirstSampleUtcMs = metadata.FirstSampleUtcMs,
            LastSampleUtcMs = metadata.LastSampleUtcMs,
            SampleCount = metadata.SampleCount,
            DatabaseBytes = new FileInfo(record.RawDatabasePath).Length,
            DatabaseSha256 = await HashFileAsync(record.RawDatabasePath, cancellationToken),
            SessionIds = metadata.SessionIds,
            MetadataComplete = true,
            UpdatedAtUtc = now
        };
        EnsureRecordPaths(hydrated);
        await ledger.UpsertAsync(hydrated, cancellationToken);
        return hydrated;
    }

    private async Task VerifyArchiveAsync(
        MonitoringArchiveRecord record,
        string archivePath,
        string executable,
        CancellationToken cancellationToken)
    {
        EnsureRecordPaths(record);
        if (!record.MetadataComplete)
        {
            throw new IOException("The monitoring archive has no complete sealed-database metadata.");
        }

        if (!File.Exists(archivePath))
        {
            throw new IOException("The monitoring archive package is missing.");
        }

        await sevenZip.TestArchiveAsync(executable, archivePath, cancellationToken);
        var extractedDatabaseHash = await sevenZip.HashExtractedEntryAsync(
            executable,
            archivePath,
            Path.GetFileName(record.RawDatabasePath),
            cancellationToken);
        if (!string.Equals(extractedDatabaseHash, record.DatabaseSha256, StringComparison.Ordinal))
        {
            throw new IOException("The monitoring archive database SHA-256 does not match the sealed source.");
        }

        var extractedManifestHash = await sevenZip.HashExtractedEntryAsync(
            executable,
            archivePath,
            Path.GetFileName(record.ManifestPath),
            cancellationToken);
        var expectedManifestHash = Convert.ToHexString(
            SHA256.HashData(BuildManifestBytes(record))).ToLowerInvariant();
        if (!string.Equals(extractedManifestHash, expectedManifestHash, StringComparison.Ordinal))
        {
            throw new IOException("The monitoring archive manifest does not match its recorded sealed-database metadata.");
        }
    }

    private static string BuildArchiveStem(
        long? firstSampleUtcMs,
        long? lastSampleUtcMs,
        string generationId,
        DateTimeOffset fallbackUtc)
    {
        var first = firstSampleUtcMs is { } firstMilliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(firstMilliseconds)
            : fallbackUtc;
        var last = lastSampleUtcMs is { } lastMilliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(lastMilliseconds)
            : first;
        var safeGeneration = generationId.Replace("-", string.Empty, StringComparison.Ordinal);
        return $"monitoring-{first:yyyyMMddTHHmmssZ}-{last:yyyyMMddTHHmmssZ}-{safeGeneration}";
    }

    private static byte[] BuildManifestBytes(MonitoringArchiveRecord record) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new MonitoringArchiveManifest(
                MonitoringSqliteStore.CurrentSchemaVersion,
                record.GenerationId,
                record.SessionId,
                record.SessionIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                record.FirstSampleUtcMs,
                record.LastSampleUtcMs,
                record.SampleCount,
                record.DatabaseBytes,
                record.DatabaseSha256));

    private static async Task VerifyManifestFileAsync(
        string path,
        byte[] expected,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != expected.Length)
        {
            throw new IOException("The existing monitoring archive manifest has an unexpected length.");
        }

        var actual = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new IOException("The existing monitoring archive manifest does not match sealed-database metadata.");
        }
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void EnsureWithinArchiveRoot(string path) => EnsureWithinRoot(archiveRoot, path);

    private void EnsureWithinSealedRoot(string path) => EnsureWithinRoot(sealedRoot, path);

    private void EnsureWithinPackageRoot(string path) => EnsureWithinRoot(packageRoot, path);

    private static void EnsureWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new IOException("Monitoring archive work cannot escape its current data-root archive directory.");
        }
    }

    private void EnsureRecordPaths(MonitoringArchiveRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        EnsureWithinSealedRoot(record.RawDatabasePath);
        EnsureWithinSealedRoot(record.ManifestPath);
        EnsureWithinPackageRoot(record.TemporaryArchivePath);
        EnsureWithinPackageRoot(record.ArchivePath);
        if (!string.Equals(
                Path.GetFullPath(record.ManifestPath),
                Path.GetFullPath(record.RawDatabasePath) + ".manifest.json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The monitoring archive manifest path does not belong to its sealed database.");
        }

        if (string.Equals(
                Path.GetFullPath(record.TemporaryArchivePath),
                Path.GetFullPath(record.ArchivePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The monitoring archive temporary and completed package paths must differ.");
        }
    }

    private static void EnsureSelfContainedSealedDatabase(string rawDatabasePath)
    {
        if (!File.Exists(rawDatabasePath))
        {
            throw new FileNotFoundException("The sealed monitoring database does not exist.", rawDatabasePath);
        }

        foreach (var sidecar in new[]
                 {
                     rawDatabasePath + "-wal",
                     rawDatabasePath + "-shm",
                     rawDatabasePath + "-journal"
                 })
        {
            if (File.Exists(sidecar))
            {
                throw new IOException("A sealed monitoring database still has SQLite sidecar files.");
            }
        }
    }

    private void TryRemoveOwnManifest(string path)
    {
        EnsureWithinSealedRoot(path);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A completed archive is already safe. Retaining a generated
            // manifest side file is preferable to treating source release as a
            // failed archive after its database has been intentionally released.
        }
    }

    private sealed record DatabaseMetadata(
        long SampleCount,
        long? FirstSampleUtcMs,
        long? LastSampleUtcMs,
        IReadOnlyList<string> SessionIds);

    private sealed record MonitoringArchiveManifest(
        int MonitoringSchemaVersion,
        string GenerationId,
        string RotationSessionId,
        IReadOnlyList<string> SessionIds,
        long? FirstSampleUtcMs,
        long? LastSampleUtcMs,
        long SampleCount,
        long DatabaseBytes,
        string DatabaseSha256);
}

internal sealed class MonitoringArchiveLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string path;
    private readonly string archiveRoot;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object recordsGate = new();
    private Dictionary<string, MonitoringArchiveRecord> records = new(StringComparer.Ordinal);

    public MonitoringArchiveLedger(string path, string archiveRoot)
    {
        this.path = Path.GetFullPath(path);
        this.archiveRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archiveRoot));
        _ = ToRelativeArchivePath(this.path);
    }

    public IReadOnlyList<MonitoringArchiveRecord> Snapshot()
    {
        lock (recordsGate)
        {
            return records.Values
                .OrderBy(record => record.CreatedAtUtc)
                .ToArray();
        }
    }

    public MonitoringArchiveRecord? Find(string generationId)
    {
        lock (recordsGate)
        {
            return records.TryGetValue(generationId, out var record) ? record : null;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await JsonSerializer.DeserializeAsync<List<MonitoringArchiveRecord>>(
                stream,
                JsonOptions,
                cancellationToken)
                ?? throw new IOException("The monitoring archive ledger is empty or invalid.");
            var loaded = stored.Select(FromStoredRecord).ToArray();
            if (loaded.Any(record => string.IsNullOrWhiteSpace(record.GenerationId))
                || loaded.Select(record => record.GenerationId)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != loaded.Length)
            {
                throw new IOException("The monitoring archive ledger has invalid generation identities.");
            }

            lock (recordsGate)
            {
                records = loaded.ToDictionary(record => record.GenerationId, StringComparer.Ordinal);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpsertAsync(
        MonitoringArchiveRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, MonitoringArchiveRecord> candidate;
            lock (recordsGate)
            {
                candidate = new Dictionary<string, MonitoringArchiveRecord>(records, StringComparer.Ordinal)
                {
                    [record.GenerationId] = record
                };
            }

            await PersistAsync(candidate.Values.OrderBy(item => item.CreatedAtUtc).ToArray(), cancellationToken);
            lock (recordsGate)
            {
                records = candidate;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PersistAsync(
        IReadOnlyList<MonitoringArchiveRecord> snapshot,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new IOException("The monitoring archive ledger has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var stored = snapshot.Select(ToStoredRecord).ToArray();
                await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private MonitoringArchiveRecord ToStoredRecord(MonitoringArchiveRecord record) => record with
    {
        RawDatabasePath = ToRelativeArchivePath(record.RawDatabasePath),
        ManifestPath = ToRelativeArchivePath(record.ManifestPath),
        TemporaryArchivePath = ToRelativeArchivePath(record.TemporaryArchivePath),
        ArchivePath = ToRelativeArchivePath(record.ArchivePath)
    };

    private MonitoringArchiveRecord FromStoredRecord(MonitoringArchiveRecord record) => record with
    {
        RawDatabasePath = FromRelativeArchivePath(record.RawDatabasePath),
        ManifestPath = FromRelativeArchivePath(record.ManifestPath),
        TemporaryArchivePath = FromRelativeArchivePath(record.TemporaryArchivePath),
        ArchivePath = FromRelativeArchivePath(record.ArchivePath)
    };

    private string ToRelativeArchivePath(string value)
    {
        var full = Path.GetFullPath(value);
        var relative = Path.GetRelativePath(archiveRoot, full);
        if (EscapesArchiveRoot(relative))
        {
            throw new IOException("The monitoring archive ledger path escapes its archive root.");
        }

        return relative;
    }

    private string FromRelativeArchivePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value))
        {
            throw new IOException("The monitoring archive ledger uses an unsupported absolute or empty path.");
        }

        var full = Path.GetFullPath(Path.Combine(archiveRoot, value));
        var relative = Path.GetRelativePath(archiveRoot, full);
        if (EscapesArchiveRoot(relative))
        {
            throw new IOException("The monitoring archive ledger path escapes its archive root.");
        }

        return full;
    }

    private static bool EscapesArchiveRoot(string relative) =>
        relative == ".."
        || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || Path.IsPathRooted(relative);
}
