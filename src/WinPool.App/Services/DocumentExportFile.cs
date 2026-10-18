using System.Text;

namespace WinPool.App.Services;

internal static class DocumentExportFile
{
    public static async Task WriteAsync(string path, string canonical, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = path + ".winpool-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(canonical.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
