using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class SqliteRepositoryTests
{
    [Fact]
    public async Task SchemaVersionCanBeReadAndUnknownNewerVersionIsPreservedAndRejected()
    {
        await using var database = await RepositoryTemporaryDatabase.CreateAsync();
        var reader = new SqliteSchemaVersionReader(database.Store);

        var current = await reader.EnsureSupportedAsync();

        Assert.NotNull(current);
        Assert.Equal(WinPoolSqliteStore.CurrentSchemaVersion, current.Version);

        var unknownVersion = WinPoolSqliteStore.CurrentSchemaVersion + 1;
        await using (var connection = await database.Store.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE schema_info
                SET schema_version = $version
                WHERE singleton = 1;
                """;
            command.Parameters.AddWithValue("$version", unknownVersion);
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<UnsupportedSqliteSchemaVersionException>(
            () => database.Store.InitializeAsync());
        Assert.Equal(unknownVersion, exception.ActualVersion);

        var observed = await reader.ReadAsync();
        Assert.NotNull(observed);
        Assert.Equal(unknownVersion, observed.Version);
    }

    [Fact]
    public async Task AgentWriteOwnerLeaseIsExclusivePerDatabaseAndCanBeReleased()
    {
        await using var database = await RepositoryTemporaryDatabase.CreateAsync();
        await using var first = AgentWriteOwnerLease.Acquire(database.Store, "agent-a");

        var exception = Assert.Throws<AgentWriteOwnershipException>(
            () => AgentWriteOwnerLease.Acquire(database.Store, "agent-b"));

        Assert.Contains("agent-a", exception.Message, StringComparison.Ordinal);
        await first.DisposeAsync();

        await using var replacement =
            AgentWriteOwnerLease.Acquire(database.Store, "agent-b");
        Assert.True(replacement.IsActive);
    }

    [Fact]
    public async Task SessionAndDeviceRepositoriesRoundTripAndReadInstancesNeedNoWriteLease()
    {
        await using var database = await RepositoryTemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var sessionWriter = new MonitorSessionRepository(database.Store, lease);
        var deviceWriter = new MonitorDeviceRepository(database.Store, lease);
        var sessionId = SessionId.New();
        var started = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_123);

        await sessionWriter.CreateAsync(
            new PersistedMonitorSession(
                sessionId,
                started,
                EndedAtUtc: null,
                "Stopwatch+UTC",
                MonitoringSessionState.Running,
                DroppedSamples: 0));
        await deviceWriter.UpsertAsync(
            new PersistedMonitorDevice(
                sessionId,
                "device-hash",
                "物理磁盘 0",
                SourceKind: 1));

        var readOnlySessions = new MonitorSessionRepository(database.Store);
        var readOnlyDevices = new MonitorDeviceRepository(database.Store);
        var actualSession = await readOnlySessions.GetAsync(sessionId);
        var actualDevices = await readOnlyDevices.ListAsync(sessionId);

        Assert.NotNull(actualSession);
        Assert.Equal(started, actualSession.StartedAtUtc);
        Assert.Equal(MonitoringSessionState.Running, actualSession.State);
        var device = Assert.Single(actualDevices);
        Assert.Equal("device-hash", device.DeviceId);
        Assert.Equal("物理磁盘 0", device.DisplayName);

        await Assert.ThrowsAsync<AgentWriteOwnershipException>(
            () => readOnlyDevices.UpsertAsync(device));
    }

    [Fact]
    public async Task SamplePagesUseTimestampAndRowIdWithoutSkippingEqualTimestamps()
    {
        await using var database = await RepositoryTemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var (sessionId, target, start) =
            await CreateSessionAndDeviceAsync(database.Store, lease);
        var writer = new MonitorSampleRepository(database.Store, lease);
        var batch = new[]
        {
            Sample(sessionId, target, start, 10),
            Sample(sessionId, target, start, 11),
            Sample(sessionId, target, start.AddMilliseconds(1), 12),
            Sample(sessionId, target, start.AddMilliseconds(2), 13),
            Sample(sessionId, target, start.AddMilliseconds(3), 14)
        };
        await writer.WriteBatchAsync(batch);

        var query = new MonitorSampleRepository(database.Store);
        var first = await query.ReadPageAsync(
            sessionId,
            start,
            start.AddSeconds(1),
            pageSize: 2);
        var second = await query.ReadPageAsync(
            sessionId,
            start,
            start.AddSeconds(1),
            pageSize: 2,
            first.Continuation);
        var third = await query.ReadPageAsync(
            sessionId,
            start,
            start.AddSeconds(1),
            pageSize: 2,
            second.Continuation);

        Assert.NotNull(first.Continuation);
        Assert.NotNull(second.Continuation);
        Assert.Null(third.Continuation);
        var values = first.Items
            .Concat(second.Items)
            .Concat(third.Items)
            .Select(sample => sample.ActivityPercent)
            .ToArray();
        Assert.Equal([10d, 11d, 12d, 13d, 14d], values);
        Assert.Equal(5, values.Distinct().Count());
    }

    [Fact]
    public async Task RangeQueryUsesInclusiveStartAndExclusiveEnd()
    {
        await using var database = await RepositoryTemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var (sessionId, target, start) =
            await CreateSessionAndDeviceAsync(database.Store, lease);
        var repository = new MonitorSampleRepository(database.Store, lease);
        await repository.WriteBatchAsync(
            [
                Sample(sessionId, target, start, 1),
                Sample(sessionId, target, start.AddMilliseconds(1), 2),
                Sample(sessionId, target, start.AddMilliseconds(2), 3)
            ]);

        var readOnlyRepository = new MonitorSampleRepository(database.Store);
        var actual = await readOnlyRepository.ReadRangeAsync(
            sessionId,
            start,
            start.AddMilliseconds(2));

        Assert.Equal([1d, 2d], actual.Select(sample => sample.ActivityPercent));
    }

    [Fact]
    public async Task FailedBatchRollsBackEverySampleAtTheCommitBoundary()
    {
        await using var database = await RepositoryTemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var (sessionId, target, start) =
            await CreateSessionAndDeviceAsync(database.Store, lease);
        var repository = new MonitorSampleRepository(database.Store, lease);
        var orphan = Sample(SessionId.New(), target, start.AddMilliseconds(1), 2);

        await Assert.ThrowsAsync<SqliteException>(
            () => repository.WriteBatchAsync(
                [
                    Sample(sessionId, target, start, 1),
                    orphan
                ]));

        var actual = await new MonitorSampleRepository(database.Store).ReadRangeAsync(
            sessionId,
            start,
            start.AddSeconds(1));
        Assert.Empty(actual);
    }

    [Fact]
    public async Task CompleteAndFlushPersistsTailSmallerThanMaximumBatch()
    {
        await using var database = await RepositoryTemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var (sessionId, target, start) =
            await CreateSessionAndDeviceAsync(database.Store, lease);

        await using (var writer = new MonitorSampleBatchWriter(
                         database.Store,
                         lease,
                         capacity: 16,
                         maximumBatchSize: 10,
                         maximumBatchDelay: TimeSpan.FromMinutes(1)))
        {
            await writer.EnqueueAsync(Sample(sessionId, target, start, 42));
            await writer.CompleteAndFlushAsync();
        }

        var actual = await new MonitorSampleRepository(database.Store).ReadRangeAsync(
            sessionId,
            start,
            start.AddSeconds(1));
        Assert.Equal(42, Assert.Single(actual).ActivityPercent);
    }

    [Fact]
    public async Task LockedDatabaseCreatesObservableBoundedBackpressureAndRecoversAfterRelease()
    {
        await using var database = await RepositoryTemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var (sessionId, target, start) =
            await CreateSessionAndDeviceAsync(database.Store, lease);
        await using var blocker = await database.Store.OpenConnectionAsync();
        await using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }

        await using var writer = new MonitorSampleBatchWriter(
            database.Store,
            lease,
            capacity: 4,
            maximumBatchSize: 1,
            maximumBatchDelay: TimeSpan.Zero);
        var accepted = 0;
        try
        {
            for (var index = 0; index < 10_000; index++)
            {
                if (writer.TryEnqueue(
                        Sample(sessionId, target, start.AddMilliseconds(index), index)))
                {
                    accepted++;
                }
            }

            Assert.InRange(accepted, 1, 9_999);
            Assert.Equal(10_000 - accepted, writer.RejectedSamples);
        }
        finally
        {
            await using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }

        await writer.CompleteAndFlushAsync();

        var actual = await new MonitorSampleRepository(database.Store).ReadRangeAsync(
            sessionId,
            start,
            start.AddSeconds(11),
            maximumResults: 10_000);
        Assert.Equal(accepted, actual.Count);
    }

    [Fact]
    public async Task MillionSampleDatasetUsesBoundedKeysetPages()
    {
        const int sampleCount = 1_000_000;
        const int pageSize = 257;
        await using var database = await RepositoryTemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var (sessionId, target, start) =
            await CreateSessionAndDeviceAsync(database.Store, lease);
        var identitySample = Sample(sessionId, target, start, 0);
        var deviceId = MonitorSampleBatchWriter.PersistedDeviceId(identitySample);

        await using (var connection = await database.Store.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE sequence(value) AS (
                    VALUES(0)
                    UNION ALL
                    SELECT value + 1
                    FROM sequence
                    WHERE value < $last
                )
                INSERT INTO monitor_samples(
                    session_id, device_id, timestamp_utc_ms, activity_pct,
                    read_bytes_per_sec, write_bytes_per_sec, queue_length)
                SELECT
                    $session, $device, $start + value, value % 101,
                    value * 2, value * 3, value % 17
                FROM sequence;
                """;
            command.Parameters.AddWithValue("$last", sampleCount - 1);
            command.Parameters.AddWithValue("$session", sessionId.Value.ToString("N"));
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds());
            Assert.Equal(sampleCount, await command.ExecuteNonQueryAsync());
        }

        var repository = new MonitorSampleRepository(database.Store);
        var first = await repository.ReadPageAsync(
            sessionId,
            start,
            start.AddMilliseconds(sampleCount + 1L),
            pageSize);
        Assert.Equal(pageSize, first.Items.Count);
        Assert.NotNull(first.Continuation);
        Assert.Equal(start, first.Items[0].SampledAtUtc);
        Assert.Equal(start.AddMilliseconds(pageSize - 1L), first.Items[^1].SampledAtUtc);

        var second = await repository.ReadPageAsync(
            sessionId,
            start,
            start.AddMilliseconds(sampleCount + 1L),
            pageSize,
            first.Continuation);
        Assert.Equal(pageSize, second.Items.Count);
        Assert.Equal(start.AddMilliseconds(pageSize), second.Items[0].SampledAtUtc);

        var tail = await repository.ReadPageAsync(
            sessionId,
            start,
            start.AddMilliseconds(sampleCount + 1L),
            pageSize,
            new MonitorSampleCursor(
                start.AddMilliseconds(sampleCount - pageSize - 1L)
                    .ToUnixTimeMilliseconds(),
                long.MaxValue));
        Assert.Equal(pageSize, tail.Items.Count);
        Assert.Equal(
            start.AddMilliseconds(sampleCount - pageSize),
            tail.Items[0].SampledAtUtc);
        Assert.Equal(
            start.AddMilliseconds(sampleCount - 1L),
            tail.Items[^1].SampledAtUtc);
        Assert.Null(tail.Continuation);
    }

    [Fact]
    public async Task CrashRecoveryMarksOpenSessionInterruptedAndRecordsEvidenceGap()
    {
        await using var database = await RepositoryTemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var sessions = new MonitorSessionRepository(database.Store, lease);
        var runningId = SessionId.New();
        var stoppedId = SessionId.New();
        var started = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        await sessions.CreateAsync(
            new PersistedMonitorSession(
                runningId,
                started,
                null,
                "Stopwatch+UTC",
                MonitoringSessionState.Running,
                DroppedSamples: 2));
        await sessions.CreateAsync(
            new PersistedMonitorSession(
                stoppedId,
                started,
                started.AddSeconds(1),
                "Stopwatch+UTC",
                MonitoringSessionState.Stopped,
                DroppedSamples: 0));
        var recoveredAt = started.AddMinutes(1);

        var recovered = await sessions.RecoverInterruptedSessionsAsync(
            recoveredAt,
            minimumUnflushedSamples: 1);

        Assert.Equal(1, recovered);
        var interrupted = await sessions.GetAsync(runningId);
        Assert.NotNull(interrupted);
        Assert.Equal(MonitoringSessionState.Interrupted, interrupted.State);
        Assert.Equal(recoveredAt, interrupted.EndedAtUtc);
        Assert.Equal(3, interrupted.DroppedSamples);
        var stopped = await sessions.GetAsync(stoppedId);
        Assert.NotNull(stopped);
        Assert.Equal(MonitoringSessionState.Stopped, stopped.State);
        Assert.Equal(0, stopped.DroppedSamples);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitorPersistencePreservesEveryMetricAndCsvColumn(bool useBatchWriter)
    {
        await using var database = await RepositoryTemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var (sessionId, target, start) =
            await CreateSessionAndDeviceAsync(database.Store, lease);
        var sample = new MonitorSample(
            sessionId,
            target,
            start,
            [
                // Input order deliberately differs from the database and CSV order.
                new(MonitorMetricKind.VirtualDiskPendingDeletionBytes, 14014),
                new(MonitorMetricKind.CpuPercent, 8.75),
                new(MonitorMetricKind.WriteBytesPerSecond, 3003),
                new(MonitorMetricKind.VirtualDiskStaleBytes, 11011),
                new(MonitorMetricKind.ActiveTimePercent, 1.25),
                new(MonitorMetricKind.VirtualDiskNeedRegenerationBytes, 12012),
                new(MonitorMetricKind.ReadOperationsPerSecond, 44),
                new(MonitorMetricKind.AverageQueueLength, 6.5),
                new(MonitorMetricKind.VirtualDiskActiveBytes, 9009),
                new(MonitorMetricKind.ReadBytesPerSecond, 2002),
                new(MonitorMetricKind.VirtualDiskRegeneratingBytes, 13013),
                new(MonitorMetricKind.AverageLatencyMilliseconds, 7.125),
                new(MonitorMetricKind.WriteOperationsPerSecond, 55),
                new(MonitorMetricKind.VirtualDiskMissingBytes, 10010)
            ]);
        Assert.Equal(
            Enum.GetValues<MonitorMetricKind>(),
            sample.Values.Select(value => value.Kind).OrderBy(kind => kind));
        await WriteMonitorSamplesAsync(database.Store, lease, [sample], useBatchWriter);

        var persisted = Assert.Single(await new MonitorSampleRepository(database.Store)
            .ReadRangeAsync(sessionId, start, start.AddMilliseconds(1)));
        Assert.True(persisted.RowId > 0);
        Assert.Equal(
            new PersistedMonitorSample(
                persisted.RowId, sessionId, MonitorSampleBatchWriter.PersistedDeviceId(sample), start,
                ActivityPercent: 1.25,
                ReadBytesPerSecond: 2002,
                WriteBytesPerSecond: 3003,
                ReadOperationsPerSecond: 44,
                WriteOperationsPerSecond: 55,
                QueueLength: 6.5,
                AverageLatencyMilliseconds: 7.125,
                CpuPercent: 8.75,
                VirtualDiskActiveBytes: 9009,
                VirtualDiskMissingBytes: 10010,
                VirtualDiskStaleBytes: 11011,
                VirtualDiskNeedRegenerationBytes: 12012,
                VirtualDiskRegeneratingBytes: 13013,
                VirtualDiskPendingDeletionBytes: 14014),
            persisted);

        var destination = Path.Combine(database.Directory, "monitor.csv");
        var exported = await new MonitorCsvExporter(database.Store).ExportAsync(
            sessionId, destination, overwrite: false);
        var lines = await File.ReadAllLinesAsync(destination);
        Assert.Equal(1, exported.RowCount);
        Assert.Equal(2, lines.Length);
        Assert.Equal(MonitorCsvHeader, lines[0]);
        Assert.Equal(
            $"{start:O},Disk,1.25,2002,3003,44,55,6.5,7.125,8.75,9009,10010,11011,12012,13013,14014",
            lines[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitorPersistenceDistinguishesEveryMissingMetricFromTrueZero(bool useBatchWriter)
    {
        await using var database = await RepositoryTemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var (sessionId, target, start) =
            await CreateSessionAndDeviceAsync(database.Store, lease);
        var zero = new MonitorSample(sessionId, target, start,
            Enum.GetValues<MonitorMetricKind>()
                .Select(kind => new MonitorMetricValue(kind, 0)).ToArray());
        var missing = new MonitorSample(sessionId, target, start.AddMilliseconds(1), []);
        var partial = new MonitorSample(sessionId, target, start.AddMilliseconds(2),
            [new(MonitorMetricKind.ReadOperationsPerSecond, 0)]);
        // Reusing a prepared batch command must clear the preceding sample's values.
        await WriteMonitorSamplesAsync(database.Store, lease, [zero, missing, partial], useBatchWriter);

        var persisted = await new MonitorSampleRepository(database.Store)
            .ReadRangeAsync(sessionId, start, start.AddMilliseconds(3));
        Assert.Equal(3, persisted.Count);
        Assert.Equal(start, persisted[0].SampledAtUtc);
        Assert.Equal(start.AddMilliseconds(1), persisted[1].SampledAtUtc);
        Assert.Equal(start.AddMilliseconds(2), persisted[2].SampledAtUtc);
        AssertEveryPersistedMetric(persisted[0], 0);
        AssertEveryPersistedMetric(persisted[1], null);
        Assert.Equal(0, persisted[2].ReadOperationsPerSecond);
        AssertEveryPersistedMetric(persisted[2] with { ReadOperationsPerSecond = null }, null);

        var destination = Path.Combine(database.Directory, "monitor.csv");
        var exported = await new MonitorCsvExporter(database.Store).ExportAsync(
            sessionId, destination, overwrite: false);
        var lines = await File.ReadAllLinesAsync(destination);
        Assert.Equal(3, exported.RowCount);
        Assert.Equal(4, lines.Length);
        Assert.Equal(MonitorCsvHeader, lines[0]);
        Assert.Equal($"{start:O},Disk,0,0,0,0,0,0,0,0,0,0,0,0,0,0", lines[1]);
        var missingColumns = lines[2].Split(',');
        Assert.Equal(16, missingColumns.Length);
        Assert.Equal($"{missing.SampledAtUtc:O}", missingColumns[0]);
        Assert.Equal("Disk", missingColumns[1]);
        Assert.All(missingColumns.Skip(2), value => Assert.Equal(string.Empty, value));
        var partialColumns = lines[3].Split(',');
        Assert.Equal(16, partialColumns.Length);
        Assert.Equal($"{partial.SampledAtUtc:O}", partialColumns[0]);
        Assert.Equal("Disk", partialColumns[1]);
        Assert.Equal("0", partialColumns[5]);
        Assert.All(partialColumns.Skip(2).Where((_, index) => index != 3),
            value => Assert.Equal(string.Empty, value));
    }

    private const string MonitorCsvHeader =
        "TimestampUtc,Device,ActivityPercent,ReadBytesPerSecond," +
        "WriteBytesPerSecond,ReadOperationsPerSecond,WriteOperationsPerSecond," +
        "QueueLength,AverageLatencyMilliseconds,CpuPercent,VirtualDiskActiveBytes," +
        "VirtualDiskMissingBytes,VirtualDiskStaleBytes,VirtualDiskNeedRegenerationBytes," +
        "VirtualDiskRegeneratingBytes,VirtualDiskPendingDeletionBytes";

    private static void AssertEveryPersistedMetric(PersistedMonitorSample sample, double? expected)
    {
        Assert.Equal(expected, sample.ActivityPercent);
        Assert.Equal(expected, sample.ReadBytesPerSecond);
        Assert.Equal(expected, sample.WriteBytesPerSecond);
        Assert.Equal(expected, sample.ReadOperationsPerSecond);
        Assert.Equal(expected, sample.WriteOperationsPerSecond);
        Assert.Equal(expected, sample.QueueLength);
        Assert.Equal(expected, sample.AverageLatencyMilliseconds);
        Assert.Equal(expected, sample.CpuPercent);
        Assert.Equal(expected, sample.VirtualDiskActiveBytes);
        Assert.Equal(expected, sample.VirtualDiskMissingBytes);
        Assert.Equal(expected, sample.VirtualDiskStaleBytes);
        Assert.Equal(expected, sample.VirtualDiskNeedRegenerationBytes);
        Assert.Equal(expected, sample.VirtualDiskRegeneratingBytes);
        Assert.Equal(expected, sample.VirtualDiskPendingDeletionBytes);
    }

    private static async Task WriteMonitorSamplesAsync(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease lease,
        IReadOnlyList<MonitorSample> samples,
        bool useBatchWriter)
    {
        if (!useBatchWriter)
        {
            await new MonitorSampleRepository(store, lease).WriteBatchAsync(samples);
            return;
        }

        await using var writer = new MonitorSampleBatchWriter(
            store, lease, capacity: 16, maximumBatchSize: 10,
            maximumBatchDelay: TimeSpan.FromMinutes(1));
        foreach (var sample in samples)
        {
            await writer.EnqueueAsync(sample);
        }
        await writer.CompleteAndFlushAsync();
        Assert.Equal(0, writer.RejectedSamples);
    }

    private static async Task<(SessionId SessionId, StorageObjectId Target, DateTimeOffset Start)>
        CreateSessionAndDeviceAsync(
            WinPoolSqliteStore store,
            AgentWriteOwnerLease lease)
    {
        var sessionId = SessionId.New();
        var target = new StorageObjectId(
            SystemId.New(),
            StorageObjectKind.PhysicalDisk,
            $"provider-{Guid.NewGuid():N}");
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        await new MonitorSessionRepository(store, lease).CreateAsync(
            new PersistedMonitorSession(
                sessionId,
                start,
                null,
                "Stopwatch+UTC",
                MonitoringSessionState.Running,
                DroppedSamples: 0));
        var sample = Sample(sessionId, target, start, 0);
        await new MonitorDeviceRepository(store, lease).UpsertAsync(
            new PersistedMonitorDevice(
                sessionId,
                MonitorSampleBatchWriter.PersistedDeviceId(sample),
                "Disk",
                SourceKind: 0));
        return (sessionId, target, start);
    }

    private static MonitorSample Sample(
        SessionId sessionId,
        StorageObjectId target,
        DateTimeOffset timestamp,
        double activity) =>
        new(
            sessionId,
            target,
            timestamp,
            [
                new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, activity),
                new MonitorMetricValue(MonitorMetricKind.ReadBytesPerSecond, activity * 100),
                new MonitorMetricValue(MonitorMetricKind.WriteBytesPerSecond, activity * 200),
                new MonitorMetricValue(MonitorMetricKind.AverageQueueLength, activity / 10)
            ]);

    private sealed class RepositoryTemporaryDatabase : IAsyncDisposable
    {
        private RepositoryTemporaryDatabase(string directory, WinPoolSqliteStore store)
        {
            Directory = directory;
            Store = store;
        }

        public string Directory { get; }

        public WinPoolSqliteStore Store { get; }

        public static async Task<RepositoryTemporaryDatabase> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "WinPool.Persistence.Repository.Tests",
                Guid.NewGuid().ToString("N"));
            var store = new WinPoolSqliteStore(Path.Combine(directory, "winpool.db"));
            await store.InitializeAsync();
            return new RepositoryTemporaryDatabase(directory, store);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
