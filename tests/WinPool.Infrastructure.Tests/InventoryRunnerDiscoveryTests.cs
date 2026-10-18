using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

[Collection("PowerShellHost")]
public sealed class InventoryRunnerDiscoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FixedRunnerPassesExplicitDiscoveryAndExactLocatorsAsData(bool runnerDiscovery, bool scopeDiscovery)
    {
        var templates = new PowerShellHostTemplates("param($p); $p | ConvertTo-Json -Depth 12 -Compress",
            "param($p); throw 'writes-forbidden'", "param($kind,$p); throw 'other-queries-forbidden'", false);
        await using var host = new WindowsPowerShellHost(templates);
        var runner = new WindowsPowerShellRunner(CollectionPurpose.Storage, runnerDiscovery, host);
        var scope = Scope() with { DiscoverProviderCache = scopeDiscovery };
        var result = await runner.RunInventoryAsync(scope, default);
        using var payload = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(runnerDiscovery || scopeDiscovery, payload.RootElement.GetProperty("DiscoverProviderCache").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("IncludeHardware").GetBoolean());
        Assert.Equal(scope.BeforeLocators[0].IdentityValue,
            Assert.Single(payload.RootElement.GetProperty("ScopeLocators").EnumerateArray()).GetProperty("IdentityValue").GetString());
    }

    [Fact]
    public void ScopedTemplateEnablesDiscoveryOnlyWhenExplicitAndRejectsNonPhysicalTargets()
    {
        var scope = Scope();
        Assert.Contains("$WinPoolDiscoverProviderCache = $false", EmbeddedStorageInventoryScript.ForScope(scope));
        var script = EmbeddedStorageInventoryScript.ForScope(scope with { DiscoverProviderCache = true });
        Assert.Contains("$WinPoolDiscoverProviderCache = $true", script);
        Assert.True(script.IndexOf("Update-StorageProviderCache -StorageSubSystem", StringComparison.Ordinal)
            < script.IndexOf("$physicalObjects = @(Get-SourceSet", StringComparison.Ordinal));
        var target = new StorageObjectId(scope.SystemId, StorageObjectKind.OsDisk, "osdisk");
        var invalid = scope with { DiscoverProviderCache = true, Targets = [target], BeforeObjectIds = [target.ProviderKey],
            BeforeLocators = [new(target, "MSFT_Disk", "UniqueId", "provider-disk")] };
        Assert.Throws<InvalidDataException>(invalid.Validate);
    }

    private static StorageInventoryScope Scope()
    {
        var system = SystemId.New();
        var target = new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "physical");
        return new(system, OperationId.New(), "poolability-discovery", [target], [target.ProviderKey],
            [new(target, "MSFT_PhysicalDisk", "UniqueId", "exact-provider-uid")]);
    }
}
