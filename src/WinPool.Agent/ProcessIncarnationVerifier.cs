using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 258;

    public ProcessIncarnation? TryRead(int processId) => TryRead(processId, false, out _);

    public ProcessIncarnation? TryReadWithIntegrity(int processId, out int? integrityLevel) =>
        TryRead(processId, true, out integrityLevel);

    private static ProcessIncarnation? TryRead(int processId, bool readIntegrity, out int? integrityLevel)
    {
        integrityLevel = null;
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, processId);
            if (handle.IsInvalid)
            {
                return null;
            }

            // The same live handle supplies image, creation and optional token
            // evidence. No PID-only observation is cached for a later request.
            if (WaitForSingleObject(handle, 0) != WaitTimeout) return null;
            var imagePath = ReadImagePath(handle);
            var startedAtUtc = ReadStartedAtUtc(handle);
            if (readIntegrity) integrityLevel = WindowsProcessIntegrity.TryRead(handle);
            return imagePath is null || startedAtUtc is null
                || WaitForSingleObject(handle, 0) != WaitTimeout
                ? null
                : new(processId, imagePath, startedAtUtc.Value);
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

    private static string? ReadImagePath(SafeProcessHandle handle)
    {
        var buffer = new StringBuilder(32_768);
        var length = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref length)
            ? Path.GetFullPath(buffer.ToString())
            : null;
    }

    private static DateTimeOffset? ReadStartedAtUtc(SafeProcessHandle handle)
    {
        return GetProcessTimes(handle, out var created, out _, out _, out _)
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(created.ToFileTime()))
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime(uint lowDateTime, uint highDateTime)
    {
        public long ToFileTime() => checked((long)(((ulong)highDateTime << 32) | lowDateTime));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executablePath,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

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
