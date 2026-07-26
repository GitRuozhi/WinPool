using System.Text.Json;
using Windows.Storage.Pickers;
using WinPool.Core;

namespace WinPool.App.Services;

public sealed class DesktopExportService : IExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<string?> ExportAsync(
        StorageSnapshot snapshot,
        StorageUnitRef? selectedUnit,
        CancellationToken cancellationToken = default)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = $"WinPool-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinPool_App.App.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }

        var export = new
        {
            Product = "WinPool",
            ReadOnly = true,
            SelectedUnit = selectedUnit,
            Snapshot = snapshot
        };
        await File.WriteAllTextAsync(
            file.Path,
            JsonSerializer.Serialize(export, JsonOptions),
            cancellationToken);
        return file.Path;
    }
}
