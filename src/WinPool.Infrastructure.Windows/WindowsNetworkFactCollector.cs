using System.Collections.Immutable;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

internal static class WindowsNetworkFactCollector
{
    public static WinPoolFacts AddTo(WinPoolFacts facts, DateTimeOffset capturedAt)
    {
        var source = Source("WinPool.NetworkAdapter", capturedAt);
        var platformSource = Source("WinPool.Platform", capturedAt);
        var sources = facts.Sources.Add(source).Add(platformSource);
        var objects = facts.Objects.ToBuilder();
        var identities = facts.Identities.ToBuilder();
        var networkObjects = ImmutableArray.CreateBuilder<WinPoolSourceObject>();
        var networkIdentities = ImmutableArray.CreateBuilder<WinPoolIdentityBinding>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                var identity = adapter.Id;
                if (string.IsNullOrWhiteSpace(identity)) continue;
                var opaque = WinPoolIdentityRegistry.OpaqueSourceIdentity(source.Namespace, source.ClassName, identity);
                var id = ScopedId(facts, FactObjectType.NetworkAdapter, opaque);
                var properties = adapter.GetIPProperties();
                var addresses = properties.UnicastAddresses.Select(x => x.Address).ToArray();
                networkObjects.Add(new(id, FactObjectType.NetworkAdapter, source.Id, opaque, true,
                [
                    Text("Name", adapter.Name, source.Id), Text("InterfaceDescription", adapter.Description, source.Id),
                    Speed(adapter.Speed, source.Id),
                    TextArray("IPv4Addresses", addresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork).Select(x => x.ToString()), source.Id),
                    TextArray("IPv6Addresses", addresses.Where(x => x.AddressFamily == AddressFamily.InterNetworkV6).Select(x => x.ToString()), source.Id),
                    Text("MacAddress", adapter.GetPhysicalAddress().ToString(), source.Id),
                    Flag("Primary", properties.GatewayAddresses.Any(x => !x.Address.Equals(System.Net.IPAddress.Any)
                        && !x.Address.Equals(System.Net.IPAddress.IPv6Any)), source.Id),
                    Text("OperationalStatus", adapter.OperationalStatus.ToString(), source.Id)
                ]));
                networkIdentities.Add(new(FactObjectType.NetworkAdapter, opaque, id));
            }

            objects.AddRange(networkObjects);
            identities.AddRange(networkIdentities);
        }
        catch (NetworkInformationException exception)
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
        var result = facts with { Sources = sources, Objects = objects.ToImmutable(),
            Identities = identities.DistinctBy(x => (x.ObjectType, x.SourceIdentity)).ToImmutableArray() };
        result.Validate();
        return result;
    }

    private static WinPoolSource Source(string name, DateTimeOffset capturedAt)
    {
        const string ns = "winpool/native";
        return new(WinPoolIdentityRegistry.OpaqueSourceIdentity(ns, name, capturedAt.ToString("O")),
            FactOrigin.Native, ns, name, capturedAt, CollectionPurpose.Hardware);
    }
    private static string ScopedId(WinPoolFacts facts, FactObjectType type, string identity) =>
        "object:" + WinPoolIdentityRegistry.OpaqueSourceIdentity(facts.SystemId.Value.ToString("N"), type.ToString(), identity);
    private static WinPoolSourceField Text(string name, string? value, string source) => string.IsNullOrWhiteSpace(value)
        ? WinPoolSourceField.Missing(name, FactValueType.String, source, FieldReadState.Unavailable, "NativeValueUnavailable")
        : WinPoolSourceField.Returned(name, value, FactValueType.String, source);
    private static WinPoolSourceField Signed(string name, long value, string source, string unit) =>
        WinPoolSourceField.Returned(name, value, FactValueType.Int64, source, unit);
    private static WinPoolSourceField Speed(long value, string source) => value < 0
        ? WinPoolSourceField.Missing("LinkSpeed", FactValueType.Int64, source, FieldReadState.Unavailable,
            "NativeValueUnavailable", "bits/second")
        : Signed("LinkSpeed", value, source, "bits/second");
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
}
