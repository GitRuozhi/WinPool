using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinPool.Ipc;

/// <summary>Bounded control-message bodies. This is not a file-transfer protocol.</summary>
public sealed class IpcPayloadBusyException(string code) : IOException(code);
public sealed class IpcPayloadTransferTimeoutException(Exception? innerException = null)
    : TimeoutException("The control body transfer timed out.", innerException);

public sealed class IpcPayloadTransfer : IAsyncDisposable
{
    private const string RejectedType = "agent.control.payload_rejected";
    private const string AbortType = "agent.control.payload_abort";
    private const string ChunkType = "agent.control.payload_chunk";
    // Account for base64 strings and encoded frames as well as the raw block.
    private const int ChunkBytes = 128 * 1024;
    private const long MaximumEncodedBytes = 65L * 1024 * 1024;
    private static readonly ConcurrentDictionary<Guid, Outgoing> OutgoingTransfers = new();
    private static readonly SemaphoreSlim Transfers = new(2, 2);
    // Reserve the maximum for an outgoing body before serialization. This
    // prevents two growing temporary files from deadlocking at the disk budget.
    private static readonly SemaphoreSlim ReservationGate = new(1, 1);
    private static readonly SemaphoreSlim TemporaryBudget = new(128, 128);
    private static readonly SemaphoreSlim ParseGate = new(1, 1);
    private static readonly JsonSerializerOptions Options = IpcJson.CreateOptions();
    private readonly Dictionary<Guid, Incoming> incoming = [];
    private readonly Dictionary<Guid, Guid> rejectedIncoming = [];
    private readonly SemaphoreSlim? responseFrameGate;
    private readonly Action<Guid, string>? bodyRejected;
    private readonly SemaphoreSlim parseGate;
    private readonly Channel<IpcEnvelope> completed = Channel.CreateBounded<IpcEnvelope>(new BoundedChannelOptions(32)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly List<Task> parsing = [];
    private CancellationTokenSource? readerCancellation;
    private Task? readerTask;

    public IpcPayloadTransfer(SemaphoreSlim? largeParseGate = null, SemaphoreSlim? responseFrameGate = null,
        Action<Guid, string>? bodyRejected = null)
    {
        parseGate = largeParseGate ?? ParseGate;
        this.responseFrameGate = responseFrameGate;
        this.bodyRejected = bodyRejected;
    }

    public static async Task WriteAsync(Stream stream, IpcEnvelope envelope, SemaphoreSlim frameGate,
        CancellationToken cancellationToken, Action? onFrameStarted = null)
    {
        if (!MayContainLargeBody(envelope))
        {
            await frameGate.WaitAsync(cancellationToken);
            using var frameTimeout = new CancellationTokenSource(IpcProtocol.RequestTransferTimeout);
            try { onFrameStarted?.Invoke(); await IpcFrameCodec.WriteAsync(stream, envelope, frameTimeout.Token); }
            finally { frameGate.Release(); }
            return;
        }
        await Transfers.WaitAsync(cancellationToken);
        var reserved = 0;
        try
        {
            var small = await TryEncodeSmallAsync(envelope, cancellationToken);
            if (small is not null)
            {
                await frameGate.WaitAsync(cancellationToken);
                using var frameTimeout = new CancellationTokenSource(IpcProtocol.RequestTransferTimeout);
                try { onFrameStarted?.Invoke(); await IpcFrameCodec.WriteAsync(stream, small, frameTimeout.Token); }
                finally { frameGate.Release(); }
                return;
            }
            await ReservationGate.WaitAsync(cancellationToken);
            try { for (; reserved < 65; reserved++) await TemporaryBudget.WaitAsync(cancellationToken); }
            finally { ReservationGate.Release(); }
            await using var file = TemporaryFile();
            await JsonSerializer.SerializeAsync(new BoundedWriteStream(file), envelope, Options, cancellationToken);
            file.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            file.Position = 0;
            var transferId = Guid.NewGuid();
            using var outgoing = new Outgoing(envelope.CorrelationId, cancellationToken);
            OutgoingTransfers[transferId] = outgoing;
            var transferCancellation = outgoing.Cancellation.Token;
            var buffer = new byte[ChunkBytes];
            long offset = 0;
            int count;
            try
            {
            while ((count = await file.ReadAsync(buffer, transferCancellation)) != 0)
            {
                var chunk = new Chunk(transferId, envelope.MessageType, file.Length, hash, offset,
                    Convert.ToBase64String(buffer, 0, count));
                var frame = new IpcEnvelope(IpcProtocol.CurrentVersion, Guid.NewGuid(), envelope.CorrelationId,
                    ChunkType, envelope.SentAt, JsonSerializer.SerializeToElement(chunk, Options));
                await frameGate.WaitAsync(transferCancellation);
                using var frameTimeout = new CancellationTokenSource(IpcProtocol.RequestTransferTimeout);
                try { onFrameStarted?.Invoke(); await IpcFrameCodec.WriteAsync(stream, frame, frameTimeout.Token); }
                finally { frameGate.Release(); }
                offset += count;
                await Task.Yield();
            }
            }
            catch (OperationCanceledException) when (transferCancellation.IsCancellationRequested)
            {
                using var abortTimeout = new CancellationTokenSource(IpcProtocol.RequestTransferTimeout);
                var abort = new IpcEnvelope(IpcProtocol.CurrentVersion, Guid.NewGuid(), envelope.CorrelationId,
                    AbortType, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(transferId));
                await frameGate.WaitAsync(abortTimeout.Token);
                try { await IpcFrameCodec.WriteAsync(stream, abort, abortTimeout.Token); }
                finally { frameGate.Release(); }
                if (outgoing.Rejection is { } rejection) throw new IpcPayloadBusyException(rejection);
                throw;
            }
            finally { OutgoingTransfers.TryRemove(transferId, out _); }
        }
        finally
        {
            if (reserved != 0) TemporaryBudget.Release(reserved);
            Transfers.Release();
        }
    }

    public async Task<IpcEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken,
        TimeSpan? frameTransferTimeout = null)
    {
        if (readerTask is null)
        {
            readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readerTask = PumpAsync(stream, readerCancellation.Token, frameTransferTimeout);
        }
        try { return await completed.Reader.ReadAsync(cancellationToken); }
        catch (ChannelClosedException exception) when (exception.InnerException is EndOfStreamException)
        { throw new EndOfStreamException("The control stream ended.", exception); }
        catch (ChannelClosedException exception) when (exception.InnerException is IpcPayloadTransferTimeoutException timeout)
        { throw new IpcPayloadTransferTimeoutException(timeout); }
        catch (ChannelClosedException exception)
        { throw new IOException("The control body reader ended.", exception.InnerException ?? exception); }
    }

    private async Task PumpAsync(Stream stream, CancellationToken cancellationToken, TimeSpan? frameTransferTimeout)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
                await completed.Writer.WriteAsync(await ReadNextAsync(stream, cancellationToken, frameTransferTimeout), cancellationToken);
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            try { await Task.WhenAll(parsing); }
            catch (Exception exception) { failure ??= exception; }
            completed.Writer.TryComplete(failure);
        }
    }

    private async Task<IpcEnvelope> ReadNextAsync(Stream stream, CancellationToken cancellationToken,
        TimeSpan? frameTransferTimeout)
    {
        while (true)
        {
            using var transferDeadline = incoming.Count == 0 ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (transferDeadline is not null)
            {
                var remaining = incoming.Values.Min(value => IpcProtocol.RequestTransferTimeout - Stopwatch.GetElapsedTime(value.StartedAt));
                if (remaining <= TimeSpan.Zero) throw new IpcPayloadTransferTimeoutException();
                transferDeadline.CancelAfter(remaining);
            }
            IpcEnvelope envelope;
            try { envelope = await IpcFrameCodec.ReadAsync(stream, transferDeadline?.Token ?? cancellationToken, frameTransferTimeout); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new IpcPayloadTransferTimeoutException(); }
            if (envelope.MessageType == RejectedType)
            {
                var rejection = envelope.Payload.Deserialize<Rejection>(Options)
                    ?? throw new InvalidDataException("The control body rejection is missing.");
                if (OutgoingTransfers.TryGetValue(rejection.TransferId, out var outgoing)
                    && outgoing.CorrelationId == envelope.CorrelationId)
                {
                    outgoing.Reject(rejection.Code);
                }
                bodyRejected?.Invoke(envelope.CorrelationId, rejection.Code);
                continue;
            }
            if (envelope.MessageType == AbortType)
            {
                var id = envelope.Payload.GetGuid();
                rejectedIncoming.Remove(id);
                if (incoming.Remove(id, out var abandoned))
                {
                    if (abandoned.CorrelationId != envelope.CorrelationId)
                        throw new InvalidDataException("The abort belongs to another control body.");
                    await abandoned.DisposeAsync();
                }
                continue;
            }
            if (envelope.MessageType != ChunkType) return envelope;
            var chunk = envelope.Payload.Deserialize<Chunk>(Options)
                ?? throw new InvalidDataException("The control body chunk is missing.");
            if (chunk.TransferId == Guid.Empty || chunk.TotalLength is <= 0 or > MaximumEncodedBytes
                || string.IsNullOrEmpty(chunk.Data) || chunk.Sha256?.Length != 64
                || string.IsNullOrWhiteSpace(chunk.MessageType) || chunk.MessageType.Length > 256
                || chunk.MessageType is not (AgentControlMessageTypes.SaveSimulationDocument
                    or AgentControlMessageTypes.CommitSimulationEdit or AgentControlMessageTypes.Response)
                || chunk.Data.Length > ((ChunkBytes + 2) / 3) * 4)
                throw new InvalidDataException("The control body chunk exceeds its budget.");
            if (rejectedIncoming.TryGetValue(chunk.TransferId, out var rejectedCorrelation))
            {
                if (rejectedCorrelation != envelope.CorrelationId) throw new InvalidDataException("A rejected transfer identity changed.");
                if (chunk.Offset + DecodedLength(chunk.Data) == chunk.TotalLength) rejectedIncoming.Remove(chunk.TransferId);
                continue;
            }
            if (!incoming.TryGetValue(chunk.TransferId, out var transfer))
            {
                if (chunk.Offset != 0) throw new InvalidDataException("The control body transfer is out of order.");
                if (!Transfers.Wait(0))
                {
                    await RejectIncomingAsync(stream, envelope, chunk, cancellationToken);
                    continue;
                }
                var required = checked((int)((chunk.TotalLength + 1024 * 1024 - 1) / (1024 * 1024)));
                var reserved = 0;
                try
                {
                    for (; reserved < required; reserved++)
                        if (!TemporaryBudget.Wait(0)) throw new IpcPayloadBusyException("ipc.payload.temporary_budget_busy");
                    transfer = new(TemporaryFile(), chunk, envelope.CorrelationId, reserved);
                    incoming.Add(chunk.TransferId, transfer);
                }
                catch (IpcPayloadBusyException)
                {
                    if (reserved != 0) TemporaryBudget.Release(reserved);
                    Transfers.Release();
                    await RejectIncomingAsync(stream, envelope, chunk, cancellationToken);
                    continue;
                }
                catch
                {
                    if (reserved != 0) TemporaryBudget.Release(reserved);
                    Transfers.Release();
                    throw;
                }
            }
            if (transfer.CorrelationId != envelope.CorrelationId || transfer.Header.MessageType != chunk.MessageType
                || transfer.Header.TotalLength != chunk.TotalLength || transfer.Header.Sha256 != chunk.Sha256
                || transfer.File.Length != chunk.Offset)
                throw new InvalidDataException("The control body identity or sequence changed.");
            var bytes = Convert.FromBase64String(chunk.Data);
            if (bytes.Length is <= 0 or > ChunkBytes || chunk.Offset + bytes.Length > chunk.TotalLength)
                throw new InvalidDataException("The control body chunk length is invalid.");
            await transfer.File.WriteAsync(bytes, cancellationToken);
            if (transfer.File.Length != chunk.TotalLength) continue;
            incoming.Remove(chunk.TransferId);
            parsing.RemoveAll(task => task.IsCompletedSuccessfully);
            parsing.Add(Task.Run(async () =>
            {
                try
                {
                    transfer.File.Position = 0;
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(transfer.File, cancellationToken));
                    if (!StringComparer.Ordinal.Equals(actual, chunk.Sha256))
                        throw new InvalidDataException("The control body hash did not match.");
                    transfer.File.Position = 0;
                    await parseGate.WaitAsync(cancellationToken);
                    try
                    {
                        var complete = await JsonSerializer.DeserializeAsync<IpcEnvelope>(transfer.File, Options, cancellationToken)
                            ?? throw new InvalidDataException("The control body is empty.");
                        if (complete.ProtocolVersion != IpcProtocol.CurrentVersion || complete.MessageId == Guid.Empty
                            || complete.CorrelationId != transfer.CorrelationId || complete.MessageType != chunk.MessageType
                            || !MayContainLargeBody(complete))
                            throw new InvalidDataException("The completed control body identity changed.");
                        await completed.Writer.WriteAsync(complete, cancellationToken);
                    }
                    finally { parseGate.Release(); }
                }
                catch (Exception exception)
                {
                    completed.Writer.TryComplete(exception);
                    readerCancellation?.Cancel();
                    throw;
                }
                finally { await transfer.DisposeAsync(); }
            }, CancellationToken.None));
        }
    }

    private async Task RejectIncomingAsync(Stream stream, IpcEnvelope envelope, Chunk chunk, CancellationToken cancellationToken)
    {
        if (responseFrameGate is null || rejectedIncoming.Count >= 32)
            throw new IpcPayloadBusyException("ipc.payload.busy");
        if (chunk.Offset + DecodedLength(chunk.Data) < chunk.TotalLength)
            rejectedIncoming[chunk.TransferId] = envelope.CorrelationId;
        bodyRejected?.Invoke(envelope.CorrelationId, "ipc.payload.busy");
        var rejection = new IpcEnvelope(IpcProtocol.CurrentVersion, Guid.NewGuid(), envelope.CorrelationId,
            RejectedType, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new Rejection(chunk.TransferId, "ipc.payload.busy"), Options));
        await responseFrameGate.WaitAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(IpcProtocol.RequestTransferTimeout);
        try { await IpcFrameCodec.WriteAsync(stream, rejection, timeout.Token); }
        finally { responseFrameGate.Release(); }
    }

    private static int DecodedLength(string value) => value.Length / 4 * 3
        - (value.EndsWith("==", StringComparison.Ordinal) ? 2 : value.EndsWith('=') ? 1 : 0);

    private static async Task<byte[]?> TryEncodeSmallAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        await using var memory = new SmallBodyStream();
        try
        {
            await JsonSerializer.SerializeAsync(memory, envelope, Options, cancellationToken);
            return memory.ToArray();
        }
        catch (SmallBodyExceededException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        readerCancellation?.Cancel();
        if (readerTask is not null) await readerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (var transfer in incoming.Values) await transfer.DisposeAsync();
        incoming.Clear();
        readerCancellation?.Dispose();
    }

    private static bool MayContainLargeBody(IpcEnvelope envelope) =>
        envelope.MessageType == AgentControlMessageTypes.SaveSimulationDocument
        || envelope.MessageType == AgentControlMessageTypes.CommitSimulationEdit
        || (envelope.MessageType == AgentControlMessageTypes.Response
            && envelope.Payload.TryGetProperty("responseType", out var type)
            && type.GetString() is "SimulationDocumentLoadedResponse" or "ManageInventoryLoadedResponse"
                or "AgentRealOperationEvidenceResponse");

    private static FileStream TemporaryFile() => new(Path.Combine(Path.GetTempPath(), "WinPool-body-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, ChunkBytes,
        FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);

    private sealed record Rejection(Guid TransferId, string Code);
    private sealed class Outgoing(Guid correlationId, CancellationToken cancellationToken) : IDisposable
    {
        public Guid CorrelationId { get; } = correlationId;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        private readonly object sync = new();
        private bool disposed;
        public string? Rejection { get; private set; }
        public void Reject(string code)
        {
            lock (sync)
            {
                if (disposed) return;
                Rejection = code;
                Cancellation.Cancel();
            }
        }
        public void Dispose()
        {
            lock (sync) { disposed = true; Cancellation.Dispose(); }
        }
    }
    private sealed class SmallBodyExceededException : Exception;
    private sealed class SmallBodyStream : MemoryStream
    {
        public SmallBodyStream() : base(ChunkBytes) { }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > ChunkBytes) throw new SmallBodyExceededException();
            base.Write(buffer, offset, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position + buffer.Length > ChunkBytes) throw new SmallBodyExceededException();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class BoundedWriteStream(Stream target) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => target.Length;
        public override long Position { get => target.Position; set => throw new NotSupportedException(); }
        public override void Flush() => target.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => target.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count); target.Write(buffer, offset, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length); return target.WriteAsync(buffer, cancellationToken);
        }
        private void Check(int count)
        {
            if (target.Position + count > MaximumEncodedBytes)
                throw new IpcFrameTooLargeException(checked((int)(target.Position + count)));
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed record Chunk(Guid TransferId, string MessageType, long TotalLength, string Sha256, long Offset, string Data);
    private sealed record Incoming(FileStream File, Chunk Header, Guid CorrelationId, int Reserved) : IAsyncDisposable
    {
        public long StartedAt { get; } = Stopwatch.GetTimestamp();
        public async ValueTask DisposeAsync()
        {
            await File.DisposeAsync();
            TemporaryBudget.Release(Reserved);
            Transfers.Release();
        }
    }
}
