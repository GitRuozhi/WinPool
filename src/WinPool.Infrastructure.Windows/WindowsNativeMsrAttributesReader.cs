using System.Buffers.Binary;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

internal sealed record WindowsNativeMsrIdentity(
    string DiskPath, string DiskUniqueId, string DiskObjectId, Guid DiskGuid,
    string PhysicalUniqueId, string PhysicalObjectId, string SerialNumber,
    Guid PartitionGuid, Guid PartitionType, long Offset, long Length, int PartitionNumber,
    string? VirtualDiskUniqueId = null, string? VirtualDiskObjectId = null,
    string? PoolUniqueId = null, string? PoolObjectId = null)
{
    internal string[] AccessPaths { get; init; } = [];
    internal bool ExpectedDiskOffline { get; init; }
    internal string NativeBindingMethod => VirtualDiskUniqueId is null
        ? "ProviderPathGptGuidAndPhysicalSerial"
        : "ProviderPathGptGuidAndVirtualDiskPoolSingleMemberAssociations";
}

internal sealed record WindowsNativeMsrAttributes(WindowsNativeMsrIdentity Identity, ulong Attributes)
{
    internal const ulong PlatformRequired = 1;
    internal const ulong ReadOnly = 0x1000000000000000;
    internal const ulong ShadowCopy = 0x2000000000000000;
}

/// <summary>
/// Reads a selected MSR, EFI, or Recovery partition through its exact provider path.
/// BasicData attributes are available only for an exact offline disk-state check.
/// No write access or write IOCTL is used.
/// Layout offsets follow Windows SDK winioctl.h (DRIVE_LAYOUT_INFORMATION_EX and
/// PARTITION_INFORMATION_EX); GPT attributes are documented at
/// https://learn.microsoft.com/windows/win32/api/winioctl/ns-winioctl-partition_information_gpt.
/// </summary>
internal static class WindowsNativeMsrAttributesReader
{
    internal static readonly Guid MsrRole = Guid.Parse("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    internal static readonly Guid EfiRole = Guid.Parse("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    internal static readonly Guid RecoveryRole = Guid.Parse("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
    internal static readonly Guid BasicDataRole = Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    internal static bool SupportsRole(Guid role) => role == MsrRole || role == EfiRole || role == RecoveryRole;
    private static bool SupportsRole(Guid role, bool diskOffline) => SupportsRole(role)
        || diskOffline && role == BasicDataRole;
    private const uint GetLayout = 0x00070050;
    private const uint QueryStorageProperty = 0x002D1400;

    internal static WindowsNativeMsrIdentity Identify(WindowsRealStorageTopology topology,
        PartitionInfo partition, bool expectedDiskOffline = false)
    {
        var closure = topology.RequireSinglePhysicalClosure([
            new StorageObjectId(topology.SystemId, StorageObjectKind.Partition, partition.StableId)]);
        var disk = topology.Snapshot.OsDisks.Single(item => item.StableId == partition.OsDiskStableId);
        if (disk.PartitionStyle != "GPT")
            throw new InvalidDataException("Native MSR evidence requires an exact basic GPT disk.");
        if (expectedDiskOffline && !disk.IsOffline)
            throw new InvalidDataException("Native offline partition evidence requires the exact offline disk.");
        WinPoolSourceObject? rawVirtual = null, rawPool = null;
        if (disk.VirtualDiskStableId is { } virtualId)
        {
            if (disk.PhysicalDiskStableId is not null)
                throw new InvalidDataException("The native MSR disk has conflicting physical and virtual associations.");
            RequireFactParent(topology, "same-device", disk.StableId, virtualId);
            var vd = topology.Snapshot.VirtualDisks.Single(item => item.StableId == virtualId);
            var pool = topology.Snapshot.StoragePools.Single(item => item.StableId == vd.PoolStableId);
            if (pool.IsPrimordial || pool.MemberPhysicalDiskIds.Count != 1
                || pool.MemberPhysicalDiskIds[0] != closure.PhysicalDiskId)
                throw new InvalidDataException("The native MSR virtual disk requires one exact non-primordial pool member.");
            RequireFactParent(topology, "pool-virtual-disk", vd.StableId, pool.StableId);
            var members = topology.Facts.Relationships.Where(item => !item.IsRetained
                && item.Kind == "pool-member" && item.FromId == pool.StableId).ToArray();
            if (members.Length != 1 || members[0].ToId != closure.PhysicalDiskId)
                throw new InvalidDataException("The native MSR virtual disk pool-member evidence is not exact.");
            rawVirtual = topology.RequireObject(new(topology.SystemId, StorageObjectKind.VirtualDisk, vd.StableId));
            rawPool = topology.RequireObject(new(topology.SystemId, StorageObjectKind.StoragePool, pool.StableId));
        }
        else
        {
            if (disk.PhysicalDiskStableId != closure.PhysicalDiskId)
                throw new InvalidDataException("The native MSR disk physical association is not exact.");
            RequireFactParent(topology, "same-device", disk.StableId, closure.PhysicalDiskId);
        }
        var rawDisk = topology.RequireObject(new(topology.SystemId, StorageObjectKind.OsDisk, disk.StableId));
        if (expectedDiskOffline && rawDisk.Field("IsOffline") is not
            { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.True } })
            throw new InvalidDataException("The exact native offline disk state is unavailable.");
        var rawPart = topology.RequireObject(new(topology.SystemId, StorageObjectKind.Partition, partition.StableId));
        var rawPhysical = topology.RequireObject(new(topology.SystemId, StorageObjectKind.PhysicalDisk, closure.PhysicalDiskId));
        var path = Text(rawDisk, "Path");
        var serial = Text(rawPhysical, "SerialNumber");
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal)
            && !path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new InvalidDataException("The exact provider disk path is not a Windows device path.");
        if ((rawVirtual is null && !Same(Text(rawDisk, "SerialNumber"), serial))
            || !Same(Text(rawPart, "DiskId"), path))
            throw new InvalidDataException("The selected MSR disk path or physical serial is inconsistent.");
        var type = ParseGuid(Text(rawPart, "GptType"));
        var guid = ParseGuid(Text(rawPart, "Guid"));
        if (!SupportsRole(type, expectedDiskOffline) || guid != ParseGuid(partition.Guid ?? "")
            || Number(rawPart, "Offset") != partition.Offset
            || Number(rawPart, "Size") != partition.Size
            || Number(rawPart, "PartitionNumber") != partition.PartitionNumber
            || partition.Offset < 0 || partition.Size <= 0 || partition.PartitionNumber <= 0)
            throw new InvalidDataException("The selected MSR identity or geometry is inconsistent.");
        var paths = rawPart.Field("AccessPaths");
        string[] accessPaths = [];
        if (expectedDiskOffline)
        {
            if (paths is not { ReadState: FieldReadState.Returned }
                || !(paths.Value is null || paths.Value.Value.ValueKind == JsonValueKind.Null
                    || paths.Value.Value.ValueKind == JsonValueKind.Array && paths.Value.Value.GetArrayLength() == 0))
                throw new InvalidDataException("The exact offline partition has unknown or assigned access paths.");
        }
        else if (type != MsrRole)
        {
            if (paths is not { ReadState: FieldReadState.Returned,
                Value: { ValueKind: JsonValueKind.Array } array }
                || array.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("The selected GPT partition access paths are unknown.");
            accessPaths = array.EnumerateArray().Select(item => item.GetString()!).ToArray();
        }
        return new(path, Text(rawDisk, "UniqueId"), Text(rawDisk, "ObjectId"),
            ParseGuid(Text(rawDisk, "Guid")), Text(rawPhysical, "UniqueId"),
            Text(rawPhysical, "ObjectId"), serial, guid, type,
            partition.Offset, partition.Size, partition.PartitionNumber,
            rawVirtual is null ? null : Text(rawVirtual, "UniqueId"),
            rawVirtual is null ? null : Text(rawVirtual, "ObjectId"),
            rawPool is null ? null : Text(rawPool, "UniqueId"),
            rawPool is null ? null : Text(rawPool, "ObjectId"))
        { AccessPaths = accessPaths, ExpectedDiskOffline = expectedDiskOffline };
    }

    private static void RequireFactParent(WindowsRealStorageTopology topology,
        string kind, string childId, string parentId)
    {
        var parents = topology.Facts.Relationships.Where(item => !item.IsRetained
            && item.Kind == kind && item.ToId == childId).ToArray();
        if (parents.Length != 1 || parents[0].FromId != parentId)
            throw new InvalidDataException("The native MSR provider association is not exact: " + kind);
    }

    internal static WindowsNativeMsrAttributes Read(WindowsNativeMsrIdentity expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireCurrentIdentity(expected, token);
        // DesiredAccess=0: metadata queries only; the handle cannot write the disk.
        using var handle = CreateFileW(expected.DiskPath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new InvalidDataException("Cannot open the exact MSR disk for read-only attributes.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        if (expected.VirtualDiskUniqueId is null) RequireNativeSerial(handle, expected.SerialNumber);
        var first = ParseLayout(Control(handle, GetLayout, null), expected);
        token.ThrowIfCancellationRequested();
        if (expected.VirtualDiskUniqueId is null) RequireNativeSerial(handle, expected.SerialNumber);
        var last = ParseLayout(Control(handle, GetLayout, null), expected);
        RequireCurrentIdentity(expected, token);
        if (first != last)
            throw new InvalidDataException("The selected MSR attributes changed during read-only verification.");
        return last;
    }

    internal static WindowsNativeMsrAttributes ParseLayout(byte[] bytes, WindowsNativeMsrIdentity expected)
    {
        const int header = 48, stride = 144;
        if (bytes.Length < header || U32(bytes, 0) != 1 || new Guid(bytes.AsSpan(8, 16)) != expected.DiskGuid)
            throw new InvalidDataException("The native handle does not contain the exact GPT disk layout.");
        var count = U32(bytes, 4);
        if (count == 0 || count > 4096 || bytes.Length < header + (long)count * stride)
            throw new InvalidDataException("The native GPT layout is empty, truncated, or exceeds the read bound.");
        var usableStart = I64(bytes, 24);
        var usableLength = I64(bytes, 32);
        if (usableStart < 0 || usableLength <= 0 || usableStart > long.MaxValue - usableLength)
            throw new InvalidDataException("The native GPT usable geometry is invalid.");
        var ids = new HashSet<Guid>();
        var numbers = new HashSet<uint>();
        var ranges = new List<(long Start, long End)>();
        WindowsNativeMsrAttributes? found = null;
        for (var index = 0; index < count; index++)
        {
            var p = header + index * stride;
            if (U32(bytes, p) != 1)
                throw new InvalidDataException("The native GPT layout contains a non-GPT entry.");
            var type = new Guid(bytes.AsSpan(p + 32, 16));
            if (type == Guid.Empty) continue; // SDK explicitly defines this as an unused entry.
            var guid = new Guid(bytes.AsSpan(p + 48, 16));
            var offset = I64(bytes, p + 8);
            var length = I64(bytes, p + 16);
            var number = U32(bytes, p + 24);
            if (guid == Guid.Empty || !ids.Add(guid) || number == 0 || !numbers.Add(number)
                || offset < usableStart || length <= 0 || offset > long.MaxValue - length
                || offset + length > usableStart + usableLength)
                throw new InvalidDataException("The native GPT entry identity or geometry is invalid or duplicated.");
            if (ranges.Any(range => offset < range.End && offset + length > range.Start))
                throw new InvalidDataException("The native GPT layout contains overlapping partitions.");
            ranges.Add((offset, offset + length));
            if (guid != expected.PartitionGuid) continue;
            if (type != expected.PartitionType || !SupportsRole(type, expected.ExpectedDiskOffline) || offset != expected.Offset
                || length != expected.Length || number != expected.PartitionNumber)
                throw new InvalidDataException("The native selected MSR differs from its fresh exact identity.");
            found = new(expected, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(p + 64, 8)));
        }
        return found ?? throw new InvalidDataException("The exact selected MSR is absent from the native GPT layout.");
    }

    private static void RequireCurrentIdentity(WindowsNativeMsrIdentity expected, CancellationToken token)
    {
        RequireUnique("MSFT_Disk", item => Same(item["Path"]?.ToString(), expected.DiskPath), item =>
        {
            var exact = Same(item["UniqueId"]?.ToString(), expected.DiskUniqueId)
            && Same(item["ObjectId"]?.ToString(), expected.DiskObjectId)
            && (expected.VirtualDiskUniqueId is not null || Same(item["SerialNumber"]?.ToString(), expected.SerialNumber))
            && item["IsReadOnly"] is false && item["IsOffline"] is bool offline && offline == expected.ExpectedDiskOffline
            && item["IsBoot"] is false && item["IsSystem"] is false && item["IsClustered"] is false
            && Guid.TryParse(item["Guid"]?.ToString(), out var guid) && guid == expected.DiskGuid;
            if (exact && expected.VirtualDiskUniqueId is not null)
                RequireVirtualBacking(item, expected, token);
            return exact;
        }, token);
        RequireUnique("MSFT_PhysicalDisk", item => Same(item["UniqueId"]?.ToString(), expected.PhysicalUniqueId), item =>
            Same(item["ObjectId"]?.ToString(), expected.PhysicalObjectId)
            && Same(item["SerialNumber"]?.ToString(), expected.SerialNumber), token);
        RequireUnique("MSFT_Partition", item => Same(item["DiskId"]?.ToString(), expected.DiskPath)
            && Guid.TryParse(item["Guid"]?.ToString(), out var guid) && guid == expected.PartitionGuid, item =>
            Guid.TryParse(item["GptType"]?.ToString(), out var type) && type == expected.PartitionType
            && item["Offset"] is not null && Convert.ToInt64(item["Offset"]) == expected.Offset
            && item["Size"] is not null && Convert.ToInt64(item["Size"]) == expected.Length
            && item["PartitionNumber"] is not null && Convert.ToInt32(item["PartitionNumber"]) == expected.PartitionNumber
            && item["IsBoot"] is false && item["IsSystem"] is false
            && (expected.ExpectedDiskOffline ? item["IsOffline"] is null or false or true : item["IsOffline"] is false)
            && (item["IsReadOnly"] is null or false) && (item["IsShadowCopy"] is null or false)
            && IsUnassignedDriveLetter(item["DriveLetter"])
            && (expected.ExpectedDiskOffline || expected.PartitionType == MsrRole
                ? item["AccessPaths"] is null || item["AccessPaths"] is string[] { Length: 0 }
                : item["AccessPaths"] is string[] paths
                    && paths.SequenceEqual(expected.AccessPaths, StringComparer.OrdinalIgnoreCase)), token);
    }

    internal static bool IsUnassignedDriveLetter(object? value) => value is null
        || value is char character && character == '\0'
        || value is string text && text.Length == 0;

    private static void RequireVirtualBacking(ManagementObject disk,
        WindowsNativeMsrIdentity expected, CancellationToken token)
    {
        // These are official Storage Management Provider association classes.
        // Each relationship is checked before and after reading the same native handle.
        RequireSingleRelated(disk, "MSFT_VirtualDisk", "MSFT_VirtualDiskToDisk", "VirtualDisk", "Disk", vd =>
        {
            if (!Same(vd["UniqueId"]?.ToString(), expected.VirtualDiskUniqueId!)
                || !Same(vd["ObjectId"]?.ToString(), expected.VirtualDiskObjectId!))
                throw new InvalidDataException("The exact native MSR virtual disk identity changed.");
            RequireSingleRelated(vd, "MSFT_StoragePool", "MSFT_StoragePoolToVirtualDisk", "StoragePool", "VirtualDisk", pool =>
            {
                if (pool["IsPrimordial"] is not false
                    || !Same(pool["UniqueId"]?.ToString(), expected.PoolUniqueId!)
                    || !Same(pool["ObjectId"]?.ToString(), expected.PoolObjectId!))
                    throw new InvalidDataException("The exact native MSR virtual disk pool changed.");
                RequireSingleRelated(pool, "MSFT_PhysicalDisk", "MSFT_StoragePoolToPhysicalDisk", "PhysicalDisk", "StoragePool", physical =>
                {
                    if (!Same(physical["UniqueId"]?.ToString(), expected.PhysicalUniqueId)
                        || !Same(physical["ObjectId"]?.ToString(), expected.PhysicalObjectId)
                        || !Same(physical["SerialNumber"]?.ToString(), expected.SerialNumber))
                        throw new InvalidDataException("The exact native MSR virtual disk single physical member changed.");
                }, token);
            }, token);
        }, token);
    }

    private static void RequireSingleRelated(ManagementObject source, string resultClass,
        string associationClass, string resultRole, string sourceRole,
        Action<ManagementObject> verify, CancellationToken token)
    {
        using var related = source.GetRelated(resultClass, associationClass, null, null,
            resultRole, sourceRole, false, new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) });
        var candidates = related.Cast<ManagementObject>().ToArray();
        try
        {
            token.ThrowIfCancellationRequested();
            if (candidates.Length != 1)
                throw new InvalidDataException("The native MSR provider relationship is unavailable or not unique: " + associationClass);
            verify(candidates[0]);
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
    }

    private static void RequireUnique(string className, Func<ManagementObject, bool> select,
        Func<ManagementObject, bool> verify, CancellationToken token)
    {
        using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM " + className);
        searcher.Options.Timeout = TimeSpan.FromSeconds(10);
        using var results = searcher.Get();
        var matches = 0;
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                token.ThrowIfCancellationRequested();
                if (!select(item)) continue;
                matches++;
                if (!verify(item)) throw new InvalidDataException("The current native MSR disk identity changed: " + className);
            }
        }
        if (matches != 1) throw new InvalidDataException("The current native MSR disk identity is not unique: " + className);
    }

    private static void RequireNativeSerial(SafeFileHandle handle, string expected)
    {
        var descriptor = Control(handle, QueryStorageProperty, new byte[12]);
        if (descriptor.Length < 36) throw new InvalidDataException("The native disk descriptor is incomplete.");
        var size = U32(descriptor, 4);
        var offset = U32(descriptor, 24);
        if (size > descriptor.Length || size < 36 || offset < 36 || offset >= size)
            throw new InvalidDataException("The native disk serial descriptor is invalid.");
        var end = Array.IndexOf(descriptor, (byte)0, (int)offset, (int)(size - offset));
        if (end < 0 || !Same(Encoding.ASCII.GetString(descriptor, (int)offset, end - (int)offset), expected))
            throw new InvalidDataException("The opened native disk does not have the exact physical serial.");
    }

    private static byte[] Control(SafeFileHandle handle, uint code, byte[]? input)
    {
        for (var size = 65536; size <= 1048576; size *= 2)
        {
            var output = new byte[size];
            if (DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output.Length, out var returned, IntPtr.Zero))
            {
                if (returned > output.Length) throw new InvalidDataException("The native read returned an invalid length.");
                return output.AsSpan(0, (int)returned).ToArray();
            }
            var error = Marshal.GetLastWin32Error();
            if (error is not (122 or 234))
                throw new InvalidDataException("The read-only native disk query failed.", new Win32Exception(error));
        }
        throw new InvalidDataException("The native disk query exceeds the read bound.");
    }

    private static string Text(WinPoolSourceObject item, string name) =>
        item.Field(name) is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.String } value }
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim()
            : throw new InvalidDataException("Native MSR identity field is unavailable: " + name);
    private static long Number(WinPoolSourceObject item, string name) =>
        item.Field(name) is { ReadState: FieldReadState.Returned, Value: { } value } && value.TryGetInt64(out var number)
            ? number : throw new InvalidDataException("Native MSR geometry field is unavailable: " + name);
    private static Guid ParseGuid(string value) => Guid.TryParse(value, out var guid) && guid != Guid.Empty
        ? guid : throw new InvalidDataException("Native MSR evidence requires a nonempty exact GUID.");
    private static bool Same(string? left, string right) => !string.IsNullOrWhiteSpace(left)
        && StringComparer.OrdinalIgnoreCase.Equals(left.Trim(), right.Trim());
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte[]? inputBuffer,
        int inputBufferSize, byte[] outputBuffer, int outputBufferSize, out uint bytesReturned, IntPtr overlapped);
}
