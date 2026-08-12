using System.Diagnostics;

namespace WinPool.Agent;

public sealed record ProcessIncarnation(
    int ProcessId,
    string ImagePath,
    DateTimeOffset StartedAtUtc);

public interface IProcessIncarnationVerifier
{
    ProcessIncarnation? TryRead(int processId);

    bool IsExpectedExecutable(int processId, string expectedExecutablePath);

    bool Matches(
        AgentManagedProcess registration,
        string expectedExecutablePath);
}

public sealed class WindowsProcessIncarnationVerifier : IProcessIncarnationVerifier
{
    public ProcessIncarnation? TryRead(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || process.MainModule?.FileName is not { } imagePath)
            {
                return null;
            }

            return new(
                processId,
                Path.GetFullPath(imagePath),
                process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            return null;
        }
    }

    public bool IsExpectedExecutable(int processId, string expectedExecutablePath) =>
        ProcessIncarnationMatcher.HasExpectedImage(
            TryRead(processId),
            processId,
            expectedExecutablePath);

    public bool Matches(
        AgentManagedProcess registration,
        string expectedExecutablePath) =>
        ProcessIncarnationMatcher.Matches(
            TryRead(registration.ProcessId),
            registration,
            expectedExecutablePath);
}

public static class ProcessIncarnationMatcher
{
    public static bool HasExpectedImage(
        ProcessIncarnation? witness,
        int expectedProcessId,
        string expectedExecutablePath) =>
        witness is not null
        && witness.ProcessId == expectedProcessId
        && HasExpectedImage(witness.ImagePath, expectedExecutablePath);

    public static bool Matches(
        ProcessIncarnation? witness,
        AgentManagedProcess registration,
        string expectedExecutablePath) =>
        witness is not null
        && witness.ProcessId == registration.ProcessId
        && witness.StartedAtUtc.ToUnixTimeMilliseconds()
            == registration.StartedAtUtc.ToUnixTimeMilliseconds()
        && HasExpectedImage(witness.ImagePath, expectedExecutablePath);

    private static bool HasExpectedImage(
        string actualImagePath,
        string expectedExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(actualImagePath)
            || string.IsNullOrWhiteSpace(expectedExecutablePath)
            || !Path.IsPathFullyQualified(expectedExecutablePath))
        {
            return false;
        }

        return StringComparer.OrdinalIgnoreCase.Equals(
            Path.GetFullPath(actualImagePath),
            Path.GetFullPath(expectedExecutablePath));
    }
}
