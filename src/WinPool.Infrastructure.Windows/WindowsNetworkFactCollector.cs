using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Runtime.InteropServices;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

internal static class WindowsNetworkFactCollector
{
    public static WinPoolFacts AddTo(WinPoolFacts facts, DateTimeOffset capturedAt, JsonElement captured = default)
    {
        var source = Source("winpool/native", "WinPool.NetworkAdapter", FactOrigin.Native, capturedAt);
        var platformSource = Source("WinPool.Platform", capturedAt);
        var replacedSourceIds = facts.Sources.Where(x =>
            x.Namespace.Equals("root/standardcimv2", StringComparison.OrdinalIgnoreCase) && x.ClassName == "MSFT_NetAdapter"
            || x.Namespace.Equals(source.Namespace, StringComparison.OrdinalIgnoreCase) && x.ClassName == source.ClassName)
            .Select(x => x.Id).ToHashSet();
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
            if (captured.ValueKind != JsonValueKind.Object || captured.TryGetProperty("Error", out _))
                throw new InvalidDataException("The bounded inventory did not return network observations.");
            var addresses = captured.GetProperty("Addresses").EnumerateArray().Where(item => UInt32Value(item,"InterfaceIndex") is not null
                && UInt32Value(item,"AddressFamily") is not null && TextValue(item,"IPAddress").Length > 0)
                .GroupBy(item => UInt32Value(item,"InterfaceIndex")!.Value)
                .ToDictionary(group => group.Key, group => group.Select(item => new NetworkAddressInfo(UInt32Value(item,"AddressFamily")!.Value, TextValue(item,"IPAddress"))).ToArray());
            var defaultRoutes = captured.GetProperty("Routes").EnumerateArray().Select(item => UInt32Value(item,"InterfaceIndex"))
                .OfType<uint>().ToHashSet();
            foreach (var adapter in captured.GetProperty("Adapters").EnumerateArray().Select(item => new NetworkAdapterInfo(
                TextValue(item,"InterfaceGuid"), UInt32Value(item,"InterfaceIndex"), TextValue(item,"Name"), TextValue(item,"DriverDescription"),
                UInt64Value(item,"Speed"), TextValue(item,"PermanentAddress"), BooleanValue(item,"ConnectorPresent"), UInt32Value(item,"InterfaceType") ?? 0,
                UInt32Value(item,"InterfaceOperationalStatus"), BooleanValue(item,"HardwareInterface"), BooleanValue(item,"Virtual"))).OrderBy(item => item.InterfaceIndex))
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
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or JsonException or KeyNotFoundException)
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

    private static string TextValue(JsonElement item, string property) => item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static uint? UInt32Value(JsonElement item, string property) => item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number) ? number : null;
    private static ulong? UInt64Value(JsonElement item, string property) => item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number : null;
    private static bool BooleanValue(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
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
