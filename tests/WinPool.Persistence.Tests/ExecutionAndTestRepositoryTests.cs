using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;
using WinPool.Monitoring;

namespace WinPool.Persistence.Tests;

public sealed class ExecutionAndTestRepositoryTests
{
    [Fact]
    public async Task OperationPlanAndEventsRoundTripInStableOrder()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var plans = new OperationPlanRepository(database.Store, lease);
        var events = new ExecutionEventRepository(database.Store, lease);
        var plan = CreateOperationPlan();
        await plans.SaveAsync(plan);
        await plans.SetStateAsync(plan.OperationId, PersistedOperationState.Running);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        await events.AppendAsync(
            new ExecutionEvent(
                plan.OperationId,
                ExecutionEventKind.Started,
                at,
                "execution.started",
                "redacted"));
        await events.AppendAsync(
            new ExecutionEvent(
                plan.OperationId,
                ExecutionEventKind.Completed,
                at.AddMilliseconds(1),
                "execution.completed",
                string.Empty));

        var persisted = await new OperationPlanRepository(database.Store)
            .GetAsync(plan.OperationId);
        var history = await new ExecutionEventRepository(database.Store)
            .ListAsync(plan.OperationId);

        Assert.NotNull(persisted);
        Assert.Equal(PersistedOperationState.Running, persisted.State);
        Assert.Equal(plan.PlanHash, persisted.Plan.PlanHash);
        Assert.Equal(["execution.started", "execution.completed"],
            history.Select(item => item.Event.Code));
    }

    [Fact]
    public async Task OperationSaveIsAtomicWhenStepIdentityConflicts()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new OperationPlanRepository(database.Store, lease);
        var plan = CreateOperationPlan();
        var invalid = plan with
        {
            Steps =
            [
                new PlanStep("duplicate", "one", []),
                new PlanStep("duplicate", "two", [])
            ]
        };

        await Assert.ThrowsAsync<SqliteException>(() => repository.SaveAsync(invalid));

        Assert.Null(await new OperationPlanRepository(database.Store)
            .GetAsync(plan.OperationId));
    }

    [Fact]
    public async Task SystemSupportAuditSinkPersistsOnlyRedactedContract()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var writer = new SystemSupportAuditRepository(database.Store, lease);
        var correlation = CorrelationId.New();
        await writer.WriteAsync(
            new SystemSupportAuditEvent(
                correlation,
                new string('a', 64),
                SystemSupportActionKind.TrimOrOptimizeVolume,
                SystemSupportAuditStage.PolicyDecision,
                DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000),
                "system-support.policy.confirmed",
                "system-support.policy.confirmed",
                "target=redacted",
                "system-support-v1"),
            CancellationToken.None);

        var actual = Assert.Single(
            await new SystemSupportAuditRepository(database.Store)
                .ListAsync(new string('a', 64)));

        Assert.Equal(correlation, actual.Event.CorrelationId);
        Assert.Equal("target=redacted", actual.Event.RedactedDiagnostic);
        Assert.Equal(
            SystemSupportActionKind.TrimOrOptimizeVolume,
            actual.Event.ActionKind);
    }

    [Fact]
    public async Task TestRunDefinitionStepsMetricsAndHistogramPersistAtomically()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new TestRunRepository(database.Store, lease);
        var (definition, plan) = CreateTestPlan();
        await repository.SaveDefinitionAsync(definition, plan.CreatedAtUtc);
        await repository.CreateRunAsync(
            plan,
            """{"machine":"redacted","inventory":"v1"}""");
        await repository.UpdateStepStateAsync(
            plan.RunId,
            "step-1",
            ApplicationTaskState.Running);
        await repository.AddMetricAsync(
            plan.RunId,
            "step-1",
            "throughput",
            123.5,
            "MiB/s",
            "median");
        await repository.AddLatencyHistogramAsync(
            plan.RunId,
            "step-1",
            new Dictionary<long, long> { [1_000] = 10, [2_000] = 5 });
        await repository.AddWorkerEventsAsync(
            plan.RunId,
            [
                new(
                    plan.RunId,
                    "step-1",
                    WorkerEventKind.StandardOutput,
                    WorkerEventImportance.Output,
                    plan.CreatedAtUtc.AddSeconds(2),
                    "tool.process.stdout",
                    System.Text.Encoding.UTF8.GetBytes("sensitive raw output"),
                    42)
            ]);
        var ended = plan.CreatedAtUtc.AddSeconds(30);
        await repository.CompleteAsync(
            plan.RunId,
            PersistedTestRunState.Completed,
            ended);

        var actual = await new TestRunRepository(database.Store).GetAsync(plan.RunId);

        Assert.NotNull(actual);
        var persistedPlan = await new TestRunRepository(database.Store)
            .GetPlanAsync(plan.RunId);
        Assert.NotNull(persistedPlan);
        Assert.Equal(plan.PlanHash, persistedPlan.PlanHash);
        Assert.Equal(
            plan.Steps.Select(item => item.Id),
            persistedPlan.Steps.Select(item => item.Id));
        Assert.Equal(PersistedTestRunState.Completed, actual.State);
        Assert.Equal(ended, actual.EndedAtUtc);
        Assert.Contains("redacted", actual.EnvironmentSnapshotJson, StringComparison.Ordinal);
        var workerEvent = Assert.Single(
            await new TestRunRepository(database.Store)
                .ListWorkerEventsAsync(plan.RunId, 10));
        Assert.Equal("tool.process.stdout", workerEvent.Code);
        Assert.Equal(20, workerEvent.RawByteCount);
        Assert.Equal(42, workerEvent.ProcessId);
        var persistedStep = Assert.Single(
            await new TestRunRepository(database.Store)
                .ListStepsAsync(plan.RunId));
        Assert.Equal("step-1", persistedStep.StepId);
        Assert.Equal(ApplicationTaskState.Running, persistedStep.State);
        await using var connection = await database.Store.OpenConnectionAsync();
        Assert.Equal(1, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM test_steps;"));
        Assert.Equal((int)ApplicationTaskState.Running, await ScalarAsync(
            connection,
            "SELECT state FROM test_steps WHERE step_id='step-1';"));
        Assert.Equal(1, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM test_metrics;"));
        Assert.Equal(2, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM latency_histograms;"));
        Assert.Equal(1, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM test_events;"));
        var completedRuns = await new TestRunRepository(database.Store)
            .ListRunsAsync([PersistedTestRunState.Completed], 10);
        Assert.Equal(plan.RunId, Assert.Single(completedRuns).RunId);
        Assert.Empty(
            await new TestRunRepository(database.Store)
                .ListRunsAsync([PersistedTestRunState.Failed], 10));
    }

    [Fact]
    public async Task StartupRecoveryInterruptsOpenRunsAndRetainsResumablePlan()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease =
            AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new TestRunRepository(database.Store, lease);
        var (definition, plan) = CreateTestPlan();
        await repository.SaveDefinitionAsync(definition, plan.CreatedAtUtc);
        await repository.CreateRunAsync(
            plan,
            "{}",
            PersistedTestRunState.Running);
        var untouched = plan with
        {
            RunId = TestRunId.New(),
            PlanHash = new string('b', 64)
        };
        await repository.CreateRunAsync(
            untouched,
            "{}",
            PersistedTestRunState.Created);
        var recoveredAt = plan.CreatedAtUtc.AddMinutes(5);

        var recovered = await repository.RecoverInterruptedRunsAsync(
            recoveredAt);

        Assert.Equal(plan.RunId, Assert.Single(recovered));
        var interrupted = await repository.GetAsync(plan.RunId);
        Assert.NotNull(interrupted);
        Assert.Equal(PersistedTestRunState.Interrupted, interrupted.State);
        Assert.Equal(recoveredAt, interrupted.EndedAtUtc);
        var resumablePlan = await repository.GetPlanAsync(plan.RunId);
        Assert.NotNull(resumablePlan);
        Assert.Equal(plan.PlanHash, resumablePlan.PlanHash);
        Assert.Equal(
            plan.Steps.Select(item => item.Id),
            resumablePlan.Steps.Select(item => item.Id));
        Assert.Equal(
            PersistedTestRunState.Created,
            (await repository.GetAsync(untouched.RunId))!.State);
        await repository.ResumeInterruptedAsync(
            plan.RunId,
            plan.PlanHash,
            recoveredAt.AddSeconds(1));
        var resumed = await repository.GetAsync(plan.RunId);
        Assert.NotNull(resumed);
        Assert.Equal(PersistedTestRunState.Running, resumed.State);
        Assert.Null(resumed.EndedAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ResumeInterruptedAsync(
                plan.RunId,
                new string('0', 64),
                recoveredAt.AddSeconds(2)));
    }

    [Fact]
    public async Task ParsedToolMetricsAndCombinedHistogramPersistFromWorkerChunks()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new TestRunRepository(database.Store, lease);
        var (definition, plan) = CreateTestPlan();
        await repository.SaveDefinitionAsync(definition, plan.CreatedAtUtc);
        await repository.CreateRunAsync(plan, """{"source":"test"}""");
        var events = new[]
        {
            new WorkerEvent(
                plan.RunId,
                "step-1",
                WorkerEventKind.StandardOutput,
                WorkerEventImportance.Output,
                plan.CreatedAtUtc,
                "tool.process.stdout",
                System.Text.Encoding.UTF8.GetBytes("part-1")),
            new WorkerEvent(
                plan.RunId,
                "step-1",
                WorkerEventKind.StandardOutput,
                WorkerEventImportance.Output,
                plan.CreatedAtUtc.AddMilliseconds(1),
                "tool.process.stdout",
                System.Text.Encoding.UTF8.GetBytes("part-2"))
        };

        var failed = await new TestToolResultRepositoryWriter(repository)
            .PersistAsync(
                plan.RunId,
                "step-1",
                new ControlledAdapter(),
                events,
                0,
                ToolOutputEncoding.Utf8);

        Assert.False(failed);
        await using var connection = await database.Store.OpenConnectionAsync();
        Assert.Equal(1, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM test_metrics WHERE metric_name='throughput.total';"));
        Assert.Equal(7, await ScalarAsync(
            connection,
            "SELECT sample_count FROM latency_histograms WHERE bucket_upper_ns=2000;"));
    }

    [Fact]
    public async Task TestRunExportsCsvJsonMarkdownAndEvidencePackage()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var repository = new TestRunRepository(database.Store, lease);
        var artifactStore = new TestArtifactStore(database.Store, lease);
        var (definition, plan) = CreateTestPlan();
        await repository.SaveDefinitionAsync(definition, plan.CreatedAtUtc);
        await repository.CreateRunAsync(plan, """{"source":"test"}""");
        await repository.AddMetricAsync(
            plan.RunId,
            "step-1",
            "throughput",
            123.5,
            "MiB/s",
            "single");
        await repository.CompleteAsync(
            plan.RunId,
            PersistedTestRunState.Completed,
            plan.CreatedAtUtc.AddSeconds(1));
        await artifactStore.SaveGeneratedArtifactAsync(
            plan.RunId,
            "raw",
            "application/json",
            System.Text.Encoding.UTF8.GetBytes("""{"raw":true}"""));
        var exporter = new TestRunExporter(
            database.Store,
            repository,
            artifactStore);
        var exports = Path.Combine(database.Directory, "exports");
        var cases = new[]
        {
            (TestExportFormat.Csv, "result.csv"),
            (TestExportFormat.Json, "result.json"),
            (TestExportFormat.Markdown, "result.md"),
            (TestExportFormat.EvidencePackage, "result.zip")
        };

        foreach (var item in cases)
        {
            var result = await exporter.ExportAsync(
                plan.RunId,
                item.Item1,
                Path.Combine(exports, item.Item2),
                overwrite: false);
            Assert.True(File.Exists(result.DestinationPath));
            Assert.Equal(64, result.Sha256.Length);
            Assert.Equal(2, result.ItemCount);
        }

        using var archive = System.IO.Compression.ZipFile.OpenRead(
            Path.Combine(exports, "result.zip"));
        Assert.Contains(archive.Entries, item => item.FullName == "manifest.json");
        Assert.Contains(archive.Entries, item => item.FullName == "metrics.csv");
        Assert.Contains(archive.Entries, item => item.FullName == "report.md");
        Assert.Contains(
            archive.Entries,
            item => item.FullName.StartsWith(
                "attachments/",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadOnlyRepositoryInstancesRejectEveryWrite()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await Assert.ThrowsAsync<AgentWriteOwnershipException>(
            () => new OperationPlanRepository(database.Store)
                .SaveAsync(CreateOperationPlan()));
        await Assert.ThrowsAsync<AgentWriteOwnershipException>(
            () => new ExecutionEventRepository(database.Store)
                .AppendAsync(
                    new ExecutionEvent(
                        OperationId.New(),
                        ExecutionEventKind.Started,
                        DateTimeOffset.UtcNow,
                        "code",
                        string.Empty)));
    }

    [Fact]
    public async Task SqliteMonitorSessionPersistenceFlushesTailAndFinalState()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent");
        var system = SystemId.New();
        var sessionId = SessionId.New();
        var target = new StorageObjectId(
            system,
            StorageObjectKind.PhysicalDisk,
            "pdh:0 Disk");
        var request = new MonitorRequest(
            sessionId,
            system,
            [
                new MonitorTarget(
                    new StorageObjectId(
                        system,
                        StorageObjectKind.PhysicalDisk,
                        "pdh-wildcard"),
                    "*")
            ],
            [MonitorMetricKind.ActiveTimePercent],
            TimeSpan.FromSeconds(1),
            true);
        var started = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        var persistence = new SqliteMonitorSessionPersistenceFactory(
            database.Store,
            lease,
            channelCapacity: 4,
            maximumBatchSize: 4,
            maximumBatchDelay: TimeSpan.FromMinutes(1)).Create(sessionId);
        await persistence.StartAsync(
            new MonitoringSession(
                sessionId,
                request,
                MonitoringSessionState.Starting,
                started,
                null),
            CancellationToken.None);
        Assert.True(
            persistence.TryWrite(
                new MonitorSample(
                    sessionId,
                    target,
                    started.AddSeconds(1),
                    [new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, 42)],
                    false)));
        var ended = started.AddSeconds(2);
        await persistence.CompleteAsync(
            MonitoringSessionState.Stopped,
            ended,
            CancellationToken.None);
        await persistence.DisposeAsync();

        var session = await new MonitorSessionRepository(database.Store)
            .GetAsync(sessionId);
        var samples = await new MonitorSampleRepository(database.Store)
            .ReadRangeAsync(sessionId, started, ended);

        Assert.NotNull(session);
        Assert.Equal(MonitoringSessionState.Stopped, session.State);
        Assert.Equal(ended, session.EndedAtUtc);
        Assert.Equal(42, Assert.Single(samples).ActivityPercent);

        var csvPath = Path.Combine(database.Directory, "monitor.csv");
        var export = await new MonitorCsvExporter(database.Store).ExportAsync(
            sessionId,
            csvPath,
            overwrite: false);
        Assert.Equal(1, export.RowCount);
        Assert.Equal(64, export.Sha256.Length);
        var csv = await File.ReadAllTextAsync(csvPath);
        Assert.Contains("TimestampUtc,Device,ActivityPercent", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret", csv, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<IOException>(
            () => new MonitorCsvExporter(database.Store).ExportAsync(
                sessionId,
                csvPath,
                overwrite: false));
    }

    private static OperationPlan CreateOperationPlan()
    {
        var system = SystemId.New();
        var request = new OperationRequest(
            OperationId.New(),
            EnvironmentId.New(),
            system,
            OperationIntent.ReadInventory,
            [new StorageObjectId(system, StorageObjectKind.System, "system")],
            new Dictionary<string, string>(),
            DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000));
        return OperationPlan.Create(
            request,
            ExecutionCapability.ReadInventory,
            RiskLevel.R0ReadOnly,
            "inventory-v1",
            ["refresh"],
            [new PlanStep("read", "Read inventory", [])],
            null,
            "inventory",
            "none",
            "none",
            new AlgorithmIdentity(
                "ALGO-TEST",
                "1",
                AlgorithmConfidence.Derived,
                "test"),
            request.RequestedAt);
    }

    private static (TestDefinition Definition, TestPlan Plan) CreateTestPlan()
    {
        var definitionId = TestDefinitionId.New();
        var taskId = TestTaskId.New();
        var definition = new TestDefinition(
            definitionId,
            "Persistence test",
            "1",
            new Dictionary<string, TestParameter>(),
            [
                new TestTaskDefinition(
                    taskId,
                    "Read",
                    TestActionKind.RunIo,
                    null,
                    null,
                    new Dictionary<string, TestParameter>())
            ],
            [new TestScheduleStep("step-1", taskId, [], true)],
            AlgorithmConfidence.Derived);
        var system = SystemId.New();
        var created = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        var workspace = new TestWorkspacePlan(
            Path.GetTempPath(),
            Path.Combine(Path.GetTempPath(), "WinPoolRuns", Guid.NewGuid().ToString("N")),
            [],
            0,
            TestWorkspaceCleanupPolicy.KeepAll,
            created.AddHours(1));
        var plan = new TestPlan(
            TestRunId.New(),
            definitionId,
            "1",
            new TestTarget(
                system,
                new StorageObjectId(system, StorageObjectKind.Partition, "volume"),
                Path.GetTempPath(),
                long.MaxValue,
                true),
            workspace,
            [
                new TestStep(
                    "step-1",
                    TestActionKind.RunIo,
                    null,
                    null,
                    new Dictionary<string, TestParameter>(),
                    [],
                    true)
            ],
            [],
            [],
            0,
            RiskLevel.R2RecoverableFileWrite,
            new AlgorithmIdentity(
                "ALG-TEST",
                "1",
                AlgorithmConfidence.Derived,
                "test"),
            created,
            new string('b', 64));
        return (definition, plan);
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class ControlledAdapter : IExternalToolAdapter
    {
        public ToolId ToolId => new("controlled");

        public ToolCapabilities Capabilities => ToolCapabilities.StructuredOutput;

        public ApplicationResult<ToolInvocation> BuildInvocation(
            TestStep step,
            WinPool.Application.AuthorizedTestWorkspace workspace,
            CorrelationId correlationId) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ToolEvent> ParseAsync(
            ToolProcessStreams streams,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            await foreach (var chunk in streams.Chunks.WithCancellation(
                               cancellationToken))
            {
                await output.WriteAsync(chunk.Bytes, cancellationToken);
            }

            Assert.Equal("part-1part-2", System.Text.Encoding.UTF8.GetString(output.ToArray()));
            yield return new(
                ToolId,
                ToolEventKind.Metric,
                DateTimeOffset.UtcNow,
                "metric",
                string.Empty,
                new("throughput.total", 12.5, "MiB/s", DateTimeOffset.UtcNow));
            yield return new(
                ToolId,
                ToolEventKind.Metric,
                DateTimeOffset.UtcNow,
                "histogram",
                string.Empty,
                HistogramBucket: new("read", 2000, 3));
            yield return new(
                ToolId,
                ToolEventKind.Metric,
                DateTimeOffset.UtcNow,
                "histogram",
                string.Empty,
                HistogramBucket: new("write", 2000, 4));
        }
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private TemporaryDatabase(string directory, WinPoolSqliteStore store)
        {
            Directory = directory;
            Store = store;
        }

        public string Directory { get; }
        public WinPoolSqliteStore Store { get; }

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "WinPool.Persistence.Execution.Tests",
                Guid.NewGuid().ToString("N"));
            var store = new WinPoolSqliteStore(Path.Combine(directory, "winpool.db"));
            await store.InitializeAsync();
            return new TemporaryDatabase(directory, store);
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
