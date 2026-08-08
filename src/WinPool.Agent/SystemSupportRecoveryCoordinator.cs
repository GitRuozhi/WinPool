using WinPool.Application;

namespace WinPool.Agent;

public sealed record SystemSupportRecoverySummary(
    int Restored,
    int NoLongerApplicable,
    int Failed);

public sealed class SystemSupportRecoveryCoordinator
{
    private readonly ISystemSupportRecoveryStore recovery;
    private readonly ITestProcessSchedulingPort processScheduling;
    private readonly ITemporaryPowerPlanPort powerPlans;
    private readonly ISystemSupportAuditSink audit;
    private readonly Func<int, bool> isRegisteredTestProcess;
    private readonly Func<int, bool> isProcessAlive;
    private readonly TimeProvider timeProvider;
    private SystemSupportRecoverySummary lastSummary = new(0, 0, 0);

    public SystemSupportRecoveryCoordinator(
        ISystemSupportRecoveryStore recovery,
        ITestProcessSchedulingPort processScheduling,
        ITemporaryPowerPlanPort powerPlans,
        ISystemSupportAuditSink audit,
        Func<int, bool> isRegisteredTestProcess,
        Func<int, bool> isProcessAlive,
        TimeProvider? timeProvider = null)
    {
        this.recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        this.processScheduling = processScheduling
            ?? throw new ArgumentNullException(nameof(processScheduling));
        this.powerPlans = powerPlans ?? throw new ArgumentNullException(nameof(powerPlans));
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.isRegisteredTestProcess = isRegisteredTestProcess
            ?? throw new ArgumentNullException(nameof(isRegisteredTestProcess));
        this.isProcessAlive = isProcessAlive
            ?? throw new ArgumentNullException(nameof(isProcessAlive));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SystemSupportRecoverySummary LastSummary => lastSummary;

    public async Task<SystemSupportRecoverySummary> RecoverPendingAsync(
        CancellationToken cancellationToken)
    {
        var restored = 0;
        var noLongerApplicable = 0;
        var failed = 0;
        foreach (var entry in await recovery.GetPendingAsync(cancellationToken)
                     .ConfigureAwait(false))
        {
            var correlationId = CorrelationId.New();
            try
            {
                await AuditAsync(
                    entry,
                    correlationId,
                    SystemSupportAuditStage.RecoveryStarted,
                    "system-support.recovery-started",
                    cancellationToken).ConfigureAwait(false);
                var becameNoLongerApplicable = false;
                switch (entry.State)
                {
                    case PowerPlanRecoveryState power:
                        await powerPlans.RestoreAsync(
                            power.Snapshot,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case ProcessSchedulingRecoveryState scheduling
                        when !isProcessAlive(scheduling.Snapshot.ProcessId):
                        becameNoLongerApplicable = true;
                        break;
                    case ProcessSchedulingRecoveryState scheduling
                        when isRegisteredTestProcess(scheduling.Snapshot.ProcessId):
                        await processScheduling.RestoreAsync(
                            scheduling.Snapshot,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case ProcessSchedulingRecoveryState:
                        throw new InvalidOperationException(
                            "A live process is no longer a registered WinPool test process.");
                    default:
                        throw new InvalidDataException(
                            "The recovery state kind is unsupported.");
                }

                await AuditAsync(
                    entry,
                    correlationId,
                    SystemSupportAuditStage.RecoveryCompleted,
                    "system-support.recovery-completed",
                    cancellationToken).ConfigureAwait(false);
                await recovery.RemoveAsync(entry.RecoveryId, cancellationToken)
                    .ConfigureAwait(false);
                if (becameNoLongerApplicable)
                {
                    noLongerApplicable++;
                }
                else
                {
                    restored++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                failed++;
                await TryAuditAsync(
                    entry,
                    correlationId,
                    SystemSupportAuditStage.RecoveryFailed,
                    "system-support.recovery-failed").ConfigureAwait(false);
            }
        }

        var summary = new SystemSupportRecoverySummary(
            restored,
            noLongerApplicable,
            failed);
        lastSummary = summary;
        return summary;
    }

    private ValueTask AuditAsync(
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
                $"state={entry.State.GetType().Name};stage={stage}",
                "system-support-v1"),
            cancellationToken);

    private async ValueTask TryAuditAsync(
        SystemSupportRecoveryEntry entry,
        CorrelationId correlationId,
        SystemSupportAuditStage stage,
        string code)
    {
        try
        {
            await AuditAsync(
                entry,
                correlationId,
                stage,
                code,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Recovery evidence remains pending. Audit failure must not prevent the
            // Agent from attempting the remaining independent recovery entries.
        }
    }
}
