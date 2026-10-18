namespace WinPool.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [EnvironmentEnabledFact("WINPOOL_READ_ONLY_ADMISSION")]
    public async Task PhysicalDiskDeviceResolverUsesAQuickTargetedReadOnlyLookup()
    {
        // A physical member disappears from Win32_DiskDrive after joining a
        // concrete pool. Disk zero is therefore not a guaranteed basic disk.
        const string expectedRead = "$ErrorActionPreference='Stop'; ConvertTo-Json -Compress -InputObject @(Get-CimInstance -Namespace root/CIMV2 -Query 'SELECT Index,PNPDeviceID FROM Win32_DiskDrive' | Select-Object Index,PNPDeviceID)";
        var start = new System.Diagnostics.ProcessStartInfo(WinPool.Infrastructure.Windows.WindowsPowerShellRunner.ExecutablePath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(PowerShellFixtureScriptCodec.EncodeFixedCompressedScript(expectedRead));
        using var expectation = System.Diagnostics.Process.Start(start)!;
        var output = expectation.StandardOutput.ReadToEndAsync();
        var error = expectation.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await expectation.WaitForExitAsync(timeout.Token);
        Assert.True(expectation.ExitCode == 0, await error);
        using var results = System.Text.Json.JsonDocument.Parse(await output);
        var available = results.RootElement.EnumerateArray()
            .Where(disk => disk.GetProperty("Index").TryGetInt32(out _)
                && disk.GetProperty("PNPDeviceID").ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(disk.GetProperty("PNPDeviceID").GetString()))
            .Select(disk => (Number: disk.GetProperty("Index").GetInt32(), DeviceId: disk.GetProperty("PNPDeviceID").GetString()!.Trim())).ToArray();
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

    [EnvironmentEnabledFact("WINPOOL_READ_ONLY_ADMISSION")]
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
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "WinPool.Preference.Tests", Guid.NewGuid().ToString("N"));
        var service = new WinPool.Infrastructure.Windows.LocalUserPreferencesService(temporaryRoot);
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

    [EnvironmentEnabledFact("WINPOOL_READ_ONLY_ADMISSION")]
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
