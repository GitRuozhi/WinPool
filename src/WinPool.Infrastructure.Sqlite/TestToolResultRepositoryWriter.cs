using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed class TestToolResultRepositoryWriter(
    TestRunRepository repository)
{
    public async Task<bool> PersistAsync(
        TestRunId runId,
        string stepId,
        IExternalToolAdapter adapter,
        IReadOnlyList<WorkerEvent> events,
        int exitCode,
        ToolOutputEncoding outputEncoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(events);
        var histogram = new Dictionary<long, long>();
        var parseFailed = false;
        await foreach (var toolEvent in adapter.ParseAsync(
                           new ToolProcessStreams(
                               ToOutputChunks(events),
                               Task.FromResult(exitCode),
                               outputEncoding,
                               events.Select(item => item.OutputCodePage)
                                   .FirstOrDefault(item => item is not null)),
                           cancellationToken))
        {
            if (toolEvent.Metric is { } metric)
            {
                await repository.AddMetricAsync(
                    runId,
                    stepId,
                    metric.MetricId,
                    metric.Value,
                    metric.Unit,
                    "single",
                    cancellationToken);
            }

            if (toolEvent.HistogramBucket is { } bucket)
            {
                histogram.TryGetValue(
                    bucket.UpperBoundNanoseconds,
                    out var count);
                histogram[bucket.UpperBoundNanoseconds] =
                    checked(count + bucket.SampleCount);
            }

            if (toolEvent.Kind == ToolEventKind.Failed)
            {
                parseFailed = true;
                await repository.AddWorkerEventsAsync(
                    runId,
                    [
                        new(
                            runId,
                            stepId,
                            WorkerEventKind.Error,
                            WorkerEventImportance.Error,
                            toolEvent.OccurredAtUtc,
                            toolEvent.Code,
                            ReadOnlyMemory<byte>.Empty)
                    ],
                    cancellationToken);
            }
        }

        if (histogram.Count > 0)
        {
            await repository.AddLatencyHistogramAsync(
                runId,
                stepId,
                histogram,
                cancellationToken);
        }

        return parseFailed;
    }

    private static async IAsyncEnumerable<ToolOutputChunk> ToOutputChunks(
        IReadOnlyList<WorkerEvent> events)
    {
        foreach (var item in events)
        {
            if (item.Kind is WorkerEventKind.StandardOutput
                or WorkerEventKind.StandardError)
            {
                yield return new(
                    item.Kind == WorkerEventKind.StandardOutput
                        ? ToolOutputStream.StandardOutput
                        : ToolOutputStream.StandardError,
                    item.RawBytes,
                    item.OccurredAtUtc);
            }
        }

        await Task.CompletedTask;
    }
}
