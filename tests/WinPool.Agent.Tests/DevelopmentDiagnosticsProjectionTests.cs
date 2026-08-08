using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Agent.Tests;

public sealed class DevelopmentDiagnosticsProjectionTests
{
    [Fact]
    public void PlanProjectionExposesStructureButNeverParameterValues()
    {
        var runId = TestRunId.New();
        var definitionId = TestDefinitionId.New();
        var now = DateTimeOffset.UtcNow;
        var systemId = SystemId.New();
        var plan = new TestPlan(
            runId,
            definitionId,
            "1",
            new TestTarget(
                systemId,
                new StorageObjectId(systemId, StorageObjectKind.Partition, "partition:test"),
                @"C:\registered-test-root",
                1024 * 1024,
                true),
            new TestWorkspacePlan(
                @"C:\registered-test-root",
                "run",
                [],
                1024,
                TestWorkspaceCleanupPolicy.KeepAll,
                now.AddHours(1)),
            [
                new TestStep(
                    "step-1",
                    TestActionKind.RunIo,
                    new ToolId("microsoft.diskspd"),
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["target"] = new(
                            "target",
                            TestParameterKind.Text,
                            @"C:\private\do-not-display.bin",
                            "test.target")
                    },
                    [],
                    true)
            ],
            [],
            [new ToolId("microsoft.diskspd")],
            1024,
            RiskLevel.R2RecoverableFileWrite,
            new AlgorithmIdentity(
                "ALG-TEST",
                "1",
                AlgorithmConfidence.Derived,
                "unit-test"),
            now,
            new string('a', 64));
        var persisted = new PersistedTestRun(
            runId,
            definitionId,
            PersistedTestRunState.Running,
            now,
            null,
            plan.PlanHash,
            "{}");

        var projected = DevelopmentDiagnosticsProjection.ProjectPlan(
            persisted,
            plan,
            [new PersistedTestStep(
                "step-1",
                0,
                ApplicationTaskState.Running,
                new ToolId("microsoft.diskspd"))]);

        var step = Assert.Single(projected.Steps);
        Assert.Equal("Running", step.State);
        Assert.Equal(["target"], step.ParameterKeys);
        Assert.DoesNotContain("private", string.Join('|', step.ParameterKeys));
        Assert.DoesNotContain("registered-test-root", projected.ToString());
    }

    [Fact]
    public void AlgorithmCatalogIncludesSpeculativeIdentityWithoutHidingConfidence()
    {
        var algorithms = DevelopmentDiagnosticsProjection.Algorithms([]);

        Assert.Contains(algorithms, item => item.Id == "ALG-TEST-PLAN-001");
        Assert.Contains(
            algorithms,
            item => item.Id == "ALG-CAP-002"
                    && item.Confidence == AlgorithmConfidence.Speculative);
        Assert.Equal(
            algorithms.Count,
            algorithms.Select(item => (item.Id, item.Version)).Distinct().Count());
    }
}
