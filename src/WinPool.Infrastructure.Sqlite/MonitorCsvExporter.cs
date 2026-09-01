using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WinPool.Domain;

namespace WinPool.Infrastructure.Sqlite;

public sealed record MonitorCsvExportResult(
    string DestinationPath,
    string Sha256,
    long RowCount);

public sealed class MonitorCsvExporter(WinPoolSqliteStore store)
{
    public async Task<MonitorCsvExportResult> ExportAsync(
        SessionId sessionId,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(
                Path.GetExtension(destination),
                ".csv",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "监控导出文件必须使用 .csv 扩展名。",
                nameof(destinationPath));
        }

        if (string.Equals(
                destination,
                store.DatabasePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("不能用监控导出覆盖 WinPool 数据库。");
        }

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("监控导出路径没有父目录。");
        Directory.CreateDirectory(parent);
        if (File.Exists(destination) && !overwrite)
        {
            throw new IOException("目标 CSV 已存在且没有覆盖确认。");
        }

        var temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        long rowCount = 0;
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(
                             output,
                             new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            await using (var connection = await store.OpenConnectionAsync(cancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT
                        s.timestamp_utc_ms, d.sanitized_name, s.activity_pct,
                        s.read_bytes_per_sec, s.write_bytes_per_sec,
                        s.queue_length
                    FROM monitor_samples AS s
                    JOIN monitor_devices AS d
                      ON d.session_id = s.session_id AND d.device_id = s.device_id
                    WHERE s.session_id = $session
                    ORDER BY s.timestamp_utc_ms, s.rowid;
                    """;
                command.Parameters.AddWithValue(
                    "$session",
                    sessionId.Value.ToString("N"));
                await writer.WriteLineAsync(
                    "TimestampUtc,Device,ActivityPercent,ReadBytesPerSecond," +
                    "WriteBytesPerSecond,QueueLength");
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = string.Join(
                        ',',
                        DateTimeOffset
                            .FromUnixTimeMilliseconds(reader.GetInt64(0))
                            .ToString("O", CultureInfo.InvariantCulture),
                        Csv(reader.GetString(1)),
                        reader.GetDouble(2).ToString("R", CultureInfo.InvariantCulture),
                        reader.GetDouble(3).ToString("R", CultureInfo.InvariantCulture),
                        reader.GetDouble(4).ToString("R", CultureInfo.InvariantCulture),
                        reader.GetDouble(5).ToString("R", CultureInfo.InvariantCulture));
                    await writer.WriteLineAsync(line);
                    rowCount++;
                }

                await writer.FlushAsync(cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            var sha256 = await HashAsync(temporary, cancellationToken);
            File.Move(temporary, destination, overwrite);
            return new MonitorCsvExportResult(destination, sha256, rowCount);
        }
        catch
        {
            TryRemoveTemporary(temporary);
            throw;
        }
    }

    private static string Csv(string value)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.IndexOfAny([',', '"']) < 0
            ? sanitized
            : $"\"{sanitized.Replace("\"", "\"\"")}\"";
    }

    private static async Task<string> HashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryRemoveTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
