using WinPool.Application;

namespace WinPool.Agent;

internal sealed record PreparedTestPowerPlanScope(
    SystemSupportRecoveryEntry RecoveryEntry);

/// <summary>
/// Persists the active power plan before invoking the one-shot elevated Broker.
/// Both activation and restoration use the same closed SetActivePowerPlan action.
/// </summary>
internal sealed class TestPowerPlanScope
{
    private readonly ITemporaryPowerPlanPort powerPlans;
    private readonly ISystemSupportRecoveryStore recovery;
    private readonly ISystemSupportAuditSink audit;
    private readonly Func<Guid, string, CorrelationId, CancellationToken, Task>
        setActivePowerPlan;
    private readonly TimeProvider timeProvider;

    public TestPowerPlanScope(
        ITemporaryPowerPlanPort powerPlans,
        ISystemSupportRecoveryStore recovery,
        ISystemSupportAuditSink audit,
        Func<Guid, string, CorrelationId, CancellationToken, Task> setActivePowerPlan,
        TimeProvider? timeProvider = null)
    {
        this.powerPlans = powerPlans ?? throw new ArgumentNullException(nameof(powerPlans));
        this.recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.setActivePowerPlan = setActivePowerPlan
            ?? throw new ArgumentNullException(nameof(setActivePowerPlan));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PreparedTestPowerPlanScope> PrepareAsync(
        string planHash,
        Guid requestedPowerPlanId,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);
        if (requestedPowerPlanId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty power plan ID is required.",
                nameof(requestedPowerPlanId));
        }

        var snapshot = await powerPlans.CaptureActiveAsync(cancellationToken)
            .ConfigureAwait(false);
        var entry = new SystemSupportRecoveryEntry(
            Guid.NewGuid(),
            planHash,
            SystemSupportActionKind.UseTemporaryPowerPlan,
            new PowerPlanRecoveryState(snapshot),
            timeProvider.GetUtcNow());
        await recovery.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAuditAsync(
                entry,
                correlationId,
                SystemSupportAuditStage.RestorationPrepared,
                "system-support.power-restoration-prepared",
                cancellationToken).ConfigureAwait(false);
            await setActivePowerPlan(
                    requestedPowerPlanId,
                    planHash,
                    correlationId,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteAuditAsync(
                entry,
                correlationId,
                SystemSupportAuditStage.Completed,
                "system-support.power-plan-applied",
                cancellationToken).ConfigureAwait(false);
            return new PreparedTestPowerPlanScope(entry);
        }
        catch
        {
            await TryRestoreAsync(entry, correlationId).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RestoreAsync(
        PreparedTestPowerPlanScope prepared,
        CorrelationId correlationId)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var snapshot =
            ((PowerPlanRecoveryState)prepared.RecoveryEntry.State).Snapshot;
        await setActivePowerPlan(
                snapshot.PowerPlanId,
                prepared.RecoveryEntry.PlanHash,
                correlationId,
                CancellationToken.None)
            .ConfigureAwait(false);
        await WriteAuditAsync(
            prepared.RecoveryEntry,
            correlationId,
            SystemSupportAuditStage.Restored,
            "system-support.power-plan-restored",
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
                new PreparedTestPowerPlanScope(entry),
                correlationId).ConfigureAwait(false);
        }
        catch
        {
            // Keep the durable entry for startup recovery and user notification.
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
                $"target=active-power-plan;stage={stage}",
                "system-support-v1"),
            cancellationToken);
}
