namespace WinPool.Infrastructure.Sqlite;

public sealed class AgentWriteOwnershipException : InvalidOperationException
{
    public AgentWriteOwnershipException(string message)
        : base(message)
    {
    }
}

public sealed class AgentWriteOwnerLease : IDisposable, IAsyncDisposable
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, LeaseRegistration> ActiveLeases =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Guid leaseId = Guid.NewGuid();
    private bool disposed;

    private AgentWriteOwnerLease(string databasePath, string ownerId)
    {
        DatabasePath = Normalize(databasePath);
        OwnerId = ownerId;
    }

    public string DatabasePath { get; }

    public string OwnerId { get; }

    public bool IsActive
    {
        get
        {
            lock (SyncRoot)
            {
                return !disposed
                    && ActiveLeases.TryGetValue(DatabasePath, out var registration)
                    && registration.LeaseId == leaseId;
            }
        }
    }

    public static AgentWriteOwnerLease Acquire(
        WinPoolSqliteStore store,
        string ownerId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var lease = new AgentWriteOwnerLease(store.DatabasePath, ownerId.Trim());
        lock (SyncRoot)
        {
            if (ActiveLeases.TryGetValue(lease.DatabasePath, out var existing))
            {
                throw new AgentWriteOwnershipException(
                    $"数据库写入 owner 已由“{existing.OwnerId}”持有；" +
                    $"“{lease.OwnerId}”不能同时取得写入权。");
            }

            ActiveLeases.Add(
                lease.DatabasePath,
                new LeaseRegistration(lease.leaseId, lease.OwnerId));
        }

        return lease;
    }

    public void AssertOwnership(WinPoolSqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var requestedPath = Normalize(store.DatabasePath);
        lock (SyncRoot)
        {
            if (disposed
                || !string.Equals(DatabasePath, requestedPath, StringComparison.OrdinalIgnoreCase)
                || !ActiveLeases.TryGetValue(DatabasePath, out var registration)
                || registration.LeaseId != leaseId)
            {
                throw new AgentWriteOwnershipException(
                    "当前对象不持有该数据库的 Agent 写入 lease。");
            }
        }
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (disposed)
            {
                return;
            }

            if (ActiveLeases.TryGetValue(DatabasePath, out var registration)
                && registration.LeaseId == leaseId)
            {
                ActiveLeases.Remove(DatabasePath);
            }

            disposed = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Normalize(string databasePath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(databasePath));

    private sealed record LeaseRegistration(Guid LeaseId, string OwnerId);
}
