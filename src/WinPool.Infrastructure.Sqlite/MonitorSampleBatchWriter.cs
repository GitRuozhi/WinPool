using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed class MonitorSampleBatchWriter : IAsyncDisposable
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease writeOwner;
    private readonly Channel<MonitorSample> channel;
    private readonly int maximumBatchSize;
    private readonly TimeSpan maximumBatchDelay;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task writerTask;
    private long rejectedSamples;
    private long enqueuedSamples;
    private long persistedSamples;

    public MonitorSampleBatchWriter(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner,
        int capacity = 8_192,
        int maximumBatchSize = 1_000,
        TimeSpan? maximumBatchDelay = null)
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
        channel = Channel.CreateBounded<MonitorSample>(
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

    public bool TryEnqueue(MonitorSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (channel.Writer.TryWrite(sample))
        {
            Interlocked.Increment(ref enqueuedSamples);
            return true;
        }

        Interlocked.Increment(ref rejectedSamples);
        return false;
    }

    public async ValueTask EnqueueAsync(
        MonitorSample sample,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        await channel.Writer.WriteAsync(sample, cancellationToken);
        Interlocked.Increment(ref enqueuedSamples);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var target = Interlocked.Read(ref enqueuedSamples);
        while (Interlocked.Read(ref persistedSamples) < target)
        {
            if (writerTask.IsCompleted)
            {
                await writerTask;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                cancellationToken);
        }
    }

    public async Task CompleteAndFlushAsync(CancellationToken cancellationToken = default)
    {
        channel.Writer.TryComplete();
        await writerTask.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        channel.Writer.TryComplete();
        try
        {
            await writerTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            shutdown.Cancel();
            await writerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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
        var batch = new List<MonitorSample>(maximumBatchSize);
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

    private async Task WriteBatchAsync(
        IReadOnlyList<MonitorSample> batch,
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
                read_bytes_per_sec, write_bytes_per_sec, queue_length)
            VALUES (
                $session, $device, $timestamp, $activity,
                $read, $write, $queue);
            """;
        var session = command.Parameters.Add("$session", SqliteType.Text);
        var device = command.Parameters.Add("$device", SqliteType.Text);
        var timestamp = command.Parameters.Add("$timestamp", SqliteType.Integer);
        var activity = command.Parameters.Add("$activity", SqliteType.Real);
        var read = command.Parameters.Add("$read", SqliteType.Real);
        var write = command.Parameters.Add("$write", SqliteType.Real);
        var queue = command.Parameters.Add("$queue", SqliteType.Real);

        foreach (var sample in batch)
        {
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
            queue.Value = Metric(sample, MonitorMetricKind.AverageQueueLength);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        Interlocked.Add(ref persistedSamples, batch.Count);
    }

    private static double Metric(MonitorSample sample, MonitorMetricKind kind) =>
        sample.Values.FirstOrDefault(value => value.Kind == kind)?.Value ?? 0d;
}
