using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;
using WinPool.Monitoring;

namespace WinPool.Persistence.Tests;

/// <summary>
/// Opt-in monitoring archive measurements. They are intentionally skipped in
/// ordinary unit runs: every database is schema-valid but isolated below the
/// system temporary directory. The direct-archive baseline and the production
/// threshold/rotation path remain separate pieces of evidence.
/// </summary>
public sealed class MonitoringArchiveMeasurementTests
{
    private const long OneGiB = 1_073_741_824;
    private const long InputHeadroomBytes = 32L * 1024 * 1024;
    private const long RepresentativeComparisonBytes = 96L * 1024 * 1024;
    private const string ProductionCompressionArguments =
        "-mx=9,-m0=lzma2,-md=64m,-ms=on,-mmt=2";
    private const string HigherResourceComparisonArguments =
        "-mx=9,-m0=lzma2,-md=128m,-ms=on,-mmt=4";

    [EnvironmentEnabledFact("WINPOOL_RUN_1GIB_ARCHIVE_MEASUREMENT")]
    [Trait("Category", "ManualPerformance")]
    public async Task OneGiBSyntheticMonitoringDatabaseUsesProductionArchiveParameters()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WinPool 1GiB archive {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var activePath = Path.Combine(root, "monitoring.db");
            var store = new MonitoringSqliteStore(activePath);
            await store.InitializeAsync();
            var session = await CreateSyntheticSessionAsync(store);
            var samples = await AppendSyntheticSamplesUntilThresholdAsync(
                store,
                activePath,
                session,
                OneGiB + InputHeadroomBytes);
            await store.CheckpointAndCloseAsync();

            var rawBytes = new FileInfo(activePath).Length;
            Assert.True(
                rawBytes >= OneGiB,
                $"Checkpointed measurement input was {rawBytes:N0} bytes, below the 1 GiB production threshold.");

            var sealedRoot = Path.Combine(
                root,
                MonitoringArchiveCoordinator.ArchiveDirectoryName,
                MonitoringArchiveCoordinator.SealedDirectoryName);
            var packageRoot = Path.Combine(
                root,
                MonitoringArchiveCoordinator.ArchiveDirectoryName,
                MonitoringArchiveCoordinator.PackageDirectoryName);
            Directory.CreateDirectory(sealedRoot);
            Directory.CreateDirectory(packageRoot);
            var rawPath = Path.Combine(sealedRoot, "monitoring-1gib-measurement.db");
            File.Move(activePath, rawPath);
            var manifestPath = rawPath + ".manifest.json";
            await File.WriteAllTextAsync(
                manifestPath,
                "{\"measurement\":\"1GiB synthetic monitoring database\",\"schema\":1}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var temporaryPackage = Path.Combine(packageRoot, "monitoring-1gib-measurement.tmp.7z");
            var package = Path.Combine(packageRoot, "monitoring-1gib-measurement.7z");
            var executable = BundledSevenZipPath();
            var stopwatch = Stopwatch.StartNew();
            var compression = await new ControlledProcessRunner().RunAsync(
                new ControlledProcessInvocation(
                    executable,
                    [
                        "a",
                        "-t7z",
                        "-mx=9",
                        "-m0=lzma2",
                        "-md=64m",
                        "-ms=on",
                        "-mmt=2",
                        "-y",
                        "-bso0",
                        "-bsp0",
                        temporaryPackage,
                        Path.GetFileName(rawPath),
                        Path.GetFileName(manifestPath)
                    ],
                    sealedRoot),
                CancellationToken.None);
            stopwatch.Stop();
            Assert.Equal(0, compression.ExitCode);
            Assert.True(File.Exists(rawPath), "The source must exist before archive verification succeeds.");

            var sevenZip = new SevenZipArchiveAdapter();
            await sevenZip.TestArchiveAsync(executable, temporaryPackage, CancellationToken.None);
            var sourceHash = await HashFileAsync(rawPath);
            var extractedHash = await sevenZip.HashExtractedEntryAsync(
                executable,
                temporaryPackage,
                Path.GetFileName(rawPath),
                CancellationToken.None);
            Assert.Equal(sourceHash, extractedHash);
            File.Move(temporaryPackage, package);

            var packageBytes = new FileInfo(package).Length;
            var evidence = new MeasurementEvidence(
                rawBytes,
                packageBytes,
                samples,
                stopwatch.ElapsedMilliseconds,
                compression.PeakWorkingSetBytes,
                compression.ProcessorTimeMilliseconds,
                "-mx=9,-m0=lzma2,-md=64m,-ms=on,-mmt=2");
            var summary =
                "WINPOOL_1GIB_ARCHIVE_MEASUREMENT "
                + $"input_bytes={evidence.InputBytes}; archive_bytes={evidence.ArchiveBytes}; samples={evidence.Samples}; "
                + $"elapsed_ms={evidence.ElapsedMilliseconds}; "
                + $"7za_peak_working_set_bytes={evidence.SevenZipPeakWorkingSetBytes}; "
                + $"7za_processor_ms={evidence.SevenZipProcessorMilliseconds}; "
                + $"arguments={evidence.Arguments}";
            Console.WriteLine(summary);
            var evidencePath = Environment.GetEnvironmentVariable(
                "WINPOOL_1GIB_ARCHIVE_EVIDENCE_PATH");
            if (!string.IsNullOrWhiteSpace(evidencePath))
            {
                var fullEvidencePath = Path.GetFullPath(evidencePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullEvidencePath)
                    ?? throw new IOException("The measurement evidence path has no directory."));
                await File.WriteAllTextAsync(
                    fullEvidencePath,
                    JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [EnvironmentEnabledFact("WINPOOL_RUN_1GIB_ARCHIVE_MEASUREMENT")]
    [Trait("Category", "ManualPerformance")]
    public async Task OneGiBSyntheticMonitoringDatabaseTriggersProductionRotationAndFullArchiveLifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WinPool 1GiB production rotation {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var executable = BundledSevenZipPath();

            // Direct inserts only prepare an isolated, schema-valid input.
            // The threshold observation, fixed-name replacement, archive
            // registration, 7z lifecycle, verification, and source release
            // below all go through the production factory/coordinator path.
            var activePath = Path.Combine(
                root,
                RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName);
            var populatedStore = new MonitoringSqliteStore(activePath);
            await populatedStore.InitializeAsync();
            var prepopulatedSessionId = await CreateSyntheticSessionAsync(populatedStore);
            var prepopulatedSamples = await AppendSyntheticSamplesUntilThresholdAsync(
                populatedStore,
                activePath,
                prepopulatedSessionId,
                OneGiB + InputHeadroomBytes);
            await populatedStore.CheckpointAndCloseAsync();

            var preRotationFootprintBytes = DatabaseFootprintBytes(activePath);
            Assert.True(
                preRotationFootprintBytes >= OneGiB,
                $"Checkpointed production-rotation input was {preRotationFootprintBytes:N0} bytes, below the 1 GiB threshold.");

            var lifecycleRunner = new MeasuringProcessRunner();
            var archive = new MonitoringArchiveCoordinator(
                root,
                root,
                () => executable,
                new SevenZipArchiveAdapter(lifecycleRunner));
            MonitoringArchiveRecord releasedRecord;
            long archivedLiveBoundarySamples;
            long activeLiveBoundarySamples;
            long lifecycleElapsedMilliseconds;
            string newActiveMarkerStage;
            IReadOnlyList<MeasurementSampleIdentity> archivedLiveMarker;
            IReadOnlyList<MeasurementSampleIdentity> activeLiveMarker;

            await using (var factory = new RotatingMonitorSessionPersistenceFactory(
                             root,
                             "1gib-production-rotation-agent",
                             archive))
            {
                await factory.InitializeAsync();
                var liveSession = CreateMeasurementMonitoringSession();
                var firstLiveSample = CreateMeasurementSample(liveSession, 1);
                var postRotationSample = CreateMeasurementSample(liveSession, 2);

                await using (var persistence = factory.Create(liveSession.SessionId))
                {
                    var lifecycle = Stopwatch.StartNew();
                    await persistence.StartAsync(liveSession, CancellationToken.None);
                    // This sample crosses the first production threshold with
                    // the populated active database and must be sealed with it.
                    Assert.True(persistence.TryWrite(firstLiveSample));
                    await persistence.FlushAsync(CancellationToken.None);

                    // Sealed/Compressing means the fixed active path has
                    // already been replaced. Persist this distinct marker
                    // while the old database is still being archived, rather
                    // than merely proving that it can be written after the
                    // archive worker has finished.
                    await WaitUntilAsync(
                        () => archive.Snapshot().Any(record =>
                            record.Stage is MonitoringArchiveStage.Sealed
                                or MonitoringArchiveStage.Compressing),
                        TimeSpan.FromMinutes(2));
                    newActiveMarkerStage = Assert.Single(archive.Snapshot()).Stage.ToString();
                    Assert.True(
                        newActiveMarkerStage is nameof(MonitoringArchiveStage.Sealed)
                            or nameof(MonitoringArchiveStage.Compressing));
                    Assert.True(persistence.TryWrite(postRotationSample));
                    await persistence.FlushAsync(CancellationToken.None);
                    activeLiveMarker = await ReadMeasurementSampleIdentitiesAsync(
                        activePath,
                        liveSession.SessionId);
                    Assert.Equal([ToMeasurementSampleIdentity(postRotationSample)], activeLiveMarker);
                    activeLiveBoundarySamples = activeLiveMarker.Count;

                    await WaitUntilAsync(
                        () => archive.Snapshot().Any(record =>
                            record.Stage == MonitoringArchiveStage.SourceReleased),
                        TimeSpan.FromMinutes(20));
                    await archive.WaitForIdleAsync(TimeSpan.FromMinutes(2));
                    lifecycle.Stop();

                    releasedRecord = Assert.Single(
                        archive.Snapshot(),
                        record => record.Stage == MonitoringArchiveStage.SourceReleased);
                    Assert.True(File.Exists(releasedRecord.ArchivePath));
                    Assert.False(File.Exists(releasedRecord.RawDatabasePath));
                    Assert.True(releasedRecord.DatabaseBytes >= OneGiB);
                    Assert.Equal(prepopulatedSamples + 1, releasedRecord.SampleCount);
                    Assert.Contains(prepopulatedSessionId.Value.ToString("N"), releasedRecord.SessionIds);
                    Assert.Contains(liveSession.SessionId.Value.ToString("N"), releasedRecord.SessionIds);

                    var extractionDirectory = Path.Combine(root, "production-boundary-verification");
                    Directory.CreateDirectory(extractionDirectory);
                    var extraction = await new ControlledProcessRunner().RunAsync(
                        new ControlledProcessInvocation(
                            executable,
                            [
                                "x",
                                "-y",
                                "-bso0",
                                "-bsp0",
                                $"-o{extractionDirectory}",
                                releasedRecord.ArchivePath
                            ]),
                        CancellationToken.None);
                    Assert.Equal(0, extraction.ExitCode);
                    archivedLiveMarker = await ReadMeasurementSampleIdentitiesAsync(
                        Path.Combine(extractionDirectory, Path.GetFileName(releasedRecord.RawDatabasePath)),
                        liveSession.SessionId);
                    Assert.Equal([ToMeasurementSampleIdentity(firstLiveSample)], archivedLiveMarker);
                    archivedLiveBoundarySamples = archivedLiveMarker.Count;

                    await persistence.CompleteAsync(
                        MonitoringSessionState.Stopped,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None);
                    lifecycleElapsedMilliseconds = lifecycle.ElapsedMilliseconds;
                }
            }

            var lifecycleProcesses = lifecycleRunner.Snapshot();
            Assert.Equal(
                ["create", "test", "extract", "extract", "test", "extract", "extract"],
                lifecycleProcesses.Select(process => process.Operation));
            Assert.All(lifecycleProcesses, process => Assert.Equal(0, process.ExitCode));
            var peakWorkingSetBytes = lifecycleProcesses.Max(process => process.PeakWorkingSetBytes);
            var processorTimeMilliseconds = lifecycleProcesses.Sum(process => process.ProcessorTimeMilliseconds);
            Assert.True(peakWorkingSetBytes > 0, "The production 7z lifecycle did not report a peak working set.");

            var evidence = new ProductionRotationMeasurementEvidence(
                ProductionThresholdBytes: OneGiB,
                PreRotationFootprintBytes: preRotationFootprintBytes,
                ReleasedDatabaseBytes: releasedRecord.DatabaseBytes,
                ArchiveBytes: new FileInfo(releasedRecord.ArchivePath).Length,
                PrepopulatedSamples: prepopulatedSamples,
                ArchivedSamples: releasedRecord.SampleCount,
                ArchivedLiveBoundarySamples: archivedLiveBoundarySamples,
                ActiveLiveBoundarySamples: activeLiveBoundarySamples,
                NewActiveMarkerStage: newActiveMarkerStage,
                ArchivedLiveMarker: archivedLiveMarker,
                ActiveLiveMarker: activeLiveMarker,
                RotationAndArchiveElapsedMilliseconds: lifecycleElapsedMilliseconds,
                SevenZipPeakWorkingSetBytes: peakWorkingSetBytes,
                SevenZipProcessorTimeMilliseconds: processorTimeMilliseconds,
                SevenZipProcesses: lifecycleProcesses,
                ProductionArguments: ProductionCompressionArguments);
            Console.WriteLine(
                "WINPOOL_1GIB_PRODUCTION_ROTATION "
                + $"threshold_bytes={evidence.ProductionThresholdBytes}; pre_rotation_bytes={evidence.PreRotationFootprintBytes}; "
                + $"released_db_bytes={evidence.ReleasedDatabaseBytes}; archive_bytes={evidence.ArchiveBytes}; "
                + $"samples={evidence.ArchivedSamples}; lifecycle_elapsed_ms={evidence.RotationAndArchiveElapsedMilliseconds}; "
                + $"7za_peak_working_set_bytes={evidence.SevenZipPeakWorkingSetBytes}; "
                + $"7za_processor_ms={evidence.SevenZipProcessorTimeMilliseconds}; "
                + $"processes={evidence.SevenZipProcesses.Count}; arguments={evidence.ProductionArguments}");
            await WriteEvidenceAsync(
                "WINPOOL_1GIB_ROTATION_EVIDENCE_PATH",
                evidence);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [EnvironmentEnabledFact("WINPOOL_RUN_7Z_PARAMETER_COMPARISON")]
    [Trait("Category", "ManualPerformance")]
    public async Task RepresentativeSyntheticMonitoringDatabaseComparesFixedCompressionParameters()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WinPool 7z parameter comparison {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var comparison = await MeasureRepresentativeParametersAsync(root, BundledSevenZipPath());
            Console.WriteLine(
                "WINPOOL_7Z_PARAMETER_COMPARISON "
                + $"input_bytes={comparison.InputBytes}; samples={comparison.Samples}; "
                + $"production_archive_bytes={comparison.Production.ArchiveBytes}; "
                + $"production_elapsed_ms={comparison.Production.CreateElapsedMilliseconds}; "
                + $"production_peak_working_set_bytes={comparison.Production.PeakWorkingSetBytes}; "
                + $"production_processor_ms={comparison.Production.ProcessorTimeMilliseconds}; "
                + $"comparison_archive_bytes={comparison.HigherResourceCandidate.ArchiveBytes}; "
                + $"comparison_elapsed_ms={comparison.HigherResourceCandidate.CreateElapsedMilliseconds}; "
                + $"comparison_peak_working_set_bytes={comparison.HigherResourceCandidate.PeakWorkingSetBytes}; "
                + $"comparison_processor_ms={comparison.HigherResourceCandidate.ProcessorTimeMilliseconds}; "
                + $"production_arguments={comparison.Production.Arguments}; "
                + $"comparison_arguments={comparison.HigherResourceCandidate.Arguments}");
            await WriteEvidenceAsync(
                "WINPOOL_7Z_PARAMETER_COMPARISON_EVIDENCE_PATH",
                comparison);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<ParameterComparisonEvidence> MeasureRepresentativeParametersAsync(
        string root,
        string executable)
    {
        var comparisonRoot = Path.Combine(root, "parameter-comparison");
        Directory.CreateDirectory(comparisonRoot);
        var databasePath = Path.Combine(comparisonRoot, "monitoring.db");
        var store = new MonitoringSqliteStore(databasePath);
        await store.InitializeAsync();
        var sessionId = await CreateSyntheticSessionAsync(store);
        var sampleCount = await AppendSyntheticSamplesUntilThresholdAsync(
            store,
            databasePath,
            sessionId,
            RepresentativeComparisonBytes + InputHeadroomBytes);
        await store.CheckpointAndCloseAsync();

        var inputBytes = DatabaseFootprintBytes(databasePath);
        Assert.True(inputBytes >= RepresentativeComparisonBytes);
        var production = await MeasureCompressionCandidateAsync(
            executable,
            comparisonRoot,
            databasePath,
            Path.Combine(comparisonRoot, "production-64m-two-workers.7z"),
            ProductionCompressionArguments);
        var higherResource = await MeasureCompressionCandidateAsync(
            executable,
            comparisonRoot,
            databasePath,
            Path.Combine(comparisonRoot, "comparison-128m-four-workers.7z"),
            HigherResourceComparisonArguments);

        Assert.True(production.ArchiveBytes > 0);
        Assert.True(higherResource.ArchiveBytes > 0);
        Assert.True(production.PeakWorkingSetBytes <= higherResource.PeakWorkingSetBytes,
            $"The fixed 64 MiB/two-worker configuration used {production.PeakWorkingSetBytes:N0} bytes, "
            + $"which exceeded the 128 MiB/four-worker comparison at {higherResource.PeakWorkingSetBytes:N0} bytes.");

        return new ParameterComparisonEvidence(
            inputBytes,
            sampleCount,
            production,
            higherResource,
            "The 64 MiB/two-worker configuration is retained when it keeps peak memory at or below the larger-dictionary/four-worker candidate; archive size and elapsed/CPU values are retained beside that resource bound.");
    }

    private static async Task<CompressionCandidateEvidence> MeasureCompressionCandidateAsync(
        string executable,
        string workingDirectory,
        string databasePath,
        string archivePath,
        string compressionArguments)
    {
        var runner = new MeasuringProcessRunner();
        var arguments = new List<string>
        {
            "a",
            "-t7z"
        };
        arguments.AddRange(compressionArguments.Split(',', StringSplitOptions.RemoveEmptyEntries));
        arguments.AddRange(
        [
            "-y",
            "-bso0",
            "-bsp0",
            archivePath,
            Path.GetFileName(databasePath)
        ]);

        var stopwatch = Stopwatch.StartNew();
        var created = await runner.RunAsync(
            new ControlledProcessInvocation(executable, arguments, workingDirectory),
            CancellationToken.None);
        stopwatch.Stop();
        Assert.Equal(0, created.ExitCode);
        Assert.True(File.Exists(archivePath));

        var tested = await runner.RunAsync(
            new ControlledProcessInvocation(
                executable,
                ["t", "-bso0", "-bsp0", archivePath]),
            CancellationToken.None);
        Assert.Equal(0, tested.ExitCode);
        var measuredCreate = Assert.Single(
            runner.Snapshot(),
            process => process.Operation == "create");
        return new CompressionCandidateEvidence(
            compressionArguments,
            new FileInfo(archivePath).Length,
            Math.Round((double)new FileInfo(archivePath).Length / new FileInfo(databasePath).Length, 8),
            stopwatch.ElapsedMilliseconds,
            measuredCreate.PeakWorkingSetBytes,
            measuredCreate.ProcessorTimeMilliseconds,
            runner.Snapshot().Count);
    }

    private static MonitoringSession CreateMeasurementMonitoringSession()
    {
        var systemId = SystemId.New();
        var sessionId = SessionId.New();
        var request = new MonitorRequest(
            sessionId,
            systemId,
            [new MonitorTarget(
                new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "measurement-live-disk"),
                "Measurement live disk")],
            [MonitorMetricKind.ActiveTimePercent, MonitorMetricKind.ReadBytesPerSecond],
            TimeSpan.FromSeconds(1),
            ContinueWhenUiCloses: true);
        return new MonitoringSession(
            sessionId,
            request,
            MonitoringSessionState.Running,
            DateTimeOffset.UtcNow,
            null);
    }

    private static MonitorSample CreateMeasurementSample(MonitoringSession session, int sequence) =>
        new(
            session.SessionId,
            session.Request.Targets[0].ObjectId,
            session.CreatedAtUtc.AddMilliseconds(sequence),
            [
                new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, sequence),
                new MonitorMetricValue(MonitorMetricKind.ReadBytesPerSecond, sequence * 4096d)
            ]);

    private static async Task<long> CountSamplesForSessionAsync(
        string databasePath,
        SessionId sessionId)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM monitor_samples WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.Value.ToString("N"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<MeasurementSampleIdentity>> ReadMeasurementSampleIdentitiesAsync(
        string databasePath,
        SessionId sessionId)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        var result = new List<MeasurementSampleIdentity>();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp_utc_ms, activity_pct, read_bytes_per_sec
            FROM monitor_samples
            WHERE session_id = $sessionId
            ORDER BY timestamp_utc_ms, rowid;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new MeasurementSampleIdentity(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2)));
        }

        return result;
    }

    private static MeasurementSampleIdentity ToMeasurementSampleIdentity(MonitorSample sample)
    {
        var activity = sample.Values.SingleOrDefault(value =>
            value.Kind == MonitorMetricKind.ActiveTimePercent);
        var read = sample.Values.SingleOrDefault(value =>
            value.Kind == MonitorMetricKind.ReadBytesPerSecond);
        return new MeasurementSampleIdentity(
            sample.SampledAtUtc.ToUnixTimeMilliseconds(),
            activity?.Value,
            read?.Value);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException("The production 1 GiB monitoring rotation did not finish in time.");
    }

    private static async Task WriteEvidenceAsync<T>(string environmentVariable, T evidence)
    {
        var evidencePath = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(evidencePath))
        {
            return;
        }

        var fullEvidencePath = Path.GetFullPath(evidencePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullEvidencePath)
            ?? throw new IOException("The measurement evidence path has no directory."));
        await File.WriteAllTextAsync(
            fullEvidencePath,
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static async Task<SessionId> CreateSyntheticSessionAsync(MonitoringSqliteStore store)
    {
        var sessionId = SessionId.New();
        var now = DateTimeOffset.UtcNow;
        await using var lease = AgentWriteOwnerLease.Acquire(store, "1gib-measurement-agent");
        var sessions = new MonitorSessionRepository(store, lease);
        await sessions.CreateAsync(new PersistedMonitorSession(
            sessionId,
            now,
            null,
            "Stopwatch+UTC",
            MonitoringSessionState.Running,
            0));
        var devices = new MonitorDeviceRepository(store, lease);
        for (var device = 0; device < 4; device++)
        {
            await devices.UpsertAsync(new PersistedMonitorDevice(
                sessionId,
                DeviceId(device),
                $"Synthetic monitoring disk {device}",
                (int)StorageObjectKind.PhysicalDisk));
        }

        return sessionId;
    }

    private static async Task<long> AppendSyntheticSamplesUntilThresholdAsync(
        MonitoringSqliteStore store,
        string databasePath,
        SessionId sessionId,
        long thresholdBytes)
    {
        const int batchSize = 10_000;
        var samples = 0L;
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var connection = await store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO monitor_samples(
                session_id, device_id, timestamp_utc_ms, activity_pct,
                read_bytes_per_sec, write_bytes_per_sec,
                read_operations_per_sec, write_operations_per_sec, queue_length,
                average_latency_ms, cpu_pct, virtual_disk_active_bytes,
                virtual_disk_missing_bytes, virtual_disk_stale_bytes,
                virtual_disk_need_regeneration_bytes, virtual_disk_regenerating_bytes,
                virtual_disk_pending_deletion_bytes)
            VALUES(
                $session, $device, $timestamp, $activity,
                $read, $write, $readOps, $writeOps, $queue,
                $latency, $cpu, $activeBytes, $missingBytes, $staleBytes,
                $needRegenerationBytes, $regeneratingBytes, $pendingDeletionBytes);
            """;
        var session = command.Parameters.Add("$session", SqliteType.Text);
        var device = command.Parameters.Add("$device", SqliteType.Text);
        var timestamp = command.Parameters.Add("$timestamp", SqliteType.Integer);
        var activity = command.Parameters.Add("$activity", SqliteType.Real);
        var read = command.Parameters.Add("$read", SqliteType.Real);
        var write = command.Parameters.Add("$write", SqliteType.Real);
        var readOps = command.Parameters.Add("$readOps", SqliteType.Real);
        var writeOps = command.Parameters.Add("$writeOps", SqliteType.Real);
        var queue = command.Parameters.Add("$queue", SqliteType.Real);
        var latency = command.Parameters.Add("$latency", SqliteType.Real);
        var cpu = command.Parameters.Add("$cpu", SqliteType.Real);
        var activeBytes = command.Parameters.Add("$activeBytes", SqliteType.Real);
        var missingBytes = command.Parameters.Add("$missingBytes", SqliteType.Real);
        var staleBytes = command.Parameters.Add("$staleBytes", SqliteType.Real);
        var needRegenerationBytes = command.Parameters.Add("$needRegenerationBytes", SqliteType.Real);
        var regeneratingBytes = command.Parameters.Add("$regeneratingBytes", SqliteType.Real);
        var pendingDeletionBytes = command.Parameters.Add("$pendingDeletionBytes", SqliteType.Real);
        command.Prepare();

        while (DatabaseFootprintBytes(databasePath) < thresholdBytes)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
            command.Transaction = transaction;
            for (var index = 0; index < batchSize; index++)
            {
                var value = checked(samples + index);
                session.Value = sessionId.Value.ToString("N");
                device.Value = DeviceId((int)(value % 4));
                timestamp.Value = checked(startedAt + value);
                activity.Value = (value * 17 % 10_000) / 100d;
                read.Value = value % 5 == 0 ? DBNull.Value : value * 4096d + 0.125d;
                write.Value = value % 7 == 0 ? 0d : value * 2048d + 0.875d;
                readOps.Value = value * 3d + 0.25d;
                writeOps.Value = value * 5d + 0.5d;
                queue.Value = value % 31 + 0.75d;
                latency.Value = value % 17 + 0.125d;
                cpu.Value = value % 101 + 0.5d;
                activeBytes.Value = value * 8192d + 0.625d;
                missingBytes.Value = value % 19 == 0 ? value + 0.75d : 0d;
                staleBytes.Value = value % 23 == 0 ? value + 0.5d : 0d;
                needRegenerationBytes.Value = value % 29 == 0 ? value + 0.25d : 0d;
                regeneratingBytes.Value = value % 37 == 0 ? value + 0.375d : 0d;
                pendingDeletionBytes.Value = value % 41 == 0 ? value + 0.875d : 0d;
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            samples = checked(samples + batchSize);
        }

        return samples;
    }

    private static long DatabaseFootprintBytes(string databasePath)
    {
        var primary = File.Exists(databasePath) ? new FileInfo(databasePath).Length : 0;
        var walPath = databasePath + "-wal";
        var wal = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        return checked(primary + wal);
    }

    private static string DeviceId(int device) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"synthetic-device-{device}")))
            .ToLowerInvariant();

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BundledSevenZipPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinPool.slnx")))
            {
                var executable = Path.Combine(
                    directory.FullName,
                    "assets",
                    "ThirdParty",
                    "7zip",
                    "26.03",
                    "x64",
                    "7za.exe");
                Assert.True(File.Exists(executable), $"Missing bundled 7-Zip asset: {executable}");
                return executable;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the WinPool repository root.");
    }

    private sealed record SevenZipProcessMeasurement(
        string Operation,
        int ExitCode,
        long PeakWorkingSetBytes,
        long ProcessorTimeMilliseconds);

    private sealed record MeasurementSampleIdentity(
        long TimestampUtcMilliseconds,
        double? ActivityPercent,
        double? ReadBytesPerSecond);

    private sealed record CompressionCandidateEvidence(
        string Arguments,
        long ArchiveBytes,
        double ArchiveToInputRatio,
        long CreateElapsedMilliseconds,
        long PeakWorkingSetBytes,
        long ProcessorTimeMilliseconds,
        int ProcessCount);

    private sealed record ParameterComparisonEvidence(
        long InputBytes,
        long Samples,
        CompressionCandidateEvidence Production,
        CompressionCandidateEvidence HigherResourceCandidate,
        string SelectionBasis);

    private sealed record ProductionRotationMeasurementEvidence(
        long ProductionThresholdBytes,
        long PreRotationFootprintBytes,
        long ReleasedDatabaseBytes,
        long ArchiveBytes,
        long PrepopulatedSamples,
        long ArchivedSamples,
        long ArchivedLiveBoundarySamples,
        long ActiveLiveBoundarySamples,
        string NewActiveMarkerStage,
        IReadOnlyList<MeasurementSampleIdentity> ArchivedLiveMarker,
        IReadOnlyList<MeasurementSampleIdentity> ActiveLiveMarker,
        long RotationAndArchiveElapsedMilliseconds,
        long SevenZipPeakWorkingSetBytes,
        long SevenZipProcessorTimeMilliseconds,
        IReadOnlyList<SevenZipProcessMeasurement> SevenZipProcesses,
        string ProductionArguments);

    /// <summary>
    /// Test-only wrapper: it observes the exact production adapter calls
    /// without changing the production process/7z implementation for a
    /// benchmark. Binary extraction remains streamed by the wrapped runner.
    /// </summary>
    private sealed class MeasuringProcessRunner : IControlledProcessRunner
    {
        private readonly IControlledProcessRunner inner;
        private readonly object gate = new();
        private readonly List<SevenZipProcessMeasurement> measurements = [];

        public MeasuringProcessRunner(IControlledProcessRunner? inner = null)
        {
            this.inner = inner ?? new ControlledProcessRunner();
        }

        public async Task<ControlledProcessResult> RunAsync(
            ControlledProcessInvocation invocation,
            CancellationToken cancellationToken)
        {
            var result = await inner.RunAsync(invocation, cancellationToken);
            Record(
                invocation,
                result.ExitCode,
                result.PeakWorkingSetBytes,
                result.ProcessorTimeMilliseconds);
            return result;
        }

        public async Task<ControlledProcessBinaryResult> RunBinaryOutputAsync(
            ControlledProcessInvocation invocation,
            Func<Stream, CancellationToken, Task> consumeStandardOutputAsync,
            CancellationToken cancellationToken)
        {
            var result = await inner.RunBinaryOutputAsync(
                invocation,
                consumeStandardOutputAsync,
                cancellationToken);
            Record(
                invocation,
                result.ExitCode,
                result.PeakWorkingSetBytes,
                result.ProcessorTimeMilliseconds);
            return result;
        }

        public IReadOnlyList<SevenZipProcessMeasurement> Snapshot()
        {
            lock (gate)
            {
                return measurements.ToArray();
            }
        }

        private void Record(
            ControlledProcessInvocation invocation,
            int exitCode,
            long peakWorkingSetBytes,
            long processorTimeMilliseconds)
        {
            var operation = invocation.Arguments.FirstOrDefault() switch
            {
                "a" => "create",
                "t" => "test",
                "x" => "extract",
                { } argument => argument,
                null => "unknown"
            };
            lock (gate)
            {
                measurements.Add(new SevenZipProcessMeasurement(
                    operation,
                    exitCode,
                    peakWorkingSetBytes,
                    processorTimeMilliseconds));
            }
        }
    }

    private sealed record MeasurementEvidence(
        long InputBytes,
        long ArchiveBytes,
        long Samples,
        long ElapsedMilliseconds,
        long SevenZipPeakWorkingSetBytes,
        long SevenZipProcessorMilliseconds,
        string Arguments);
}

/// <summary>
/// Marks an expensive isolated measurement as skipped during ordinary gates,
/// while still allowing an explicit environment switch to enable it.  The
/// decision happens during discovery so the xUnit 2 adapter reports a skip
/// rather than treating a runtime dynamic-skip exception as a failure.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EnvironmentEnabledFactAttribute : FactAttribute
{
    public EnvironmentEnabledFactAttribute(string environmentVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariable);
        if (!string.Equals(
                Environment.GetEnvironmentVariable(environmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {environmentVariable}=1 to run this isolated manual performance measurement.";
        }
    }
}
