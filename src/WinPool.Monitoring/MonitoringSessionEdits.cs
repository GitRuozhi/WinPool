using System.Collections.Concurrent;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Monitoring;

public sealed partial class MonitoringSessionCoordinator
{
    private const string RecoveredEndpointUnknown = "monitor.edit.recovered_endpoint_unknown";
    private readonly IMonitorTargetIdentityResolver? targetIdentityResolver;
    private readonly SemaphoreSlim editGate = new(1, 1);
    private readonly ConcurrentDictionary<StorageObjectId, MonitorEditTargetState> editTargets = new();
    private readonly Dictionary<OperationId, string> observedOperations = new();
    private readonly HashSet<(OperationId, string)> observedCalls = [];
    private readonly HashSet<(OperationId, string)> resolvedSteps = [];
    private readonly Dictionary<(OperationId, string), int> observedStepRanks = new();
    private readonly Dictionary<(SessionId, StorageObjectId, string), MonitorEditGap> editGaps = new();
    private readonly ConcurrentQueue<(MonitorTargetAvailability Observation, MonitorEditTargetState State)> availabilityQueue = new();
    private readonly ConcurrentDictionary<(SessionId, StorageObjectId, string), bool> counterAvailability = new();
    private int queuedAvailability;
    private int drainingAvailability;
    private Task availabilityDrain = Task.CompletedTask;
    private readonly Dictionary<string, (ActiveSession Session, MonitorEditGap Gap)> pendingGapWrites = new();
    private WinPoolFacts? identityFacts;

    private IReadOnlyList<MonitorEditTargetState> SnapshotEditTargets() => editTargets.Values
        .OrderBy(x => x.TargetId.ProviderKey, StringComparer.Ordinal).ToArray();

    /// <summary>Open gaps recovered from a prior process keep their real start and unknown endpoint.</summary>
    public async Task InitializeEditRecoveryAsync(CancellationToken cancellationToken = default)
    {
        if (persistenceFactory is not IMonitorEditGapHistory history) return;
        foreach (var gap in (await history.LoadOpenEditGapsAsync(cancellationToken))
            .OrderByDescending(x => x.StartedAtUtc).ThenBy(x => x.GapId, StringComparer.Ordinal))
            editTargets.TryAdd(gap.TargetId, new(gap.SystemId, gap.TargetId, gap.OperationId, gap.StepId,
                MonitorEditTargetStatus.PendingVerification, RecoveredEndpointUnknown, gap.StartedAtUtc, gap.CounterIdentity));
    }

    private static bool HasResumableSamplingEvidence(MonitorEditTargetState state) =>
        (state.Status is MonitorEditTargetStatus.PendingVerification or MonitorEditTargetStatus.NeedsSelection)
        && state.ReasonCode is RecoveredEndpointUnknown or "monitor.edit.verified_without_active_sampler"
            or "monitor.edit.binding_verified_waiting_sample" or "monitor.edit.unchanged_waiting_sample";

    // Historical endpoints and durable verified/no-call states do not prove an
    // outstanding call. A fresh unique binding permits actual sampling, without
    // marking it restored or changing any old interval. Unknown/call-issued
    // reasons remain suspended even when the hardware identity resolves.
    private static bool CanSampleVerifiedTarget(MonitorTargetResolution resolution,
        MonitorTarget target, MonitorEditTargetState state) =>
        HasResumableSamplingEvidence(state)
        && (target.ObjectId.Kind is StorageObjectKind.PhysicalDisk or StorageObjectKind.VirtualDisk)
        && state.SystemId == resolution.SystemId && target.ObjectId.System == resolution.SystemId
        && resolution.Facts.SystemId == resolution.SystemId
        && !resolution.Facts.IsMerged && !resolution.Facts.IsSimulation && resolution.Facts.ScopedCollection is null
        && !string.IsNullOrWhiteSpace(target.ProviderIdentity) && !string.IsNullOrWhiteSpace(target.CounterIdentity)
        && target.CounterIdentity != "*"
        && resolution.Targets.Count(x => x.ObjectId == target.ObjectId
            && CounterFamily(x.CounterIdentity) == CounterFamily(target.CounterIdentity)) == 1
        && !resolution.UnresolvedTargets.Any(x => x.TargetId == target.ObjectId);

    public async Task ObserveAsync(AgentRealOperationResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Plan.RealOperation is not { } frozen || response.State is RealOperationState.Prepared or RealOperationState.Rejected)
            return;
        await editGate.WaitAsync(cancellationToken);
        try
        {
            var operation = response.Plan.OperationId;
            var signature = response.State + ":" + response.RequiresReconciliation + ":"
                + string.Join('|', response.Steps.Select(x => x.StepId + "=" + x.State));
            if (observedOperations.GetValueOrDefault(operation) == signature) return;
            // A cancelled prepared draft never opens an editing interval.
            if (response.State == RealOperationState.Cancelled && !observedOperations.ContainsKey(operation)
                && response.Steps.All(x => x.State is RealOperationStepState.Pending or RealOperationStepState.StoppedBeforeCall)) return;
            observedOperations[operation] = signature;
            var active = sessions.Values.FirstOrDefault(x => x.Request.SystemId == response.Plan.SystemId
                && x.Snapshot().State is MonitoringSessionState.Running or MonitoringSessionState.Starting);
            var now = timeProvider.GetUtcNow();
            foreach (var step in frozen.Steps)
            {
                var progress = response.Steps.SingleOrDefault(x => x.StepId == step.Id);
                if (progress is null || !AffectsSampling(step.Command)) continue;
                var key = (operation, step.Id);
                var rank = StepRank(progress.State);
                if (observedStepRanks.TryGetValue(key, out var previousRank) && rank < previousRank) continue;
                observedStepRanks[key] = rank;
                var affected = AffectedTargets(response, step.Command, active);
                var inCall = progress.State is RealOperationStepState.CallIssued or RealOperationStepState.WaitingForProvider
                    or RealOperationStepState.Verifying or RealOperationStepState.OutcomeUnknown;
                if (inCall) observedCalls.Add(key);
                var terminal = response.State is not (RealOperationState.Accepted or RealOperationState.Running);
                if (progress.State == RealOperationStepState.Verified)
                {
                    if (!resolvedSteps.Add(key)) continue;
                    if (active is not null) await ResolveVerifiedAsync(active, response, step, affected, now, cancellationToken);
                    else
                        foreach (var id in affected) SetState(id, response, step.Id,
                            IsVerifiedRemoval(step.Command, id) ? MonitorEditTargetStatus.RemovedByEdit : MonitorEditTargetStatus.PendingVerification,
                            "monitor.edit.verified_without_active_sampler", now);
                    continue;
                }
                if (terminal)
                {
                    var noEffect = (progress.State is RealOperationStepState.Pending or RealOperationStepState.StoppedBeforeCall) && !observedCalls.Contains(key)
                        || HasNoWindowsCall(progress.ResultEvidence);
                    foreach (var id in affected)
                    {
                        if (editTargets.TryGetValue(id, out var prior) && prior.Status == MonitorEditTargetStatus.RemovedByEdit) continue;
                        var wasPaused = active?.Request.Targets.Any(x => x.ObjectId == id && x.SuspendedForEdit) == true;
                        SetState(id, response, step.Id, noEffect && !wasPaused ? MonitorEditTargetStatus.Restored : MonitorEditTargetStatus.PendingVerification,
                            noEffect ? "monitor.edit.unchanged_waiting_sample" : "monitor.edit.outcome_needs_reconciliation", now);
                    }
                    if (active is not null) UpdateSuspension(active, affected, !noEffect);
                    continue;
                }
                foreach (var id in affected)
                {
                    if (editTargets.TryGetValue(id, out var prior) && prior.Status == MonitorEditTargetStatus.RemovedByEdit) continue;
                    // A later step's intent cannot relabel a target whose earlier step has entered its call boundary.
                    if (progress.State == RealOperationStepState.Pending && prior is not null
                        && prior.OperationId == operation && prior.StepId != step.Id
                        && observedStepRanks.TryGetValue((operation, prior.StepId), out var priorRank)
                        && priorRank >= StepRank(RealOperationStepState.CallIssued)) continue;
                    SetState(id, response, step.Id, MonitorEditTargetStatus.Editing,
                        inCall ? "monitor.edit.expected_change_sampling_paused" : "monitor.edit.accepted_expected_change", now);
                }
                // Accepted plans report their intent; sampling only pauses after a Windows call may have begun.
                if (active is not null && inCall) UpdateSuspension(active, affected, true);
            }
        }
        finally { editGate.Release(); }
    }

    private async Task ResolveVerifiedAsync(ActiveSession active, AgentRealOperationResponse response,
        RealOperationStep step, HashSet<StorageObjectId> affected, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var id in affected.Where(id => IsVerifiedRemoval(step.Command, id)))
        {
            SetState(id, response, step.Id, MonitorEditTargetStatus.RemovedByEdit, "monitor.edit.removed", now);
            await CloseGapsAsync(active, [id], MonitorEditTargetStatus.RemovedByEdit, "monitor.edit.removed", now, cancellationToken);
        }
        try
        {
            if (targetIdentityResolver is null || identityFacts is null) throw new InvalidOperationException("Fresh monitor rebinding is unavailable.");
            var anchors = response.Plan.Targets.Where(x => x.Kind == StorageObjectKind.PhysicalDisk).ToArray();
            if (anchors.Length == 0) throw new InvalidDataException("The frozen plan has no exact physical monitor anchor.");
            var scope = StorageInventoryScopeFactory.Create(identityFacts, response.Plan.OperationId, step.Id, anchors);
            var resolution = await targetIdentityResolver.ResolveAsync(active.Request, scope, cancellationToken);
            if (resolution.SystemId != active.Request.SystemId || resolution.Facts.IsMerged
                || resolution.Facts.ScopedCollection is not { Complete: true })
                throw new InvalidDataException("Monitor rebinding returned incomplete or merged evidence.");
            var covered = scope.BeforeObjectIds.Concat(resolution.Facts.Objects.Select(x => x.Id)).ToHashSet(StringComparer.Ordinal);
            var targets = active.Request.Targets.Where(x => !covered.Contains(x.ObjectId.ProviderKey)
                    && (!editTargets.TryGetValue(x.ObjectId, out var retainedState) || retainedState.Status != MonitorEditTargetStatus.RemovedByEdit))
                .Concat(resolution.Targets.Where(x => !editTargets.TryGetValue(x.ObjectId, out var state)
                    || state.Status != MonitorEditTargetStatus.RemovedByEdit)).ToArray();
            active.ReplaceTargets(targets);
            if (source is IRebindableMonitorSource rebindable) rebindable.SetTargets(active.Request.SessionId, targets);
            identityFacts = WinPoolFactRefresh.Merge(identityFacts, resolution.Facts);
            foreach (var id in affected)
            {
                if (editTargets.TryGetValue(id, out var old) && old.Status == MonitorEditTargetStatus.RemovedByEdit) continue;
                if (targets.Any(x => x.ObjectId == id))
                    SetState(id, response, step.Id, MonitorEditTargetStatus.PendingVerification, "monitor.edit.binding_verified_waiting_sample", now);
                else SetState(id, response, step.Id, MonitorEditTargetStatus.NeedsSelection, "monitor.edit.no_unique_current_sampling_binding", now);
            }
            foreach (var state in resolution.UnresolvedTargets)
                if (!targets.Any(x => x.ObjectId == state.TargetId)
                    && (!editTargets.TryGetValue(state.TargetId, out var old) || old.Status != MonitorEditTargetStatus.RemovedByEdit))
                    editTargets[state.TargetId] = state;
            // Successful mapping is not a successful sample: existing gaps close on the first actual new sample.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            foreach (var id in affected)
                if (!editTargets.TryGetValue(id, out var old) || old.Status != MonitorEditTargetStatus.RemovedByEdit)
                    SetState(id, response, step.Id, MonitorEditTargetStatus.NeedsSelection,
                        "monitor.edit.rebinding_failed:" + exception.Message, now);
            UpdateSuspension(active, affected, true);
        }
    }

    private void UpdateSuspension(ActiveSession active, IEnumerable<StorageObjectId> affected, bool suspended)
    {
        var ids = affected.ToHashSet();
        var targets = active.Request.Targets.Where(x => !editTargets.TryGetValue(x.ObjectId, out var state)
                || state.Status != MonitorEditTargetStatus.RemovedByEdit)
            .Select(x => ids.Contains(x.ObjectId) ? x with { SuspendedForEdit = suspended } : x).ToArray();
        active.ReplaceTargets(targets);
        if (source is IRebindableMonitorSource rebindable) rebindable.SetTargets(active.Request.SessionId, targets);
    }

    private Task ObserveAvailabilityAsync(MonitorTargetAvailability observation, CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(observation.SessionId, out var current)
            || !current.Request.Targets.Any(x => x.ObjectId == observation.TargetId && x.CounterIdentity == observation.CounterIdentity))
            return Task.CompletedTask;
        counterAvailability[(observation.SessionId, observation.TargetId, observation.CounterIdentity)] = observation.Available;
        if (!editTargets.TryGetValue(observation.TargetId, out var state))
        {
            if (!observation.Available) editTargets[observation.TargetId] = new(observation.TargetId.System, observation.TargetId,
                new OperationId(Guid.Empty), "sampling", MonitorEditTargetStatus.NeedsSelection,
                "monitor.identity.current_counter_unavailable", observation.ObservedAtUtc, observation.CounterIdentity);
            return Task.CompletedTask;
        }
        if (state.OperationId.Value == Guid.Empty || state.ReasonCode == "monitor.identity.current_counter_unavailable")
        {
            if (observation.Available && state.ReasonCode == "monitor.identity.current_counter_unavailable"
                && AllTargetCountersAvailable(current, observation.TargetId))
                editTargets[observation.TargetId] = state with { Status = MonitorEditTargetStatus.Restored,
                    ReasonCode = "monitor.edit.actual_sampling_restored", ObservedAtUtc = observation.ObservedAtUtc };
            return Task.CompletedTask;
        }
        if (!observation.Available && state.Status == MonitorEditTargetStatus.Restored)
        {
            editTargets[observation.TargetId] = state with { Status = MonitorEditTargetStatus.NeedsSelection,
                ReasonCode = "monitor.identity.current_counter_unavailable", ObservedAtUtc = observation.ObservedAtUtc };
            return Task.CompletedTask;
        }
        if (Interlocked.Increment(ref queuedAvailability) > 8192)
        {
            Interlocked.Decrement(ref queuedAvailability);
            if (sessions.TryGetValue(observation.SessionId, out var active))
                active.SetPersistenceFailure(new IOException("monitor.edit.availability_evidence_queue_full"));
            return Task.CompletedTask;
        }
        availabilityQueue.Enqueue((observation, state));
        StartAvailabilityDrain();
        return Task.CompletedTask;
    }

    private void StartAvailabilityDrain()
    {
        if (Interlocked.CompareExchange(ref drainingAvailability, 1, 0) != 0) return;
        availabilityDrain = Task.Run(async () =>
        {
            try
            {
                while (availabilityQueue.TryDequeue(out var item))
                {
                    Interlocked.Decrement(ref queuedAvailability);
                    try { await ProcessAvailabilityAsync(item.Observation, item.State, CancellationToken.None); }
                    catch (Exception exception) when (IsPersistenceFailure(exception))
                    {
                        if (sessions.TryGetValue(item.Observation.SessionId, out var active)) active.SetPersistenceFailure(exception);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref drainingAvailability, 0);
                if (!availabilityQueue.IsEmpty) StartAvailabilityDrain();
            }
        });
    }

    private async Task ProcessAvailabilityAsync(MonitorTargetAvailability observation, MonitorEditTargetState observedState, CancellationToken cancellationToken)
    {
        await editGate.WaitAsync(cancellationToken);
        try
        {
            if (!sessions.TryGetValue(observation.SessionId, out var active)
                || !editTargets.TryGetValue(observation.TargetId, out var state) || state.OperationId.Value == Guid.Empty) return;
            var key = (observation.SessionId, observation.TargetId, observation.CounterIdentity);
            if (!observation.Available)
            {
                if (observedState.Status is MonitorEditTargetStatus.Restored or MonitorEditTargetStatus.RemovedByEdit) return;
                if (!editGaps.ContainsKey(key))
                {
                    var ended = state.Status == MonitorEditTargetStatus.RemovedByEdit && state.ObservedAtUtc >= observation.ObservedAtUtc
                        ? state.ObservedAtUtc : (DateTimeOffset?)null;
                    var gap = new MonitorEditGap(Guid.NewGuid().ToString("N"), observation.SessionId, observedState.SystemId,
                        observation.TargetId, observation.CounterIdentity, observedState.OperationId, observedState.StepId,
                        observation.ObservedAtUtc, ended, ended is null ? observedState.Status : state.Status, observedState.ReasonCode);
                    if (ended is null) editGaps[key] = gap;
                    await SaveGapAsync(active, gap, cancellationToken);
                }
            }
            else
            {
                if (!active.Request.Targets.Any(x => x.ObjectId == observation.TargetId
                    && x.CounterIdentity == observation.CounterIdentity && !x.SuspendedForEdit)) return;
                await CloseGapsAsync(active, [observation.TargetId], MonitorEditTargetStatus.Restored,
                    "monitor.edit.actual_sampling_restored", observation.ObservedAtUtc, cancellationToken, observation.CounterIdentity);
                if (state.Status != MonitorEditTargetStatus.RemovedByEdit && AllTargetCountersAvailable(active, observation.TargetId)
                    && !editGaps.Keys.Any(x => x.Item1 == observation.SessionId && x.Item2 == observation.TargetId))
                    editTargets[observation.TargetId] = state with { Status = MonitorEditTargetStatus.Restored,
                        ReasonCode = "monitor.edit.actual_sampling_restored", ObservedAtUtc = observation.ObservedAtUtc };
            }
            await FlushGapWritesAsync(cancellationToken);
        }
        finally { editGate.Release(); }
    }

    private async Task CloseGapsAsync(ActiveSession active, IEnumerable<StorageObjectId> ids, MonitorEditTargetStatus state,
        string reason, DateTimeOffset at, CancellationToken cancellationToken, string? restoredCounter = null)
    {
        var targets = ids.ToHashSet();
        foreach (var (key, gap) in editGaps.Where(x => x.Key.Item1 == active.Request.SessionId && targets.Contains(x.Key.Item2)
            && (restoredCounter is null || CounterFamily(x.Key.Item3) == CounterFamily(restoredCounter))).ToArray())
        {
            if (at < gap.StartedAtUtc) continue; // A wall-clock rollback does not prove an interval endpoint.
            editGaps.Remove(key);
            await SaveGapAsync(active, gap with { EndedAtUtc = at, Status = state, ReasonCode = reason }, cancellationToken);
        }
    }

    private static string CounterFamily(string counter) => counter.StartsWith("disk-number:", StringComparison.Ordinal) ? "physical"
        : counter.StartsWith("vd-guid:", StringComparison.Ordinal) || counter.StartsWith("vd-disk-number:", StringComparison.Ordinal)
            ? "virtual-state" : counter;

    private bool AllTargetCountersAvailable(ActiveSession active, StorageObjectId target) => active.Request.Targets
        .Where(x => x.ObjectId == target).All(x => !x.SuspendedForEdit
            && counterAvailability.TryGetValue((active.Request.SessionId, target, x.CounterIdentity), out var available) && available);

    private async Task SaveGapAsync(ActiveSession active, MonitorEditGap gap, CancellationToken cancellationToken)
    {
        pendingGapWrites[gap.GapId] = (active, gap);
        await FlushGapWritesAsync(cancellationToken);
    }

    private async Task FlushGapWritesAsync(CancellationToken cancellationToken)
    {
        foreach (var (id, item) in pendingGapWrites.ToArray())
        {
            if (item.Session.Persistence is IMonitorEditGapPersistence persistence)
                await persistence.SaveEditGapAsync(item.Gap, cancellationToken);
            pendingGapWrites.Remove(id);
        }
    }

    private async Task FlushAvailabilityAsync()
    {
        do { await availabilityDrain; } while (Volatile.Read(ref drainingAvailability) != 0 || !availabilityQueue.IsEmpty);
        await editGate.WaitAsync();
        try { await FlushGapWritesAsync(CancellationToken.None); }
        finally { editGate.Release(); }
    }

    private void SetState(StorageObjectId id, AgentRealOperationResponse response, string stepId,
        MonitorEditTargetStatus status, string reason, DateTimeOffset at) => editTargets[id] =
        new(response.Plan.SystemId, id, response.Plan.OperationId, stepId, status, reason, at);

    private HashSet<StorageObjectId> AffectedTargets(AgentRealOperationResponse response, RealStorageCommand command, ActiveSession? active)
    {
        var existing = ExistingTarget(command);
        var current = active?.Request.Targets.Select(x => x.ObjectId).Distinct().ToArray()
            ?? editTargets.Keys.Where(x => x.System == response.Plan.SystemId).ToArray();
        if (existing is null) return [];
        if (command is CreateVirtualDiskCommand or CreateTieredVirtualDiskCommand or CreateTierCommand) return [];
        if (command is DeleteVirtualDiskCommand) return [existing.Value];
        if (active is null && identityFacts is null)
            return response.Plan.Targets.Where(x => x.Kind is StorageObjectKind.PhysicalDisk or StorageObjectKind.VirtualDisk).ToHashSet();
        var ids = new HashSet<string>(StringComparer.Ordinal) { existing.Value.ProviderKey };
        if (identityFacts is not null && identityFacts.SystemId == response.Plan.SystemId)
        {
            // The primordial pool groups all available hardware; it is not a storage component.
            var primordial = identityFacts.Objects.Where(x => x.ObjectType == FactObjectType.StoragePool
                    && x.Field("IsPrimordial")?.DisplayValue() == "true")
                .Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            var pending = new Queue<string>(ids);
            var component = command is CreatePoolCommand or DeletePoolCommand;
            while (pending.TryDequeue(out var id))
                foreach (var edge in identityFacts.Relationships.Where(x => !x.IsRetained && (x.FromId == id || x.ToId == id)
                    && !primordial.Contains(x.FromId) && !primordial.Contains(x.ToId)
                    && (component ? x.Kind is "pool-member" or "pool-virtual-disk" or "same-device" : x.Kind == "same-device")))
                {
                    var next = edge.FromId == id ? edge.ToId : edge.FromId;
                    if (ids.Add(next)) pending.Enqueue(next);
                }
        }
        return current.Where(x => ids.Contains(x.ProviderKey)).ToHashSet();
    }

    private static bool AffectsSampling(RealStorageCommand command) => command is SetDiskOnlineCommand or ClearDiskCommand
        or InitializeGptCommand or CreatePoolCommand or DeletePoolCommand or CreateVirtualDiskCommand
        or CreateTieredVirtualDiskCommand or DeleteVirtualDiskCommand;
    private static int StepRank(RealOperationStepState state) => state switch
    {
        RealOperationStepState.Pending => 0,
        RealOperationStepState.PreparingCall => 1,
        RealOperationStepState.CallIssued => 2,
        RealOperationStepState.WaitingForProvider => 3,
        RealOperationStepState.Verifying => 4,
        RealOperationStepState.OutcomeUnknown or RealOperationStepState.Failed or RealOperationStepState.StoppedBeforeCall => 5,
        RealOperationStepState.Verified => 6,
        _ => throw new InvalidDataException("Unknown durable monitor edit step state.")
    };
    private static bool IsVerifiedRemoval(RealStorageCommand command, StorageObjectId id) =>
        command is DeleteVirtualDiskCommand delete && delete.VirtualDisk.Existing == id
        || command is DeletePoolCommand && id.Kind == StorageObjectKind.VirtualDisk;
    private static StorageObjectId? ExistingTarget(RealStorageCommand command) => command switch
    {
        SetDiskOnlineCommand x => x.Disk.Existing, ClearDiskCommand x => x.Disk.Existing,
        InitializeGptCommand x => x.Disk.Existing, CreatePoolCommand x => x.PhysicalDisk.Existing,
        DeletePoolCommand x => x.Pool.Existing, CreateVirtualDiskCommand x => x.Pool.Existing,
        CreateTieredVirtualDiskCommand x => x.Pool.Existing, DeleteVirtualDiskCommand x => x.VirtualDisk.Existing,
        _ => null
    };
    private static bool HasNoWindowsCall(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { using var parsed = JsonDocument.Parse(json); return parsed.RootElement.TryGetProperty("NoWindowsCall", out var value) && value.ValueKind == JsonValueKind.True; }
        catch (JsonException) { return false; }
    }
}
