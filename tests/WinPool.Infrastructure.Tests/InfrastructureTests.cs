namespace WinPool.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void PhysicalDiskDeviceResolverUsesAQuickTargetedReadOnlyLookup()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var deviceId = new WinPool.Infrastructure.Windows.WindowsPhysicalDiskDeviceResolver()
            .ResolvePnpDeviceId(0);
        timer.Stop();

        Assert.False(string.IsNullOrWhiteSpace(deviceId));
        Assert.True(
            timer.Elapsed < TimeSpan.FromSeconds(5),
            $"Targeted disk lookup took {timer.Elapsed.TotalMilliseconds:N0} ms.");
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
            @"(?im)^\s*(New|Set|Remove|Clear|Initialize|Format)-[A-Za-z]+",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.DoesNotMatch(forbidden, script);
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

        var method = Assert.Single(publicMethods);
        Assert.Equal("RunInventoryAsync", method.Name);
        Assert.DoesNotContain(
            method.GetParameters(),
            parameter => parameter.ParameterType == typeof(string));
        Assert.Equal(
            [typeof(CancellationToken)],
            method.GetParameters().Select(parameter => parameter.ParameterType));
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
        var compatibilityProvider =
            new WinPool.Infrastructure.Windows.EmbeddedPowerShellInventoryProvider(
                new FixedHardwareProvider(document));
        var projected = await compatibilityProvider.CaptureAsync(
            new WinPool.Application.InventoryRequest(
                systemId,
                WinPool.Application.InventoryCaptureReason.Comparison),
            CancellationToken.None);
        Assert.True(projected.IsSuccess);
        Assert.Equal(
            WinPool.Application.InventoryProviderKind.EmbeddedReadOnlyPowerShell,
            projected.Value!.ProviderKind);
        Assert.Equal(64, projected.Value.MachineBinding.Length);
        Assert.Contains(
            projected.Value.Objects,
            item => item.Id.Kind == WinPool.Domain.StorageObjectKind.PhysicalDisk);
        Assert.Contains(
            projected.Value.Objects,
            item => item.Id.Kind == WinPool.Domain.StorageObjectKind.Partition);
        Assert.All(
            projected.Value.Objects.Where(
                item => item.Id.Kind == WinPool.Domain.StorageObjectKind.PhysicalDisk),
            item => Assert.True(item.Properties.ContainsKey("pnpDeviceId")));

        var stale = await compatibilityProvider.CaptureAsync(
            new WinPool.Application.InventoryRequest(
                systemId,
                WinPool.Application.InventoryCaptureReason.PreExecutionValidation,
                ExpectedInventoryVersion: "stale-version"),
            CancellationToken.None);
        Assert.Equal(WinPool.Application.ApplicationStatus.Rejected, stale.Status);
        Assert.NotNull(stale.Value);
    }

    [Fact]
    public async Task NativeReadOnlyScanReturnsSystemMountedVolumesAndIdentifiers()
    {
        var provider =
            new WinPool.Infrastructure.Windows.NativeWindowsInventoryProvider();
        var result = await provider.CaptureAsync(
            new WinPool.Application.InventoryRequest(
                WinPool.Domain.SystemId.New(),
                WinPool.Application.InventoryCaptureReason.Comparison),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(
            WinPool.Application.InventoryProviderKind.NativeWindows,
            result.Value.ProviderKind);
        Assert.Equal(64, result.Value.InventoryVersion.Length);
        Assert.Equal(64, result.Value.MachineBinding.Length);
        Assert.Contains(
            result.Value.Objects,
            item => item.Id.Kind == WinPool.Domain.StorageObjectKind.System);
        Assert.Contains(
            result.Value.Objects,
            item => item.Id.Kind == WinPool.Domain.StorageObjectKind.Volume);
        Assert.Contains(
            result.Value.Objects,
            item => item.Id.Kind == WinPool.Domain.StorageObjectKind.PhysicalDisk
                    && item.Properties.ContainsKey("physicalDriveNumber")
                    && item.Properties.ContainsKey("busType"));
        Assert.All(
            result.Value.Objects,
            item => Assert.Equal(64, item.Id.ProviderKey.Length));
        Assert.All(
            result.Value.Objects.Where(item => item.Id.Kind == WinPool.Domain.StorageObjectKind.Volume),
            item => Assert.True(item.Properties.ContainsKey("volumeGuid")));
        Assert.NotEmpty(result.Value.Relationships ?? []);
        var objectIds = result.Value.Objects.Select(item => item.Id).ToHashSet();
        Assert.All(
            result.Value.Relationships ?? [],
            relationship =>
            {
                Assert.Contains(relationship.FromObjectId, objectIds);
                Assert.Contains(relationship.ToObjectId, objectIds);
            });
    }

    private sealed class FixedHardwareProvider(
        WinPool.Application.StorageSystemDocument document)
        : WinPool.Application.IHardwareInventoryProvider
    {
        public Task<WinPool.Application.StorageSystemDocument> CollectLocalAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(document);
        }
    }

}
