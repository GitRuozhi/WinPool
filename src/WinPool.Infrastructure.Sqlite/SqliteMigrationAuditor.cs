using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WinPool.Infrastructure.Sqlite;

public sealed record SqliteTableMigrationAudit(
    string TableName,
    long RowCount,
    IReadOnlyList<string> PrimaryKeyColumns,
    string PrimaryKeySha256);

public sealed record SqliteMigrationAuditReport(
    int SchemaVersion,
    string IntegrityResult,
    IReadOnlyList<SqliteTableMigrationAudit> Tables)
{
    public bool IsHealthy =>
        string.Equals(IntegrityResult, "ok", StringComparison.OrdinalIgnoreCase);

    public bool HasSameLogicalIdentity(SqliteMigrationAuditReport other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!IsHealthy
            || !other.IsHealthy
            || SchemaVersion != other.SchemaVersion
            || Tables.Count != other.Tables.Count)
        {
            return false;
        }

        for (var index = 0; index < Tables.Count; index++)
        {
            var expected = Tables[index];
            var actual = other.Tables[index];
            if (!StringComparer.Ordinal.Equals(expected.TableName, actual.TableName)
                || expected.RowCount != actual.RowCount
                || !expected.PrimaryKeyColumns.SequenceEqual(
                    actual.PrimaryKeyColumns,
                    StringComparer.Ordinal)
                || !StringComparer.Ordinal.Equals(
                    expected.PrimaryKeySha256,
                    actual.PrimaryKeySha256))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Produces a bounded-memory, read-only migration audit. It intentionally
/// fingerprints row identity rather than arbitrary payload fields; the root
/// file manifest supplies the byte-for-byte SHA-256 evidence.
/// </summary>
public sealed class SqliteMigrationAuditor
{
    public async Task<SqliteMigrationAuditReport> CaptureAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "The SQLite database to audit does not exist.",
                fullPath);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var integrity = await ScalarTextAsync(
            connection,
            "PRAGMA quick_check;",
            cancellationToken);
        var schemaVersion = checked((int)await ScalarInt64Async(
            connection,
            "SELECT schema_version FROM schema_info WHERE singleton=1;",
            cancellationToken));
        var tableNames = await ReadTableNamesAsync(connection, cancellationToken);
        var tables = new List<SqliteTableMigrationAudit>(tableNames.Count);
        foreach (var tableName in tableNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tables.Add(await AuditTableAsync(
                connection,
                tableName,
                cancellationToken));
        }

        return new(schemaVersion, integrity, tables);
    }

    private static async Task<IReadOnlyList<string>> ReadTableNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type='table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name COLLATE BINARY;
            """;
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<SqliteTableMigrationAudit> AuditTableAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var quotedTable = QuoteIdentifier(tableName);
        var primaryKeys = new List<(int Ordinal, string Name)>();
        await using (var keyCommand = connection.CreateCommand())
        {
            keyCommand.CommandText = $"PRAGMA table_info({quotedTable});";
            await using var keyReader = await keyCommand.ExecuteReaderAsync(
                cancellationToken);
            while (await keyReader.ReadAsync(cancellationToken))
            {
                var primaryKeyOrdinal = keyReader.GetInt32(5);
                if (primaryKeyOrdinal > 0)
                {
                    primaryKeys.Add((primaryKeyOrdinal, keyReader.GetString(1)));
                }
            }
        }

        var keyColumns = primaryKeys
            .OrderBy(item => item.Ordinal)
            .Select(item => item.Name)
            .ToArray();
        var selectedKeys = keyColumns.Length == 0
            ? ["rowid"]
            : keyColumns;
        var keySql = string.Join(", ", selectedKeys.Select(QuoteIdentifier));
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {keySql} FROM {quotedTable} ORDER BY {keySql};";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long rowCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendInt64(hash, rowCount);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                AppendValue(hash, reader.GetValue(index));
            }

            rowCount = checked(rowCount + 1);
        }

        return new(
            tableName,
            rowCount,
            selectedKeys,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static void AppendValue(IncrementalHash hash, object value)
    {
        switch (value)
        {
            case DBNull:
                hash.AppendData([0]);
                return;
            case long integer:
                hash.AppendData([1]);
                AppendInt64(hash, integer);
                return;
            case int integer:
                hash.AppendData([1]);
                AppendInt64(hash, integer);
                return;
            case double number:
                hash.AppendData([2]);
                AppendInt64(hash, BitConverter.DoubleToInt64Bits(number));
                return;
            case string text:
                hash.AppendData([3]);
                AppendBytes(hash, Encoding.UTF8.GetBytes(text));
                return;
            case byte[] bytes:
                hash.AppendData([4]);
                AppendBytes(hash, bytes);
                return;
            default:
                hash.AppendData([5]);
                AppendBytes(
                    hash,
                    Encoding.UTF8.GetBytes(Convert.ToString(
                        value,
                        CultureInfo.InvariantCulture) ?? string.Empty));
                return;
        }
    }

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static async Task<string> ScalarTextAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
                   await command.ExecuteScalarAsync(cancellationToken),
                   CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }
}
