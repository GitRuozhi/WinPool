using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

public sealed record SimulationEditCommit(StorageSystemDocument Document, SimulationCommitIntent Intent);

/// <summary>Validates frozen edits in the background and hands the accepted local candidate to its save owner.</summary>
public sealed class SimulationEditCoordinator(
    Func<StorageSystemDocument> currentDocument,
    Func<SimulationEditCommit, CancellationToken, Task> commitDocument,
    ISimulationOperationService simulationEditor) : ISimulationEditCoordinator
{
    public Task<ApplicationResult<SimulationEditReceipt>> ExecuteAsync(
        SimulationEditRequest request, CancellationToken cancellationToken)
    {
        var document = currentDocument();
        var commitId = Guid.NewGuid().ToString("N");
        var frozen = SimulationEditIntent.Freeze(request, commitId, DateTimeOffset.UtcNow);
        if (frozen.TargetProviderKey == "primordial"
            && document.Snapshot.StoragePools.FirstOrDefault(pool => pool.IsPrimordial) is { } primordial)
            frozen = frozen with { TargetProviderKey = primordial.StableId };
        return ExecuteCoreAsync(document, new SimulationDraftPlan(commitId, [frozen]), commitId,
            singleEdit: true, cancellationToken);
    }

    public Task<ApplicationResult<SimulationEditReceipt>> ExecutePlanAsync(
        SimulationDraftPlan plan, CancellationToken cancellationToken)
    {
        var document = currentDocument();
        var commitId = Guid.NewGuid().ToString("N");
        return ExecuteCoreAsync(document,
            plan with { Steps = SimulationEditIntent.Freeze(plan.Steps, commitId, DateTimeOffset.UtcNow) },
            commitId, singleEdit: false, cancellationToken);
    }

    private async Task<ApplicationResult<SimulationEditReceipt>> ExecuteCoreAsync(
        StorageSystemDocument document, SimulationDraftPlan plan, string commitId,
        bool singleEdit, CancellationToken cancellationToken)
    {
        var correlation = CorrelationId.New();
        if (document.Kind != StorageSystemKind.Simulation)
            return Failure(ApplicationStatus.Rejected, correlation, "simulation.local-read-only", "Local storage systems are read-only.");
        if (plan.IsEmpty)
            return Failure(ApplicationStatus.Rejected, correlation, "simulation.plan-empty", "The simulation plan has no steps.");
        if ((plan.BaselineSystemId is { } system && system != document.SystemId)
            || (plan.BaselineRevision is { } revision && revision != document.Revision)
            || (plan.BaselineInventoryVersion is { } version && version != document.InventoryVersion))
            return Failure(ApplicationStatus.Rejected, correlation, "simulation.plan.revision_conflict", "The simulation changed since this plan was prepared. Refresh the draft before retrying.");
        if (plan.Steps.Any(edit => edit.Kind == SimulationEditKind.ResetDocument)
            && !document.Id.StartsWith("simulation:builtin", StringComparison.Ordinal))
            return Failure(ApplicationStatus.Rejected, correlation, "simulation.reset-built-in-only", "Only a built-in simulation document can be reset.");
        var unit = singleEdit ? document.Snapshot.FindUnit(plan.Steps[0].TargetProviderKey) : null;
        if (singleEdit && (unit is null || unit.Kind is StorageUnitKind.SyntheticStoragePool or StorageUnitKind.SyntheticStorageTier))
            return Failure(ApplicationStatus.Rejected, correlation, "simulation.target-missing", "The selected simulation target no longer exists.");

        var commitStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SimulationOperationResult applied;
            if (plan.PreviewDocument is { } preview && preview.Id == document.Id
                && preview.Revision == document.Revision + 1)
                applied = new(true, preview, string.Empty,
                    plan.DisplayItems.SelectMany(item => item.CommandPreview).ToArray());
            else
                applied = await Task.Run(() => singleEdit
                    ? simulationEditor.Apply(document, plan.Steps[0])
                    : simulationEditor.ApplyPlan(document, plan), cancellationToken);
            if (!applied.Succeeded)
                return Failure(ApplicationStatus.Rejected, correlation, "simulation.plan-rejected", applied.Error);
            var current = currentDocument();
            if (current.Id != document.Id || current.Revision != document.Revision
                || current.InventoryVersion != document.InventoryVersion)
                return Failure(ApplicationStatus.Rejected, correlation, "simulation.plan.revision_conflict", "The local document changed while this edit was being previewed. The newer content was preserved.");
            var candidate = applied.Document with { Revision = checked(document.Revision + 1) };
            // Reject a non-exportable candidate before publishing local content. The canonical
            // envelope contract is shared with import/export and the durable repository.
            await Task.Run(() => DocumentPayloadLimits.EncodeExport(candidate), cancellationToken);
            current = currentDocument();
            if (current.Id != document.Id || current.Revision != document.Revision
                || current.InventoryVersion != document.InventoryVersion)
                return Failure(ApplicationStatus.Rejected, correlation, "simulation.plan.revision_conflict", "The local document changed while this edit was being prepared. The newer content was preserved.");
            var intent = new SimulationCommitIntent(document.Id, document.Revision, commitId, plan.Steps);
            commitStarted = true;
            await commitDocument(new(candidate, intent), cancellationToken);
            var target = unit is null
                ? new StorageObjectId(document.SystemId, StorageObjectKind.System, document.Snapshot.Computer.StableId)
                : new StorageObjectId(document.SystemId, MapKind(unit.Kind), unit.StableId);
            return ApplicationResult<SimulationEditReceipt>.Succeeded(new(
                new OperationId(Guid.ParseExact(commitId, "N")), intent.IntentHash,
                document.SystemId, target, document.Revision, candidate.Revision, applied.Commands), correlation);
        }
        catch (OperationCanceledException)
        { return Failure(ApplicationStatus.Cancelled, correlation, "simulation.cancelled", "The simulation operation was cancelled."); }
        catch (SimulationCommitOutcomeUnknownException exception)
        { return Failure(ApplicationStatus.OutcomeUnknown, correlation, "simulation.commit.outcome_unknown", exception.Message); }
        catch (SimulationCommitConflictException exception)
        { return Failure(ApplicationStatus.Rejected, correlation, "simulation.commit.conflict", exception.Message); }
        catch (SimulationCommitFailureException exception)
        { return Failure(ApplicationStatus.Failed, correlation, "simulation.commit.storage_failed", exception.Message); }
        catch (InvalidDataException exception)
        { return Failure(ApplicationStatus.Rejected, correlation, "simulation.document.invalid", exception.Message); }
        catch (IOException) when (commitStarted)
        { return Failure(ApplicationStatus.OutcomeUnknown, correlation, "simulation.commit.outcome_unknown", "The save connection failed after the commit began. Reconcile the document before retrying."); }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        { return Failure(ApplicationStatus.Rejected, correlation, "simulation.invalid-model", "The simulated model contains values that cannot be safely applied."); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        { return Failure(ApplicationStatus.Failed, correlation, "simulation.execution-failed", "The simulation operation failed without changing the active document."); }
    }

    private static StorageObjectKind MapKind(StorageUnitKind kind) => kind switch
    {
        StorageUnitKind.PhysicalDisk => StorageObjectKind.PhysicalDisk,
        StorageUnitKind.StoragePool => StorageObjectKind.StoragePool,
        StorageUnitKind.StorageTier => StorageObjectKind.StorageTier,
        StorageUnitKind.VirtualDisk => StorageObjectKind.VirtualDisk,
        StorageUnitKind.OsDisk => StorageObjectKind.OsDisk,
        StorageUnitKind.Partition => StorageObjectKind.Partition,
        StorageUnitKind.Volume => StorageObjectKind.Volume,
        _ => StorageObjectKind.System
    };

    private static ApplicationResult<SimulationEditReceipt> Failure(
        ApplicationStatus status, CorrelationId correlation, string code, string message) =>
        ApplicationResult<SimulationEditReceipt>.FromStatus(status, correlation,
            new ApplicationMessage(code, message, string.Empty,
                status == ApplicationStatus.Rejected ? ApplicationMessageSeverity.Warning : ApplicationMessageSeverity.Error, []));
}
