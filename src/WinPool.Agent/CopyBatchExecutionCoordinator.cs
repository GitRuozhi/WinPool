using WinPool.Application;
using WinPool.Infrastructure.Sqlite;
using WinPool.Monitoring;
using WinPool.Testing;
using WinPool.Testing.Tools;

namespace WinPool.Agent;

/// <summary>
/// Executes registered-directory CopyBatch work while delegating the reviewed
/// R3 support actions to the Agent-owned audited entry points.
/// </summary>
internal sealed class CopyBatchExecutionCoordinator
{
    private readonly CopyBatchRecoveryCoordinator copyBatchRecovery;
    private readonly CopyBatchRepository copyBatchRepository;
    private readonly TestWorkerSupervisor testWorkerSupervisor;
    private readonly TestArtifactStore testArtifactStore;
    private readonly TestRunRepository testRunRepository;
    private readonly MonitoringSessionCoordinator monitoring;
    private readonly Func<TestRunId, string, string, ClearSystemFileCacheAction, CorrelationId, CancellationToken, Task>
        executeRamMapBeforeBatchAsync;
    private readonly Func<AuthorizedTestRun, CopyBatchManifest, int, FlushVolumeAction, CorrelationId, CancellationToken, Task>
        executeFlushBetweenBatchesAsync;

    public CopyBatchExecutionCoordinator(
        CopyBatchRecoveryCoordinator copyBatchRecovery,
        CopyBatchRepository copyBatchRepository,
        TestWorkerSupervisor testWorkerSupervisor,
        TestArtifactStore testArtifactStore,
        TestRunRepository testRunRepository,
        MonitoringSessionCoordinator monitoring,
        Func<TestRunId, string, string, ClearSystemFileCacheAction, CorrelationId, CancellationToken, Task>
            executeRamMapBeforeBatchAsync,
        Func<AuthorizedTestRun, CopyBatchManifest, int, FlushVolumeAction, CorrelationId, CancellationToken, Task>
            executeFlushBetweenBatchesAsync)
    {
        this.copyBatchRecovery = copyBatchRecovery ?? throw new ArgumentNullException(nameof(copyBatchRecovery));
        this.copyBatchRepository = copyBatchRepository ?? throw new ArgumentNullException(nameof(copyBatchRepository));
        this.testWorkerSupervisor = testWorkerSupervisor ?? throw new ArgumentNullException(nameof(testWorkerSupervisor));
        this.testArtifactStore = testArtifactStore ?? throw new ArgumentNullException(nameof(testArtifactStore));
        this.testRunRepository = testRunRepository ?? throw new ArgumentNullException(nameof(testRunRepository));
        this.monitoring = monitoring ?? throw new ArgumentNullException(nameof(monitoring));
        this.executeRamMapBeforeBatchAsync = executeRamMapBeforeBatchAsync
            ?? throw new ArgumentNullException(nameof(executeRamMapBeforeBatchAsync));
        this.executeFlushBetweenBatchesAsync = executeFlushBetweenBatchesAsync
            ?? throw new ArgumentNullException(nameof(executeFlushBetweenBatchesAsync));
    }

    public async Task<bool> ExecuteAsync(
        AuthorizedTestRun run,
        CorrelationId correlationId,
        PreparedExecutionStep prepared,
        CancellationToken cancellationToken)
    {
        if (prepared.Adapter is not RoboCopyAdapter adapter
            || prepared.Request is null)
        {
            throw new InvalidOperationException(
                "A registered directory copy requires the RoboCopy adapter and tool identity.");
        }

        var manifest = await copyBatchRecovery.PrepareAsync(
                run,
                prepared.Step,
                cancellationToken)
            ?? throw new InvalidDataException(
                "The directory copy did not produce a recovery manifest.");
        var checkpoints = await copyBatchRepository.ListEntryCheckpointsAsync(
            manifest.RunId,
            manifest.StepId,
            cancellationToken);
        var groups = new CopyBatchInvocationPlanner().Build(
            manifest,
            checkpoints,
            prepared.Step,
            run.Workspace,
            prepared.Request.ExpectedTool,
            adapter,
            correlationId);
        var ramMapAction = run.SupportActions
            .Select(item => item.Action)
            .OfType<ClearSystemFileCacheAction>()
            .SingleOrDefault();
        var flushAction = run.SupportActions
            .Select(item => item.Action)
            .OfType<FlushVolumeAction>()
            .SingleOrDefault();
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            if (ramMapAction is not null)
            {
                await executeRamMapBeforeBatchAsync(
                    manifest.RunId,
                    manifest.StepId,
                    manifest.PlanHash,
                    ramMapAction,
                    correlationId,
                    cancellationToken);
            }

            foreach (var chunk in group.Items.Chunk(512))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await copyBatchRepository.MarkEntriesCopyingAsync(
                    manifest.RunId,
                    manifest.StepId,
                    chunk.Select(item => item.Entry.Ordinal).ToArray(),
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                TestWorkerRunResult result;
                try
                {
                    result = await testWorkerSupervisor.RunAsync(
                        run,
                        correlationId,
                        chunk.Select(item => item.Request).ToArray(),
                        new Dictionary<string, ToolId?>(StringComparer.Ordinal)
                        {
                            [manifest.StepId] = prepared.Step.ToolId
                        },
                        cancellationToken);
                }
                catch
                {
                    await copyBatchRepository.MarkOpenBatchInterruptedAsync(
                        manifest.RunId,
                        manifest.StepId,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None);
                    throw;
                }

                await testArtifactStore.SaveWorkerOutputAsync(
                    manifest.RunId,
                    manifest.StepId,
                    result.Events,
                    CancellationToken.None);
                var processFailure = false;
                var failedEntries = new List<(int Ordinal, int ExitCode, string Code)>();
                for (var index = 0; index < result.ToolResults.Count; index++)
                {
                    var toolResult = result.ToolResults[index];
                    var processEvents = result.Events.Where(item =>
                            item.ProcessId == toolResult.Audit.Identity.ProcessId)
                        .ToArray();
                    var parseFailed = await new TestToolResultRepositoryWriter(
                            testRunRepository)
                        .PersistAsync(
                            manifest.RunId,
                            manifest.StepId,
                            adapter,
                            processEvents,
                            toolResult.Audit.ExitCode,
                            prepared.Request.Invocation.OutputEncoding,
                            CancellationToken.None);
                    var itemFailed = parseFailed
                        || !ToolProcessExitPolicy.IsAccepted(
                            toolResult.Audit.ToolId,
                            toolResult.Audit.ExitCode)
                        || toolResult.Audit.TerminationReason
                            is not ToolProcessTerminationReason.Completed;
                    processFailure |= itemFailed;
                    if (itemFailed)
                    {
                        failedEntries.Add(
                            (
                                chunk[index].Entry.Ordinal,
                                toolResult.Audit.ExitCode,
                                parseFailed
                                    ? "copy.output_parse_failed"
                                    : "copy.process_failed"));
                    }
                }

                var incomplete = result.ToolResults.Count != chunk.Length;
                var inspector = new RegisteredTestDirectoryInspector();
                var source = await inspector.CaptureAsync(
                    run,
                    GetTextParameter(
                        prepared.Step,
                        "sourceRelativeDirectory")!,
                    includeHashes: false,
                    CancellationToken.None);
                var destination = await CaptureOrCreateEmptyDirectoryEvidenceAsync(
                    run,
                    GetTextParameter(
                        prepared.Step,
                        "destinationRelativeDirectory")!,
                    inspector,
                    CancellationToken.None);
                var report = new CopyBatchPlanner().Recover(
                    manifest,
                    source,
                    destination);
                await copyBatchRepository.ApplyRecoveryReportAsync(
                    manifest.RunId,
                    manifest.StepId,
                    report,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
                if (failedEntries.Count > 0)
                {
                    var afterRecovery = (await copyBatchRepository
                            .ListEntryCheckpointsAsync(
                                manifest.RunId,
                                manifest.StepId,
                                CancellationToken.None))
                        .ToDictionary(item => item.Ordinal);
                    foreach (var failure in failedEntries)
                    {
                        var checkpoint = afterRecovery[failure.Ordinal];
                        if (checkpoint.State is CopyBatchEntryState.Pending)
                        {
                            await copyBatchRepository.UpdateEntryCheckpointAsync(
                                checkpoint with
                                {
                                    State = CopyBatchEntryState.Failed,
                                    LastExitCode = failure.ExitCode,
                                    DiagnosticCode = failure.Code,
                                    UpdatedAtUtc = DateTimeOffset.UtcNow
                                },
                                CancellationToken.None);
                        }
                    }
                }

                if (incomplete)
                {
                    await copyBatchRepository.MarkOpenBatchInterruptedAsync(
                        manifest.RunId,
                        manifest.StepId,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None);
                }

                if (processFailure
                    || incomplete
                    || report.ConflictCount > 0)
                {
                    return false;
                }

                var refreshed = await copyBatchRepository.ListEntryCheckpointsAsync(
                    manifest.RunId,
                    manifest.StepId,
                    CancellationToken.None);
                var refreshedByOrdinal = refreshed.ToDictionary(
                    item => item.Ordinal);
                if (chunk.Any(item =>
                        refreshedByOrdinal[item.Entry.Ordinal].State
                            is not CopyBatchEntryState.Completed))
                {
                    return false;
                }
            }

            if (groupIndex < groups.Count - 1)
            {
                if (flushAction is not null)
                {
                    await executeFlushBetweenBatchesAsync(
                        run,
                        manifest,
                        group.Batch.BatchNumber,
                        flushAction,
                        correlationId,
                        cancellationToken);
                }

                await WaitForSettleAsync(
                    manifest,
                    group.Batch.BatchNumber,
                    cancellationToken);
            }
        }

        await ValidateExternalDirectoryOutputAsync(
            run,
            prepared.Step,
            CancellationToken.None);
        await copyBatchRecovery.FinalizeAsync(
            run,
            prepared.Step,
            CancellationToken.None);
        return true;
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

    private async Task WaitForSettleAsync(
        CopyBatchManifest manifest,
        int completedBatchNumber,
        CancellationToken cancellationToken)
    {
        if (monitoring.CurrentSession is null)
        {
            throw new InvalidOperationException(
                "A multi-batch copy requires an active Agent monitoring session for settle evidence.");
        }

        var evidence = await new MonitorIdleDetector().WaitAsync(
            () => monitoring.CurrentSamples,
            MonitorIdlePolicy.CopyBatchDefault,
            cancellationToken);
        var aggregation = $"copy-batch-{completedBatchNumber}";
        await testRunRepository.AddMetricAsync(
            manifest.RunId,
            manifest.StepId,
            "copy_settle_seconds",
            (evidence.CompletedAtUtc - evidence.StartedAtUtc).TotalSeconds,
            "seconds",
            aggregation,
            cancellationToken);
        await testRunRepository.AddMetricAsync(
            manifest.RunId,
            manifest.StepId,
            "copy_settle_max_activity_percent",
            evidence.FinalObservation.MaximumActivityPercent,
            "percent",
            aggregation,
            cancellationToken);
        await testRunRepository.AddMetricAsync(
            manifest.RunId,
            manifest.StepId,
            "copy_settle_max_queue_length",
            evidence.FinalObservation.MaximumQueueLength,
            "count",
            aggregation,
            cancellationToken);
        await testRunRepository.AddMetricAsync(
            manifest.RunId,
            manifest.StepId,
            "copy_settle_max_combined_bytes_per_second",
            evidence.FinalObservation.MaximumCombinedBytesPerSecond,
            "bytes/s",
            aggregation,
            cancellationToken);
    }

    private static async Task<RegisteredDirectoryEvidence>
        CaptureOrCreateEmptyDirectoryEvidenceAsync(
            AuthorizedTestRun run,
            string relativePath,
            RegisteredTestDirectoryInspector inspector,
            CancellationToken cancellationToken)
    {
        try
        {
            return await inspector.CaptureAsync(
                run,
                relativePath,
                includeHashes: false,
                cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            var registration = run.Plan.Workspace.RegisteredDirectories.Single(
                item => StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(Path.Combine(
                        run.Plan.Workspace.NormalizedRootDirectory,
                        item.RelativePath)),
                    Path.GetFullPath(Path.Combine(
                        run.Plan.Workspace.NormalizedRootDirectory,
                        relativePath))));
            return new(
                registration.RelativePath,
                registration.IdentityToken,
                registration.MaximumBytes,
                registration.MaximumFileCount,
                0,
                0,
                []);
        }
    }

    private static string? GetTextParameter(TestStep step, string key) =>
        step.Parameters.TryGetValue(key, out var parameter)
        && parameter.Kind is TestParameterKind.Text
        && !string.IsNullOrWhiteSpace(parameter.SerializedValue)
            ? parameter.SerializedValue
            : null;
}
