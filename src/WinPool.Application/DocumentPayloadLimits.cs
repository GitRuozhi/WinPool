using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinPool.Application;

public static class DocumentPayloadLimits
{
    public const int MaximumDocumentBytes = 64 * 1024 * 1024;
    public const int MaximumIntentBytes = 64 * 1024 * 1024;
    public static readonly SemaphoreSlim LargeParseGate = new(1, 1);
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<T> RunLargeAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var background = Task.Run(async () =>
        {
            await LargeParseGate.WaitAsync(cancellationToken);
            try { cancellationToken.ThrowIfCancellationRequested(); return work(); }
            finally { LargeParseGate.Release(); }
        }, cancellationToken);
        return await background.WaitAsync(cancellationToken);
    }

    public static string EncodeBody(StorageSystemDocument document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        EnsureEnvelopeFits(json, document.UpdatedAt);
        return json;
    }

    public static void EnsureEnvelopeFits(string? json, DateTimeOffset updatedAt)
    {
        if (string.IsNullOrEmpty(json)) throw new InvalidDataException("The WinPool document is empty.");
        if (Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(Prefix(updatedAt)) + 1 > MaximumDocumentBytes)
            throw new InvalidDataException("The complete WinPool document exceeds the 64 MiB limit.");
    }

    public static string EncodeExport(StorageSystemDocument document)
    {
        var json = EncodeBody(document);
        return Prefix(document.UpdatedAt) + json + "}";
    }

    public static async Task ValidateIntentAsync(SimulationCommitIntent intent, CancellationToken cancellationToken)
    {
        await using var counter = new LimitedCounter(MaximumIntentBytes);
        await JsonSerializer.SerializeAsync(counter, intent, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);
    }

    public static async Task<StorageSystemDocument> ReadExportAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek && stream.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The WinPool import exceeds the 64 MiB limit.");
        await LargeParseGate.WaitAsync(cancellationToken);
        try
        {
            StorageSystemExportEnvelope envelope;
            using (var input = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken))
            {
                StorageSystemImportJsonValidator.Validate(input.RootElement);
                envelope = input.RootElement.Deserialize<StorageSystemExportEnvelope>(JsonOptions)
                    ?? throw new InvalidDataException("The WinPool import is empty.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (envelope.Product != "WinPool" || envelope.SchemaVersion != StorageSystemDocument.CurrentSchemaVersion
                || envelope.System?.SchemaVersion != StorageSystemDocument.CurrentSchemaVersion)
                throw new InvalidDataException("The WinPool import version is not supported.");
            StorageSystemImportValidator.Validate(envelope.System);
            await CheckCanonicalAsync(envelope.System, cancellationToken);
            var imported = envelope.System.AsImportedSimulation();
            await CheckCanonicalAsync(imported, cancellationToken);
            return imported;
        }
        finally { LargeParseGate.Release(); }
    }

    private static async Task CheckCanonicalAsync(StorageSystemDocument document, CancellationToken cancellationToken)
    {
        await using var counter = new LimitedCounter(MaximumDocumentBytes - Encoding.UTF8.GetByteCount(Prefix(document.UpdatedAt)) - 1);
        await JsonSerializer.SerializeAsync(counter, document, JsonOptions, cancellationToken);
    }

    private static string Prefix(DateTimeOffset updatedAt) =>
        "{\"Product\":\"WinPool\",\"SchemaVersion\":" + StorageSystemDocument.CurrentSchemaVersion
        + ",\"ExportedAt\":" + JsonSerializer.Serialize(updatedAt) + ",\"System\":";

    private sealed class LimitedCounter(long maximum) : Stream
    {
        private long count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => count;
        public override long Position { get => count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int length) => Count(length);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count(buffer.Length);
            return ValueTask.CompletedTask;
        }
        private void Count(int length)
        {
            count += length;
            if (count > maximum) throw new InvalidDataException("The canonical WinPool payload exceeds its byte limit.");
        }
        public override int Read(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public sealed record StorageSystemExportEnvelope(string Product, int SchemaVersion, DateTimeOffset ExportedAt,
    StorageSystemDocument System);
