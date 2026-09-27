using Microsoft.Data.Sqlite;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class MonitorExportBoundaryTests
{
    [Theory]
    [InlineData("relative.csv")]
    [InlineData(@"C:\output\file:stream.csv")]
    [InlineData(@"C:\output\NUL.csv")]
    [InlineData(@"C:\output\COM1.csv")]
    [InlineData(@"C:\output.\file.csv")]
    [InlineData(@"\\?\C:\output\file.csv")]
    [InlineData(@"\\.\C:\output\file.csv")]
    public void RejectsAmbiguousAndDevicePaths(string path) =>
        Assert.Throws<ArgumentException>(() => MonitorCsvExporter.ValidateDestination(path));

    [Theory]
    [InlineData(@"C:\my exports\报告.csv")]
    [InlineData(@"\\server\share\custom\file.csv")]
    public void PreservesOrdinaryUserSelectedDestinations(string path) =>
        Assert.Equal(path, MonitorCsvExporter.ValidateDestination(path));

    [Fact]
    public async Task EmptyOrArchivedSessionDoesNotOverwriteExistingExport()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPool.Export.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new WinPoolSqliteStore(Path.Combine(root, "winpool.db"));
        await store.InitializeAsync();
        var destination = Path.Combine(root, "keep.csv");
        await File.WriteAllTextAsync(destination, "previous export");
        var result = await new MonitorCsvExporter(store).ExportAsync(SessionId.New(), destination, overwrite: true);
        Assert.Equal(0, result.RowCount);
        Assert.Equal("previous export", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        SqliteConnection.ClearAllPools();
        // Retain the isolated fixture for inspection; no user data is involved.
    }
}
