using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Agent;

/// <summary>
/// Owns the short IPC requests and the Agent-lifetime execution task. Windows
/// calls are made only through the closed backend after durable acceptance.
/// </summary>
public sealed class AgentRealOperationService : IRealOperationService
{
    private readonly OperationPlanRepository plans;
    private readonly IRealStorageBackend? backend;
    private readonly IRealMachineIdentityProvider machineIdentity;
    private readonly IOperationAuthority authority;
    private readonly TimeProvider timeProvider;
    private readonly Func<bool> isAdministrator;
    private readonly Func<TrustedRealSession, bool> isSessionStillArmed;
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly ConcurrentDictionary<OperationId, byte> stopAfterCurrentStep = new();
    private readonly ConcurrentDictionary<OperationId, Task> runningTasks = new();
    private readonly ConcurrentDictionary<OperationId, Task> reconciliationTasks = new();
    private int recoveryReady;
    private int admissionClosed;

    public AgentRealOperationService(
        OperationPlanRepository plans,
        IRealStorageBackend? backend,
        IRealMachineIdentityProvider machineIdentity,
        AgentRealModeGate realModeGate,
        IOperationAuthority? authority = null,
        TimeProvider? timeProvider = null)
        : this(plans, backend, machineIdentity, authority, timeProvider,
            realModeGate.IsSessionArmed,
            IsCurrentAgentAdministrator)
    {
    }

    internal AgentRealOperationService(
        OperationPlanRepository plans,
        IRealStorageBackend? backend,
        IRealMachineIdentityProvider machineIdentity,
        IOperationAuthority? authority,
        TimeProvider? timeProvider,
        Func<TrustedRealSession, bool> isSessionStillArmed,
        Func<bool> isAdministrator)
    {
        this.plans = plans ?? throw new ArgumentNullException(nameof(plans));
        this.backend = backend;
        this.machineIdentity = machineIdentity
            ?? throw new ArgumentNullException(nameof(machineIdentity));
        this.authority = authority
            ?? new InMemoryOperationAuthority(new OperationPolicyEvaluator());
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.isAdministrator = isAdministrator
            ?? throw new ArgumentNullException(nameof(isAdministrator));
        this.isSessionStillArmed = isSessionStillArmed
            ?? throw new ArgumentNullException(nameof(isSessionStillArmed));
    }

    /// <summary>
    /// Must finish before the Agent exposes the real request service. An
    /// accepted operation is never replayed after restart.
    /// </summary>
    public async Task InitializeRecoveryAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref recoveryReady, 0);
        var unfinished = await plans.ListUnfinishedAsync(cancellationToken);
        foreach (var operation in unfinished)
        {
            if (operation.State == PersistedOperationState.Prepared)
            {
                await plans.TransitionAsync(
                    operation.Plan.OperationId,
                    PersistedOperationState.Prepared,
                    PersistedOperationState.Cancelled,
                    Event(operation.Plan.OperationId, ExecutionEventKind.Cancelled,
                        "operation.prepared_session_expired"), cancellationToken);
            }
            else if (operation.State is PersistedOperationState.Accepted
                     or PersistedOperationState.Running)
            {
                await plans.TransitionAsync(
                    operation.Plan.OperationId,
                    operation.State,
                    PersistedOperationState.OutcomeUnknown,
                    Event(operation.Plan.OperationId, ExecutionEventKind.Failed,
                        "operation.interrupted_requires_reconciliation"), cancellationToken);
            }
        }

        Volatile.Write(ref recoveryReady, 1);
        if (backend is not null)
        {
            foreach (var operation in await plans.ListUnfinishedAsync(cancellationToken))
            {
                if (operation.State == PersistedOperationState.OutcomeUnknown)
                {
                    ScheduleReconciliation(operation);
                }
            }
        }
    }

    public async Task<ApplicationResult<AgentResponse>> EnterModeAsync(
        EnterAgentRealModeRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken)
    {
        if (!CanEnter(session))
        {
            return Reject(request.CorrelationId,
                "agent.real_mode.administrator_required");
        }

        if (!await mutationGate.WaitAsync(0, cancellationToken))
        {
            return Reject(request.CorrelationId, "agent.real_operation.busy");
        }
        try
        {
            await CancelPreparedAsync(
                session.Binding, cancelDifferentSession: true,
                cancellationToken);
        }
        finally
        {
            mutationGate.Release();
        }

        return ApplicationResult<AgentResponse>.Succeeded(
            new AgentRealModeResponse(true, "agent.real_mode.armed"),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> ExitModeAsync(
        ExitAgentRealModeRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken)
    {
        // Coordinator has already disarmed memory. A prepared plan has no OS
        // side effect and cannot remain a barrier after that session exits.
        if (await mutationGate.WaitAsync(0, cancellationToken))
        {
            try
            {
                await CancelPreparedAsync(
                    session.Binding, cancelDifferentSession: false,
                    cancellationToken);
            }
            finally
            {
                mutationGate.Release();
            }
        }

        return ApplicationResult<AgentResponse>.Succeeded(
            new AgentRealModeResponse(false, "agent.real_mode.disarmed"),
            request.CorrelationId);
    }

    private async Task CancelPreparedAsync(
        string sessionBinding,
        bool cancelDifferentSession,
        CancellationToken cancellationToken)
    {
        foreach (var operation in await plans.ListUnfinishedAsync(cancellationToken))
        {
            if (operation.State != PersistedOperationState.Prepared)
            {
                continue;
            }

            var same = StringComparer.Ordinal.Equals(
                operation.Plan.RealOperation?.SessionBinding, sessionBinding);
            if ((cancelDifferentSession && same)
                || (!cancelDifferentSession && !same))
            {
                continue;
            }

            await plans.TransitionAsync(
                operation.Plan.OperationId,
                PersistedOperationState.Prepared,
                PersistedOperationState.Cancelled,
                Event(operation.Plan.OperationId, ExecutionEventKind.Cancelled,
                    "operation.prepared_session_ended"), cancellationToken);
        }
    }

    public async Task<ApplicationResult<AgentResponse>> PrepareAsync(
        PrepareAgentRealOperationRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken)
    {
        if (request.PreparationId == Guid.Empty || request.Intent is null)
        {
            return Reject(request.CorrelationId, "agent.real_operation.invalid_preparation");
        }
        if (!CanEnter(session))
        {
            return Reject(request.CorrelationId, "agent.real_mode.administrator_required");
        }
        if (!await mutationGate.WaitAsync(0, cancellationToken))
        {
            return Reject(request.CorrelationId, "agent.real_operation.busy");
        }

        try
        {
            var machineBinding = await machineIdentity.ReadBindingAsync(cancellationToken);
            var intentHash = RealOperationIntentHasher.Compute(
                request.Intent, session, machineBinding);
            var prior = await plans.GetByPreparationIdAsync(
                request.PreparationId, cancellationToken);
            if (prior is not null)
            {
                return StringComparer.Ordinal.Equals(prior.PreparationIntentHash, intentHash)
                    ? await StatusAsync(prior, request.CorrelationId, cancellationToken)
                    : Reject(request.CorrelationId,
                        "agent.real_operation.preparation_id_conflict");
            }

            if (!CanAcceptNewOperation()
                || await plans.HasRealWriteBarrierAsync(cancellationToken))
            {
                return Reject(request.CorrelationId, "agent.real_operation.write_barrier");
            }
            if (backend is null)
            {
                return Reject(request.CorrelationId, "agent.real_operation.backend_unavailable");
            }

            var operationId = OperationId.New();
            var plan = await backend.PrepareAsync(
                request.Intent, session, operationId, cancellationToken);
            if (plan.OperationId != operationId
                || plan.RealOperation is null
                || !StringComparer.Ordinal.Equals(
                    plan.RealOperation.MachineBinding, machineBinding)
                || !StringComparer.Ordinal.Equals(
                    plan.RealOperation.SessionBinding, session.Binding)
                || !RealOperationValidator.IsValid(plan))
            {
                return Reject(request.CorrelationId, "agent.real_operation.invalid_plan");
            }
            if (!isSessionStillArmed(session))
            {
                return Reject(request.CorrelationId,
                    "agent.real_mode.disarmed_during_preparation");
            }

            var prepared = await plans.PrepareAsync(
                plan, request.PreparationId, intentHash, cancellationToken);
            return await StatusAsync(prepared, request.CorrelationId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(request.CorrelationId);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return Reject(request.CorrelationId,
                "agent.real_operation.prepare_failed");
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<ApplicationResult<AgentResponse>> AcceptAsync(
        AcceptAgentRealOperationRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken)
    {
        if (request.OperationId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(request.PlanHash))
        {
            return Reject(request.CorrelationId, "agent.real_operation.invalid_identity");
        }

        // An accepted request is idempotent even after the App's mode changed.
        var prior = await plans.GetAsync(request.OperationId, cancellationToken);
        if (prior is null || !StringComparer.Ordinal.Equals(
                prior.Plan.PlanHash, request.PlanHash))
        {
            return Reject(request.CorrelationId, "agent.real_operation.plan_mismatch");
        }
        if (prior.State != PersistedOperationState.Prepared)
        {
            return await StatusAsync(prior, request.CorrelationId, cancellationToken);
        }
        if (!CanEnter(session))
        {
            return Reject(request.CorrelationId, "agent.real_mode.administrator_required");
        }
        if (!await mutationGate.WaitAsync(0, cancellationToken))
        {
            var concurrent = await plans.GetAsync(
                request.OperationId, cancellationToken);
            return concurrent is not null
                && StringComparer.Ordinal.Equals(
                    concurrent.Plan.PlanHash, request.PlanHash)
                && concurrent.State != PersistedOperationState.Prepared
                    ? await StatusAsync(concurrent, request.CorrelationId,
                        cancellationToken)
                    : Reject(request.CorrelationId, "agent.real_operation.busy");
        }

        var handedToBackground = false;
        try
        {
            prior = await plans.GetAsync(request.OperationId, cancellationToken);
            if (prior is null || !StringComparer.Ordinal.Equals(
                    prior.Plan.PlanHash, request.PlanHash))
            {
                return Reject(request.CorrelationId, "agent.real_operation.plan_mismatch");
            }
            if (prior.State != PersistedOperationState.Prepared)
            {
                return await StatusAsync(prior, request.CorrelationId, cancellationToken);
            }
            if (!CanAcceptNewOperation() || backend is null)
            {
                return Reject(request.CorrelationId, "agent.real_operation.unavailable");
            }
            if (prior.Plan.RealOperation is not { } real
                || real.ExpiresAt <= timeProvider.GetUtcNow()
                || !StringComparer.Ordinal.Equals(
                    real.SessionBinding, session.Binding))
            {
                return Reject(request.CorrelationId, "agent.real_operation.prepared_plan_expired");
            }

            var firstStep = real.Steps.FirstOrDefault();
            if (firstStep is null)
            {
                return Reject(request.CorrelationId, "agent.real_operation.empty_plan");
            }
            var preflight = await backend.PreflightStepAsync(
                prior.Plan, firstStep,
                new Dictionary<string, string>(), cancellationToken);
            var machineBinding = await machineIdentity.ReadBindingAsync(cancellationToken);
            var context = CreateExecutionContext(
                prior.Plan, session, machineBinding, preflight);
            if (!isSessionStillArmed(session))
            {
                return Reject(request.CorrelationId,
                    "agent.real_mode.disarmed_before_accept");
            }
            var issued = await authority.AuthorizeConfirmedRealAsync(
                prior.Plan, context, request.PlanHash, cancellationToken);
            if (issued.Kind != AuthorizationIssueKind.Issued || issued.Token is null)
            {
                return Reject(request.CorrelationId, issued.Code);
            }
            var consumed = authority.Consume(issued.Token, prior.Plan, context);
            if (!consumed.IsValid)
            {
                return Reject(request.CorrelationId, consumed.Code);
            }
            if (!isSessionStillArmed(session))
            {
                return Reject(request.CorrelationId,
                    "agent.real_mode.disarmed_before_accept");
            }

            // The token is consumed before the durable Accepted transaction. If
            // that transaction fails, there has been no Windows call and the
            // confirmation must be repeated with a new token.
            var accepted = await plans.AcceptAsync(
                request.OperationId, request.PlanHash,
                Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(issued.Token.TokenId))).ToLowerInvariant(),
                timeProvider.GetUtcNow(), CancellationToken.None);
            handedToBackground = true;
            var worker = Task.Run(() => RunAcceptedAsync(accepted.Plan));
            runningTasks[request.OperationId] = worker;
            // The accepted job and its status read no longer depend on the
            // App's request token or pipe lifetime.
            return await StatusAsync(
                accepted, request.CorrelationId, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(request.CorrelationId);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            PersistedOperation? durable;
            try
            {
                durable = await plans.GetAsync(
                    request.OperationId, CancellationToken.None);
            }
            catch
            {
                return Unknown(request.CorrelationId);
            }

            if (durable is not null
                && StringComparer.Ordinal.Equals(
                    durable.Plan.PlanHash, request.PlanHash)
                && durable.State == PersistedOperationState.Accepted
                && !handedToBackground)
            {
                handedToBackground = true;
                var worker = Task.Run(() => RunAcceptedAsync(durable.Plan));
                runningTasks[request.OperationId] = worker;
                return await StatusAsync(
                    durable, request.CorrelationId, CancellationToken.None);
            }

            return durable is not null
                && StringComparer.Ordinal.Equals(
                    durable.Plan.PlanHash, request.PlanHash)
                    ? await StatusAsync(durable, request.CorrelationId,
                        CancellationToken.None)
                    : Reject(request.CorrelationId,
                        "agent.real_operation.accept_failed");
        }
        finally
        {
            if (!handedToBackground)
            {
                mutationGate.Release();
            }
        }
    }

    public async Task<ApplicationResult<AgentResponse>> QueryAsync(
        QueryAgentRealOperationRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken)
    {
        var operation = await plans.GetAsync(request.OperationId, cancellationToken);
        if (operation?.State == PersistedOperationState.OutcomeUnknown
            && backend is not null)
        {
            ScheduleReconciliation(operation);
        }
        return operation is null
            ? Reject(request.CorrelationId, "agent.real_operation.not_found")
            : await StatusAsync(operation, request.CorrelationId, cancellationToken);
    }

    public async Task<ApplicationResult<AgentResponse>> StopFollowingStepsAsync(
        StopAgentRealOperationFollowingStepsRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken)
    {
        var operation = await plans.GetAsync(request.OperationId, cancellationToken);
        if (operation is null || !StringComparer.Ordinal.Equals(
                operation.Plan.PlanHash, request.PlanHash))
        {
            return Reject(request.CorrelationId, "agent.real_operation.plan_mismatch");
        }

        if (operation.State is PersistedOperationState.Accepted
            or PersistedOperationState.Running)
        {
            stopAfterCurrentStep[request.OperationId] = 1;
        }
        else if (operation.State == PersistedOperationState.Prepared)
        {
            await plans.TransitionAsync(
                request.OperationId,
                PersistedOperationState.Prepared,
                PersistedOperationState.Cancelled,
                Event(request.OperationId, ExecutionEventKind.Cancelled,
                    "operation.cancelled_before_accept"), cancellationToken);
            operation = (await plans.GetAsync(request.OperationId, cancellationToken))!;
        }

        return await StatusAsync(operation, request.CorrelationId, cancellationToken);
    }

    public async Task<bool> HasRealWriteBarrierAsync(
        CancellationToken cancellationToken = default) =>
        Volatile.Read(ref recoveryReady) != 1
        || await plans.HasRealWriteBarrierAsync(cancellationToken);

    public async Task<bool> TryCloseAdmissionForShutdownAsync()
    {
        if (!await mutationGate.WaitAsync(0))
        {
            return false;
        }

        try
        {
            if (Volatile.Read(ref recoveryReady) != 1
                || await plans.HasRealWriteBarrierAsync(CancellationToken.None))
            {
                return false;
            }

            Volatile.Write(ref admissionClosed, 1);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private bool CanAcceptNewOperation() =>
        Volatile.Read(ref recoveryReady) == 1
        && Volatile.Read(ref admissionClosed) == 0;

    private bool CanEnter(TrustedRealSession session) =>
        session.IsArmed
        && session.IsWellFormed
        && backend is not null
        && CanAcceptNewOperation()
        && isAdministrator();

    private static bool IsCurrentAgentAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static WinPool.Execution.ExecutionContext CreateExecutionContext(
        OperationPlan plan,
        TrustedRealSession session,
        string machineBinding,
        RealStepPreflight preflight) =>
        new WinPool.Execution.ExecutionContext(
            new EnvironmentProfile(
                plan.EnvironmentId,
                EnvironmentKind.LocalMachine,
                machineBinding,
                ExecutionCapability.ReadInventory
                    | ExecutionCapability.MutateStorageStructure,
                IsUserProvidedDisposableEnvironment: false,
                plan.CreatedAt),
            ExecutionMode.Real,
            PrivilegeState.Administrator,
            machineBinding,
            preflight.InventoryVersion,
            IsReleaseBuild: true)
        {
            RealSession = session,
            CurrentTargetFingerprint = preflight.TargetFingerprint,
            CurrentPhysicalMemberFingerprint =
                preflight.PhysicalMemberFingerprint
        };

    private static bool IsExpectedFailure(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or Microsoft.Data.Sqlite.SqliteException;

    private static ApplicationResult<AgentResponse> Reject(
        CorrelationId correlationId,
        string code) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            new ApplicationMessage(code, code, string.Empty,
                ApplicationMessageSeverity.Warning, []));

    private static ApplicationResult<AgentResponse> Cancelled(
        CorrelationId correlationId) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Cancelled,
            correlationId,
            new ApplicationMessage("agent.real_operation.cancelled",
                "agent.real_operation.cancelled", string.Empty,
            ApplicationMessageSeverity.Information, []));

    private static ApplicationResult<AgentResponse> Unknown(
        CorrelationId correlationId) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.OutcomeUnknown,
            correlationId,
            new ApplicationMessage("agent.real_operation.outcome_unknown",
                "agent.real_operation.outcome_unknown", string.Empty,
                ApplicationMessageSeverity.Warning, []));

    private ExecutionEvent Event(
        OperationId operationId,
        ExecutionEventKind kind,
        string code) =>
        new(operationId, kind, timeProvider.GetUtcNow(), code, code);

    private async Task<ApplicationResult<AgentResponse>> StatusAsync(
        PersistedOperation operation,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var steps = await plans.GetStepsAsync(operation.Plan.OperationId, cancellationToken);
        var response = new AgentRealOperationResponse(
            operation.Plan,
            ToPublicState(operation.State),
            steps.Select(step => new RealOperationStepProgress(
                step.StepId,
                ToPublicState(step.State),
                null,
                step.TargetJson,
                step.EvidenceJson)).ToArray(),
            null,
            operation.State == PersistedOperationState.OutcomeUnknown);
        return ApplicationResult<AgentResponse>.Succeeded(response, correlationId);
    }

    private static RealOperationState ToPublicState(PersistedOperationState state) =>
        state switch
        {
            PersistedOperationState.Prepared => RealOperationState.Prepared,
            PersistedOperationState.Accepted => RealOperationState.Accepted,
            PersistedOperationState.Running => RealOperationState.Running,
            PersistedOperationState.Completed => RealOperationState.Succeeded,
            PersistedOperationState.Cancelled => RealOperationState.Cancelled,
            PersistedOperationState.Rejected => RealOperationState.Rejected,
            PersistedOperationState.Failed => RealOperationState.Failed,
            PersistedOperationState.PartiallyCompleted => RealOperationState.PartiallyCompleted,
            _ => RealOperationState.OutcomeUnknown
        };

    private static RealOperationStepState ToPublicState(PersistedOperationStepState state) =>
        state switch
        {
            PersistedOperationStepState.NotStarted => RealOperationStepState.Pending,
            PersistedOperationStepState.PreparingCall => RealOperationStepState.PreparingCall,
            PersistedOperationStepState.CallIssued => RealOperationStepState.CallIssued,
            PersistedOperationStepState.AwaitingProvider => RealOperationStepState.WaitingForProvider,
            PersistedOperationStepState.Verifying => RealOperationStepState.Verifying,
            PersistedOperationStepState.Verified => RealOperationStepState.Verified,
            PersistedOperationStepState.Failed => RealOperationStepState.Failed,
            PersistedOperationStepState.Skipped => RealOperationStepState.StoppedBeforeCall,
            _ => RealOperationStepState.OutcomeUnknown
        };

    private async Task RunAcceptedAsync(OperationPlan plan)
    {
        try
        {
            await RunStepsAsync(plan);
        }
        catch
        {
            // The durable Accepted/Running or CallIssued state remains a write
            // barrier. Recovery inspects it without replaying Windows calls.
        }
        finally
        {
            runningTasks.TryRemove(plan.OperationId, out _);
            mutationGate.Release();
        }
    }

    private void ScheduleReconciliation(PersistedOperation operation)
    {
        var task = reconciliationTasks.GetOrAdd(
            operation.Plan.OperationId,
            operationId => Task.Run(() => ReconcilePersistedAsync(operation.Plan)));
        _ = task.ContinueWith(
            completedTask => reconciliationTasks.TryRemove(
                operation.Plan.OperationId, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ReconcilePersistedAsync(OperationPlan plan)
    {
        try
        {
            var stored = await plans.GetStepsAsync(plan.OperationId);
            var progress = stored.Select(step => new RealOperationStepProgress(
                step.StepId, ToPublicState(step.State), null,
                step.TargetJson, step.EvidenceJson)).ToArray();
            var result = await backend!.ReconcileAsync(
                plan, progress, CancellationToken.None);
            if (!result.CanReleaseWriteBarrier)
            {
                return;
            }

            var byId = result.Steps.ToDictionary(
                step => step.StepId, StringComparer.Ordinal);
            if (byId.Count != stored.Count)
            {
                return;
            }

            foreach (var step in stored)
            {
                if (!byId.TryGetValue(step.StepId, out var observed))
                {
                    return;
                }

                if (step.State is PersistedOperationStepState.Verified
                    or PersistedOperationStepState.Failed
                    or PersistedOperationStepState.Skipped)
                {
                    continue;
                }

                var next = observed.State switch
                {
                    RealOperationStepState.Verified =>
                        PersistedOperationStepState.Verified,
                    RealOperationStepState.Failed =>
                        PersistedOperationStepState.Failed,
                    RealOperationStepState.StoppedBeforeCall
                        when step.State == PersistedOperationStepState.NotStarted =>
                        PersistedOperationStepState.Skipped,
                    _ => PersistedOperationStepState.OutcomeUnknown
                };
                if (next == PersistedOperationStepState.OutcomeUnknown
                    || (next != PersistedOperationStepState.Skipped
                        && string.IsNullOrWhiteSpace(observed.ResultEvidence)))
                {
                    return;
                }

                if (!await plans.TransitionStepAsync(
                        plan.OperationId, step.StepId,
                        step.State, next,
                        observed.TargetEvidence,
                        observed.ResultEvidence,
                        Event(plan.OperationId, ExecutionEventKind.Progress,
                            "operation.reconciled_step")))
                {
                    return;
                }
            }

            var finalSteps = await plans.GetStepsAsync(plan.OperationId);
            if (finalSteps.Any(step => step.State is not (
                    PersistedOperationStepState.Verified
                    or PersistedOperationStepState.Failed
                    or PersistedOperationStepState.Skipped)))
            {
                return;
            }

            var verified = finalSteps.Count(step =>
                step.State == PersistedOperationStepState.Verified);
            var terminal = result.State switch
            {
                RealOperationState.Succeeded when verified == finalSteps.Count =>
                    PersistedOperationState.Completed,
                RealOperationState.PartiallyCompleted when verified > 0 =>
                    PersistedOperationState.PartiallyCompleted,
                RealOperationState.Failed when verified == 0 =>
                    PersistedOperationState.Failed,
                RealOperationState.Cancelled when verified == 0
                    && finalSteps.All(step => step.State == PersistedOperationStepState.Skipped) =>
                    PersistedOperationState.Cancelled,
                _ => PersistedOperationState.OutcomeUnknown
            };
            if (terminal != PersistedOperationState.OutcomeUnknown)
            {
                await plans.TransitionAsync(
                    plan.OperationId,
                    PersistedOperationState.OutcomeUnknown,
                    terminal,
                    Event(plan.OperationId,
                        terminal switch
                        {
                            PersistedOperationState.Completed => ExecutionEventKind.Completed,
                            PersistedOperationState.Cancelled => ExecutionEventKind.Cancelled,
                            _ => ExecutionEventKind.Failed
                        },
                        result.Code));
            }
        }
        catch
        {
            // Keep the durable barrier. The next explicit query can retry a
            // read-only reconciliation against current facts.
        }
    }

    private async Task RunStepsAsync(OperationPlan plan)
    {
        var operationId = plan.OperationId;
        if (!RealOperationValidator.IsValid(plan)
            || !StringComparer.Ordinal.Equals(
                plan.PlanHash, OperationPlanHasher.Compute(plan)))
        {
            return; // Accepted remains a durable barrier; no Windows call.
        }
        if (!await plans.TransitionAsync(
                operationId, PersistedOperationState.Accepted,
                PersistedOperationState.Running,
                Event(operationId, ExecutionEventKind.Started,
                    "operation.running")))
        {
            return;
        }

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var verifiedCount = 0;
        var interrupted = false;
        foreach (var step in plan.RealOperation!.Steps)
        {
            if (stopAfterCurrentStep.ContainsKey(operationId))
            {
                interrupted = true;
                break;
            }

            RealStepPreflight preflight;
            try
            {
                preflight = await backend!.PreflightStepAsync(
                    plan, step, outputs, CancellationToken.None);
                if (!StringComparer.Ordinal.Equals(
                        preflight.PhysicalMemberFingerprint,
                        plan.RealOperation.PhysicalMemberFingerprint))
                {
                    throw new InvalidDataException(
                        "The physical member identity changed before a real step.");
                }
            }
            catch
            {
                await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.NotStarted,
                    PersistedOperationStepState.Failed,
                    null, "preflight_failed",
                    Event(operationId, ExecutionEventKind.Failed,
                        "operation.preflight_failed"));
                interrupted = true;
                break;
            }

            if (stopAfterCurrentStep.ContainsKey(operationId))
            {
                interrupted = true;
                break;
            }

            if (!await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.NotStarted,
                    PersistedOperationStepState.PreparingCall,
                    preflight.TargetEvidenceJson, null,
                    Event(operationId, ExecutionEventKind.Progress,
                        "operation.step.preparing_call")))
            {
                interrupted = true;
                break;
            }
            if (stopAfterCurrentStep.ContainsKey(operationId))
            {
                await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.PreparingCall,
                    PersistedOperationStepState.Skipped,
                    null, "stopped_before_call",
                    Event(operationId, ExecutionEventKind.Cancelled,
                        "operation.step.skipped"));
                interrupted = true;
                break;
            }
            if (!await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.PreparingCall,
                    PersistedOperationStepState.CallIssued,
                    preflight.TargetEvidenceJson, null,
                    Event(operationId, ExecutionEventKind.Progress,
                        "operation.step.call_issued")))
            {
                interrupted = true;
                break;
            }

            RealStepResult result;
            try
            {
                result = await backend!.ExecuteStepAsync(
                    plan, step, preflight, CancellationToken.None);
            }
            catch
            {
                await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.CallIssued,
                    PersistedOperationStepState.OutcomeUnknown,
                    null, "adapter_exception",
                    Event(operationId, ExecutionEventKind.Failed,
                        "operation.step.outcome_unknown"));
                interrupted = true;
                break;
            }

            if (result.Outcome == RealStepOutcome.OutcomeUnknown)
            {
                await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.CallIssued,
                    PersistedOperationStepState.OutcomeUnknown,
                    null, result.ResultEvidenceJson,
                    Event(operationId, ExecutionEventKind.Failed, result.Code));
                interrupted = true;
                break;
            }

            if (!await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.CallIssued,
                    PersistedOperationStepState.Verifying,
                    null, result.ResultEvidenceJson,
                    Event(operationId, ExecutionEventKind.Progress,
                        "operation.step.verifying")))
            {
                interrupted = true;
                break;
            }
            var next = result.Outcome == RealStepOutcome.Verified
                ? PersistedOperationStepState.Verified
                : PersistedOperationStepState.Failed;
            if (!await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.Verifying,
                    next,
                    null, result.ResultEvidenceJson,
                    Event(operationId,
                        next == PersistedOperationStepState.Verified
                            ? ExecutionEventKind.Progress
                            : ExecutionEventKind.Failed,
                        result.Code)))
            {
                interrupted = true;
                break;
            }

            if (next == PersistedOperationStepState.Failed)
            {
                interrupted = true;
                break;
            }

            verifiedCount++;
            outputs[step.Id] = result.ResultEvidenceJson;
        }

        var persistedSteps = await plans.GetStepsAsync(operationId);
        foreach (var step in persistedSteps.Where(step =>
                     step.State == PersistedOperationStepState.NotStarted))
        {
            await plans.TransitionStepAsync(
                operationId, step.StepId,
                PersistedOperationStepState.NotStarted,
                PersistedOperationStepState.Skipped,
                null, "stopped_after_previous_step",
                Event(operationId, ExecutionEventKind.Cancelled,
                    "operation.step.skipped"));
        }

        var progress = (await plans.GetStepsAsync(operationId))
            .Select(step => new RealOperationStepProgress(
                step.StepId, ToPublicState(step.State), null,
                step.TargetJson, step.EvidenceJson)).ToArray();
        RealReconciliationResult reconciled;
        try
        {
            reconciled = await backend!.ReconcileAsync(
                plan, progress, CancellationToken.None);
        }
        catch
        {
            reconciled = new(
                RealOperationState.OutcomeUnknown, progress,
                "operation.reconciliation_failed", false);
        }

        var terminal = reconciled.CanReleaseWriteBarrier
            ? reconciled.State switch
            {
                RealOperationState.Succeeded when !interrupted =>
                    PersistedOperationState.Completed,
                RealOperationState.PartiallyCompleted =>
                    PersistedOperationState.PartiallyCompleted,
                RealOperationState.Failed when verifiedCount == 0
                    && stopAfterCurrentStep.ContainsKey(operationId)
                    && progress.All(step => step.State == RealOperationStepState.StoppedBeforeCall) =>
                    PersistedOperationState.Cancelled,
                RealOperationState.Cancelled when verifiedCount == 0
                    && progress.All(step => step.State == RealOperationStepState.StoppedBeforeCall) =>
                    PersistedOperationState.Cancelled,
                RealOperationState.Failed when verifiedCount == 0 =>
                    PersistedOperationState.Failed,
                _ => PersistedOperationState.OutcomeUnknown
            }
            : PersistedOperationState.OutcomeUnknown;
        await plans.TransitionAsync(
            operationId, PersistedOperationState.Running, terminal,
            Event(operationId,
                terminal switch
                {
                    PersistedOperationState.Completed => ExecutionEventKind.Completed,
                    PersistedOperationState.Cancelled => ExecutionEventKind.Cancelled,
                    _ => ExecutionEventKind.Failed
                },
                reconciled.Code));
    }
}
