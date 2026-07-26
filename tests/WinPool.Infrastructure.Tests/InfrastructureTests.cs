namespace WinPool.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void InventoryScriptContainsNoMutatingStorageCommands()
    {
        var script = File.ReadAllText(ScriptPath);
        var forbidden = new System.Text.RegularExpressions.Regex(
            @"(?im)^\s*(New|Set|Remove|Clear|Initialize|Format)-[A-Za-z]+",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.DoesNotMatch(forbidden, script);
        Assert.Contains("Get-PhysicalDisk", script);
        Assert.Contains("Get-StoragePool", script);
        Assert.Contains("Get-StorageTier", script);
        Assert.Contains("Get-VirtualDisk", script);
        Assert.Contains("Win32_LogicalDisk", script);
        Assert.Contains("NetworkDisks", script);
        Assert.DoesNotContain("Volumes = @(", script);
    }

    [Fact]
    public async Task PreferencesPersistThemeAndLanguageButNotExecutionMode()
    {
        var service = new WinPool.Infrastructure.Windows.LocalUserPreferencesService();
        var original = await service.LoadAsync();
        try
        {
            await service.SaveAsync(new WinPool.Core.UserPreferences(
                WinPool.Core.ThemePreference.Dark,
                WinPool.Core.AccentColorPreference.Purple,
                WinPool.Core.LanguagePreference.EnUs));
            var loaded = await service.LoadAsync();
            Assert.Equal(WinPool.Core.ThemePreference.Dark, loaded.Theme);
            Assert.Equal(WinPool.Core.AccentColorPreference.Purple, loaded.AccentColor);
            Assert.Equal(WinPool.Core.LanguagePreference.EnUs, loaded.Language);
            Assert.DoesNotContain("ExecutionMode", await File.ReadAllTextAsync(service.SettingsPath));
        }
        finally
        {
            await service.SaveAsync(original);
        }
    }

    [Fact]
    public async Task RealReadOnlyScanReturnsUsableInventory()
    {
        var provider = new WinPool.Infrastructure.Windows.WindowsStorageInventoryProvider(ScriptPath);
        var snapshot = await provider.ScanAsync(CancellationToken.None);
        Assert.Equal(14, snapshot.PhysicalDisks.Count);
        Assert.True(snapshot.StoragePools.Count >= 3);
        Assert.Contains(snapshot.StoragePools, pool => pool.IsPrimordial);
        Assert.Equal(4, snapshot.StorageTiers.Count);
        Assert.Equal(2, snapshot.VirtualDisks.Count);
        Assert.Equal(13, snapshot.Partitions.Count);
        Assert.All(snapshot.Partitions, partition => Assert.DoesNotContain('\0', partition.FileSystemLabel));
        Assert.All(
            snapshot.Partitions,
            partition => Assert.True(
                string.IsNullOrEmpty(partition.DriveLetter)
                || (partition.DriveLetter.Length == 1
                    && partition.DriveLetter[0] is >= 'A' and <= 'Z')));
        Assert.Equal(2, snapshot.SchemaVersion);
    }

    private static string ScriptPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "WinPool.Infrastructure.Windows", "Scripts", "Get-StorageInventory.ps1"));
}
