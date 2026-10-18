using WinPool.App.Services;

namespace WinPool.App.Tests;

public sealed class DocumentExportFileTests
{
    [Fact]
    public async Task CancelledExportPreservesExistingDestinationAndLeavesNoTemporaryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPool-Export-Cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "existing.json");
            await File.WriteAllTextAsync(destination, "original");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DocumentExportFile.WriteAsync(destination, "replacement", cancellation.Token));
            Assert.Equal("original", await File.ReadAllTextAsync(destination));
            Assert.Single(Directory.EnumerateFiles(root));
            await DocumentExportFile.WriteAsync(destination, "replacement", CancellationToken.None);
            Assert.Equal("replacement", await File.ReadAllTextAsync(destination));
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
