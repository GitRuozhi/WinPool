using WinPool.Application;
using WinPool.Infrastructure.Sqlite;
using WinPool.Monitoring;
using WinPool.Testing;
using WinPool.Testing.Tools;

namespace WinPool.Agent;

/// <summary>
/// Owns one authorized test run from temporary support-action setup through
/// terminal persistence and test-state publication.
/// </summary>
internal sealed class AgentTestRunWorkflow
{
    private readonly AgentTestCoordinator testCoordinator;
    private readonly TestPowerPlanScope testPowerPlanScope;
    private readonly TestRunRepository testRunRepository;
    private readonly MonitoringSessionCoordinator monitoring;
    private readonly TestArtifactStore testArtifactStore;
    private readonly CopyBatchRepository copyBatchRepository;
    private readonly CopyBatchRecoveryCoordinator copyBatchRecovery;
    private readonly CopyBatchExecutionCoordinator copyBatchExecutor;
    private readonly TestWorkerSupervisor testWorkerSupervisor;
    private readonly TrayApplicationContext tray;
    private readonly AgentEventHub agentEvents;
    private readonly Func<TestRunId, string, string, ClearSystemFileCacheAction, CorrelationId, CancellationToken, Task>
        executeRamMapBeforeBatchAsync;

    public AgentTestRunWorkflow(
        AgentTestCoordinator testCoordinator,
        TestPowerPlanScope testPowerPlanScope,
        TestRunRepository testRunRepository,
        MonitoringSessionCoordinator monitoring,
        TestArtifactStore testArtifactStore,
        CopyBatchRepository copyBatchRepository,
        CopyBatchRecoveryCoordinator copyBatchRecovery,
        CopyBatchExecutionCoordinator copyBatchExecutor,
        TestWorkerSupervisor testWorkerSupervisor,
        TrayApplicationContext tray,
        AgentEventHub agentEvents,
        Func<TestRunId, string, string, ClearSystemFileCacheAction, CorrelationId, CancellationToken, Task>
            executeRamMapBeforeBatchAsync)
    {
        this.testCoordinator = testCoordinator ?? throw new ArgumentNullException(nameof(testCoordinator));
        this.testPowerPlanScope = testPowerPlanScope ?? throw new ArgumentNullException(nameof(testPowerPlanScope));
        this.testRunRepository = testRunRepository ?? throw new ArgumentNullException(nameof(testRunRepository));
        this.monitoring = monitoring ?? throw new ArgumentNullException(nameof(monitoring));
        this.testArtifactStore = testArtifactStore ?? throw new ArgumentNullException(nameof(testArtifactStore));
        this.copyBatchRepository = copyBatchRepository ?? throw new ArgumentNullException(nameof(copyBatchRepository));
        this.copyBatchRecovery = copyBatchRecovery ?? throw new ArgumentNullException(nameof(copyBatchRecovery));
        this.copyBatchExecutor = copyBatchExecutor ?? throw new ArgumentNullException(nameof(copyBatchExecutor));
        this.testWorkerSupervisor = testWorkerSupervisor ?? throw new ArgumentNullException(nameof(testWorkerSupervisor));
        this.tray = tray ?? throw new ArgumentNullException(nameof(tray));
        this.agentEvents = agentEvents ?? throw new ArgumentNullException(nameof(agentEvents));
        this.executeRamMapBeforeBatchAsync = executeRamMapBeforeBatchAsync
            ?? throw new ArgumentNullException(nameof(executeRamMapBeforeBatchAsync));
    }

    public async Task RunAsync(
        AuthorizedTestRun run,
        CorrelationId correlationId,
        IReadOnlyList<PreparedExecutionStep> preparedSteps,
        CancellationTokenSource runCancellation)
    {
        var runId = run.Plan.RunId;
        var completedStepIds = new HashSet<string>(StringComparer.Ordinal);
        var failed = false;
        var finalState = PersistedTestRunState.Completed;
        PreparedTestPowerPlanScope? powerPlanScope = null;
        try
        {
            var powerAction = run.SupportActions
                .Select(item => item.Action)
                .OfType<UseTemporaryPowerPlanAction>()
                .SingleOrDefault();
            if (powerAction is not null)
            {
                powerPlanScope = await testPowerPlanScope.PrepareAsync(
                    run.Plan.PlanHash,
                    powerAction.PowerPlanId,
                    correlationId,
                    runCancellation.Token);
            }

            var localExecutor = new LocalTestStepExecutor(
                testRunRepository,
                monitoring,
                testArtifactStore);
            var index = 0;
            while (index < preparedSteps.Count)
            {
                runCancellation.Token.ThrowIfCancellationRequested();
                var current = preparedSteps[index];
                if (current.Request is null)
                {
                    await localExecutor.ExecuteAsync(
                        run,
                        current.Step,
                        runCancellation.Token);
                    completedStepIds.Add(current.Step.Id);
                    index++;
                    continue;
                }

                if (IsRegisteredDirectoryCopy(current.Step))
                {
                    var copySucceeded = await copyBatchExecutor.ExecuteAsync(
                        run,
                        correlationId,
                        current,
                        runCancellation.Token);
                    await testRunRepository.UpdateStepStateAsync(
                        runId,
                        current.Step.Id,
                        copySucceeded
                            ? ApplicationTaskState.Succeeded
                            : ApplicationTaskState.Failed,
                        CancellationToken.None);
                    if (!copySucceeded)
                    {
                        failed = true;
                        finalState = PersistedTestRunState.Failed;
                        break;
                    }

                    completedStepIds.Add(current.Step.Id);
                    index++;
                    continue;
                }

                var batchSteps = new List<PreparedExecutionStep>();
                while (index < preparedSteps.Count
                       && preparedSteps[index].Request is not null)
                {
                    var prepared = preparedSteps[index];
                    if (batchSteps.Count > 0
                        && IsRegisteredDirectoryCopy(prepared.Step))
                    {
                        break;
                    }

                    batchSteps.Add(prepared);
                    index++;
                    if (RequiresDirectoryQuotaBoundary(prepared.Step))
                    {
                        break;
                    }
                }

                var ramMapAction = run.SupportActions
                    .Select(item => item.Action)
                    .OfType<ClearSystemFileCacheAction>()
                    .SingleOrDefault();
                if (ramMapAction is not null)
                {
                    await executeRamMapBeforeBatchAsync(
                        runId,
                        batchSteps[0].Step.Id,
                        run.Plan.PlanHash,
                        ramMapAction,
                        correlationId,
                        runCancellation.Token);
                }

                foreach (var copyStep in batchSteps.Where(
                             item => IsRegisteredDirectoryCopy(item.Step)))
                {
                    await copyBatchRecovery.PrepareAsync(
                        run,
                        copyStep.Step,
                        runCancellation.Token);
                }

                var result = await testWorkerSupervisor.RunAsync(
                    run,
                    correlationId,
                    batchSteps.Select(item => item.Request!).ToArray(),
                    batchSteps.ToDictionary(
                        item => item.Step.Id,
                        item => item.Step.ToolId,
                        StringComparer.Ordinal),
                    runCancellation.Token);

                var parseFailed = false;
                foreach (var toolResult in result.ToolResults)
                {
                    var prepared = batchSteps.Single(
                        item => string.Equals(
                            item.Step.Id,
                            toolResult.Audit.StepId,
                            StringComparison.Ordinal));
                    await testArtifactStore.SaveWorkerOutputAsync(
                        runId,
                        prepared.Step.Id,
                        result.Events.Where(item => string.Equals(
                                item.StepId,
                                prepared.Step.Id,
                                StringComparison.Ordinal))
                            .ToArray(),
                        CancellationToken.None);
                    var stepParseFailed = await new TestToolResultRepositoryWriter(
                            testRunRepository)
                        .PersistAsync(
                            runId,
                            prepared.Step.Id,
                            prepared.Adapter!,
                            result.Events.Where(item => string.Equals(
                                    item.StepId,
                                    prepared.Step.Id,
                                    StringComparison.Ordinal))
                                .ToArray(),
                            toolResult.Audit.ExitCode,
                            prepared.Request!.Invocation.OutputEncoding,
                            CancellationToken.None);
                    parseFailed |= stepParseFailed;
                    var cancelled = toolResult.Audit.TerminationReason
                        is ToolProcessTerminationReason.Cancelled;
                    var stepSucceeded = TestExecutionRules.IsAcceptedToolExit(
                            prepared.Step.ToolId,
                            toolResult.Audit.ExitCode)
                        && !stepParseFailed
                        && !cancelled;
                    if (stepSucceeded)
                    {
                        try
                        {
                            await ValidateExternalDirectoryOutputAsync(
                                run,
                                prepared.Step,
                                CancellationToken.None);
                            await copyBatchRecovery.FinalizeAsync(
                                run,
                                prepared.Step,
                                CancellationToken.None);
                        }
                        catch (Exception exception) when (
                            exception is IOException
                                or UnauthorizedAccessException
                                or InvalidDataException)
                        {
                            stepParseFailed = true;
                            parseFailed = true;
                            stepSucceeded = false;
                        }
                    }

                    if (!stepSucceeded
                        && IsRegisteredDirectoryCopy(prepared.Step))
                    {
                        await copyBatchRepository.MarkOpenBatchInterruptedAsync(
                            runId,
                            prepared.Step.Id,
                            DateTimeOffset.UtcNow,
                            CancellationToken.None);
                    }

                    await testRunRepository.UpdateStepStateAsync(
                        runId,
                        prepared.Step.Id,
                        cancelled
                            ? ApplicationTaskState.Cancelled
                            : stepSucceeded
                                ? ApplicationTaskState.Succeeded
                                : ApplicationTaskState.Failed,
                        CancellationToken.None);
                    if (stepSucceeded)
                    {
                        completedStepIds.Add(prepared.Step.Id);
                    }
                }

                var batchCancelled = result.ToolResults.Any(item =>
                    item.Audit.TerminationReason
                    is ToolProcessTerminationReason.Cancelled);
                var incomplete = result.ToolResults.Count != batchSteps.Count;
                if (batchCancelled
                    || incomplete && runCancellation.IsCancellationRequested)
                {
                    finalState = PersistedTestRunState.Cancelled;
                    break;
                }

                if (incomplete
                    || parseFailed
                    || result.ToolResults.Any(item =>
                    {
                        var prepared = batchSteps.Single(
                            step => StringComparer.Ordinal.Equals(
                                step.Step.Id,
                                item.Audit.StepId));
                        return !TestExecutionRules.IsAcceptedToolExit(
                            prepared.Step.ToolId,
                            item.Audit.ExitCode);
                    }))
                {
                    failed = true;
                    finalState = PersistedTestRunState.Failed;
                    break;
                }
            }

            if (completedStepIds.Count != preparedSteps.Count)
            {
                foreach (var skipped in preparedSteps.Where(
                             item => !completedStepIds.Contains(item.Step.Id)))
                {
                    await testRunRepository.UpdateStepStateAsync(
                        runId,
                        skipped.Step.Id,
                        finalState == PersistedTestRunState.Cancelled
                            ? ApplicationTaskState.Cancelled
                            : ApplicationTaskState.Rejected,
                        CancellationToken.None);
                }
            }
        }
        catch (Exception exception)
        {
            failed = exception is not OperationCanceledException
                     && !runCancellation.IsCancellationRequested;
            finalState = failed
                ? PersistedTestRunState.Failed
                : PersistedTestRunState.Cancelled;
            foreach (var copyStep in preparedSteps.Where(
                         item => !completedStepIds.Contains(item.Step.Id)
                             && IsRegisteredDirectoryCopy(item.Step)))
            {
                try
                {
                    await copyBatchRepository.MarkOpenBatchInterruptedAsync(
                        runId,
                        copyStep.Step.Id,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None);
                }
                catch (Exception persistenceException) when (
                    persistenceException is IOException
                        or Microsoft.Data.Sqlite.SqliteException)
                {
                }
            }

            foreach (var skipped in preparedSteps.Where(
                         item => !completedStepIds.Contains(item.Step.Id)))
            {
                try
                {
                    await testRunRepository.UpdateStepStateAsync(
                        runId,
                        skipped.Step.Id,
                        finalState == PersistedTestRunState.Cancelled
                            ? ApplicationTaskState.Cancelled
                            : ApplicationTaskState.Rejected,
                        CancellationToken.None);
                }
                catch (Exception persistenceException) when (
                    persistenceException is IOException
                        or Microsoft.Data.Sqlite.SqliteException
                        or KeyNotFoundException)
                {
                }
            }
        }
        finally
        {
            if (powerPlanScope is not null)
            {
                try
                {
                    await testPowerPlanScope.RestoreAsync(
                        powerPlanScope,
                        correlationId);
                }
                catch
                {
                    failed = true;
                    finalState = PersistedTestRunState.Failed;
                }
            }

            try
            {
                await testRunRepository.CompleteAsync(
                    runId,
                    finalState,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is IOException
                    or Microsoft.Data.Sqlite.SqliteException
                    or KeyNotFoundException)
            {
            }

            testCoordinator.Complete(runId);

            runCancellation.Dispose();
            tray.SetTestRun(
                null,
                finalState switch
                {
                    PersistedTestRunState.Failed => "failed",
                    PersistedTestRunState.Cancelled => "cancelled",
                    _ => "completed"
                });
            PublishTestEvent(
                runId,
                correlationId,
                null,
                TestEventKind.StateChanged,
                ApplicationTaskEventKind.StateChanged,
                finalState switch
                {
                    PersistedTestRunState.Failed => ApplicationTaskState.Failed,
                    PersistedTestRunState.Cancelled => ApplicationTaskState.Cancelled,
                    _ => ApplicationTaskState.Succeeded
                },
                $"agent.testing.{finalState.ToString().ToLowerInvariant()}",
                DateTimeOffset.UtcNow);
        }
    }

    private async Task ValidateExternalDirectoryOutputAsync(
        AuthorizedTestRun run,
        TestStep step,
        CancellationToken cancellationToken)
    {
        var relativeDirectory =
            GetTextParameter(step, "targetRelativeDirectory")
            ?? GetTextParameter(step, "destinationRelativeDirectory");
        if (relativeDirectory is null)
        {
            return;
        }

        var evidence = await new RegisteredTestDirectoryInspector().CaptureAsync(
            run,
            relativeDirectory,
            includeHashes: false,
            cancellationToken);
        await testRunRepository.AddMetricAsync(
            run.Plan.RunId,
            step.Id,
            "bounded_directory_file_count",
            evidence.ActualFileCount,
            "files",
            "observed",
            cancellationToken);
        await testRunRepository.AddMetricAsync(
            run.Plan.RunId,
            step.Id,
            "bounded_directory_bytes",
            evidence.ActualBytes,
            "bytes",
            "observed",
            cancellationToken);
    }

    private static bool RequiresDirectoryQuotaBoundary(TestStep step) =>
        step.Parameters.ContainsKey("targetRelativeDirectory")
        || step.Parameters.ContainsKey("destinationRelativeDirectory");

    private static bool IsRegisteredDirectoryCopy(TestStep step) =>
        step.Action is TestActionKind.Copy
        && step.ToolId?.Value is "windows.robocopy"
        && GetTextParameter(step, "sourceRelativeDirectory") is not null
        && GetTextParameter(step, "destinationRelativeDirectory") is not null;

    private static string? GetTextParameter(TestStep step, string key) =>
        step.Parameters.TryGetValue(key, out var parameter)
        && parameter.Kind is TestParameterKind.Text
        && !string.IsNullOrWhiteSpace(parameter.SerializedValue)
            ? parameter.SerializedValue
            : null;

    private void PublishTestEvent(
        TestRunId runId,
        CorrelationId correlationId,
        string? stepId,
        TestEventKind testKind,
        ApplicationTaskEventKind taskKind,
        ApplicationTaskState state,
        string code,
        DateTimeOffset occurredAtUtc,
        double? progressFraction = null)
    {
        agentEvents.Publish(
            new AgentTestEvent(
                new TestEvent(
                    runId,
                    testKind,
                    new ApplicationTaskEvent(
                        new ApplicationTaskId(runId.Value),
                        correlationId,
                        taskKind,
                        state,
                        occurredAtUtc,
                        code,
                        code,
                        string.Empty,
                        stepId,
                        progressFraction))));
    }
}
