namespace WinPool.Application;

/// <summary>
/// Defines process-exit acceptance at the shared Agent/TestWorker boundary.
/// This is intentionally separate from output parsing: an accepted exit still
/// requires the adapter and post-run verification to succeed.
/// </summary>
public static class ToolProcessExitPolicy
{
    public const string RoboCopyToolId = "windows.robocopy";

    public static bool IsAccepted(ToolId? toolId, int exitCode) =>
        toolId?.Value is RoboCopyToolId
            ? exitCode is >= 0 and <= 7
            : exitCode == 0;
}
