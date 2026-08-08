using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;
using WinPool.Monitoring;
using WinPool.Testing;

namespace WinPool.Agent.Tests;

public sealed class LocalTestStepExecutorTests
{
    [Fact]
    public async Task CheckSpaceStoreAndSummarizeExecuteInTypedContext()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(database.Store, "agent-local-step-test");
        var repository = new TestRunRepository(database.Store, lease);
        var definition = CreateDefinition(database.Directory);
        var systemId = SystemId.New();
        var planResult = new TestPlanCompiler().Compile(
            definition,
            new(
                systemId,
                new StorageObjectId(
                    systemId,
                    StorageObjectKind.Partition,
                    "local-step-test-volume"),
                database.Directory,
                long.MaxValue,
                IsWriteAllowed: true),
            CorrelationId.New());
        Assert.True(planResult.IsSuccess);
        var plan = planResult.Value!;
        var authorization = await new TestRunAuthorizationCoordinator(
                (_, _) => Task.FromResult(true))
            .AuthorizeAsync(plan, CancellationToken.None);
        Assert.True(authorization.IsSuccess);
        await repository.SaveDefinitionAsync(definition, DateTimeOffset.UtcNow);
        await repository.CreateRunAsync(
            plan,
            "{}",
            PersistedTestRunState.Running);

        var executor = new LocalTestStepExecutor(
            repository,
            new MonitoringSessionCoordinator(new EmptyMonitorSource()),
            new TestArtifactStore(database.Store, lease));
        foreach (var step in plan.Steps)
        {
            await executor.ExecuteAsync(
                authorization.Value!,
                step,
                CancellationToken.None);
        }

        var steps = await repository.ListStepsAsync(plan.RunId);
        Assert.All(steps, item => Assert.Equal(ApplicationTaskState.Succeeded, item.State));
        var metrics = await repository.ListStepMetricsAsync(plan.RunId);
        Assert.Contains(
            metrics,
            item => item.StepId == "space"
                    && item.MetricId == "available_bytes"
                    && item.Value > 0);
        Assert.Contains(
            metrics,
            item => item.StepId == "store"
                    && item.MetricId == "free_alias"
                    && item.Aggregation == "stored");
        Assert.Contains(
            metrics,
            item => item.StepId == "summary"
                    && item.MetricId == "available_bytes"
                    && item.Aggregation == "mean");
        Assert.Contains(
            metrics,
            item => item.StepId == "repeat"
                    && item.MetricId == "available_bytes"
                    && item.Aggregation == "median");
        Assert.Contains(
            metrics,
            item => item.StepId == "repeat"
                    && item.MetricId == "available_bytes"
                    && item.Aggregation == "min");
        Assert.Contains(
            metrics,
            item => item.StepId == "repeat"
                    && item.MetricId == "available_bytes"
                    && item.Aggregation == "max");
        var artifacts = await new TestArtifactStore(database.Store, lease)
            .ListRunArtifactsAsync(plan.RunId);
        Assert.Contains(
            artifacts,
            item => item.MediaType == "application/json"
                    && item.ByteLength > 0);
    }

    [Fact]
    public void SupportedActionsExcludeFileMutationAndExport()
    {
        Assert.True(LocalTestStepExecutor.IsSupported(TestActionKind.CheckSpace));
        Assert.True(LocalTestStepExecutor.IsSupported(TestActionKind.Repeat));
        Assert.True(LocalTestStepExecutor.IsSupported(TestActionKind.Store));
        Assert.True(LocalTestStepExecutor.IsSupported(TestActionKind.Summarize));
        Assert.True(LocalTestStepExecutor.IsSupported(TestActionKind.WaitForIdle));
        Assert.True(LocalTestStepExecutor.IsSupported(TestActionKind.CaptureHealth));
        Assert.True(LocalTestStepExecutor.IsSupported(TestActionKind.Verify));
        Assert.True(LocalTestStepExecutor.IsSupported(TestActionKind.Cleanup));
        Assert.False(LocalTestStepExecutor.IsSupported(TestActionKind.GenerateFile));
        Assert.True(LocalTestStepExecutor.IsSupported(TestActionKind.ExportArtifact));
    }

    private static TestDefinition CreateDefinition(string root)
    {
        var checkOne = TestTaskId.New();
        var store = TestTaskId.New();
        var checkTwo = TestTaskId.New();
        var summary = TestTaskId.New();
        var repeat = TestTaskId.New();
        var export = TestTaskId.New();
        return new(
            TestDefinitionId.New(),
            "local-control",
            "1",
            new Dictionary<string, TestParameter>(),
            [
                new(
                    checkOne,
                    "space-1",
                    TestActionKind.CheckSpace,
                    null,
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["requiredBytes"] = new(
                            "requiredBytes",
                            TestParameterKind.Integer,
                            "0",
                            "test.required_bytes")
                    }),
                new(
                    store,
                    "store",
                    TestActionKind.Store,
                    null,
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["sourceStepId"] = Text("sourceStepId", "space"),
                        ["metricId"] = Text("metricId", "available_bytes"),
                        ["storeAs"] = Text("storeAs", "free_alias")
                    }),
                new(
                    checkTwo,
                    "space-2",
                    TestActionKind.CheckSpace,
                    null,
                    null,
                    new Dictionary<string, TestParameter>()),
                new(
                    summary,
                    "summary",
                    TestActionKind.Summarize,
                    null,
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["metricId"] = Text("metricId", "available_bytes"),
                        ["aggregation"] = Text("aggregation", "mean")
                    }),
                new(
                    repeat,
                    "repeat",
                    TestActionKind.Repeat,
                    null,
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["metricId"] = Text("metricId", "available_bytes")
                    }),
                new(
                    export,
                    "export",
                    TestActionKind.ExportArtifact,
                    null,
                    null,
                    new Dictionary<string, TestParameter>())
            ],
            [
                new("space", checkOne, [], true),
                new("store", store, ["space"], true),
                new("space-2", checkTwo, ["store"], true),
                new("summary", summary, ["space", "space-2"], true),
                new("repeat", repeat, ["space", "space-2"], true),
                new("export", export, ["summary", "repeat"], true)
            ],
            AlgorithmConfidence.Derived);
    }

    private static TestParameter Text(string key, string value) =>
        new(key, TestParameterKind.Text, value, $"test.{key}");

    private sealed class EmptyMonitorSource : IMonitorSource
    {
        public async IAsyncEnumerable<MonitorSample> SampleAsync(
            MonitorRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
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
                "WinPool.Agent.LocalStep.Tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var store = new WinPoolSqliteStore(Path.Combine(directory, "winpool.db"));
            await store.InitializeAsync();
            return new(directory, store);
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
