using System.Text.Json;

namespace WinPool.Application;

/// <summary>
/// Defines the fixed, non-authoritative subdirectories inside one WinPool data
/// root. Preferences and SQLite data retain their separate ownership; these
/// paths only keep rebuildable runtime files and bounded diagnostics out of
/// that root's top level.
/// </summary>
public static class DataRootLayout
{
    public static string RuntimeDirectory(string dataRoot) =>
        Path.Combine(NormalizeDataRoot(dataRoot), "Runtime");

    public static string DiagnosticsDirectory(string dataRoot) =>
        Path.Combine(NormalizeDataRoot(dataRoot), "Diagnostics");

    public static string StagingDirectory(string dataRoot) =>
        Path.Combine(NormalizeDataRoot(dataRoot), "Staging");

    public static string AgentEndpointPath(string dataRoot) =>
        Path.Combine(RuntimeDirectory(dataRoot), "agent-endpoint.json");

    private static string NormalizeDataRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
    }
}

/// <summary>
/// Writes small JSONL diagnostic streams with one retained rollover. Diagnostic
/// files are not state authorities and must never stop product execution.
/// </summary>
public static class DiagnosticLog
{
    private const long MaximumActiveFileBytes = 1_048_576;
    private static readonly object Sync = new();

    public static void AppendFailure(
        string dataRoot,
        string fileName,
        string source,
        Exception? exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        try
        {
            var directory = DataRootLayout.DiagnosticsDirectory(dataRoot);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            lock (Sync)
            {
                if (File.Exists(path)
                    && new FileInfo(path).Length >= MaximumActiveFileBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                var entry = JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    source,
                    exception = exception?.ToString()
                });
                File.AppendAllText(path, entry + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics are deliberately best-effort.
        }
    }
}
