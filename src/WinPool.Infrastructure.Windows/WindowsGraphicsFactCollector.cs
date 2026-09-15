using System.Collections.Immutable;
using System.Runtime.InteropServices;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>Read-only DXGI/D3D12 supplement ported from the KS graphics collector.</summary>
internal static class WindowsGraphicsFactCollector
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int AdapterRegistryInfo = 8;
    private const int AdapterType = 15;
    private const int DriverDescription = 65;
    private const int DriverDescriptionRender = 66;
    private const uint IndirectDisplayDevice = 1u << 6;
    private const int MaxPath = 260;
    private const int DriverDescriptionLength = 4096;
    private static readonly Guid Factory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid Output6 = new("068346e8-9ecf-42c6-a15d-eb0c5d9a6b8f");
    private static readonly Guid D3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");

    public static WinPoolFacts AddTo(WinPoolFacts facts, DateTimeOffset capturedAt)
    {
        if (!OperatingSystem.IsWindows()) return facts;
        var sources = facts.Sources.ToBuilder();
        var objects = facts.Objects.ToBuilder();
        var relations = facts.Relationships.ToBuilder();
        var identities = facts.Identities.ToBuilder();
        var adapterSource = Source("WinPool.GraphicsAdapter", capturedAt);
        var outputSource = Source("WinPool.GraphicsOutput", capturedAt);
        sources.Add(adapterSource);
        sources.Add(outputSource);
        try
        {
            foreach (var adapter in ReadAdapters())
            {
                var adapterIdentity = $"{adapter.LuidHigh:x8}:{adapter.LuidLow:x8}";
                var adapterOpaque = WinPoolIdentityRegistry.OpaqueSourceIdentity(adapterSource.Namespace, adapterSource.ClassName, adapterIdentity);
                var adapterId = ScopedId(facts, FactObjectType.VideoController, adapterOpaque);
                objects.Add(new(adapterId, FactObjectType.VideoController, adapterSource.Id, adapterOpaque, true,
                [
                    Text("Name", adapter.Name, adapterSource.Id),
                    Number("VendorId", adapter.VendorId, adapterSource.Id),
                    Number("DeviceId", adapter.DeviceId, adapterSource.Id),
                    Number("DedicatedVideoMemory", adapter.DedicatedVideoMemory, adapterSource.Id, "bytes"),
                    Number("SharedSystemMemory", adapter.SharedSystemMemory, adapterSource.Id, "bytes"),
                    Number("TotalVideoMemory", checked(adapter.DedicatedVideoMemory + adapter.SharedSystemMemory), adapterSource.Id, "bytes"),
                    Text("DirectXFeatureLevel", adapter.DirectXFeatureLevel, adapterSource.Id),
                    Text("DxgiDescription", adapter.DxgiDescription, adapterSource.Id),
                    Text("DisplayDriverDescription", adapter.DisplayDriverDescription, adapterSource.Id),
                    Text("RenderDriverDescription", adapter.RenderDriverDescription, adapterSource.Id),
                    OptionalNumber("AdapterTypeFlags", adapter.AdapterTypeFlags, adapterSource.Id),
                    OptionalFlag("IndirectDisplayDevice", adapter.IsIndirectDisplayDevice, adapterSource.Id)
                ]));
                identities.Add(new(FactObjectType.VideoController, adapterOpaque, adapterId));
                foreach (var output in adapter.Outputs)
                {
                    var outputIdentity = adapterIdentity + ":" + output.DeviceName;
                    var outputOpaque = WinPoolIdentityRegistry.OpaqueSourceIdentity(outputSource.Namespace, outputSource.ClassName, outputIdentity);
                    var outputId = ScopedId(facts, FactObjectType.Monitor, outputOpaque);
                    objects.Add(new(outputId, FactObjectType.Monitor, outputSource.Id, outputOpaque, true,
                    [
                        Text("Name", output.DeviceName, outputSource.Id),
                        Text("ConnectedAdapter", adapter.Name, outputSource.Id),
                        Signed("DesktopX", output.X, outputSource.Id),
                        Signed("DesktopY", output.Y, outputSource.Id),
                        Number("HorizontalResolution", (ulong)Math.Max(0, output.Width), outputSource.Id),
                        Number("VerticalResolution", (ulong)Math.Max(0, output.Height), outputSource.Id),
                        Number("RefreshRate", output.RefreshRate, outputSource.Id, "hertz"),
                        Flag("Primary", output.X == 0 && output.Y == 0, outputSource.Id),
                        Number("BitsPerColor", output.BitsPerColor > 0 ? output.BitsPerColor : output.BitsPerPixel / 4, outputSource.Id, "bits"),
                        Text("ColorSpace", output.ColorSpace, outputSource.Id),
                        Text("DynamicRange", output.DynamicRange, outputSource.Id),
                        Text("MonitorDeviceId", MonitorDeviceId(output.DeviceName), outputSource.Id)
                    ]));
                    identities.Add(new(FactObjectType.Monitor, outputOpaque, outputId));
                    relations.Add(new(adapterId, outputId, "graphics-output", capturedAt));
                }
            }
        }
        catch (Exception exception) when (exception is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            sources[^2] = adapterSource with { ReadState = FieldReadState.Failed, ReasonCode = exception.GetType().Name };
            sources[^1] = outputSource with { ReadState = FieldReadState.Failed, ReasonCode = exception.GetType().Name };
        }
        var result = facts with { Sources = sources.ToImmutable(), Objects = objects.ToImmutable(),
            Relationships = relations.ToImmutable(), Identities = identities.DistinctBy(x => (x.ObjectType, x.SourceIdentity)).ToImmutableArray() };
        result.Validate();
        return result;
    }

    private static WinPoolSource Source(string name, DateTimeOffset capturedAt)
    {
        const string sourceNamespace = "winpool/native";
        var id = WinPoolIdentityRegistry.OpaqueSourceIdentity(sourceNamespace, name, capturedAt.ToString("O"));
        return new(id, FactOrigin.Native, sourceNamespace, name, capturedAt, CollectionPurpose.Hardware);
    }

    private static string ScopedId(WinPoolFacts facts, FactObjectType type, string identity) =>
        "object:" + WinPoolIdentityRegistry.OpaqueSourceIdentity(
            facts.SystemId.Value.ToString("N"), type.ToString(), identity);

    private static WinPoolSourceField Text(string name, string? value, string source) => string.IsNullOrWhiteSpace(value)
        ? WinPoolSourceField.Missing(name, FactValueType.String, source, FieldReadState.Unavailable, "NativeValueUnavailable")
        : WinPoolSourceField.Returned(name, value, FactValueType.String, source);
    private static WinPoolSourceField Number(string name, ulong value, string source, string? unit = null) =>
        WinPoolSourceField.Returned(name, value, FactValueType.UInt64, source, unit);
    private static WinPoolSourceField OptionalNumber(string name, uint? value, string source) => value is { } number
        ? Number(name, number, source)
        : WinPoolSourceField.Missing(name, FactValueType.UInt64, source, FieldReadState.Unavailable, "NativeValueUnavailable");
    private static WinPoolSourceField Signed(string name, long value, string source) =>
        WinPoolSourceField.Returned(name, value, FactValueType.Int64, source);
    private static WinPoolSourceField Flag(string name, bool value, string source) =>
        WinPoolSourceField.Returned(name, value, FactValueType.Boolean, source);
    private static WinPoolSourceField OptionalFlag(string name, bool? value, string source) => value is { } flag
        ? Flag(name, flag, source)
        : WinPoolSourceField.Missing(name, FactValueType.Boolean, source, FieldReadState.Unavailable, "NativeValueUnavailable");

    private static IReadOnlyList<AdapterInfo> ReadAdapters()
    {
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in Factory1, out var factory));
        var result = new List<AdapterInfo>();
        try
        {
            var enumerate = Method<EnumAdapters1>(factory, 12);
            for (uint index = 0; ; index++)
            {
                var hr = enumerate(factory, index, out var adapter);
                if (hr == DxgiErrorNotFound) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    var desc = new AdapterDescription1();
                    Marshal.ThrowExceptionForHR(Method<GetAdapterDescription1>(adapter, 10)(adapter, ref desc));
                    var outputs = ReadOutputs(adapter);
                    var dxgiDescription = desc.Description.TrimEnd('\0');
                    var kernel = ReadKernelAdapterInfo(desc.AdapterLuid);
                    var name = kernel.IsIndirectDisplayDevice == true && !string.IsNullOrWhiteSpace(kernel.DisplayDriverDescription)
                        ? kernel.DisplayDriverDescription : dxgiDescription;
                    result.Add(new(name, dxgiDescription, desc.VendorId, desc.DeviceId,
                        (ulong)desc.DedicatedVideoMemory, (ulong)desc.SharedSystemMemory,
                        desc.AdapterLuid.HighPart, desc.AdapterLuid.LowPart, FeatureLevel(adapter), outputs,
                        kernel.AdapterTypeFlags, kernel.IsIndirectDisplayDevice,
                        kernel.DisplayDriverDescription, kernel.RenderDriverDescription));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }

    private static KernelAdapterInfo ReadKernelAdapterInfo(Luid luid)
    {
        var open = new OpenAdapterFromLuid { AdapterLuid = luid };
        if (D3DKMTOpenAdapterFromLuid(ref open) != 0 || open.Adapter == 0)
            return new(null, null, string.Empty, string.Empty);
        try
        {
            var flags = QueryUInt32(open.Adapter, AdapterType);
            var display = QueryText(open.Adapter, DriverDescription, DriverDescriptionLength * sizeof(char));
            if (string.IsNullOrWhiteSpace(display))
                display = QueryText(open.Adapter, AdapterRegistryInfo, 4 * MaxPath * sizeof(char));
            var render = QueryText(open.Adapter, DriverDescriptionRender, DriverDescriptionLength * sizeof(char));
            return new(flags, flags is { } value ? (value & IndirectDisplayDevice) != 0 : null, display, render);
        }
        finally
        {
            var close = new CloseAdapter(open.Adapter);
            _ = D3DKMTCloseAdapter(in close);
        }
    }

    private static uint? QueryUInt32(uint adapter, int type)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(buffer, 0);
            var query = new QueryAdapterInfo(adapter, type, buffer, sizeof(uint));
            return D3DKMTQueryAdapterInfo(in query) == 0 ? unchecked((uint)Marshal.ReadInt32(buffer)) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string QueryText(uint adapter, int type, int byteCount)
    {
        var buffer = Marshal.AllocHGlobal(byteCount);
        try
        {
            for (var offset = 0; offset < byteCount; offset += sizeof(long)) Marshal.WriteInt64(buffer, offset, 0);
            var query = new QueryAdapterInfo(adapter, type, buffer, (uint)byteCount);
            return D3DKMTQueryAdapterInfo(in query) == 0
                ? Marshal.PtrToStringUni(buffer)?.TrimEnd('\0') ?? string.Empty : string.Empty;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IReadOnlyList<OutputInfo> ReadOutputs(IntPtr adapter)
    {
        var result = new List<OutputInfo>();
        var enumerate = Method<EnumOutputs>(adapter, 7);
        for (uint index = 0; ; index++)
        {
            var hr = enumerate(adapter, index, out var output);
            if (hr == DxgiErrorNotFound) break;
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var desc = new OutputDescription();
                Marshal.ThrowExceptionForHR(Method<GetOutputDescription>(output, 7)(output, ref desc));
                uint bits = 0;
                var colorSpace = string.Empty;
                var range = string.Empty;
                if (Marshal.QueryInterface(output, in Output6, out var output6) == 0)
                {
                    try
                    {
                        var desc1 = new OutputDescription1();
                        if (Method<GetOutputDescription1>(output6, 27)(output6, ref desc1) == 0)
                        {
                            bits = desc1.BitsPerColor;
                            colorSpace = ColorSpaceName(desc1.ColorSpace);
                            range = desc1.ColorSpace is 12 or 13 or 14 or 15 ? "HDR" : "SDR";
                        }
                    }
                    finally { Marshal.Release(output6); }
                }
                var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
                _ = EnumDisplaySettings(desc.DeviceName.TrimEnd('\0'), -1, ref mode);
                result.Add(new(desc.DeviceName.TrimEnd('\0'), desc.DesktopCoordinates.Left, desc.DesktopCoordinates.Top,
                    desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                    desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top,
                    mode.DisplayFrequency > 1 ? (uint)mode.DisplayFrequency : 0,
                    mode.BitsPerPel > 0 ? (uint)mode.BitsPerPel : 0, bits, colorSpace, range));
            }
            finally { Marshal.Release(output); }
        }
        return result;
    }

    private static string FeatureLevel(IntPtr adapter)
    {
        foreach (var feature in new uint[] { 0xc200, 0xc100, 0xc000, 0xb100, 0xb000 })
        {
            if (D3D12CreateDevice(adapter, feature, in D3D12Device, out var device) < 0) continue;
            if (device != IntPtr.Zero) Marshal.Release(device);
            return $"{feature >> 12}.{(feature >> 8) & 0xf} (FL {feature >> 12}.{(feature >> 8) & 0xf})";
        }
        return "Unsupported";
    }

    private static string MonitorDeviceId(string displayName)
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(displayName, 0, ref device, 0) ? device.DeviceId.TrimEnd('\0') : string.Empty;
    }

    private static string ColorSpaceName(int value) => value switch
    {
        0 => "RGB Full G22 P709", 12 => "RGB Full G2084 P2020", 13 => "YCbCr Studio G2084 P2020",
        14 => "RGB Full G10 P709", 15 => "RGB Full G22 P2020", _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private static T Method<T>(IntPtr instance, int index) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, index * IntPtr.Size));
    }

    [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);
    [DllImport("d3d12.dll", ExactSpelling = true)] private static extern int D3D12CreateDevice(IntPtr adapter, uint minimumFeatureLevel, in Guid riid, out IntPtr device);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid open);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTQueryAdapterInfo(in QueryAdapterInfo query);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTCloseAdapter(in CloseAdapter close);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice displayDevice, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string device, int modeNumber, ref DevMode mode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapterDescription1(IntPtr self, ref AdapterDescription1 desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumOutputs(IntPtr self, uint index, out IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputDescription(IntPtr self, ref OutputDescription desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputDescription1(IntPtr self, ref OutputDescription1 desc);

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct OpenAdapterFromLuid { public Luid AdapterLuid; public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct CloseAdapter
    {
        public readonly uint Adapter;
        public CloseAdapter(uint adapter) => Adapter = adapter;
    }
    [StructLayout(LayoutKind.Sequential)] private readonly struct QueryAdapterInfo
    {
        public readonly uint Adapter;
        public readonly int Type;
        public readonly IntPtr PrivateDriverData;
        public readonly uint PrivateDriverDataSize;
        public QueryAdapterInfo(uint adapter, int type, IntPtr data, uint size) =>
            (Adapter, Type, PrivateDriverData, PrivateDriverDataSize) = (adapter, type, data, size);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct AdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OutputDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public Rect DesktopCoordinates;
        [MarshalAs(UnmanagedType.Bool)] public bool AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OutputDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public Rect DesktopCoordinates;
        [MarshalAs(UnmanagedType.Bool)] public bool AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
        public uint BitsPerColor;
        public int ColorSpace;
        public float RedX, RedY, GreenX, GreenY, BlueX, BlueY, WhiteX, WhiteY;
        public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    private sealed record AdapterInfo(string Name, string DxgiDescription, uint VendorId, uint DeviceId,
        ulong DedicatedVideoMemory, ulong SharedSystemMemory, int LuidHigh, uint LuidLow, string DirectXFeatureLevel,
        IReadOnlyList<OutputInfo> Outputs, uint? AdapterTypeFlags, bool? IsIndirectDisplayDevice,
        string DisplayDriverDescription, string RenderDriverDescription);
    private sealed record KernelAdapterInfo(uint? AdapterTypeFlags, bool? IsIndirectDisplayDevice,
        string DisplayDriverDescription, string RenderDriverDescription);
    private sealed record OutputInfo(string DeviceName, int X, int Y, int Width, int Height,
        uint RefreshRate, uint BitsPerPixel, uint BitsPerColor, string ColorSpace, string DynamicRange);
}
