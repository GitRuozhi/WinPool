using WinPool.Agent;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Agent.Tests;

public sealed class TestSupportActionValidationTests
{
    [Fact]
    public void AcceptsOnlyClosedRuntimeBoundSchedulingPolicy()
    {
        var valid = Plan(
            new TestProcessSchedulingPolicyAction(
                TestProcessPriority.AboveNormal,
                [0]));
        var arbitraryPid = Plan(
            new AdjustProcessSchedulingAction(
                [Environment.ProcessId],
                TestProcessPriority.High,
                [0]));
        var oneShot = Plan(new UseTemporaryPowerPlanAction(Guid.NewGuid()));
        var ramMap = Plan(
            new ClearSystemFileCacheAction(
                RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
                new(
                    new string('a', 64),
                    "1.61",
                    "Microsoft Corporation",
                    new string('b', 64),
                    true,
                    true)));

        Assert.Null(DesktopAgentRuntime.ValidateTestSupportActions(valid));
        Assert.Equal(
            "agent.testing.support_actions_require_orchestration",
            DesktopAgentRuntime.ValidateTestSupportActions(arbitraryPid));
        Assert.Null(DesktopAgentRuntime.ValidateTestSupportActions(oneShot));
        Assert.Null(DesktopAgentRuntime.ValidateTestSupportActions(ramMap));
    }

    [Fact]
    public void RejectsOutOfRangeProcessorAndRiskDowngrade()
    {
        var outOfRange = Plan(
            new TestProcessSchedulingPolicyAction(
                TestProcessPriority.AboveNormal,
                [Environment.ProcessorCount]));
        var downgraded = Plan(
            new TestProcessSchedulingPolicyAction(
                TestProcessPriority.AboveNormal,
                [0])) with
        {
            Risk = RiskLevel.R2RecoverableFileWrite
        };
        var invalidPower = Plan(new UseTemporaryPowerPlanAction(Guid.Empty));
        var invalidRamMap = Plan(
            new ClearSystemFileCacheAction(
                RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
                new(
                    new string('a', 64),
                    "1.61",
                    "Microsoft Corporation",
                    "short",
                    true,
                    true)));

        Assert.Equal(
            "agent.testing.scheduling_policy_invalid",
            DesktopAgentRuntime.ValidateTestSupportActions(outOfRange));
        Assert.Equal(
            "agent.testing.support_actions_require_orchestration",
            DesktopAgentRuntime.ValidateTestSupportActions(downgraded));
        Assert.Equal(
            "agent.testing.power_plan_invalid",
            DesktopAgentRuntime.ValidateTestSupportActions(invalidPower));
        Assert.Equal(
            "agent.testing.rammap_action_invalid",
            DesktopAgentRuntime.ValidateTestSupportActions(invalidRamMap));
    }

    [Fact]
    public void FlushRequiresMatchingGuidSnapshotAndDirectoryCopyStep()
    {
        var basePlan = Plan(new UseTemporaryPowerPlanAction(Guid.NewGuid()));
        var volume = basePlan.Target.VolumeId;
        var snapshot = new VolumeTargetSnapshot(
            volume,
            @"\\?\Volume{11111111-1111-1111-1111-111111111111}",
            Path.GetFullPath(Path.GetTempPath()));
        var copyStep = new TestStep(
            "copy",
            TestActionKind.Copy,
            new ToolId("windows.robocopy"),
            null,
            new Dictionary<string, TestParameter>
            {
                ["sourceRelativeDirectory"] = new(
                    "sourceRelativeDirectory",
                    TestParameterKind.Text,
                    "source",
                    "test.source"),
                ["destinationRelativeDirectory"] = new(
                    "destinationRelativeDirectory",
                    TestParameterKind.Text,
                    "destination",
                    "test.destination")
            },
            [],
            true);
        var valid = basePlan with
        {
            SupportActions = [new FlushVolumeAction(volume, snapshot)],
            Steps = [copyStep]
        };
        var missingSnapshot = valid with
        {
            SupportActions = [new FlushVolumeAction(volume)]
        };
        var noCopy = valid with { Steps = [] };

        Assert.Null(DesktopAgentRuntime.ValidateTestSupportActions(valid));
        Assert.Equal(
            "agent.testing.flush_action_invalid",
            DesktopAgentRuntime.ValidateTestSupportActions(missingSnapshot));
        Assert.Equal(
            "agent.testing.flush_action_invalid",
            DesktopAgentRuntime.ValidateTestSupportActions(noCopy));
    }

    private static TestPlan Plan(SystemSupportAction supportAction)
    {
        var systemId = SystemId.New();
        return new(
            TestRunId.New(),
            TestDefinitionId.New(),
            "1",
            new(
                systemId,
                new StorageObjectId(
                    systemId,
                    StorageObjectKind.Partition,
                    "test-volume"),
                Path.GetTempPath(),
                long.MaxValue,
                true),
            new(
                Path.GetTempPath(),
                Path.Combine(Path.GetTempPath(), "WinPoolRuns", Guid.NewGuid().ToString("N")),
                [],
                0,
                TestWorkspaceCleanupPolicy.KeepAll,
                DateTimeOffset.UtcNow.AddMinutes(5)),
            [],
            [supportAction],
            [],
            0,
            RiskLevel.R3ControlledSystemSupport,
            new(
                "ALG-TEST",
                "1",
                AlgorithmConfidence.Derived,
                "test"),
            DateTimeOffset.UtcNow,
            new string('a', 64));
    }
}
