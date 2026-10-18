using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Windows.Storage.Pickers;
using WinPool.Application;

namespace WinPool.App.Services;

public sealed class DesktopExportService : IExportService, IStorageSystemImportExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
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

        var canonical = await Task.Run(async () =>
        {
            await DocumentPayloadLimits.LargeParseGate.WaitAsync(cancellationToken);
            try { return DocumentPayloadLimits.EncodeExport(document); }
            finally { DocumentPayloadLimits.LargeParseGate.Release(); }
        }, cancellationToken).WaitAsync(cancellationToken);
        await File.WriteAllTextAsync(file.Path, canonical, cancellationToken);
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
        if (new FileInfo(file.Path).Length > DocumentPayloadLimits.MaximumDocumentBytes)
        {
            throw new InvalidDataException("The WinPool import exceeds the 64 MiB safety limit.");
        }

        var loading = Task.Run(async () =>
        {
            await using var stream = File.OpenRead(file.Path);
            return await DocumentPayloadLimits.ReadExportAsync(stream, cancellationToken);
        }, cancellationToken);
        return await loading.WaitAsync(cancellationToken);
    }

    private static string NormalizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static Microsoft.UI.WindowId ParentWindowId => WinPool_App.App.Window.AppWindow.Id;

}
