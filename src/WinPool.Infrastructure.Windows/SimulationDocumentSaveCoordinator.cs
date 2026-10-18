using System.Diagnostics;
using System.Text.Json;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

public enum SimulationSavePhase { Saved, Pending, Saving, Failed, Conflict, OutcomeUnknown, Deleted }

public sealed record SimulationSaveState(string DocumentId, long Generation, long ContentRevision,
    long DurableContentRevision, SimulationSavePhase Phase, string? Error,
    TimeSpan SchedulingDelay, TimeSpan QueueDelay, TimeSpan DurableDuration)
{
    public bool IsDurable => ContentRevision == DurableContentRevision && Phase == SimulationSavePhase.Saved;
}

/// <summary>One coalescing save owner per document. Accepted edits and their frozen identity never change on retry.</summary>
public sealed class SimulationDocumentSaveCoordinator(
    Func<SimulationEditCommit, CancellationToken, Task<StorageSystemDocument>> save,
    TimeProvider? timeProvider = null) : IDisposable
{
    private const int EarlyFlushBytes = 1024 * 1024;
    private const int MaximumPendingBytes = DocumentPayloadLimits.MaximumIntentBytes;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly Dictionary<string, DocumentQueue> documents = new(StringComparer.Ordinal);
    private bool accepting = true;
    private bool disposed;
    private long nextGeneration;
    public event Action<SimulationSaveState>? Changed;

    public SimulationSaveState? GetState(string documentId)
    { lock (sync) return documents.TryGetValue(documentId, out var queue) ? Snapshot(queue) : null; }

    public StorageSystemDocument Accept(SimulationEditCommit commit)
    {
        // This is the accepted typed-intent boundary, not a per-keystroke check.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(commit.Intent).Length;
        if (bytes > MaximumPendingBytes) throw new InvalidDataException("The atomic simulation edit exceeds 64 MiB.");
        DocumentQueue queue;
        StorageSystemDocument local;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!accepting) throw new InvalidOperationException("The workspace is preparing to close.");
            if (!documents.TryGetValue(commit.Document.Id, out queue!))
            {
                queue = new(commit.Document.Id, commit.Intent.ExpectedRevision, clock.GetTimestamp(), ++nextGeneration);
                documents.Add(queue.Id, queue);
            }
            if (queue.Stopped || queue.Phase == SimulationSavePhase.Deleted)
                throw new InvalidOperationException("This document is no longer accepting edits.");
            if (queue.PendingBytes + (queue.Inflight?.EncodedBytes ?? 0) + bytes > MaximumPendingBytes)
                throw new InvalidOperationException("Unsaved edits have reached 64 MiB. Save or export the preserved draft before applying more edits.");
            var now = clock.GetTimestamp();
            if (queue.Pending.Count == 0) queue.FirstPendingAt = now;
            queue.LastEditAt = now;
            queue.Pending.AddRange(commit.Intent.Edits);
            queue.PendingBytes += bytes;
            queue.ContentRevision++;
            // Document.Revision is a durable-batch revision; content has its own counter.
            local = commit.Document with { Revision = checked(queue.DurableRevision + (queue.Inflight is null ? 1 : 2)) };
            queue.Local = local;
            if (queue.Phase is not (SimulationSavePhase.Failed or SimulationSavePhase.Conflict or SimulationSavePhase.OutcomeUnknown))
                queue.Phase = queue.Inflight is null ? SimulationSavePhase.Pending : SimulationSavePhase.Saving;
            if (queue.PendingBytes >= EarlyFlushBytes) queue.FlushRequested = true;
            Pulse(queue);
            queue.Pump ??= Task.Run(() => PumpAsync(queue));
        }
        Publish(queue);
        return local;
    }

    public void SetAccepting(bool value) { lock (sync) accepting = value; }

    public async Task FlushAsync(string documentId, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task changed;
            lock (sync)
            {
                if (!documents.TryGetValue(documentId, out var queue) || queue.Phase == SimulationSavePhase.Deleted) return;
                if (queue.Phase is SimulationSavePhase.Failed or SimulationSavePhase.Conflict or SimulationSavePhase.OutcomeUnknown)
                    throw new IOException(queue.Error ?? "The simulation draft has not been saved.");
                if (queue.Pending.Count == 0 && queue.Inflight is null) return;
                queue.FlushRequested = true;
                changed = queue.NextChange.Task;
                Wake(queue);
            }
            // Cancelling a caller never cancels an already submitted CAS.
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task FlushAllAsync(CancellationToken cancellationToken = default)
    {
        string[] ids;
        lock (sync) ids = documents.Keys.ToArray();
        return Task.WhenAll(ids.Select(id => FlushAsync(id, cancellationToken)));
    }

    public async Task RetryAsync(string documentId, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (!documents.TryGetValue(documentId, out var queue)) return;
            if (queue.Phase == SimulationSavePhase.Conflict)
                throw new SimulationCommitConflictException();
            queue.Error = null;
            queue.Phase = queue.Inflight is null ? SimulationSavePhase.Pending : SimulationSavePhase.Saving;
            queue.FlushRequested = true;
            Pulse(queue);
        }
        await FlushAsync(documentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletion first resolves any issued CAS. Pending, never-issued edits are cancelled.</summary>
    public async Task<long> PrepareDeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task changed;
            lock (sync)
            {
                if (!documents.TryGetValue(documentId, out var queue)) return -1;
                queue.Stopped = true;
                if (queue.Inflight is null) return queue.DurableRevision;
                if (queue.Phase is SimulationSavePhase.Failed or SimulationSavePhase.Conflict or SimulationSavePhase.OutcomeUnknown)
                    throw new IOException(queue.Error ?? "Resolve the issued save before deleting this document.");
                changed = queue.NextChange.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void CancelDelete(string documentId)
    { lock (sync) if (documents.TryGetValue(documentId, out var queue)) { queue.Stopped = false; Pulse(queue); } }

    public void PauseDocument(string documentId)
    { lock (sync) if (documents.TryGetValue(documentId, out var queue)) queue.Stopped = true; }

    public void Discard(string documentId)
    {
        lock (sync)
        {
            if (!documents.TryGetValue(documentId, out var queue)) return;
            queue.Generation = ++nextGeneration;
            queue.Pending.Clear(); queue.PendingBytes = 0;
            queue.Local = null; queue.Inflight = null;
            queue.Stopped = true; queue.Phase = SimulationSavePhase.Deleted;
            Pulse(queue);
        }
    }

    public void DiscardAll()
    { string[] ids; lock (sync) ids = documents.Keys.ToArray(); foreach (var id in ids) Discard(id); }

    public void Forget(string documentId)
    {
        Discard(documentId);
        lock (sync) documents.Remove(documentId);
    }

    private async Task PumpAsync(DocumentQueue queue)
    {
        while (true)
        {
            Batch? batch = null;
            TimeSpan? delay = null;
            lock (sync)
            {
                if (disposed || queue.Phase == SimulationSavePhase.Deleted) return;
                if (queue.Phase is not (SimulationSavePhase.Failed or SimulationSavePhase.Conflict or SimulationSavePhase.OutcomeUnknown))
                {
                    if (queue.Inflight is not null) batch = queue.Inflight; // Explicit retry preserves the commit ID.
                    else if (!queue.Stopped && queue.Pending.Count > 0)
                    {
                        var now = clock.GetTimestamp();
                        var idle = TimeSpan.FromMilliseconds(300) - clock.GetElapsedTime(queue.LastEditAt, now);
                        var maximum = TimeSpan.FromSeconds(2) - clock.GetElapsedTime(queue.FirstPendingAt, now);
                        delay = idle < maximum ? idle : maximum;
                        if (queue.FlushRequested || delay <= TimeSpan.Zero)
                        {
                            var intent = new SimulationCommitIntent(queue.Id, queue.DurableRevision,
                                Guid.NewGuid().ToString("N"), queue.Pending.ToArray());
                            var candidate = queue.Local! with { Revision = checked(queue.DurableRevision + 1) };
                            batch = new(new(candidate, intent), queue.ContentRevision, queue.Generation, now, queue.PendingBytes);
                            queue.QueueDelay = queue.PreviousCompletedAt > queue.FirstPendingAt
                                ? clock.GetElapsedTime(queue.FirstPendingAt, queue.PreviousCompletedAt) : TimeSpan.Zero;
                            queue.SchedulingDelay = clock.GetElapsedTime(queue.FirstPendingAt, now) - queue.QueueDelay;
                            queue.Inflight = batch; queue.Pending.Clear(); queue.PendingBytes = 0;
                            queue.FlushRequested = false; queue.Phase = SimulationSavePhase.Saving;
                            Pulse(queue);
                        }
                    }
                }
            }
            if (batch is null)
            {
                if (delay is { } remaining && remaining > TimeSpan.Zero)
                    await queue.Wake.WaitAsync(remaining).ConfigureAwait(false);
                else await queue.Wake.WaitAsync().ConfigureAwait(false);
                continue;
            }
            Publish(queue);
            Exception? failure = null;
            try
            {
                var stored = await save(batch.Commit, CancellationToken.None).ConfigureAwait(false);
                if (!ReferenceEquals(stored, batch.Commit.Document)
                    && SimulationDocumentCodec.Encode(stored).Sha256 != SimulationDocumentCodec.Encode(batch.Commit.Document).Sha256)
                    throw new SimulationCommitConflictException();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
            { failure = exception; }
            lock (sync)
            {
                if (queue.Generation != batch.Generation || queue.Phase == SimulationSavePhase.Deleted) continue;
                queue.DurableDuration = clock.GetElapsedTime(batch.StartedAt);
                if (failure is null)
                {
                    queue.DurableRevision = batch.Commit.Intent.ExpectedRevision + 1;
                    queue.DurableContentRevision = batch.ContentRevision;
                    queue.Inflight = null; queue.Error = null;
                    queue.PreviousCompletedAt = clock.GetTimestamp();
                    queue.Phase = queue.Pending.Count == 0 ? SimulationSavePhase.Saved : SimulationSavePhase.Pending;
                    // No full document is published by a receipt: later local content remains untouched.
                    if (queue.Pending.Count > 0 && clock.GetElapsedTime(queue.FirstPendingAt) >= TimeSpan.FromSeconds(2))
                        queue.FlushRequested = true;
                }
                else
                {
                    queue.Error = failure.Message;
                    queue.Phase = failure switch
                    {
                        SimulationCommitConflictException => SimulationSavePhase.Conflict,
                        SimulationCommitOutcomeUnknownException => SimulationSavePhase.OutcomeUnknown,
                        _ => SimulationSavePhase.Failed
                    };
                }
                Pulse(queue);
            }
            Publish(queue);
        }
    }

    private static void Wake(DocumentQueue queue)
    { if (queue.Wake.CurrentCount == 0) queue.Wake.Release(); }
    private static void Pulse(DocumentQueue queue)
    {
        var previous = queue.NextChange;
        queue.NextChange = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
        Wake(queue);
    }
    private static SimulationSaveState Snapshot(DocumentQueue queue) => new(queue.Id, queue.Generation,
        queue.ContentRevision, queue.DurableContentRevision, queue.Phase, queue.Error,
        queue.SchedulingDelay, queue.QueueDelay, queue.DurableDuration);

    private void Publish(DocumentQueue queue)
    {
        SimulationSaveState state;
        lock (sync)
        {
            if (!documents.TryGetValue(queue.Id, out var current) || !ReferenceEquals(current, queue)) return;
            state = Snapshot(queue);
        }
        Changed?.Invoke(state);
    }

    public void Dispose()
    { lock (sync) { disposed = true; accepting = false; foreach (var queue in documents.Values) Pulse(queue); } }

    private sealed record Batch(SimulationEditCommit Commit, long ContentRevision, long Generation, long StartedAt, int EncodedBytes);
    private sealed class DocumentQueue(string id, long revision, long now, long generation)
    {
        public string Id { get; } = id;
        public long Generation = generation, ContentRevision, DurableContentRevision, DurableRevision = revision;
        public long FirstPendingAt = now, LastEditAt = now, PreviousCompletedAt;
        public int PendingBytes;
        public bool FlushRequested, Stopped;
        public SimulationSavePhase Phase = SimulationSavePhase.Pending;
        public string? Error;
        public TimeSpan SchedulingDelay, QueueDelay, DurableDuration;
        public StorageSystemDocument? Local;
        public List<SimulationEditRequest> Pending { get; } = [];
        public Batch? Inflight;
        public Task? Pump;
        public SemaphoreSlim Wake { get; } = new(0, 1);
        public TaskCompletionSource NextChange = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
