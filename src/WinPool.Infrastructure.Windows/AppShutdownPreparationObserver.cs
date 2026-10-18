using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>One subscription per App; cancellation of an obsolete attempt never tears down the save connection.</summary>
public sealed class AppShutdownPreparationObserver(IAgentConnection connection, int processId,
    DateTimeOffset startedAtUtc, Func<PrepareAgentShutdownEvent, CancellationToken, Task<bool>> prepare,
    Action<AgentShutdownPreparationEndedEvent> ended) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Dictionary<Guid, CancellationTokenSource> attempts = [];
    private Task loop = Task.CompletedTask;
    public void Start() => loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await foreach (var report in connection.WatchAsync(lifetime.Token))
                    {
                        if (report is PrepareAgentShutdownEvent request && request.AppProcessId == processId
                            && request.AppStartedAtUtc.ToUnixTimeMilliseconds() == startedAtUtc.ToUnixTimeMilliseconds())
                        {
                            CancellationTokenSource cancellation;
                            lock (sync)
                            {
                                if (attempts.ContainsKey(request.ShutdownAttemptId)) continue;
                                cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                                attempts.Add(request.ShutdownAttemptId, cancellation);
                            }
                            _ = PrepareAsync(request, cancellation);
                        }
                        else if (report is AgentShutdownPreparationEndedEvent completed)
                        {
                            lock (sync) if (attempts.Remove(completed.ShutdownAttemptId, out var cancellation)) cancellation.Cancel();
                            ended(completed);
                        }
                        else if (report is AgentStateReseedEvent reseed)
                        {
                            Guid[] obsolete;
                            lock (sync) obsolete = attempts.Keys.ToArray();
                            foreach (var attempt in obsolete)
                            {
                                lock (sync) if (attempts.Remove(attempt, out var cancellation)) cancellation.Cancel();
                                ended(new AgentShutdownPreparationEndedEvent(attempt,
                                    reseed.Snapshot.ShutdownStatus.State is AgentLifecycleState.ShuttingDown or AgentLifecycleState.Stopped,
                                    reseed.OccurredAtUtc));
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException) { }
                await Task.Delay(1000, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task PrepareAsync(PrepareAgentShutdownEvent request, CancellationTokenSource cancellation)
    {
        try
        {
            var ready = await prepare(request, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                await connection.SendAsync(new AcknowledgeAgentShutdownPreparationRequest(request.ShutdownAttemptId,
                    request.AppInstanceId, ready, CorrelationId.New()), lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!lifetime.IsCancellationRequested)
                try { await connection.SendAsync(new AcknowledgeAgentShutdownPreparationRequest(request.ShutdownAttemptId,
                    request.AppInstanceId, false, CorrelationId.New()), lifetime.Token); }
                catch (Exception replyException) when (replyException is IOException or InvalidOperationException or OperationCanceledException) { }
        }
        // The attempt stays registered through Ended, making duplicate prepare frames idempotent.
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lock (sync) foreach (var cancellation in attempts.Values) cancellation.Cancel();
    }
}
