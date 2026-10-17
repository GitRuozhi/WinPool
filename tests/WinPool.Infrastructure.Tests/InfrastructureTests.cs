namespace WinPool.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void PhysicalDiskDeviceResolverUsesAQuickTargetedReadOnlyLookup()
    {
        // A physical member disappears from Win32_DiskDrive after joining a
        // concrete pool. Disk zero is therefore not a guaranteed basic disk.
        using var searcher = new System.Management.ManagementObjectSearcher(
            "root\\CIMV2", "SELECT Index,PNPDeviceID FROM Win32_DiskDrive");
        searcher.Options.Timeout = TimeSpan.FromSeconds(5);
        using var results = searcher.Get();
        var available = new List<(int Number, string DeviceId)>();
        foreach (System.Management.ManagementBaseObject disk in results)
        using (disk)
        {
            if (disk["Index"] is uint index && index <= int.MaxValue
                && disk["PNPDeviceID"] is string pnp && !string.IsNullOrWhiteSpace(pnp))
                available.Add(((int)index, pnp.Trim()));
        }
        Assert.NotEmpty(available);
        var selected = available.OrderBy(item => item.Number).First();
        var resolver = new WinPool.Infrastructure.Windows.WindowsPhysicalDiskDeviceResolver();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var deviceId = resolver.ResolvePnpDeviceId(selected.Number);
        timer.Stop();

        Assert.Equal(selected.DeviceId, deviceId);
        Assert.True(
            timer.Elapsed < TimeSpan.FromSeconds(5),
            $"Targeted disk lookup took {timer.Elapsed.TotalMilliseconds:N0} ms.");
        Assert.Null(resolver.ResolvePnpDeviceId(-1));
    }

    [Fact]
    public void StorageSpacesVirtualDiskCountersAreReadOnlyAndFiniteWhenPresent()
    {
        using var sampler =
            new WinPool.Infrastructure.Windows.StorageSpacesVirtualDiskSampler();
        var samples = sampler.Sample();
        Assert.All(
            samples,
            sample =>
            {
                Assert.False(string.IsNullOrWhiteSpace(sample.InstanceName));
                Assert.All(
                    new[]
                    {
                        sample.ActiveBytes,
                        sample.MissingBytes,
                        sample.StaleBytes,
                        sample.NeedRegenerationBytes,
                        sample.RegeneratingBytes,
                        sample.PendingDeletionBytes
                    },
                    value => Assert.True(double.IsFinite(value) && value >= 0));
            });
    }

    [Fact]
    public void EmbeddedInventoryContainsNoMutatingStorageCommands()
    {
        var script = WinPool.Infrastructure.Windows.WindowsHardwareInventoryProvider.FixedStorageCommand;
        var forbidden = new System.Text.RegularExpressions.Regex(
            @"(?i)\b(?:New|Set|Remove|Clear|Initialize|Format|Resize|Repair|Optimize|Reset)-(?:Disk|Partition|Volume|Storage[A-Za-z]*|PhysicalDisk|VirtualDisk)\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.DoesNotMatch(forbidden, script);
        foreach (var command in new[] { "New-StoragePool", "Set-PhysicalDisk", "Remove-StorageTier",
            "Clear-Disk", "Initialize-Disk", "Format-Volume", "Resize-VirtualDisk" })
            Assert.Matches(forbidden, command);
        Assert.Contains("Get-PhysicalDisk", script);
        Assert.Contains("Get-StoragePool", script);
        Assert.Contains("Get-StorageTier", script);
        Assert.Contains("Get-VirtualDisk", script);
        Assert.Contains("Win32_LogicalDisk", script);
        Assert.Contains("NetworkDisks", script);
        Assert.Contains("$physicalByDeviceNumber", script);
        Assert.Contains("Removable USB media", script);
        Assert.DoesNotContain(" Volumes = @(", script);
        Assert.EndsWith(
            @"WindowsPowerShell\v1.0\powershell.exe",
            WinPool.Infrastructure.Windows.WindowsPowerShellRunner.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShellInventoryRunnerExposesNoCallerSuppliedScriptOrCommandText()
    {
        var publicMethods = typeof(WinPool.Infrastructure.Windows.WindowsPowerShellRunner)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(method => method.DeclaringType == typeof(
                WinPool.Infrastructure.Windows.WindowsPowerShellRunner))
            .ToArray();

        Assert.Equal(2, publicMethods.Length);
        Assert.All(publicMethods, method =>
        {
            Assert.Equal("RunInventoryAsync", method.Name);
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(string));
            Assert.All(method.GetParameters(), parameter => Assert.Contains(parameter.ParameterType,
                new[] { typeof(CancellationToken), typeof(WinPool.Application.StorageInventoryScope) }));
        });
        Assert.Contains(publicMethods, method => method.GetParameters().Select(parameter => parameter.ParameterType)
            .SequenceEqual([typeof(CancellationToken)]));
        Assert.Contains(publicMethods, method => method.GetParameters().Select(parameter => parameter.ParameterType)
            .SequenceEqual([typeof(WinPool.Application.StorageInventoryScope), typeof(CancellationToken)]));
    }

    [Fact]
    public async Task PreferencesPersistThemeAndLanguageButNotExecutionMode()
    {
        var service = new WinPool.Infrastructure.Windows.LocalUserPreferencesService();
        var original = await service.LoadAsync();
        try
        {
            await service.SaveAsync(new WinPool.Domain.UserPreferences(
                WinPool.Domain.ThemePreference.Dark,
                WinPool.Domain.AccentColorPreference.Purple,
                WinPool.Domain.LanguagePreference.EnUs));
            var loaded = await service.LoadAsync();
            Assert.Equal(WinPool.Domain.ThemePreference.Dark, loaded.Theme);
            Assert.Equal(WinPool.Domain.AccentColorPreference.Purple, loaded.AccentColor);
            Assert.Equal(WinPool.Domain.LanguagePreference.EnUs, loaded.Language);
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
        var provider = new WinPool.Infrastructure.Windows.WindowsHardwareInventoryProvider();
        var document = await provider.CollectHardwareAsync(CancellationToken.None);
        var snapshot = document.Snapshot;
        Assert.NotEmpty(snapshot.PhysicalDisks);
        Assert.NotEmpty(snapshot.StoragePools);
        Assert.Contains(snapshot.StoragePools, pool => pool.IsPrimordial);
        Assert.NotEmpty(snapshot.Partitions);
        Assert.All(snapshot.Partitions, partition => Assert.DoesNotContain('\0', partition.FileSystemLabel));
        Assert.All(
            snapshot.Partitions,
            partition => Assert.True(
                string.IsNullOrEmpty(partition.DriveLetter)
                || (partition.DriveLetter.Length == 1
                    && partition.DriveLetter[0] is >= 'A' and <= 'Z')));
        Assert.Equal(3, snapshot.SchemaVersion);
        Assert.Contains(document.SourceFacts!.Objects, x => x.ObjectType == WinPool.Application.FactObjectType.Processor);
        Assert.Contains(document.SourceFacts.Objects, x => x.ObjectType == WinPool.Application.FactObjectType.Volume);
        var systemId = WinPool.Domain.SystemId.New();
        var projected = WinPool.Infrastructure.Windows.InventorySnapshotProjection.Project(systemId, snapshot);
        Assert.Equal(WinPool.Application.InventoryProviderKind.EmbeddedReadOnlyPowerShell, projected.ProviderKind);
        Assert.Equal(systemId, projected.SystemId);
        Assert.Equal(64, projected.MachineBinding.Length);
        Assert.Contains(projected.Objects, item => item.Id.Kind == WinPool.Domain.StorageObjectKind.PhysicalDisk);
        Assert.Contains(projected.Objects, item => item.Id.Kind == WinPool.Domain.StorageObjectKind.Partition);
        Assert.All(projected.Objects.Where(item => item.Id.Kind == WinPool.Domain.StorageObjectKind.PhysicalDisk),
            item => Assert.True(item.Properties.ContainsKey("pnpDeviceId")));
    }

}
