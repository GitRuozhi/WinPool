using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinPool.Infrastructure.Windows;

/// <summary>Creates the process already inside the machine-wide, one-member Job.</summary>
internal sealed class PowerShellHostProcess : IDisposable
{
    internal const string JobName = @"Global\WinPool.PowerShellHost";
    private static readonly object ownershipGate = new();
    // Keep the observed name and exact exit witnesses for this Agent process.
    // Job membership may disappear during termination before a process handle
    // becomes signalled; membership alone is not proof of process exit.
    private static SafeFileHandle? processJobGuard;
    private static readonly Dictionary<int, (SafeProcessHandle Handle, JobMember Identity)> observedMembers = new();
    private readonly SafeFileHandle job;
    private readonly Process process;
    private readonly SafeProcessHandle processHandle;
    public Stream Input { get; }
    public Stream Output { get; }
    public Stream Error { get; }
    public int Id => process.Id;
    public DateTimeOffset StartedAtUtc { get; }
    public uint ExitCode => GetExitCodeProcess(processHandle, out var code) ? code
        : throw new Win32Exception(Marshal.GetLastWin32Error());
    public bool HasExited
    {
        get
        {
            var result = WaitForSingleObject(processHandle, 0);
            return result switch { 0 => true, 258 => false,
                _ => throw new Win32Exception(Marshal.GetLastWin32Error(), $"The exact PowerShell process exit signal is unverifiable (wait={result}).") };
        }
    }
    internal static bool IsProcessExited(int processId)
    {
        using var handle = OpenProcess(0x00100000, false, processId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return error == 87 ? true : throw new Win32Exception(error);
        }
        var result = WaitForSingleObject(handle, 0);
        return result switch { 0 => true, 258 => false, _ => throw new Win32Exception(Marshal.GetLastWin32Error()) };
    }

    internal async Task WaitForExitSignalAsync(CancellationToken token)
    {
        while (!HasExited)
        {
            token.ThrowIfCancellationRequested();
            if (await WaitForExactExitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false)) return;
        }
    }

    private PowerShellHostProcess(SafeFileHandle job, Process process, SafeProcessHandle processHandle, Stream input, Stream output, Stream error)
    {
        this.job = job; this.process = process; this.processHandle = processHandle; Input = input; Output = output; Error = error;
        if (!GetProcessTimes(processHandle, out var creation, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The exact PowerShell process creation time is unavailable.");
        StartedAtUtc = new DateTimeOffset(DateTime.FromFileTimeUtc(creation));
    }

    public static PowerShellHostProcess Start(string encodedBootstrap)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No Windows user SID.");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{sid})", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        SafeFileHandle? job = null;
        try
        {
            security.SecurityDescriptor = descriptor;
            job = CreateJobObject(ref security, JobName);
            var existed = Marshal.GetLastWin32Error() == 183;
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "The global PowerShell host Job is unavailable.");
            if (!existed)
            {
                var limits = new JobExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = 0x8; // ACTIVE_PROCESS only; never KILL_ON_JOB_CLOSE.
                limits.BasicLimitInformation.ActiveProcessLimit = 1;
                if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobExtendedLimitInformation>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (!QueryInformationJobObject(job, 9, out JobExtendedLimitInformation current,
                    Marshal.SizeOf<JobExtendedLimitInformation>(), out _)
                || current.BasicLimitInformation.LimitFlags != 0x8
                || current.BasicLimitInformation.ActiveProcessLimit != 1)
                throw new InvalidOperationException("The existing PowerShell host Job constraints cannot be verified.");
            lock (ownershipGate)
                RetainJobNameLocked(job);
            var existingMembers = ReadMembers(job);
            if (existingMembers.Any(member => !member.Exited))
                throw new InvalidOperationException("A previous WinPool PowerShell host is still running. No replacement host is permitted. " + string.Join("; ", existingMembers));

            var pipeSecurity = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            if (!CreatePipe(out var childInput, out var parentInput, ref pipeSecurity, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (childInput)
            using (parentInput)
            {
                if (!CreatePipe(out var parentOutput, out var childOutput, ref pipeSecurity, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                using (parentOutput)
                using (childOutput)
                {
                    if (!CreatePipe(out var parentError, out var childError, ref pipeSecurity, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    using (parentError)
                    using (childError)
                    {
                        Check(SetHandleInformation(parentInput, 1, 0));
                        Check(SetHandleInformation(parentOutput, 1, 0));
                        Check(SetHandleInformation(parentError, 1, 0));
                        nuint size = 0;
                        // A child-held query handle keeps the Job's name open
                        // across an Agent crash, including the pre-journal gap.
                        Check(DuplicateHandle(GetCurrentProcess(), job, GetCurrentProcess(), out var childJob, 4, true, 0));
                        using var childJobHandle = childJob;
                        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                        var attributes = Marshal.AllocHGlobal(checked((int)size));
                        var jobPointer = Marshal.AllocHGlobal(IntPtr.Size);
                        var handles = Marshal.AllocHGlobal(4 * IntPtr.Size);
                        var initialized = false;
                        try
                        {
                            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size)); initialized = true;
                            Marshal.WriteIntPtr(jobPointer, job.DangerousGetHandle());
                            Check(UpdateProcThreadAttribute(attributes, 0, (nuint)0x0002000D, jobPointer, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
                            Marshal.WriteIntPtr(handles, 0, childInput.DangerousGetHandle());
                            Marshal.WriteIntPtr(handles, IntPtr.Size, childOutput.DangerousGetHandle());
                            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, childError.DangerousGetHandle());
                            Marshal.WriteIntPtr(handles, 3 * IntPtr.Size, childJobHandle.DangerousGetHandle());
                            Check(UpdateProcThreadAttribute(attributes, 0, (nuint)0x00020002, handles, (nuint)(4 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
                            var startup = new StartupInfoEx
                            {
                                StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x101, ShowWindow = 0,
                                    StandardInput = childInput.DangerousGetHandle(), StandardOutput = childOutput.DangerousGetHandle(), StandardError = childError.DangerousGetHandle() },
                                AttributeList = attributes
                            };
                            var executable = WindowsPowerShellRunner.ExecutablePath;
                            var command = new StringBuilder($"\"{executable}\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedBootstrap}");
                            Check(CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                                0x08000000 | 0x00080000, IntPtr.Zero, null, ref startup, out var created));
                            var nativeProcess = new SafeProcessHandle(created.Process, true);
                            using var nativeThread = new SafeFileHandle(created.Thread, true);
                            Process? process = null; FileStream? input = null, output = null, error = null;
                            var transferred = false;
                            try
                            {
                                process = Process.GetProcessById(created.ProcessId);
                                // Duplicate only the parent ends. The child ends close before return.
                                input = DuplicateStream(parentInput, FileAccess.Write);
                                output = DuplicateStream(parentOutput, FileAccess.Read);
                                error = DuplicateStream(parentError, FileAccess.Read);
                                var result = new PowerShellHostProcess(job, process, nativeProcess, input, output, error);
                                job = null; transferred = true;
                                return result;
                            }
                            finally
                            {
                                if (!transferred)
                                { input?.Dispose(); output?.Dispose(); error?.Dispose(); process?.Dispose(); nativeProcess.Dispose(); }
                            }
                        }
                        finally
                        {
                            if (initialized) DeleteProcThreadAttributeList(attributes);
                            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(jobPointer); Marshal.FreeHGlobal(handles);
                        }
                    }
                }
            }
        }
        finally { job?.Dispose(); LocalFree(descriptor); }
    }

    private static FileStream DuplicateStream(SafeFileHandle source, FileAccess access)
    {
        var current = GetCurrentProcess();
        Check(DuplicateHandle(current, source, current, out var duplicate, 0, false, 2));
        return new FileStream(duplicate, access, 4096, isAsync: false);
    }

    internal static bool VerifyShutdownOccupancy(int? ownedProcessId, out string? reason)
    {
        reason = null;
        using var handle = OpenJobObject(4, false, JobName);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 2) return true;
            reason = "The global host Job cannot be opened: " + new Win32Exception(error).Message;
            return false;
        }
        if (!QueryInformationJobObject(handle, 9, out JobExtendedLimitInformation limits,
                Marshal.SizeOf<JobExtendedLimitInformation>(), out _)
            || limits.BasicLimitInformation.LimitFlags != 8 || limits.BasicLimitInformation.ActiveProcessLimit != 1)
        { reason = "The global host Job limits cannot be verified."; return false; }
        try
        {
            var members = ReadMembers(handle);
            var live = members.Where(member => !member.Exited).ToArray();
            if (!QueryInformationJobObject(handle, 1, out JobBasicAccountingInformation accounting,
                Marshal.SizeOf<JobBasicAccountingInformation>(), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (live.Length == 0 && accounting.ActiveProcesses == 0) return true;
            var owned = live.SingleOrDefault(member => member.ProcessId == ownedProcessId);
            if (owned is not null && live.All(member => member == owned || IsOwnedConsole(member, owned))
                && live.Length <= 2 && accounting.ActiveProcesses is 1 or 2) return true;
            reason = $"The global host Job occupancy differs from owned PID {ownedProcessId?.ToString() ?? "none"}: {string.Join("; ", members)}.";
            return false;
        }
        catch (Exception error) when (error is Win32Exception or InvalidDataException)
        { reason = "The global host Job members cannot be verified: " + error.Message; return false; }
    }

    private static bool IsOwnedConsole(JobMember member, JobMember owned) => member.ParentProcessId == owned.ProcessId
        && member.StartedAtUtc >= owned.StartedAtUtc
        && StringComparer.OrdinalIgnoreCase.Equals(member.ImagePath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe"));

    internal static string DescribeGlobalJob()
    {
        using var handle = OpenJobObject(4, false, JobName);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!QueryInformationJobObject(handle, 1, out JobBasicAccountingInformation accounting,
            Marshal.SizeOf<JobBasicAccountingInformation>(), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return System.Text.Json.JsonSerializer.Serialize(new { accounting.ActiveProcesses, Members = ReadMembers(handle) });
    }

    private sealed record JobMember(int ProcessId, string ImagePath, bool Exited, int ParentProcessId = 0, DateTimeOffset StartedAtUtc = default);
    private static IReadOnlyList<JobMember> ReadMembers(SafeFileHandle handle)
    {
        lock (ownershipGate) return ReadMembersLocked(handle);
    }
    private static IReadOnlyList<JobMember> ReadMembersLocked(SafeFileHandle handle)
    {
        RetainJobNameLocked(handle);
        const int capacity = 65536;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            if (!QueryInformationJobObject(handle, 3, buffer, capacity, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var assigned = Marshal.ReadInt32(buffer); var count = Marshal.ReadInt32(buffer, 4);
            if (count < 0 || count != assigned || 8L + (long)count * IntPtr.Size > capacity)
                throw new InvalidDataException("The global host Job member list is incomplete.");
            var result = new List<JobMember>(count);
            for (var index = 0; index < count; index++)
            {
                var id = checked((int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64());
                if (observedMembers.TryGetValue(id, out var observed))
                { result.Add(observed.Identity); continue; }
                var process = OpenProcess(0x1000 | 0x0400 | 0x00100000, false, id);
                var retained = false;
                try
                {
                if (process.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 87) { result.Add(new(id, "already exited", true)); continue; }
                    throw new Win32Exception(error, $"Job member PID {id} is unverifiable.");
                }
                var wait = WaitForSingleObject(process, 0);
                if (wait is not (0 or 258)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Job member PID {id} exit signal is unverifiable.");
                var path = new StringBuilder(32768); var length = path.Capacity;
                var image = QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : "image unavailable";
                if (wait == 0) { result.Add(new(id, image, true)); continue; }
                var status = NtQueryInformationProcess(process, 0, out var basic,
                    Marshal.SizeOf<ProcessBasicInformation>(), out _);
                if (status != 0 || !GetProcessTimes(process, out var creation, out _, out _, out _))
                    throw new InvalidDataException($"Job member PID {id} parent/creation identity is unverifiable (NTSTATUS=0x{status:X8}).");
                var identity = new JobMember(id, image, false, checked((int)basic.ParentProcessId.ToInt64()),
                    new DateTimeOffset(DateTime.FromFileTimeUtc(creation)));
                observedMembers[id] = (process, identity); retained = true;
                result.Add(identity);
                }
                finally { if (!retained) process.Dispose(); }
            }
            foreach (var (id, witness) in observedMembers.ToArray())
            {
                var wait = WaitForSingleObject(witness.Handle, 0);
                if (wait is not (0 or 258)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"The previously observed host PID {id} exit signal is unverifiable.");
                result.RemoveAll(member => member.ProcessId == id);
                if (wait == 0) { observedMembers.Remove(id); witness.Handle.Dispose(); }
                else result.Add(witness.Identity);
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void RetainJobNameLocked(SafeFileHandle handle)
    {
        if (processJobGuard is not null) return;
        Check(DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out var retained, 0, false, 2));
        processJobGuard = retained;
    }

    public async Task<bool> TerminateReadOnlyAsync()
    {
        var started = Stopwatch.GetTimestamp();
        Input.Dispose();
        if (!HasExited && !TerminateProcess(processHandle, 1) && !HasExited)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!await WaitForExactExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)) return false;
        while (ReadMembers(job).Any(member => !member.Exited))
        {
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5)) return false;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return true;
    }

    private async Task<bool> WaitForExactExitAsync(TimeSpan timeout)
    {
        if (HasExited) return true;
        using var exited = new EventWaitHandle(false, EventResetMode.ManualReset);
        exited.SafeWaitHandle = new SafeWaitHandle(processHandle.DangerousGetHandle(), ownsHandle: false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(exited,
            (_, timedOut) => completion.TrySetResult(!timedOut), null, timeout, executeOnlyOnce: true);
        try { return await completion.Task.ConfigureAwait(false) && HasExited; }
        finally { registration.Unregister(null); }
    }

    public async ValueTask DisposeAsync()
    {
        var started = Stopwatch.GetTimestamp();
        Input.Dispose();
        if (!await WaitForExactExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            throw new InvalidOperationException("The PowerShell host has not exited; handoff remains unavailable.");
        // The OS may attach its own conhost to CREATE_NO_WINDOW. Never replace
        // the product host until that exact Job is entirely free of live members.
        while (ReadMembers(job).Any(member => !member.Exited))
        {
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("The old PowerShell host Job still has a live member; handoff remains unavailable.");
            await Task.Delay(10).ConfigureAwait(false);
        }
        Dispose();
    }

    public void Dispose() { Input.Dispose(); Output.Dispose(); Error.Dispose(); process.Dispose(); processHandle.Dispose(); job.Dispose(); }
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimitInformation { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasicAccountingInformation { public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime; public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, ParentProcessId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimitInformation { public JobBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public ushort ShowWindow, Reserved2Size; public IntPtr Reserved2, StandardInput, StandardOutput, StandardError; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(ref SecurityAttributes security, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobExtendedLimitInformation information, int length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out JobExtendedLimitInformation information, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out JobBasicAccountingInformation information, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess, out SafeFileHandle target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, out ProcessBasicInformation information, int length, out int returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr security, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
