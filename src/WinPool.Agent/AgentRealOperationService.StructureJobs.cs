using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent;

public sealed partial class AgentRealOperationService
{
    private static bool IsExactProviderMsr(PartitionInfo partition) => partition.IsStable
        && partition.Type.Equals("MicrosoftReserved", StringComparison.OrdinalIgnoreCase)
        && Guid.TryParse(partition.PartitionTypeId, out var type)
        && type == new Guid("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    private sealed record FrozenStructureJob(RealStructureJobGoal Goal, StorageSystemDocument Baseline,
        RealAutomaticLayoutSnapshot? Layout = null);
    private sealed record PendingStructureStage(OperationId OperationId, RealOperationIntentRequest Proposal,
        RealStructureResultSynchronization.OutputMapping? Mapping, string Description,
        RealAutomaticLayoutSnapshot? Layout);
    private sealed record StructureJobResult(IReadOnlyList<string> Completed, RealStructureJobGoal Remaining, string? Code,
        StorageSystemDocument? Baseline = null, PendingStructureStage? Pending = null,
        RealAutomaticLayoutSnapshot? Layout = null);
    private readonly ConcurrentDictionary<Guid, Task> structureWorkers = new();
    private readonly ConcurrentDictionary<Guid, byte> structureStops = new();
    private readonly ConcurrentDictionary<Guid, OperationId> structureChildren = new();
    private Func<CancellationToken, Task<StorageSystemDocument>>? readStructureInventory;
    private Func<CancellationToken, Task<StorageSystemDocument?>>? readPublishedInventory;
    private Func<StorageInventoryScope, CancellationToken, Task<StorageSystemDocument>>? readStructureScoped;
    private Func<AgentRealStructureJobResponse, Task>? structureStatusObserver;

    public void AttachStructureInventory(Func<CancellationToken, Task<StorageSystemDocument>> fresh,
        Func<CancellationToken, Task<StorageSystemDocument?>> published,
        Func<StorageInventoryScope, CancellationToken, Task<StorageSystemDocument>>? scoped = null) =>
        (readStructureInventory, readPublishedInventory, readStructureScoped) = (fresh, published, scoped);
    public void AttachStructureStatusObserver(Func<AgentRealStructureJobResponse, Task> observer) =>
        structureStatusObserver = observer;

    private Task<bool> HasStructureBarrierAsync(CancellationToken ct) => plans.HasStructureWriteBarrierAsync(ct);

    private async Task RecoverStructureJobsAsync(CancellationToken ct)
    {
        foreach (var job in await plans.ListUnfinishedStructureJobsAsync(ct))
        {
            var next = job.State == RealOperationState.Prepared ? RealOperationState.Cancelled
                : job.State is RealOperationState.Accepted or RealOperationState.Running
                    ? RealOperationState.OutcomeUnknown : job.State;
            if (next != job.State)
                await plans.TransitionStructureJobAsync(job.JobId, job.State, next,
                    job.ResultJson, timeProvider.GetUtcNow(), ct);
        }
    }

    private async Task ReconcileInterruptedStructureJobsAsync(CancellationToken ct)
    {
        foreach (var job in await plans.ListUnfinishedStructureJobsAsync(ct))
        {
            if (job.State != RealOperationState.OutcomeUnknown || structureWorkers.ContainsKey(job.JobId)) continue;
            try
            {
                foreach (var child in job.Children)
                    if (reconciliationTasks.TryGetValue(child, out var pending)) await pending.WaitAsync(ct).ConfigureAwait(false);
                var children = new List<PersistedOperation>();
                foreach (var id in job.Children)
                    if (await plans.GetAsync(id, ct) is { } child) children.Add(child);
                if (children.Count != job.Children.Count || children.Any(child => child.State is
                    PersistedOperationState.Accepted or PersistedOperationState.Running or PersistedOperationState.OutcomeUnknown
                    or PersistedOperationState.Prepared)) continue;
                var frozen = JsonSerializer.Deserialize<FrozenStructureJob>(job.GoalJson)!;
                var saved = job.ResultJson is null ? new StructureJobResult([], frozen.Goal, null, frozen.Baseline, Layout: frozen.Layout)
                    : JsonSerializer.Deserialize<StructureJobResult>(job.ResultJson)!;
                if (saved.Pending is { } stage)
                {
                    var child = children.SingleOrDefault(item => item.Plan.OperationId == stage.OperationId);
                    if (child is null || readStructureScoped is null) continue;
                    var mapped = await RecoverPendingStructureStageAsync(job, saved, child, ct);
                    if (mapped is null) continue;
                    saved = mapped;
                }
                else if (children.Any(child => child.State == PersistedOperationState.Completed)
                    && job.ResultJson is null)
                    continue; // Old/incomplete durable state cannot invent a creation output mapping.
                var next = children.Count == 0 || children.All(child => child.State == PersistedOperationState.Cancelled)
                    ? RealOperationState.Cancelled : RealOperationState.PartiallyCompleted;
                if (children.Count > 0 && children.All(child => child.State == PersistedOperationState.Completed)
                    && saved.Baseline is not null && RealStructureDraftPlanner.IsSatisfiedByVerifiedBaseline(
                        BindStructureSession(saved.Remaining, saved.Baseline))) next = RealOperationState.Succeeded;
                saved = saved with { Code = "agent.structure.interrupted_no_automatic_continuation",
                    Remaining = saved.Remaining with { ResumeFromJobId = job.JobId } };
                await plans.TransitionStructureJobAsync(job.JobId, RealOperationState.OutcomeUnknown, next,
                    JsonSerializer.Serialize(saved), timeProvider.GetUtcNow(), ct);
                await PublishStructureStatusAsync(job.JobId);
            }
            catch (Exception exception) when (IsExpectedFailure(exception) || exception is JsonException or KeyNotFoundException)
            {
                // Leave the durable pending stage and write barrier intact; a later explicit Recheck may retry the read.
                DiagnosticLog.AppendFailure(plans.DiagnosticsRoot, "agent-control.jsonl", "agent.structure.recovery_incomplete",
                    exception, $"job={job.JobId:D}");
            }
        }
    }

    private async Task<StructureJobResult?> RecoverPendingStructureStageAsync(PersistedStructureJob job,
        StructureJobResult saved, PersistedOperation child, CancellationToken ct)
    {
        var pending = saved.Pending!;
        var baseline = saved.Baseline ?? JsonSerializer.Deserialize<FrozenStructureJob>(job.GoalJson)!.Baseline;
        var session = BindStructureSession(saved.Remaining, baseline);
        var layout = pending.Layout is { } checkpoint ? RealAutomaticLayoutProgress.Restore(checkpoint) : null;
        var full = await StatusAsync(child, CorrelationId.New(), ct, includeEvidence: true);
        var response = (AgentRealOperationResponse)full.Value!;
        if (!RealOperationSubmissionState.IsReconciledTerminal(response)) return null;
        var synchronization = new RealStructureResultSynchronization();
        synchronization.Register(session, response with { State = RealOperationState.Prepared }, pending.Proposal, pending.Mapping, layout);
        var scope = StorageInventoryScopeFactory.Create(baseline.SourceFacts!, child.Plan.OperationId, null, child.Plan.Targets);
        var fresh = await readStructureScoped!(scope, ct);
        if (!HasStructureResultScope(fresh, child.Plan.OperationId, session.SystemId)
            || !synchronization.Reconcile(response, fresh, true)) return null;
        if (layout is not null)
        {
            var pool = session.PoolIntents.Single(item => item.Value.VerifiedVirtualDiskId == layout.VirtualDiskId);
            if (layout.BuildNext(fresh, pool.Value) is null)
                session.PoolIntents[pool.Key] = pool.Value with { PendingAutomaticLayout = false };
        }
        return new(response.State == RealOperationState.Succeeded ? saved.Completed.Append(pending.Description).ToArray() : saved.Completed,
            StructureGoal(session, job.JobId), "agent.structure.recovered_read_only", fresh, Layout: layout?.Snapshot() ?? saved.Layout);
    }

    private static bool HasStructureResultScope(StorageSystemDocument document, OperationId operation, SystemId system) =>
        document.SystemId == system && document.SourceFacts?.ScopedCollection is { Complete: true } collection
        && collection.Scope.SystemId == system && collection.Scope.OperationId == operation;

    private static RealStructureJobGoal StructureGoal(SimulationEditingSession session, Guid? resume = null) =>
        RealStructureJobGoal.Create(session, resumeFromJobId: resume);

    private static RealStructureJobGoal WithDraftIntent(RealStructureJobGoal goal, StorageSystemDocument baseline)
    {
        if (goal.DraftIntentHash is not null) return goal;
        var editing = BindStructureSession(goal, baseline);
        return goal with { DraftIntentHash = RealStructureJobGoal.ComputeDraftIntentHash(editing,
            RealStructureDraftPlanner.Build(editing)) };
    }

    public async Task<ApplicationResult<AgentResponse>> PrepareStructureJobAsync(
        PrepareAgentRealStructureJobRequest request, TrustedRealSession session, CancellationToken ct)
    {
        if (request.JobId == Guid.Empty || request.Goal is null || !CanEnter(session)
            || !isSessionStillArmed(session) || request.ProductSessionId != session.ProductSessionId)
            return Reject(request.CorrelationId, "agent.structure.invalid_request");
        var canonical = request.Goal with { PoolIntents = request.Goal.PoolIntents.OrderBy(item => item.PoolId, StringComparer.Ordinal).ToArray() };
        var machine = await machineIdentity.ReadBindingAsync(ct);
        var existing = await plans.GetStructureJobAsync(request.JobId, ct);
        if (existing is not null)
            return existing.MachineBinding == machine
                && JsonSerializer.Serialize(JsonSerializer.Deserialize<FrozenStructureJob>(existing.GoalJson)!.Goal)
                    == JsonSerializer.Serialize(canonical) ? StructureStatus(existing, request.CorrelationId)
                : Reject(request.CorrelationId, "agent.structure.job_id_conflict");
        if (!await mutationGate.WaitAsync(0, ct)) return Reject(request.CorrelationId, "agent.real_operation.busy");
        try
        {
            if (!CanAcceptNewOperation() || await plans.HasRealWriteBarrierAsync(ct) || await HasStructureBarrierAsync(ct)
                || readStructureInventory is null)
                return Reject(request.CorrelationId, "agent.real_operation.write_barrier");
            RealAutomaticLayoutSnapshot? resumedLayout = null;
            if (canonical.ResumeFromJobId is { } sourceJobId)
            {
                var source = await plans.GetStructureJobAsync(sourceJobId, ct);
                var result = source?.ResultJson is { } json ? JsonSerializer.Deserialize<StructureJobResult>(json) : null;
                if (source is { State: RealOperationState.Cancelled, Children.Count: 0 } && result is null)
                {
                    var cancelled = JsonSerializer.Deserialize<FrozenStructureJob>(source.GoalJson)!;
                    result = new([], cancelled.Goal, "agent.structure.cancelled_before_accept", cancelled.Baseline, Layout: cancelled.Layout);
                }
                if (source is null || source.MachineBinding != machine || source.State is not
                    (RealOperationState.PartiallyCompleted or RealOperationState.Failed or RealOperationState.Cancelled)
                    || result is null || result.Pending is not null
                    || JsonSerializer.Serialize(canonical with { ResumeFromJobId = null, DraftIntentHash = null })
                        != JsonSerializer.Serialize(result.Remaining with { ResumeFromJobId = null, DraftIntentHash = null }))
                    return Reject(request.CorrelationId, "agent.structure.remaining_goal_changed_or_unresolved");
                var savedBaseline = result.Baseline ?? JsonSerializer.Deserialize<FrozenStructureJob>(source.GoalJson)!.Baseline;
                if (canonical.DraftIntentHash != WithDraftIntent(result.Remaining, savedBaseline).DraftIntentHash)
                    return Reject(request.CorrelationId, "agent.structure.remaining_goal_changed_or_unresolved");
                resumedLayout = result.Layout;
            }
            var baseline = await readStructureInventory(ct);
            if (resumedLayout is not null)
            {
                var progress = RealAutomaticLayoutProgress.Restore(resumedLayout);
                var intent = canonical.PoolIntents.Single(item => item.Intent.VerifiedVirtualDiskId == progress.VirtualDiskId).Intent;
                _ = progress.BuildNext(baseline, intent);
            }
            if (baseline.SystemId != canonical.SystemId) return Reject(request.CorrelationId, "agent.structure.system_mismatch");
            var editing = BindStructureSession(canonical, baseline);
            var draft = RealStructureDraftPlanner.Build(editing);
            if (!draft.Preview.CanApply) return Reject(request.CorrelationId, "agent.structure.invalid_goal", string.Join("; ", draft.Preview.BlockingReasons));
            var actualIntentHash = RealStructureJobGoal.ComputeDraftIntentHash(editing, draft);
            if (canonical.DraftIntentHash != actualIntentHash)
                return Reject(request.CorrelationId, "agent.structure.draft_changed",
                    "The live structural targets or parameters differ from the original draft. Refresh and review the draft before confirming.");
            _ = new RealStructureApplyAuthorization(draft, editing, session.ProductSessionId);
            foreach (var creation in draft.Creations.Where(item => item.CreatePool || item.CreateVirtualDisk || item.CreateTier))
                foreach (var member in creation.PhysicalDiskIds)
                    await backend!.ReadStructureCreationSupportAsync(new(canonical.SystemId, StorageObjectKind.PhysicalDisk, member),
                        (creation.CreateTier || creation.CreateVirtualDisk) && creation.Intent.Layout == PoolVirtualDiskLayout.HddTiered,
                        session, ct);
            if (!isSessionStillArmed(session)) return Reject(request.CorrelationId, "agent.real_mode.disarmed_during_preparation");
            var now = timeProvider.GetUtcNow();
            var frozenJson = JsonSerializer.Serialize(new FrozenStructureJob(canonical, baseline, resumedLayout));
            var hash = StructureGoalHash(frozenJson, machine, session.Binding);
            var job = new PersistedStructureJob(request.JobId, hash, frozenJson,
                machine, session.Binding, RealOperationState.Prepared, now, now, null, []);
            await plans.SaveStructureJobAsync(job, ct);
            return StructureStatus(job, request.CorrelationId);
        }
        catch (Exception exception) when (IsExpectedFailure(exception) || exception is NotSupportedException)
        { return Reject(request.CorrelationId, "agent.structure.prepare_failed", exception.Message); }
        finally { mutationGate.Release(); }
    }

    public async Task<ApplicationResult<AgentResponse>> AcceptStructureJobAsync(
        AcceptAgentRealStructureJobRequest request, TrustedRealSession session, CancellationToken ct)
    {
        var job = await plans.GetStructureJobAsync(request.JobId, ct);
        if (job is null || job.IntentHash != request.GoalHash) return Reject(request.CorrelationId, "agent.structure.goal_mismatch");
        if (job.State != RealOperationState.Prepared) return StructureStatus(job, request.CorrelationId);
        if (JsonSerializer.Deserialize<FrozenStructureJob>(job.GoalJson)!.Goal.DraftIntentHash is null)
            return Reject(request.CorrelationId, "agent.structure.draft_changed",
                "This saved structural job needs a new draft review before it can be accepted.");
        if (!CanEnter(session) || !isSessionStillArmed(session) || session.Binding != job.SessionBinding
            || request.ProductSessionId != session.ProductSessionId
            || StructureGoalHash(job.GoalJson, job.MachineBinding, job.SessionBinding) != job.IntentHash
            || timeProvider.GetUtcNow() - job.CreatedAt >= InMemoryOperationAuthority.DefaultLifetime
            || await machineIdentity.ReadBindingAsync(ct) != job.MachineBinding)
            return Reject(request.CorrelationId, "agent.structure.confirmation_expired_or_session_changed");
        if (!await mutationGate.WaitAsync(0, ct)) return Reject(request.CorrelationId, "agent.real_operation.busy");
        var handed = false;
        try
        {
            if (!CanAcceptNewOperation() || await plans.HasRealWriteBarrierAsync(ct) || await HasStructureBarrierAsync(ct))
                return Reject(request.CorrelationId, "agent.real_operation.write_barrier");
            if (!await plans.TransitionStructureJobAsync(job.JobId, RealOperationState.Prepared,
                    RealOperationState.Accepted, null, timeProvider.GetUtcNow(), CancellationToken.None))
                return StructureStatus((await plans.GetStructureJobAsync(job.JobId, ct))!, request.CorrelationId);
            handed = true;
            var worker = Task.Run(() => RunStructureJobAsync(job, session));
            structureWorkers[job.JobId] = worker;
            _ = worker.ContinueWith(_ => structureWorkers.TryRemove(job.JobId, out var ignored),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return StructureStatus((await plans.GetStructureJobAsync(job.JobId, CancellationToken.None))!, request.CorrelationId);
        }
        finally { if (!handed) mutationGate.Release(); }
    }

    public async Task<ApplicationResult<AgentResponse>> QueryStructureJobAsync(
        QueryAgentRealStructureJobRequest request, TrustedRealSession session, CancellationToken ct)
    {
        var job = await plans.GetStructureJobAsync(request.JobId, ct);
        return job is null ? Reject(request.CorrelationId, "agent.structure.not_found") : StructureStatus(job, request.CorrelationId, includeRemaining: true);
    }

    public async Task<ApplicationResult<AgentResponse>> StopStructureJobAsync(
        StopAgentRealStructureJobRequest request, TrustedRealSession session, CancellationToken ct)
    {
        var job = await plans.GetStructureJobAsync(request.JobId, ct);
        if (job is null || job.IntentHash != request.GoalHash) return Reject(request.CorrelationId, "agent.structure.goal_mismatch");
        structureStops[job.JobId] = 1;
        if (structureChildren.TryGetValue(job.JobId, out var child)) stopAfterCurrentStep[child] = 1;
        if (job.State == RealOperationState.Prepared)
            await plans.TransitionStructureJobAsync(job.JobId, job.State, RealOperationState.Cancelled,
                job.ResultJson, timeProvider.GetUtcNow(), ct);
        return StructureStatus((await plans.GetStructureJobAsync(job.JobId, ct))!, request.CorrelationId, includeRemaining: true);
    }

    private ApplicationResult<AgentResponse> StructureStatus(PersistedStructureJob job, CorrelationId correlation,
        bool includeRemaining = false)
    {
        var frozen = JsonSerializer.Deserialize<FrozenStructureJob>(job.GoalJson)!;
        var result = job.ResultJson is null ? null : JsonSerializer.Deserialize<StructureJobResult>(job.ResultJson);
        var terminal = job.State is not (RealOperationState.Prepared or RealOperationState.Accepted or RealOperationState.Running);
        var remaining = includeRemaining && terminal && job.State != RealOperationState.Succeeded
            ? result?.Remaining ?? frozen.Goal with { ResumeFromJobId = job.JobId }
            : null;
        if (remaining is not null) remaining = WithDraftIntent(remaining, result?.Baseline ?? frozen.Baseline);
        return ApplicationResult<AgentResponse>.Succeeded(new AgentRealStructureJobResponse(job.JobId, job.IntentHash,
            job.State, job.UpdatedAt.ToUnixTimeMilliseconds(), job.Children, result?.Completed ?? [], result?.Code,
            job.State == RealOperationState.OutcomeUnknown,
            remaining, frozen.Goal.SystemId, frozen.Goal.DraftIntentHash), correlation);
    }

    private async Task<IReadOnlyList<AgentRealStructureJobResponse>> ListRecoverableStructureStatusesAsync(
        CorrelationId correlation, CancellationToken ct)
    {
        var jobs = await plans.ListRecoverableStructureJobsAsync(ct);
        return jobs.Select(job => (AgentRealStructureJobResponse)StructureStatus(job, correlation).Value!).ToArray();
    }

    private static SimulationEditingSession BindStructureSession(RealStructureJobGoal goal, StorageSystemDocument baseline)
    {
        var editing = new SimulationEditingSession();
        editing.Bind(baseline, goal.Working);
        foreach (var item in goal.PoolIntents) editing.PoolIntents.Add(item.PoolId, item.Intent);
        return editing;
    }

    private static string StructureGoalHash(string frozenJson, string machine, string session) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(frozenJson + "\n" + machine + "\n" + session))).ToLowerInvariant();

    private async Task RunStructureJobAsync(PersistedStructureJob job, TrustedRealSession trustedSession)
    {
        var frozen = JsonSerializer.Deserialize<FrozenStructureJob>(job.GoalJson)!;
        var session = BindStructureSession(frozen.Goal, frozen.Baseline);
        var completed = new List<string>();
        var terminal = RealOperationState.OutcomeUnknown;
        string? code = null;
        try
        {
            if (!await plans.TransitionStructureJobAsync(job.JobId, RealOperationState.Accepted,
                RealOperationState.Running, null, timeProvider.GetUtcNow())) return;
            var success = await ExecuteStructureStagesAsync(job, frozen, session, trustedSession, completed);
            terminal = success ? RealOperationState.Succeeded : RealOperationState.PartiallyCompleted;
            if (structureChildren.TryGetValue(job.JobId, out var last)
                && await plans.GetAsync(last) is { State: PersistedOperationState.OutcomeUnknown })
                terminal = RealOperationState.OutcomeUnknown;
            if (!success && completed.Count == 0 && terminal != RealOperationState.OutcomeUnknown)
                terminal = structureStops.ContainsKey(job.JobId) ? RealOperationState.Cancelled : RealOperationState.Failed;
            code = success ? "agent.structure.completed" : "agent.structure.remaining_goal_retained";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            code = exception.GetType().Name + ": " + exception.Message;
            if (!structureChildren.TryGetValue(job.JobId, out var child))
                terminal = completed.Count == 0 ? RealOperationState.Failed : RealOperationState.PartiallyCompleted;
            else if (await plans.GetAsync(child) is { State: PersistedOperationState.Prepared })
            {
                await plans.TransitionAsync(child, PersistedOperationState.Prepared, PersistedOperationState.Cancelled,
                    Event(child, ExecutionEventKind.Cancelled, "operation.parent_stopped_before_accept"));
                terminal = completed.Count == 0 ? RealOperationState.Failed : RealOperationState.PartiallyCompleted;
            }
        }
        finally
        {
            try
            {
                var remaining = StructureGoal(session);
                var latest = await plans.GetStructureJobAsync(job.JobId);
                var durable = latest?.ResultJson is { } json ? JsonSerializer.Deserialize<StructureJobResult>(json) : null;
                if (durable?.Pending is { } pending
                    && await plans.GetAsync(pending.OperationId) is { State: PersistedOperationState.Cancelled or PersistedOperationState.Rejected })
                {
                    var steps = await plans.GetStepsAsync(pending.OperationId);
                    if (steps.Count == pending.Proposal.Steps.Count
                        && steps.All(step => step.State is PersistedOperationStepState.NotStarted or PersistedOperationStepState.Skipped))
                        durable = durable with { Pending = null };
                }
                var result = durable?.Pending is not null ? durable with { Code = code }
                    : new StructureJobResult(completed.ToArray(), remaining with { ResumeFromJobId = job.JobId }, code,
                        durable?.Baseline ?? frozen.Baseline, Layout: durable?.Layout ?? frozen.Layout);
                if (result.Pending is not null) terminal = RealOperationState.OutcomeUnknown;
                await plans.TransitionStructureJobAsync(job.JobId, RealOperationState.Running, terminal,
                    JsonSerializer.Serialize(result), timeProvider.GetUtcNow());
                await PublishStructureStatusAsync(job.JobId);
            }
            finally
            {
                structureChildren.TryRemove(job.JobId, out _);
                structureStops.TryRemove(job.JobId, out _);
                structureWorkers.TryRemove(job.JobId, out _);
                mutationGate.Release();
            }
        }
    }

    private async Task PublishStructureStatusAsync(Guid jobId)
    {
        if (structureStatusObserver is not { } publish) return;
        var job = await plans.GetStructureJobAsync(jobId);
        if (job is not null) await publish((AgentRealStructureJobResponse)StructureStatus(job, CorrelationId.New()).Value!);
    }

    private async Task<bool> ExecuteStructureStagesAsync(PersistedStructureJob job, FrozenStructureJob frozen, SimulationEditingSession session,
        TrustedRealSession trustedSession, List<string> completed)
    {
        var jobId = job.JobId;
        var childOrdinal = job.Children.Count;
        var current = frozen.Baseline;
        if (current.SourceFacts is not { IsMerged: false } baselineFacts
            || !WindowsRealStorageReadProfile.Covers(baselineFacts.ScopedCollection?.Scope.EvidenceProfile, null))
            throw new InvalidDataException("The confirmed structural baseline must contain full raw storage evidence.");
        var draft = RealStructureDraftPlanner.Build(session);
        var authorization = new RealStructureApplyAuthorization(draft, session, trustedSession.ProductSessionId);
        var topology = new WindowsRealStorageTopology(current, job.MachineBinding, job.CreatedAt);
        var authorizedEvidence = new Dictionary<string, (StorageSystemDocument Document, string? Profile)>(StringComparer.Ordinal);
        foreach (var members in authorization.PhysicalTargetSets)
        {
            var ids = members.Select(member => member.ProviderKey).ToHashSet(StringComparer.Ordinal);
            var pools = topology.Snapshot.StoragePools.Where(pool => !pool.IsPrimordial && pool.MemberPhysicalDiskIds.Any(ids.Contains)).ToArray();
            var closure = pools.Length == 1
                ? topology.RequireExactPhysicalMemberSet(new(topology.SystemId, StorageObjectKind.StoragePool, pools[0].StableId), members)
                : topology.RequireSelectedPhysicalMemberSet(members, requirePoolable: false);
            authorization.FreezeBaselineEvidence(members, topology.MachineBinding, trustedSession.Binding,
                closure.PhysicalMemberFingerprint, closure.Fingerprint);
            authorizedEvidence.Add(EvidenceScope(members), (current, null));
        }
        authorization.Confirm();
        var synchronization = new RealStructureResultSynchronization();
        var resumed = frozen.Layout;
        var automaticLayoutProgress = new Dictionary<SimulationEditingSession, RealAutomaticLayoutProgress>();
        if (resumed is not null) automaticLayoutProgress.Add(session, RealAutomaticLayoutProgress.Restore(resumed));
        string Text(string zh, string en) => en;
        void Report(string description) { }
            foreach (var pool in draft.RemovedPools)
            {
                var vds = session.Baseline.VirtualDisks.Where(vd => vd.PoolStableId == pool.StableId).ToArray();
                var tiers = session.Baseline.StorageTiers.Where(tier => tier.PoolStableId == pool.StableId
                    && tier.VirtualDiskStableId is null).Select(tier => Id(StorageObjectKind.StorageTier, tier.StableId)).ToArray();
                if (!await Stage(RealOperationProposalFactory.DissolveSingleMemberPool(session.SystemId,
                    Id(StorageObjectKind.StoragePool, pool.StableId), vds.Length == 0 ? null : Id(StorageObjectKind.VirtualDisk, vds.Single().StableId), tiers,
                    pool.MemberPhysicalDiskIds.Select(id => Id(StorageObjectKind.PhysicalDisk, id)).ToArray()),
                    Text($"已解散 {pool.FriendlyName}", $"Dissolved {pool.FriendlyName}"))) return false;
            }
            foreach (var vd in draft.RemovedVirtualDisks)
                if (!await Stage(RealOperationProposalFactory.OneStep(session.SystemId, OperationIntent.DeleteVirtualDisk,
                    Id(StorageObjectKind.VirtualDisk, vd.StableId),
                    new DeleteVirtualDiskCommand(RealTargetReference.ForExisting(Id(StorageObjectKind.VirtualDisk, vd.StableId))),
                    "The exact virtual disk and its children are absent", "All files, partitions and volumes on the exact virtual disk are lost"),
                    Text($"已删除 VD {vd.FriendlyName}", $"Deleted VD {vd.FriendlyName}"))) return false;
            foreach (var tier in draft.RemovedTiers)
                if (!await Stage(RealOperationProposalFactory.DeleteHddTierTemplate(session.SystemId,
                    Id(StorageObjectKind.StorageTier, tier.StableId)),
                    Text($"已删除模板 {tier.FriendlyName}", $"Deleted template {tier.FriendlyName}"))) return false;

            foreach (var creation in draft.Creations)
            {
                var intent = creation.Intent;
                var poolId = creation.Pool.StableId;
                var members = creation.PhysicalDiskIds.Select(id => Id(StorageObjectKind.PhysicalDisk, id)).ToArray();
                if (creation.CreatePool)
                {
                    foreach (var member in members)
                    {
                        var snapshot = current.Snapshot;
                        if (snapshot.StoragePools.Any(pool => !pool.IsPrimordial && pool.MemberPhysicalDiskIds.Contains(member.ProviderKey)))
                            throw new InvalidDataException("An exact member still belongs to a concrete pool.");
                        var direct = snapshot.OsDisks.SingleOrDefault(disk => disk.PhysicalDiskStableId == member.ProviderKey
                            && disk.VirtualDiskStableId is null);
                        if (direct is not null && (!direct.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
                            || snapshot.Partitions.Any(partition => partition.OsDiskStableId == direct.StableId)))
                        {
                            var clear = RealOperationProposalFactory.ClearToRaw(session.SystemId,
                                Id(StorageObjectKind.OsDisk, direct.StableId));
                            clear = clear with { Targets = clear.Targets.Concat(members).Distinct().ToArray() };
                            if (!await Stage(clear, Text("已核对清空至 RAW", "Verified clear to RAW"))) return false;
                        }
                    }
                    var temporaryPoolId = poolId;
                    if (!await Stage(RealOperationProposalFactory.CreatePoolWithMembers(session.SystemId,
                        members, creation.Pool.FriendlyName),
                        Text("已核对新池", "Verified new pool"),
                        new(StorageObjectKind.StoragePool, temporaryPoolId, "create-pool"))) return false;
                    poolId = session.PoolIntents.Keys.Single(id => current.Snapshot.StoragePools
                        .Any(pool => pool.StableId == id && pool.MemberPhysicalDiskIds.ToHashSet(StringComparer.Ordinal)
                            .SetEquals(creation.PhysicalDiskIds)));
                }
                foreach (var goal in intent.MemberUsages ?? [])
                {
                    var actual = current.Snapshot.PhysicalDisks.Single(item => item.StableId == goal.PhysicalDiskId);
                    if (PhysicalDiskUsage.Normalize(actual.Usage) == PhysicalDiskUsage.Normalize(goal.Usage)) continue;
                    if (!await Stage(RealOperationProposalFactory.SetPhysicalDiskUsage(session.SystemId,
                        Id(StorageObjectKind.PhysicalDisk, goal.PhysicalDiskId), Id(StorageObjectKind.StoragePool, poolId), members, goal.Usage),
                        Text("已核对成员角色", "Verified member usage"))) return false;
                }
                var tierTargets = new Dictionary<string, StorageObjectId>(StringComparer.Ordinal);
                if ((creation.CreateTier || creation.CreateVirtualDisk) && intent.Layout == PoolVirtualDiskLayout.HddTiered)
                {
                    foreach (var goal in creation.TierGoals)
                    {
                        var existing = current.Snapshot.StorageTiers.Where(tier => tier.PoolStableId == poolId
                            && tier.VirtualDiskStableId is null && tier.MediaType == goal.MediaType).ToArray();
                        if (existing.Length == 0)
                        {
                            if (!await Stage(RealOperationProposalFactory.CreateTierTemplate(session.SystemId,
                                Id(StorageObjectKind.StoragePool, poolId), members, creation.Pool.FriendlyName + "_" + goal.MediaType,
                                goal.MediaType, goal.Layout), Text($"已核对 {goal.MediaType} 层模板", $"Verified {goal.MediaType} tier template"),
                                new(StorageObjectKind.StorageTier, poolId, "create-tier", goal.MediaType))) return false;
                            existing = current.Snapshot.StorageTiers.Where(tier => tier.PoolStableId == poolId
                                && tier.VirtualDiskStableId is null && tier.MediaType == goal.MediaType).ToArray();
                        }
                        if (existing.Length != 1 || goal.VerifiedTemplateId is not null && goal.VerifiedTemplateId != existing[0].StableId)
                            throw new InvalidDataException("One exact unused template is required for each requested media type.");
                        tierTargets.Add(goal.MediaType, Id(StorageObjectKind.StorageTier, existing[0].StableId));
                    }
                }
                if (!creation.CreateVirtualDisk && !creation.ContinueLayout) continue;
                string? vdId = intent.VerifiedVirtualDiskId;
                if (creation.CreateVirtualDisk)
                {
                    // In a same-pool replacement this is reached only after the exact
                    // old VD deletion stage and its scoped refresh have completed.
                    var freshPool = current.Snapshot.StoragePools.SingleOrDefault(pool => pool.StableId == poolId);
                    if (freshPool is null || current.SourceFacts?.Objects
                        .SingleOrDefault(item => item.Id == poolId) is not { HasReliableIdentity: true }
                        || current.Snapshot.VirtualDisks.Any(vd => vd.PoolStableId == poolId))
                        throw new InvalidDataException("The fresh exact pool must be empty before preparing its first virtual disk.");
                    if (intent.VirtualDiskUseMaximum)
                        Report(Text("正在确定最大可用容量", "Determining maximum available capacity"));
                    async Task<RealVirtualDiskCreationCapacity> Capacity(StorageObjectId sizeTarget, bool maximum, long? size, bool autoPartition)
                        => await RealVirtualDiskCreationCapacityResolver.ResolveAsync(maximum, size,
                        autoPartition, intent.CreateMsr, async () =>
                        {
                            Report(Text("正在核对创建容量", "Checking creation capacity"));
                            return await backend!.ReadVirtualDiskCreationRangeAsync(sizeTarget, trustedSession, CancellationToken.None).ConfigureAwait(false);
                        });
                    RealOperationIntentRequest proposal;
                    if (intent.Layout == PoolVirtualDiskLayout.HddTiered)
                    {
                        var inputs = new List<MaximumCapacityTier>();
                        foreach (var goal in creation.TierGoals)
                        {
                            var target = tierTargets[goal.MediaType];
                            var capacity = await Capacity(target, goal.UseMaximumSize, goal.SizeBytes, false);
                            inputs.Add(new(RealTargetReference.ForExisting(target), null,
                                capacity.UseMaximumSize ? null : capacity.SizeBytes, capacity.UseMaximumSize, goal.Layout, goal.MediaType));
                        }
                        proposal = RealOperationProposalFactory.CreateTieredVirtualDisk(session.SystemId,
                            Id(StorageObjectKind.StoragePool, poolId), members, inputs, intent.VirtualDiskName!);
                    }
                    else
                    {
                        var capacity = await Capacity(Id(StorageObjectKind.StoragePool, poolId), intent.VirtualDiskUseMaximum,
                            intent.VirtualDiskSizeBytes, intent.AutoCreatePartition);
                        proposal = RealOperationProposalFactory.CreateFirstVirtualDisk(session.SystemId,
                            Id(StorageObjectKind.StoragePool, poolId), members, new(intent.VirtualDiskName!, capacity.SizeBytes, false,
                                intent.CreateMsr, true, intent.VolumeName, intent.DriveLetter, capacity.UseMaximumSize));
                    }
                    if (!await Stage(proposal, Text("已核对新 VD", "Verified new VD"),
                        new(StorageObjectKind.VirtualDisk, poolId,
                            intent.Layout == PoolVirtualDiskLayout.HddTiered ? "create-tiered-vdisk" : "create-vdisk"))) return false;
                    vdId = session.PoolIntents[poolId].VerifiedVirtualDiskId;
                }
                if (!intent.AutoCreatePartition) continue;
                if (vdId is null) throw new InvalidDataException("Automatic layout lacks a verified VD identity.");
                // Partition layout uses the actual OS-disk size only after the MAX search/explicit creation is verified.
                var osDisk = current.Snapshot.OsDisks.SingleOrDefault(disk => disk.VirtualDiskStableId == vdId)
                    ?? throw new InvalidDataException("The verified VD has no unique current OS disk.");
                if (osDisk.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase))
                {
                    if (!await Stage(RealOperationProposalFactory.InitializeGpt(session.SystemId,
                        Id(StorageObjectKind.OsDisk, osDisk.StableId)), Text("已核对 GPT 初始化", "Verified GPT initialization"))) return false;
                    osDisk = current.Snapshot.OsDisks.Single(disk => disk.VirtualDiskStableId == vdId);
                }
                var currentIntent = session.PoolIntents[poolId];
                if (!automaticLayoutProgress.TryGetValue(session, out var progress)
                    || progress.VirtualDiskId != vdId)
                {
                    var partitions = current.Snapshot.Partitions
                        .Where(partition => partition.OsDiskStableId == osDisk.StableId).ToArray();
                    if (!osDisk.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase)
                        || partitions.Length > 1 || partitions.Any(partition => !IsExactProviderMsr(partition)))
                        throw new InvalidDataException("The automatic layout has no session-verified progression for existing data partitions. The remaining goal is retained.");
                    var layout = RealOperationProposalFactory.ConfigureInitializedDisk(session.SystemId,
                        Id(StorageObjectKind.OsDisk, osDisk.StableId), partitions.Length == 0 ? null
                            : Id(StorageObjectKind.Partition, partitions[0].StableId), intent.CreateMsr,
                        osDisk.Size, true, intent.VolumeName, intent.DriveLetter,
                        checked((int)intent.AllocationUnitSize), intent.FileSystem)!;
                    progress = new RealAutomaticLayoutProgress(layout, vdId, currentIntent);
                    automaticLayoutProgress[session] = progress;
                }
                while (progress.BuildNext(current, currentIntent) is { } next)
                {
                    var description = next.Steps[0].Command switch
                    {
                        DeletePartitionCommand => Text("已调整保留分区", "Adjusted the reserved partition"),
                        CreatePartitionCommand { Role: RealPartitionRole.Msr } => Text("已配置 MSR", "Configured the MSR"),
                        CreatePartitionCommand => Text("已创建数据分区", "Created the data partition"),
                        FormatVolumeCommand => Text("已格式化数据卷", "Formatted the data volume"),
                        SetDriveLetterCommand => Text("已设置盘符", "Assigned the drive letter"),
                        _ => Text("已完成自动分区", "Completed automatic partitioning")
                    };
                    if (!await Stage(next, description, layout: progress)) return false;
                }
                session.PoolIntents[poolId] = currentIntent with { PendingAutomaticLayout = false };
                automaticLayoutProgress.Remove(session);
            }

        return true;

        StorageObjectId Id(StorageObjectKind kind, string key) => new(session.SystemId, kind, key);
        string EvidenceScope(IEnumerable<StorageObjectId> members) => JsonSerializer.Serialize(members
            .Select(member => member.ProviderKey).Order(StringComparer.Ordinal).ToArray());
        RealExactPhysicalMemberSetClosure EvidenceClosure(StorageSystemDocument document,
            IReadOnlyList<StorageObjectId> members, string? profile)
        {
            if (document.SystemId != session.SystemId || document.SourceFacts is not { IsMerged: false } facts
                || !WindowsRealStorageReadProfile.Covers(facts.ScopedCollection?.Scope.EvidenceProfile, profile))
                throw new InvalidDataException("Structural authorization needs complete raw facts covering its evidence profile.");
            var evidence = new WindowsRealStorageTopology(document, job.MachineBinding, timeProvider.GetUtcNow(), profile);
            var ids = members.Select(member => member.ProviderKey).ToHashSet(StringComparer.Ordinal);
            var pools = evidence.Snapshot.StoragePools.Where(pool => !pool.IsPrimordial && pool.MemberPhysicalDiskIds.Any(ids.Contains)).ToArray();
            if (pools.Length > 1) throw new InvalidDataException("A structural evidence scope cannot span concrete pools.");
            return pools.Length == 1
                ? evidence.RequireExactPhysicalMemberSet(Id(StorageObjectKind.StoragePool, pools[0].StableId), members)
                : evidence.RequireSelectedPhysicalMemberSet(members, requirePoolable: false);
        }
        async Task<bool> Stage(RealOperationIntentRequest proposal, string description,
            RealStructureResultSynchronization.OutputMapping? mapping = null, RealAutomaticLayoutProgress? layout = null)
        {
            if (structureStops.ContainsKey(jobId)) return false;
            // Closure comes from current verified provider facts; never broaden a
            // child stage to an unrelated member merely because it shares Primordial.
            proposal = RealOperationProposalFactory.DeclareCurrentPoolMembers(proposal, current.Snapshot);
            authorization.ValidateProposal(proposal);
            var members = proposal.Targets.Where(target => target.Kind == StorageObjectKind.PhysicalDisk).ToArray();
            var scopeKey = EvidenceScope(members);
            var previousEvidence = authorizedEvidence[scopeKey];
            var nextProfile = WindowsRealStorageReadProfile.ForSteps(proposal.Steps)?.Id;
            var operationId = OperationId.New();
            if (previousEvidence.Profile != nextProfile)
            {
                var raw = previousEvidence.Document;
                if (!WindowsRealStorageReadProfile.Covers(raw.SourceFacts!.ScopedCollection?.Scope.EvidenceProfile, nextProfile))
                {
                    if (readStructureScoped is null)
                        throw new InvalidDataException("A wider structural evidence profile requires a fresh scoped observation.");
                    var scope = StorageInventoryScopeFactory.Create(current.SourceFacts!, operationId, null, proposal.Targets)
                        with { EvidenceProfile = WindowsRealStorageReadProfile.CoveringBoth(previousEvidence.Profile, nextProfile) };
                    raw = await readStructureScoped(scope, CancellationToken.None);
                    if (!HasStructureResultScope(raw, operationId, session.SystemId)
                        || !raw.SourceFacts!.ScopedCollection!.Scope.Targets.ToHashSet().SetEquals(proposal.Targets))
                        throw new InvalidDataException("The structural evidence refresh belongs to another operation or target closure.");
                }
                var oldClosure = EvidenceClosure(raw, members, previousEvidence.Profile);
                var newClosure = EvidenceClosure(raw, members, nextProfile);
                authorization.RebindEvidenceProfile(proposal, previousEvidence.Profile, oldClosure.Fingerprint,
                    nextProfile, newClosure.Fingerprint, newClosure.PhysicalMemberFingerprint);
                authorizedEvidence[scopeKey] = (raw, nextProfile);
            }
            var plan = await backend!.PrepareAsync(proposal, trustedSession, operationId, CancellationToken.None);
            var frozen = new AgentRealOperationResponse(plan, RealOperationState.Prepared,
                plan.RealOperation!.Steps.Select(item => new RealOperationStepProgress(item.Id, RealOperationStepState.Pending, null, null, null)).ToArray(), null, false);
            authorization.ValidateFrozen(proposal, frozen);
            var persisted = await plans.PrepareAsync(plan, Guid.NewGuid(), plan.PlanHash);
            // Parent link and confirmation range are durable before child acceptance/CallIssued.
            await plans.LinkStructureChildAsync(jobId, plan.OperationId, childOrdinal++);
            structureChildren[jobId] = plan.OperationId;
            var checkpoint = layout?.Snapshot() ?? automaticLayoutProgress.GetValueOrDefault(session)?.Snapshot();
            var pendingStage = new PendingStructureStage(plan.OperationId, proposal, mapping, description, layout?.Snapshot());
            var before = current;
            var durablePending = new StructureJobResult(completed.ToArray(), StructureGoal(session, jobId),
                "agent.structure.stage_prepared", before, pendingStage, checkpoint);
            if (!await plans.TransitionStructureJobAsync(jobId, RealOperationState.Running, RealOperationState.Running,
                JsonSerializer.Serialize(durablePending), timeProvider.GetUtcNow()))
                throw new InvalidOperationException("The pending structural stage could not be made durable.");
            if (structureStops.ContainsKey(jobId))
            {
                await plans.TransitionAsync(plan.OperationId, PersistedOperationState.Prepared, PersistedOperationState.Cancelled,
                    Event(plan.OperationId, ExecutionEventKind.Cancelled, "operation.cancelled_before_accept"));
                return false;
            }
            var context = CreateExecutionContext(plan, trustedSession, await machineIdentity.ReadBindingAsync(CancellationToken.None));
            var grant = await authority.AuthorizeConfirmedRealAsync(plan, context, plan.PlanHash, CancellationToken.None);
            if (grant.Token is null || !authority.Consume(grant.Token, plan, context).IsValid)
                throw new InvalidOperationException(grant.Code);
            synchronization.Register(session, frozen, proposal, mapping, layout);
            await plans.AcceptAsync(plan.OperationId, plan.PlanHash,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(grant.Token.TokenId))).ToLowerInvariant(),
                timeProvider.GetUtcNow());
            // The parent already owns mutationGate. Internal stages never re-enter AcceptAsync.
            var post = await RunStepsAsync(plan);
            await NotifyEditObserverAsync(plan.OperationId);
            var status = await StatusAsync((await plans.GetAsync(plan.OperationId))!, CorrelationId.New(), CancellationToken.None, includeEvidence: true);
            var response = (AgentRealOperationResponse)status.Value!;
            if (post is not null)
                current = await readPublishedInventory!(CancellationToken.None) ?? post.Document;
            if (!RealOperationSubmissionState.IsReconciledTerminal(response))
            {
                layout?.Observe(proposal, response);
                // The pending baseline is the pre-call baseline written above, not a partial post-state.
                await plans.TransitionStructureJobAsync(jobId, RealOperationState.Running, RealOperationState.Running,
                    JsonSerializer.Serialize(durablePending with { Code = "agent.structure.stage_unresolved",
                        Pending = pendingStage with { Layout = layout?.Snapshot() }, Layout = layout?.Snapshot() ?? checkpoint }), timeProvider.GetUtcNow());
                return false;
            }
            if (response.State != RealOperationState.Succeeded || !HasStructureResultScope(current, plan.OperationId, session.SystemId))
            {
                if (readStructureScoped is null) return false;
                var scope = StorageInventoryScopeFactory.Create(before.SourceFacts!, plan.OperationId, null, plan.Targets);
                current = await readStructureScoped(scope, CancellationToken.None);
            }
            if (!HasStructureResultScope(current, plan.OperationId, session.SystemId)
                || !synchronization.Reconcile(response, current, true)) return false;
            if (response.State == RealOperationState.Succeeded)
            {
                authorization.ObserveVerified(proposal, response, current);
                if (post?.Document is not { SourceFacts.IsMerged: false } rawPostState)
                    throw new InvalidDataException("A verified structural stage must retain its raw post-state evidence.");
                authorizedEvidence[scopeKey] = (rawPostState, nextProfile);
                completed.Add(description);
            }
            var remaining = StructureGoal(session, jobId);
            await plans.TransitionStructureJobAsync(jobId, RealOperationState.Running, RealOperationState.Running,
                JsonSerializer.Serialize(new StructureJobResult(completed.ToArray(), remaining,
                    "agent.structure.stage_verified", current, Layout: layout?.Snapshot()
                        ?? automaticLayoutProgress.GetValueOrDefault(session)?.Snapshot())), timeProvider.GetUtcNow());
            await PublishStructureStatusAsync(jobId);
            return response.State == RealOperationState.Succeeded;
        }
    }
}
