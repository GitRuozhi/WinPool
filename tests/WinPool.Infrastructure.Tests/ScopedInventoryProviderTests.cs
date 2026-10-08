using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class ScopedInventoryProviderTests
{
    [Fact]
    public void ScopedScriptQueriesProviderIdentitiesBeforeCollectingAndNeverPerformsFullStorageQueries()
    {
        var scope = Scope("uid';throw 'not-executable");
        var script = ScopedStorageInventoryScript.Create(scope);
        Assert.Contains("Get-CimInstance -Namespace $WinPoolScopeNamespace -ClassName $class -Filter", script);
        Assert.Contains("Get-CimAssociatedInstance -InputObject $item -ResultClassName $resultClass", script);
        Assert.DoesNotContain(scope.BeforeLocators[0].IdentityValue, script);
        Assert.DoesNotContain("Update-StorageProviderCache", script);
        foreach (var query in new[] { "Get-Disk", "Get-Partition", "Get-Volume", "Get-PhysicalDisk", "Get-StoragePool", "Get-VirtualDisk", "Get-StorageTier", "Get-StorageSubSystem" })
            Assert.DoesNotContain("{ " + query + " -ErrorAction Stop }", script);
        Assert.Contains("Get-CimInstance -ClassName Win32_LogicalDisk", script); // Independent global mount occupancy evidence.
        ReadOnlyStorageCommandPolicy.EnsureSafe(script);
    }

    [Fact]
    public async Task ScopedProviderUsesOnlyTheScopedRunnerAndPersistsPreciseCoverageWithoutClaimingFullRefresh()
    {
        var scope = Scope("disk-provider-uid") with { Generation = 7 };
        var runner = new ScopedRunner();
        var document = await new WindowsHardwareInventoryProvider(runner).CollectScopedAsync(scope, CancellationToken.None);
        Assert.Equal(1, runner.ScopedCalls);
        Assert.Equal(scope.SystemId, document.SystemId);
        Assert.False(document.SourceFacts!.IsMerged);
        Assert.Empty(document.SourceFacts.Collections);
        var state = document.SourceFacts.ScopedCollection!;
        Assert.True(state.Complete);
        Assert.Equal(scope, state.Scope);
        Assert.All(document.SourceFacts.Sources, source =>
        {
            Assert.True(source.Coverage!.Complete);
            Assert.Equal(7, source.Coverage.Generation);
            Assert.Contains("before-partition", source.Coverage.ObjectIds);
        });
        var restored = WinPoolFactsCodec.Decode(WinPoolFactsCodec.Encode(document.SourceFacts));
        Assert.True(restored.ScopedCollection!.Complete);
        Assert.Equal(scope.OperationId, restored.ScopedCollection.Scope.OperationId);
    }

    [Fact]
    public async Task FailedOrMissingClosureEvidenceIsExplicitlyIncomplete()
    {
        foreach (var runner in new[] { new ScopedRunner(failed: true), new ScopedRunner(missing: true) })
        {
            var document = await new WindowsHardwareInventoryProvider(runner).CollectScopedAsync(Scope("uid"), CancellationToken.None);
            Assert.False(document.SourceFacts!.ScopedCollection!.Complete);
            Assert.Equal("inventory.scope.incomplete", document.SourceFacts.ScopedCollection.ReasonCode);
            Assert.All(document.SourceFacts.Sources, source => Assert.False(source.Coverage!.Complete));
        }
    }

    [Fact]
    public async Task ProviderWithoutScopedRunnerRejectsInsteadOfSilentlyRunningFullStorage()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WindowsHardwareInventoryProvider(new FullOnlyRunner()).CollectScopedAsync(Scope("uid"), CancellationToken.None));
    }

    private static StorageInventoryScope Scope(string uid)
    {
        var system = SystemId.New();
        var target = new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "physical-id");
        return new(system, OperationId.New(), "step-1", [target], ["physical-id", "before-partition"],
            [new(target, "MSFT_PhysicalDisk", "UniqueId", uid)]);
    }

    private sealed class FullOnlyRunner : IReadOnlyInventoryCommandRunner
    {
        public Task<ReadOnlyCommandResult> RunInventoryAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A scoped capture must not call this full collector.");
    }

    private sealed class ScopedRunner(bool failed = false, bool missing = false) : IReadOnlyInventoryCommandRunner, IScopedInventoryCommandRunner
    {
        public int ScopedCalls { get; private set; }
        public Task<ReadOnlyCommandResult> RunInventoryAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A scoped capture must not call this full collector.");
        public Task<ReadOnlyCommandResult> RunInventoryAsync(StorageInventoryScope scope, CancellationToken cancellationToken)
        {
            ScopedCalls++;
            string[] classes = ["MSFT_StorageSubSystem", "MSFT_StoragePool", "MSFT_PhysicalDisk", "MSFT_VirtualDisk",
                "MSFT_StorageTier", "MSFT_Disk", "MSFT_Partition", "MSFT_Volume"];
            var json = JsonSerializer.Serialize(new
            {
                ScannedAt = DateTimeOffset.UtcNow,
                Computer = new { Name = Environment.MachineName },
                SourceQuerySuccesses = classes.Where(name => !missing || name != "MSFT_Partition")
                    .Select(name => new { ClassName = name, Namespace = "root/Microsoft/Windows/Storage" }).ToArray(),
                SourceQueryFailures = failed ? new[] { new { ClassName = "MSFT_PhysicalDisk", Namespace = "root/Microsoft/Windows/Storage", ReasonCode = "ScopeClosureIncomplete" } } : [],
                SourceObservations = Array.Empty<object>()
            });
            return Task.FromResult(new ReadOnlyCommandResult(0, json, "", TimeSpan.FromMilliseconds(10)));
        }
    }
}
