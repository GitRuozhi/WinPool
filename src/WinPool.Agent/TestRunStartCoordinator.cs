using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Infrastructure.Sqlite;
using WinPool.Testing;
using WinPool.Testing.Tools;
using WinPool.ToolManagement;

namespace WinPool.Agent;

/// <summary>
/// Prepares, authorizes, persists, and starts one test request without making
/// the Agent request facade own the test-run pipeline.
/// </summary>
internal sealed class TestRunStartCoordinator
{
    private readonly AgentInstanceId instanceId;
    private readonly TestRunRepository testRunRepository;
    private readonly IExternalToolRegistry toolRegistry;
    private readonly ExternalToolStateRepository toolStateRepository;
    private readonly AgentTestCoordinator testCoordinator;
    private readonly AgentTestRunWorkflow testRunWorkflow;
    private readonly TrayApplicationContext tray;

    public TestRunStartCoordinator(
        AgentInstanceId instanceId,
        TestRunRepository testRunRepository,
        IExternalToolRegistry toolRegistry,
        ExternalToolStateRepository toolStateRepository,
        AgentTestCoordinator testCoordinator,
        AgentTestRunWorkflow testRunWorkflow,
        TrayApplicationContext tray)
    {
        this.instanceId = instanceId;
        this.testRunRepository = testRunRepository
            ?? throw new ArgumentNullException(nameof(testRunRepository));
        this.toolRegistry = toolRegistry
            ?? throw new ArgumentNullException(nameof(toolRegistry));
        this.toolStateRepository = toolStateRepository
            ?? throw new ArgumentNullException(nameof(toolStateRepository));
        this.testCoordinator = testCoordinator
            ?? throw new ArgumentNullException(nameof(testCoordinator));
        this.testRunWorkflow = testRunWorkflow
            ?? throw new ArgumentNullException(nameof(testRunWorkflow));
        this.tray = tray ?? throw new ArgumentNullException(nameof(tray));
    }

    public async Task<ApplicationResult<AgentResponse>> StartAsync(
        StartAgentTestRequest request,
        CancellationToken cancellationToken)
    {
        var existingRun = await testRunRepository.GetAsync(
            request.Plan.RunId,
            cancellationToken);
        var authorizationCoordinator = new TestRunAuthorizationCoordinator(
            (_, _) => Task.FromResult(request.UserConfirmedWrite));
        var authorization = existingRun is
            {
                State: PersistedTestRunState.Interrupted
            }
            && StringComparer.Ordinal.Equals(
                existingRun.PlanHash,
                request.Plan.PlanHash)
                ? await authorizationCoordinator.AuthorizeResumeAsync(
                    request.Plan,
                    existingRun.PlanHash,
                    cancellationToken)
                : await authorizationCoordinator.AuthorizeAsync(
                    request.Plan,
                    cancellationToken);
        if (!authorization.IsSuccess)
        {
            return new(
                authorization.Status,
                null,
                authorization.Messages,
                request.CorrelationId);
        }

        var run = authorization.Value!;
        var supportActionError = TestExecutionRules.ValidateSupportActions(run.Plan);
        if (supportActionError is not null)
        {
            return Reject(
                request.CorrelationId,
                supportActionError);
        }

        if (request.Definition.Id != run.Plan.DefinitionId
            || !string.Equals(
                request.Definition.Version,
                run.Plan.DefinitionVersion,
                StringComparison.Ordinal)
            || request.Definition.Tasks.Count == 0)
        {
            return Reject(
                request.CorrelationId,
                "agent.testing.definition_plan_mismatch");
        }

        var orderedSteps = TestExecutionRules.OrderStepsForExecution(run.Plan.Steps);
        if (orderedSteps is null)
        {
            return Reject(
                request.CorrelationId,
                "agent.testing.invalid_step_graph");
        }

        if (orderedSteps.Any(step =>
                step.ToolId is null
                && !LocalTestStepExecutor.IsSupported(step.Action)))
        {
            return Reject(
                request.CorrelationId,
                "agent.testing.non_tool_step_not_connected");
        }

        var preparedSteps = new List<PreparedExecutionStep>(orderedSteps.Count);
        foreach (var step in orderedSteps)
        {
            if (step.ToolId is null)
            {
                preparedSteps.Add(new(step, null, null));
                continue;
            }

            var tool = await toolRegistry.DetectAsync(
                step.ToolId.Value,
                cancellationToken);
            if (!tool.IsSuccess || tool.Value?.ExecutablePath is null)
            {
                return new(
                    tool.Status,
                    null,
                    tool.Messages,
                    request.CorrelationId);
            }

            await toolStateRepository.SaveAsync(
                tool.Value,
                DateTimeOffset.UtcNow,
                cancellationToken);
            var adapter = CreateAdapter(tool.Value);
            if (adapter is null)
            {
                return Reject(
                    request.CorrelationId,
                    "agent.testing.tool_adapter_not_supported");
            }

            var invocation = adapter.BuildInvocation(
                step,
                run.Workspace,
                request.CorrelationId);
            if (!invocation.IsSuccess || invocation.Value is null)
            {
                return new(
                    invocation.Status,
                    null,
                    invocation.Messages,
                    request.CorrelationId);
            }

            preparedSteps.Add(
                new(
                    step,
                    new(
                        run.Plan.RunId,
                        step.Id,
                        invocation.Value,
                        tool.Value,
                        TimeSpan.FromSeconds(3)),
                    adapter));
        }

        var runCancellation = new CancellationTokenSource();
        if (!testCoordinator.TryReserve(run.Plan.RunId, runCancellation))
        {
            runCancellation.Dispose();
            return Reject(
                request.CorrelationId,
                "agent.testing.already_running");
        }

        try
        {
            await testRunRepository.SaveDefinitionAsync(
                request.Definition,
                DateTimeOffset.UtcNow,
                cancellationToken);
            if (existingRun is null)
            {
                await testRunRepository.CreateRunAsync(
                    run.Plan,
                    $$"""{"agentSession":"{{instanceId.Value:N}}","source":"WinPool.Agent"}""",
                    PersistedTestRunState.Running,
                    cancellationToken);
            }
            else if (existingRun.State is PersistedTestRunState.Interrupted
                     && StringComparer.Ordinal.Equals(
                         existingRun.PlanHash,
                         run.Plan.PlanHash))
            {
                await testRunRepository.ResumeInterruptedAsync(
                    run.Plan.RunId,
                    run.Plan.PlanHash,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }
            else
            {
                throw new InvalidOperationException(
                    "The test run identity already exists and is not resumable.");
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or Microsoft.Data.Sqlite.SqliteException
                or InvalidOperationException
                or OperationCanceledException)
        {
            testCoordinator.ReleaseReservation();
            runCancellation.Dispose();
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Failed,
                request.CorrelationId,
                Message("agent.testing.persistence_failed"));
        }

        testCoordinator.Attach(testRunWorkflow.RunAsync(
            run,
            request.CorrelationId,
            preparedSteps,
            runCancellation));

        tray.SetTestRun(run.Plan.RunId, "starting");
        return await SuccessAsync(
            new AgentAcknowledgement(),
            request.CorrelationId);
    }

    private static IExternalToolAdapter? CreateAdapter(ToolState tool) =>
        tool.ToolId.Value switch
        {
            "microsoft.diskspd" => new DiskSpdAdapter(tool.ExecutablePath!),
            "fio" => new FioAdapter(tool.ExecutablePath!),
            "windows.robocopy" => new RoboCopyAdapter(tool.ExecutablePath!),
            "dite.filegen" => new DiteFileGenAdapter(tool.ExecutablePath!),
            _ => null
        };

    private static ApplicationResult<AgentResponse> Reject(
        CorrelationId correlationId,
        string code) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Rejected,
            correlationId,
            Message(code));

    private static Task<ApplicationResult<AgentResponse>> SuccessAsync(
        AgentResponse response,
        CorrelationId correlationId) =>
        Task.FromResult(ApplicationResult<AgentResponse>.Succeeded(response, correlationId));

    private static ApplicationMessage Message(string code) =>
        new(
            code,
            code,
            string.Empty,
            ApplicationMessageSeverity.Warning,
            []);
}
