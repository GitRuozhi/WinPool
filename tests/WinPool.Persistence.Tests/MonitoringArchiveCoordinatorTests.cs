using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;
using System.Text.Json;

namespace WinPool.Persistence.Tests;

public sealed class MonitoringArchiveCoordinatorTests
{
    [Fact]
    public async Task VerifiedArchiveReleasesOnlyItsOwnSealedDatabase()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var store = new MonitoringSqliteStore(fixture.ActiveDatabasePath);
        await store.InitializeAsync();
        var sessionId = SessionId.New();
        var systemId = SystemId.New();
        var sample = new MonitorSample(
            sessionId,
            new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "archive-test"),
            DateTimeOffset.UtcNow,
            [new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, 17)]);
        await using (var lease = AgentWriteOwnerLease.Acquire(store, "archive-test-agent"))
        {
            await new MonitorSessionRepository(store, lease).CreateAsync(
                new PersistedMonitorSession(
                    sessionId,
                    sample.SampledAtUtc,
                    null,
                    "Stopwatch+UTC",
                    MonitoringSessionState.Running,
                    0));
            await new MonitorSampleRepository(store, lease).WriteBatchAsync([sample]);
        }

        await store.CheckpointAndCloseAsync();
        var archiveRoot = Path.Combine(
            fixture.DataRoot,
            MonitoringArchiveCoordinator.ArchiveDirectoryName,
            MonitoringArchiveCoordinator.SealedDirectoryName);
        Directory.CreateDirectory(archiveRoot);
        var rawPath = Path.Combine(archiveRoot, "monitoring-sealed.db");
        File.Move(fixture.ActiveDatabasePath, rawPath);
        var rawHash = await HashFileAsync(rawPath);

        await using var coordinator = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await coordinator.InitializeAsync();
        var registered = await coordinator.RegisterSealedDatabaseAsync(
            rawPath,
            sessionId,
            Guid.NewGuid().ToString("N"));
        await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(30));

        var completed = Assert.Single(coordinator.Snapshot());
        Assert.Equal(MonitoringArchiveStage.SourceReleased, completed.Stage);
        Assert.Equal(registered.GenerationId, completed.GenerationId);
        Assert.True(File.Exists(completed.ArchivePath));
        Assert.False(File.Exists(rawPath));
        Assert.True(File.Exists(Path.Combine(
            fixture.DataRoot,
            MonitoringArchiveCoordinator.ArchiveDirectoryName,
            MonitoringArchiveCoordinator.LedgerFileName)));

        var archive = new SevenZipArchiveAdapter();
        Assert.Equal(
            rawHash,
            await archive.HashExtractedEntryAsync(
                fixture.SevenZipPath,
                completed.ArchivePath,
                Path.GetFileName(rawPath),
                CancellationToken.None));
    }

    [Fact]
    public async Task FailedArchiveRetainsRawDatabaseAndRestartRetriesFromLedger()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var (rawPath, sessionId) = await CreateSealedDatabaseAsync(fixture);
        var failingAdapter = new SevenZipArchiveAdapter(new AlwaysFailingRunner());
        await using (var failing = new MonitoringArchiveCoordinator(
                         fixture.DataRoot,
                         fixture.DataRoot,
                         () => fixture.SevenZipPath,
                         failingAdapter,
                         TimeSpan.FromMilliseconds(50)))
        {
            await failing.InitializeAsync();
            await failing.RegisterSealedDatabaseAsync(rawPath, sessionId, Guid.NewGuid().ToString("N"));
            await failing.WaitForIdleAsync(TimeSpan.FromSeconds(10));

            var failed = Assert.Single(failing.Snapshot());
            Assert.Equal(MonitoringArchiveStage.Sealed, failed.Stage);
            Assert.NotNull(failed.LastError);
            Assert.True(File.Exists(rawPath));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(75));
        await using var recovered = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath,
            retryDelay: TimeSpan.FromMilliseconds(50));
        await recovered.InitializeAsync();
        await recovered.WaitForIdleAsync(TimeSpan.FromSeconds(30));

        var completed = Assert.Single(recovered.Snapshot());
        Assert.Equal(MonitoringArchiveStage.SourceReleased, completed.Stage);
        Assert.False(File.Exists(rawPath));
        Assert.True(File.Exists(completed.ArchivePath));
    }

    [Fact]
    public async Task DisposeCancelsActiveArchiveWorkAndLeavesTheRawDatabaseUnlockable()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var (rawPath, sessionId) = await CreateSealedDatabaseAsync(fixture);
        var runner = new CancellationObservingRunner();
        var coordinator = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath,
            new SevenZipArchiveAdapter(runner));
        var disposed = false;
        try
        {
            await coordinator.InitializeAsync();
            await coordinator.RegisterSealedDatabaseAsync(
                rawPath,
                sessionId,
                Guid.NewGuid().ToString("N"));
            await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));

            await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            disposed = true;
            await runner.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(File.Exists(rawPath));
            await using var raw = new FileStream(
                rawPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            Assert.True(raw.Length > 0);
        }
        finally
        {
            if (!disposed)
            {
                await coordinator.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task LedgerWriteFailureRemainsVisibleAndRetriesTheSameTemporaryArchivePath()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var (rawPath, sessionId) = await CreateSealedDatabaseAsync(fixture);
        var runner = new FirstCreateFailsAfterWritingPartialPackageRunner(new ControlledProcessRunner());
        var adapter = new SevenZipArchiveAdapter(runner);
        await using var coordinator = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath,
            adapter,
            TimeSpan.FromMilliseconds(100));
        await coordinator.InitializeAsync();
        await coordinator.RegisterSealedDatabaseAsync(rawPath, sessionId, Guid.NewGuid().ToString("N"));

        await runner.FirstCreateStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var ledgerPath = Path.Combine(
            fixture.DataRoot,
            MonitoringArchiveCoordinator.ArchiveDirectoryName,
            MonitoringArchiveCoordinator.LedgerFileName);
        await using (var ledgerLock = new FileStream(
                         ledgerPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.None))
        {
            runner.AllowFirstCreateFailure();
            await WaitUntilAsync(
                () => coordinator.GetDiagnostics().FailureOccurrenceId is not null,
                TimeSpan.FromSeconds(10));

            var transient = coordinator.GetDiagnostics();
            Assert.Contains("could not be recorded", transient.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.True(transient.ArchiveRawDatabaseRetained);
            Assert.StartsWith("ledger:", transient.FailureOccurrenceId, StringComparison.Ordinal);
            Assert.True(File.Exists(rawPath));
        }

        await WaitUntilAsync(
            () => coordinator.Snapshot().Single().Stage == MonitoringArchiveStage.SourceReleased,
            TimeSpan.FromSeconds(30));
        var completed = Assert.Single(coordinator.Snapshot());
        Assert.Equal(MonitoringArchiveStage.SourceReleased, completed.Stage);
        Assert.False(File.Exists(rawPath));
        Assert.True(File.Exists(completed.ArchivePath));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(
                fixture.DataRoot,
                MonitoringArchiveCoordinator.ArchiveDirectoryName,
                MonitoringArchiveCoordinator.PackageDirectoryName),
            "*.tmp.7z",
            SearchOption.TopDirectoryOnly));
        var recovered = coordinator.GetDiagnostics();
        Assert.Null(recovered.LastError);
        Assert.Null(recovered.FailureOccurrenceId);
    }

    [Fact]
    public async Task LedgerUsesArchiveRelativePathsAndResolvesThemAtMigratedRoot()
    {
        await using var source = await ArchiveFixture.CreateAsync();
        var sourceSealedRoot = Path.Combine(
            source.DataRoot,
            MonitoringArchiveCoordinator.ArchiveDirectoryName,
            MonitoringArchiveCoordinator.SealedDirectoryName);
        Directory.CreateDirectory(sourceSealedRoot);
        var rawPath = Path.Combine(sourceSealedRoot, "pending-migration.db");
        await using (var coordinator = new MonitoringArchiveCoordinator(
                         source.DataRoot,
                         source.DataRoot,
                         () => source.SevenZipPath))
        {
            await coordinator.InitializeAsync();
            await coordinator.CreatePendingSealAsync(
                rawPath,
                SessionId.New(),
                Guid.NewGuid().ToString("N"));
        }

        var ledgerPath = Path.Combine(
            source.DataRoot,
            MonitoringArchiveCoordinator.ArchiveDirectoryName,
            MonitoringArchiveCoordinator.LedgerFileName);
        var ledgerText = await File.ReadAllTextAsync(ledgerPath);
        Assert.DoesNotContain(source.DataRoot, ledgerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sealed/", ledgerText.Replace('\\', '/'), StringComparison.Ordinal);

        var targetRoot = Path.Combine(Path.GetTempPath(), $"WinPool archive migration {Guid.NewGuid():N}");
        try
        {
            CopyDirectory(
                Path.Combine(source.DataRoot, MonitoringArchiveCoordinator.ArchiveDirectoryName),
                Path.Combine(targetRoot, MonitoringArchiveCoordinator.ArchiveDirectoryName));
            await using var migrated = new MonitoringArchiveCoordinator(
                targetRoot,
                targetRoot,
                () => source.SevenZipPath);
            await migrated.InitializeAsync();

            var restored = Assert.Single(migrated.Snapshot());
            Assert.StartsWith(
                Path.GetFullPath(targetRoot),
                restored.RawDatabasePath,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(source.DataRoot, restored.RawDatabasePath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(
                Path.Combine(targetRoot, MonitoringArchiveCoordinator.ArchiveDirectoryName),
                restored.ArchivePath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(targetRoot))
            {
                Directory.Delete(targetRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExistingPartialManifestIsRejectedAndRawDatabaseIsRetained()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var (rawPath, sessionId) = await CreateSealedDatabaseAsync(fixture);
        await File.WriteAllTextAsync(rawPath + ".manifest.json", "{ incomplete");
        await using var coordinator = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath,
            retryDelay: TimeSpan.FromMinutes(1));
        await coordinator.InitializeAsync();
        await coordinator.RegisterSealedDatabaseAsync(rawPath, sessionId, Guid.NewGuid().ToString("N"));
        await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(20));

        var failed = Assert.Single(coordinator.Snapshot());
        Assert.Equal(MonitoringArchiveStage.Sealed, failed.Stage);
        Assert.Contains("manifest", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(rawPath));
        Assert.False(File.Exists(failed.ArchivePath));
    }

    [Fact]
    public async Task ArchiveAttemptUsesOneExecutablePathAfterSettingsChange()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var (rawPath, sessionId) = await CreateSealedDatabaseAsync(fixture);
        var alternateExecutable = Path.Combine(fixture.DataRoot, "alternate-7za.exe");
        File.Copy(fixture.SevenZipPath, alternateExecutable);
        var configuredExecutable = fixture.SevenZipPath;
        var runner = new PathRecordingRunner(
            new ControlledProcessRunner(),
            () => configuredExecutable = alternateExecutable);
        var adapter = new SevenZipArchiveAdapter(runner);
        await using var coordinator = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => configuredExecutable,
            adapter);
        await coordinator.InitializeAsync();
        await coordinator.RegisterSealedDatabaseAsync(rawPath, sessionId, Guid.NewGuid().ToString("N"));
        await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(30));

        Assert.NotEmpty(runner.ExecutablePaths);
        Assert.All(
            runner.ExecutablePaths,
            path => Assert.Equal(fixture.SevenZipPath, path));
        Assert.Equal(alternateExecutable, configuredExecutable);
    }

    [Fact]
    public async Task ArchiveManifestDescribesEverySessionAndUsesSampleUtcRangeInName()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var (rawPath, triggerSessionId, allSessionIds, first, last) =
            await CreateMultiSessionSealedDatabaseAsync(fixture);
        await using var coordinator = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await coordinator.InitializeAsync();
        await coordinator.RegisterSealedDatabaseAsync(
            rawPath,
            triggerSessionId,
            Guid.NewGuid().ToString("N"));
        await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(30));

        var completed = Assert.Single(coordinator.Snapshot());
        Assert.Equal(MonitoringArchiveStage.SourceReleased, completed.Stage);
        Assert.Equal(
            allSessionIds.OrderBy(value => value, StringComparer.Ordinal),
            completed.SessionIds.OrderBy(value => value, StringComparer.Ordinal));
        var packageName = Path.GetFileName(completed.ArchivePath);
        Assert.Contains(first.ToString("yyyyMMddTHHmmssZ"), packageName, StringComparison.Ordinal);
        Assert.Contains(last.ToString("yyyyMMddTHHmmssZ"), packageName, StringComparison.Ordinal);

        var extractionDirectory = Path.Combine(fixture.DataRoot, "manifest-extract");
        Directory.CreateDirectory(extractionDirectory);
        var result = await new ControlledProcessRunner().RunAsync(
            new ControlledProcessInvocation(
                fixture.SevenZipPath,
                ["x", "-y", "-bso0", "-bsp0", $"-o{extractionDirectory}", completed.ArchivePath]),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(extractionDirectory, Path.GetFileName(completed.ManifestPath))));
        var manifestSessions = manifest.RootElement
            .GetProperty("SessionIds")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();
        Assert.Equal(
            allSessionIds.OrderBy(value => value, StringComparer.Ordinal),
            manifestSessions.OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RestartDoesNotRescanAlreadyReleasedPackageContents()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var (rawPath, sessionId) = await CreateSealedDatabaseAsync(fixture);
        MonitoringArchiveRecord released;
        await using (var coordinator = new MonitoringArchiveCoordinator(
                         fixture.DataRoot,
                         fixture.DataRoot,
                         () => fixture.SevenZipPath))
        {
            await coordinator.InitializeAsync();
            await coordinator.RegisterSealedDatabaseAsync(
                rawPath,
                sessionId,
                Guid.NewGuid().ToString("N"));
            await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(30));
            released = Assert.Single(coordinator.Snapshot());
            Assert.Equal(MonitoringArchiveStage.SourceReleased, released.Stage);
        }

        var package = await File.ReadAllBytesAsync(released.ArchivePath);
        Assert.NotEmpty(package);
        package[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(released.ArchivePath, package);

        await using var restarted = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await restarted.InitializeAsync();

        // SourceReleased is written only after test + both stream hashes have
        // passed. A startup scan checks package presence but must not unpack
        // every historical archive and delay new monitoring writes.
        var releasedAgain = Assert.Single(restarted.Snapshot());
        Assert.Equal(MonitoringArchiveStage.SourceReleased, releasedAgain.Stage);
        Assert.False(File.Exists(releasedAgain.RawDatabasePath));
        Assert.Null(releasedAgain.LastError);
        Assert.Equal(0, restarted.GetDiagnostics().FailedArchives);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartVerifiesArchiveCompletedPackageBeforeReleasingItsRawDatabase(
        bool corruptCompletedPackage)
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        var (rawPath, sessionId) = await CreateSealedDatabaseAsync(fixture);
        var metadata = await ReadArchiveMetadataAsync(rawPath);
        var generationId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var archiveRoot = Path.Combine(fixture.DataRoot, MonitoringArchiveCoordinator.ArchiveDirectoryName);
        var packageRoot = Path.Combine(archiveRoot, MonitoringArchiveCoordinator.PackageDirectoryName);
        Directory.CreateDirectory(packageRoot);
        var manifestPath = rawPath + ".manifest.json";
        var archivePath = Path.Combine(packageRoot, "completed-before-release.7z");
        var record = new MonitoringArchiveRecord(
            generationId,
            MonitoringArchiveStage.ArchiveCompleted,
            rawPath,
            manifestPath,
            Path.Combine(packageRoot, "completed-before-release.tmp.7z"),
            archivePath,
            sessionId.Value.ToString("N"),
            metadata.FirstSampleUtcMs,
            metadata.LastSampleUtcMs,
            metadata.SampleCount,
            new FileInfo(rawPath).Length,
            await HashFileAsync(rawPath),
            now,
            now)
        {
            SessionIds = [sessionId.Value.ToString("N")],
            MetadataComplete = true
        };
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            MonitoringSchemaVersion = MonitoringSqliteStore.CurrentSchemaVersion,
            GenerationId = record.GenerationId,
            RotationSessionId = record.SessionId,
            SessionIds = record.SessionIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            record.FirstSampleUtcMs,
            record.LastSampleUtcMs,
            record.SampleCount,
            record.DatabaseBytes,
            record.DatabaseSha256
        });
        await File.WriteAllBytesAsync(manifestPath, manifest);
        await new SevenZipArchiveAdapter().CreateArchiveAsync(
            fixture.SevenZipPath,
            archivePath,
            Path.GetDirectoryName(rawPath)!,
            [Path.GetFileName(rawPath), Path.GetFileName(manifestPath)],
            CancellationToken.None);
        if (corruptCompletedPackage)
        {
            // Replace the package rather than merely changing trailing padding:
            // the restart must reach the verification path and reject it before
            // it can delete the only raw database.
            await File.WriteAllBytesAsync(archivePath, [0x00, 0x01, 0x02, 0x03]);
        }

        var ledgerRecord = record with
        {
            RawDatabasePath = Path.GetRelativePath(archiveRoot, record.RawDatabasePath),
            ManifestPath = Path.GetRelativePath(archiveRoot, record.ManifestPath),
            TemporaryArchivePath = Path.GetRelativePath(archiveRoot, record.TemporaryArchivePath),
            ArchivePath = Path.GetRelativePath(archiveRoot, record.ArchivePath)
        };
        await File.WriteAllTextAsync(
            Path.Combine(archiveRoot, MonitoringArchiveCoordinator.LedgerFileName),
            JsonSerializer.Serialize(
                new[] { ledgerRecord },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        await using var restarted = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await restarted.InitializeAsync();
        await restarted.WaitForIdleAsync(TimeSpan.FromSeconds(30));

        var afterRestart = Assert.Single(restarted.Snapshot());
        if (corruptCompletedPackage)
        {
            Assert.NotEqual(MonitoringArchiveStage.SourceReleased, afterRestart.Stage);
            Assert.NotNull(afterRestart.LastError);
            Assert.True(File.Exists(rawPath));
            var diagnostics = restarted.GetDiagnostics();
            Assert.True(diagnostics.ArchiveRawDatabaseRetained);
            Assert.NotNull(diagnostics.LastError);
            return;
        }

        Assert.True(
            afterRestart.Stage == MonitoringArchiveStage.SourceReleased,
            afterRestart.LastError ?? "The archive did not report a retry error.");
        Assert.False(File.Exists(rawPath));
        Assert.True(File.Exists(archivePath));
    }

    private static async Task<(string RawPath, SessionId SessionId)> CreateSealedDatabaseAsync(
        ArchiveFixture fixture)
    {
        var store = new MonitoringSqliteStore(fixture.ActiveDatabasePath);
        await store.InitializeAsync();
        var sessionId = SessionId.New();
        var systemId = SystemId.New();
        var sample = new MonitorSample(
            sessionId,
            new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "archive-retry"),
            DateTimeOffset.UtcNow,
            [new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, 3)]);
        await using (var lease = AgentWriteOwnerLease.Acquire(store, "archive-retry-agent"))
        {
            await new MonitorSessionRepository(store, lease).CreateAsync(
                new PersistedMonitorSession(
                    sessionId,
                    sample.SampledAtUtc,
                    null,
                    "Stopwatch+UTC",
                    MonitoringSessionState.Running,
                    0));
            await new MonitorSampleRepository(store, lease).WriteBatchAsync([sample]);
        }

        await store.CheckpointAndCloseAsync();
        var sealedRoot = Path.Combine(
            fixture.DataRoot,
            MonitoringArchiveCoordinator.ArchiveDirectoryName,
            MonitoringArchiveCoordinator.SealedDirectoryName);
        Directory.CreateDirectory(sealedRoot);
        var rawPath = Path.Combine(sealedRoot, "monitoring-retry.db");
        File.Move(fixture.ActiveDatabasePath, rawPath);
        return (rawPath, sessionId);
    }

    private static async Task<(string RawPath, SessionId TriggerSessionId, string[] SessionIds, DateTimeOffset First, DateTimeOffset Last)>
        CreateMultiSessionSealedDatabaseAsync(ArchiveFixture fixture)
    {
        var store = new MonitoringSqliteStore(fixture.ActiveDatabasePath);
        await store.InitializeAsync();
        var first = new DateTimeOffset(2026, 9, 21, 1, 2, 3, TimeSpan.Zero);
        var last = first.AddMinutes(7);
        var firstSession = SessionId.New();
        var triggerSession = SessionId.New();
        var systemId = SystemId.New();
        var target = new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "archive-multi-session");
        await using (var lease = AgentWriteOwnerLease.Acquire(store, "archive-multi-session-agent"))
        {
            var sessions = new MonitorSessionRepository(store, lease);
            await sessions.CreateAsync(new PersistedMonitorSession(
                firstSession, first, first.AddMinutes(1), "Stopwatch+UTC", MonitoringSessionState.Stopped, 0));
            await sessions.CreateAsync(new PersistedMonitorSession(
                triggerSession, first.AddMinutes(2), null, "Stopwatch+UTC", MonitoringSessionState.Running, 0));
            await new MonitorSampleRepository(store, lease).WriteBatchAsync(
            [
                new MonitorSample(firstSession, target, first,
                    [new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, 4)]),
                new MonitorSample(triggerSession, target, last,
                    [new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, 9)])
            ]);
        }

        await store.CheckpointAndCloseAsync();
        var sealedRoot = Path.Combine(
            fixture.DataRoot,
            MonitoringArchiveCoordinator.ArchiveDirectoryName,
            MonitoringArchiveCoordinator.SealedDirectoryName);
        Directory.CreateDirectory(sealedRoot);
        var rawPath = Path.Combine(sealedRoot, "monitoring-multi-session.db");
        File.Move(fixture.ActiveDatabasePath, rawPath);
        return (
            rawPath,
            triggerSession,
            [firstSession.Value.ToString("N"), triggerSession.Value.ToString("N")],
            first,
            last);
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        Assert.True(condition(), "The expected asynchronous condition was not reached before its timeout.");
    }

    private static async Task<(long SampleCount, long? FirstSampleUtcMs, long? LastSampleUtcMs)> ReadArchiveMetadataAsync(
        string path)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = $"file:{path.Replace('\\', '/')}?immutable=1",
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MIN(timestamp_utc_ms), MAX(timestamp_utc_ms) FROM monitor_samples;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private sealed class ArchiveFixture : IAsyncDisposable
    {
        private ArchiveFixture(string dataRoot, string sevenZipPath)
        {
            DataRoot = dataRoot;
            SevenZipPath = sevenZipPath;
            ActiveDatabasePath = Path.Combine(dataRoot, "monitoring.db");
        }

        public string DataRoot { get; }

        public string SevenZipPath { get; }

        public string ActiveDatabasePath { get; }

        public static Task<ArchiveFixture> CreateAsync()
        {
            var dataRoot = Path.Combine(Path.GetTempPath(), $"WinPool archive {Guid.NewGuid():N}");
            Directory.CreateDirectory(dataRoot);
            var root = FindRepositoryRoot();
            var sevenZipPath = Path.Combine(
                root,
                "assets",
                "ThirdParty",
                "7zip",
                "26.03",
                "x64",
                "7za.exe");
            Assert.True(File.Exists(sevenZipPath), $"Missing bundled 7-Zip asset: {sevenZipPath}");
            return Task.FromResult(new ArchiveFixture(dataRoot, sevenZipPath));
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(DataRoot))
            {
                Directory.Delete(DataRoot, recursive: true);
            }

            return ValueTask.CompletedTask;
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "WinPool.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the WinPool repository root.");
        }
    }

    private sealed class AlwaysFailingRunner : IControlledProcessRunner
    {
        public Task<ControlledProcessResult> RunAsync(
            ControlledProcessInvocation invocation,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ControlledProcessResult(7, string.Empty, "intentional test failure"));

        public Task<ControlledProcessBinaryResult> RunBinaryOutputAsync(
            ControlledProcessInvocation invocation,
            Func<Stream, CancellationToken, Task> consumeStandardOutputAsync,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CancellationObservingRunner : IControlledProcessRunner
    {
        private readonly TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public Task CancellationObserved => cancellationObserved.Task;

        public async Task<ControlledProcessResult> RunAsync(
            ControlledProcessInvocation invocation,
            CancellationToken cancellationToken)
        {
            _ = invocation;
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ControlledProcessResult(0, string.Empty, string.Empty);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        }

        public Task<ControlledProcessBinaryResult> RunBinaryOutputAsync(
            ControlledProcessInvocation invocation,
            Func<Stream, CancellationToken, Task> consumeStandardOutputAsync,
            CancellationToken cancellationToken)
        {
            _ = invocation;
            _ = consumeStandardOutputAsync;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<ControlledProcessBinaryResult>(new InvalidOperationException(
                "Archive creation must finish before verification can begin."));
        }
    }

    private sealed class PathRecordingRunner(
        IControlledProcessRunner inner,
        Action firstInvocation) : IControlledProcessRunner
    {
        private int invocationCount;

        public List<string> ExecutablePaths { get; } = [];

        public async Task<ControlledProcessResult> RunAsync(
            ControlledProcessInvocation invocation,
            CancellationToken cancellationToken)
        {
            Record(invocation);
            return await inner.RunAsync(invocation, cancellationToken);
        }

        public async Task<ControlledProcessBinaryResult> RunBinaryOutputAsync(
            ControlledProcessInvocation invocation,
            Func<Stream, CancellationToken, Task> consumeStandardOutputAsync,
            CancellationToken cancellationToken)
        {
            Record(invocation);
            return await inner.RunBinaryOutputAsync(
                invocation,
                consumeStandardOutputAsync,
                cancellationToken);
        }

        private void Record(ControlledProcessInvocation invocation)
        {
            ExecutablePaths.Add(invocation.ExecutablePath);
            if (Interlocked.Increment(ref invocationCount) == 1)
            {
                firstInvocation();
            }
        }
    }

    private sealed class FirstCreateFailsAfterWritingPartialPackageRunner(
        IControlledProcessRunner inner) : IControlledProcessRunner
    {
        private readonly TaskCompletionSource firstCreateStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource allowFirstCreateFailure = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int createAttempts;

        public Task FirstCreateStarted => firstCreateStarted.Task;

        public void AllowFirstCreateFailure() => allowFirstCreateFailure.TrySetResult();

        public async Task<ControlledProcessResult> RunAsync(
            ControlledProcessInvocation invocation,
            CancellationToken cancellationToken)
        {
            if (invocation.Arguments.FirstOrDefault() == "a"
                && Interlocked.Increment(ref createAttempts) == 1)
            {
                var temporaryArchivePath = invocation.Arguments
                    .Single(argument => argument.EndsWith(".tmp.7z", StringComparison.OrdinalIgnoreCase));
                await File.WriteAllBytesAsync(
                    temporaryArchivePath,
                    [0x00, 0x01, 0x02, 0x03],
                    cancellationToken);
                firstCreateStarted.TrySetResult();
                await allowFirstCreateFailure.Task.WaitAsync(cancellationToken);
                return new ControlledProcessResult(7, string.Empty, "intentional partial create failure");
            }

            return await inner.RunAsync(invocation, cancellationToken);
        }

        public Task<ControlledProcessBinaryResult> RunBinaryOutputAsync(
            ControlledProcessInvocation invocation,
            Func<Stream, CancellationToken, Task> consumeStandardOutputAsync,
            CancellationToken cancellationToken) =>
            inner.RunBinaryOutputAsync(invocation, consumeStandardOutputAsync, cancellationToken);
    }
}
