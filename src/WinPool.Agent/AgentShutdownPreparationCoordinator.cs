using WinPool.Application;

namespace WinPool.Agent;

public interface IAgentShutdownPreparation
{
    Task<bool> PrepareClientsAsync(Guid attemptId, ShutdownReason reason, CancellationToken cancellationToken);
    bool AcknowledgePreparation(AcknowledgeAgentShutdownPreparationRequest request, AgentVerifiedPeer? peer);
    void EndPreparation(Guid attemptId, bool proceeding);
}

/// <summary>Flush negotiation precedes Closing, so App save/lookup requests remain available.</summary>
internal sealed class AgentShutdownPreparationCoordinator(AgentProcessRegistry registry,
    IProcessIncarnationVerifier verifier, string applicationPath, Action<AgentEvent> publish)
    : IAgentShutdownPreparation
{
    private readonly object sync = new();
    private readonly Dictionary<(Guid Attempt, ProcessInstanceId Instance), Pending> pending = [];

    public async Task<bool> PrepareClientsAsync(Guid attemptId, ShutdownReason reason, CancellationToken cancellationToken)
    {
        var clients = registry.Snapshot().Where(process => process.Kind == AgentManagedProcessKind.MainApplication
            && process.State is not (SupervisedProcessState.Exited or SupervisedProcessState.Failed)).ToArray();
        var tasks = new List<Task<bool>>();
        foreach (var client in clients)
        {
            var observation = verifier.Observe(client.ProcessId);
            if (observation.State == ProcessIncarnationState.Exited) continue;
            if (!verifier.Matches(client, applicationPath)) return false;
            var request = new Pending(client);
            lock (sync) pending.Add((attemptId, client.ProcessInstanceId), request);
            tasks.Add(request.Completion.Task);
            publish(new PrepareAgentShutdownEvent(attemptId, client.ProcessInstanceId, client.ProcessId,
                client.StartedAtUtc, reason, DateTimeOffset.UtcNow));
        }
        if (tasks.Count == 0) return true;
        try
        {
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            return results.All(result => result);
        }
        catch (TimeoutException) { return false; }
        finally
        {
            lock (sync) foreach (var key in pending.Keys.Where(key => key.Attempt == attemptId).ToArray()) pending.Remove(key);
        }
    }

    public bool AcknowledgePreparation(AcknowledgeAgentShutdownPreparationRequest request, AgentVerifiedPeer? peer)
    {
        lock (sync)
        {
            if (peer is null || peer.ProcessInstanceId != request.AppInstanceId
                || !pending.TryGetValue((request.ShutdownAttemptId, request.AppInstanceId), out var entry)
                || peer.ProcessId != entry.Process.ProcessId || peer.StartedAtUtc != entry.Process.StartedAtUtc
                || !verifier.Matches(entry.Process, applicationPath)) return false;
            return entry.Completion.TrySetResult(request.Prepared);
        }
    }

    public void EndPreparation(Guid attemptId, bool proceeding)
    {
        lock (sync)
        {
            foreach (var key in pending.Keys.Where(key => key.Attempt == attemptId).ToArray())
            {
                pending[key].Completion.TrySetResult(false);
                pending.Remove(key);
            }
        }
        publish(new AgentShutdownPreparationEndedEvent(attemptId, proceeding, DateTimeOffset.UtcNow));
    }

    private sealed class Pending(AgentManagedProcess process)
    {
        public AgentManagedProcess Process { get; } = process;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
