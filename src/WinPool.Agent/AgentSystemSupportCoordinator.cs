using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Agent;

internal sealed class AgentSystemSupportCoordinator
{
    private readonly AgentInstanceId instanceId;
    private readonly SystemSupportAuditRepository auditRepository;
    private readonly Func<
        ElevatedBrokerExecutionRequest,
        CorrelationId,
        CancellationToken,
        Task<ElevatedBrokerExecutionResult>> executeElevated;
    private readonly SystemSupportReviewStore reviews = new();

    public AgentSystemSupportCoordinator(
        AgentInstanceId instanceId,
        SystemSupportAuditRepository auditRepository,
        Func<
            ElevatedBrokerExecutionRequest,
            CorrelationId,
            CancellationToken,
            Task<ElevatedBrokerExecutionResult>> executeElevated)
    {
        this.instanceId = instanceId;
        this.auditRepository = auditRepository
            ?? throw new ArgumentNullException(nameof(auditRepository));
        this.executeElevated = executeElevated
            ?? throw new ArgumentNullException(nameof(executeElevated));
    }

    public async Task<ApplicationResult<AgentResponse>> ReviewAsync(
        ReviewAgentSystemSupportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(2);
        var candidate = request.ExecutionRequest with
        {
            Nonce = Guid.NewGuid(),
            AgentSessionId = instanceId.Value,
            AgentProcessId = Environment.ProcessId,
            UserSidHash = new string('a', 64),
            ExpiresAtUtc = now.AddMinutes(1)
        };
        var rejection = ElevatedBrokerExecutionValidator.Validate(
            candidate,
            candidate.Nonce,
            instanceId.Value,
            Environment.ProcessId,
            candidate.UserSidHash,
            now);
        if (rejection is not null)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Rejected,
                request.CorrelationId,
                Message(rejection, ApplicationMessageSeverity.Error));
        }

        var execution = request.ExecutionRequest with
        {
            Nonce = Guid.Empty,
            AgentSessionId = Guid.Empty,
            AgentProcessId = 0,
            UserSidHash = string.Empty,
            ExpiresAtUtc = expires
        };
        var review = reviews.Create(execution, now, TimeSpan.FromMinutes(2));
        var actionKind = ToSystemSupportActionKind(execution.Operation);
        await WriteAuditAsync(
            execution,
            request.CorrelationId,
            actionKind,
            SystemSupportAuditStage.Review,
            "system-support.review-ready",
            cancellationToken).ConfigureAwait(false);
        var candidates = execution.TemporaryCleanupCandidates ?? [];
        var warningCode = candidates.Count ==
                          ElevatedBrokerExecutionValidator.MaximumTemporaryCleanupCandidates
            ? "system-support.warning.candidate-batch-limit"
            : $"system-support.warning.{execution.Operation}";
        return ApplicationResult<AgentResponse>.Succeeded(
            new SystemSupportReviewResponse(
                review.ReviewId,
                execution.Operation,
                execution.PlanHash,
                expires,
                candidates.Count,
                candidates.Sum(item => item.Length),
                warningCode),
            request.CorrelationId);
    }

    public async Task<ApplicationResult<AgentResponse>> ExecuteAsync(
        ExecuteAgentSystemSupportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!reviews.TryTake(
                request.ReviewId,
                DateTimeOffset.UtcNow,
                out var pending,
                out var reviewCode))
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                reviewCode == "system-support.review-expired"
                    ? ApplicationStatus.RequiresAuthorization
                    : ApplicationStatus.Rejected,
                request.CorrelationId,
                Message(reviewCode, ApplicationMessageSeverity.Error));
        }

        var executionRequest = pending!.ExecutionRequest;
        var actionKind = ToSystemSupportActionKind(executionRequest.Operation);
#if DEBUG
        const bool confirmationRequired = false;
#else
        const bool confirmationRequired = true;
#endif
        if (pending.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || (confirmationRequired && !request.UserConfirmed))
        {
            var code = pending.ExpiresAtUtc <= DateTimeOffset.UtcNow
                ? "system-support.review-expired"
                : "system-support.release-confirmation-required";
            await WriteAuditAsync(
                executionRequest,
                request.CorrelationId,
                actionKind,
                SystemSupportAuditStage.Rejected,
                code,
                CancellationToken.None).ConfigureAwait(false);
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.RequiresAuthorization,
                request.CorrelationId,
                Message(code, ApplicationMessageSeverity.Warning));
        }

        try
        {
            await WriteAuditAsync(
                executionRequest,
                request.CorrelationId,
                actionKind,
                SystemSupportAuditStage.Started,
                "system-support.broker-started",
                cancellationToken).ConfigureAwait(false);
            var result = await executeElevated(
                executionRequest,
                request.CorrelationId,
                cancellationToken).ConfigureAwait(false);
            await WriteAuditAsync(
                executionRequest,
                request.CorrelationId,
                actionKind,
                result.Succeeded
                    ? SystemSupportAuditStage.Completed
                    : SystemSupportAuditStage.Rejected,
                result.Code,
                CancellationToken.None).ConfigureAwait(false);
            return new ApplicationResult<AgentResponse>(
                result.Succeeded
                    ? ApplicationStatus.Succeeded
                    : ApplicationStatus.Rejected,
                new SystemSupportExecutionResponse(result),
                result.Succeeded
                    ? []
                    : [Message(result.Code, ApplicationMessageSeverity.Error)],
                request.CorrelationId);
        }
        catch (OperationCanceledException)
        {
            await WriteAuditAsync(
                executionRequest,
                request.CorrelationId,
                actionKind,
                SystemSupportAuditStage.Cancelled,
                "system-support.elevation-cancelled",
                CancellationToken.None).ConfigureAwait(false);
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Cancelled,
                request.CorrelationId,
                Message(
                    "system-support.elevation-cancelled",
                    ApplicationMessageSeverity.Warning));
        }
        catch
        {
            await WriteAuditAsync(
                executionRequest,
                request.CorrelationId,
                actionKind,
                SystemSupportAuditStage.Failed,
                "system-support.broker-failed",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask WriteAuditAsync(
        ElevatedBrokerExecutionRequest executionRequest,
        CorrelationId correlationId,
        SystemSupportActionKind actionKind,
        SystemSupportAuditStage stage,
        string code,
        CancellationToken cancellationToken) =>
        auditRepository.WriteAsync(
            new SystemSupportAuditEvent(
                correlationId,
                executionRequest.PlanHash,
                actionKind,
                stage,
                DateTimeOffset.UtcNow,
                code,
                code,
                $"operation={executionRequest.Operation};stage={stage}",
                "system-support-v1"),
            cancellationToken);

    internal static SystemSupportActionKind ToSystemSupportActionKind(
        ElevatedBrokerOperationKind operation) =>
        operation switch
        {
            ElevatedBrokerOperationKind.CleanTemporaryFiles =>
                SystemSupportActionKind.CleanTemporaryFiles,
            ElevatedBrokerOperationKind.ClearSystemFileCache =>
                SystemSupportActionKind.ClearSystemFileCache,
            ElevatedBrokerOperationKind.FlushVolume =>
                SystemSupportActionKind.FlushVolume,
            ElevatedBrokerOperationKind.TrimOrOptimizeVolume =>
                SystemSupportActionKind.TrimOrOptimizeVolume,
            ElevatedBrokerOperationKind.SetActivePowerPlan =>
                SystemSupportActionKind.UseTemporaryPowerPlan,
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unsupported elevated Broker operation.")
        };

    private static ApplicationMessage Message(
        string code,
        ApplicationMessageSeverity severity) =>
        new(code, code, string.Empty, severity, []);
}
