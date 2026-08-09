using System.Diagnostics;
using WinPool.ToolManagement;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Uses the fixed, catalog-owned command line required by tools whose Windows
/// file metadata does not contain a usable version. No caller supplied command
/// or argument list is accepted.
/// </summary>
public sealed class WindowsToolVersionProbe : IToolVersionProbe
{
    private readonly FileMetadataToolVersionProbe metadataProbe = new();

    public Task<ToolVersionProbeResult> ProbeAsync(
        ToolVersionProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Descriptor.Id == KnownToolIds.Fio
            ? ProbeFioAsync(request.ExecutablePath, cancellationToken)
            : metadataProbe.ProbeAsync(request, cancellationToken);
    }

    private static async Task<ToolVersionProbeResult> ProbeFioAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("--version");
            if (!process.Start())
            {
                return ToolVersionProbeResult.Failure("tool.version.process-start-failed");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                return ToolVersionProbeResult.Failure("tool.version.process-failed");
            }

            var text = string.Concat(output, "\n", error);
            return ToolVersionParser.TryParse(text, out var version)
                ? ToolVersionProbeResult.Success(version.ToString())
                : ToolVersionProbeResult.Failure("tool.version.output-unrecognized");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ToolVersionProbeResult.Failure("tool.version.timeout");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return ToolVersionProbeResult.Failure("tool.version.process-unreadable");
        }
    }
}
