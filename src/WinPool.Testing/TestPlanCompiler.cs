using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Numerics;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Testing;

public sealed class TestPlanCompiler : ITestPlanner
{
    public static readonly AlgorithmIdentity Algorithm =
        new("ALG-TEST-PLAN-001", "1.3.0", AlgorithmConfidence.Derived, "docs/Archive/V0.2/04_外部工具测试监控与SQLite.md §3");

    public ApplicationResult<TestPlan> Compile(
        TestDefinition definition,
        TestTarget target,
        CorrelationId correlationId) =>
        Compile(definition, target, [], correlationId);

    public ApplicationResult<TestPlan> Compile(
        TestDefinition definition,
        TestTarget target,
        IReadOnlyList<SystemSupportAction> supportActions,
        CorrelationId correlationId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(supportActions);

        var error = Validate(definition, target);
        if (error is not null)
        {
            return ApplicationResult<TestPlan>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                new ApplicationMessage(
                    error.Value.Code,
                    error.Value.Code,
                    error.Value.Message,
                    ApplicationMessageSeverity.Error,
                [target.VolumeId]));
        }

        var supportError = ValidateSupportActions(
            supportActions,
            target,
            definition);
        if (supportError is not null)
        {
            return ApplicationResult<TestPlan>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                new ApplicationMessage(
                    supportError.Value.Code,
                    supportError.Value.Code,
                    supportError.Value.Message,
                    ApplicationMessageSeverity.Error,
                    [target.VolumeId]));
        }

        var runId = TestRunId.New();
        var root = Path.GetFullPath(target.TestRootDirectory);
        var runDirectory = Path.Combine(root, "WinPoolRuns", runId.Value.ToString("N"));
        var primaryFiles = definition.Tasks
            .Where(task => task.Action is TestActionKind.GenerateFile or TestActionKind.RunIo)
            .Where(task => !IsDirectoryOutput(task))
            .ToDictionary(
                task => task.Id,
                task => new RegisteredTestFile(
                    TargetRelativePath(runId, task.Id),
                    task.Workload!.FileSizeBytes,
                    CreateIdentityToken(runId, task.Id)));
        var primaryDirectories = definition.Tasks
            .Where(task => task.Action is TestActionKind.GenerateFile)
            .Where(IsDirectoryOutput)
            .ToDictionary(
                task => task.Id,
                task => new RegisteredTestDirectory(
                    TargetRelativeDirectory(runId, task.Id),
                    task.Workload!.FileSizeBytes,
                    task.Parameters.ContainsKey("maximumFileCount")
                        ? ParsePositiveInteger(task, "maximumFileCount")
                        : ParsePositiveInteger(task, "targetCount"),
                    CreateIdentityToken(runId, task.Id)));
        var copyFiles = new Dictionary<TestTaskId, (RegisteredTestFile Source, RegisteredTestFile Destination)>();
        var copyDirectories =
            new Dictionary<TestTaskId, (RegisteredTestDirectory Source, RegisteredTestDirectory Destination)>();
        foreach (var task in definition.Tasks.Where(item => item.Action == TestActionKind.Copy))
        {
            var sourceTaskId = ParseTaskId(task, "sourceTaskId");
            if (primaryFiles.TryGetValue(sourceTaskId, out var sourceFile))
            {
                var destination = new RegisteredTestFile(
                    CopyDestinationRelativePath(runId, task.Id, sourceFile.RelativePath),
                    sourceFile.PlannedLength,
                    CreateIdentityToken(runId, task.Id));
                copyFiles[task.Id] = (sourceFile, destination);
                continue;
            }

            if (primaryDirectories.TryGetValue(sourceTaskId, out var sourceDirectory))
            {
                var destination = new RegisteredTestDirectory(
                    CopyDestinationRelativeDirectory(runId, task.Id),
                    sourceDirectory.MaximumBytes,
                    sourceDirectory.MaximumFileCount,
                    CreateIdentityToken(runId, task.Id));
                copyDirectories[task.Id] = (sourceDirectory, destination);
                continue;
            }

            return ApplicationResult<TestPlan>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                new ApplicationMessage(
                    "test.plan.copy_source_invalid",
                    "test.plan.copy_source_invalid",
                    "A copy task must reference a registered file or directory generation task.",
                    ApplicationMessageSeverity.Error,
                    [target.VolumeId]));
        }

        var verificationFiles =
            new Dictionary<TestTaskId, (RegisteredTestFile Source, RegisteredTestFile Destination)>();
        var verificationDirectories =
            new Dictionary<TestTaskId, (RegisteredTestDirectory Source, RegisteredTestDirectory Destination)>();
        foreach (var task in definition.Tasks.Where(item => item.Action == TestActionKind.Verify))
        {
            var hasSource = task.Parameters.ContainsKey("sourceTaskId");
            var hasCopy = task.Parameters.ContainsKey("copyTaskId");
            if (!hasSource && !hasCopy)
            {
                continue;
            }

            var sourceTaskId = ParseTaskId(task, "sourceTaskId");
            var copyTaskId = ParseTaskId(task, "copyTaskId");
            if (primaryFiles.TryGetValue(sourceTaskId, out var sourceFile)
                && copyFiles.TryGetValue(copyTaskId, out var copyFile)
                && StringComparer.OrdinalIgnoreCase.Equals(
                    sourceFile.RelativePath,
                    copyFile.Source.RelativePath))
            {
                verificationFiles[task.Id] = (sourceFile, copyFile.Destination);
                continue;
            }

            if (primaryDirectories.TryGetValue(sourceTaskId, out var sourceDirectory)
                && copyDirectories.TryGetValue(copyTaskId, out var copyDirectory)
                && StringComparer.OrdinalIgnoreCase.Equals(
                    sourceDirectory.RelativePath,
                    copyDirectory.Source.RelativePath))
            {
                verificationDirectories[task.Id] =
                    (sourceDirectory, copyDirectory.Destination);
                continue;
            }

            return ApplicationResult<TestPlan>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                new ApplicationMessage(
                    "test.plan.verify_pair_invalid",
                    "test.plan.verify_pair_invalid",
                    "A pair verification task must reference the registered source and its matching Copy task.",
                    ApplicationMessageSeverity.Error,
                    [target.VolumeId]));
        }

        var registeredFiles = primaryFiles.Values
            .Concat(copyFiles.Values.Select(item => item.Destination))
            .ToArray();
        var registeredDirectories = primaryDirectories.Values
            .Concat(copyDirectories.Values.Select(item => item.Destination))
            .ToArray();
        var estimatedWriteBytes = registeredFiles.Aggregate(
            0L,
            (current, file) => checked(current + file.PlannedLength));
        estimatedWriteBytes = registeredDirectories.Aggregate(
            estimatedWriteBytes,
            (current, directory) => checked(current + directory.MaximumBytes));
        if (estimatedWriteBytes > target.AvailableBytes)
        {
            return ApplicationResult<TestPlan>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                new ApplicationMessage(
                    "test.plan.insufficient_space",
                    "test.plan.insufficient_space",
                    "The conservative planned write size exceeds available space.",
                    ApplicationMessageSeverity.Error,
                    [target.VolumeId]));
        }

        var steps = definition.Schedule.Select(schedule =>
        {
            var task = definition.Tasks.Single(item => item.Id == schedule.TaskId);
            var parameters = new Dictionary<string, TestParameter>(
                task.Parameters,
                StringComparer.Ordinal);
            if (task.Action is TestActionKind.GenerateFile
                && primaryDirectories.TryGetValue(task.Id, out var targetDirectory))
            {
                parameters["targetRelativeDirectory"] = new(
                    "targetRelativeDirectory",
                    TestParameterKind.Text,
                    targetDirectory.RelativePath,
                    "test.parameter.target_relative_directory");
            }
            else if (task.Action is TestActionKind.GenerateFile or TestActionKind.RunIo)
            {
                parameters["targetRelativePath"] = new(
                    "targetRelativePath",
                    TestParameterKind.Text,
                    TargetRelativePath(runId, task.Id),
                    "test.parameter.target_relative_path");
            }
            else if (task.Action == TestActionKind.Copy)
            {
                if (copyFiles.TryGetValue(task.Id, out var copyFile))
                {
                    parameters["sourceRelativePath"] = new(
                        "sourceRelativePath",
                        TestParameterKind.Text,
                        copyFile.Source.RelativePath,
                        "test.parameter.source_relative_path");
                    parameters["destinationRelativePath"] = new(
                        "destinationRelativePath",
                        TestParameterKind.Text,
                        copyFile.Destination.RelativePath,
                        "test.parameter.destination_relative_path");
                }
                else
                {
                    var copyDirectory = copyDirectories[task.Id];
                    parameters["sourceRelativeDirectory"] = new(
                        "sourceRelativeDirectory",
                        TestParameterKind.Text,
                        copyDirectory.Source.RelativePath,
                        "test.parameter.source_relative_directory");
                    parameters["destinationRelativeDirectory"] = new(
                        "destinationRelativeDirectory",
                        TestParameterKind.Text,
                        copyDirectory.Destination.RelativePath,
                        "test.parameter.destination_relative_directory");
                }
            }
            else if (task.Action == TestActionKind.Verify
                     && verificationFiles.TryGetValue(task.Id, out var verification))
            {
                parameters["sourceRelativePath"] = new(
                    "sourceRelativePath",
                    TestParameterKind.Text,
                    verification.Source.RelativePath,
                    "test.parameter.source_relative_path");
                parameters["destinationRelativePath"] = new(
                    "destinationRelativePath",
                    TestParameterKind.Text,
                    verification.Destination.RelativePath,
                    "test.parameter.destination_relative_path");
                parameters["relativePaths"] = new(
                    "relativePaths",
                    TestParameterKind.Text,
                    string.Join(
                        ',',
                        verification.Source.RelativePath,
                        verification.Destination.RelativePath),
                    "test.parameter.relative_paths");
            }
            else if (task.Action == TestActionKind.Verify
                     && verificationDirectories.TryGetValue(
                         task.Id,
                         out var directoryVerification))
            {
                parameters["sourceRelativeDirectory"] = new(
                    "sourceRelativeDirectory",
                    TestParameterKind.Text,
                    directoryVerification.Source.RelativePath,
                    "test.parameter.source_relative_directory");
                parameters["destinationRelativeDirectory"] = new(
                    "destinationRelativeDirectory",
                    TestParameterKind.Text,
                    directoryVerification.Destination.RelativePath,
                    "test.parameter.destination_relative_directory");
            }

            return new TestStep(
                schedule.Id,
                task.Action,
                task.RequiredTool,
                task.Workload,
                parameters,
                schedule.DependsOn,
                schedule.IsCancellationBoundary);
        }).ToArray();
        var requiredTools = definition.Tasks
            .Where(task => task.RequiredTool.HasValue)
            .Select(task => task.RequiredTool!.Value)
            .Distinct()
            .ToArray();
        var workspace = new TestWorkspacePlan(
            root,
            runDirectory,
            registeredFiles,
            estimatedWriteBytes,
            TestWorkspaceCleanupPolicy.RemoveVerifiedRegisteredFiles,
            DateTimeOffset.UtcNow.AddHours(12))
        {
            RegisteredDirectories = registeredDirectories
        };
        var createdAt = DateTimeOffset.UtcNow;
        var plan = new TestPlan(
            runId,
            definition.Id,
            definition.Version,
            target,
            workspace,
            steps,
            supportActions.ToArray(),
            requiredTools,
            estimatedWriteBytes,
            supportActions.Count > 0
                ? RiskLevel.R3ControlledSystemSupport
                : estimatedWriteBytes > 0
                    ? RiskLevel.R2RecoverableFileWrite
                    : RiskLevel.R0ReadOnly,
            Algorithm,
            createdAt,
            string.Empty);
        plan = plan with { PlanHash = ComputeHash(plan) };
        return ApplicationResult<TestPlan>.Succeeded(plan, correlationId);
    }

    public static bool HasValidHash(TestPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var supplied = Encoding.ASCII.GetBytes(plan.PlanHash);
        var expected = Encoding.ASCII.GetBytes(ComputeHash(plan));
        return supplied.Length == expected.Length
               && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static (string Code, string Message)? Validate(
        TestDefinition definition,
        TestTarget target)
    {
        if (string.IsNullOrWhiteSpace(definition.Name)
            || string.IsNullOrWhiteSpace(definition.Version)
            || definition.Tasks.Count == 0
            || definition.Schedule.Count == 0)
        {
            return ("test.plan.invalid_definition", "The test definition is incomplete.");
        }

        if (!Path.IsPathFullyQualified(target.TestRootDirectory))
        {
            return ("test.plan.root_not_absolute", "The test root must be fully qualified.");
        }

        var taskIds = definition.Tasks.Select(task => task.Id).ToHashSet();
        if (taskIds.Count != definition.Tasks.Count
            || definition.Schedule.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count()
               != definition.Schedule.Count
            || definition.Schedule.Any(step => !taskIds.Contains(step.TaskId)))
        {
            return ("test.plan.invalid_schedule", "The schedule contains duplicate or unknown tasks.");
        }

        var stepIds = definition.Schedule.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
        if (definition.Schedule.Any(step => step.DependsOn.Any(dependency => !stepIds.Contains(dependency)))
            || ContainsCycle(definition.Schedule))
        {
            return ("test.plan.invalid_dag", "The test schedule is not a valid acyclic graph.");
        }

        foreach (var task in definition.Tasks)
        {
            var requiresExternalTool = task.Action is
                TestActionKind.GenerateFile
                or TestActionKind.RunIo
                or TestActionKind.Copy;
            if (requiresExternalTool != task.RequiredTool.HasValue)
            {
                return (
                    "test.plan.invalid_tool_binding",
                    requiresExternalTool
                        ? "The file I/O action requires a registered external tool."
                        : "Coordinator-owned actions cannot carry an external tool binding.");
            }

            if (task.Action is TestActionKind.GenerateFile or TestActionKind.RunIo)
            {
                if (!target.IsWriteAllowed || task.Workload is null)
                {
                    return ("test.plan.write_not_allowed", "A writable test task has no authorized writable target.");
                }

                var workload = task.Workload;
                if (workload.FileSizeBytes <= 0
                    || workload.BlockSizeBytes <= 0
                    || !BitOperations.IsPow2((uint)workload.BlockSizeBytes)
                    || workload.ThreadCount <= 0
                    || workload.QueueDepth <= 0
                    || workload.Warmup < TimeSpan.Zero
                    || workload.Duration <= TimeSpan.Zero
                    || workload.Cooldown < TimeSpan.Zero
                    || workload.WritePercentage is < 0 or > 100)
                {
                    return ("test.plan.invalid_workload", "The workload contains invalid sizes, concurrency, duration, or write ratio.");
                }

                if (task.Parameters.TryGetValue("outputKind", out var outputKind)
                    && (task.Action is not TestActionKind.GenerateFile
                        || outputKind.Kind is not TestParameterKind.Choice
                        || outputKind.SerializedValue is not ("file" or "directory")))
                {
                    return (
                        "test.plan.output_kind_invalid",
                        "Only GenerateFile may select the typed file or directory output kind.");
                }

                if (IsDirectoryOutput(task)
                    && (!TryGetPositiveInteger(task, "targetCount", out var targetCount)
                        || targetCount > 1_000_000
                        || task.Parameters.TryGetValue("maximumFileCount", out _)
                        && (!TryGetPositiveInteger(
                                task,
                                "maximumFileCount",
                                out var maximumFileCount)
                            || maximumFileCount < targetCount
                            || maximumFileCount > 1_000_000)
                        || !task.Parameters.TryGetValue("profile", out var profile)
                        || profile.Kind is not TestParameterKind.Choice
                        || profile.SerializedValue is not ("big" or "mixed")))
                {
                    return (
                        "test.plan.directory_generation_invalid",
                        "Directory generation requires a reviewed big/mixed profile and targetCount from 1 through 1,000,000.");
                }
            }
            else if (task.Action == TestActionKind.Copy)
            {
                if (!target.IsWriteAllowed
                    || !task.Parameters.TryGetValue("sourceTaskId", out var source)
                    || source.Kind is not TestParameterKind.Text
                    || !Guid.TryParse(source.SerializedValue, out _))
                {
                    return (
                        "test.plan.copy_source_invalid",
                        "A writable copy task requires a typed sourceTaskId.");
                }
            }
            else if (task.Action == TestActionKind.Verify)
            {
                var hasSource = task.Parameters.ContainsKey("sourceTaskId");
                var hasCopy = task.Parameters.ContainsKey("copyTaskId");
                if (hasSource != hasCopy
                    || hasSource
                    && (!HasTypedTaskId(task, "sourceTaskId")
                        || !HasTypedTaskId(task, "copyTaskId")))
                {
                    return (
                        "test.plan.verify_pair_invalid",
                        "A pair verification task requires typed sourceTaskId and copyTaskId values.");
                }

                if (task.Parameters.TryGetValue("verificationMode", out var mode)
                    && (mode.Kind is not TestParameterKind.Choice
                        || !Enum.TryParse<RegisteredTestFileVerificationMode>(
                            mode.SerializedValue,
                            ignoreCase: true,
                            out var parsedMode)
                        || parsedMode is RegisteredTestFileVerificationMode.PatternReplay))
                {
                    return (
                        "test.plan.verification_mode_invalid",
                        "External copy verification supports Metadata, SampledContent, or FullHash.");
                }
            }
        }

        return null;
    }

    private static (string Code, string Message)? ValidateSupportActions(
        IReadOnlyList<SystemSupportAction> actions,
        TestTarget target,
        TestDefinition definition)
    {
        if (actions.Count > 8 ||
            actions.GroupBy(action => action.Kind).Any(group => group.Count() > 1))
        {
            return (
                "test.plan.support_actions_invalid",
                "A test plan may contain at most one action of each supported system-action kind.");
        }

        foreach (var action in actions)
        {
            var valid = action switch
            {
                ClearSystemFileCacheAction
                {
                    Mode: RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
                    PlannedToolIdentity: { SignatureTrusted: true } identity
                } => identity.Sha256.Length == 64
                     && !string.IsNullOrWhiteSpace(identity.PathBindingHash),
                FlushVolumeAction flush =>
                    flush.VolumeId == target.VolumeId
                    && flush.PlannedTarget is { } snapshot
                    && snapshot.VolumeId == flush.VolumeId
                    && !string.IsNullOrWhiteSpace(snapshot.StableIdentity)
                    && !string.IsNullOrWhiteSpace(snapshot.DisplayIdentity)
                    && definition.Tasks.Any(task =>
                        task.Action == TestActionKind.Copy
                        && task.Parameters.ContainsKey("sourceTaskId")),
                TrimOrOptimizeVolumeAction optimize =>
                    optimize.VolumeId == target.VolumeId,
                TestProcessSchedulingPolicyAction scheduling =>
                    scheduling.LogicalProcessorIndices.Count > 0
                    && scheduling.LogicalProcessorIndices.All(index => index >= 0)
                    && scheduling.LogicalProcessorIndices.Distinct().Count()
                    == scheduling.LogicalProcessorIndices.Count
                    && Enum.IsDefined(scheduling.Priority),
                UseTemporaryPowerPlanAction power => power.PowerPlanId != Guid.Empty,
                CleanTemporaryFilesAction cleanup =>
                    cleanup.Scopes.Count > 0
                    && cleanup.Scopes.Distinct().Count() == cleanup.Scopes.Count
                    && cleanup.Scopes.All(Enum.IsDefined),
                _ => false
            };
            if (!valid)
            {
                return (
                    "test.plan.support_action_invalid",
                    $"The system support action '{action.Kind}' is not valid for this test target.");
            }
        }

        return null;
    }

    private static bool ContainsCycle(IReadOnlyList<TestScheduleStep> schedule)
    {
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        var byId = schedule.ToDictionary(step => step.Id, StringComparer.Ordinal);

        bool Visit(string id)
        {
            if (states.TryGetValue(id, out var state))
            {
                return state == 1;
            }

            states[id] = 1;
            if (byId[id].DependsOn.Any(Visit))
            {
                return true;
            }

            states[id] = 2;
            return false;
        }

        return schedule.Any(step => Visit(step.Id));
    }

    private static string CreateIdentityToken(TestRunId runId, TestTaskId taskId)
    {
        var material = $"{runId.Value:N}|{taskId.Value:N}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static string TargetRelativePath(TestRunId runId, TestTaskId taskId) =>
        Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            $"{taskId.Value:N}.bin");

    private static string TargetRelativeDirectory(
        TestRunId runId,
        TestTaskId taskId) =>
        Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "generated",
            taskId.Value.ToString("N"));

    private static string CopyDestinationRelativePath(
        TestRunId runId,
        TestTaskId taskId,
        string sourceRelativePath) =>
        Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "copies",
            taskId.Value.ToString("N"),
            Path.GetFileName(sourceRelativePath));

    private static string CopyDestinationRelativeDirectory(
        TestRunId runId,
        TestTaskId taskId) =>
        Path.Combine(
            "WinPoolRuns",
            runId.Value.ToString("N"),
            "copies",
            taskId.Value.ToString("N"));

    private static TestTaskId ParseTaskId(
        TestTaskDefinition task,
        string parameterName)
    {
        if (!task.Parameters.TryGetValue(parameterName, out var parameter)
            || parameter.Kind is not TestParameterKind.Text
            || !Guid.TryParse(parameter.SerializedValue, out var value))
        {
            throw new InvalidOperationException(
                $"The validated task parameter '{parameterName}' is unavailable.");
        }

        return new TestTaskId(value);
    }

    private static bool HasTypedTaskId(
        TestTaskDefinition task,
        string parameterName) =>
        task.Parameters.TryGetValue(parameterName, out var parameter)
        && parameter.Kind is TestParameterKind.Text
        && Guid.TryParse(parameter.SerializedValue, out _);

    private static bool IsDirectoryOutput(TestTaskDefinition task) =>
        task.Parameters.TryGetValue("outputKind", out var outputKind)
        && outputKind.Kind is TestParameterKind.Choice
        && StringComparer.Ordinal.Equals(
            outputKind.SerializedValue,
            "directory");

    private static int ParsePositiveInteger(
        TestTaskDefinition task,
        string parameterName) =>
        TryGetPositiveInteger(task, parameterName, out var value)
            ? value
            : throw new InvalidOperationException(
                $"The validated task parameter '{parameterName}' is unavailable.");

    private static bool TryGetPositiveInteger(
        TestTaskDefinition task,
        string parameterName,
        out int value)
    {
        value = 0;
        return task.Parameters.TryGetValue(parameterName, out var parameter)
               && parameter.Kind is TestParameterKind.Integer
               && int.TryParse(parameter.SerializedValue, out value)
               && value > 0;
    }

    private static string ComputeHash(TestPlan plan)
    {
        var canonical = new
        {
            plan.RunId,
            plan.DefinitionId,
            plan.DefinitionVersion,
            plan.Target,
            plan.Workspace,
            plan.Steps,
            plan.SupportActions,
            plan.RequiredTools,
            plan.EstimatedWriteBytes,
            plan.Risk,
            plan.PlannerAlgorithm,
            plan.CreatedAtUtc
        };
        return Convert.ToHexString(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)))
            .ToLowerInvariant();
    }
}
