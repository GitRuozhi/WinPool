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
        locations.WriteStandard(
            Path.Combine(StorageLocationManager.RuntimeDirectoryName, "agent-endpoint.json"),
            """{"pipeName":"ephemeral"}""");

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

        Assert.True(
            applied.IsSuccess,
            string.Join(" | ", applied.Messages.Select(message => message.DiagnosticText))
            + " events=" + string.Join(",", coordinator.Events));
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
        Assert.False(File.Exists(Path.Combine(
            locations.PortableRoot,
            StorageLocationManager.RuntimeDirectoryName,
            "agent-endpoint.json")));
        Assert.Equal(["quiesce", "commit", "resume"], coordinator.Events);
        Assert.False(coordinator.IsQuiesced);

        var current = await manager.GetCurrentAsync(correlation, CancellationToken.None);
        Assert.Equal(StorageLocationMode.Portable, current.Value!.Mode);
    }

    [Fact]
    public async Task PostCommitCleanupFailureKeepsTargetStateAndReportsPartialSuccess()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "database");
        var coordinator = new RecordingCoordinator();
        var manager = locations.CreateManager(
            coordinator,
            new ObservingCommitter(coordinator),
            cleanup: path =>
            {
                if (Path.GetFileName(path).Contains(".portable.winpool-rollback-", StringComparison.Ordinal))
                {
                    throw new IOException("Injected post-commit cleanup failure.");
                }

                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            });
        var correlation = CorrelationId.New();
        var plan = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                correlation,
                CancellationToken.None)).Value);

        var applied = await manager.ApplySwitchAsync(plan, correlation, CancellationToken.None);

        Assert.Equal(ApplicationStatus.PartiallyCompleted, applied.Status);
        Assert.Equal(StorageLocationMode.Portable, applied.Value!.Mode);
        Assert.Contains(applied.Messages, message =>
            message.Code == "storage.location.cleanup_pending");
        Assert.Equal("database", File.ReadAllText(
            Path.Combine(locations.PortableRoot, StorageLocationManager.DatabaseFileName)));
        var current = await manager.GetCurrentAsync(correlation, CancellationToken.None);
        Assert.Equal(StorageLocationMode.Portable, current.Value!.Mode);
    }

    [Fact]
    public async Task QuiesceReleasesSourceDatabaseHandleBeforeSnapshot()
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
                INSERT INTO test_presets(preset_id, json, created_at_utc_ms, updated_at_utc_ms)
                VALUES('migration-rehearsal', '{"enabled":true}', 1, 1);
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

        Assert.True(
            applied.IsSuccess,
            string.Join(" | ", applied.Messages.Select(message => message.DiagnosticText))
            + " events=" + string.Join(",", coordinator.Events));
        var destinationDatabase = Path.Combine(
            locations.PortableRoot,
            StorageLocationManager.DatabaseFileName);
        var auditor = new SqliteMigrationAuditor();
        var sourceAudit = await auditor.CaptureAsync(sourceDatabase);
        var destinationAudit = await auditor.CaptureAsync(destinationDatabase);
        Assert.True(sourceAudit.HasSameLogicalIdentity(destinationAudit));
        Assert.Equal(["quiesce", "commit", "resume"], coordinator.Events);
        AssertDatabaseCanBeOpenedExclusively(sourceDatabase);
        AssertDatabaseCanBeOpenedExclusively(destinationDatabase);
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
        Assert.Empty(coordinator.Events);
        Assert.False(coordinator.IsQuiesced);
        var current = await manager.GetCurrentAsync(
            CorrelationId.New(),
            CancellationToken.None);
        Assert.Equal(StorageLocationMode.Standard, current.Value!.Mode);
    }

    [Fact]
    public async Task StaleTargetSqliteSidecarDoesNotSurviveManagedPayloadMigration()
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

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(Path.Combine(locations.PortableRoot, "winpool.db")));
        Assert.False(File.Exists(Path.Combine(locations.PortableRoot, "winpool.db-wal")));
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
        Assert.Empty(coordinator.Events);
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
        Assert.False(Directory.Exists(locations.PortableRoot));
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

    [Fact]
    public async Task SourceMutationAfterHandleDrainRejectsPlan()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "database");
        var manager = locations.CreateManager(new CallbackCoordinator(
            () => locations.WriteStandard("late-write.bin", "changed")));
        var plan = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                CorrelationId.New(),
                CancellationToken.None)).Value);

        var result = await manager.ApplySwitchAsync(
            plan,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Contains(result.Messages, message => message.Code == "storage.location.plan_stale");
        Assert.False(Directory.Exists(locations.PortableRoot));
        AssertNoMigrationTemporaryRoots(locations, "portable");
    }

    [Fact]
    public async Task StaleOrdinaryTargetFileDoesNotSurviveManagedPayloadMigration()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "new-database");
        Directory.CreateDirectory(locations.PortableRoot);
        File.WriteAllText(Path.Combine(locations.PortableRoot, "stale.txt"), "old");
        var manager = locations.CreateManager(new RecordingCoordinator());
        var plan = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                CorrelationId.New(),
                CancellationToken.None)).Value);

        var result = await manager.ApplySwitchAsync(
            plan,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(File.Exists(Path.Combine(locations.PortableRoot, "stale.txt")));
        Assert.Equal("new-database", File.ReadAllText(
            Path.Combine(locations.PortableRoot, "winpool.db")));
    }

    [Fact]
    public async Task TargetManifestExactlyMatchesSource()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "database");
        locations.WriteStandard(Path.Combine("Artifacts", "a.bin"), "alpha");
        locations.WriteStandard(Path.Combine("Nested", "b.json"), "{\"b\":2}");
        Directory.CreateDirectory(locations.PortableRoot);
        File.WriteAllText(Path.Combine(locations.PortableRoot, "stale.txt"), "stale");
        var manager = locations.CreateManager(new RecordingCoordinator());
        var plan = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                CorrelationId.New(),
                CancellationToken.None)).Value);

        var result = await manager.ApplySwitchAsync(
            plan,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ReadManagedPayload(locations.StandardRoot),
            ReadManagedPayload(locations.PortableRoot));
    }

    [Fact]
    public async Task PointerFailureRestoresPreviousTarget()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "new");
        Directory.CreateDirectory(locations.PortableRoot);
        File.WriteAllText(Path.Combine(locations.PortableRoot, "previous.txt"), "previous");
        var coordinator = new RecordingCoordinator();
        var manager = locations.CreateManager(coordinator, new FailingCommitter(coordinator));
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
        Assert.Equal("previous", File.ReadAllText(
            Path.Combine(locations.PortableRoot, "previous.txt")));
        Assert.False(File.Exists(Path.Combine(locations.PortableRoot, "winpool.db")));
        AssertNoMigrationTemporaryRoots(locations, "portable");
    }

    [Fact]
    public async Task CancellationRestoresPreviousTarget()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "new");
        Directory.CreateDirectory(locations.PortableRoot);
        File.WriteAllText(Path.Combine(locations.PortableRoot, "previous.txt"), "previous");
        using var cancellation = new CancellationTokenSource();
        var manager = locations.CreateManager(
            new RecordingCoordinator(),
            new CancellingCommitter(cancellation));
        var plan = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                CorrelationId.New(),
                CancellationToken.None)).Value);

        var result = await manager.ApplySwitchAsync(
            plan,
            CorrelationId.New(),
            cancellation.Token);

        Assert.Equal(ApplicationStatus.Cancelled, result.Status);
        Assert.Equal("previous", File.ReadAllText(
            Path.Combine(locations.PortableRoot, "previous.txt")));
        Assert.False(File.Exists(Path.Combine(locations.PortableRoot, "winpool.db")));
        AssertNoMigrationTemporaryRoots(locations, "portable");
    }

    [Fact]
    public async Task HashMismatchRestoresPreviousTarget()
    {
        using var locations = TemporaryLocations.Create();
        await CreateRealDatabaseAsync(locations.StandardRoot);
        locations.WriteStandard("attachment.txt", "source");
        Directory.CreateDirectory(locations.PortableRoot);
        File.WriteAllText(Path.Combine(locations.PortableRoot, "previous.txt"), "previous");
        var auditor = new DelegatingAuditor((call, path, report) =>
        {
            if (call == 4)
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "attachment.txt"), "tampered");
            }

            return report;
        });
        var manager = locations.CreateManager(new RecordingCoordinator(), auditor: auditor);
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
        Assert.Equal("previous", File.ReadAllText(
            Path.Combine(locations.PortableRoot, "previous.txt")));
        AssertNoMigrationTemporaryRoots(locations, "portable");
    }

    [Fact]
    public async Task SqliteVerificationFailureRestoresPreviousTarget()
    {
        using var locations = TemporaryLocations.Create();
        await CreateRealDatabaseAsync(locations.StandardRoot);
        Directory.CreateDirectory(locations.PortableRoot);
        File.WriteAllText(Path.Combine(locations.PortableRoot, "previous.txt"), "previous");
        var auditor = new DelegatingAuditor((call, _, report) =>
            call == 4 ? report with { SchemaVersion = report.SchemaVersion + 1 } : report);
        var manager = locations.CreateManager(new RecordingCoordinator(), auditor: auditor);
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
        Assert.Equal("previous", File.ReadAllText(
            Path.Combine(locations.PortableRoot, "previous.txt")));
        AssertNoMigrationTemporaryRoots(locations, "portable");
    }

    [Fact]
    public async Task StandardToPortableToStandardRoundTripIsExact()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "database");
        locations.WriteStandard(Path.Combine("Artifacts", "result.json"), "{\"ok\":true}");
        var original = ReadManagedPayload(locations.StandardRoot);
        var manager = locations.CreateManager(new RecordingCoordinator());
        var toPortable = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                CorrelationId.New(),
                CancellationToken.None)).Value);
        Assert.True((await manager.ApplySwitchAsync(
            toPortable,
            CorrelationId.New(),
            CancellationToken.None)).IsSuccess);
        var toStandard = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Standard,
                CorrelationId.New(),
                CancellationToken.None)).Value);

        var result = await manager.ApplySwitchAsync(
            toStandard,
            CorrelationId.New(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(original, ReadManagedPayload(locations.StandardRoot));
        AssertNoMigrationTemporaryRoots(locations, "standard");
        AssertNoMigrationTemporaryRoots(locations, "portable");
    }

    [Fact]
    public async Task MigrationTemporaryRootsCanBeCleanedImmediately()
    {
        using var locations = TemporaryLocations.Create();
        locations.WriteStandard("winpool.db", "database");
        var manager = locations.CreateManager(new RecordingCoordinator());
        var plan = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                CorrelationId.New(),
                CancellationToken.None)).Value);

        Assert.True((await manager.ApplySwitchAsync(
            plan,
            CorrelationId.New(),
            CancellationToken.None)).IsSuccess);

        AssertNoMigrationTemporaryRoots(locations, "portable");
        Directory.Move(locations.PortableRoot, locations.PortableRoot + ".moved");
        Directory.Move(locations.PortableRoot + ".moved", locations.PortableRoot);
    }

    [Fact]
    public async Task MigratedDatabaseCanBeReopenedImmediately()
    {
        using var locations = TemporaryLocations.Create();
        await CreateRealDatabaseAsync(locations.StandardRoot);
        var manager = locations.CreateManager(new RecordingCoordinator());
        var plan = Assert.IsType<StorageLocationSwitchPlan>(
            (await manager.PlanSwitchAsync(
                StorageLocationMode.Portable,
                CorrelationId.New(),
                CancellationToken.None)).Value);

        Assert.True((await manager.ApplySwitchAsync(
            plan,
            CorrelationId.New(),
            CancellationToken.None)).IsSuccess);

        var migrated = Path.Combine(locations.PortableRoot, StorageLocationManager.DatabaseFileName);
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = migrated,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        Assert.Equal("ok", Convert.ToString(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task MigrationDoesNotDependOnGlobalSqlitePoolCleanup()
    {
        using var locations = TemporaryLocations.Create();
        await CreateRealDatabaseAsync(locations.StandardRoot);
        var unrelatedPath = Path.Combine(locations.BaseDirectory, "unrelated.db");
        var unrelated = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = unrelatedPath,
                Pooling = true
            }.ToString());
        await using (unrelated)
        {
            await unrelated.OpenAsync();
            await using (var command = unrelated.CreateCommand())
            {
                command.CommandText = "CREATE TABLE marker(value INTEGER); INSERT INTO marker VALUES(7);";
                await command.ExecuteNonQueryAsync();
            }

            var manager = locations.CreateManager(new RecordingCoordinator());
            var plan = Assert.IsType<StorageLocationSwitchPlan>(
                (await manager.PlanSwitchAsync(
                    StorageLocationMode.Portable,
                    CorrelationId.New(),
                    CancellationToken.None)).Value);

            Assert.True((await manager.ApplySwitchAsync(
                plan,
                CorrelationId.New(),
                CancellationToken.None)).IsSuccess);
            await using var verify = unrelated.CreateCommand();
            verify.CommandText = "SELECT value FROM marker;";
            Assert.Equal(7L, Convert.ToInt64(await verify.ExecuteScalarAsync()));
        }

        // Release only this test's deliberately pooled, unrelated connection
        // before the temporary fixture removes its own directory.
        SqliteConnection.ClearPool(unrelated);
    }

    private static async Task CreateRealDatabaseAsync(string root)
    {
        Directory.CreateDirectory(root);
        var store = new WinPoolSqliteStore(Path.Combine(
            root,
            StorageLocationManager.DatabaseFileName));
        await store.InitializeAsync();
        await using var connection = await store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync();
    }

    private static string[] ReadManagedPayload(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                return !string.Equals(
                           relative,
                           StorageLocationManager.PointerFileName,
                           StringComparison.OrdinalIgnoreCase)
                       && !relative.StartsWith(
                           StorageLocationManager.PointerFileName + ".tmp-",
                           StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(
                           relative,
                           StorageLocationManager.DatabaseFileName + "-wal",
                           StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(
                           relative,
                           StorageLocationManager.DatabaseFileName + "-shm",
                           StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(
                           relative,
                           StorageLocationManager.DatabaseFileName + "-journal",
                           StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
                Path.GetRelativePath(root, path).Replace('\\', '/')
                + "=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(path))))
            .ToArray();

    private static void AssertNoMigrationTemporaryRoots(
        TemporaryLocations locations,
        string targetName) =>
        Assert.Empty(Directory.EnumerateDirectories(
            locations.BaseDirectory,
            $".{targetName}.winpool-*",
            SearchOption.TopDirectoryOnly));

    private sealed class CallbackCoordinator(Action onQuiesced)
        : IStorageWriteQuiescenceCoordinator
    {
        public Task<IAsyncDisposable> QuiesceAndFlushAsync(
            CorrelationId correlationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onQuiesced();
            return Task.FromResult<IAsyncDisposable>(new NoopLease());
        }

        private sealed class NoopLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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

    private static void AssertDatabaseCanBeOpenedExclusively(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        Assert.True(stream.Length > 0);
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

    private sealed class CancellingCommitter(CancellationTokenSource cancellation)
        : IStorageLocationPointerCommitter
    {
        public Task CommitAsync(
            string pointerPath,
            StorageLocationMode mode,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The cancellation token should have thrown.");
        }
    }

    private sealed class DelegatingAuditor(
        Func<int, string, SqliteMigrationAuditReport, SqliteMigrationAuditReport> transform)
        : ISqliteMigrationAuditor
    {
        private readonly SqliteMigrationAuditor inner = new();
        private int callCount;

        public async Task<SqliteMigrationAuditReport> CaptureAsync(
            string databasePath,
            CancellationToken cancellationToken = default)
        {
            var report = await inner.CaptureAsync(databasePath, cancellationToken);
            return transform(++callCount, databasePath, report);
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
            IStorageLocationPointerCommitter? committer = null,
            ISqliteMigrationAuditor? auditor = null,
            Action<string>? cleanup = null) =>
            new(StandardRoot, PortableRoot, coordinator, committer, auditor, cleanup);

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
