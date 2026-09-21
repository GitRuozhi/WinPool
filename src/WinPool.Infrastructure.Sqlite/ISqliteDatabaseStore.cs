using Microsoft.Data.Sqlite;

namespace WinPool.Infrastructure.Sqlite;

/// <summary>
/// The deliberately small surface shared by the independent core and
/// monitoring databases. It is not a public database compatibility contract;
/// it only lets the Agent write lease and monitoring repositories bind to one
/// concrete SQLite file at a time.
/// </summary>
public interface ISqliteDatabaseStore
{
    string DatabasePath { get; }

    Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default);
}
