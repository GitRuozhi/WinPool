using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// First native provider slice. It uses read-only Win32/NT calls for system and
/// mounted-volume facts and runs in parallel with the legacy fixed script.
/// Storage pools, tiers and virtual disks remain on the legacy provider until
/// their native Storage Management API collectors pass field comparison.
/// </summary>
public sealed class NativeWindowsInventoryProvider : IInventoryProvider
{
    public InventoryProviderKind Kind => InventoryProviderKind.NativeWindows;

    public Task<ApplicationResult<InventorySnapshot>> CaptureAsync(
        InventoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var correlation = CorrelationId.New();
        if (request.SystemId.Value == Guid.Empty)
        {
            return Task.FromResult(
                Failure(
                    ApplicationStatus.Rejected,
                    correlation,
                    "inventory.request.missing_system"));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capturedAt = DateTimeOffset.UtcNow;
            var computerName = Environment.MachineName;
            var osVersion = NativeInventoryApi.ReadOsVersion();
            var systemObject = new StorageObjectId(
                request.SystemId,
                StorageObjectKind.System,
                HashIdentity($"computer:{computerName}"));
            var objects = new List<StorageObjectView>
            {
                new(
                    systemObject,
                    null,
                    computerName,
                    IdentityStability.Stable,
                    new Dictionary<string, string?>
                    {
                        ["provider"] = "Win32/NT native",
                        ["osMajor"] = osVersion.Major.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        ["osMinor"] = osVersion.Minor.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        ["osBuild"] = osVersion.Build.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                    })
            };
            var diagnostics = new List<InventoryIdentityDiagnostic>();
            var relationships = new List<StorageRelationshipView>();
            var diskObjects = new Dictionary<uint, StorageObjectId>();
            foreach (var disk in NativeInventoryApi.ReadPhysicalDisks())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stable = !string.IsNullOrWhiteSpace(disk.SerialNumber);
                var identityMaterial = stable
                    ? $"disk:{disk.SerialNumber}|{disk.ProductId}"
                    : $"physical-drive:{disk.Number}|{disk.ProductId}|{disk.SizeBytes}";
                var id = new StorageObjectId(
                    request.SystemId,
                    StorageObjectKind.PhysicalDisk,
                    HashIdentity(identityMaterial));
                diskObjects[disk.Number] = id;
                var properties = new Dictionary<string, string?>
                {
                    ["physicalDriveNumber"] = Number(disk.Number),
                    ["model"] = First(disk.ProductId, disk.VendorId, $"PhysicalDrive{disk.Number}"),
                    ["vendor"] = disk.VendorId,
                    ["firmwareVersion"] = disk.ProductRevision,
                    ["busType"] = disk.BusType,
                    ["sizeBytes"] = Number(disk.SizeBytes),
                    ["logicalSectorSizeBytes"] = Number(disk.BytesPerSector),
                    ["removable"] = disk.Removable ? "true" : "false"
                };
                if (request.IncludeSensitiveValuesInMemory)
                {
                    properties["serialNumber"] = disk.SerialNumber;
                }

                objects.Add(
                    new(
                        id,
                        systemObject,
                        First(disk.ProductId, $"Physical Disk {disk.Number}"),
                        stable
                            ? IdentityStability.Stable
                            : IdentityStability.Temporary,
                        properties));
                if (!stable)
                {
                    diagnostics.Add(
                        new(
                            id,
                            IdentityStability.Temporary,
                            "inventory.native.disk_serial_unavailable",
                            string.Empty));
                }
            }

            foreach (var volume in NativeInventoryApi.ReadMountedVolumes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stable = !string.IsNullOrWhiteSpace(volume.VolumeGuid);
                var identityMaterial = stable
                    ? volume.VolumeGuid
                    : $"mount:{volume.RootPath}";
                var id = new StorageObjectId(
                    request.SystemId,
                    StorageObjectKind.Partition,
                    HashIdentity(identityMaterial));
                var properties = new Dictionary<string, string?>
                {
                    ["driveLetter"] = volume.RootPath.TrimEnd('\\'),
                    ["fileSystemLabel"] = volume.Label,
                    ["fileSystem"] = volume.FileSystem,
                    ["driveType"] = volume.DriveType,
                    ["sizeBytes"] = Number(volume.TotalBytes),
                    ["sizeRemainingBytes"] = Number(volume.FreeBytes),
                    ["serialNumber"] = volume.SerialNumber.ToString("X8"),
                    ["maximumComponentLength"] = Number(volume.MaximumComponentLength),
                    ["fileSystemFlags"] = volume.FileSystemFlags.ToString("X8")
                };
                if (request.IncludeSensitiveValuesInMemory)
                {
                    properties["volumeGuid"] = volume.VolumeGuid;
                }

                objects.Add(
                    new StorageObjectView(
                        id,
                        systemObject,
                        First(volume.Label, volume.RootPath.TrimEnd('\\')),
                        stable
                            ? IdentityStability.Stable
                            : IdentityStability.Temporary,
                        properties));
                foreach (var diskNumber in volume.PhysicalDiskNumbers)
                {
                    if (diskObjects.TryGetValue(diskNumber, out var diskId))
                    {
                        relationships.Add(
                            new(
                                diskId,
                                id,
                                "contains-volume-extent"));
                    }
                }

                if (!stable)
                {
                    diagnostics.Add(
                        new InventoryIdentityDiagnostic(
                            id,
                            IdentityStability.Temporary,
                            "inventory.native.volume_guid_unavailable",
                            string.Empty));
                }
            }

            var storageManagement = NativeStorageManagementInventoryReader.Read(
                request.SystemId,
                systemObject,
                diskObjects);
            objects.AddRange(storageManagement.Objects);
            relationships.AddRange(storageManagement.Relationships);
            if (storageManagement.DiagnosticCode is { } diagnosticCode)
            {
                diagnostics.Add(
                    new(
                        systemObject,
                        IdentityStability.Stable,
                        diagnosticCode,
                        string.Empty));
            }

            var version = InventoryVersion(objects);
            var snapshot = new InventorySnapshot(
                request.SystemId,
                Kind,
                version,
                MachineBinding.Create([computerName]),
                capturedAt,
                objects,
                diagnostics,
                relationships);
            if (!string.IsNullOrWhiteSpace(request.ExpectedInventoryVersion)
                && !string.Equals(
                    request.ExpectedInventoryVersion,
                    version,
                    StringComparison.Ordinal))
            {
                return Task.FromResult(
                    new ApplicationResult<InventorySnapshot>(
                        ApplicationStatus.Rejected,
                        snapshot,
                        [Message("inventory.version.changed")],
                        correlation));
            }

            return Task.FromResult(
                ApplicationResult<InventorySnapshot>.Succeeded(
                    snapshot,
                    correlation));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(
                Failure(
                    ApplicationStatus.Cancelled,
                    correlation,
                    "inventory.capture.cancelled"));
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or IOException)
        {
            return Task.FromResult(
                Failure(
                    ApplicationStatus.Failed,
                    correlation,
                    "inventory.native.capture_failed"));
        }
    }

    private static string InventoryVersion(
        IEnumerable<StorageObjectView> objects)
    {
        var material = string.Join(
            '\n',
            objects
                .OrderBy(item => item.Id.Kind)
                .ThenBy(item => item.Id.ProviderKey, StringComparer.Ordinal)
                .Select(item =>
                    $"{item.Id.Kind}|{item.Id.ProviderKey}|"
                    + string.Join(
                        ';',
                        item.Properties
                            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .Select(pair => $"{pair.Key}={pair.Value}"))));
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private static string HashIdentity(string value) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant())))
            .ToLowerInvariant();

    private static string Number(ulong value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Number(uint value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
        ?? string.Empty;

    private static ApplicationResult<InventorySnapshot> Failure(
        ApplicationStatus status,
        CorrelationId correlation,
        string code) =>
        ApplicationResult<InventorySnapshot>.FromStatus(
            status,
            correlation,
            Message(code));

    private static ApplicationMessage Message(string code) =>
        new(
            code,
            code,
            string.Empty,
            ApplicationMessageSeverity.Warning,
            []);
}

internal sealed record NativeMountedVolume(
    string RootPath,
    string VolumeGuid,
    string Label,
    string FileSystem,
    string DriveType,
    ulong TotalBytes,
    ulong FreeBytes,
    uint SerialNumber,
    uint MaximumComponentLength,
    uint FileSystemFlags,
    IReadOnlyList<uint> PhysicalDiskNumbers);

internal sealed record NativePhysicalDisk(
    uint Number,
    ulong SizeBytes,
    uint BytesPerSector,
    string VendorId,
    string ProductId,
    string ProductRevision,
    string SerialNumber,
    string BusType,
    bool Removable);

internal readonly record struct NativeOsVersion(
    uint Major,
    uint Minor,
    uint Build);

internal static class NativeInventoryApi
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

    public static NativeOsVersion ReadOsVersion()
    {
        var information = new RtlOsVersionInfo
        {
            Size = (uint)Marshal.SizeOf<RtlOsVersionInfo>()
        };
        var status = RtlGetVersion(ref information);
        if (status != 0)
        {
            throw new Win32Exception(
                status,
                "RtlGetVersion failed.");
        }

        return new NativeOsVersion(
            information.Major,
            information.Minor,
            information.Build);
    }

    public static IReadOnlyList<NativeMountedVolume> ReadMountedVolumes()
    {
        var required = GetLogicalDriveStrings(0, null);
        if (required == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var buffer = new StringBuilder(checked((int)required));
        var written = GetLogicalDriveStrings(required, buffer);
        if (written == 0 || written > required)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var roots = buffer
            .ToString()
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var volumes = new List<NativeMountedVolume>(roots.Length);
        foreach (var root in roots)
        {
            var volumeName = new StringBuilder(128);
            var hasVolumeName = GetVolumeNameForVolumeMountPoint(
                root,
                volumeName,
                (uint)volumeName.Capacity);
            var label = new StringBuilder(261);
            var fileSystem = new StringBuilder(64);
            var hasInformation = GetVolumeInformation(
                root,
                label,
                (uint)label.Capacity,
                out var serial,
                out var maximumComponentLength,
                out var flags,
                fileSystem,
                (uint)fileSystem.Capacity);
            var hasSpace = GetDiskFreeSpaceEx(
                root,
                out var freeAvailable,
                out var totalBytes,
                out var totalFreeBytes);
            volumes.Add(
                new NativeMountedVolume(
                    root,
                    hasVolumeName ? volumeName.ToString() : string.Empty,
                    hasInformation ? label.ToString().TrimEnd('\0') : string.Empty,
                    hasInformation ? fileSystem.ToString().TrimEnd('\0') : string.Empty,
                    DriveTypeName(GetDriveType(root)),
                    hasSpace ? totalBytes : 0,
                    hasSpace ? totalFreeBytes : freeAvailable,
                    hasInformation ? serial : 0,
                    hasInformation ? maximumComponentLength : 0,
                    hasInformation ? flags : 0,
                    ReadVolumeDiskNumbers(root)));
        }

        return volumes;
    }

    public static IReadOnlyList<NativePhysicalDisk> ReadPhysicalDisks()
    {
        var disks = new List<NativePhysicalDisk>();
        var consecutiveMissing = 0;
        for (uint number = 0; number < 256 && consecutiveMissing < 32; number++)
        {
            using var handle = CreateFile(
                $@"\\.\PhysicalDrive{number}",
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                consecutiveMissing++;
                continue;
            }

            consecutiveMissing = 0;
            var geometry = TryDeviceIoControlBuffer(
                handle,
                IoctlDiskGetDriveGeometryEx,
                null,
                1024);
            var descriptor = TryDeviceIoControlBuffer(
                handle,
                IoctlStorageQueryProperty,
                new byte[12],
                4096);
            var size = geometry.Length >= 32
                ? checked((ulong)BitConverter.ToInt64(geometry, 24))
                : 0;
            var bytesPerSector = geometry.Length >= 24
                ? BitConverter.ToUInt32(geometry, 20)
                : 0;
            disks.Add(
                new(
                    number,
                    size,
                    bytesPerSector,
                    DescriptorString(descriptor, 12),
                    DescriptorString(descriptor, 16),
                    DescriptorString(descriptor, 20),
                    DescriptorString(descriptor, 24),
                    descriptor.Length >= 32
                        ? StorageBusTypeName(BitConverter.ToUInt32(descriptor, 28))
                        : "Unknown",
                    descriptor.Length >= 11 && descriptor[10] != 0));
        }

        return disks;
    }

    private static byte[] TryDeviceIoControlBuffer(
        SafeFileHandle handle,
        uint controlCode,
        byte[]? input,
        int outputLength)
    {
        try
        {
            return DeviceIoControlBuffer(
                handle,
                controlCode,
                input,
                outputLength);
        }
        catch (Win32Exception)
        {
            return [];
        }
    }

    private static IReadOnlyList<uint> ReadVolumeDiskNumbers(string root)
    {
        if (GetDriveType(root) == 4)
        {
            return [];
        }

        var volumePath = $@"\\.\{root.TrimEnd('\\')}";
        using var handle = CreateFile(
            volumePath,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return [];
        }

        try
        {
            var buffer = DeviceIoControlBuffer(
                handle,
                IoctlVolumeGetVolumeDiskExtents,
                null,
                64 * 1024);
            if (buffer.Length < 8)
            {
                return [];
            }

            var count = BitConverter.ToUInt32(buffer, 0);
            var boundedCount = checked((int)Math.Min(count, 1024u));
            var results = new List<uint>(boundedCount);
            const int extentSize = 24;
            const int firstExtentOffset = 8;
            for (var index = 0; index < boundedCount; index++)
            {
                var offset = checked(firstExtentOffset + (int)index * extentSize);
                if (offset + extentSize > buffer.Length)
                {
                    break;
                }

                results.Add(BitConverter.ToUInt32(buffer, offset));
            }

            return results.Distinct().ToArray();
        }
        catch (Win32Exception)
        {
            return [];
        }
    }

    private static byte[] DeviceIoControlBuffer(
        SafeFileHandle handle,
        uint controlCode,
        byte[]? input,
        int outputLength)
    {
        var output = new byte[outputLength];
        if (!DeviceIoControl(
                handle,
                controlCode,
                input,
                input?.Length ?? 0,
                output,
                output.Length,
                out var bytesReturned,
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return output.AsSpan(0, checked((int)bytesReturned)).ToArray();
    }

    private static string DescriptorString(byte[] descriptor, int offsetField)
    {
        if (descriptor.Length < offsetField + sizeof(uint))
        {
            return string.Empty;
        }

        var offset = BitConverter.ToUInt32(descriptor, offsetField);
        if (offset == 0 || offset >= descriptor.Length)
        {
            return string.Empty;
        }

        var end = Array.IndexOf(descriptor, (byte)0, checked((int)offset));
        if (end < 0)
        {
            end = descriptor.Length;
        }

        return Encoding.ASCII
            .GetString(descriptor, checked((int)offset), end - checked((int)offset))
            .Trim();
    }

    private static string StorageBusTypeName(uint value) =>
        value switch
        {
            1 => "SCSI",
            2 => "ATAPI",
            3 => "ATA",
            4 => "1394",
            5 => "SSA",
            6 => "Fibre",
            7 => "USB",
            8 => "RAID",
            9 => "iSCSI",
            10 => "SAS",
            11 => "SATA",
            12 => "SD",
            13 => "MMC",
            14 => "Virtual",
            15 => "FileBackedVirtual",
            16 => "StorageSpaces",
            17 => "NVMe",
            18 => "SCM",
            19 => "UFS",
            _ => "Unknown"
        };

    private static string DriveTypeName(uint value) =>
        value switch
        {
            2 => "Removable",
            3 => "Fixed",
            4 => "Network",
            5 => "Optical",
            6 => "RamDisk",
            _ => "Unknown"
        };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RtlOsVersionInfo
    {
        public uint Size;
        public uint Major;
        public uint Minor;
        public uint Build;
        public uint PlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ServicePack;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref RtlOsVersionInfo versionInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLogicalDriveStrings(
        uint bufferLength,
        StringBuilder? buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        StringBuilder volumeName,
        uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        uint fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[]? inputBuffer,
        int inputBufferSize,
        byte[] outputBuffer,
        int outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
