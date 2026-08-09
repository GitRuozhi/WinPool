using System.Diagnostics;
using WinPool.Application;

namespace WinPool.TestWorker;

public interface IGracefulToolTermination
{
    ValueTask<bool> RequestAsync(
        ToolId toolId,
        Process process,
        CancellationToken cancellationToken);
}

/// <summary>
/// Requests WM_CLOSE when a configured tool exposes a main window. Console-only
/// tools normally return false, after which the runner still observes the grace
/// period before terminating the Job. More specific adapters can replace this
/// strategy without giving the runner a free-form command hook.
/// </summary>
public sealed class WindowCloseGracefulToolTermination : IGracefulToolTermination
{
    public ValueTask<bool> RequestAsync(
        ToolId toolId,
        Process process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return ValueTask.FromResult(process.CloseMainWindow());
        }
        catch (InvalidOperationException)
        {
            return ValueTask.FromResult(false);
        }
    }
}
