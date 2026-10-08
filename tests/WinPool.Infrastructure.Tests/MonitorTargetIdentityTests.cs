using System.Collections.Immutable;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class MonitorTargetIdentityTests
{
    private readonly SystemId system = SystemId.New();

    [Fact]
    public void HexProviderIdentityBindsStateCountersThroughItsExactCurrentOsDisk()
    {
        var facts = VirtualFacts(3, "current-vd", "47589F7504BDB54583984EF9C2D51F43", "current-disk");
        var resolved = WindowsMonitorTargetIdentityResolver.ResolveFacts(StateRequest(), facts, null);
        Assert.Empty(resolved.UnresolvedTargets);
        var target = Assert.Single(resolved.Targets);
        Assert.Equal("current-vd", target.ObjectId.ProviderKey);
        Assert.Equal("vd-disk-number:3", target.CounterIdentity);
        Assert.Equal(MonitorCounterSource.StorageSpacesVirtualDisk, target.CounterSource);
        Assert.Equal("47589F7504BDB54583984EF9C2D51F43|object-current-vd|uid-current-disk|object-current-disk", target.ProviderIdentity);
        StorageSpacesVirtualDiskSample Sample(string name) => new(name, 1, 2, 3, 4, 5, 6);
        var exact = Sample("a completely different display name - Disk 3");
        Assert.Equal(exact, Assert.Single(PdhDiskMonitorSource.SelectVirtualDisks(target.CounterIdentity, [exact, Sample("same-name - Disk 30")])));
    }

    [Fact]
    public void ReusedOsNumberBelongsToTheNewProviderIdentityAndMovedDiskUsesItsNewNumber()
    {
        var old = Assert.Single(WindowsMonitorTargetIdentityResolver.ResolveFacts(StateRequest(),
            VirtualFacts(3, "old-vd", "11111111111111111111111111111111", "old-disk"), null).Targets);
        var replacement = Assert.Single(WindowsMonitorTargetIdentityResolver.ResolveFacts(StateRequest(),
            VirtualFacts(3, "new-vd", "22222222222222222222222222222222", "new-disk"), null).Targets);
        Assert.Equal(old.CounterIdentity, replacement.CounterIdentity);
        Assert.NotEqual(old.ObjectId, replacement.ObjectId);
        Assert.NotEqual(old.ProviderIdentity, replacement.ProviderIdentity);
        Assert.Contains("new-disk", replacement.ProviderIdentity!);
        var moved = Assert.Single(WindowsMonitorTargetIdentityResolver.ResolveFacts(StateRequest(),
            VirtualFacts(8, "new-vd", "22222222222222222222222222222222", "moved-disk"), null).Targets);
        Assert.Equal("vd-disk-number:8", moved.CounterIdentity);
        Assert.Contains("moved-disk", moved.ProviderIdentity!);
        Assert.Empty(PdhDiskMonitorSource.SelectVirtualDisks(moved.CounterIdentity, [new("same-name - Disk 3", 1, 0, 0, 0, 0, 0)]));
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Retained")]
    [InlineData("Ambiguous")]
    public void StateNumberBindingRequiresOneCurrentProviderAssociation(string condition)
    {
        var facts = VirtualFacts(3, "current-vd", "47589F7504BDB54583984EF9C2D51F43", "current-disk");
        facts = facts with { Relationships = condition switch
        {
            "Missing" => [],
            "Retained" => facts.Relationships.Select(x => x with { IsRetained = true }).ToImmutableArray(),
            _ => [.. facts.Relationships, new("current-vd", "disk-b", "same-device")]
        } };
        var resolved = WindowsMonitorTargetIdentityResolver.ResolveFacts(StateRequest(), facts, null);
        Assert.Empty(resolved.Targets);
        Assert.Single(resolved.UnresolvedTargets);
    }

    [Fact]
    public void DuplicateProviderDiskNumbersAndDuplicatePdhSuffixesNeverBindStateCounters()
    {
        var facts = Facts(3, 3);
        facts = facts with { Objects = facts.Objects.Select(x => x.ObjectType == FactObjectType.PhysicalDisk
            ? x with { ObjectType = FactObjectType.VirtualDisk } : x).ToImmutableArray() };
        var resolved = WindowsMonitorTargetIdentityResolver.ResolveFacts(StateRequest(), facts, null);
        Assert.Empty(resolved.Targets);
        Assert.All(resolved.UnresolvedTargets, x => Assert.Equal("monitor.identity.os_disk_binding_ambiguous", x.ReasonCode));
        Assert.Equal(2, resolved.UnresolvedTargets.Count);
        Assert.Empty(PdhDiskMonitorSource.SelectVirtualDisks("vd-disk-number:3",
            [new("first - Disk 3", 1, 0, 0, 0, 0, 0), new("second - Disk 3", 2, 0, 0, 0, 0, 0)]));
    }

    [Theory]
    [InlineData("same-name")]
    [InlineData("same-name - Disk 30")]
    [InlineData("same-name - Disk 03")]
    [InlineData("same-name - Disk 3 trailing")]
    [InlineData("same-name - Disk -3")]
    public void StateCounterSuffixMustBeExact(string instance) =>
        Assert.Empty(PdhDiskMonitorSource.SelectVirtualDisks("vd-disk-number:3", [new(instance, 1, 0, 0, 0, 0, 0)]));

    [Fact]
    public void SameNamesRemainDistinctAndAmbiguousDiskNumbersNeverBind()
    {
        var facts = Facts(3, 4);
        var resolved = WindowsMonitorTargetIdentityResolver.ResolveFacts(Request(), facts, null);
        Assert.Equal(new[] { "disk-number:3", "disk-number:4" }, resolved.Targets.Select(x => x.CounterIdentity));
        Assert.Equal(2, resolved.Targets.Select(x => x.ObjectId).Distinct().Count());
        Assert.All(resolved.Targets, x => Assert.Equal("same-name", x.DisplayName));
        var ambiguous = WindowsMonitorTargetIdentityResolver.ResolveFacts(Request(), Facts(3, 3), null);
        Assert.Empty(ambiguous.Targets);
        Assert.Equal(2, ambiguous.UnresolvedTargets.Count);
        Assert.All(ambiguous.UnresolvedTargets, x => Assert.Equal("monitor.identity.os_disk_binding_ambiguous", x.ReasonCode));
    }

    [Fact]
    public void StateOnlyRequestDoesNotRequireOsThroughputBindingOrProduceEmptyThroughputFrames()
    {
        var facts = Facts(3, 4);
        var guid = Guid.NewGuid();
        var item = facts.Objects[0] with { ObjectType = FactObjectType.VirtualDisk,
            Fields = facts.Objects[0].Fields.Select(x => x.Name == "UniqueId"
                ? WinPoolSourceField.Returned("UniqueId", guid.ToString("D"), FactValueType.String, x.SourceRef) : x).ToImmutableArray() };
        facts = facts with { Objects = [item], Relationships = [] };
        var request = Request() with { Metrics = [MonitorMetricKind.VirtualDiskActiveBytes] };
        var resolved = WindowsMonitorTargetIdentityResolver.ResolveFacts(request, facts, null);
        Assert.Empty(resolved.UnresolvedTargets);
        var target = Assert.Single(resolved.Targets);
        Assert.Equal(MonitorCounterSource.StorageSpacesVirtualDisk, target.CounterSource);
        Assert.Equal("vd-guid:" + guid, target.CounterIdentity);
        var throughput = WindowsMonitorTargetIdentityResolver.ResolveFacts(Request(), facts, null);
        Assert.Empty(throughput.Targets);
        Assert.Single(throughput.UnresolvedTargets);
    }

    [Fact]
    public void RetainedAssociationCannotSupplyFreshSamplingIdentity()
    {
        var facts = Facts(3, 4);
        facts = facts with { Relationships = facts.Relationships.Select(x => x with { IsRetained = true, ReasonCode = "PartialCollection" }).ToImmutableArray() };
        var resolved = WindowsMonitorTargetIdentityResolver.ResolveFacts(Request(), facts, null);
        Assert.Empty(resolved.Targets);
        Assert.Equal(2, resolved.UnresolvedTargets.Count);
    }

    [Fact]
    public void VirtualInstanceRequiresExactGuidAndPhysicalSelectorRequiresExactIndex()
    {
        var guid = Guid.NewGuid();
        StorageSpacesVirtualDiskSample Sample(string name) => new(name, 1, 0, 0, 0, 0, 0);
        var matching = Sample("same-name {" + guid + "}");
        var unrelated = Sample("same-name {" + Guid.NewGuid() + "}");
        var ambiguous = Sample("same-name {" + guid + "} {" + Guid.NewGuid() + "}");
        Assert.Equal(matching, Assert.Single(PdhDiskMonitorSource.SelectVirtualDisks("vd-guid:" + guid, [matching, unrelated, ambiguous])));
        Assert.Empty(PdhDiskMonitorSource.SelectVirtualDisks("vd-guid:" + guid, [Sample("same-name")]));
        var disk = new DiskPerformanceSample("3 E:", 1, 2, 3, 4);
        Assert.Equal(disk, Assert.Single(PdhDiskMonitorSource.SelectPhysicalDisks("disk-number:3", [disk, new("30 F:", 1, 2, 3, 4)])));
    }

    private MonitorRequest Request() => new(SessionId.New(), system,
        [new(new(system, StorageObjectKind.PhysicalDisk, "wildcard"), "*")],
        [MonitorMetricKind.ActiveTimePercent], TimeSpan.FromSeconds(1), true);

    [Theory]
    [InlineData(false, "ProviderSourceFailed")]
    [InlineData(true, "ProviderSourceFailed")]
    [InlineData(false, "ProviderIdentityFailed")]
    [InlineData(true, "ProviderIdentityFailed")]
    [InlineData(false, "ProviderIdentityNull")]
    [InlineData(true, "ProviderIdentityNull")]
    [InlineData(false, "OsSourceFailed")]
    [InlineData(true, "OsSourceFailed")]
    [InlineData(false, "OsIdentityUnavailable")]
    [InlineData(true, "OsIdentityUnavailable")]
    [InlineData(false, "OsIdentityMissing")]
    [InlineData(true, "OsIdentityMissing")]
    public void FreshTargetCannotBindFromFailedOrMissingExactProviderEvidence(bool virtualDisk, string failure)
    {
        var facts = virtualDisk ? VirtualFacts(3, "current-vd", "47589F7504BDB54583984EF9C2D51F43", "current-disk") : Facts(3, 4);
        var targetId = virtualDisk ? "current-vd" : "physical-a";
        var osSource = facts.Sources[0] with { Id = "os-source" };
        facts = facts with
        {
            Sources = [.. facts.Sources, osSource],
            Objects = facts.Objects.Select(x => x.Id == "disk-a" ? x with { SourceRef = osSource.Id,
                Fields = x.Fields.Select(field => field with { SourceRef = osSource.Id }).ToImmutableArray() } : x).ToImmutableArray()
        };
        if (failure.EndsWith("SourceFailed", StringComparison.Ordinal))
            facts = facts with { Sources = facts.Sources.Select(x => x.Id == (failure == "OsSourceFailed" ? osSource.Id : "storage")
                ? x with { ReadState = FieldReadState.Failed, ReasonCode = "source-query-failed" } : x).ToImmutableArray() };
        else
        {
            var broken = failure.StartsWith("Os", StringComparison.Ordinal) ? "disk-a" : targetId;
            facts = facts with { Objects = facts.Objects.Select(x => x.Id != broken ? x : x with
            {
                Fields = x.Fields.Where(field => !(failure == "OsIdentityMissing" && field.Name == "ObjectId"))
                    .Select(field => field.Name != "UniqueId" || failure == "OsIdentityMissing" ? field : failure == "ProviderIdentityNull"
                        ? WinPoolSourceField.Returned<string?>(field.Name, null, field.ValueType, field.SourceRef)
                        : WinPoolSourceField.Missing(field.Name, field.ValueType, field.SourceRef,
                            failure == "ProviderIdentityFailed" ? FieldReadState.Failed : FieldReadState.Unavailable)).ToImmutableArray()
            }).ToImmutableArray() };
        }
        var request = Request() with { Targets = [new(new(system, virtualDisk ? StorageObjectKind.VirtualDisk : StorageObjectKind.PhysicalDisk, targetId), "old-counter")],
            Metrics = virtualDisk ? [MonitorMetricKind.ActiveTimePercent, MonitorMetricKind.VirtualDiskActiveBytes] : [MonitorMetricKind.ActiveTimePercent] };
        var resolved = WindowsMonitorTargetIdentityResolver.ResolveFacts(request, facts, null);
        Assert.Empty(resolved.Targets);
        Assert.All(resolved.UnresolvedTargets, x => Assert.Equal(targetId, x.TargetId.ProviderKey));
        Assert.NotEmpty(resolved.UnresolvedTargets);
    }

    [Fact]
    public void FreshSameVirtualDiskBindsBothFamiliesButDoesNotSubstituteAReplacementForAnOldId()
    {
        var facts = VirtualFacts(9, "current-vd", "47589F7504BDB54583984EF9C2D51F43", "current-disk");
        var id = new StorageObjectId(system, StorageObjectKind.VirtualDisk, "current-vd");
        var request = StateRequest() with { Targets = [new(id, "old-counter")],
            Metrics = [MonitorMetricKind.ActiveTimePercent, MonitorMetricKind.VirtualDiskActiveBytes] };
        var resolved = WindowsMonitorTargetIdentityResolver.ResolveFacts(request, facts, null);
        Assert.Empty(resolved.UnresolvedTargets);
        Assert.Equal(new[] { "disk-number:9", "vd-disk-number:9" }, resolved.Targets.Select(x => x.CounterIdentity));
        Assert.All(resolved.Targets, x => Assert.Equal(id, x.ObjectId));
        Assert.Single(resolved.Targets.Select(x => x.ProviderIdentity).Distinct());
        var old = request with { Targets = [new(new(system, StorageObjectKind.VirtualDisk, "old-vd"), "old-counter")] };
        Assert.Empty(WindowsMonitorTargetIdentityResolver.ResolveFacts(old, facts, null).Targets);
    }

    private MonitorRequest StateRequest() => Request() with { Metrics = [MonitorMetricKind.VirtualDiskActiveBytes,
        MonitorMetricKind.VirtualDiskMissingBytes, MonitorMetricKind.VirtualDiskStaleBytes,
        MonitorMetricKind.VirtualDiskNeedRegenerationBytes, MonitorMetricKind.VirtualDiskRegeneratingBytes,
        MonitorMetricKind.VirtualDiskPendingDeletionBytes] };

    private WinPoolFacts VirtualFacts(int number, string virtualId, string uniqueId, string diskIdentity)
    {
        var facts = Facts(number, 4);
        return facts with
        {
            Objects = facts.Objects.Select(x => x.Id == "physical-a"
                ? x with { Id = virtualId, ObjectType = FactObjectType.VirtualDisk,
                    Fields = x.Fields.Select(f => f.Name switch
                    {
                        "UniqueId" => WinPoolSourceField.Returned(f.Name, uniqueId, FactValueType.String, f.SourceRef),
                        "ObjectId" => WinPoolSourceField.Returned(f.Name, "object-" + virtualId, FactValueType.String, f.SourceRef),
                        _ => f
                    }).ToImmutableArray() }
                : x.Id == "disk-a" ? x with { Fields = x.Fields.Select(f => f.Name switch
                    {
                        "UniqueId" => WinPoolSourceField.Returned(f.Name, "uid-" + diskIdentity, FactValueType.String, f.SourceRef),
                        "ObjectId" => WinPoolSourceField.Returned(f.Name, "object-" + diskIdentity, FactValueType.String, f.SourceRef),
                        _ => f
                    }).ToImmutableArray() } : x).ToImmutableArray(),
            Relationships = [new(virtualId, "disk-a", "same-device"), new("physical-b", "disk-b", "same-device")]
        };
    }

    private WinPoolFacts Facts(int first, int second)
    {
        var source = new WinPoolSource("storage", FactOrigin.StorageCim, "root/microsoft/windows/storage", "MSFT_Disk", DateTimeOffset.UtcNow, CollectionPurpose.Storage);
        WinPoolSourceObject Item(string id, FactObjectType kind, int index) => new(id, kind, source.Id, "identity-" + id, true,
            [WinPoolSourceField.Returned("UniqueId", "uid-" + id, FactValueType.String, source.Id),
             WinPoolSourceField.Returned("ObjectId", "object-" + id, FactValueType.String, source.Id),
             WinPoolSourceField.Returned("FriendlyName", "same-name", FactValueType.String, source.Id),
             WinPoolSourceField.Returned("Number", index, FactValueType.Int64, source.Id)]);
        return new(WinPoolFacts.CurrentFormatVersion, system, 0, [source],
            [Item("physical-a", FactObjectType.PhysicalDisk, first), Item("physical-b", FactObjectType.PhysicalDisk, second),
             Item("disk-a", FactObjectType.Disk, first), Item("disk-b", FactObjectType.Disk, second)],
            [new("physical-a", "disk-a", "same-device"), new("physical-b", "disk-b", "same-device")], [], []);
    }
}
