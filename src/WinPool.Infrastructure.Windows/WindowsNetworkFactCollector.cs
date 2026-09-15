using System.Collections.Immutable;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

internal static class WindowsNetworkFactCollector
{
    private const string NetworkNamespace = @"\\.\ROOT\StandardCimv2";

    public static WinPoolFacts AddTo(WinPoolFacts facts, DateTimeOffset capturedAt)
    {
        var source = Source("root/standardcimv2", "MSFT_NetAdapter", FactOrigin.StorageCim, capturedAt);
        var platformSource = Source("WinPool.Platform", capturedAt);
        var replacedSourceIds = facts.Sources.Where(x => x.Namespace.Equals(source.Namespace, StringComparison.OrdinalIgnoreCase)
            && x.ClassName == source.ClassName).Select(x => x.Id).ToHashSet();
        var replacedObjectIds = facts.Objects.Where(x => replacedSourceIds.Contains(x.SourceRef)).Select(x => x.Id).ToHashSet();
        var sources = facts.Sources.Where(x => !replacedSourceIds.Contains(x.Id)).ToImmutableArray().Add(source).Add(platformSource);
        var objects = facts.Objects.Where(x => !replacedObjectIds.Contains(x.Id)).ToImmutableArray().ToBuilder();
        var identities = facts.Identities.Where(x => !replacedObjectIds.Contains(x.ObjectId)).ToImmutableArray().ToBuilder();
        var relationships = facts.Relationships.Where(x => !replacedObjectIds.Contains(x.FromId) && !replacedObjectIds.Contains(x.ToId))
            .ToImmutableArray();
        var networkObjects = ImmutableArray.CreateBuilder<WinPoolSourceObject>();
        var networkIdentities = ImmutableArray.CreateBuilder<WinPoolIdentityBinding>();
        try
        {
            var scope = new ManagementScope(NetworkNamespace);
            scope.Connect();
            var addresses = ReadAddresses(scope);
            var defaultRoutes = ReadDefaultRouteInterfaces(scope);
            foreach (var adapter in ReadAdapters(scope))
            {
                if (!adapter.ConnectorPresent && adapter.InterfaceType == 0) continue;
                var reliableIdentity = adapter.InterfaceGuid.Length > 0
                    ? adapter.InterfaceGuid
                    : adapter.InterfaceIndex is { } index ? $"interface-index:{index}" : string.Empty;
                var reliable = reliableIdentity.Length > 0;
                var opaque = reliable
                    ? WinPoolIdentityRegistry.OpaqueSourceIdentity(source.Namespace, source.ClassName, reliableIdentity)
                    : string.Empty;
                var id = reliable ? ScopedId(facts, FactObjectType.NetworkAdapter, opaque) : "temporary:" + Guid.NewGuid().ToString("N");
                var adapterAddresses = adapter.InterfaceIndex is { } interfaceIndex
                    && addresses.TryGetValue(interfaceIndex, out var found) ? found : [];
                networkObjects.Add(new(id, FactObjectType.NetworkAdapter, source.Id, opaque, reliable,
                [
                    Text("Name", adapter.Name, source.Id),
                    Text("DriverDescription", adapter.DriverDescription, source.Id),
                    OptionalNumber("InterfaceIndex", adapter.InterfaceIndex, source.Id),
                    OptionalNumber("LinkSpeed", adapter.Speed, source.Id, "bits/second"),
                    TextArray("IPv4Addresses", adapterAddresses.Where(x => x.AddressFamily == 2).Select(x => x.Address), source.Id),
                    TextArray("IPv6Addresses", adapterAddresses.Where(x => x.AddressFamily == 23).Select(x => x.Address), source.Id),
                    Text("PermanentAddress", adapter.PermanentAddress, source.Id),
                    Flag("Primary", adapter.InterfaceIndex is { } routeIndex && defaultRoutes.Contains(routeIndex), source.Id),
                    OptionalNumber("InterfaceOperationalStatus", adapter.InterfaceOperationalStatus, source.Id),
                    Flag("ConnectorPresent", adapter.ConnectorPresent, source.Id),
                    OptionalNumber("InterfaceType", adapter.InterfaceType, source.Id),
                    Flag("HardwareInterface", adapter.HardwareInterface, source.Id),
                    Flag("Virtual", adapter.Virtual, source.Id)
                ]));
                if (reliable) networkIdentities.Add(new(FactObjectType.NetworkAdapter, opaque, id));
            }

            objects.AddRange(networkObjects);
            identities.AddRange(networkIdentities);
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or COMException)
        {
            sources = sources.SetItem(sources.Length - 2, source with { ReadState = FieldReadState.Failed, ReasonCode = exception.GetType().Name });
        }

        var platformIdentity = WinPoolIdentityRegistry.OpaqueSourceIdentity(platformSource.Namespace, platformSource.ClassName, "machine");
        var platformId = ScopedId(facts, FactObjectType.HardwareSupplement, platformIdentity);
        objects.Add(new(platformId, FactObjectType.HardwareSupplement, platformSource.Id, platformIdentity, true,
        [
            Text("BiosMode", ReadBiosMode(), platformSource.Id)
        ]));
        identities.Add(new(FactObjectType.HardwareSupplement, platformIdentity, platformId));
        var result = facts with { Sources = sources, Objects = objects.ToImmutable(), Relationships = relationships,
            Identities = identities.DistinctBy(x => (x.ObjectType, x.SourceIdentity)).ToImmutableArray() };
        result.Validate();
        return result;
    }

    private static IReadOnlyList<NetworkAdapterInfo> ReadAdapters(ManagementScope scope)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
            "SELECT InterfaceGuid, InterfaceIndex, Name, DriverDescription, Speed, PermanentAddress, " +
            "ConnectorPresent, InterfaceType, InterfaceOperationalStatus, HardwareInterface, Virtual FROM MSFT_NetAdapter"));
        using var results = searcher.Get();
        var adapters = new List<NetworkAdapterInfo>();
        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                adapters.Add(new(
                    TextValue(item, "InterfaceGuid"), UInt32Value(item, "InterfaceIndex"), TextValue(item, "Name"),
                    TextValue(item, "DriverDescription"), UInt64Value(item, "Speed"), TextValue(item, "PermanentAddress"),
                    BooleanValue(item, "ConnectorPresent"), UInt32Value(item, "InterfaceType") ?? 0,
                    UInt32Value(item, "InterfaceOperationalStatus"), BooleanValue(item, "HardwareInterface"),
                    BooleanValue(item, "Virtual")));
            }
        }
        return adapters.OrderBy(x => x.InterfaceIndex).ToArray();
    }

    private static IReadOnlyDictionary<uint, IReadOnlyList<NetworkAddressInfo>> ReadAddresses(ManagementScope scope)
    {
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT InterfaceIndex, AddressFamily, IPAddress FROM MSFT_NetIPAddress"));
        using var results = searcher.Get();
        var addresses = new List<(uint InterfaceIndex, NetworkAddressInfo Address)>();
        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                if (UInt32Value(item, "InterfaceIndex") is not { } index || UInt32Value(item, "AddressFamily") is not { } family
                    || TextValue(item, "IPAddress") is not { Length: > 0 } address) continue;
                addresses.Add((index, new(family, address)));
            }
        }
        return addresses.GroupBy(x => x.InterfaceIndex)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<NetworkAddressInfo>)x.Select(y => y.Address).ToArray());
    }

    private static IReadOnlySet<uint> ReadDefaultRouteInterfaces(ManagementScope scope)
    {
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT InterfaceIndex, DestinationPrefix FROM MSFT_NetRoute"));
        using var results = searcher.Get();
        var interfaces = new HashSet<uint>();
        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                var prefix = TextValue(item, "DestinationPrefix");
                if (prefix is not ("0.0.0.0/0" or "::/0") || UInt32Value(item, "InterfaceIndex") is not { } index) continue;
                interfaces.Add(index);
            }
        }
        return interfaces;
    }

    private static string TextValue(ManagementBaseObject item, string property) =>
        Convert.ToString(item.Properties[property]?.Value, CultureInfo.InvariantCulture) ?? string.Empty;
    private static uint? UInt32Value(ManagementBaseObject item, string property) =>
        item.Properties[property]?.Value is { } value ? Convert.ToUInt32(value, CultureInfo.InvariantCulture) : null;
    private static ulong? UInt64Value(ManagementBaseObject item, string property) =>
        item.Properties[property]?.Value is { } value ? Convert.ToUInt64(value, CultureInfo.InvariantCulture) : null;
    private static bool BooleanValue(ManagementBaseObject item, string property) =>
        item.Properties[property]?.Value is { } value && Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private static WinPoolSource Source(string sourceNamespace, string name, FactOrigin origin, DateTimeOffset capturedAt)
    {
        var id = WinPoolIdentityRegistry.OpaqueSourceIdentity(sourceNamespace, name, capturedAt.ToString("O"));
        return new(id, origin, sourceNamespace, name, capturedAt, CollectionPurpose.Hardware);
    }
    private static WinPoolSource Source(string name, DateTimeOffset capturedAt)
    {
        const string ns = "winpool/native";
        return Source(ns, name, FactOrigin.Native, capturedAt);
    }
    private static string ScopedId(WinPoolFacts facts, FactObjectType type, string identity) =>
        "object:" + WinPoolIdentityRegistry.OpaqueSourceIdentity(facts.SystemId.Value.ToString("N"), type.ToString(), identity);
    private static WinPoolSourceField Text(string name, string? value, string source) => string.IsNullOrWhiteSpace(value)
        ? WinPoolSourceField.Missing(name, FactValueType.String, source, FieldReadState.Unavailable, "NativeValueUnavailable")
        : WinPoolSourceField.Returned(name, value, FactValueType.String, source);
    private static WinPoolSourceField OptionalNumber(string name, ulong? value, string source, string? unit = null) => value is { } number
        ? WinPoolSourceField.Returned(name, number, FactValueType.UInt64, source, unit)
        : WinPoolSourceField.Missing(name, FactValueType.UInt64, source, FieldReadState.Unavailable, "CimValueUnavailable", unit);
    private static WinPoolSourceField OptionalNumber(string name, uint? value, string source, string? unit = null) =>
        OptionalNumber(name, value is { } number ? (ulong?)number : null, source, unit);
    private static WinPoolSourceField Flag(string name, bool value, string source) =>
        WinPoolSourceField.Returned(name, value, FactValueType.Boolean, source);
    private static WinPoolSourceField TextArray(string name, IEnumerable<string> values, string source) =>
        WinPoolSourceField.Returned(name, values.ToArray(), FactValueType.StringArray, source);
    private static string? ReadBiosMode()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return GetFirmwareType(out var type) ? type switch
        {
            1 => "Legacy",
            2 => "UEFI",
            _ => null
        } : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint firmwareType);

    private sealed record NetworkAdapterInfo(string InterfaceGuid, uint? InterfaceIndex, string Name,
        string DriverDescription, ulong? Speed, string PermanentAddress, bool ConnectorPresent, uint InterfaceType,
        uint? InterfaceOperationalStatus, bool HardwareInterface, bool Virtual);
    private sealed record NetworkAddressInfo(uint AddressFamily, string Address);
}
