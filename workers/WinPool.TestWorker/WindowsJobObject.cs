using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinPool.TestWorker;

internal interface IProcessTreeJob : IDisposable
{
    void Assign(Process process);

    void Terminate(uint exitCode);
}

internal interface IProcessTreeJobFactory
{
    IProcessTreeJob Create();
}

internal sealed class WindowsJobObjectFactory : IProcessTreeJobFactory
{
    public IProcessTreeJob Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "WinPool.TestWorker process supervision requires Windows Job Objects.");
        }

        return new WindowsJobObject();
    }
}

internal sealed partial class WindowsJobObject : IProcessTreeJob
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int ExtendedLimitInformationClass = 9;

    private readonly SafeFileHandle _handle;
    private bool _disposed;

    public WindowsJobObject()
    {
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        if (!NativeMethods.SetInformationJobObject(
                _handle,
                ExtendedLimitInformationClass,
                ref information,
                checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            _handle.Dispose();
            throw error;
        }
    }

    public void Assign(Process process)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(process);
        if (!NativeMethods.AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Terminate(uint exitCode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!NativeMethods.TerminateJobObject(_handle, exitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW",
            SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial SafeFileHandle CreateJobObject(
            IntPtr jobAttributes,
            string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AssignProcessToJobObject(
            SafeFileHandle job,
            SafeProcessHandle process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool TerminateJobObject(
            SafeFileHandle job,
            uint exitCode);
    }
}
