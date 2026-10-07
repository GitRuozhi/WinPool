using System.Runtime.InteropServices;
using System.Diagnostics;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Read-only VDS filesystem classification for an exact volume GUID path.
/// COM slots and structs follow the installed Windows SDK vds.h. No formatting,
/// mount, refresh, or other mutation method is declared or called.
/// VDS distinguishes RAW from UNKNOWN (which includes locked BitLocker volumes):
/// https://learn.microsoft.com/windows/win32/api/vdshwprv/ne-vdshwprv-vds_file_system_type.
/// </summary>
internal static class WindowsReadOnlyVdsFileSystemReader
{
    internal sealed record Evidence(string VolumePath, int FileSystemType, uint AllocationUnitSize);
    private static readonly object securityInitializationGate = new();
    private static int? processSecurityResult;

    // Called once by Agent startup before any COM/WMI activity. A late or failed
    // initialization is reported by HRESULT; the volume read remains fail closed.
    internal static int InitializeProcessSecurity()
    {
        lock (securityInitializationGate)
            return processSecurityResult ??= CoInitializeSecurity(IntPtr.Zero,
                -1, IntPtr.Zero, IntPtr.Zero, 6, 3, IntPtr.Zero, 0, IntPtr.Zero);
    }

    internal static Evidence Read(string volumePath, CancellationToken token)
    {
        object? loaderObject = null, serviceObject = null;
        try
        {
            token.ThrowIfCancellationRequested();
            lock (securityInitializationGate)
                if (processSecurityResult != 0)
                    throw new InvalidDataException("Read-only VDS process security was not initialized early: "
                        + (processSecurityResult is { } result ? $"HRESULT 0x{result:X8}" : "no initialization result"));
            var loaderClass = Guid.Parse("9c38ed61-d565-4728-aeee-c80952f0ecde");
            var loaderInterface = typeof(IVdsServiceLoader).GUID;
            // Loading VDS explicitly requires LOCAL_SERVER without DISABLE_AAA.
            // https://learn.microsoft.com/windows/win32/vds/loading-vds
            Check(CoCreateInstance(ref loaderClass, IntPtr.Zero, 4, ref loaderInterface, out var loaderPointer));
            try { loaderObject = Marshal.GetObjectForIUnknown(loaderPointer); }
            finally { Marshal.Release(loaderPointer); }
            Secure((IVdsServiceLoader)loaderObject);
            Check(((IVdsServiceLoader)loaderObject).LoadService(null, out var service));
            serviceObject = service;
            Secure(service);
            var initialization = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var ready = service.IsServiceReady();
                if (ready == 0) break;
                if (ready != 1) Check(ready);
                if (initialization.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new InvalidDataException("The read-only VDS service did not become ready within 10 seconds.");
                if (token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
            }
            Check(service.QueryProviders(1, out var providers));
            var matches = new List<Evidence>();
            Visit(providers, token, provider =>
            {
                Secure((IVdsSwProvider)provider);
                Check(((IVdsSwProvider)provider).QueryPacks(out var packs));
                Visit(packs, token, pack =>
                {
                    Secure((IVdsPack)pack);
                    Check(((IVdsPack)pack).QueryVolumes(out var volumes));
                    Visit(volumes, token, volume =>
                    {
                        Secure((IVdsVolumeMF3)volume);
                        Check(((IVdsVolumeMF3)volume).QueryVolumeGuidPathnames(out var paths, out var count));
                        try
                        {
                            if (count > 64 || count > 0 && paths == IntPtr.Zero)
                                throw new InvalidDataException("The VDS volume GUID path result is invalid.");
                            var matched = false;
                            for (var index = 0; index < count; index++)
                                matched |= StringComparer.OrdinalIgnoreCase.Equals(
                                    Marshal.PtrToStringUni(Marshal.ReadIntPtr(paths, index * IntPtr.Size)), volumePath);
                            if (!matched) return;
                            Secure((IVdsVolumeMF)volume);
                            Check(((IVdsVolumeMF)volume).GetFileSystemProperties(out var fs));
                            try { matches.Add(new(volumePath, fs.Type, fs.AllocationUnitSize)); }
                            finally { Marshal.FreeCoTaskMem(fs.Label); }
                        }
                        finally
                        {
                            for (var index = 0; index < Math.Min(count, 64); index++)
                                if (paths != IntPtr.Zero) Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(paths, index * IntPtr.Size));
                            Marshal.FreeCoTaskMem(paths);
                        }
                    });
                });
            });
            token.ThrowIfCancellationRequested();
            return matches.Count == 1 ? matches[0]
                : throw new InvalidDataException("The exact VDS volume is absent or not unique.");
        }
        finally
        {
            Release(serviceObject);
            Release(loaderObject);
        }
    }

    private static void Visit(IEnumVdsObject enumerator, CancellationToken token, Action<object> visit)
    {
        try
        {
            Secure(enumerator);
            for (var index = 0; index < 4096; index++)
            {
                token.ThrowIfCancellationRequested();
                var hr = enumerator.Next(1, out var item, out var fetched);
                if (hr != 1) Check(hr);
                if (fetched == 0 && hr == 1) { Release(item); return; }
                if (fetched != 1 || item is null || hr != 0)
                    throw new InvalidDataException("The VDS enumeration result is invalid.");
                try { visit(item); }
                finally { Release(item); }
            }
            throw new InvalidDataException("The VDS enumeration exceeds the read bound.");
        }
        finally { Release(enumerator); }
    }

    private static void Check(int hr,
        [System.Runtime.CompilerServices.CallerArgumentExpression("hr")] string? call = null)
    {
        if (hr != 0) throw new InvalidDataException(
            $"The read-only VDS call failed ({call}, HRESULT 0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
    }
    private static void Release(object? item)
    {
        if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
    }

    private static void Secure<T>(T proxy) where T : class
    {
        // Configure this proxy only; do not change process or system COM security.
        // Default COM impersonation is IDENTIFY, while VDS must impersonate this
        // administrator for its read-only calls. RPC constants follow rpcdce.h.
        var pointer = Marshal.GetComInterfaceForObject(proxy, typeof(T));
        try { Check(CoSetProxyBlanket(pointer, 10, 0, IntPtr.Zero, 6, 3, IntPtr.Zero, 0)); }
        finally { Marshal.Release(pointer); }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeSecurity(IntPtr security, int serviceCount,
        IntPtr services, IntPtr reserved, uint authenticationLevel, uint impersonationLevel,
        IntPtr authenticationList, uint capabilities, IntPtr reserved3);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context,
        ref Guid interfaceId, out IntPtr result);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoSetProxyBlanket(IntPtr proxy, uint authenticationService,
        uint authorizationService, IntPtr principal, uint authenticationLevel,
        uint impersonationLevel, IntPtr authenticationInfo, uint capabilities);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileSystemProperties
    {
        internal int Type;
        internal Guid VolumeId;
        internal uint Flags;
        internal ulong TotalAllocationUnits, AvailableAllocationUnits;
        internal uint AllocationUnitSize;
        internal IntPtr Label;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceProperties { internal IntPtr Version; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PackProperties { internal Guid Id; internal IntPtr Name; internal int Status; internal uint Flags; }

    // Only the leading slots required for read-only calls are declared, in SDK order.
    [ComImport, Guid("e0393303-90d4-4a97-ab71-e9b671ee2729"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVdsServiceLoader
    {
        [PreserveSig] int LoadService([MarshalAs(UnmanagedType.LPWStr)] string? machineName, out IVdsService service);
    }
    [ComImport, Guid("0818a8ef-9ba9-40d8-a6f9-e22833cc771e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVdsService
    {
        [PreserveSig] int IsServiceReady();
        [PreserveSig] int WaitForServiceReady();
        [PreserveSig] int GetProperties(out ServiceProperties properties);
        [PreserveSig] int QueryProviders(uint mask, out IEnumVdsObject providers);
    }
    [ComImport, Guid("118610b7-8d94-4030-b5b8-500889788e4e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumVdsObject
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.IUnknown)] out object? item, out uint fetched);
    }
    [ComImport, Guid("9aa58360-ce33-4f92-b658-ed24b14425b8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVdsSwProvider
    {
        [PreserveSig] int QueryPacks(out IEnumVdsObject packs);
    }
    [ComImport, Guid("3b69d7f5-9d94-4648-91ca-79939ba263bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVdsPack
    {
        [PreserveSig] int GetProperties(out PackProperties properties);
        [PreserveSig] int GetProvider(out IntPtr provider);
        [PreserveSig] int QueryVolumes(out IEnumVdsObject volumes);
    }
    [ComImport, Guid("6788faf9-214e-4b85-ba59-266953616e09"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVdsVolumeMF3
    {
        [PreserveSig] int QueryVolumeGuidPathnames(out IntPtr paths, out uint count);
    }
    [ComImport, Guid("ee2d5ded-6236-4169-931d-b9778ce03dc6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVdsVolumeMF
    {
        [PreserveSig] int GetFileSystemProperties(out FileSystemProperties properties);
    }
}
