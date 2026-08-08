using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class StorageLocationManagerTests
{
    [Fact]
    public async Task PlanThenApplyCopiesDatabaseAndAttachmentsAndRetainsSource()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "database");
        locations.WriteStandard(
            Path.Combine("Artifacts", "run-1", "result.json"),
            """{"iops":42}""");

        var coordinator = new RecordingCoordinator();
        var committer = new ObservingCommitter(coordinator);
        var manager = locations.CreateManager(coordinator, committer);
        var correlation = CorrelationId.New();

        var planResult = await manager.PlanSwitchAsync(
            StorageLocationMode.Portable,
            correlation,
            CancellationToken.None);

        Assert.True(planResult.IsSuccess);
        var plan = Assert.IsType<StorageLocationSwitchPlan>(planResult.Value);
        Assert.Equal(StorageLocationMode.Standard, plan.SourceMode);
        Assert.Equal(StorageLocationMode.Portable, plan.TargetMode);
        Assert.Equal(2, plan.FileCount);
        Assert.Equal(64, plan.SourceManifestSha256.Length);
        Assert.All(plan.SourceManifestSha256, character => Assert.True(Uri.IsHexDigit(character)));
        Assert.Equal(
            new FileInfo(Path.Combine(locations.StandardRoot, "winpool.db")).Length
            + new FileInfo(Path.Combine(
                locations.StandardRoot,
                "Artifacts",
                "run-1",
                "result.json")).Length,
            plan.TotalBytes);
        Assert.False(Directory.Exists(locations.PortableRoot));

        var applied = await manager.ApplySwitchAsync(
            plan,
            correlation,
            CancellationToken.None);

        Assert.True(applied.IsSuccess);
        Assert.Equal(StorageLocationMode.Portable, applied.Value!.Mode);
        Assert.Equal(
            Path.Combine(locations.PortableRoot, "winpool.db"),
            applied.Value.DatabasePath);
        Assert.Equal(
            "database",
            File.ReadAllText(Path.Combine(locations.PortableRoot, "winpool.db")));
        Assert.Equal(
            """{"iops":42}""",
            File.ReadAllText(Path.Combine(
                locations.PortableRoot,
                "Artifacts",
                "run-1",
                "result.json")));

        // Migration is copy + pointer commit: the old root remains recoverable.
        Assert.Equal(
            "database",
            File.ReadAllText(Path.Combine(locations.StandardRoot, "winpool.db")));
        Assert.True(File.Exists(Path.Combine(
            locations.StandardRoot,
            "Artifacts",
            "run-1",
            "result.json")));
        Assert.False(File.Exists(Path.Combine(
            locations.PortableRoot,
            StorageLocationManager.PointerFileName)));
        Assert.Equal(["quiesce", "commit", "resume"], coordinator.Events);
        Assert.False(coordinator.IsQuiesced);

        var current = await manager.GetCurrentAsync(correlation, CancellationToken.None);
        Assert.Equal(StorageLocationMode.Portable, current.Value!.Mode);
    }

    [Fact]
    public async Task RealSqliteMigrationVerifiesLogicalIdentityBeforePointerCommit()
    {
        using var locations = TemporaryLocations.Create();
        Directory.CreateDirectory(locations.StandardRoot);
        var sourceDatabase = Path.Combine(
            locations.StandardRoot,
            StorageLocationManager.DatabaseFileName);
        var store = new WinPoolSqliteStore(sourceDatabase);
        await store.InitializeAsync();
        await using (var connection = await store.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO preferences(key, json, updated_at_utc_ms)
                VALUES('migration-rehearsal', '{"enabled":true}', 1);
                PRAGMA wal_checkpoint(TRUNCATE);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var coordinator = new RecordingCoordinator();
        var manager = locations.CreateManager(
            coordinator,
            new ObservingCommitter(coordinator));
        var plan = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                CorrelationId.New(),
                CancellationToken.None)).Value);
        var applied = await manager.ApplySwitchAsync(
            plan,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.True(applied.IsSuccess);
        var destinationDatabase = Path.Combine(
            locations.PortableRoot,
            StorageLocationManager.DatabaseFileName);
        var auditor = new SqliteMigrationAuditor();
        var sourceAudit = await auditor.CaptureAsync(sourceDatabase);
        var destinationAudit = await auditor.CaptureAsync(destinationDatabase);
        Assert.True(sourceAudit.HasSameLogicalIdentity(destinationAudit));
        Assert.Equal(["quiesce", "commit", "resume"], coordinator.Events);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ApplyRejectsAPlanThatWasNotIssuedByTheManager()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "database");
        var coordinator = new RecordingCoordinator();
        var manager = locations.CreateManager(coordinator);
        var forged = new StorageLocationSwitchPlan(
            StorageLocationMode.Standard,
            StorageLocationMode.Portable,
            locations.StandardRoot,
            locations.PortableRoot,
            1,
            8,
            new string('0', 64),
            DateTimeOffset.UtcNow);

        var result = await manager.ApplySwitchAsync(
            forged,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Contains(
            result.Messages,
            message => message.Code == "storage.location.plan_not_issued");
        Assert.Empty(coordinator.Events);
        Assert.False(Directory.Exists(locations.PortableRoot));
    }

    [Fact]
    public async Task ApplyRejectsPlanWhenSourceChangedAfterPlanning()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "one");
        var coordinator = new RecordingCoordinator();
        var manager = locations.CreateManager(coordinator);
        var plan = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                CorrelationId.New(),
                CancellationToken.None)).Value);
        locations.WriteStandard("new-attachment.bin", "later");

        var result = await manager.ApplySwitchAsync(
            plan,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Contains(
            result.Messages,
            message => message.Code == "storage.location.plan_stale");
        Assert.Equal(["quiesce", "resume"], coordinator.Events);
        Assert.False(coordinator.IsQuiesced);
        var current = await manager.GetCurrentAsync(
            CorrelationId.New(),
            CancellationToken.None);
        Assert.Equal(StorageLocationMode.Standard, current.Value!.Mode);
    }

    [Fact]
    public async Task ApplyRejectsStaleTargetSqliteSidecarInsteadOfReusingIt()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "database");
        Directory.CreateDirectory(locations.PortableRoot);
        File.WriteAllText(
            Path.Combine(locations.PortableRoot, "winpool.db-wal"),
            "stale");
        var coordinator = new RecordingCoordinator();
        var manager = locations.CreateManager(coordinator);
        var plan = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                CorrelationId.New(),
                CancellationToken.None)).Value);

        var result = await manager.ApplySwitchAsync(
            plan,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Failed, result.Status);
        Assert.Contains(
            result.Messages,
            message => message.Code == "storage.location.apply_failed");
        Assert.False(File.Exists(Path.Combine(locations.PortableRoot, "winpool.db")));
        Assert.Equal(["quiesce", "resume"], coordinator.Events);
    }

    [Fact]
    public async Task ApplyRejectsSameLengthContentChangeEvenWhenTimestampIsRestored()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "AAAA");
        var sourcePath = Path.Combine(locations.StandardRoot, "winpool.db");
        var originalTimestamp = File.GetLastWriteTimeUtc(sourcePath);
        var coordinator = new RecordingCoordinator();
        var manager = locations.CreateManager(coordinator);
        var plan = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                CorrelationId.New(),
                CancellationToken.None)).Value);
        File.WriteAllText(sourcePath, "BBBB");
        File.SetLastWriteTimeUtc(sourcePath, originalTimestamp);

        var result = await manager.ApplySwitchAsync(
            plan,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Contains(
            result.Messages,
            message => message.Code == "storage.location.plan_stale");
        Assert.False(File.Exists(Path.Combine(locations.PortableRoot, "winpool.db")));
        Assert.Equal(["quiesce", "resume"], coordinator.Events);
    }

    [Fact]
    public async Task PointerCommitFailureKeepsOldPointerAndSourceActive()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "database");
        var oldPointer = """{"mode":"standard"}""";
        locations.WriteStandard(StorageLocationManager.PointerFileName, oldPointer);
        var coordinator = new RecordingCoordinator();
        var manager = locations.CreateManager(
            coordinator,
            new FailingCommitter(coordinator));
        var plan = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                CorrelationId.New(),
                CancellationToken.None)).Value);

        var result = await manager.ApplySwitchAsync(
            plan,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Failed, result.Status);
        Assert.Equal(
            oldPointer,
            File.ReadAllText(Path.Combine(
                locations.StandardRoot,
                StorageLocationManager.PointerFileName)));
        Assert.True(File.Exists(Path.Combine(locations.StandardRoot, "winpool.db")));
        Assert.True(File.Exists(Path.Combine(locations.PortableRoot, "winpool.db")));
        Assert.Equal(["quiesce", "commit-failed", "resume"], coordinator.Events);
        Assert.False(coordinator.IsQuiesced);

        var current = await manager.GetCurrentAsync(
            CorrelationId.New(),
            CancellationToken.None);
        Assert.Equal(StorageLocationMode.Standard, current.Value!.Mode);
    }

    [Fact]
    public async Task PlanRejectsTargetThatIsAFileAndCannotBeWrittenAsARoot()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "database");
        Directory.CreateDirectory(Path.GetDirectoryName(locations.PortableRoot)!);
        File.WriteAllText(locations.PortableRoot, "not-a-directory");
        var manager = locations.CreateManager(new RecordingCoordinator());

        var result = await manager.PlanSwitchAsync(
            StorageLocationMode.Portable,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Contains(
            result.Messages,
            message => message.Code == "storage.location.target_not_writable");
    }

    [Fact]
    public async Task PlanRejectsReparseTargetWhenPlatformAllowsCreatingOne()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "database");
        var realTarget = Path.Combine(locations.BaseDirectory, "real-target");
        Directory.CreateDirectory(realTarget);
        try
        {
            Directory.CreateSymbolicLink(locations.PortableRoot, realTarget);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or PlatformNotSupportedException)
        {
            // Windows without Developer Mode may forbid unelevated symlink creation.
            return;
        }

        var manager = locations.CreateManager(new RecordingCoordinator());
        var result = await manager.PlanSwitchAsync(
            StorageLocationMode.Portable,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Contains(
            result.Messages,
            message => message.Code == "storage.location.reparse_path_rejected");
    }

    [Fact]
    public async Task AtomicCommitterReplacesPointerAndLeavesNoTemporaryFile()
    {
        using var locations = TemporaryLocations.Create();
        Directory.CreateDirectory(locations.StandardRoot);
        var pointerPath = Path.Combine(
            locations.StandardRoot,
            StorageLocationManager.PointerFileName);
        File.WriteAllText(pointerPath, """{"mode":"standard"}""");
        var committer = new AtomicStorageLocationPointerCommitter();

        await committer.CommitAsync(
            pointerPath,
            StorageLocationMode.Portable,
            CancellationToken.None);

        var manager = locations.CreateManager(new RecordingCoordinator());
        var current = await manager.GetCurrentAsync(
            CorrelationId.New(),
            CancellationToken.None);
        Assert.Equal(StorageLocationMode.Portable, current.Value!.Mode);
        Assert.Empty(Directory.EnumerateFiles(
            locations.StandardRoot,
            StorageLocationManager.PointerFileName + ".tmp-*"));
    }

    [Fact]
    public async Task AtomicCommitterFailureKeepsExistingPointerAndCleansTemporaryFile()
    {
        using var locations = TemporaryLocations.Create();
        Directory.CreateDirectory(locations.StandardRoot);
        var pointerPath = Path.Combine(
            locations.StandardRoot,
            StorageLocationManager.PointerFileName);
        var oldPointer = """{"mode":"standard"}""";
        File.WriteAllText(pointerPath, oldPointer);
        var committer = new AtomicStorageLocationPointerCommitter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => committer.CommitAsync(
                pointerPath,
                StorageLocationMode.Portable,
                cancellation.Token));

        Assert.Equal(oldPointer, File.ReadAllText(pointerPath));
        Assert.Empty(Directory.EnumerateFiles(
            locations.StandardRoot,
            StorageLocationManager.PointerFileName + ".tmp-*"));
    }

    [Fact]
    public async Task CancelledPlanningReturnsCancelledResult()
    {
        using var locations = TemporaryLocations.Create();
        var manager = locations.CreateManager(new RecordingCoordinator());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await manager.PlanSwitchAsync(
            StorageLocationMode.Portable,
            CorrelationId.New(),
            cancellation.Token);

        Assert.Equal(ApplicationStatus.Cancelled, result.Status);
    }

    private sealed class RecordingCoordinator : IStorageWriteQuiescenceCoordinator
    {
        public List<string> Events { get; } = [];

        public bool IsQuiesced { get; private set; }

        public Task<IAsyncDisposable> QuiesceAndFlushAsync(
            CorrelationId correlationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(IsQuiesced);
            IsQuiesced = true;
            Events.Add("quiesce");
            return Task.FromResult<IAsyncDisposable>(new Lease(this));
        }

        private sealed class Lease(RecordingCoordinator owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Assert.True(owner.IsQuiesced);
                owner.IsQuiesced = false;
                owner.Events.Add("resume");
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ObservingCommitter(RecordingCoordinator coordinator)
        : IStorageLocationPointerCommitter
    {
        private readonly AtomicStorageLocationPointerCommitter inner = new();

        public Task CommitAsync(
            string pointerPath,
            StorageLocationMode mode,
            CancellationToken cancellationToken)
        {
            Assert.True(coordinator.IsQuiesced);
            coordinator.Events.Add("commit");
            return inner.CommitAsync(pointerPath, mode, cancellationToken);
        }
    }

    private sealed class FailingCommitter(RecordingCoordinator coordinator)
        : IStorageLocationPointerCommitter
    {
        public Task CommitAsync(
            string pointerPath,
            StorageLocationMode mode,
            CancellationToken cancellationToken)
        {
            Assert.True(coordinator.IsQuiesced);
            coordinator.Events.Add("commit-failed");
            throw new IOException("Injected pointer commit failure.");
        }
    }

    private sealed class TemporaryLocations : IDisposable
    {
        private TemporaryLocations(string baseDirectory)
        {
            BaseDirectory = baseDirectory;
            StandardRoot = Path.Combine(baseDirectory, "standard");
            PortableRoot = Path.Combine(baseDirectory, "portable");
        }

        public string BaseDirectory { get; }

        public string StandardRoot { get; }

        public string PortableRoot { get; }

        public static TemporaryLocations Create()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "WinPool.StorageLocation.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return new TemporaryLocations(directory);
        }

        public StorageLocationManager CreateManager(
            IStorageWriteQuiescenceCoordinator coordinator,
            IStorageLocationPointerCommitter? committer = null) =>
            new(StandardRoot, PortableRoot, coordinator, committer);

        public void WriteStandard(string relativePath, string contents)
        {
            var path = Path.Combine(StandardRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void Dispose()
        {
            if (!Directory.Exists(BaseDirectory))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         BaseDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }
                catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException)
                {
                }
            }

            Directory.Delete(BaseDirectory, recursive: true);
        }
    }
}
