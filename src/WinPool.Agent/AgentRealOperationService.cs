using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent;

/// <summary>
/// Owns the short IPC requests and the Agent-lifetime execution task. Windows
/// calls are made only through the closed backend after durable acceptance.
/// </summary>
public sealed class AgentRealOperationService : IRealOperationService
{
    private readonly OperationPlanRepository plans;
    private readonly ExecutionEventRepository events;
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
    private IRealStorageEditObserver? editObserver;

    public void AttachEditObserver(IRealStorageEditObserver observer) =>
        editObserver = observer ?? throw new ArgumentNullException(nameof(observer));

    public async Task PublishRecoveredEditStatesAsync()
    {
        foreach (var operation in await plans.ListUnfinishedAsync(CancellationToken.None).ConfigureAwait(false))
            if (operation.Plan.RealOperation is not null)
                await NotifyEditObserverAsync(operation.Plan.OperationId).ConfigureAwait(false);
    }

    public AgentRealOperationService(
        OperationPlanRepository plans,
        ExecutionEventRepository events,
        IRealStorageBackend? backend,
        IRealMachineIdentityProvider machineIdentity,
        AgentRealModeGate realModeGate,
        IOperationAuthority? authority = null,
        TimeProvider? timeProvider = null)
        : this(plans, events, backend, machineIdentity, authority, timeProvider,
            realModeGate.IsSessionArmed,
            IsCurrentAgentAdministrator)
    {
    }

    internal AgentRealOperationService(
        OperationPlanRepository plans,
        ExecutionEventRepository events,
        IRealStorageBackend? backend,
        IRealMachineIdentityProvider machineIdentity,
        IOperationAuthority? authority,
        TimeProvider? timeProvider,
        Func<TrustedRealSession, bool> isSessionStillArmed,
        Func<bool> isAdministrator)
    {
        this.plans = plans ?? throw new ArgumentNullException(nameof(plans));
        this.events = events ?? throw new ArgumentNullException(nameof(events));
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
            if (await plans.HasRealWriteBarrierAsync(cancellationToken))
                return Reject(request.CorrelationId, "agent.real_operation.write_barrier");
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

    public async Task<ApplicationResult<AgentResponse>> QueryStructureCreationSupportAsync(
        QueryAgentRealStructureCreationSupportRequest request, TrustedRealSession session,
        CancellationToken cancellationToken)
    {
        if (request.PhysicalTarget.Kind != StorageObjectKind.PhysicalDisk
            || request.PhysicalTarget.System.Value == Guid.Empty || string.IsNullOrWhiteSpace(request.PhysicalTarget.ProviderKey))
            return Reject(request.CorrelationId, "agent.real_creation.invalid_target");
        if (!CanEnter(session) || !isSessionStillArmed(session) || request.ProductSessionId != session.ProductSessionId)
            return Reject(request.CorrelationId, "agent.real_mode.administrator_required");
        try
        {
            var support = await backend!.ReadStructureCreationSupportAsync(request.PhysicalTarget,
                request.Tiered, session, cancellationToken);
            if (support.PhysicalTarget != request.PhysicalTarget || support.Tiered != request.Tiered
                || string.IsNullOrWhiteSpace(support.TargetFingerprint) || support.CapturedAtUtc == default)
                return Reject(request.CorrelationId, "agent.real_creation.invalid_support");
            return ApplicationResult<AgentResponse>.Succeeded(new AgentRealStructureCreationSupportResponse(support), request.CorrelationId);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return Reject(request.CorrelationId, "agent.real_creation.unsupported_layout", exception.Message);
        }
    }

    public async Task<ApplicationResult<AgentResponse>> QueryVirtualDiskCreationRangeAsync(
        QueryAgentRealVirtualDiskCreationRangeRequest request,
        TrustedRealSession session, CancellationToken cancellationToken)
    {
        if (request.Target.System.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Target.ProviderKey)
            || request.Target.Kind is not (StorageObjectKind.StoragePool or StorageObjectKind.StorageTier))
            return Reject(request.CorrelationId, "agent.real_creation.invalid_target");
        if (!CanEnter(session) || !isSessionStillArmed(session)
            || request.ProductSessionId != session.ProductSessionId)
            return Reject(request.CorrelationId, "agent.real_mode.administrator_required");
        try
        {
            var range = await backend!.ReadVirtualDiskCreationRangeAsync(
                request.Target, session, cancellationToken);
            if (range.Target != request.Target || string.IsNullOrWhiteSpace(range.TargetFingerprint)
                || range.CapturedAtUtc == default || !range.Supports(range.ResolveMaximum()))
                return Reject(request.CorrelationId, "agent.real_creation.invalid_range");
            return ApplicationResult<AgentResponse>.Succeeded(
                new AgentRealVirtualDiskCreationRangeResponse(range), request.CorrelationId);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return Reject(request.CorrelationId, "agent.real_creation.range_unavailable", exception.Message);
        }
    }

    public async Task<ApplicationResult<AgentResponse>> ListRecoverableAsync(
        ListAgentRecoverableRealOperationsRequest request,
        TrustedRealSession session, CancellationToken cancellationToken)
    {
        var responses = new List<AgentRealOperationResponse>();
        foreach (var operation in await plans.ListUnfinishedAsync(cancellationToken))
        {
            if (operation.Plan.RealOperation is null) continue;
            if (operation.State == PersistedOperationState.OutcomeUnknown && backend is not null)
                ScheduleReconciliation(operation);
            var result = await StatusAsync(operation, request.CorrelationId, cancellationToken);
            if (result.Value is AgentRealOperationResponse status) responses.Add(status);
        }
        return ApplicationResult<AgentResponse>.Succeeded(
            new AgentRecoverableRealOperationsResponse(responses), request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> QueryPartitionResizeRangeAsync(
        QueryAgentRealPartitionResizeRangeRequest request,
        TrustedRealSession session,
        CancellationToken cancellationToken)
    {
        if (request.Partition.Kind != StorageObjectKind.Partition ||
            request.Partition.System.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Partition.ProviderKey))
            return Reject(request.CorrelationId, "agent.real_resize.invalid_partition");
        if (!CanEnter(session) ||
            !StringComparer.Ordinal.Equals(request.ProductSessionId, session.ProductSessionId) ||
            !isSessionStillArmed(session))
            return Reject(request.CorrelationId, "agent.real_mode.administrator_required");

        try
        {
            var range = await backend!.ReadPartitionResizeRangeAsync(
                request.Partition, session, cancellationToken);
            if (range.Partition != request.Partition ||
                range.CurrentSizeBytes <= 0 ||
                range.ProviderMinBytes <= 0 ||
                range.ProviderMaxBytes < range.ProviderMinBytes ||
                range.AllowedMinBytes < range.ProviderMinBytes ||
                range.AllowedMaxBytes > range.ProviderMaxBytes ||
                range.AllowedMinBytes > range.AllowedMaxBytes ||
                string.IsNullOrWhiteSpace(range.TargetFingerprint) ||
                string.IsNullOrWhiteSpace(range.Code))
                return Reject(request.CorrelationId, "agent.real_resize.invalid_range");
            return ApplicationResult<AgentResponse>.Succeeded(
                new AgentRealPartitionResizeRangeResponse(range),
                request.CorrelationId);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or ArgumentException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException)
        {
            return Reject(request.CorrelationId, "agent.real_resize.range_unavailable");
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
            var plan = await StorageOperationTiming.MeasureAsync("real.prepare", () => backend.PrepareAsync(
                request.Intent, session, operationId, cancellationToken), operationId);
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
        // A planner capability refusal has no prepared plan or dispatched write.
        // Return it to the caller instead of letting it tear down the IPC request.
        catch (Exception exception) when (IsExpectedFailure(exception)
            || exception is NotSupportedException)
        {
            return Reject(request.CorrelationId,
                "agent.real_operation.prepare_failed",
                $"{exception.GetType().Name}: {exception.Message}");
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
            var preflight = await StorageOperationTiming.MeasureAsync("real.accept.preflight", () => backend.PreflightStepAsync(
                prior.Plan, firstStep,
                new Dictionary<string, string>(), cancellationToken), prior.Plan.OperationId, firstStep.Id);
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
            await NotifyEditObserverAsync(accepted.Plan.OperationId);
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
            or NotSupportedException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or Microsoft.Data.Sqlite.SqliteException;

    private static ApplicationResult<AgentResponse> Reject(
        CorrelationId correlationId,
        string code,
        string diagnostic = "") =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            new ApplicationMessage(code, code, diagnostic,
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
        string code,
        string? stepId = null) =>
        new(operationId, kind, timeProvider.GetUtcNow(), code, stepId ?? code);

    private async Task<ApplicationResult<AgentResponse>> StatusAsync(
        PersistedOperation operation,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var steps = await plans.GetStepsAsync(operation.Plan.OperationId, cancellationToken);
        var history = await events.ListAsync(operation.Plan.OperationId, cancellationToken);
        var stepCodes = LatestStepCodes(steps, history);
        var response = new AgentRealOperationResponse(
            operation.Plan,
            ToPublicState(operation.State),
            steps.Select(step => new RealOperationStepProgress(
                step.StepId,
                ToPublicState(step.State),
                stepCodes.GetValueOrDefault(step.StepId),
                step.TargetJson,
                step.EvidenceJson)).ToArray(),
            history.LastOrDefault()?.Event.Code,
            operation.State == PersistedOperationState.OutcomeUnknown);
        if (editObserver is { } observer)
        {
            try { await observer.ObserveAsync(response, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                DiagnosticLog.AppendFailure(StorageDataLocations.CurrentRoot,
                    "monitor-edit.jsonl", "monitor.edit.observer_failed", exception);
            }
        }
        return ApplicationResult<AgentResponse>.Succeeded(response, correlationId);
    }

    private async Task NotifyEditObserverAsync(OperationId operationId)
    {
        if (editObserver is null) return;
        try
        {
            var durable = await plans.GetAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            if (durable is not null)
                _ = await StatusAsync(durable, CorrelationId.New(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DiagnosticLog.AppendFailure(StorageDataLocations.CurrentRoot,
                "monitor-edit.jsonl", "monitor.edit.durable_status_unavailable", exception);
        }
    }

    private static IReadOnlyDictionary<string, string> LatestStepCodes(
        IReadOnlyList<PersistedOperationStep> steps,
        IReadOnlyList<PersistedExecutionEvent> history)
    {
        var known = steps.Select(step => step.StepId).ToHashSet(StringComparer.Ordinal);
        return history.Where(item => known.Contains(item.Event.Message))
            .GroupBy(item => item.Event.Message, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Event.Code,
                StringComparer.Ordinal);
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
            await NotifyEditObserverAsync(plan.OperationId);
            runningTasks.TryRemove(plan.OperationId, out _);
            mutationGate.Release();
        }
    }

    private void ScheduleReconciliation(PersistedOperation operation)
    {
        // GetOrAdd's factory may run more than once. Start exactly one read-only
        // reconciler so concurrent queries cannot race durable step transitions.
        lock (reconciliationTasks)
        {
            var operationId = operation.Plan.OperationId;
            // An Unknown durable state can be visible before its original
            // worker finishes. Never reconcile concurrently with that worker.
            if (runningTasks.TryGetValue(operationId, out var worker) && !worker.IsCompleted)
                return;
            if (reconciliationTasks.ContainsKey(operationId))
                return;
            var task = Task.Run(() => ReconcilePersistedAsync(operation.Plan));
            reconciliationTasks[operationId] = task;
            _ = task.ContinueWith(completedTask =>
            {
                lock (reconciliationTasks)
                    reconciliationTasks.TryRemove(operationId, out _);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ReconcilePersistedAsync(OperationPlan plan)
    {
        try
        {
            var stored = await plans.GetStepsAsync(plan.OperationId);
            var history = await events.ListAsync(plan.OperationId);
            var stepCodes = LatestStepCodes(stored, history);
            var progress = stored.Select(step => new RealOperationStepProgress(
                step.StepId, ToPublicState(step.State),
                stepCodes.GetValueOrDefault(step.StepId),
                step.TargetJson, step.EvidenceJson)).ToArray();
            var result = await ReconcilePlanAsync(plan, progress);
            if (!result.CanReleaseWriteBarrier)
            {
                return;
            }

            var terminal = await PersistReconciledStepsAsync(plan, stored, result);
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
        finally
        {
            await NotifyEditObserverAsync(plan.OperationId).ConfigureAwait(false);
        }
    }

    // A reconciled operation may finish only after its observed step states and
    // evidence are durable. Both live execution and restart recovery use this
    // path; the repository remains the authority for legal/CAS transitions.
    private async Task<PersistedOperationState> PersistReconciledStepsAsync(
        OperationPlan plan,
        IReadOnlyList<PersistedOperationStep> stored,
        RealReconciliationResult result)
    {
        if (!result.CanReleaseWriteBarrier
            || result.Steps.Count != stored.Count
            || result.Steps.Select(step => step.StepId).Distinct(StringComparer.Ordinal).Count() != stored.Count)
            return PersistedOperationState.OutcomeUnknown;

        var byId = result.Steps.ToDictionary(step => step.StepId, StringComparer.Ordinal);
        foreach (var step in stored)
        {
            if (!byId.TryGetValue(step.StepId, out var observed))
                return PersistedOperationState.OutcomeUnknown;

            var next = observed.State switch
            {
                RealOperationStepState.Verified => PersistedOperationStepState.Verified,
                RealOperationStepState.Failed => PersistedOperationStepState.Failed,
                RealOperationStepState.StoppedBeforeCall => PersistedOperationStepState.Skipped,
                _ => PersistedOperationStepState.OutcomeUnknown
            };
            if (next == PersistedOperationStepState.OutcomeUnknown)
                return PersistedOperationState.OutcomeUnknown;

            if (step.State is PersistedOperationStepState.Verified
                or PersistedOperationStepState.Failed or PersistedOperationStepState.Skipped)
            {
                // Reconciliation cannot rewrite or contradict a durable terminal step.
                if (next != step.State || (next != PersistedOperationStepState.Skipped
                    && string.IsNullOrWhiteSpace(step.EvidenceJson)))
                    return PersistedOperationState.OutcomeUnknown;
                continue;
            }

            if ((next == PersistedOperationStepState.Skipped
                    && step.State != PersistedOperationStepState.NotStarted)
                || (next == PersistedOperationStepState.Verified
                    && step.State == PersistedOperationStepState.NotStarted)
                || (next != PersistedOperationStepState.Skipped
                    && string.IsNullOrWhiteSpace(observed.ResultEvidence)))
                return PersistedOperationState.OutcomeUnknown;

            if (!await plans.TransitionStepAsync(
                    plan.OperationId, step.StepId, step.State, next,
                    observed.TargetEvidence ?? step.TargetJson, observed.ResultEvidence,
                    Event(plan.OperationId, ExecutionEventKind.Progress,
                        string.IsNullOrWhiteSpace(observed.Code)
                            ? "operation.reconciled_step" : observed.Code, step.StepId)))
                return PersistedOperationState.OutcomeUnknown;
        }

        var finalSteps = await plans.GetStepsAsync(plan.OperationId);
        if (finalSteps.Count != stored.Count || finalSteps.Any(step => step.State is not (
                PersistedOperationStepState.Verified or PersistedOperationStepState.Failed
                or PersistedOperationStepState.Skipped)))
            return PersistedOperationState.OutcomeUnknown;

        var verified = finalSteps.Count(step => step.State == PersistedOperationStepState.Verified);
        var stopped = finalSteps.All(step => step.State == PersistedOperationStepState.Skipped);
        return result.State switch
        {
            RealOperationState.Succeeded when verified == finalSteps.Count => PersistedOperationState.Completed,
            RealOperationState.PartiallyCompleted when verified > 0 => PersistedOperationState.PartiallyCompleted,
            RealOperationState.Failed when verified == 0 && stopped
                && stopAfterCurrentStep.ContainsKey(plan.OperationId) => PersistedOperationState.Cancelled,
            RealOperationState.Failed when verified == 0 => PersistedOperationState.Failed,
            RealOperationState.Cancelled when verified == 0 && stopped => PersistedOperationState.Cancelled,
            _ => PersistedOperationState.OutcomeUnknown
        };
    }

    private static bool IsMaximumCapacityMacro(RealOperationStep step) => step.Command is
        CreateVirtualDiskCommand { UseMaximumSize: true, MaximumCapacity: not null }
        or CreateTieredVirtualDiskCommand { UseMaximumSize: true, MaximumCapacity: not null };

    private async Task<RealStepResult> ExecuteAcceptedStepAsync(OperationPlan plan,
        RealOperationStep step, RealStepPreflight preflight)
    {
        if (!IsMaximumCapacityMacro(step))
            return await backend!.ExecuteStepAsync(plan, step, preflight, CancellationToken.None);
        if (backend is not IMaximumCapacitySearchBackend maximum)
            return new(RealStepOutcome.OutcomeUnknown, "operation.maximum.backend_unavailable",
                "{\"NotVerified\":true,\"Reason\":\"capacity_backend_unavailable\"}");
        var journal = new GuardedMaximumCapacityJournal(plans.CreateMaximumCapacityJournal(
            plan.OperationId, step.Id, () => stopAfterCurrentStep.ContainsKey(plan.OperationId)));
        // The parent CallIssued is durable before entry. The backend must also
        // persist each candidate and its own CallIssued before invoking Windows.
        var result = await maximum.ExecuteMaximumCapacitySearchAsync(plan, step, preflight,
            journal, CancellationToken.None);
        var attempts = await journal.ReadAsync(CancellationToken.None);
        if (journal.MutationRejected || attempts.Any(attempt => attempt.State is
                MaximumCapacityAttemptState.PreparingCall or MaximumCapacityAttemptState.CallIssued
                    or MaximumCapacityAttemptState.OutcomeUnknown)
            || result.Outcome == RealStepOutcome.Verified &&
                (attempts.Count == 0 || !attempts.Any(attempt => attempt.State == MaximumCapacityAttemptState.Verified)
                 || attempts.Any(attempt => attempt.State == MaximumCapacityAttemptState.FailedWithoutCall)
                 || !CapacityJournalProvesMaximum(step, attempts)))
            return new(RealStepOutcome.OutcomeUnknown, "operation.maximum.attempt_not_durable",
                JsonSerializer.Serialize(new { NotVerified = true, Attempts = attempts }));
        return result;
    }

    private async Task<RealReconciliationResult> ReconcilePlanAsync(OperationPlan plan,
        IReadOnlyList<RealOperationStepProgress> progress)
    {
        var macros = plan.RealOperation!.Steps.Where(IsMaximumCapacityMacro).ToArray();
        if (macros.Length == 0)
            return await backend!.ReconcileAsync(plan, progress, CancellationToken.None);
        // Creation plans currently contain one macro; layer attempts are its
        // journal children, never mutable additions to the frozen plan/hash.
        if (macros.Length != 1 || plan.RealOperation.Steps.Count != 1 || progress.Count != 1
            || backend is not IMaximumCapacitySearchBackend maximum)
            return new(RealOperationState.OutcomeUnknown, progress,
                "operation.maximum.recovery_shape_unavailable", false);
        var attempts = await plans.ReadMaximumCapacityAttemptsAsync(plan.OperationId, macros[0].Id);
        // A snapshot, not a writer: Query/startup/final reconciliation cannot
        // continue a capacity search or replay an issued child attempt.
        var result = await maximum.ReconcileMaximumCapacitySearchAsync(plan, macros[0], progress[0],
            attempts, CancellationToken.None);
        return result.State == RealOperationState.Succeeded && !CapacityJournalProvesMaximum(macros[0], attempts)
            ? new(RealOperationState.OutcomeUnknown, progress, "operation.maximum.boundary_not_durable", false)
            : result;
    }

    private static bool CapacityJournalProvesMaximum(RealOperationStep step,
        IReadOnlyList<MaximumCapacityAttemptRecord> attempts)
    {
        try
        {
            var policies = new Dictionary<string, MaximumCapacityPolicy>(StringComparer.Ordinal);
            var seeds = new Dictionary<string, long>(StringComparer.Ordinal);
            if (step.Command is CreateTieredVirtualDiskCommand { CapacityTiers: { Count: > 1 } tiers })
            {
                var seed = attempts.FirstOrDefault();
                if (seed?.Attempt is not { Phase: MaximumCapacityAttemptPhase.Seed, Ordinal: 1, SearchTargetKey: "seed" }
                    || seed.State != MaximumCapacityAttemptState.Verified
                    || seed.Result is not { SeedSuccessfulBytes: { } sizes } seedResult || sizes.Count != tiers.Count) return false;
                long total = 0;
                foreach (var tier in tiers)
                {
                    if (tier.Tier.Existing is not { } id || tier.MaximumCapacity is not { } policy
                        || !sizes.TryGetValue(id.ProviderKey, out var bytes)
                        || bytes != MaximumCapacityAlgorithm.SeedBytes(policy.InitialCandidateBytes) || bytes <= 0)
                        return false;
                    policies.Add(id.ProviderKey, policy);
                    seeds.Add(id.ProviderKey, bytes);
                    total = checked(total + bytes);
                }
                if (seed.Attempt.CandidateBytes != total || seedResult.LastSuccessfulBytes != total) return false;
            }
            else
            {
                var policy = step.Command switch
                {
                    CreateVirtualDiskCommand value => value.MaximumCapacity,
                    CreateTieredVirtualDiskCommand value => value.MaximumCapacity,
                    _ => null
                };
                if (policy is null) return false;
                var key = step.Command is CreateTieredVirtualDiskCommand { Tier.Existing: { } tierId }
                    ? tierId.ProviderKey : "virtual-disk";
                policies.Add(key, policy);
            }
            if (attempts.Select((row, index) => row.Attempt.Ordinal == index + 1).Any(valid => !valid)
                || attempts.Count(row => row.Attempt.Phase == MaximumCapacityAttemptPhase.Seed) != (seeds.Count > 0 ? 1 : 0)
                || attempts.Any(row => row.Attempt.Phase != MaximumCapacityAttemptPhase.Seed
                    && !policies.ContainsKey(row.Attempt.SearchTargetKey))) return false;
            var order = new List<string>();
            foreach (var row in attempts.Where(row => row.Attempt.Phase != MaximumCapacityAttemptPhase.Seed))
                if (order.Count == 0 || order[^1] != row.Attempt.SearchTargetKey)
                    order.Add(row.Attempt.SearchTargetKey);
            if (!order.SequenceEqual(policies.Keys, StringComparer.Ordinal)) return false;
            foreach (var (key, policy) in policies)
            {
                var state = MaximumCapacitySearchState.Start(policy.InitialCandidateBytes, seeds.GetValueOrDefault(key));
                var rows = attempts.Where(row => row.Attempt.SearchTargetKey == key).ToArray();
                if (rows.Length == 0 || rows.Length > policy.MaximumAttempts) return false;
                foreach (var row in rows)
                {
                    if (state.IsComplete || row.Attempt.CandidateBytes != state.CandidateBytes
                        || row.Attempt.LastSuccessfulBytes != state.LastSuccessfulBytes
                        || row.State is not (MaximumCapacityAttemptState.Verified or MaximumCapacityAttemptState.CapacityRejectedUnchanged)
                        || row.Attempt.Phase != (state.LastSuccessfulBytes == 0
                            ? MaximumCapacityAttemptPhase.Create : MaximumCapacityAttemptPhase.Resize)) return false;
                    state.Observe(row.State == MaximumCapacityAttemptState.Verified);
                    if (row.Result is not { } observed || observed.State != row.State
                        || observed.LastSuccessfulBytes != state.LastSuccessfulBytes) return false;
                }
                if (!state.HasMaximum) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    private sealed class GuardedMaximumCapacityJournal(IMaximumCapacityAttemptJournal inner)
        : IMaximumCapacityAttemptJournal
    {
        private int rejected;
        public bool MutationRejected => Volatile.Read(ref rejected) != 0;
        public bool IsStopRequested => inner.IsStopRequested;
        public Task<IReadOnlyList<MaximumCapacityAttemptRecord>> ReadAsync(CancellationToken cancellationToken) =>
            inner.ReadAsync(cancellationToken);
        public Task<bool> PrepareAsync(MaximumCapacityAttempt attempt, CancellationToken cancellationToken) =>
            GuardAsync(() => inner.PrepareAsync(attempt, cancellationToken));
        public Task<bool> MarkCallIssuedAsync(int ordinal, CancellationToken cancellationToken) =>
            GuardAsync(() => inner.MarkCallIssuedAsync(ordinal, cancellationToken));
        public Task<bool> CompleteAsync(int ordinal, MaximumCapacityAttemptResult result, CancellationToken cancellationToken) =>
            GuardAsync(() => inner.CompleteAsync(ordinal, result, cancellationToken));
        private async Task<bool> GuardAsync(Func<Task<bool>> action)
        {
            try
            {
                var accepted = await action();
                if (!accepted) Interlocked.Exchange(ref rejected, 1);
                return accepted;
            }
            catch
            {
                Interlocked.Exchange(ref rejected, 1);
                throw;
            }
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
        foreach (var step in plan.RealOperation!.Steps)
        {
            if (stopAfterCurrentStep.ContainsKey(operationId))
            {
                break;
            }

            RealStepPreflight preflight;
            try
            {
                preflight = await StorageOperationTiming.MeasureAsync("real.step.preflight", () => backend!.PreflightStepAsync(
                    plan, step, outputs, CancellationToken.None), operationId, step.Id);
                if (!StringComparer.Ordinal.Equals(
                        preflight.PhysicalMemberFingerprint,
                        plan.RealOperation.PhysicalMemberFingerprint))
                {
                    throw new InvalidDataException(
                        "The physical member identity changed before a real step.");
                }
            }
            catch (Exception exception)
            {
                var exceptionType = exception.GetType().FullName
                    ?? exception.GetType().Name;
                var diagnostic = $"{exceptionType}: {exception.Message}";
                if (diagnostic.Length > 2048)
                    diagnostic = diagnostic[..2048];
                var noEffectEvidence = JsonSerializer.Serialize(
                    new WindowsNoEffectStepEvidence(
                        true,
                        "operation.preflight_failed",
                        plan.RealOperation!.PhysicalMemberFingerprint,
                        diagnostic));
                await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.NotStarted,
                    PersistedOperationStepState.Failed,
                    null, noEffectEvidence,
                    Event(operationId, ExecutionEventKind.Failed,
                        "operation.preflight_failed", step.Id));
                break;
            }

            if (stopAfterCurrentStep.ContainsKey(operationId))
            {
                break;
            }

            if (!await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.NotStarted,
                    PersistedOperationStepState.PreparingCall,
                    preflight.TargetEvidenceJson, null,
                    Event(operationId, ExecutionEventKind.Progress,
                        "operation.step.preparing_call", step.Id)))
            {
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
                        "operation.step.skipped", step.Id));
                break;
            }
            if (!await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.PreparingCall,
                    PersistedOperationStepState.CallIssued,
                    preflight.TargetEvidenceJson, null,
                    Event(operationId, ExecutionEventKind.Progress,
                        "operation.step.call_issued", step.Id)))
            {
                break;
            }

            RealStepResult result;
            await NotifyEditObserverAsync(operationId);
            try
            {
                result = await StorageOperationTiming.MeasureAsync("real.step.execute", () => ExecuteAcceptedStepAsync(
                    plan, step, preflight), operationId, step.Id);
            }
            catch
            {
                await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.CallIssued,
                    PersistedOperationStepState.OutcomeUnknown,
                    null, "adapter_exception",
                    Event(operationId, ExecutionEventKind.Failed,
                        "operation.step.outcome_unknown", step.Id));
                break;
            }

            if (result.Outcome == RealStepOutcome.OutcomeUnknown)
            {
                await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.CallIssued,
                    PersistedOperationStepState.OutcomeUnknown,
                    null, result.ResultEvidenceJson,
                    Event(operationId, ExecutionEventKind.Failed, result.Code, step.Id));
                break;
            }

            if (!await plans.TransitionStepAsync(
                    operationId, step.Id,
                    PersistedOperationStepState.CallIssued,
                    PersistedOperationStepState.Verifying,
                    null, result.ResultEvidenceJson,
                    Event(operationId, ExecutionEventKind.Progress,
                        "operation.step.verifying", step.Id)))
            {
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
                        result.Code, step.Id)))
            {
                break;
            }

            if (next == PersistedOperationStepState.Failed)
            {
                break;
            }

            outputs[step.Id] = result.ResultEvidenceJson;
            await NotifyEditObserverAsync(operationId);
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
                    "operation.step.skipped", step.StepId));
        }

        var finalStepRecords = await plans.GetStepsAsync(operationId);
        var finalEvents = await events.ListAsync(operationId);
        var finalStepCodes = LatestStepCodes(finalStepRecords, finalEvents);
        var progress = finalStepRecords
            .Select(step => new RealOperationStepProgress(
                step.StepId, ToPublicState(step.State),
                finalStepCodes.GetValueOrDefault(step.StepId),
                step.TargetJson, step.EvidenceJson)).ToArray();
        RealReconciliationResult reconciled;
        try
        {
            reconciled = await ReconcilePlanAsync(plan, progress);
        }
        catch
        {
            reconciled = new(
                RealOperationState.OutcomeUnknown, progress,
                "operation.reconciliation_failed", false);
        }

        PersistedOperationState terminal;
        try
        {
            terminal = await PersistReconciledStepsAsync(plan, finalStepRecords, reconciled);
            if (terminal == PersistedOperationState.OutcomeUnknown && reconciled.CanReleaseWriteBarrier)
                reconciled = reconciled with { Code = "operation.reconciled_steps_persistence_conflict" };
        }
        catch
        {
            terminal = PersistedOperationState.OutcomeUnknown;
            reconciled = reconciled with { Code = "operation.reconciled_steps_persistence_failed" };
        }
        if (!await plans.TransitionAsync(
                operationId, PersistedOperationState.Running, terminal,
                Event(operationId,
                    terminal switch
                    {
                        PersistedOperationState.Completed => ExecutionEventKind.Completed,
                        PersistedOperationState.Cancelled => ExecutionEventKind.Cancelled,
                        _ => ExecutionEventKind.Failed
                    },
                    reconciled.Code)))
        {
            // A stale/failed terminal CAS is not success. Leave an explicit,
            // query-recoverable barrier; never replay an issued Windows call.
            await plans.TransitionAsync(
                operationId, PersistedOperationState.Running,
                PersistedOperationState.OutcomeUnknown,
                Event(operationId, ExecutionEventKind.Failed,
                    "operation.reconciled_transition_conflict"));
        }
    }
}
