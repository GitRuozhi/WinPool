using WinPool.Application;
using WinPool.Execution;

namespace WinPool.Testing;

public sealed class TestRunAuthorizationCoordinator :
    ITestRunAuthorizationCoordinator
{
    private static readonly TimeSpan ExecutionAuthorizationLifetime =
        TimeSpan.FromDays(30);

    private readonly Func<TestPlan, CancellationToken, Task<bool>>
        requestConfirmation;
    private readonly TimeProvider timeProvider;

    public TestRunAuthorizationCoordinator(
        Func<TestPlan, CancellationToken, Task<bool>> requestConfirmation,
        TimeProvider? timeProvider = null)
    {
        this.requestConfirmation = requestConfirmation
            ?? throw new ArgumentNullException(nameof(requestConfirmation));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApplicationResult<AuthorizedTestRun>> AuthorizeAsync(
        TestPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var correlationId = CorrelationId.New();
        var error = Validate(plan);
        if (error is not null)
        {
            return Reject(correlationId, error.Value.Code, error.Value.Message);
        }

        var now = timeProvider.GetUtcNow();
        if (plan.Workspace.ExpiresAtUtc <= now)
        {
            return Reject(
                correlationId,
                "test.authorization.expired",
                "The test workspace authorization has expired.");
        }

        return await ConfirmAndAuthorizeAsync(
            plan,
            now,
            correlationId,
            cancellationToken);
    }

    public async Task<ApplicationResult<AuthorizedTestRun>>
        AuthorizeResumeAsync(
            TestPlan plan,
            string persistedPlanHash,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistedPlanHash);
        var correlationId = CorrelationId.New();
        var error = Validate(plan);
        if (error is not null)
        {
            return Reject(correlationId, error.Value.Code, error.Value.Message);
        }

        if (!StringComparer.Ordinal.Equals(
                plan.PlanHash,
                persistedPlanHash))
        {
            return Reject(
                correlationId,
                "test.authorization.resume_hash_mismatch",
                "Only the exact persisted immutable test plan can resume.");
        }

        return await ConfirmAndAuthorizeAsync(
            plan,
            timeProvider.GetUtcNow(),
            correlationId,
            cancellationToken);
    }

    private async Task<ApplicationResult<AuthorizedTestRun>>
        ConfirmAndAuthorizeAsync(
            TestPlan plan,
            DateTimeOffset now,
            CorrelationId correlationId,
            CancellationToken cancellationToken)
    {
        if ((plan.EstimatedWriteBytes > 0 || plan.SupportActions.Count > 0)
            && !await requestConfirmation(plan, cancellationToken))
        {
            return ApplicationResult<AuthorizedTestRun>.FromStatus(
                ApplicationStatus.RequiresAuthorization,
                correlationId,
                Message(
                    "test.authorization.confirmation_required",
                    "Explicit confirmation is required before a file-writing test.",
                    ApplicationMessageSeverity.Warning));
        }

        var expiresAt = now.Add(ExecutionAuthorizationLifetime);

        var workspace = new WinPool.Application.AuthorizedTestWorkspace(
            plan.Workspace,
            expiresAt);
        var supportActions = plan.SupportActions
            .Select(action => new AuthorizedSystemSupportAction(
                action,
                plan.PlanHash,
                expiresAt))
            .ToArray();
        return ApplicationResult<AuthorizedTestRun>.Succeeded(
            new AuthorizedTestRun(plan, workspace, supportActions),
            correlationId);
    }

    private static (string Code, string Message)? Validate(TestPlan plan)
    {
        if (!TestPlanCompiler.HasValidHash(plan))
        {
            return (
                "test.authorization.plan_hash_mismatch",
                "The test plan hash is missing or does not match its immutable fields.");
        }

        if (plan.Risk >= RiskLevel.R4StorageStructureMutation
            || plan.EstimatedWriteBytes < 0
            || plan.EstimatedWriteBytes > plan.Target.AvailableBytes
            || !plan.Target.IsWriteAllowed && plan.EstimatedWriteBytes > 0)
        {
            return (
                "test.authorization.plan_rejected",
                "The test plan exceeds the allowed file-test safety boundary.");
        }

        return null;
    }

    private static ApplicationResult<AuthorizedTestRun> Reject(
        CorrelationId correlationId,
        string code,
        string message) =>
        ApplicationResult<AuthorizedTestRun>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            Message(code, message, ApplicationMessageSeverity.Error));

    private static ApplicationMessage Message(
        string code,
        string diagnostic,
        ApplicationMessageSeverity severity) =>
        new(code, code, diagnostic, severity, []);

}
