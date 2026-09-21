using WinPool.Application;
using WinPool.Domain;
using WinPool.Monitoring;

namespace WinPool.Infrastructure.Sqlite;

/// <summary>
/// Coordinates the CSV reader lifetime with the short exclusive period that
/// seals and replaces the fixed monitoring.db file. It does not make a claim
/// that multiple Windows file moves are atomic; readers are simply excluded
/// while the checked, self-contained source database is renamed.
/// </summary>
public interface IMonitoringDatabaseAccess
{
    Task<IAsyncDisposable> AcquireReadLeaseAsync(CancellationToken cancellationToken);

    ISqliteDatabaseStore GetCurrentDatabase();
}

public sealed record MonitoringRotationOptions(
    long RotationThresholdBytes,
    int RotationBufferCapacity,
    int WriterChannelCapacity,
    int WriterMaximumBatchSize,
    TimeSpan WriterMaximumBatchDelay,
    TimeSpan ThresholdPollInterval,
    TimeSpan FailureRetryDelay)
{
    public static MonitoringRotationOptions Default { get; } = new(
        RotationThresholdBytes: 1_073_741_824,
        RotationBufferCapacity: 8_192,
        WriterChannelCapacity: 8_192,
        WriterMaximumBatchSize: 1_000,
        WriterMaximumBatchDelay: TimeSpan.FromMilliseconds(250),
        ThresholdPollInterval: TimeSpan.FromMilliseconds(250),
        FailureRetryDelay: TimeSpan.FromSeconds(30));

    public void Validate()
    {
        if (RotationThresholdBytes <= 0
            || RotationBufferCapacity <= 0
            || WriterChannelCapacity <= 0
            || RotationBufferCapacity > WriterChannelCapacity
            || WriterMaximumBatchSize is <= 0 or > 2_000
            || WriterMaximumBatchDelay <= TimeSpan.Zero
            || ThresholdPollInterval <= TimeSpan.Zero
            || FailureRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MonitoringRotationOptions));
        }
    }
}

/// <summary>
/// Owns one fixed-path monitoring database and creates one persistence object
/// for the Agent's single active monitoring session. The configurable-looking
/// options are construction/test seams only; the production composition uses
/// <see cref="MonitoringRotationOptions.Default"/> and exposes no user setting.
/// </summary>
public sealed class RotatingMonitorSessionPersistenceFactory :
    IMonitorSessionPersistenceFactory,
    IMonitoringDatabaseAccess,
    IMonitoringPersistenceBackgroundDiagnostics,
    IAsyncDisposable
{
    public const string MonitoringDatabaseFileName = "monitoring.db";
    // Rotation moves the old primary on the same volume, but a fresh fixed
    // database still needs room for schema/WAL creation and immediate buffered
    // replay. This conservative floor is a preflight safety guard, not a
    // storage-retention quota.
    internal const long MinimumReplacementDatabaseFreeBytes = 16L * 1024 * 1024;

    private readonly string dataRoot;
    private readonly string activeDatabasePath;
    private readonly string ownerId;
    private readonly MonitoringArchiveCoordinator archive;
    private readonly MonitoringRotationOptions options;
    private readonly Func<long>? activeDatabaseBytesProvider;
    private readonly Func<long>? availableDataRootBytesProvider;
    private readonly TimeProvider timeProvider;
    private readonly Func<MonitoringSqliteStore, CancellationToken, Task> initializeMonitoringStoreAsync;
    private readonly MonitoringDatabaseAccessGate accessGate = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private MonitoringSqliteStore? activeStore;
    private AgentWriteOwnerLease? activeLease;
    private SessionId? activeSessionId;
    private int recoveredInterruptedSessionCount;
    private string? recoveredInterruptedSessionOccurrenceId;
    private bool initialized;
    private bool disposed;

    public RotatingMonitorSessionPersistenceFactory(
        string dataRoot,
        string ownerId,
        MonitoringArchiveCoordinator archive,
        MonitoringRotationOptions? options = null,
        Func<long>? activeDatabaseBytesProvider = null,
        Func<long>? availableDataRootBytesProvider = null,
        TimeProvider? timeProvider = null,
        Func<MonitoringSqliteStore, CancellationToken, Task>? initializeMonitoringStoreAsync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(archive);
        this.dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        activeDatabasePath = Path.Combine(this.dataRoot, MonitoringDatabaseFileName);
        this.ownerId = ownerId.Trim();
        this.archive = archive;
        this.options = options ?? MonitoringRotationOptions.Default;
        this.activeDatabaseBytesProvider = activeDatabaseBytesProvider;
        this.availableDataRootBytesProvider = availableDataRootBytesProvider;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.initializeMonitoringStoreAsync = initializeMonitoringStoreAsync
            ?? ((store, token) => store.InitializeAsync(token));
        this.options.Validate();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (initialized)
            {
                return;
            }

            Directory.CreateDirectory(dataRoot);
            await archive.InitializeAsync(cancellationToken);
            // Pending records describe a crash boundary. Decide their file
            // facts before creating monitoring.db; otherwise an empty new file
            // can hide the fatal "neither active nor sealed" case.
            await RecoverPendingSealsAsync(cancellationToken);
            await EnsureActiveDatabaseAsync(cancellationToken);
            initialized = true;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public IMonitorSessionPersistence Create(SessionId sessionId) =>
        new RotatingMonitorSessionPersistence(this, sessionId, options, timeProvider);

    public Task<IAsyncDisposable> AcquireReadLeaseAsync(CancellationToken cancellationToken) =>
        accessGate.AcquireReadAsync(cancellationToken);

    public ISqliteDatabaseStore GetCurrentDatabase()
    {
        lock (accessGate.SyncRoot)
        {
            ThrowIfDisposed();
            return activeStore
                ?? throw new InvalidOperationException("The monitoring database has not been initialized.");
        }
    }

    internal long GetActiveDatabaseBytes()
    {
        if (activeDatabaseBytesProvider is not null)
        {
            return Math.Max(0, activeDatabaseBytesProvider());
        }

        long bytes = 0;
        foreach (var path in new[] { activeDatabasePath, activeDatabasePath + "-wal" })
        {
            if (File.Exists(path))
            {
                checked
                {
                    bytes += new FileInfo(path).Length;
                }
            }
        }

        return bytes;
    }

    internal long GetAvailableDataRootBytes()
    {
        if (availableDataRootBytesProvider is not null)
        {
            return Math.Max(0, availableDataRootBytesProvider());
        }

        try
        {
            var root = Path.GetPathRoot(dataRoot)
                ?? throw new IOException("The monitoring data root has no volume root.");
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            throw new IOException(
                "The monitoring data root free space could not be determined before rotation.",
                exception);
        }
    }

    private void EnsureReplacementDatabaseSpace()
    {
        var available = GetAvailableDataRootBytes();
        if (available < MinimumReplacementDatabaseFreeBytes)
        {
            throw new IOException(
                $"The monitoring data root has insufficient free space for a replacement database " +
                $"(available={available}, required={MinimumReplacementDatabaseFreeBytes}).");
        }
    }

    internal MonitoringArchiveDiagnostics GetArchiveDiagnostics() => archive.GetDiagnostics();

    public MonitorPersistenceDiagnostics GetBackgroundDiagnostics()
    {
        var archiveDiagnostics = archive.GetDiagnostics();
        return new MonitorPersistenceDiagnostics(
            PendingArchives: archiveDiagnostics.PendingArchives,
            FailedArchives: archiveDiagnostics.FailedArchives,
            ArchiveFailure: archiveDiagnostics.LastError,
            ArchiveRawDatabaseRetained: archiveDiagnostics.ArchiveRawDatabaseRetained,
            ArchiveFailureOccurrenceId: archiveDiagnostics.FailureOccurrenceId,
            HasRecoveredInterruptedSession: Volatile.Read(ref recoveredInterruptedSessionCount) > 0,
            RecoveredInterruptedSessionOccurrenceId: recoveredInterruptedSessionOccurrenceId);
    }

    internal async Task<ActiveSegment> OpenInitialSegmentAsync(
        MonitoringSession session,
        MonitoringRotationOptions configuration,
        CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!initialized || activeStore is null || activeLease is null)
            {
                throw new InvalidOperationException("The monitoring persistence factory is not initialized.");
            }

            if (activeSessionId is not null)
            {
                throw new InvalidOperationException("A monitoring persistence session is already active.");
            }

            var persistence = CreateSegmentPersistence(activeStore, activeLease, session.SessionId, configuration, timeProvider);
            await persistence.StartAsync(session, cancellationToken);
            activeSessionId = session.SessionId;
            return new ActiveSegment(activeStore, activeLease, persistence);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    internal async Task ReleaseSessionAsync(SessionId sessionId)
    {
        await lifecycleGate.WaitAsync();
        try
        {
            if (activeSessionId == sessionId)
            {
                activeSessionId = null;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    internal async Task<RotationOutcome> RotateAsync(
        MonitoringSession session,
        ActiveSegment oldSegment,
        MonitoringRotationOptions configuration,
        CancellationToken cancellationToken)
    {
        await using var exclusive = await accessGate.AcquireExclusiveAsync(cancellationToken);
        await lifecycleGate.WaitAsync(cancellationToken);
        ActiveSegment? replacement = null;
        var generationId = Guid.NewGuid().ToString("N");
        var rawPath = Path.Combine(
            archive.ArchiveRoot,
            MonitoringArchiveCoordinator.SealedDirectoryName,
            $"monitoring-sealed-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{generationId}.db");
        var sourceWasSealed = false;
        var oldWriterTouched = false;
        var knownUnpersisted = 0L;
        try
        {
            EnsureCurrentSegment(oldSegment, session.SessionId);
            // This preflight must precede draining/disposal of the current
            // writer: insufficient space leaves the fixed active database and
            // its live writer untouched so sampling can safely continue.
            EnsureReplacementDatabaseSpace();
            // This waits for CompleteAndFlushAsync. A writer fault cannot be
            // mistaken for a drained old database before we release its lease
            // or checkpoint the fixed active file.
            oldWriterTouched = true;
            await oldSegment.Persistence.DisposeAsync();
            knownUnpersisted = oldSegment.Persistence.ConfirmedLostSamples;
            await oldSegment.Lease.DisposeAsync();
            activeLease = null;
            activeStore = null;
            await oldSegment.Store.CheckpointAndCloseAsync(cancellationToken);

            await archive.CreatePendingSealAsync(
                rawPath,
                session.SessionId,
                generationId,
                cancellationToken);
            File.Move(activeDatabasePath, rawPath);
            sourceWasSealed = true;

            // The replacement always starts at the fixed active path only
            // after the old, checkpointed primary has been sealed. No prepared
            // numbered database is ever promoted into an active name.
            var nextStore = new MonitoringSqliteStore(activeDatabasePath);
            await InitializeMonitoringStoreAsync(nextStore, cancellationToken);
            var nextLease = AgentWriteOwnerLease.Acquire(nextStore, ownerId);
            var nextPersistence = CreateSegmentPersistence(
                nextStore,
                nextLease,
                session.SessionId,
                configuration,
                timeProvider);
            try
            {
                await nextPersistence.StartAsync(session, cancellationToken);
                replacement = new ActiveSegment(nextStore, nextLease, nextPersistence);
            }
            catch
            {
                await nextPersistence.DisposeAsync();
                await nextLease.DisposeAsync();
                throw;
            }

            await archive.ActivateSealedArchiveAsync(generationId, cancellationToken);
            activeStore = replacement.Store;
            activeLease = replacement.Lease;
            if (!replacement.Lease.IsActive)
            {
                throw new InvalidOperationException("The replacement monitoring database lease was lost during rotation.");
            }
            return new RotationOutcome(
                replacement,
                Rotated: true,
                Failure: null,
                generationId,
                knownUnpersisted);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or TimeoutException
                or OperationCanceledException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            Exception rotationFailure = exception;
            if (!oldWriterTouched
                && !sourceWasSealed
                && activeSessionId == session.SessionId
                && ReferenceEquals(activeStore, oldSegment.Store)
                && ReferenceEquals(activeLease, oldSegment.Lease))
            {
                // The failure happened before the writer/lease/file state was
                // touched (for example the free-space preflight). Returning
                // the original segment avoids disposing a live writer merely
                // to recover from a condition that never began rotation.
                return new RotationOutcome(
                    Segment: oldSegment,
                    Rotated: false,
                    Failure: rotationFailure,
                    GenerationId: generationId,
                    ConfirmedLostSamples: oldSegment.Persistence.ConfirmedLostSamples);
            }

            knownUnpersisted = Math.Max(
                knownUnpersisted,
                oldSegment.Persistence.ConfirmedLostSamples);
            if (replacement is not null)
            {
                knownUnpersisted = Math.Max(
                    knownUnpersisted,
                    replacement.Persistence.ConfirmedLostSamples);
                try
                {
                    await replacement.Persistence.DisposeAsync();
                }
                catch (Exception cleanupFailure) when (
                    cleanupFailure is IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                        or TimeoutException
                        or Microsoft.Data.Sqlite.SqliteException)
                {
                    rotationFailure = CombineRotationFailures(rotationFailure, cleanupFailure);
                }
                finally
                {
                    await replacement.Lease.DisposeAsync();
                }

                replacement = null;
            }

            var restored = await TryRestoreOldSegmentAsync(
                session,
                rawPath,
                sourceWasSealed,
                configuration,
                // Once an old primary has been drained or renamed, cancellation
                // may stop sampling but must not abandon its file-state repair.
                CancellationToken.None);
            if (restored is not null)
            {
                if (sourceWasSealed && File.Exists(rawPath))
                {
                    // A replacement session was already valid at the fixed
                    // path when a later archive activation failed. Preserve
                    // the sealed source and reactivate its deferred worker;
                    // marking it aborted here would leak its only archive job.
                    try
                    {
                        await archive.ActivateSealedArchiveAsync(
                            generationId,
                            CancellationToken.None);
                    }
                    catch (Exception activationFailure) when (
                        activationFailure is IOException
                            or UnauthorizedAccessException
                            or InvalidOperationException
                            or TimeoutException
                            or Microsoft.Data.Sqlite.SqliteException)
                    {
                        // Keep both independently valid databases. A later
                        // startup can retry activating the pending raw record;
                        // the rotation itself must still return a usable fixed
                        // active database rather than strand its buffer.
                        rotationFailure = CombineRotationFailures(
                            rotationFailure,
                            activationFailure);
                    }
                }
                else
                {
                    try
                    {
                        await archive.MarkRotationAbortedAsync(
                            generationId,
                            rotationFailure.Message,
                            CancellationToken.None);
                    }
                    catch (Exception ledgerFailure) when (
                        ledgerFailure is IOException
                            or UnauthorizedAccessException
                            or InvalidOperationException
                            or TimeoutException
                            or Microsoft.Data.Sqlite.SqliteException)
                    {
                        rotationFailure = CombineRotationFailures(rotationFailure, ledgerFailure);
                    }
                }
                activeStore = restored.Store;
                activeLease = restored.Lease;
                if (!restored.Lease.IsActive)
                {
                    throw new InvalidOperationException("The restored monitoring database lease was lost during rotation recovery.");
                }
                return new RotationOutcome(
                    restored,
                    Rotated: false,
                    Failure: rotationFailure,
                    generationId,
                    knownUnpersisted);
            }

            return new RotationOutcome(
                Segment: null,
                Rotated: false,
                Failure: rotationFailure,
                generationId,
                knownUnpersisted);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Exception? checkpointFailure = null;
        await using var exclusive = await accessGate.AcquireExclusiveAsync(CancellationToken.None);
        await lifecycleGate.WaitAsync();
        try
        {
            if (disposed)
            {
                return;
            }

            if (activeSessionId is not null)
            {
                // The segment owns a live bounded writer. Releasing its lease
                // or checkpointing the file from under that writer would turn
                // an orderly shutdown failure into silent sample loss.
                throw new InvalidOperationException(
                    "The active monitoring session must stop before its persistence factory is disposed.");
            }

            disposed = true;
            try
            {
                if (activeStore is not null)
                {
                    await activeStore.CheckpointAndCloseAsync(CancellationToken.None);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or Microsoft.Data.Sqlite.SqliteException)
            {
                checkpointFailure = exception;
            }

            activeLease?.Dispose();
            activeLease = null;
        }
        finally
        {
            lifecycleGate.Release();
        }

        await archive.DisposeAsync();
        lifecycleGate.Dispose();
        if (checkpointFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(checkpointFailure)
                .Throw();
        }
    }

    private async Task EnsureActiveDatabaseAsync(CancellationToken cancellationToken)
    {
        var store = new MonitoringSqliteStore(activeDatabasePath);
        await InitializeMonitoringStoreAsync(store, cancellationToken);
        var lease = AgentWriteOwnerLease.Acquire(store, ownerId);
        try
        {
            var recovered = await new MonitorSessionRepository(store, lease).RecoverInterruptedSessionsAsync(
                DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken);
            if (recovered > 0)
            {
                Interlocked.Exchange(ref recoveredInterruptedSessionCount, recovered);
                recoveredInterruptedSessionOccurrenceId = $"interrupted:{Guid.NewGuid():N}";
            }
            activeStore = store;
            activeLease = lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private async Task RecoverPendingSealsAsync(CancellationToken cancellationToken)
    {
        foreach (var record in archive.Snapshot()
                     .Where(item => item.Stage == MonitoringArchiveStage.PendingSeal))
        {
            var rawExists = File.Exists(record.RawDatabasePath);
            var activeExists = File.Exists(activeDatabasePath);
            if (rawExists)
            {
                EnsureSealedDatabaseHasNoSidecars(record.RawDatabasePath);
                await RecoverSealedInterruptedSessionAsync(record, cancellationToken);
                await archive.ActivateSealedArchiveAsync(record.GenerationId, cancellationToken);
            }
            else if (activeExists)
            {
                await archive.MarkRotationAbortedAsync(
                    record.GenerationId,
                    "Rotation ended before the sealed database rename; the active source database was retained.",
                    cancellationToken);
            }
            else
            {
                throw new IOException("A pending monitoring rotation has neither an active nor a sealed database.");
            }
        }
        }

    private async Task RecoverSealedInterruptedSessionAsync(
        MonitoringArchiveRecord record,
        CancellationToken cancellationToken)
    {
        var store = new MonitoringSqliteStore(record.RawDatabasePath);
        await store.InitializeAsync(cancellationToken);
        await using (var lease = AgentWriteOwnerLease.Acquire(
                         store,
                         $"{ownerId}-recovery-{record.GenerationId}"))
        {
            await new MonitorSessionRepository(store, lease).RecoverInterruptedSessionsAsync(
                DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken);
        }

        await store.CheckpointAndCloseAsync(cancellationToken);
    }

    private async Task<ActiveSegment?> TryRestoreOldSegmentAsync(
        MonitoringSession session,
        string rawPath,
        bool sourceWasSealed,
        MonitoringRotationOptions configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            // A failure after a new fixed-path database has appeared must not
            // move that file aside or overwrite it. It may contain the only
            // valid replacement metadata. Only move the sealed source back
            // when the fixed active path is genuinely absent.
            if (sourceWasSealed
                && !File.Exists(activeDatabasePath)
                && File.Exists(rawPath))
            {
                EnsureSealedDatabaseHasNoSidecars(rawPath);
                File.Move(rawPath, activeDatabasePath);
            }

            if (!File.Exists(activeDatabasePath))
            {
                return null;
            }

            if (activeLease is not null)
            {
                await activeLease.DisposeAsync();
                activeLease = null;
            }
            activeStore = null;

            var restoredStore = new MonitoringSqliteStore(activeDatabasePath);
            await InitializeMonitoringStoreAsync(restoredStore, cancellationToken);
            var restoredLease = AgentWriteOwnerLease.Acquire(restoredStore, ownerId);
            var restoredPersistence = CreateSegmentPersistence(
                restoredStore,
                restoredLease,
                session.SessionId,
                configuration,
                timeProvider);
            try
            {
                await restoredPersistence.ResumeAsync(session, cancellationToken);
                return new ActiveSegment(restoredStore, restoredLease, restoredPersistence);
            }
            catch
            {
                await restoredPersistence.DisposeAsync();
                await restoredLease.DisposeAsync();
                throw;
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            _ = exception;
            return null;
        }
    }

    internal async Task<RotationOutcome> TryResumePausedRotationAsync(
        MonitoringSession session,
        string generationId,
        MonitoringRotationOptions configuration,
        CancellationToken cancellationToken)
    {
        await using var exclusive = await accessGate.AcquireExclusiveAsync(cancellationToken);
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (disposed || activeSessionId != session.SessionId)
            {
                return new RotationOutcome(null, false, null, generationId, 0);
            }

            var record = archive.Snapshot().SingleOrDefault(
                item => string.Equals(item.GenerationId, generationId, StringComparison.Ordinal));
            var restoredSealedSourceToActive = false;
            if (!File.Exists(activeDatabasePath))
            {
                // The first repair attempt can itself fail while moving the
                // sealed primary back (for example an antivirus/CSV handle
                // temporarily holds it). Do not leave that generation paused
                // forever: every bounded retry rechecks the same ledger/file
                // facts and retries only this safe reverse move.
                if (record is null
                    || record.Stage != MonitoringArchiveStage.PendingSeal
                    || !File.Exists(record.RawDatabasePath))
                {
                    return new RotationOutcome(null, false, null, generationId, 0);
                }

                EnsureSealedDatabaseHasNoSidecars(record.RawDatabasePath);
                File.Move(record.RawDatabasePath, activeDatabasePath);
                restoredSealedSourceToActive = true;
            }

            if (activeLease is not null)
            {
                await activeLease.DisposeAsync();
                activeLease = null;
            }
            activeStore = null;

            var store = new MonitoringSqliteStore(activeDatabasePath);
            await InitializeMonitoringStoreAsync(store, cancellationToken);
            var lease = AgentWriteOwnerLease.Acquire(store, ownerId);
            var persistence = CreateSegmentPersistence(
                store,
                lease,
                session.SessionId,
                configuration,
                timeProvider);
            try
            {
                var existing = await new MonitorSessionRepository(store, lease).GetAsync(
                    session.SessionId,
                    cancellationToken);
                if (existing is null)
                {
                    await persistence.StartAsync(session, cancellationToken);
                }
                else
                {
                    await persistence.ResumeAsync(session, cancellationToken);
                }

                Exception? archiveFailure = null;
                try
                {
                    if (record is not null
                        && record.Stage == MonitoringArchiveStage.PendingSeal)
                    {
                        if (restoredSealedSourceToActive)
                        {
                            await archive.MarkRotationAbortedAsync(
                                generationId,
                                "Rotation recovery restored the sealed source to the fixed active database.",
                                cancellationToken);
                        }
                        else if (File.Exists(record.RawDatabasePath))
                        {
                            await archive.ActivateSealedArchiveAsync(generationId, cancellationToken);
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                        or TimeoutException
                        or Microsoft.Data.Sqlite.SqliteException)
                {
                    // The active database/persistence is already usable. Keep
                    // that fact rather than tearing it down because the
                    // recovery ledger could not be updated; the error remains
                    // visible in the rotation outcome for a later repair.
                    archiveFailure = exception;
                }

                var replacement = new ActiveSegment(store, lease, persistence);
                activeStore = store;
                activeLease = lease;
                return new RotationOutcome(replacement, false, archiveFailure, generationId, 0);
            }
            catch
            {
                await persistence.DisposeAsync();
                await lease.DisposeAsync();
                throw;
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or TimeoutException
                or OperationCanceledException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            return new RotationOutcome(null, false, exception, generationId, 0);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private Task InitializeMonitoringStoreAsync(
        MonitoringSqliteStore store,
        CancellationToken cancellationToken) =>
        initializeMonitoringStoreAsync(store, cancellationToken);

    private static void EnsureSealedDatabaseHasNoSidecars(string databasePath)
    {
        foreach (var sidecar in new[]
                 {
                     databasePath + "-wal",
                     databasePath + "-shm",
                     databasePath + "-journal"
                 })
        {
            if (File.Exists(sidecar))
            {
                throw new IOException("A sealed monitoring database has SQLite sidecars and cannot be recovered as self-contained.");
            }
        }
    }

    private static Exception CombineRotationFailures(
        Exception original,
        Exception additional) =>
        new IOException(
            "Monitoring rotation encountered a failure while restoring a safe database state.",
            new AggregateException(original, additional));

    private static SqliteMonitorSessionPersistence CreateSegmentPersistence(
        MonitoringSqliteStore store,
        AgentWriteOwnerLease lease,
        SessionId sessionId,
        MonitoringRotationOptions configuration,
        TimeProvider timeProvider) =>
        new(
            store,
            lease,
            sessionId,
            configuration.WriterChannelCapacity,
            configuration.WriterMaximumBatchSize,
            configuration.WriterMaximumBatchDelay,
            timeProvider);

    private void EnsureCurrentSegment(ActiveSegment segment, SessionId sessionId)
    {
        if (activeStore is null
            || activeLease is null
            || activeSessionId != sessionId
            || !ReferenceEquals(activeStore, segment.Store)
            || !ReferenceEquals(activeLease, segment.Lease))
        {
            throw new InvalidOperationException("The monitoring rotation segment is no longer current.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(RotatingMonitorSessionPersistenceFactory));
        }
    }

    internal sealed record ActiveSegment(
        MonitoringSqliteStore Store,
        AgentWriteOwnerLease Lease,
        SqliteMonitorSessionPersistence Persistence);

    internal sealed record RotationOutcome(
        ActiveSegment? Segment,
        bool Rotated,
        Exception? Failure,
        string GenerationId,
        long ConfirmedLostSamples);
}

internal sealed class RotatingMonitorSessionPersistence :
    IMonitorSessionPersistence,
    IMonitorSessionPersistenceDiagnostics
{
    private readonly RotatingMonitorSessionPersistenceFactory factory;
    private readonly SessionId expectedSessionId;
    private readonly MonitoringRotationOptions options;
    private readonly object gate = new();
    private readonly Queue<BufferedSample> rotationBuffer = [];
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource rotationCancellation = new();
    private readonly SemaphoreSlim droppedSamplesGate = new(1, 1);
    private RotatingMonitorSessionPersistenceFactory.ActiveSegment? segment;
    private MonitoringSession? session;
    private Task? thresholdTask;
    private bool started;
    private bool completed;
    private bool rotating;
    private bool paused;
    // This is distinct from a failed file-state rotation. The fixed database
    // still exists and the old lease remains valid; only its bounded batch
    // writer faulted and must prove that it can write again before buffered
    // samples are replayed.
    private bool writerRecoveryPending;
    private string? pausedGenerationId;
    private DateTimeOffset nextRotationAttemptUtc;
    // Internal losses are shown independently from the coordinator's rejected
    // source samples.  The second counter tracks both kinds until their
    // durable session total has accepted them; it is never exchanged away
    // before the database write succeeds.
    private long internalConfirmedLostSamples;
    private long pendingDurableDroppedSamples;
    private object? countedFaultedWriterIdentity;
    private Exception? lastRotationFailure;
    private string? rotationFailureOccurrenceId;

    public RotatingMonitorSessionPersistence(
        RotatingMonitorSessionPersistenceFactory factory,
        SessionId expectedSessionId,
        MonitoringRotationOptions options,
        TimeProvider timeProvider)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.expectedSessionId = expectedSessionId;
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public MonitorPersistenceDiagnostics GetDiagnostics()
    {
        var archiveDiagnostics = factory.GetArchiveDiagnostics();
        var backgroundDiagnostics = factory.GetBackgroundDiagnostics();
        lock (gate)
        {
            var segmentDiagnostics = segment?.Persistence.GetDiagnostics()
                ?? new MonitorPersistenceDiagnostics();
            var bufferedSamples = rotationBuffer.Count;
            var oldestBufferedMilliseconds = bufferedSamples == 0
                ? 0L
                : Math.Max(
                    0,
                    (long)timeProvider.GetElapsedTime(
                        rotationBuffer.Peek().EnqueuedTimestamp,
                        timeProvider.GetTimestamp()).TotalMilliseconds);
            var pendingSamples = segmentDiagnostics.PendingSamples > int.MaxValue - bufferedSamples
                ? int.MaxValue
                : segmentDiagnostics.PendingSamples + bufferedSamples;
            var oldestPendingMilliseconds = Math.Max(
                segmentDiagnostics.OldestPendingMilliseconds,
                oldestBufferedMilliseconds);
            var confirmedUnpersisted = Math.Max(0, Interlocked.Read(ref internalConfirmedLostSamples));
            // Once a writer fault has latched the recovery pause, its exact
            // pending count has been copied into internalConfirmedLostSamples
            // so it survives writer replacement. Do not add that same faulted
            // writer's count a second time while it is still retained only for
            // failure diagnostics.
            var activeUnpersisted = ReferenceEquals(
                    segment?.Persistence.WriterIdentity,
                    countedFaultedWriterIdentity)
                ? 0
                : Math.Max(0, segmentDiagnostics.ConfirmedLostSamples);
            var knownUnpersisted = confirmedUnpersisted > long.MaxValue - activeUnpersisted
                ? long.MaxValue
                : confirmedUnpersisted + activeUnpersisted;
            return new MonitorPersistenceDiagnostics(
                ConfirmedLostSamples: knownUnpersisted,
                PendingSamples: pendingSamples,
                OldestPendingMilliseconds: oldestPendingMilliseconds,
                IsDelayed: segmentDiagnostics.IsDelayed
                    || (bufferedSamples > 0
                        && oldestBufferedMilliseconds >= SqliteMonitorSessionPersistence.PersistenceDelayThreshold.TotalMilliseconds),
                IsPaused: paused,
                RotationInProgress: rotating,
                RotationBufferedSamples: bufferedSamples,
                Failure: lastRotationFailure?.Message ?? segmentDiagnostics.Failure,
                PendingArchives: archiveDiagnostics.PendingArchives,
                FailedArchives: archiveDiagnostics.FailedArchives,
                ArchiveFailure: archiveDiagnostics.LastError,
                ArchiveRawDatabaseRetained: archiveDiagnostics.ArchiveRawDatabaseRetained,
                FailureOccurrenceId: rotationFailureOccurrenceId
                    ?? segmentDiagnostics.FailureOccurrenceId,
                ArchiveFailureOccurrenceId: archiveDiagnostics.FailureOccurrenceId,
                HasRecoveredInterruptedSession: backgroundDiagnostics.HasRecoveredInterruptedSession,
                RecoveredInterruptedSessionOccurrenceId: backgroundDiagnostics.RecoveredInterruptedSessionOccurrenceId);
        }
    }

    public async Task StartAsync(
        MonitoringSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.SessionId != expectedSessionId)
        {
            throw new InvalidOperationException("The monitoring session identity does not match its persistence.");
        }

        lock (gate)
        {
            if (started)
            {
                throw new InvalidOperationException("The monitoring persistence session is already started.");
            }
        }

        var opened = await factory.OpenInitialSegmentAsync(session, options, cancellationToken);
        lock (gate)
        {
            this.session = session;
            segment = opened;
            started = true;
            thresholdTask = Task.Run(MonitorThresholdAsync);
        }
    }

    public bool TryWrite(MonitorSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (gate)
        {
            if (!started || completed || sample.SessionId != expectedSessionId)
            {
                return false;
            }

            // A failed rotation may need a later repair attempt. Keep sampling
            // bounded during that interval just as we do during the normal
            // exclusive switch; callers receive false only after the bounded
            // buffer is genuinely full.
            if (rotating || paused)
            {
                if (rotationBuffer.Count >= options.RotationBufferCapacity)
                {
                    return false;
                }

                rotationBuffer.Enqueue(new BufferedSample(sample, timeProvider.GetTimestamp()));
                return true;
            }

            return segment!.Persistence.TryWrite(sample);
        }
    }

    public async Task AddDroppedSamplesAsync(long count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return;
        }

        // Source-enqueue rejection is already exposed by the monitoring
        // coordinator. Track it durably without adding it a second time to
        // the persistence-only confirmed-loss diagnostic.
        AddPendingDurableLoss(count);
        await PersistPendingDroppedSamplesAsync(cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await WaitForRotationAsync(cancellationToken);
        var current = GetActiveSegment();
        await current.Persistence.FlushAsync(cancellationToken);
    }

    public async Task CompleteAsync(
        MonitoringSessionState finalState,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken)
    {
        rotationCancellation.Cancel();
        await WaitForThresholdTaskAsync();
        if (IsWriterRecoveryPending())
        {
            // A space/lock failure may have cleared while the user was
            // stopping. Make one orderly recovery attempt before declaring
            // accepted buffered samples unsaved.
            await TryResumeFaultedWriterAsync(CancellationToken.None);
        }

        long bufferedAtStop = 0;
        var stoppedWithoutWritableSegment = false;
        lock (gate)
        {
            if (paused || segment is null)
            {
                stoppedWithoutWritableSegment = true;
                bufferedAtStop = rotationBuffer.Count;
                rotationBuffer.Clear();
                if (bufferedAtStop > 0)
                {
                    AddInternalConfirmedLoss(bufferedAtStop, durable: false);
                }

                completed = true;
            }
        }

        if (stoppedWithoutWritableSegment)
        {
            await factory.ReleaseSessionAsync(expectedSessionId);
            throw new IOException(
                "Monitoring persistence stopped while a rotation could not restore a writable database; accepted buffered samples were not saved.",
                lastRotationFailure);
        }

        await PersistPendingDroppedSamplesAsync(cancellationToken);
        var current = GetActiveSegment();
        await current.Persistence.CompleteAsync(finalState, endedAtUtc, cancellationToken);
        lock (gate)
        {
            completed = true;
        }

        await factory.ReleaseSessionAsync(expectedSessionId);
    }

    public async ValueTask DisposeAsync()
    {
        rotationCancellation.Cancel();
        try
        {
            await WaitForThresholdTaskAsync();
            RotatingMonitorSessionPersistenceFactory.ActiveSegment? current;
            lock (gate)
            {
                current = segment;
            }

            if (current is not null)
            {
                await current.Persistence.DisposeAsync();
            }
        }
        finally
        {
            // A faulted writer still needs its factory session released. The
            // caller may then close the factory without releasing a live
            // lease or hiding the original persistence failure.
            await factory.ReleaseSessionAsync(expectedSessionId);
            rotationCancellation.Dispose();
        }
    }

    private async Task MonitorThresholdAsync()
    {
        try
        {
            while (!rotationCancellation.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(options.ThresholdPollInterval, rotationCancellation.Token);
                    if (IsPaused())
                    {
                        if (DateTimeOffset.UtcNow >= nextRotationAttemptUtc)
                        {
                            if (IsWriterRecoveryPending())
                            {
                                await TryResumeFaultedWriterAsync(rotationCancellation.Token);
                            }
                            else
                            {
                                await TryResumePausedRotationAsync(rotationCancellation.Token);
                            }
                        }
                        continue;
                    }

                    if (TryPauseForWriterFailure())
                    {
                        continue;
                    }

                    if (DateTimeOffset.UtcNow < nextRotationAttemptUtc
                        || factory.GetActiveDatabaseBytes() < options.RotationThresholdBytes)
                    {
                        continue;
                    }

                    await BeginRotationAsync(rotationCancellation.Token);
                }
                catch (OperationCanceledException) when (rotationCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // A rotation-monitor failure must remain observable and
                    // retryable. Do not fault the only loop that can release
                    // a paused bounded buffer on a later healthy attempt.
                    lock (gate)
                    {
                        lastRotationFailure = exception;
                        rotationFailureOccurrenceId = $"rotation:{expectedSessionId.Value:N}:{Guid.NewGuid():N}";
                        rotating = false;
                        nextRotationAttemptUtc = DateTimeOffset.UtcNow + options.FailureRetryDelay;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (rotationCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task BeginRotationAsync(CancellationToken cancellationToken)
    {
        MonitoringSession localSession;
        RotatingMonitorSessionPersistenceFactory.ActiveSegment old;
        var fallbackGenerationId = Guid.NewGuid().ToString("N");
        lock (gate)
        {
            if (!started || completed || rotating || paused || session is null || segment is null)
            {
                return;
            }

            localSession = session;
            old = segment;
        }

        lock (gate)
        {
            if (completed || rotating || paused || segment is null || !ReferenceEquals(segment, old))
            {
                return;
            }

            rotating = true;
        }

        RotatingMonitorSessionPersistenceFactory.RotationOutcome outcome;
        try
        {
            outcome = await factory.RotateAsync(
                localSession,
                old,
                options,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation before the factory acquired its exclusive lease has
            // not touched the old writer. Keep that segment rather than
            // pausing/recreating a concurrent writer during StopAsync.
            outcome = new(
                Segment: old,
                Rotated: false,
                Failure: null,
                GenerationId: fallbackGenerationId,
                ConfirmedLostSamples: 0);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or TimeoutException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            outcome = new(
                Segment: null,
                Rotated: false,
                Failure: exception,
                GenerationId: fallbackGenerationId,
                ConfirmedLostSamples: 0);
        }
        catch (Exception exception)
        {
            // Keep the retry loop alive even for an unforeseen implementation
            // failure. The normal outcome path below records the fault,
            // retains the bounded buffer, and retries recovery.
            outcome = new(
                Segment: null,
                Rotated: false,
                Failure: exception,
                GenerationId: fallbackGenerationId,
                ConfirmedLostSamples: 0);
        }

        lock (gate)
        {
            checked
            {
                AddInternalConfirmedLoss(outcome.ConfirmedLostSamples, durable: true);
            }
            if (outcome.Failure is not null)
            {
                lastRotationFailure = outcome.Failure;
                rotationFailureOccurrenceId = $"rotation:{expectedSessionId.Value:N}:{Guid.NewGuid():N}";
            }
            else if (outcome.Rotated)
            {
                lastRotationFailure = null;
                rotationFailureOccurrenceId = null;
            }
            if (outcome.Segment is null)
            {
                paused = true;
                writerRecoveryPending = false;
                pausedGenerationId = outcome.GenerationId;
                rotating = false;
                nextRotationAttemptUtc = DateTimeOffset.UtcNow + options.FailureRetryDelay;
                return;
            }

            segment = outcome.Segment;
            DrainRotationBufferLocked();
            paused = false;
            writerRecoveryPending = false;
            pausedGenerationId = null;
            rotating = false;
            if (outcome.Failure is not null)
            {
                nextRotationAttemptUtc = DateTimeOffset.UtcNow + options.FailureRetryDelay;
            }
        }
    }

    private async Task TryResumePausedRotationAsync(CancellationToken cancellationToken)
    {
        MonitoringSession localSession;
        string generationId;
        lock (gate)
        {
            if (!paused
                || rotating
                || completed
                || session is null
                || string.IsNullOrWhiteSpace(pausedGenerationId))
            {
                return;
            }

            localSession = session;
            generationId = pausedGenerationId;
            rotating = true;
        }

        RotatingMonitorSessionPersistenceFactory.RotationOutcome outcome;
        try
        {
            outcome = await factory.TryResumePausedRotationAsync(
                localSession,
                generationId,
                options,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or TimeoutException
                or OperationCanceledException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            outcome = new(null, false, exception, generationId, 0);
        }
        catch (Exception exception)
        {
            outcome = new(null, false, exception, generationId, 0);
        }

        lock (gate)
        {
            checked
            {
                AddInternalConfirmedLoss(outcome.ConfirmedLostSamples, durable: true);
            }
            if (outcome.Failure is not null)
            {
                lastRotationFailure = outcome.Failure;
                rotationFailureOccurrenceId = $"rotation:{expectedSessionId.Value:N}:{Guid.NewGuid():N}";
            }
            else if (outcome.Segment is not null)
            {
                lastRotationFailure = null;
                rotationFailureOccurrenceId = null;
            }
            if (outcome.Segment is not null)
            {
                segment = outcome.Segment;
                DrainRotationBufferLocked();
                paused = false;
                writerRecoveryPending = false;
                pausedGenerationId = null;
            }
            else
            {
                nextRotationAttemptUtc = DateTimeOffset.UtcNow + options.FailureRetryDelay;
            }

            rotating = false;
        }
    }

    private bool IsPaused()
    {
        lock (gate)
        {
            return paused && !completed;
        }
    }

    private bool IsWriterRecoveryPending()
    {
        lock (gate)
        {
            return paused && writerRecoveryPending && !completed;
        }
    }

    private bool TryPauseForWriterFailure()
    {
        lock (gate)
        {
            if (!started
                || completed
                || rotating
                || paused
                || segment is null
                || segment.Persistence.BackgroundFailure is not { } failure)
            {
                return false;
            }

            CaptureCurrentFaultedWriterLossLocked();
            paused = true;
            writerRecoveryPending = true;
            pausedGenerationId = null;
            lastRotationFailure = failure;
            rotationFailureOccurrenceId = $"writer:{expectedSessionId.Value:N}:{Guid.NewGuid():N}";
            nextRotationAttemptUtc = DateTimeOffset.UtcNow + options.FailureRetryDelay;
            return true;
        }
    }

    private async Task TryResumeFaultedWriterAsync(CancellationToken cancellationToken)
    {
        RotatingMonitorSessionPersistenceFactory.ActiveSegment current;
        lock (gate)
        {
            if (!paused
                || !writerRecoveryPending
                || rotating
                || completed
                || segment is null)
            {
                return;
            }

            current = segment;
            CaptureCurrentFaultedWriterLossLocked();
            rotating = true;
        }

        try
        {
            await current.Persistence.RecoverFaultedWriterAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (gate)
            {
                if (ReferenceEquals(segment, current))
                {
                    rotating = false;
                }
            }

            return;
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                if (ReferenceEquals(segment, current))
                {
                    paused = true;
                    writerRecoveryPending = true;
                    rotating = false;
                    lastRotationFailure = exception;
                    rotationFailureOccurrenceId ??= $"writer:{expectedSessionId.Value:N}:{Guid.NewGuid():N}";
                    nextRotationAttemptUtc = DateTimeOffset.UtcNow + options.FailureRetryDelay;
                }
            }

            return;
        }

        lock (gate)
        {
            if (!ReferenceEquals(segment, current) || completed)
            {
                rotating = false;
                return;
            }

            DrainRotationBufferLocked();
            paused = false;
            writerRecoveryPending = false;
            rotating = false;
            lastRotationFailure = null;
            rotationFailureOccurrenceId = null;
        }

        try
        {
            await PersistPendingDroppedSamplesAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or TimeoutException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            lock (gate)
            {
                if (ReferenceEquals(segment, current) && !completed)
                {
                    paused = true;
                    writerRecoveryPending = true;
                    lastRotationFailure = exception;
                    rotationFailureOccurrenceId ??= $"writer:{expectedSessionId.Value:N}:{Guid.NewGuid():N}";
                    nextRotationAttemptUtc = DateTimeOffset.UtcNow + options.FailureRetryDelay;
                }
            }
        }
    }

    // Must be called with <see cref="gate"/> held. The writer reference, not
    // the paused state, is the loss identity: a failed durable-count update
    // can leave a healthy replacement paused, and that replacement may later
    // fault with a new exact pending count.
    private void CaptureCurrentFaultedWriterLossLocked()
    {
        if (segment is null || segment.Persistence.BackgroundFailure is null)
        {
            return;
        }

        var writerIdentity = segment.Persistence.WriterIdentity;
        if (writerIdentity is null
            || ReferenceEquals(writerIdentity, countedFaultedWriterIdentity))
        {
            return;
        }

        AddInternalConfirmedLoss(segment.Persistence.ConfirmedLostSamples, durable: true);
        countedFaultedWriterIdentity = writerIdentity;
    }

    private void DrainRotationBufferLocked()
    {
        while (rotationBuffer.TryDequeue(out var buffered))
        {
            if (segment is null || !segment.Persistence.TryWrite(buffered.Sample))
            {
                AddInternalConfirmedLoss(1, durable: true);
            }
        }
    }

    private RotatingMonitorSessionPersistenceFactory.ActiveSegment GetActiveSegment()
    {
        lock (gate)
        {
            if (!started || segment is null || paused)
            {
                throw new IOException("Monitoring persistence is not writable.");
            }

            return segment;
        }
    }

    private async Task WaitForRotationAsync(CancellationToken cancellationToken)
    {
        // The threshold loop itself lasts for the entire monitoring session;
        // wait only while its one in-flight rotation has latched the writer.
        while (true)
        {
            lock (gate)
            {
                if (!rotating)
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private async Task WaitForThresholdTaskAsync(
        CancellationToken cancellationToken = default)
    {
        Task? task;
        lock (gate)
        {
            task = thresholdTask;
        }

        if (task is null)
        {
            return;
        }

        await task.WaitAsync(cancellationToken);
    }

    private void AddInternalConfirmedLoss(long count, bool durable)
    {
        if (count <= 0)
        {
            return;
        }

        checked
        {
            Interlocked.Add(ref internalConfirmedLostSamples, count);
        }

        if (durable)
        {
            AddPendingDurableLoss(count);
        }
    }

    private void AddPendingDurableLoss(long count)
    {
        if (count <= 0)
        {
            return;
        }

        checked
        {
            Interlocked.Add(ref pendingDurableDroppedSamples, count);
        }
    }

    private async Task PersistPendingDroppedSamplesAsync(CancellationToken cancellationToken)
    {
        await droppedSamplesGate.WaitAsync(cancellationToken);
        try
        {
            var pending = Interlocked.Read(ref pendingDurableDroppedSamples);
            if (pending == 0)
            {
                return;
            }

            var current = GetActiveSegment();
            await current.Persistence.AddDroppedSamplesAsync(pending, cancellationToken);
            Interlocked.Add(ref pendingDurableDroppedSamples, -pending);
        }
        finally
        {
            droppedSamplesGate.Release();
        }
    }

    private sealed record BufferedSample(MonitorSample Sample, long EnqueuedTimestamp);
}

internal sealed class MonitoringDatabaseAccessGate
{
    private readonly object gate = new();
    private TaskCompletionSource stateChanged = NewSignal();
    private int readers;
    private bool exclusiveRequested;
    private bool exclusiveActive;

    public object SyncRoot => gate;

    public async Task<IAsyncDisposable> AcquireReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (!exclusiveRequested && !exclusiveActive)
                {
                    readers++;
                    return new Lease(ReleaseRead);
                }

                wait = stateChanged.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    public async Task<IAsyncDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (exclusiveRequested || exclusiveActive)
            {
                throw new InvalidOperationException("A monitoring database rotation is already in progress.");
            }

            exclusiveRequested = true;
        }

        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (readers == 0 && !exclusiveActive)
                {
                    exclusiveActive = true;
                    return new Lease(ReleaseExclusive);
                }

                wait = stateChanged.Task;
            }

            try
            {
                await wait.WaitAsync(cancellationToken);
            }
            catch
            {
                lock (gate)
                {
                    exclusiveRequested = false;
                    Pulse();
                }

                throw;
            }
        }
    }

    private void ReleaseRead()
    {
        lock (gate)
        {
            if (readers <= 0)
            {
                throw new InvalidOperationException("A monitoring reader lease was released twice.");
            }

            readers--;
            if (readers == 0)
            {
                Pulse();
            }
        }
    }

    private void ReleaseExclusive()
    {
        lock (gate)
        {
            if (!exclusiveActive)
            {
                throw new InvalidOperationException("A monitoring rotation lease was released twice.");
            }

            exclusiveActive = false;
            exclusiveRequested = false;
            Pulse();
        }
    }

    private void Pulse()
    {
        var previous = stateChanged;
        stateChanged = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Lease(Action release) : IAsyncDisposable
    {
        private Action? release = release;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref release, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
