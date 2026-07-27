namespace WinPool.Infrastructure.Tests;

public sealed class InfrastructureTests
{
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
        Assert.DoesNotContain(" Volumes = @(", script);
        Assert.EndsWith(
            @"WindowsPowerShell\v1.0\powershell.exe",
            WinPool.Infrastructure.Windows.WindowsPowerShellRunner.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KsReferenceCatalogPreservesAllStableItemIds()
    {
        var report = WinPool.Infrastructure.Windows.KsReferenceReportFactory.Create();
        Assert.Equal(154, report.Items.Count);
        Assert.Equal(154, report.Items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(13, report.Items.Select(x => x.Category).Distinct(StringComparer.Ordinal).Count());
        Assert.All(report.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.StandardName)));

        var document = new WinPool.Core.StorageSystemDocument(
            WinPool.Core.StorageSystemDocument.CurrentSchemaVersion,
            "simulation:test",
            WinPool.Core.StorageSystemKind.Simulation,
            "Test",
            WinPool.Core.StorageSnapshot.Empty("TEST"),
            report,
            [],
            DateTimeOffset.Now);
        var redacted = WinPool.Core.StorageSystemDocumentSanitizer.RedactSensitiveData(document);
        foreach (var item in redacted.HardwareReport.Items.Where(x => x.Id is "0304" or "0510" or "0718" or "0803" or "1206"))
        {
            Assert.DoesNotContain(
                item.FinalValue!.Value.EnumerateArray(),
                value => value.GetString() is { Length: > 0 } text && !text.Contains('•'));
        }
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
        var provider = new WinPool.Infrastructure.Windows.WindowsHardwareInventoryProvider();
        var document = await provider.CollectLocalAsync(CancellationToken.None);
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
        Assert.Equal(2, snapshot.SchemaVersion);
        Assert.Equal(154, document.HardwareReport.Items.Count);
        Assert.Equal(
            13,
            document.HardwareReport.Items
                .Select(x => x.Category)
                .Distinct(StringComparer.Ordinal)
                .Count());

        var expectedUnavailable = new HashSet<string>(StringComparer.Ordinal)
        {
            "0915", "1004", "1005", "1006", "1103", "1111", "1112"
        };
        var unavailable = document.HardwareReport.Items
            .Where(x => x.Sources.Any(source =>
                source.Status == WinPool.Core.CollectorSourceStatus.Unavailable))
            .Select(x => x.Id)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Subset(expectedUnavailable, unavailable);

        foreach (var id in new[] { "0101", "0201", "0203", "0401", "0701", "0802" })
        {
            var item = Assert.Single(document.HardwareReport.Items, x => x.Id == id);
            Assert.Contains(
                item.Sources,
                source => source.Status == WinPool.Core.CollectorSourceStatus.Success);
        }

        var redacted = WinPool.Core.StorageSystemDocumentSanitizer.RedactSensitiveData(document);
        foreach (var item in redacted.HardwareReport.Items.Where(
                     x => x.Id is "0304" or "0510" or "0718" or "0803" or "1206"))
        {
            if (item.FinalValue is null)
            {
                continue;
            }
            Assert.DoesNotContain(
                item.FinalValue.Value.EnumerateArray(),
                value => value.GetString() is { Length: > 0 } text
                    && text != "—"
                    && !text.Contains('•'));
        }
        Assert.All(
            redacted.Snapshot.PhysicalDisks,
            disk => Assert.True(
                !disk.MaskedSerialNumber.Any(char.IsLetterOrDigit)
                || disk.MaskedSerialNumber.Contains('•')));
    }

}
