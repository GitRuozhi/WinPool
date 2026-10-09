using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class BuildOperationGuardTests
{
    [Fact]
    public async Task ActiveRootFollowsExternalPointerInBothDirections()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new Fixture();
        var standard = await fixture.CreateDatabaseAsync(fixture.StandardRoot);
        var portable = await fixture.CreateDatabaseAsync(fixture.PortableRoot);
        Assert.NotEqual(0, await fixture.ProbeAsync()); // two databases need an authoritative pointer
        await File.WriteAllTextAsync(fixture.ExternalPointer, "{\"mode\":\"Standard\"}");
        await fixture.PrepareAsync(standard);
        Assert.NotEqual(0, await fixture.ProbeAsync());

        await fixture.CancelAsync(standard);
        Assert.Equal(0, await fixture.ProbeAsync());

        await fixture.PrepareAsync(portable);
        await File.WriteAllTextAsync(fixture.ExternalPointer, "{\"mode\":\"Portable\"}");
        Assert.NotEqual(0, await fixture.ProbeAsync());
        await File.WriteAllTextAsync(fixture.ExternalPointer, "{\"mode\":\"Standard\"}");
        Assert.Equal(0, await fixture.ProbeAsync());
        await File.WriteAllTextAsync(fixture.ExternalPointer, "{\"mode\":1}");
        Assert.NotEqual(0, await fixture.ProbeAsync());
    }

    [Fact]
    public async Task MissingCorruptAndAmbiguousRootsRefuseButPristineRootPasses()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new Fixture();
        Assert.Equal(0, await fixture.ProbeAsync());
        var store = await fixture.CreateDatabaseAsync(fixture.PortableRoot);
        Assert.NotEqual(0, await fixture.ProbeAsync());
        Directory.CreateDirectory(fixture.StandardRoot);
        await File.WriteAllTextAsync(Path.Combine(fixture.StandardRoot, "settings.json"), "{}");
        Assert.NotEqual(0, await fixture.ProbeAsync());
        await File.WriteAllTextAsync(Path.Combine(fixture.StandardRoot, "winpool.db"), "corrupt");
        Assert.NotEqual(0, await fixture.ProbeAsync());
        await File.WriteAllTextAsync(fixture.ExternalPointer, "{\"mode\":\"Portable\"}");
        Assert.Equal(0, await fixture.ProbeAsync());
        await File.WriteAllTextAsync(Path.Combine(fixture.StandardRoot,
            "storage-location.json"), "{\"mode\":\"Standard\"}");
        Assert.NotEqual(0, await fixture.ProbeAsync());
        _ = store;
    }

    [Theory]
    [InlineData(PersistedOperationState.Prepared)]
    [InlineData(PersistedOperationState.Accepted)]
    [InlineData(PersistedOperationState.Running)]
    [InlineData(PersistedOperationState.OutcomeUnknown)]
    public async Task RefusalIdentifiesOperationAndStateWithoutChangingDatabase(
        PersistedOperationState state)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new Fixture();
        var store = await fixture.CreateDatabaseAsync(fixture.StandardRoot);
        var operationId = await fixture.PrepareAsync(store);
        await fixture.ExecuteSqlAsync(store,
            "UPDATE operation_plans SET state = $state;", ("$state", (object)(int)state));
        var before = await File.ReadAllBytesAsync(store.DatabasePath);

        Assert.NotEqual(0, await fixture.ProbeAsync());

        Assert.Contains($"OperationId={operationId.Value:D}", fixture.LastProbeError);
        Assert.Contains($"state={state}", fixture.LastProbeError);
        Assert.Contains("existing runtime tree", fixture.LastProbeError);
        Assert.Contains("query by OperationId for read-only reconciliation", fixture.LastProbeError);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.DatabasePath));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public async Task SupportedSchemasWithoutRealOperationsPassWithoutUpgrade(int version)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new Fixture();
        var store = await fixture.CreateDatabaseAsync(fixture.StandardRoot);
        await fixture.ExecuteSqlAsync(store,
            "UPDATE schema_info SET schema_version = $version WHERE singleton = 1;", ("$version", (object)version));
        var before = await File.ReadAllBytesAsync(store.DatabasePath);

        Assert.Equal(0, await fixture.ProbeAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(store.DatabasePath));
    }

    [Fact]
    public async Task MalformedIdentifierStillBlocksWithBoundedDiagnostic()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new Fixture();
        var store = await fixture.CreateDatabaseAsync(fixture.StandardRoot);
        await fixture.PrepareAsync(store);
        var malformed = new string('x', 8192) + "\ninjected diagnostic";
        // This dedicated fixture connection deliberately creates a malformed
        // plan identifier despite the existing child-step reference.
        await fixture.ExecuteSqlAsync(store,
            "PRAGMA foreign_keys = OFF; UPDATE operation_plans SET operation_id = $id;",
            ("$id", (object)malformed));

        Assert.NotEqual(0, await fixture.ProbeAsync());
        Assert.Contains("OperationId=(invalid OperationId), state=Prepared", fixture.LastProbeError);
        Assert.DoesNotContain("injected diagnostic", fixture.LastProbeError);
        Assert.True(fixture.LastProbeError.Length < 2048);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(),
            "WinPool.Build.Guard.Tests", Guid.NewGuid().ToString("N"));
        public string StandardRoot => Path.Combine(root, "Local", "WinPool");
        public string PortableRoot => Path.Combine(root, "Release", "Data");
        public string ExternalPointer => StandardRoot + ".storage-location.json";
        private string RuntimeRoot => Path.Combine(root, "Release");
        public string LastProbeError { get; private set; } = string.Empty;

        public async Task<WinPoolSqliteStore> CreateDatabaseAsync(string dataRoot)
        {
            var store = new WinPoolSqliteStore(Path.Combine(dataRoot, "winpool.db"));
            await store.InitializeAsync();
            return store;
        }

        public async Task<OperationId> PrepareAsync(WinPoolSqliteStore store)
        {
            await using var lease = AgentWriteOwnerLease.Acquire(store, "guard-test");
            var system = SystemId.New();
            var request = new OperationRequest(OperationId.New(), EnvironmentId.New(), system,
                OperationIntent.InitializeDisk,
                [new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "disk-id")],
                new Dictionary<string, string>(), DateTimeOffset.UtcNow);
            var plan = OperationPlan.Create(request, ExecutionCapability.MutateStorageStructure,
                RiskLevel.R4StorageStructureMutation, "inventory", [],
                [new PlanStep("step", "initialize", [])], null, "disk", "none", "metadata",
                new AlgorithmIdentity("ALG-TEST", "1", AlgorithmConfidence.Proven, "unit-test"),
                request.RequestedAt);
            await new OperationPlanRepository(store, lease).PrepareAsync(
                plan, Guid.NewGuid(), "guard-test-intent");
            return plan.OperationId;
        }

        public async Task ExecuteSqlAsync(WinPoolSqliteStore store, string sql,
            params (string Name, object Value)[] parameters)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = store.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task CancelAsync(WinPoolSqliteStore store)
        {
            await using var lease = AgentWriteOwnerLease.Acquire(store, "guard-test");
            var writer = new OperationPlanRepository(store, lease);
            var plan = Assert.Single(await writer.ListUnfinishedAsync()).Plan;
            Assert.True(await writer.TransitionAsync(plan.OperationId,
                PersistedOperationState.Prepared, PersistedOperationState.Cancelled,
                new ExecutionEvent(plan.OperationId, ExecutionEventKind.Cancelled,
                    DateTimeOffset.UtcNow, "cancelled", "cancelled")));
        }

        public async Task<int> ProbeAsync()
        {
            var script = FindGuardScript();
            var statement = "try { . '" + Escape(script) + "'; " +
                "Assert-WinPoolRealOperationIdle -RuntimeRoot '" + Escape(RuntimeRoot) +
                "' -StandardRoot '" + Escape(StandardRoot) +
                "' } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(statement);
            using var process = Process.Start(start)!;
            var error = await process.StandardError.ReadToEndAsync();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            LastProbeError = error;
            if (process.ExitCode != 0) Console.WriteLine(error + output);
            return process.ExitCode;
        }

        private static string FindGuardScript()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var script = Path.Combine(directory.FullName, "build", "Assert-RealOperationIdle.ps1");
                if (File.Exists(script)) return script;
                directory = directory.Parent;
            }
            throw new FileNotFoundException("Could not locate build guard script.");
        }

        private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
