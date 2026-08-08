using WinPool.Application;

namespace WinPool.Agent;

internal sealed record PreparedTestProcessSchedulingScope(
    SystemSupportRecoveryEntry RecoveryEntry);

/// <summary>
/// Applies a plan-time scheduling policy only after the Agent has created and
/// registered a TestWorker. The original state is persisted before mutation.
/// </summary>
internal sealed class TestProcessSchedulingScope
{
    private readonly ITestProcessSchedulingPort scheduling;
    private readonly ISystemSupportRecoveryStore recovery;
    private readonly ISystemSupportAuditSink audit;
    private readonly TimeProvider timeProvider;

    public TestProcessSchedulingScope(
        ITestProcessSchedulingPort scheduling,
        ISystemSupportRecoveryStore recovery,
        ISystemSupportAuditSink audit,
        TimeProvider? timeProvider = null)
    {
        this.scheduling = scheduling ?? throw new ArgumentNullException(nameof(scheduling));
        this.recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PreparedTestProcessSchedulingScope> PrepareAsync(
        string planHash,
        TestProcessSchedulingPolicyAction policy,
        int registeredWorkerProcessId,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(registeredWorkerProcessId);

        var snapshot = await scheduling.CaptureAsync(
                registeredWorkerProcessId,
                cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null ||
            snapshot.ProcessId != registeredWorkerProcessId ||
            !snapshot.IsRegisteredTestProcess)
        {
            throw new InvalidOperationException(
                "The scheduling target is not the registered TestWorker process.");
        }

        var entry = new SystemSupportRecoveryEntry(
            Guid.NewGuid(),
            planHash,
            SystemSupportActionKind.AdjustProcessScheduling,
            new ProcessSchedulingRecoveryState(snapshot),
            timeProvider.GetUtcNow());
        await recovery.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAuditAsync(
                entry,
                correlationId,
                SystemSupportAuditStage.RestorationPrepared,
                "system-support.scheduling-restoration-prepared",
                cancellationToken).ConfigureAwait(false);
            await scheduling.ApplyAsync(
                    registeredWorkerProcessId,
                    policy.Priority,
                    policy.LogicalProcessorIndices,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteAuditAsync(
                entry,
                correlationId,
                SystemSupportAuditStage.Completed,
                "system-support.scheduling-applied",
                cancellationToken).ConfigureAwait(false);
            return new PreparedTestProcessSchedulingScope(entry);
        }
        catch
        {
            await TryRestoreAsync(entry, correlationId).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RestoreAsync(
        PreparedTestProcessSchedulingScope prepared,
        CorrelationId correlationId)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        await scheduling.RestoreAsync(
                ((ProcessSchedulingRecoveryState)prepared.RecoveryEntry.State).Snapshot,
                CancellationToken.None)
            .ConfigureAwait(false);
        await WriteAuditAsync(
            prepared.RecoveryEntry,
            correlationId,
            SystemSupportAuditStage.Restored,
            "system-support.scheduling-restored",
            CancellationToken.None).ConfigureAwait(false);
        await recovery.RemoveAsync(
                prepared.RecoveryEntry.RecoveryId,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task TryRestoreAsync(
        SystemSupportRecoveryEntry entry,
        CorrelationId correlationId)
    {
        try
        {
            await RestoreAsync(
                new PreparedTestProcessSchedulingScope(entry),
                correlationId).ConfigureAwait(false);
        }
        catch
        {
            // The persisted recovery entry is intentionally retained.
        }
    }

    private ValueTask WriteAuditAsync(
        SystemSupportRecoveryEntry entry,
        CorrelationId correlationId,
        SystemSupportAuditStage stage,
        string code,
        CancellationToken cancellationToken) =>
        audit.WriteAsync(
            new(
                correlationId,
                entry.PlanHash,
                entry.ActionKind,
                stage,
                timeProvider.GetUtcNow(),
                code,
                code,
                $"target=registered-test-worker;stage={stage}",
                "system-support-v1"),
            cancellationToken);
}
