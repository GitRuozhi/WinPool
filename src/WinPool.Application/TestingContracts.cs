using System.Text.Json.Serialization;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Application;

public readonly record struct TestDefinitionId(Guid Value)
{
    public static TestDefinitionId New() => new(Guid.NewGuid());
}

public readonly record struct TestTaskId(Guid Value)
{
    public static TestTaskId New() => new(Guid.NewGuid());
}

public readonly record struct TestRunId(Guid Value)
{
    public static TestRunId New() => new(Guid.NewGuid());
}

public enum TestActionKind
{
    CheckSpace,
    GenerateFile,
    RunIo,
    Copy,
    Repeat,
    Store,
    Summarize,
    Verify,
    Cleanup,
    WaitForIdle,
    CaptureHealth,
    ExportArtifact
}

public enum SoftwareCacheMode
{
    Enabled,
    Disabled
}

public enum WriteThroughMode
{
    Disabled,
    Enabled
}

public enum IoAccessPattern
{
    Sequential,
    Random,
    Mixed
}

public enum TestParameterKind
{
    Boolean,
    Integer,
    Decimal,
    Text,
    Choice
}

public sealed record TestParameter(
    string Key,
    TestParameterKind Kind,
    string SerializedValue,
    string UserTextKey);

public sealed record TestWorkload(
    long FileSizeBytes,
    int BlockSizeBytes,
    int ThreadCount,
    int QueueDepth,
    TimeSpan Warmup,
    TimeSpan Duration,
    TimeSpan Cooldown,
    IoAccessPattern AccessPattern,
    int WritePercentage,
    SoftwareCacheMode SoftwareCache,
    WriteThroughMode WriteThrough,
    bool CollectLatency);

public sealed record TestTaskDefinition(
    TestTaskId Id,
    string Name,
    TestActionKind Action,
    ToolId? RequiredTool,
    TestWorkload? Workload,
    IReadOnlyDictionary<string, TestParameter> Parameters);

public sealed record TestScheduleStep(
    string Id,
    TestTaskId TaskId,
    IReadOnlyList<string> DependsOn,
    bool IsCancellationBoundary);

public sealed record TestDefinition(
    TestDefinitionId Id,
    string Name,
    string Version,
    IReadOnlyDictionary<string, TestParameter> Parameters,
    IReadOnlyList<TestTaskDefinition> Tasks,
    IReadOnlyList<TestScheduleStep> Schedule,
    AlgorithmConfidence Confidence);

public sealed record DiteLegacyMetric(
    string MetricId,
    double Value,
    string Unit);

public sealed record DiteLegacyRun(
    string TestTime,
    string Drive,
    string Tool,
    string Profile,
    string? LogFileName,
    IReadOnlyList<DiteLegacyMetric> Metrics);

public sealed record DiteLegacyMetricSummary(
    string MetricId,
    string Unit,
    int Count,
    double Minimum,
    double Median,
    double Maximum,
    TestMetricSemantic? Semantic = null);

public sealed record DiteLegacyImportResult(
    string SourceFileName,
    string SourceSha256,
    IReadOnlyList<DiteLegacyRun> Runs,
    IReadOnlyList<DiteLegacyMetricSummary> Summaries);

public sealed record TestTarget(
    SystemId SystemId,
    StorageObjectId VolumeId,
    string TestRootDirectory,
    long AvailableBytes,
    bool IsWriteAllowed);

public sealed record RegisteredTestFile(
    string RelativePath,
    long PlannedLength,
    string IdentityToken);

public sealed record RegisteredTestDirectory(
    string RelativePath,
    long MaximumBytes,
    int MaximumFileCount,
    string IdentityToken);

public enum TestWorkspaceCleanupPolicy
{
    KeepAll,
    RemoveVerifiedRegisteredFiles,
    RemoveAllRegisteredFiles
}

public sealed record TestWorkspacePlan(
    string NormalizedRootDirectory,
    string RunDirectory,
    IReadOnlyList<RegisteredTestFile> RegisteredFiles,
    long MaximumWriteBytes,
    TestWorkspaceCleanupPolicy CleanupPolicy,
    DateTimeOffset ExpiresAtUtc)
{
    public IReadOnlyList<RegisteredTestDirectory> RegisteredDirectories { get; init; } = [];
}

public enum SystemSupportActionKind
{
    CleanTemporaryFiles,
    ClearSystemFileCache,
    FlushVolume,
    TrimOrOptimizeVolume,
    AdjustProcessScheduling,
    UseTemporaryPowerPlan
}

public enum RamMapCacheClearMode
{
    EmptySystemWorkingSetAndStandbyList
}

public sealed record RamMapToolIdentity(
    string PathBindingHash,
    string Version,
    string Publisher,
    string Sha256,
    bool SignatureTrusted,
    bool RequiresElevation = true);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$supportAction")]
[JsonDerivedType(
    typeof(ClearSystemFileCacheAction),
    typeDiscriminator: "clear-system-file-cache")]
[JsonDerivedType(typeof(FlushVolumeAction), typeDiscriminator: "flush-volume")]
[JsonDerivedType(
    typeof(TrimOrOptimizeVolumeAction),
    typeDiscriminator: "trim-or-optimize-volume")]
[JsonDerivedType(
    typeof(AdjustProcessSchedulingAction),
    typeDiscriminator: "adjust-process-scheduling")]
[JsonDerivedType(
    typeof(TestProcessSchedulingPolicyAction),
    typeDiscriminator: "test-process-scheduling-policy")]
[JsonDerivedType(
    typeof(UseTemporaryPowerPlanAction),
    typeDiscriminator: "use-temporary-power-plan")]
[JsonDerivedType(
    typeof(CleanTemporaryFilesAction),
    typeDiscriminator: "clean-temporary-files")]
public abstract record SystemSupportAction(SystemSupportActionKind Kind);

public sealed record ClearSystemFileCacheAction(
    RamMapCacheClearMode Mode,
    RamMapToolIdentity? PlannedToolIdentity = null)
    : SystemSupportAction(SystemSupportActionKind.ClearSystemFileCache);

public sealed record FlushVolumeAction(
    StorageObjectId VolumeId,
    VolumeTargetSnapshot? PlannedTarget = null)
    : SystemSupportAction(SystemSupportActionKind.FlushVolume);

public sealed record TrimOrOptimizeVolumeAction(StorageObjectId VolumeId)
    : SystemSupportAction(SystemSupportActionKind.TrimOrOptimizeVolume);

public enum TestProcessPriority
{
    Idle,
    BelowNormal,
    Normal,
    AboveNormal,
    High
}

public sealed record AdjustProcessSchedulingAction(
    IReadOnlyList<int> ProcessIds,
    TestProcessPriority Priority,
    IReadOnlyList<int> LogicalProcessorIndices)
    : SystemSupportAction(SystemSupportActionKind.AdjustProcessScheduling);

/// <summary>
/// Plan-time scheduling policy. The Agent binds this policy only to the
/// TestWorker process it has just created and registered; wire input cannot
/// nominate an arbitrary process identifier.
/// </summary>
public sealed record TestProcessSchedulingPolicyAction(
    TestProcessPriority Priority,
    IReadOnlyList<int> LogicalProcessorIndices)
    : SystemSupportAction(SystemSupportActionKind.AdjustProcessScheduling);

public sealed record UseTemporaryPowerPlanAction(Guid PowerPlanId)
    : SystemSupportAction(SystemSupportActionKind.UseTemporaryPowerPlan);

public enum TemporaryFileScope
{
    WinPoolTemporaryFiles,
    CurrentUserTemporaryFiles,
    WindowsOrdinaryTemporaryFiles
}

public sealed record CleanTemporaryFilesAction(IReadOnlyList<TemporaryFileScope> Scopes)
    : SystemSupportAction(SystemSupportActionKind.CleanTemporaryFiles);

public sealed record TestStep(
    string Id,
    TestActionKind Action,
    ToolId? ToolId,
    TestWorkload? Workload,
    IReadOnlyDictionary<string, TestParameter> Parameters,
    IReadOnlyList<string> DependsOn,
    bool IsCancellationBoundary);

public sealed record TestPlan(
    TestRunId RunId,
    TestDefinitionId DefinitionId,
    string DefinitionVersion,
    TestTarget Target,
    TestWorkspacePlan Workspace,
    IReadOnlyList<TestStep> Steps,
    IReadOnlyList<SystemSupportAction> SupportActions,
    IReadOnlyList<ToolId> RequiredTools,
    long EstimatedWriteBytes,
    RiskLevel Risk,
    AlgorithmIdentity PlannerAlgorithm,
    DateTimeOffset CreatedAtUtc,
    string PlanHash);

public sealed class AuthorizedTestWorkspace
{
    internal AuthorizedTestWorkspace(TestWorkspacePlan plan)
        : this(plan, plan.ExpiresAtUtc)
    {
    }

    internal AuthorizedTestWorkspace(
        TestWorkspacePlan plan,
        DateTimeOffset expiresAtUtc)
    {
        Plan = plan;
        ExpiresAtUtc = expiresAtUtc;
    }

    public TestWorkspacePlan Plan { get; }

    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed class AuthorizedSystemSupportAction
{
    internal AuthorizedSystemSupportAction(
        SystemSupportAction action,
        string planHash,
        DateTimeOffset expiresAtUtc)
    {
        Action = action;
        PlanHash = planHash;
        ExpiresAtUtc = expiresAtUtc;
    }

    public SystemSupportAction Action { get; }

    public string PlanHash { get; }

    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed class AuthorizedTestRun
{
    internal AuthorizedTestRun(
        TestPlan plan,
        AuthorizedTestWorkspace workspace,
        IReadOnlyList<AuthorizedSystemSupportAction> supportActions)
    {
        Plan = plan;
        Workspace = workspace;
        SupportActions = supportActions;
    }

    public TestPlan Plan { get; }

    public AuthorizedTestWorkspace Workspace { get; }

    public IReadOnlyList<AuthorizedSystemSupportAction> SupportActions { get; }
}

public enum TestEventKind
{
    StateChanged,
    Progress,
    Metric,
    Artifact,
    Diagnostic
}

public sealed record TestMetric(
    string MetricId,
    double Value,
    string Unit,
    DateTimeOffset MeasuredAtUtc);

public sealed record TestEvent(
    TestRunId RunId,
    TestEventKind Kind,
    ApplicationTaskEvent TaskEvent,
    TestMetric? Metric = null,
    string? ArtifactRelativePath = null);

public interface ITestPlanner
{
    ApplicationResult<TestPlan> Compile(
        TestDefinition definition,
        TestTarget target,
        CorrelationId correlationId);
}

public interface ITestRunAuthorizationCoordinator
{
    Task<ApplicationResult<AuthorizedTestRun>> AuthorizeAsync(
        TestPlan plan,
        CancellationToken cancellationToken);
}
