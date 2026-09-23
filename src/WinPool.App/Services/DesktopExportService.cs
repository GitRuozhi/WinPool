using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Windows.Storage.Pickers;
using WinPool.Application;

namespace WinPool.App.Services;

public sealed class DesktopExportService : IExportService, IStorageSystemImportExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<string?> ExportAsync(
        StorageSnapshot snapshot,
        StorageUnitRef? selectedUnit,
        CancellationToken cancellationToken = default)
    {
        var picker = new FileSavePicker(ParentWindowId)
        {
            SuggestedFileName = $"WinPool-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}",
            DefaultFileExtension = ".json"
        };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }

        var export = new
        {
            Product = "WinPool",
            ReadOnly = true,
            SelectedUnit = selectedUnit is null ? null : snapshot.FindUnit(selectedUnit.StableId) ?? selectedUnit,
            Snapshot = new
            {
                snapshot.SchemaVersion, snapshot.SnapshotVersion, snapshot.ScannedAt, snapshot.Computer,
                snapshot.StorageSubsystems, snapshot.PhysicalDisks, snapshot.StoragePools, snapshot.StorageTiers,
                snapshot.VirtualDisks, snapshot.OsDisks, Partitions = snapshot.PartitionUnions,
                snapshot.Warnings
            }
        };
        await File.WriteAllTextAsync(
            file.Path,
            JsonSerializer.Serialize(export, JsonOptions),
            cancellationToken);
        return file.Path;
    }

    public async Task<string?> ExportAsync(
        StorageSystemDocument document,
        CancellationToken cancellationToken = default)
    {
        var picker = new FileSavePicker(ParentWindowId)
        {
            SuggestedFileName =
                $"WinPool-{NormalizeFileName(document.DisplayName)}-{DateTime.Now:yyyyMMdd-HHmmss}",
            DefaultFileExtension = ".json"
        };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }

        await using var stream = File.Create(file.Path);
        await JsonSerializer.SerializeAsync(
            stream,
            new StorageSystemExportEnvelope(
                "WinPool",
                StorageSystemDocument.CurrentSchemaVersion,
                DateTimeOffset.Now,
                document),
            JsonOptions,
            cancellationToken);
        return file.Path;
    }

    public async Task<string?> ExportCsvAsync(
        string suggestedName,
        string csvContent,
        CancellationToken cancellationToken = default)
    {
        var picker = new FileSavePicker(ParentWindowId) { SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }

        await File.WriteAllTextAsync(file.Path, "\uFEFF" + csvContent, cancellationToken);
        return file.Path;
    }

    public async Task<StorageSystemDocument?> ImportAsync(
        CancellationToken cancellationToken = default)
    {
        var picker = new FileOpenPicker(ParentWindowId);
        picker.FileTypeFilter.Add(".winpool");
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return null;
        }
        if (new FileInfo(file.Path).Length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("The WinPool import exceeds the 64 MiB safety limit.");
        }

        await using var stream = File.OpenRead(file.Path);
        using var importJson = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        StorageSystemImportJsonValidator.Validate(importJson.RootElement);
        var envelope = JsonSerializer.Deserialize<StorageSystemExportEnvelope>(
            importJson.RootElement,
            JsonOptions)
            ?? throw new InvalidDataException("The WinPool import is empty.");
        if (envelope.System is null)
        {
            throw new InvalidDataException("The WinPool import is missing its system document.");
        }
        if (envelope.Product != "WinPool"
            || envelope.SchemaVersion != StorageSystemDocument.CurrentSchemaVersion
            || envelope.System.SchemaVersion != StorageSystemDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The WinPool import version is not supported.");
        }
        Validate(envelope.System);
        return envelope.System.AsImportedSimulation();
    }

    private static void Validate(StorageSystemDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.DisplayName)
            || string.IsNullOrWhiteSpace(document.Snapshot.Computer.StableId))
        {
            throw new InvalidDataException("The imported system is missing required identity data.");
        }
        var ids = document.Snapshot.PhysicalDisks.Select(x => x.StableId)
            .Concat(document.Snapshot.StoragePools.Select(x => x.StableId))
            .Concat(document.Snapshot.StorageTiers.Select(x => x.StableId))
            .Concat(document.Snapshot.VirtualDisks.Select(x => x.StableId))
            .Concat(document.Snapshot.OsDisks.Select(x => x.StableId))
            .Concat(document.Snapshot.Partitions.Select(x => x.StableId))
            .Concat(document.Snapshot.NetworkDisks.Select(x => x.StableId))
            .ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace)
            || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
        {
            throw new InvalidDataException("The imported system contains invalid or duplicate object IDs.");
        }
    }

    private static string NormalizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static Microsoft.UI.WindowId ParentWindowId => WinPool_App.App.Window.AppWindow.Id;

    private sealed record StorageSystemExportEnvelope(
        string Product,
        int SchemaVersion,
        DateTimeOffset ExportedAt,
        StorageSystemDocument System);
}
