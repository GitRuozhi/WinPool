using System.Collections.Immutable;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class HardwareReportProjectorTests
{
    [Fact]
    public void ReportKeepsRequiredCategoriesAndMemorySummarySeparateFromModules()
    {
        var time = DateTimeOffset.UtcNow;
        var systemId = SystemId.New();
        var sources = new[]
        {
            Source("computer", "Win32_ComputerSystem", time), Source("array", "Win32_PhysicalMemoryArray", time),
            Source("module", "Win32_PhysicalMemory", time), Source("gpu", "WinPool.GraphicsAdapter", time),
            Source("output", "WinPool.GraphicsOutput", time), Source("network", "MSFT_NetAdapter", time),
            Source("software-gpu", "Win32_VideoController", time), Source("wmi-monitor", "WmiMonitorID", time),
            Source("other-network", "Win32_NetworkAdapter", time)
        };
        var objects = new[]
        {
            Object("computer-object", FactObjectType.Computer, sources[0], ("Name", "TEST"), ("Manufacturer", "Vendor")),
            Object("array-object", FactObjectType.MemoryArray, sources[1], ("MemoryDevices", 4L), ("MemoryErrorCorrection", 3L)),
            Object("module-1", FactObjectType.MemoryModule, sources[2], ("Capacity", 16UL * 1024 * 1024 * 1024), ("PartNumber", "A")),
            Object("module-2", FactObjectType.MemoryModule, sources[2], ("Capacity", 16UL * 1024 * 1024 * 1024), ("PartNumber", "B")),
            Object("gpu-object", FactObjectType.VideoController, sources[3], ("Name", "Software GPU"), ("SharedSystemMemory", 8UL * 1024 * 1024 * 1024)),
            Object("output-object", FactObjectType.Monitor, sources[4], ("Name", "DISPLAY1"), ("DesktopX", -100L), ("Primary", false)),
            Object("network-object", FactObjectType.NetworkAdapter, sources[5], ("Name", "Ethernet"), ("Primary", true)),
            Object("wmi-gpu-supplement", FactObjectType.HardwareSupplement, sources[6], ("Name", "Software GPU")),
            Object("wmi-monitor-supplement", FactObjectType.HardwareSupplement, sources[7], ("Name", "MONITOR\\SECOND")),
            Object("other-network-object", FactObjectType.NetworkAdapter, sources[8], ("Name", "Other network"), ("Primary", false))
        };
        var facts = new WinPoolFacts(1, systemId, 1, [.. sources], [.. objects], [], [],
            [new(CollectionPurpose.Hardware, time, time, FieldReadState.Returned)]) { InventoryCapturedAt = time };
        var document = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "local:test", StorageSystemKind.Local,
            "TEST", facts, [], time) { SystemId = systemId };

        var report = HardwareReportProjector.Project(document, chinese: true);

        Assert.Equal(new[] { "Computer", "System", "Mainboard", "CPU", "Memory", "VirtualMemory", "Storage", "GPU", "Monitor", "Network" },
            report.Select(x => x.Name));
        var memory = report.Single(x => x.Name == "Memory");
        Assert.Equal(2, memory.Sections.Count);
        Assert.Single(memory.Sections[0].Rows[0].Cells);
        Assert.Equal("4", memory.Sections[0].Rows[0].Cells[0].Value);
        Assert.Equal(2, memory.Sections[1].Rows.Single(x => x.Label == "型号").Cells.Count);
        Assert.Equal("-100", report.Single(x => x.Name == "Monitor").Sections[0].Rows.Single(x => x.Label == "水平坐标").Cells[0].Value);
        Assert.Single(report.Single(x => x.Name == "GPU").Sections[0].Rows.Single(x => x.Label == "型号").Cells);
        Assert.Single(report.Single(x => x.Name == "Monitor").Sections[0].Rows.Single(x => x.Label == "型号").Cells);
        Assert.Equal(2, report.Single(x => x.Name == "Network").Sections[0].Rows.Single(x => x.Label == "名称").Cells.Count);
    }

    [Fact]
    public void StorageSummaryDistinguishesUnknownFromSuccessfulZero()
    {
        var time = DateTimeOffset.UtcNow;
        var systemId = SystemId.New();
        var failed = new WinPoolFacts(1, systemId, 1,
            [new("failed", FactOrigin.StorageCim, "root/storage", "MSFT_PhysicalDisk", time,
                CollectionPurpose.Storage, FieldReadState.Failed, "QueryFailed")], [], [], [], []);
        var unknownDocument = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion, "local:test", StorageSystemKind.Local,
            "TEST", failed, [], time) { SystemId = systemId };
        var unknown = ManageSystemSummaryProjector.Project(unknownDocument).Single(x => x.PropertyTextKey == "PhysicalDisk");
        Assert.Equal("Unknown", unknown.RawValue);
        Assert.Equal(ManageValuePresentation.LocalizationKey, unknown.Presentation);

        var returned = failed with { Sources = [failed.Sources[0] with { ReadState = FieldReadState.Returned, ReasonCode = null }] };
        var zeroDocument = unknownDocument with { SourceFacts = returned };
        var zero = ManageSystemSummaryProjector.Project(zeroDocument).Single(x => x.PropertyTextKey == "PhysicalDisk");
        Assert.Equal("0", zero.RawValue);
        Assert.Equal(ManageValuePresentation.Plain, zero.Presentation);

        var staleAfterFailure = returned with
        {
            Sources = [returned.Sources[0] with { Id = "old", CapturedAt = time.AddMinutes(-1) },
                returned.Sources[0] with { Id = "new", CapturedAt = time, ReadState = FieldReadState.Failed, ReasonCode = "QueryFailed" }]
        };
        var staleDocument = unknownDocument with { SourceFacts = staleAfterFailure };
        var stale = ManageSystemSummaryProjector.Project(staleDocument).Single(x => x.PropertyTextKey == "PhysicalDisk");
        Assert.Equal("Unknown", stale.RawValue);
        Assert.Equal(ManageValuePresentation.LocalizationKey, stale.Presentation);
    }

    private static WinPoolSource Source(string id, string className, DateTimeOffset time) =>
        new(id, FactOrigin.Native, "winpool/native", className, time, CollectionPurpose.Hardware);

    private static WinPoolSourceObject Object(string id, FactObjectType type, WinPoolSource source,
        params (string Name, object Value)[] values) => new(id, type, source.Id, id, true,
        values.Select(value => value.Value switch
        {
            string text => WinPoolSourceField.Returned(value.Name, text, FactValueType.String, source.Id),
            bool flag => WinPoolSourceField.Returned(value.Name, flag, FactValueType.Boolean, source.Id),
            long number => WinPoolSourceField.Returned(value.Name, number, FactValueType.Int64, source.Id),
            ulong number => WinPoolSourceField.Returned(value.Name, number, FactValueType.UInt64, source.Id),
            _ => throw new InvalidOperationException()
        }).ToImmutableArray());
}
