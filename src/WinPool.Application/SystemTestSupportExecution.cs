using System.Security.Cryptography;
using System.Text;
using WinPool.Domain;

namespace WinPool.Application;

public sealed record SystemSupportExecutionOptions(
    bool IsReleaseBuild,
    bool UserConfirmed,
    string PolicyRuleVersion)
{
    public static SystemSupportExecutionOptions Development(string policyRuleVersion = "system-support-v1") =>
        new(false, false, policyRuleVersion);

    public static SystemSupportExecutionOptions ReleaseConfirmed(string policyRuleVersion = "system-support-v1") =>
        new(true, true, policyRuleVersion);
}

public sealed record SystemSupportRuntimePolicySnapshot(
    bool IsReleaseBuild,
    string RuleVersion);

public interface ISystemSupportRuntimePolicy
{
    SystemSupportRuntimePolicySnapshot GetCurrent();
}

public readonly record struct TemporaryCleanupCandidateId(string Value);

public sealed record TemporaryCleanupCandidate(
    TemporaryCleanupCandidateId Id,
    string FullPath,
    TemporaryFileScope Scope,
    long Length,
    bool IsReparsePoint,
    bool IsWindowsResourceProtected);

public sealed record TemporaryCleanupRoots(
    string WinPoolTemporaryDirectory,
    string CurrentUserTemporaryDirectory,
    string WindowsTemporaryDirectory,
    string WindowsDirectory,
    IReadOnlyList<string> AdditionalProtectedDirectories);

public sealed record TemporaryCleanupCandidateDecision(
    TemporaryCleanupCandidate Candidate,
    bool IsAllowed,
    string Code);

public sealed record TemporaryCleanupReview(
    string PlanHash,
    IReadOnlyList<TemporaryFileScope> Scopes,
    IReadOnlyList<TemporaryCleanupCandidateDecision> Candidates,
    string CandidateSetHash,
    long ApprovedBytes,
    DateTimeOffset ReviewedAtUtc);

public enum TemporaryCleanupItemStatus
{
    Removed,
    Skipped,
    Failed
}

public sealed record TemporaryCleanupItemResult(
    TemporaryCleanupCandidateId CandidateId,
    TemporaryCleanupItemStatus Status,
    string Code);

public sealed record TemporaryCleanupPortResult(
    IReadOnlyList<TemporaryCleanupItemResult> Items);

public interface ITemporaryFileCleanupPort
{
    Task<IReadOnlyList<TemporaryCleanupCandidate>> ScanAsync(
        IReadOnlyList<TemporaryFileScope> scopes,
        CancellationToken cancellationToken);

    Task<TemporaryCleanupPortResult> CleanAsync(
        IReadOnlyList<TemporaryCleanupCandidate> approvedCandidates,
        CancellationToken cancellationToken);
}

public interface ITemporaryCleanupPathPolicy
{
    TemporaryCleanupCandidateDecision Evaluate(TemporaryCleanupCandidate candidate);
}

public sealed class TemporaryCleanupPathPolicy : ITemporaryCleanupPathPolicy
{
    private readonly TemporaryCleanupRoots _roots;
    private readonly IReadOnlyList<string> _protectedDirectories;

    public TemporaryCleanupPathPolicy(TemporaryCleanupRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = new TemporaryCleanupRoots(
            NormalizeDirectory(roots.WinPoolTemporaryDirectory),
            NormalizeDirectory(roots.CurrentUserTemporaryDirectory),
            NormalizeDirectory(roots.WindowsTemporaryDirectory),
            NormalizeDirectory(roots.WindowsDirectory),
            roots.AdditionalProtectedDirectories.Select(NormalizeDirectory).ToArray());

        _protectedDirectories = BuildProtectedDirectories(_roots);
    }

    public TemporaryCleanupCandidateDecision Evaluate(TemporaryCleanupCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(candidate.Id.Value) ||
            string.IsNullOrWhiteSpace(candidate.FullPath) ||
            candidate.Length < 0)
        {
            return Deny(candidate, "temporary-cleanup.candidate.invalid");
        }

        if (candidate.IsReparsePoint)
        {
            return Deny(candidate, "temporary-cleanup.reparse-point");
        }

        if (candidate.IsWindowsResourceProtected)
        {
            return Deny(candidate, "temporary-cleanup.windows-resource-protected");
        }

        string path;
        try
        {
            path = Path.GetFullPath(candidate.FullPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Deny(candidate, "temporary-cleanup.path.invalid");
        }

        var approvedRoot = candidate.Scope switch
        {
            TemporaryFileScope.WinPoolTemporaryFiles => _roots.WinPoolTemporaryDirectory,
            TemporaryFileScope.CurrentUserTemporaryFiles => _roots.CurrentUserTemporaryDirectory,
            TemporaryFileScope.WindowsOrdinaryTemporaryFiles => _roots.WindowsTemporaryDirectory,
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(approvedRoot) || !IsDescendant(path, approvedRoot))
        {
            return Deny(candidate, "temporary-cleanup.path.outside-scope");
        }

        if (_protectedDirectories.Any(directory => IsSameOrDescendant(path, directory)))
        {
            return Deny(candidate, "temporary-cleanup.path.protected");
        }

        return new(candidate, true, "temporary-cleanup.candidate.allowed");
    }

    private static IReadOnlyList<string> BuildProtectedDirectories(TemporaryCleanupRoots roots)
    {
        var windows = roots.WindowsDirectory;
        return new[]
            {
                Path.Combine(windows, "SoftwareDistribution"),
                Path.Combine(
                    windows,
                    "ServiceProfiles",
                    "NetworkService",
                    "AppData",
                    "Local",
                    "Microsoft",
                    "Windows",
                    "DeliveryOptimization"),
                Path.Combine(windows, "WinSxS"),
                Path.Combine(windows, "Installer"),
                Path.Combine(windows, "System32"),
                Path.Combine(windows, "Recovery")
            }
            .Concat(roots.AdditionalProtectedDirectories)
            .Select(NormalizeDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TemporaryCleanupCandidateDecision Deny(
        TemporaryCleanupCandidate candidate,
        string code) =>
        new(candidate, false, code);

    private static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsDescendant(string path, string directory) =>
        path.Length > directory.Length &&
        path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
        (path[directory.Length] == Path.DirectorySeparatorChar ||
         path[directory.Length] == Path.AltDirectorySeparatorChar);

    private static bool IsSameOrDescendant(string path, string directory) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            Path.TrimEndingDirectorySeparator(path),
            directory) ||
        IsDescendant(path, directory);
}

public sealed record RamMapCacheClearEvidence(
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string StandardOutputDiagnostic,
    string StandardErrorDiagnostic,
    string? BeforeSnapshotReference,
    string? AfterSnapshotReference,
    bool UsedElevatedBroker);

public sealed record RamMapCacheClearRequest(
    RamMapCacheClearMode Mode,
    string PlanHash,
    bool RequiresElevatedBroker);

public interface IRamMapCacheClearPort
{
    bool SupportsElevatedBroker { get; }

    Task<RamMapToolIdentity?> DetectIdentityAsync(CancellationToken cancellationToken);

    Task<RamMapCacheClearEvidence> ClearAsync(
        RamMapCacheClearRequest request,
        CancellationToken cancellationToken);
}

public sealed record VolumeTargetSnapshot(
    StorageObjectId VolumeId,
    string StableIdentity,
    string DisplayIdentity);

public sealed record VolumeMaintenanceEvidence(
    string Method,
    string OutputDiagnostic);

public interface IVolumeMaintenancePort
{
    Task<VolumeTargetSnapshot?> ResolvePlannedTargetAsync(
        StorageObjectId volumeId,
        string planHash,
        CancellationToken cancellationToken);

    Task<VolumeTargetSnapshot?> ResolveCurrentTargetAsync(
        StorageObjectId volumeId,
        CancellationToken cancellationToken);

    Task<VolumeMaintenanceEvidence> FlushAsync(
        VolumeTargetSnapshot target,
        CancellationToken cancellationToken);

    Task<VolumeMaintenanceEvidence> TrimOrOptimizeAsync(
        VolumeTargetSnapshot target,
        CancellationToken cancellationToken);
}

public sealed record TestProcessSchedulingSnapshot(
    int ProcessId,
    bool IsRegisteredTestProcess,
    TestProcessPriority Priority,
    IReadOnlyList<int> LogicalProcessorIndices);

public interface ITestProcessSchedulingPort
{
    Task<TestProcessSchedulingSnapshot?> CaptureAsync(
        int processId,
        CancellationToken cancellationToken);

    Task ApplyAsync(
        int processId,
        TestProcessPriority priority,
        IReadOnlyList<int> logicalProcessorIndices,
        CancellationToken cancellationToken);

    Task RestoreAsync(
        TestProcessSchedulingSnapshot snapshot,
        CancellationToken cancellationToken);
}

public sealed record PowerPlanSnapshot(Guid PowerPlanId);

public interface ITemporaryPowerPlanPort
{
    Task<PowerPlanSnapshot> CaptureActiveAsync(CancellationToken cancellationToken);

    Task ActivateAsync(Guid powerPlanId, CancellationToken cancellationToken);

    Task RestoreAsync(PowerPlanSnapshot snapshot, CancellationToken cancellationToken);
}

public abstract record SystemSupportRecoveryState;

public sealed record ProcessSchedulingRecoveryState(TestProcessSchedulingSnapshot Snapshot)
    : SystemSupportRecoveryState;

public sealed record PowerPlanRecoveryState(PowerPlanSnapshot Snapshot)
    : SystemSupportRecoveryState;

public sealed record SystemSupportRecoveryEntry(
    Guid RecoveryId,
    string PlanHash,
    SystemSupportActionKind ActionKind,
    SystemSupportRecoveryState State,
    DateTimeOffset PreparedAtUtc);

public interface ISystemSupportRecoveryStore
{
    Task SaveAsync(
        SystemSupportRecoveryEntry entry,
        CancellationToken cancellationToken);

    Task RemoveAsync(Guid recoveryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SystemSupportRecoveryEntry>> GetPendingAsync(
        CancellationToken cancellationToken);
}

public enum SystemSupportAuditStage
{
    PolicyDecision,
    Review,
    Started,
    Evidence,
    RestorationPrepared,
    Restored,
    Completed,
    Cancelled,
    Failed,
    Rejected,
    RecoveryStarted,
    RecoveryCompleted,
    RecoveryFailed
}

public sealed record SystemSupportAuditEvent(
    CorrelationId CorrelationId,
    string PlanHash,
    SystemSupportActionKind ActionKind,
    SystemSupportAuditStage Stage,
    DateTimeOffset OccurredAtUtc,
    string Code,
    string UserTextKey,
    string RedactedDiagnostic,
    string PolicyRuleVersion);

public interface ISystemSupportAuditSink
{
    ValueTask WriteAsync(
        SystemSupportAuditEvent auditEvent,
        CancellationToken cancellationToken);
}

public sealed record SystemSupportExecutionReport(
    SystemSupportActionKind ActionKind,
    IReadOnlyList<TemporaryCleanupItemResult> CleanupItems,
    bool ReversibleStateRestored,
    string EvidenceCode,
    SystemSupportEvidence? Evidence = null);

public abstract record SystemSupportEvidence;

public sealed record TemporaryCleanupEvidence(
    IReadOnlyList<TemporaryCleanupItemResult> Items)
    : SystemSupportEvidence;

public sealed record RamMapSystemSupportEvidence(
    RamMapToolIdentity ToolIdentity,
    RamMapCacheClearEvidence Execution)
    : SystemSupportEvidence;

public sealed record VolumeMaintenanceSystemSupportEvidence(
    string Method,
    string RedactedOutputDiagnostic)
    : SystemSupportEvidence;

public sealed record SystemSupportRecoveryReport(
    int RestoredCount,
    IReadOnlyList<Guid> FailedRecoveryIds);

public sealed record SystemTestSupportPorts(
    ISystemSupportRuntimePolicy RuntimePolicy,
    ITemporaryFileCleanupPort TemporaryFiles,
    ITemporaryCleanupPathPolicy TemporaryPathPolicy,
    IRamMapCacheClearPort RamMap,
    IVolumeMaintenancePort Volumes,
    ITestProcessSchedulingPort ProcessScheduling,
    ITemporaryPowerPlanPort PowerPlans,
    ISystemSupportRecoveryStore Recovery,
    ISystemSupportAuditSink Audit);

public sealed class SystemTestSupportExecutor
{
    private static readonly IReadOnlyList<string> RamMapArguments =
        Array.AsReadOnly(["-Es", "-Et"]);

    private readonly SystemTestSupportPorts _ports;
    private readonly TimeProvider _timeProvider;

    public SystemTestSupportExecutor(
        SystemTestSupportPorts ports,
        TimeProvider? timeProvider = null)
    {
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApplicationResult<TemporaryCleanupReview>> ReviewTemporaryCleanupAsync(
        AuthorizedSystemSupportAction authorization,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(
            authorization,
            SystemSupportActionKind.CleanTemporaryFiles,
            options,
            correlationId,
            cancellationToken).ConfigureAwait(false);
        if (validation is not null)
        {
            return ApplicationResult<TemporaryCleanupReview>.FromStatus(
                validation.Status,
                correlationId,
                validation.Messages.ToArray());
        }

        var action = (CleanTemporaryFilesAction)authorization.Action;
        if (action.Scopes.Count == 0 ||
            action.Scopes.Distinct().Count() != action.Scopes.Count)
        {
            return await RejectAsync<TemporaryCleanupReview>(
                authorization,
                options,
                correlationId,
                "system-support.temporary-cleanup.scopes-invalid",
                cancellationToken).ConfigureAwait(false);
        }

        var candidates = await _ports.TemporaryFiles
            .ScanAsync(action.Scopes, cancellationToken)
            .ConfigureAwait(false);
        var decisions = EvaluateTemporaryCandidates(action.Scopes, candidates);
        var review = new TemporaryCleanupReview(
            authorization.PlanHash,
            action.Scopes.ToArray(),
            decisions,
            ComputeCandidateSetHash(decisions),
            decisions.Where(item => item.IsAllowed).Sum(item => item.Candidate.Length),
            _timeProvider.GetUtcNow());

        await AuditAsync(
            authorization,
            options,
            correlationId,
            SystemSupportAuditStage.Review,
            "system-support.temporary-cleanup.reviewed",
            $"approved={decisions.Count(item => item.IsAllowed)};excluded={decisions.Count(item => !item.IsAllowed)}",
            cancellationToken).ConfigureAwait(false);

        return ApplicationResult<TemporaryCleanupReview>.Succeeded(review, correlationId);
    }

    public async Task<ApplicationResult<SystemSupportExecutionReport>> ExecuteTemporaryCleanupAsync(
        AuthorizedSystemSupportAction authorization,
        TemporaryCleanupReview approvedReview,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvedReview);
        var validation = await ValidateAsync(
            authorization,
            SystemSupportActionKind.CleanTemporaryFiles,
            options,
            correlationId,
            cancellationToken).ConfigureAwait(false);
        if (validation is not null)
        {
            return validation;
        }

        var action = (CleanTemporaryFilesAction)authorization.Action;
        if (!StringComparer.Ordinal.Equals(authorization.PlanHash, approvedReview.PlanHash) ||
            !action.Scopes.SequenceEqual(approvedReview.Scopes))
        {
            return await RejectAsync<SystemSupportExecutionReport>(
                authorization,
                options,
                correlationId,
                "system-support.temporary-cleanup.review-mismatch",
                cancellationToken).ConfigureAwait(false);
        }

        var current = EvaluateTemporaryCandidates(
            action.Scopes,
            await _ports.TemporaryFiles
                .ScanAsync(action.Scopes, cancellationToken)
                .ConfigureAwait(false));
        if (!StringComparer.Ordinal.Equals(
                approvedReview.CandidateSetHash,
                ComputeCandidateSetHash(current)))
        {
            return await RejectAsync<SystemSupportExecutionReport>(
                authorization,
                options,
                correlationId,
                "system-support.temporary-cleanup.candidates-changed",
                cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteOneShotAsync(
            authorization,
            options,
            correlationId,
            async token =>
            {
                var approved = current
                    .Where(item => item.IsAllowed)
                    .Select(item => item.Candidate)
                    .ToArray();
                var portResult = await _ports.TemporaryFiles
                    .CleanAsync(approved, token)
                    .ConfigureAwait(false);
                var excluded = current
                    .Where(item => !item.IsAllowed)
                    .Select(item => new TemporaryCleanupItemResult(
                        item.Candidate.Id,
                        TemporaryCleanupItemStatus.Skipped,
                        item.Code));
                var items = portResult.Items.Concat(excluded).ToArray();
                return new SystemSupportExecutionReport(
                    authorization.Action.Kind,
                    items,
                    false,
                    "system-support.temporary-cleanup.evidence",
                    new TemporaryCleanupEvidence(items));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationResult<SystemSupportExecutionReport>> ExecuteAsync(
        AuthorizedSystemSupportAction authorization,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(
            authorization,
            authorization.Action.Kind,
            options,
            correlationId,
            cancellationToken).ConfigureAwait(false);
        if (validation is not null)
        {
            return validation;
        }

        return authorization.Action switch
        {
            ClearSystemFileCacheAction clear => await ExecuteRamMapAsync(
                authorization,
                clear,
                options,
                correlationId,
                cancellationToken).ConfigureAwait(false),
            FlushVolumeAction flush => await ExecuteVolumeAsync(
                authorization,
                flush.VolumeId,
                false,
                options,
                correlationId,
                cancellationToken).ConfigureAwait(false),
            TrimOrOptimizeVolumeAction optimize => await ExecuteVolumeAsync(
                authorization,
                optimize.VolumeId,
                true,
                options,
                correlationId,
                cancellationToken).ConfigureAwait(false),
            CleanTemporaryFilesAction => await RejectAsync<SystemSupportExecutionReport>(
                authorization,
                options,
                correlationId,
                "system-support.temporary-cleanup.review-required",
                cancellationToken).ConfigureAwait(false),
            AdjustProcessSchedulingAction or UseTemporaryPowerPlanAction =>
                await RejectAsync<SystemSupportExecutionReport>(
                    authorization,
                    options,
                    correlationId,
                    "system-support.reversible-scope-required",
                    cancellationToken).ConfigureAwait(false),
            _ => await RejectAsync<SystemSupportExecutionReport>(
                authorization,
                options,
                correlationId,
                "system-support.action.unsupported",
                cancellationToken).ConfigureAwait(false)
        };
    }

    public async Task<ApplicationResult<SystemSupportExecutionReport>> ExecuteScopedAsync(
        AuthorizedSystemSupportAction authorization,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        Func<CancellationToken, Task> scopedWork,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopedWork);
        var validation = await ValidateAsync(
            authorization,
            authorization.Action.Kind,
            options,
            correlationId,
            cancellationToken).ConfigureAwait(false);
        if (validation is not null)
        {
            return validation;
        }

        return authorization.Action switch
        {
            AdjustProcessSchedulingAction scheduling => await ExecuteSchedulingScopeAsync(
                authorization,
                scheduling,
                options,
                correlationId,
                scopedWork,
                cancellationToken).ConfigureAwait(false),
            UseTemporaryPowerPlanAction power => await ExecutePowerScopeAsync(
                authorization,
                power,
                options,
                correlationId,
                scopedWork,
                cancellationToken).ConfigureAwait(false),
            _ => await RejectAsync<SystemSupportExecutionReport>(
                authorization,
                options,
                correlationId,
                "system-support.action.not-reversible",
                cancellationToken).ConfigureAwait(false)
        };
    }

    public async Task<ApplicationResult<SystemSupportRecoveryReport>> RecoverPendingAsync(
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var pending = await _ports.Recovery.GetPendingAsync(cancellationToken).ConfigureAwait(false);
        var restored = 0;
        var failed = new List<Guid>();
        foreach (var entry in pending.OrderByDescending(item => item.PreparedAtUtc))
        {
            try
            {
                await AuditRecoveryAsync(
                    entry,
                    options,
                    correlationId,
                    SystemSupportAuditStage.RecoveryStarted,
                    "system-support.recovery.started",
                    CancellationToken.None).ConfigureAwait(false);
                await RestoreEntryAsync(entry, CancellationToken.None).ConfigureAwait(false);
                await AuditRecoveryAsync(
                    entry,
                    options,
                    correlationId,
                    SystemSupportAuditStage.RecoveryCompleted,
                    "system-support.recovery.completed",
                    CancellationToken.None).ConfigureAwait(false);
                await _ports.Recovery.RemoveAsync(
                    entry.RecoveryId,
                    CancellationToken.None).ConfigureAwait(false);
                restored++;
            }
            catch (Exception)
            {
                failed.Add(entry.RecoveryId);
                await TryAuditRecoveryAsync(
                    entry,
                    options,
                    correlationId,
                    SystemSupportAuditStage.RecoveryFailed,
                    "system-support.recovery.failed").ConfigureAwait(false);
            }
        }

        var report = new SystemSupportRecoveryReport(restored, failed);
        if (failed.Count == 0)
        {
            return ApplicationResult<SystemSupportRecoveryReport>.Succeeded(report, correlationId);
        }

        return new(
            ApplicationStatus.PartiallyCompleted,
            report,
            [
                Message(
                    "system-support.recovery.incomplete",
                    ApplicationMessageSeverity.Warning)
            ],
            correlationId);
    }

    private async Task<ApplicationResult<SystemSupportExecutionReport>> ExecuteRamMapAsync(
        AuthorizedSystemSupportAction authorization,
        ClearSystemFileCacheAction action,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        if (action.Mode != RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList ||
            action.PlannedToolIdentity is null)
        {
            return await RejectAsync<SystemSupportExecutionReport>(
                authorization,
                options,
                correlationId,
                "system-support.rammap.plan-invalid",
                cancellationToken).ConfigureAwait(false);
        }

        var current = await _ports.RamMap.DetectIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return await RejectAsync<SystemSupportExecutionReport>(
                authorization,
                options,
                correlationId,
                "system-support.rammap.missing",
                cancellationToken).ConfigureAwait(false);
        }

        if (!IsSameRamMapIdentity(action.PlannedToolIdentity, current) ||
            (current.RequiresElevation && !_ports.RamMap.SupportsElevatedBroker))
        {
            return await RejectAsync<SystemSupportExecutionReport>(
                authorization,
                options,
                correlationId,
                !IsSameRamMapIdentity(action.PlannedToolIdentity, current)
                    ? "system-support.rammap.identity-changed"
                    : "system-support.rammap.elevated-broker-unavailable",
                cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteOneShotAsync(
            authorization,
            options,
            correlationId,
            async token =>
            {
                var evidence = await _ports.RamMap.ClearAsync(
                        new RamMapCacheClearRequest(
                            action.Mode,
                            authorization.PlanHash,
                            current.RequiresElevation),
                        token)
                    .ConfigureAwait(false);
                if (!evidence.Arguments.SequenceEqual(RamMapArguments) ||
                    evidence.ExitCode != 0 ||
                    (current.RequiresElevation && !evidence.UsedElevatedBroker))
                {
                    throw new InvalidOperationException("RAMMap did not produce valid fixed-mode evidence.");
                }

                return new SystemSupportExecutionReport(
                    action.Kind,
                    [],
                    false,
                    "system-support.rammap.evidence-complete",
                    new RamMapSystemSupportEvidence(current, evidence));
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApplicationResult<SystemSupportExecutionReport>> ExecuteVolumeAsync(
        AuthorizedSystemSupportAction authorization,
        StorageObjectId volumeId,
        bool optimize,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var planned = await _ports.Volumes
            .ResolvePlannedTargetAsync(volumeId, authorization.PlanHash, cancellationToken)
            .ConfigureAwait(false);
        var current = await _ports.Volumes
            .ResolveCurrentTargetAsync(volumeId, cancellationToken)
            .ConfigureAwait(false);
        if (planned is null || current is null ||
            planned.VolumeId != volumeId ||
            current.VolumeId != volumeId ||
            string.IsNullOrWhiteSpace(planned.StableIdentity) ||
            !StringComparer.Ordinal.Equals(planned.StableIdentity, current.StableIdentity))
        {
            return await RejectAsync<SystemSupportExecutionReport>(
                authorization,
                options,
                correlationId,
                "system-support.volume.target-changed",
                cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteOneShotAsync(
            authorization,
            options,
            correlationId,
            async token =>
            {
                var evidence = optimize
                    ? await _ports.Volumes.TrimOrOptimizeAsync(current, token).ConfigureAwait(false)
                    : await _ports.Volumes.FlushAsync(current, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(evidence.Method))
                {
                    throw new InvalidOperationException("Volume maintenance evidence did not identify the method.");
                }

                return new SystemSupportExecutionReport(
                    authorization.Action.Kind,
                    [],
                    false,
                    optimize
                        ? "system-support.volume.optimize-evidence"
                        : "system-support.volume.flush-evidence",
                    new VolumeMaintenanceSystemSupportEvidence(
                        evidence.Method,
                        evidence.OutputDiagnostic));
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApplicationResult<SystemSupportExecutionReport>> ExecuteSchedulingScopeAsync(
        AuthorizedSystemSupportAction authorization,
        AdjustProcessSchedulingAction action,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        Func<CancellationToken, Task> scopedWork,
        CancellationToken cancellationToken)
    {
        if (action.ProcessIds.Count == 0 ||
            action.ProcessIds.Any(processId => processId <= 0) ||
            action.ProcessIds.Distinct().Count() != action.ProcessIds.Count ||
            action.LogicalProcessorIndices.Count == 0 ||
            action.LogicalProcessorIndices.Any(index => index < 0) ||
            action.LogicalProcessorIndices.Distinct().Count() != action.LogicalProcessorIndices.Count)
        {
            return await RejectAsync<SystemSupportExecutionReport>(
                authorization,
                options,
                correlationId,
                "system-support.scheduling.plan-invalid",
                cancellationToken).ConfigureAwait(false);
        }

        var snapshots = new List<TestProcessSchedulingSnapshot>();
        foreach (var processId in action.ProcessIds)
        {
            var snapshot = await _ports.ProcessScheduling
                .CaptureAsync(processId, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null ||
                snapshot.ProcessId != processId ||
                !snapshot.IsRegisteredTestProcess)
            {
                return await RejectAsync<SystemSupportExecutionReport>(
                    authorization,
                    options,
                    correlationId,
                    "system-support.scheduling.process-not-registered",
                    cancellationToken).ConfigureAwait(false);
            }

            snapshots.Add(snapshot);
        }

        var recoveryEntries = snapshots
            .Select(snapshot => new SystemSupportRecoveryEntry(
                Guid.NewGuid(),
                authorization.PlanHash,
                action.Kind,
                new ProcessSchedulingRecoveryState(snapshot),
                _timeProvider.GetUtcNow()))
            .ToArray();

        return await ExecuteReversibleScopeAsync(
            authorization,
            options,
            correlationId,
            recoveryEntries,
            async token =>
            {
                foreach (var snapshot in snapshots)
                {
                    await _ports.ProcessScheduling.ApplyAsync(
                        snapshot.ProcessId,
                        action.Priority,
                        action.LogicalProcessorIndices,
                        token).ConfigureAwait(false);
                }
            },
            scopedWork,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApplicationResult<SystemSupportExecutionReport>> ExecutePowerScopeAsync(
        AuthorizedSystemSupportAction authorization,
        UseTemporaryPowerPlanAction action,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        Func<CancellationToken, Task> scopedWork,
        CancellationToken cancellationToken)
    {
        if (action.PowerPlanId == Guid.Empty)
        {
            return await RejectAsync<SystemSupportExecutionReport>(
                authorization,
                options,
                correlationId,
                "system-support.power-plan.invalid",
                cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await _ports.PowerPlans
            .CaptureActiveAsync(cancellationToken)
            .ConfigureAwait(false);
        var entry = new SystemSupportRecoveryEntry(
            Guid.NewGuid(),
            authorization.PlanHash,
            action.Kind,
            new PowerPlanRecoveryState(snapshot),
            _timeProvider.GetUtcNow());

        return await ExecuteReversibleScopeAsync(
            authorization,
            options,
            correlationId,
            [entry],
            token => _ports.PowerPlans.ActivateAsync(action.PowerPlanId, token),
            scopedWork,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApplicationResult<SystemSupportExecutionReport>> ExecuteReversibleScopeAsync(
        AuthorizedSystemSupportAction authorization,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        IReadOnlyList<SystemSupportRecoveryEntry> recoveryEntries,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task> scopedWork,
        CancellationToken cancellationToken)
    {
        try
        {
            await AuditAsync(
                authorization,
                options,
                correlationId,
                SystemSupportAuditStage.Started,
                "system-support.started",
                string.Empty,
                cancellationToken).ConfigureAwait(false);
            foreach (var entry in recoveryEntries)
            {
                await _ports.Recovery.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
            }

            await AuditAsync(
                authorization,
                options,
                correlationId,
                SystemSupportAuditStage.RestorationPrepared,
                "system-support.restoration.prepared",
                $"count={recoveryEntries.Count}",
                cancellationToken).ConfigureAwait(false);
            await apply(cancellationToken).ConfigureAwait(false);
            await scopedWork(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var restored = await RestorePreparedAsync(
                authorization,
                options,
                correlationId,
                recoveryEntries).ConfigureAwait(false);
            await AuditAsync(
                authorization,
                options,
                correlationId,
                SystemSupportAuditStage.Cancelled,
                "system-support.cancelled",
                $"restored={restored}",
                CancellationToken.None).ConfigureAwait(false);
            return Result(
                restored ? ApplicationStatus.Cancelled : ApplicationStatus.Failed,
                authorization,
                correlationId,
                restored,
                restored
                    ? "system-support.cancelled"
                    : "system-support.restore-failed");
        }
        catch (Exception)
        {
            var restored = await RestorePreparedAsync(
                authorization,
                options,
                correlationId,
                recoveryEntries).ConfigureAwait(false);
            await AuditAsync(
                authorization,
                options,
                correlationId,
                SystemSupportAuditStage.Failed,
                "system-support.failed",
                $"restored={restored}",
                CancellationToken.None).ConfigureAwait(false);
            return Result(
                ApplicationStatus.Failed,
                authorization,
                correlationId,
                restored,
                restored
                    ? "system-support.failed"
                    : "system-support.restore-failed");
        }

        var successRestored = await RestorePreparedAsync(
            authorization,
            options,
            correlationId,
            recoveryEntries).ConfigureAwait(false);
        await AuditAsync(
            authorization,
            options,
            correlationId,
            successRestored
                ? SystemSupportAuditStage.Completed
                : SystemSupportAuditStage.Failed,
            successRestored
                ? "system-support.completed"
                : "system-support.restore-failed",
            $"restored={successRestored}",
            CancellationToken.None).ConfigureAwait(false);
        return Result(
            successRestored ? ApplicationStatus.Succeeded : ApplicationStatus.Failed,
            authorization,
            correlationId,
            successRestored,
            successRestored
                ? "system-support.completed"
                : "system-support.restore-failed");
    }

    private async Task<bool> RestorePreparedAsync(
        AuthorizedSystemSupportAction authorization,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        IReadOnlyList<SystemSupportRecoveryEntry> entries)
    {
        var restored = true;
        foreach (var entry in entries.Reverse())
        {
            try
            {
                await RestoreEntryAsync(entry, CancellationToken.None).ConfigureAwait(false);
                await AuditAsync(
                    authorization,
                    options,
                    correlationId,
                    SystemSupportAuditStage.Restored,
                    "system-support.restored",
                    string.Empty,
                    CancellationToken.None).ConfigureAwait(false);
                await _ports.Recovery.RemoveAsync(
                    entry.RecoveryId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                restored = false;
            }
        }

        return restored;
    }

    private async ValueTask TryAuditRecoveryAsync(
        SystemSupportRecoveryEntry entry,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        SystemSupportAuditStage stage,
        string code)
    {
        try
        {
            await AuditRecoveryAsync(
                entry,
                options,
                correlationId,
                stage,
                code,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Keep the recovery entry pending. One audit storage failure must not
            // prevent independent recovery entries from being attempted.
        }
    }

    private Task RestoreEntryAsync(
        SystemSupportRecoveryEntry entry,
        CancellationToken cancellationToken) =>
        entry.State switch
        {
            ProcessSchedulingRecoveryState process =>
                _ports.ProcessScheduling.RestoreAsync(process.Snapshot, cancellationToken),
            PowerPlanRecoveryState power =>
                _ports.PowerPlans.RestoreAsync(power.Snapshot, cancellationToken),
            _ => throw new InvalidOperationException("The recovery entry contains an unsupported state.")
        };

    private async Task<ApplicationResult<SystemSupportExecutionReport>> ExecuteOneShotAsync(
        AuthorizedSystemSupportAction authorization,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        Func<CancellationToken, Task<SystemSupportExecutionReport>> execute,
        CancellationToken cancellationToken)
    {
        try
        {
            await AuditAsync(
                authorization,
                options,
                correlationId,
                SystemSupportAuditStage.Started,
                "system-support.started",
                string.Empty,
                cancellationToken).ConfigureAwait(false);
            var report = await execute(cancellationToken).ConfigureAwait(false);
            await AuditAsync(
                authorization,
                options,
                correlationId,
                SystemSupportAuditStage.Evidence,
                report.EvidenceCode,
                string.Empty,
                cancellationToken).ConfigureAwait(false);
            await AuditAsync(
                authorization,
                options,
                correlationId,
                SystemSupportAuditStage.Completed,
                "system-support.completed",
                string.Empty,
                cancellationToken).ConfigureAwait(false);
            return ApplicationResult<SystemSupportExecutionReport>.Succeeded(report, correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AuditAsync(
                authorization,
                options,
                correlationId,
                SystemSupportAuditStage.Cancelled,
                "system-support.cancelled",
                string.Empty,
                CancellationToken.None).ConfigureAwait(false);
            return Result(
                ApplicationStatus.Cancelled,
                authorization,
                correlationId,
                false,
                "system-support.cancelled");
        }
        catch (Exception)
        {
            await AuditAsync(
                authorization,
                options,
                correlationId,
                SystemSupportAuditStage.Failed,
                "system-support.failed",
                string.Empty,
                CancellationToken.None).ConfigureAwait(false);
            return Result(
                ApplicationStatus.Failed,
                authorization,
                correlationId,
                false,
                "system-support.failed");
        }
    }

    private async Task<ApplicationResult<SystemSupportExecutionReport>?> ValidateAsync(
        AuthorizedSystemSupportAction authorization,
        SystemSupportActionKind expectedKind,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        string? code = null;
        if (authorization.Action.Kind != expectedKind)
        {
            code = "system-support.authorization.action-mismatch";
        }
        else if (string.IsNullOrWhiteSpace(authorization.PlanHash))
        {
            code = "system-support.authorization.plan-hash-missing";
        }
        else if (authorization.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            code = "system-support.authorization.expired";
        }
        else if (string.IsNullOrWhiteSpace(options.PolicyRuleVersion))
        {
            code = "system-support.policy-version.missing";
        }
        else
        {
            var runtimePolicy = _ports.RuntimePolicy.GetCurrent();
            if (runtimePolicy.IsReleaseBuild != options.IsReleaseBuild ||
                !StringComparer.Ordinal.Equals(
                    runtimePolicy.RuleVersion,
                    options.PolicyRuleVersion))
            {
                code = "system-support.runtime-policy-mismatch";
            }
            else if (runtimePolicy.IsReleaseBuild && !options.UserConfirmed)
            {
                code = "system-support.release-confirmation-required";
            }
        }

        if (code is not null)
        {
            return await RejectAsync<SystemSupportExecutionReport>(
                authorization,
                options,
                correlationId,
                code,
                cancellationToken).ConfigureAwait(false);
        }

        await AuditAsync(
            authorization,
            options,
            correlationId,
            SystemSupportAuditStage.PolicyDecision,
            options.IsReleaseBuild
                ? "system-support.policy.release-confirmed"
                : "system-support.policy.development-allowed",
            string.Empty,
            cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task<ApplicationResult<T>> RejectAsync<T>(
        AuthorizedSystemSupportAction authorization,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        string code,
        CancellationToken cancellationToken)
    {
        await AuditAsync(
            authorization,
            options,
            correlationId,
            SystemSupportAuditStage.Rejected,
            code,
            string.Empty,
            cancellationToken).ConfigureAwait(false);
        return ApplicationResult<T>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            Message(code, ApplicationMessageSeverity.Warning));
    }

    private async ValueTask AuditAsync(
        AuthorizedSystemSupportAction authorization,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        SystemSupportAuditStage stage,
        string code,
        string redactedDiagnostic,
        CancellationToken cancellationToken)
    {
        await _ports.Audit.WriteAsync(
            new SystemSupportAuditEvent(
                correlationId,
                authorization.PlanHash,
                authorization.Action.Kind,
                stage,
                _timeProvider.GetUtcNow(),
                code,
                code,
                redactedDiagnostic,
                options.PolicyRuleVersion),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AuditRecoveryAsync(
        SystemSupportRecoveryEntry entry,
        SystemSupportExecutionOptions options,
        CorrelationId correlationId,
        SystemSupportAuditStage stage,
        string code,
        CancellationToken cancellationToken)
    {
        await _ports.Audit.WriteAsync(
            new SystemSupportAuditEvent(
                correlationId,
                entry.PlanHash,
                entry.ActionKind,
                stage,
                _timeProvider.GetUtcNow(),
                code,
                code,
                string.Empty,
                options.PolicyRuleVersion),
            cancellationToken).ConfigureAwait(false);
    }

    private static ApplicationResult<SystemSupportExecutionReport> Result(
        ApplicationStatus status,
        AuthorizedSystemSupportAction authorization,
        CorrelationId correlationId,
        bool restored,
        string code) =>
        new(
            status,
            new SystemSupportExecutionReport(
                authorization.Action.Kind,
                [],
                restored,
                code),
            status == ApplicationStatus.Succeeded
                ? []
                : [Message(code, ApplicationMessageSeverity.Warning)],
            correlationId);

    private static ApplicationMessage Message(
        string code,
        ApplicationMessageSeverity severity) =>
        new(code, code, string.Empty, severity, []);

    private static bool IsSameRamMapIdentity(
        RamMapToolIdentity planned,
        RamMapToolIdentity current) =>
        planned.SignatureTrusted &&
        current.SignatureTrusted &&
        !string.IsNullOrWhiteSpace(planned.PathBindingHash) &&
        StringComparer.OrdinalIgnoreCase.Equals(
            planned.PathBindingHash,
            current.PathBindingHash) &&
        StringComparer.Ordinal.Equals(planned.Version, current.Version) &&
        StringComparer.Ordinal.Equals(planned.Publisher, current.Publisher) &&
        StringComparer.OrdinalIgnoreCase.Equals(planned.Sha256, current.Sha256) &&
        !string.IsNullOrWhiteSpace(current.Sha256) &&
        planned.RequiresElevation == current.RequiresElevation;

    private TemporaryCleanupCandidateDecision[] EvaluateTemporaryCandidates(
        IReadOnlyList<TemporaryFileScope> requestedScopes,
        IReadOnlyList<TemporaryCleanupCandidate> candidates)
    {
        var decisions = candidates
            .Select(candidate =>
                requestedScopes.Contains(candidate.Scope)
                    ? _ports.TemporaryPathPolicy.Evaluate(candidate)
                    : new TemporaryCleanupCandidateDecision(
                        candidate,
                        false,
                        "temporary-cleanup.scope.not-requested"))
            .ToArray();
        var duplicateIds = candidates
            .GroupBy(candidate => candidate.Id, EqualityComparer<TemporaryCleanupCandidateId>.Default)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var duplicatePaths = candidates
            .GroupBy(
                candidate => NormalizePathForHash(candidate.FullPath),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return decisions
            .Select(decision =>
                duplicateIds.Contains(decision.Candidate.Id) ||
                duplicatePaths.Contains(NormalizePathForHash(decision.Candidate.FullPath))
                    ? new TemporaryCleanupCandidateDecision(
                        decision.Candidate,
                        false,
                        "temporary-cleanup.candidate.duplicate")
                    : decision)
            .ToArray();
    }

    private static string ComputeCandidateSetHash(
        IEnumerable<TemporaryCleanupCandidateDecision> decisions)
    {
        var canonical = string.Join(
            '\n',
            decisions
                .Select(item =>
                    $"{item.Candidate.Id.Value}|{NormalizePathForHash(item.Candidate.FullPath)}|{(int)item.Candidate.Scope}|{item.Candidate.Length}|{item.Candidate.IsReparsePoint}|{item.Candidate.IsWindowsResourceProtected}|{item.IsAllowed}|{item.Code}")
                .Order(StringComparer.Ordinal));
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static string NormalizePathForHash(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
