using System.Diagnostics;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>Best-effort diagnostics only; never used as execution or safety evidence.</summary>
public static class StorageOperationTiming
{
    public static Action<string> Sink { get; set; } = WriteDefault;

    public static void Record(string phase, long started, OperationId? operationId = null,
        string? stepId = null, string scope = "storage", int captureCount = 0,
        long? generation = null, Guid? hostIncarnation = null, Guid? correlationId = null, long? wireBytes = null)
    {
        var entry = JsonSerializer.Serialize(new
        {
            phase, operationId = operationId?.Value, stepId, scope, captureCount,
            generation, hostIncarnation, correlationId, wireBytes,
            buildId = typeof(StorageOperationTiming).Assembly.ManifestModule.ModuleVersionId,
            elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            processId = Environment.ProcessId
        });
        try { Sink(entry); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { /* Optional diagnostics cannot change a provider result or its durable write boundary. */ }
    }

    public static async Task<T> MeasureAsync<T>(string phase, Func<Task<T>> action,
        OperationId? operationId = null, string? stepId = null, string scope = "storage", int captureCount = 0)
    {
        var started = Stopwatch.GetTimestamp();
        try { return await action().ConfigureAwait(false); }
        finally { Record(phase, started, operationId, stepId, scope, captureCount); }
    }

    private static void WriteDefault(string entry)
    {
        Trace.WriteLine(entry);
        DiagnosticLog.AppendFailure(StorageDataLocations.CurrentRoot, "operation-timing.jsonl", entry, null);
    }
}
