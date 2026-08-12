using Microsoft.Data.Sqlite;
using System.Data;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Sqlite;

public sealed record LocalSystemIdentityResolution(
    SystemId SystemId,
    bool HasFragmentedHistory);

/// <summary>
/// Owns the durable Local-system identity. Collector snapshot bindings are not
/// used as the authority because different collectors intentionally derive
/// their bindings from different input sets.
/// </summary>
public sealed class LocalSystemIdentityResolver
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease writeOwner;
    private readonly SemaphoreSlim resolutionGate = new(1, 1);

    public LocalSystemIdentityResolver(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public static string CreateAuthorityBinding(string machineName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        return MachineBinding.Create([machineName]);
    }

    public async Task<LocalSystemIdentityResolution> ResolveAsync(
        string machineName,
        SystemId? preferredSystemId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        writeOwner.AssertOwnership(store);
        var authorityBinding = CreateAuthorityBinding(machineName);
        var normalizedName = machineName.Trim();

        await resolutionGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await store.OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(
                IsolationLevel.Serializable,
                deferred: false);
            var preferred = preferredSystemId is { } preferredSystem
                ? await ReadPreferredCandidateAsync(
                    connection,
                    transaction,
                    preferredSystem,
                    authorityBinding,
                    cancellationToken)
                : null;
            var candidates = await ReadCandidatesAsync(
                connection,
                transaction,
                authorityBinding,
                normalizedName,
                cancellationToken);
            var selected = preferred ?? Select(candidates);
            if (selected is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(
                    selected.SystemId,
                    candidates.Any(candidate => candidate.SystemId != selected.SystemId));
            }

            var created = SystemId.New();
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO systems(
                    system_id, kind, display_name, machine_binding_hash,
                    created_at_utc_ms)
                VALUES($system, $kind, $display, $binding, $created);
                """;
            insert.Parameters.AddWithValue("$system", created.Value.ToString("N"));
            insert.Parameters.AddWithValue("$kind", (int)PersistedSystemKind.Local);
            insert.Parameters.AddWithValue("$display", normalizedName);
            insert.Parameters.AddWithValue("$binding", authorityBinding);
            insert.Parameters.AddWithValue(
                "$created",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(created, false);
        }
        finally
        {
            resolutionGate.Release();
        }
    }

    private static async Task<List<Candidate>> ReadCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string authorityBinding,
        string machineName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT system_id, machine_binding_hash, display_name, created_at_utc_ms
            FROM systems
            WHERE kind = $kind
              AND (
                  machine_binding_hash = $binding
                  OR display_name = $display COLLATE NOCASE)
            ORDER BY created_at_utc_ms, system_id;
            """;
        command.Parameters.AddWithValue("$kind", (int)PersistedSystemKind.Local);
        command.Parameters.AddWithValue("$binding", authorityBinding);
        command.Parameters.AddWithValue("$display", machineName);
        var candidates = new List<Candidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new(
                new SystemId(Guid.ParseExact(reader.GetString(0), "N")),
                StringComparer.Ordinal.Equals(reader.GetString(1), authorityBinding),
                reader.GetInt64(3)));
        }

        return candidates;
    }

    private static async Task<Candidate?> ReadPreferredCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SystemId preferredSystemId,
        string authorityBinding,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT system_id, machine_binding_hash, created_at_utc_ms
            FROM systems
            WHERE system_id = $system AND kind = $kind;
            """;
        command.Parameters.AddWithValue("$system", preferredSystemId.Value.ToString("N"));
        command.Parameters.AddWithValue("$kind", (int)PersistedSystemKind.Local);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new(
            new SystemId(Guid.ParseExact(reader.GetString(0), "N")),
            !reader.IsDBNull(1)
                && StringComparer.Ordinal.Equals(reader.GetString(1), authorityBinding),
            reader.GetInt64(2));
    }

    private static Candidate? Select(
        IReadOnlyList<Candidate> candidates)
    {
        return candidates
            .OrderByDescending(candidate => candidate.HasAuthorityBinding)
            .ThenBy(candidate => candidate.CreatedAtUtcMs)
            .ThenBy(candidate => candidate.SystemId.Value)
            .FirstOrDefault();
    }

    private sealed record Candidate(
        SystemId SystemId,
        bool HasAuthorityBinding,
        long CreatedAtUtcMs);
}
