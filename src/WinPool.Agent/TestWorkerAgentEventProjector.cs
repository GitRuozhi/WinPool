using WinPool.Application;
using WinPool.Testing.Tools;

namespace WinPool.Agent;

public sealed class TestWorkerAgentEventProjector
{
    private readonly TestRunId _runId;
    private readonly CorrelationId _correlationId;
    private readonly IReadOnlyDictionary<string, ToolProcessRequest> _requests;
    private readonly ToolNativeProgressParser _progress = new();

    public TestWorkerAgentEventProjector(
        TestRunId runId,
        CorrelationId correlationId,
        IReadOnlyList<ToolProcessRequest> requests)
    {
        _runId = runId;
        _correlationId = correlationId;
        _requests = requests.ToDictionary(item => item.StepId, StringComparer.Ordinal);
    }

    public AgentTestEvent? ProjectNativeProgress(WorkerEvent workerEvent)
    {
        ArgumentNullException.ThrowIfNull(workerEvent);
        if (workerEvent.RunId != _runId ||
            !_requests.TryGetValue(workerEvent.StepId, out var request))
        {
            return null;
        }

        if (workerEvent.Code == "tool.process.exited")
        {
            _progress.Complete(_runId, workerEvent.StepId);
            return null;
        }

        var native = _progress.Consume(
            workerEvent,
            request.Invocation.ToolId,
            request.Invocation.OutputEncoding);
        return native is null
            ? null
            : new AgentTestEvent(
                new TestEvent(
                    _runId,
                    TestEventKind.Progress,
                    new ApplicationTaskEvent(
                        new ApplicationTaskId(_runId.Value),
                        _correlationId,
                        ApplicationTaskEventKind.Progress,
                        ApplicationTaskState.Running,
                        native.OccurredAtUtc,
                        native.Code,
                        native.Code,
                        string.Empty,
                        native.StepId,
                        native.Fraction)));
    }
}
