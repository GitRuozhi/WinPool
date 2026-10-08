using System.Diagnostics;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>Best-effort diagnostics only; never used as execution or safety evidence.</summary>
public static class StorageOperationTiming
{
    public static void Record(string phase, long started, OperationId? operationId = null,
        string? stepId = null, string scope = "storage", int captureCount = 0)
    {
        var entry = JsonSerializer.Serialize(new
        {
            phase, operationId = operationId?.Value, stepId, scope, captureCount,
            elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            processId = Environment.ProcessId
        });
        Trace.WriteLine(entry);
        DiagnosticLog.AppendFailure(StorageDataLocations.CurrentRoot, "operation-timing.jsonl", entry, null);
    }

    public static async Task<T> MeasureAsync<T>(string phase, Func<Task<T>> action,
        OperationId? operationId = null, string? stepId = null, string scope = "storage", int captureCount = 0)
    {
        var started = Stopwatch.GetTimestamp();
        try { return await action().ConfigureAwait(false); }
        finally { Record(phase, started, operationId, stepId, scope, captureCount); }
    }
}
