using WinPool.Application;
using WinPool.Infrastructure.Sqlite;
using WinPool.Testing.Tools;

namespace WinPool.Agent;

/// <summary>
/// Owns a TestWorker process lifetime, including process registration, worker
/// event persistence, scheduling restoration, and terminal process evidence.
/// </summary>
internal sealed class TestWorkerSupervisor
{
    private readonly AgentInstanceId instanceId;
    private readonly TestWorkerProcessHost testWorkerHost;
    private readonly AgentProcessRegistry processRegistry;
    private readonly WorkerProcessRepository workerProcessRepository;
    private readonly TestRunRepository testRunRepository;
    private readonly TestProcessSchedulingScope testProcessSchedulingScope;
    private readonly AgentEventHub agentEvents;
    private readonly TrayApplicationContext tray;

    public TestWorkerSupervisor(
        AgentInstanceId instanceId,
        TestWorkerProcessHost testWorkerHost,
        AgentProcessRegistry processRegistry,
        WorkerProcessRepository workerProcessRepository,
        TestRunRepository testRunRepository,
        TestProcessSchedulingScope testProcessSchedulingScope,
        AgentEventHub agentEvents,
        TrayApplicationContext tray)
    {
        this.instanceId = instanceId;
        this.testWorkerHost = testWorkerHost
            ?? throw new ArgumentNullException(nameof(testWorkerHost));
        this.processRegistry = processRegistry
            ?? throw new ArgumentNullException(nameof(processRegistry));
        this.workerProcessRepository = workerProcessRepository
            ?? throw new ArgumentNullException(nameof(workerProcessRepository));
        this.testRunRepository = testRunRepository
            ?? throw new ArgumentNullException(nameof(testRunRepository));
        this.testProcessSchedulingScope = testProcessSchedulingScope
            ?? throw new ArgumentNullException(nameof(testProcessSchedulingScope));
        this.agentEvents = agentEvents ?? throw new ArgumentNullException(nameof(agentEvents));
        this.tray = tray ?? throw new ArgumentNullException(nameof(tray));
    }

    public async Task<TestWorkerRunResult> RunAsync(
        AuthorizedTestRun run,
        CorrelationId correlationId,
        IReadOnlyList<ToolProcessRequest> requests,
        IReadOnlyDictionary<string, ToolId?> toolIds,
        CancellationToken cancellationToken)
    {
        var runId = run.Plan.RunId;
        var workerProcessId = 0;
        ProcessInstanceId? workerProcessInstanceId = null;
        var workerFailed = false;
        var eventProjector = new TestWorkerAgentEventProjector(
            runId,
            correlationId,
            requests);
        PreparedTestProcessSchedulingScope? schedulingScope = null;
        try
        {
            return await testWorkerHost.RunAsync(
                requests,
                async (batch, callbackToken) =>
                {
                    await testRunRepository.AddWorkerEventsAsync(
                        runId,
                        batch.Events,
                        callbackToken);
                    foreach (var item in batch.Events)
                    {
                        if (item.Code == "tool.process.started")
                        {
                            await testRunRepository.UpdateStepStateAsync(
                                runId,
                                item.StepId,
                                ApplicationTaskState.Running,
                                callbackToken);
                            PublishTestState(
                                runId,
                                correlationId,
                                item.StepId,
                                ApplicationTaskState.Running,
                                item.Code,
                                item.OccurredAtUtc);
                        }
                        else if (item.Code == "tool.process.exited"
                                 && toolIds.TryGetValue(item.StepId, out var toolId))
                        {
                            var state = TestExecutionRules.IsAcceptedToolExit(
                                toolId,
                                item.ExitCode ?? -1)
                                ? ApplicationTaskState.Succeeded
                                : ApplicationTaskState.Failed;
                            await testRunRepository.UpdateStepStateAsync(
                                runId,
                                item.StepId,
                                state,
                                callbackToken);
                            PublishTestState(
                                runId,
                                correlationId,
                                item.StepId,
                                state,
                                item.Code,
                                item.OccurredAtUtc);
                        }

                        var progressEvent = eventProjector.ProjectNativeProgress(item);
                        if (progressEvent is not null)
                        {
                            agentEvents.Publish(progressEvent);
                        }
                    }

                    if (workerProcessId > 0
                        && workerProcessInstanceId is { } processInstanceId
                        && processRegistry.TryRecordHeartbeat(
                            processInstanceId,
                            workerProcessId,
                            DateTimeOffset.UtcNow)
                        && processRegistry.TryGet(processInstanceId, out var currentProcess)
                        && currentProcess is not null)
                    {
                        await workerProcessRepository.SaveAsync(
                            instanceId,
                            AgentProcessProjection.ToRegistration(currentProcess),
                            callbackToken);
                    }
                },
                async (processId, callbackToken) =>
                {
                    workerProcessId = processId;
                    var now = DateTimeOffset.UtcNow;
                    workerProcessInstanceId = ProcessInstanceId.New();
                    var registration = new AgentManagedProcess(
                        workerProcessInstanceId.Value,
                        processId,
                        AgentManagedProcessKind.TestWorker,
                        correlationId,
                        AgentProcessProjection.GetStartedAtUtc(processId),
                        now,
                        SupervisedProcessState.Running,
                        OwnsJobObject: true,
                        ShutdownDeadlineUtc: null);
                    if (!processRegistry.TryRegister(registration))
                    {
                        throw new InvalidOperationException(
                            "The TestWorker process identity was already registered.");
                    }

                    await workerProcessRepository.SaveAsync(
                        instanceId,
                        AgentProcessProjection.ToRegistration(registration),
                        callbackToken);
                    agentEvents.Publish(
                        new AgentProcessStateEvent(
                            AgentProcessProjection.ToRegistration(registration),
                            now));
                    var schedulingPolicy = run.SupportActions
                        .Select(item => item.Action)
                        .OfType<TestProcessSchedulingPolicyAction>()
                        .SingleOrDefault();
                    if (schedulingPolicy is not null)
                    {
                        schedulingScope = await testProcessSchedulingScope.PrepareAsync(
                            run.Plan.PlanHash,
                            schedulingPolicy,
                            processId,
                            correlationId,
                            callbackToken);
                    }

                    tray.SetTestRun(runId, "running");
                },
                async (_, callbackToken) =>
                {
                    if (schedulingScope is not null)
                    {
                        await testProcessSchedulingScope.RestoreAsync(
                            schedulingScope,
                            correlationId,
                            callbackToken);
                        schedulingScope = null;
                    }
                },
                cancellationToken);
        }
        catch
        {
            workerFailed = !cancellationToken.IsCancellationRequested;
            throw;
        }
        finally
        {
            Exception? schedulingRestoreFailure = null;
            if (workerProcessId > 0 && workerProcessInstanceId is { } processInstanceId)
            {
                if (schedulingScope is not null)
                {
                    try
                    {
                        using var restoreDeadline = new CancellationTokenSource(
                            TimeSpan.FromSeconds(10));
                        await testProcessSchedulingScope.RestoreAsync(
                            schedulingScope,
                            correlationId,
                            restoreDeadline.Token);
                    }
                    catch (Exception exception)
                    {
                        workerFailed = true;
                        schedulingRestoreFailure = exception;
                    }
                }

                if (processRegistry.TryMarkExited(
                        processInstanceId,
                        workerProcessId,
                        DateTimeOffset.UtcNow,
                        out var finalProcess,
                        workerFailed)
                    && finalProcess is not null)
                {
                    try
                    {
                        await workerProcessRepository.SaveAsync(
                            instanceId,
                            AgentProcessProjection.ToRegistration(finalProcess),
                            CancellationToken.None);
                        agentEvents.Publish(
                            new AgentProcessStateEvent(
                                AgentProcessProjection.ToRegistration(finalProcess),
                                DateTimeOffset.UtcNow));
                    }
                    catch (Exception exception) when (
                        exception is IOException
                            or Microsoft.Data.Sqlite.SqliteException)
                    {
                    }
                }
            }

            if (schedulingRestoreFailure is not null)
            {
                throw new InvalidOperationException(
                    "The TestWorker scheduling state could not be restored; recovery evidence was retained.",
                    schedulingRestoreFailure);
            }
        }
    }

    private void PublishTestState(
        TestRunId runId,
        CorrelationId correlationId,
        string stepId,
        ApplicationTaskState state,
        string code,
        DateTimeOffset occurredAtUtc)
    {
        agentEvents.Publish(
            new AgentTestEvent(
                new TestEvent(
                    runId,
                    TestEventKind.StateChanged,
                    new ApplicationTaskEvent(
                        new ApplicationTaskId(runId.Value),
                        correlationId,
                        ApplicationTaskEventKind.StateChanged,
                        state,
                        occurredAtUtc,
                        code,
                        code,
                        string.Empty,
                        stepId))));
    }
}
