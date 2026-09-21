using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed class MonitorSampleBatchWriter : IAsyncDisposable
{
    private readonly ISqliteDatabaseStore store;
    private readonly AgentWriteOwnerLease writeOwner;
    private readonly Channel<PendingEntry> channel;
    private readonly int maximumBatchSize;
    private readonly TimeSpan maximumBatchDelay;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task writerTask;
    // An entry is registered before it reaches the channel because the single
    // reader is allowed to commit it immediately.  The entry becomes visible
    // to diagnostics/Flush only after the caller has observed a successful
    // enqueue.  That distinction keeps a cancelled or full-channel attempt
    // out of a flush boundary without racing a fast writer.
    private readonly ConcurrentDictionary<long, PendingEntry> pendingEntries = new();
    private long rejectedSamples;
    private long nextSequence;
    private Exception? failure;

    public MonitorSampleBatchWriter(
        ISqliteDatabaseStore store,
        AgentWriteOwnerLease writeOwner,
        int capacity = 8_192,
        int maximumBatchSize = 1_000,
        TimeSpan? maximumBatchDelay = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writeOwner);
        writeOwner.AssertOwnership(store);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (maximumBatchSize is <= 0 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBatchSize));
        }

        this.store = store;
        this.writeOwner = writeOwner;
        this.maximumBatchSize = maximumBatchSize;
        this.maximumBatchDelay = maximumBatchDelay ?? TimeSpan.FromMilliseconds(250);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        channel = Channel.CreateBounded<PendingEntry>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        writerTask = RunAsync(shutdown.Token);
    }

    public long RejectedSamples => Interlocked.Read(ref rejectedSamples);

    /// <summary>
    /// Samples accepted by this writer which are still awaiting a commit. A
    /// nonzero value is normal during the configured batch delay and is never
    /// itself a loss claim.
    /// </summary>
    public int PendingSamples => pendingEntries.Values.Count(entry => entry.IsAccepted);

    /// <summary>
    /// Age of the oldest accepted sample which has not yet committed, measured
    /// with the injected monotonic time provider.
    /// </summary>
    public long OldestPendingMilliseconds
    {
        get
        {
            if (pendingEntries.IsEmpty)
            {
                return 0;
            }

            var oldestTimestamp = long.MaxValue;
            foreach (var entry in pendingEntries.Values)
            {
                if (entry.IsAccepted)
                {
                    oldestTimestamp = Math.Min(oldestTimestamp, entry.EnqueuedTimestamp);
                }
            }

            return oldestTimestamp == long.MaxValue
                ? 0
                : Math.Max(0, (long)timeProvider.GetElapsedTime(oldestTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Only a faulted writer can turn its accepted, uncommitted entries into a
    /// confirmed loss. Healthy queued entries remain pending rather than being
    /// reported as missing data.
    /// </summary>
    public long ConfirmedLostSamples => Failure is null ? 0 : PendingSamples;

    public Exception? Failure => Volatile.Read(ref failure);

    public bool TryEnqueue(MonitorSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (Failure is not null || writerTask.IsCompleted)
        {
            Interlocked.Increment(ref rejectedSamples);
            return false;
        }

        var entry = CreatePendingEntry(sample);
        pendingEntries[entry.Sequence] = entry;
        if (channel.Writer.TryWrite(entry))
        {
            entry.MarkAccepted();
            return true;
        }

        pendingEntries.TryRemove(entry.Sequence, out _);
        Interlocked.Increment(ref rejectedSamples);
        return false;
    }

    public async ValueTask EnqueueAsync(
        MonitorSample sample,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ThrowIfFaulted();
        var entry = CreatePendingEntry(sample);
        pendingEntries[entry.Sequence] = entry;
        try
        {
            await channel.Writer.WriteAsync(entry, cancellationToken);
            entry.MarkAccepted();
        }
        catch (ChannelClosedException) when (Failure is { } exception)
        {
            pendingEntries.TryRemove(entry.Sequence, out _);
            Interlocked.Increment(ref rejectedSamples);
            throw new IOException("The monitoring sample writer has failed.", exception);
        }
        catch
        {
            pendingEntries.TryRemove(entry.Sequence, out _);
            Interlocked.Increment(ref rejectedSamples);
            throw;
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        // Snapshot concrete, caller-visible accepted entries.  A numeric
        // counter cannot serve as this boundary: a fast reader may persist an
        // earlier producer's not-yet-returned write before a later accepted
        // producer is committed.  Waiting for identities is race-free and
        // remains bounded by the channel capacity.
        var target = pendingEntries.Values
            .Where(entry => entry.IsAccepted)
            .Select(entry => entry.Sequence)
            .ToArray();
        while (target.Any(sequence => pendingEntries.ContainsKey(sequence)))
        {
            if (writerTask.IsCompleted)
            {
                await writerTask;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                cancellationToken);
        }

        ThrowIfFaulted();
    }

    public async Task CompleteAndFlushAsync(CancellationToken cancellationToken = default)
    {
        channel.Writer.TryComplete();
        await writerTask.WaitAsync(cancellationToken);
        ThrowIfFaulted();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Segment disposal is the normal rotation/exit drain boundary. Do
            // not cancel a live batch and suppress the loss: callers receive a
            // confirmed drain or the original persistence failure.
            await CompleteAndFlushAsync(CancellationToken.None);
        }
        finally
        {
            shutdown.Dispose();
        }
    }

    public static string PersistedDeviceId(MonitorSample sample)
    {
        var target = sample.TargetId;
        var material = $"{target.System.Value:N}|{target.Kind}|{target.ProviderKey}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var batch = new List<PendingEntry>(maximumBatchSize);
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                batch.Clear();
                var deadline = DateTime.UtcNow + maximumBatchDelay;

                while (batch.Count < maximumBatchSize)
                {
                    while (batch.Count < maximumBatchSize && channel.Reader.TryRead(out var sample))
                    {
                        batch.Add(sample);
                    }

                    if (batch.Count >= maximumBatchSize || channel.Reader.Completion.IsCompleted)
                    {
                        break;
                    }

                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    using var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    delay.CancelAfter(remaining);
                    try
                    {
                        if (!await channel.Reader.WaitToReadAsync(delay.Token))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                if (batch.Count > 0)
                {
                    await WriteBatchAsync(batch, cancellationToken);
                }
            }
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref failure, exception, null);
            channel.Writer.TryComplete(exception);
            throw;
        }
    }

    private async Task WriteBatchAsync(
        IReadOnlyList<PendingEntry> batch,
        CancellationToken cancellationToken)
    {
        writeOwner.AssertOwnership(store);
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var deviceCommand = connection.CreateCommand();
        deviceCommand.Transaction = transaction;
        deviceCommand.CommandText = """
            INSERT INTO monitor_devices(
                session_id, device_id, sanitized_name, source_kind)
            VALUES($session, $device, $name, $source)
            ON CONFLICT(session_id, device_id) DO NOTHING;
            """;
        var deviceSession = deviceCommand.Parameters.Add("$session", SqliteType.Text);
        var deviceIdentity = deviceCommand.Parameters.Add("$device", SqliteType.Text);
        var deviceName = deviceCommand.Parameters.Add("$name", SqliteType.Text);
        var deviceSource = deviceCommand.Parameters.Add("$source", SqliteType.Integer);
        deviceCommand.Prepare();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO monitor_samples(
                session_id, device_id, timestamp_utc_ms, activity_pct,
                read_bytes_per_sec, write_bytes_per_sec,
                read_operations_per_sec, write_operations_per_sec, queue_length,
                average_latency_ms, cpu_pct, virtual_disk_active_bytes,
                virtual_disk_missing_bytes, virtual_disk_stale_bytes,
                virtual_disk_need_regeneration_bytes, virtual_disk_regenerating_bytes,
                virtual_disk_pending_deletion_bytes)
            VALUES (
                $session, $device, $timestamp, $activity,
                $read, $write, $readOps, $writeOps, $queue, $latency, $cpu,
                $vdActive, $vdMissing, $vdStale, $vdNeedRegeneration,
                $vdRegenerating, $vdPendingDeletion);
            """;
        var session = command.Parameters.Add("$session", SqliteType.Text);
        var device = command.Parameters.Add("$device", SqliteType.Text);
        var timestamp = command.Parameters.Add("$timestamp", SqliteType.Integer);
        var activity = command.Parameters.Add("$activity", SqliteType.Real);
        var read = command.Parameters.Add("$read", SqliteType.Real);
        var write = command.Parameters.Add("$write", SqliteType.Real);
        var readOps = command.Parameters.Add("$readOps", SqliteType.Real);
        var writeOps = command.Parameters.Add("$writeOps", SqliteType.Real);
        var queue = command.Parameters.Add("$queue", SqliteType.Real);
        var latency = command.Parameters.Add("$latency", SqliteType.Real);
        var cpu = command.Parameters.Add("$cpu", SqliteType.Real);
        var vdActive = command.Parameters.Add("$vdActive", SqliteType.Real);
        var vdMissing = command.Parameters.Add("$vdMissing", SqliteType.Real);
        var vdStale = command.Parameters.Add("$vdStale", SqliteType.Real);
        var vdNeedRegeneration = command.Parameters.Add("$vdNeedRegeneration", SqliteType.Real);
        var vdRegenerating = command.Parameters.Add("$vdRegenerating", SqliteType.Real);
        var vdPendingDeletion = command.Parameters.Add("$vdPendingDeletion", SqliteType.Real);

        foreach (var queued in batch)
        {
            var sample = queued.Sample;
            var persistedDeviceId = PersistedDeviceId(sample);
            deviceSession.Value = sample.SessionId.Value.ToString("N");
            deviceIdentity.Value = persistedDeviceId;
            deviceName.Value = $"{sample.TargetId.Kind} {persistedDeviceId[..8]}";
            deviceSource.Value = (int)sample.TargetId.Kind;
            await deviceCommand.ExecuteNonQueryAsync(cancellationToken);

            session.Value = sample.SessionId.Value.ToString("N");
            device.Value = persistedDeviceId;
            timestamp.Value = sample.SampledAtUtc.ToUnixTimeMilliseconds();
            activity.Value = Metric(sample, MonitorMetricKind.ActiveTimePercent);
            read.Value = Metric(sample, MonitorMetricKind.ReadBytesPerSecond);
            write.Value = Metric(sample, MonitorMetricKind.WriteBytesPerSecond);
            readOps.Value = Metric(sample, MonitorMetricKind.ReadOperationsPerSecond);
            writeOps.Value = Metric(sample, MonitorMetricKind.WriteOperationsPerSecond);
            queue.Value = Metric(sample, MonitorMetricKind.AverageQueueLength);
            latency.Value = Metric(sample, MonitorMetricKind.AverageLatencyMilliseconds);
            cpu.Value = Metric(sample, MonitorMetricKind.CpuPercent);
            vdActive.Value = Metric(sample, MonitorMetricKind.VirtualDiskActiveBytes);
            vdMissing.Value = Metric(sample, MonitorMetricKind.VirtualDiskMissingBytes);
            vdStale.Value = Metric(sample, MonitorMetricKind.VirtualDiskStaleBytes);
            vdNeedRegeneration.Value = Metric(sample, MonitorMetricKind.VirtualDiskNeedRegenerationBytes);
            vdRegenerating.Value = Metric(sample, MonitorMetricKind.VirtualDiskRegeneratingBytes);
            vdPendingDeletion.Value = Metric(sample, MonitorMetricKind.VirtualDiskPendingDeletionBytes);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var queued in batch)
        {
            pendingEntries.TryRemove(queued.Sequence, out _);
        }
    }

    private static object Metric(MonitorSample sample, MonitorMetricKind kind) =>
        sample.Values.FirstOrDefault(value => value.Kind == kind) is { } value
            ? value.Value
            : DBNull.Value;

    private PendingEntry CreatePendingEntry(MonitorSample sample) => new(
        Interlocked.Increment(ref nextSequence),
        sample,
        timeProvider.GetTimestamp());

    private void ThrowIfFaulted()
    {
        if (Failure is { } exception)
        {
            throw new IOException("The monitoring sample writer has failed.", exception);
        }
    }

    private sealed class PendingEntry(
        long sequence,
        MonitorSample sample,
        long enqueuedTimestamp)
    {
        private int accepted;

        public long Sequence { get; } = sequence;

        public MonitorSample Sample { get; } = sample;

        public long EnqueuedTimestamp { get; } = enqueuedTimestamp;

        public bool IsAccepted => Volatile.Read(ref accepted) != 0;

        public void MarkAccepted() => Volatile.Write(ref accepted, 1);
    }

}
