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

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(),
            "WinPool.Build.Guard.Tests", Guid.NewGuid().ToString("N"));
        public string StandardRoot => Path.Combine(root, "Local", "WinPool");
        public string PortableRoot => Path.Combine(root, "Release", "Data");
        public string ExternalPointer => StandardRoot + ".storage-location.json";
        private string RuntimeRoot => Path.Combine(root, "Release");

        public async Task<WinPoolSqliteStore> CreateDatabaseAsync(string dataRoot)
        {
            var store = new WinPoolSqliteStore(Path.Combine(dataRoot, "winpool.db"));
            await store.InitializeAsync();
            return store;
        }

        public async Task PrepareAsync(WinPoolSqliteStore store)
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
            var statement = ". '" + Escape(script) + "'; " +
                "Assert-WinPoolRealOperationIdle -RuntimeRoot '" + Escape(RuntimeRoot) +
                "' -StandardRoot '" + Escape(StandardRoot) + "'";
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
