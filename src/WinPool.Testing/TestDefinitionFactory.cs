using System.Globalization;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Testing;

/// <summary>
/// Builds the closed, auditable test-definition graphs used by the desktop UI.
/// The factory owns graph shape; the UI owns only input collection and presentation.
/// </summary>
public sealed class TestDefinitionFactory
{
    private readonly ToolId toolId;
    private readonly string toolDisplayName;
    private readonly string presetDisplayName;
    private readonly TestDefinitionScenario scenario;
    private readonly int mixedFileCount;
    private readonly RegisteredTestFileVerificationMode verificationMode;

    public TestDefinitionFactory(
        ToolId toolId,
        string toolDisplayName,
        string presetDisplayName,
        TestDefinitionScenario scenario,
        int mixedFileCount,
        RegisteredTestFileVerificationMode verificationMode)
    {
        this.toolId = toolId;
        this.toolDisplayName = toolDisplayName ?? throw new ArgumentNullException(nameof(toolDisplayName));
        this.presetDisplayName = presetDisplayName ?? throw new ArgumentNullException(nameof(presetDisplayName));
        this.scenario = scenario;
        this.mixedFileCount = mixedFileCount;
        this.verificationMode = verificationMode;
    }

    public TestDefinition Build(
        TestWorkload workload,
        int repeatCount)
    {
        var definitionParameters = new Dictionary<string, TestParameter>
        {
            ["repeatCount"] = new(
                "repeatCount",
                TestParameterKind.Integer,
                repeatCount.ToString(CultureInfo.InvariantCulture),
                "test.parameter.repeat_count"),
            ["scenario"] = new(
                "scenario",
                TestParameterKind.Choice,
                scenario.ToString(),
                "test.parameter.scenario")
        };
        if (scenario is TestDefinitionScenario.IoBenchmark)
        {
            var taskId = TestTaskId.New();
            var schedule = Enumerable.Range(1, repeatCount)
                .Select(index =>
                {
                    var stepId = $"io-{index:D3}";
                    IReadOnlyList<string> dependencies = index == 1
                        ? []
                        : [$"io-{index - 1:D3}"];
                    return new TestScheduleStep(
                        stepId,
                        taskId,
                        dependencies,
                        IsCancellationBoundary: true);
                })
                .ToArray();
            return new(
                TestDefinitionId.New(),
                $"{toolDisplayName} {presetDisplayName}",
                "1.0.0",
                definitionParameters,
                [
                    new(
                        taskId,
                        "io",
                        TestActionKind.RunIo,
                        toolId,
                        workload,
                        new Dictionary<string, TestParameter>())
                ],
                schedule,
                AlgorithmConfidence.Derived);
        }

        definitionParameters["verificationMode"] = new(
            "verificationMode",
            TestParameterKind.Choice,
            verificationMode.ToString(),
            "test.parameter.verification_mode");
        if (scenario
            is TestDefinitionScenario.MixedFileCopyVerification)
        {
            return BuildMixedDirectoryDefinition(
                workload,
                repeatCount,
                verificationMode,
                definitionParameters);
        }

        var sourceTaskId = TestTaskId.New();
        var tasks = new List<TestTaskDefinition>
        {
            new(
                sourceTaskId,
                "generate-source",
                TestActionKind.GenerateFile,
                toolId,
                workload with
                {
                    Warmup = TimeSpan.Zero,
                    Cooldown = TimeSpan.Zero,
                    AccessPattern = IoAccessPattern.Sequential,
                    WritePercentage = 100
                },
                new Dictionary<string, TestParameter>())
        };
        var scheduleSteps = new List<TestScheduleStep>
        {
            new("generate-source", sourceTaskId, [], IsCancellationBoundary: true)
        };
        var previousStep = "generate-source";
        for (var index = 1; index <= repeatCount; index++)
        {
            var copyTaskId = TestTaskId.New();
            var verifyTaskId = TestTaskId.New();
            tasks.Add(
                new(
                    copyTaskId,
                    $"copy-{index:D3}",
                    TestActionKind.Copy,
                    new ToolId("windows.robocopy"),
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["sourceTaskId"] = TaskIdParameter(
                            "sourceTaskId",
                            sourceTaskId),
                        ["copyMode"] = new(
                            "copyMode",
                            TestParameterKind.Choice,
                            "default",
                            "test.parameter.copy_mode"),
                        ["threadCount"] = new(
                            "threadCount",
                            TestParameterKind.Integer,
                            workload.ThreadCount.ToString(CultureInfo.InvariantCulture),
                            "test.parameter.thread_count"),
                        ["retryCount"] = new(
                            "retryCount",
                            TestParameterKind.Integer,
                            "0",
                            "test.parameter.retry_count"),
                        ["retryWaitSeconds"] = new(
                            "retryWaitSeconds",
                            TestParameterKind.Integer,
                            "0",
                            "test.parameter.retry_wait_seconds"),
                        ["copyBatchThresholdMiB"] = new(
                            "copyBatchThresholdMiB",
                            TestParameterKind.Integer,
                            "131072",
                            "test.parameter.copy_batch_threshold_mib"),
                        ["copyBatchMaximumFiles"] = new(
                            "copyBatchMaximumFiles",
                            TestParameterKind.Integer,
                            "10000",
                            "test.parameter.copy_batch_maximum_files")
                    }));
            tasks.Add(
                new(
                    verifyTaskId,
                    $"verify-{index:D3}",
                    TestActionKind.Verify,
                    null,
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["sourceTaskId"] = TaskIdParameter(
                            "sourceTaskId",
                            sourceTaskId),
                        ["copyTaskId"] = TaskIdParameter(
                            "copyTaskId",
                            copyTaskId),
                        ["verificationMode"] = new(
                            "verificationMode",
                            TestParameterKind.Choice,
                            verificationMode.ToString(),
                            "test.parameter.verification_mode"),
                        ["sampleCount"] = new(
                            "sampleCount",
                            TestParameterKind.Integer,
                            "16",
                            "test.parameter.sample_count")
                    }));
            var copyStep = $"copy-{index:D3}";
            var verifyStep = $"verify-{index:D3}";
            scheduleSteps.Add(
                new(copyStep, copyTaskId, [previousStep], IsCancellationBoundary: true));
            scheduleSteps.Add(
                new(verifyStep, verifyTaskId, [copyStep], IsCancellationBoundary: true));
            previousStep = verifyStep;
        }

        return new(
            TestDefinitionId.New(),
            $"{toolDisplayName} → RoboCopy ({verificationMode})",
            "1.0.0",
            definitionParameters,
            tasks,
            scheduleSteps,
            AlgorithmConfidence.Derived);
    }

    private TestDefinition BuildMixedDirectoryDefinition(
        TestWorkload workload,
        int repeatCount,
        RegisteredTestFileVerificationMode verificationMode,
        Dictionary<string, TestParameter> definitionParameters)
    {
        var targetCount = checked((int)mixedFileCount);
        var totalMiB = checked((int)Math.Ceiling(
            workload.FileSizeBytes / (1024d * 1024d)));
        var maximumBytes = DiteFileGenerationBounds.CalculateMaximumBytes(
            totalMiB,
            targetCount);
        definitionParameters["targetCount"] = new(
            "targetCount",
            TestParameterKind.Integer,
            targetCount.ToString(CultureInfo.InvariantCulture),
            "test.parameter.target_count");
        definitionParameters["totalMiB"] = new(
            "totalMiB",
            TestParameterKind.Integer,
            totalMiB.ToString(CultureInfo.InvariantCulture),
            "test.parameter.total_mib");
        definitionParameters["maximumFileCount"] = new(
            "maximumFileCount",
            TestParameterKind.Integer,
            checked(targetCount + DiteFileGenerationBounds.ManifestFileCount)
                .ToString(CultureInfo.InvariantCulture),
            "test.parameter.maximum_file_count");
        var sourceTaskId = TestTaskId.New();
        var tasks = new List<TestTaskDefinition>
        {
            new(
                sourceTaskId,
                "generate-mixed-source",
                TestActionKind.GenerateFile,
                toolId,
                workload with
                {
                    FileSizeBytes = maximumBytes,
                    Warmup = TimeSpan.Zero,
                    Cooldown = TimeSpan.Zero,
                    AccessPattern = IoAccessPattern.Sequential,
                    WritePercentage = 100,
                    CollectLatency = false
                },
                new Dictionary<string, TestParameter>
                {
                    ["outputKind"] = new(
                        "outputKind",
                        TestParameterKind.Choice,
                        "directory",
                        "test.parameter.output_kind"),
                    ["profile"] = new(
                        "profile",
                        TestParameterKind.Choice,
                        "mixed",
                        "test.parameter.profile"),
                    ["totalMiB"] = new(
                        "totalMiB",
                        TestParameterKind.Integer,
                        totalMiB.ToString(CultureInfo.InvariantCulture),
                        "test.parameter.total_mib"),
                    ["targetCount"] = new(
                        "targetCount",
                        TestParameterKind.Integer,
                        targetCount.ToString(CultureInfo.InvariantCulture),
                        "test.parameter.target_count"),
                    ["maximumFileCount"] = new(
                        "maximumFileCount",
                        TestParameterKind.Integer,
                        checked(
                            targetCount
                            + DiteFileGenerationBounds.ManifestFileCount)
                            .ToString(CultureInfo.InvariantCulture),
                        "test.parameter.maximum_file_count"),
                    ["poolMiB"] = new(
                        "poolMiB",
                        TestParameterKind.Integer,
                        "64",
                        "test.parameter.pool_mib")
                })
        };
        var schedule = new List<TestScheduleStep>
        {
            new(
                "generate-mixed-source",
                sourceTaskId,
                [],
                IsCancellationBoundary: true)
        };
        var previousStep = "generate-mixed-source";
        for (var index = 1; index <= repeatCount; index++)
        {
            var copyTaskId = TestTaskId.New();
            var verifyTaskId = TestTaskId.New();
            tasks.Add(
                new(
                    copyTaskId,
                    $"copy-mixed-{index:D3}",
                    TestActionKind.Copy,
                    new ToolId("windows.robocopy"),
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["sourceTaskId"] = TaskIdParameter(
                            "sourceTaskId",
                            sourceTaskId),
                        ["copyMode"] = new(
                            "copyMode",
                            TestParameterKind.Choice,
                            "default",
                            "test.parameter.copy_mode"),
                        ["threadCount"] = new(
                            "threadCount",
                            TestParameterKind.Integer,
                            workload.ThreadCount.ToString(CultureInfo.InvariantCulture),
                            "test.parameter.thread_count"),
                        ["retryCount"] = new(
                            "retryCount",
                            TestParameterKind.Integer,
                            "0",
                            "test.parameter.retry_count"),
                        ["retryWaitSeconds"] = new(
                            "retryWaitSeconds",
                            TestParameterKind.Integer,
                            "0",
                            "test.parameter.retry_wait_seconds")
                    }));
            tasks.Add(
                new(
                    verifyTaskId,
                    $"verify-mixed-{index:D3}",
                    TestActionKind.Verify,
                    null,
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["sourceTaskId"] = TaskIdParameter(
                            "sourceTaskId",
                            sourceTaskId),
                        ["copyTaskId"] = TaskIdParameter(
                            "copyTaskId",
                            copyTaskId),
                        ["verificationMode"] = new(
                            "verificationMode",
                            TestParameterKind.Choice,
                            verificationMode.ToString(),
                            "test.parameter.verification_mode"),
                        ["sampleCount"] = new(
                            "sampleCount",
                            TestParameterKind.Integer,
                            "32",
                            "test.parameter.sample_count")
                    }));
            var copyStep = $"copy-mixed-{index:D3}";
            var verifyStep = $"verify-mixed-{index:D3}";
            schedule.Add(
                new(
                    copyStep,
                    copyTaskId,
                    [previousStep],
                    IsCancellationBoundary: true));
            schedule.Add(
                new(
                    verifyStep,
                    verifyTaskId,
                    [copyStep],
                    IsCancellationBoundary: true));
            previousStep = verifyStep;
        }

        return new(
            TestDefinitionId.New(),
            $"Dite mixed {targetCount} → RoboCopy ({verificationMode})",
            "1.0.0",
            definitionParameters,
            tasks,
            schedule,
            AlgorithmConfidence.Derived);
    }

    private static TestParameter TaskIdParameter(
        string key,
        TestTaskId taskId) =>
        new(
            key,
            TestParameterKind.Text,
            taskId.Value.ToString("D"),
            $"test.parameter.{key}");

}

public enum TestDefinitionScenario
{
    IoBenchmark,
    CopyVerification,
    MixedFileCopyVerification
}
