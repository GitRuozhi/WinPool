using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Windows;

/// <summary>PDH text only selects an instance after fresh provider identity and device associations were resolved.</summary>
public sealed partial class WindowsMonitorTargetIdentityResolver : IMonitorTargetIdentityResolver
{
    private readonly IHardwareInventoryProvider provider;
    private readonly Func<CancellationToken, Task<SystemId>>? localSystemIdentity;

    public WindowsMonitorTargetIdentityResolver(IHardwareInventoryProvider? provider = null,
        Func<CancellationToken, Task<SystemId>>? localSystemIdentity = null)
    {
        this.provider = provider ?? new WindowsHardwareInventoryProvider(purpose: CollectionPurpose.Storage);
        this.localSystemIdentity = localSystemIdentity;
    }

    public async Task<MonitorTargetResolution> ResolveAsync(MonitorRequest request, StorageInventoryScope? scope,
        CancellationToken cancellationToken)
    {
        StorageSystemDocument document;
        try { document = await StorageOperationTiming.MeasureAsync("monitor.identity.collect", async () => scope is null ? await provider.CollectLocalAsync(cancellationToken).ConfigureAwait(false)
            : provider is IScopedHardwareInventoryProvider scoped ? await scoped.CollectScopedAsync(scope, cancellationToken).ConfigureAwait(false)
            : throw new InvalidOperationException("The monitor identity provider cannot obtain scoped evidence."),
            scope?.OperationId, scope?.StepId, scope?.Key ?? "monitor.initial", 1).ConfigureAwait(false); }
        catch (InventoryScanException exception) { throw new InvalidDataException("Fresh monitoring identity collection failed.", exception); }
        var facts = document.SourceFacts ?? throw new InvalidDataException("Monitor identity facts are missing.");
        if (document.Kind != StorageSystemKind.Local || facts.SystemId != document.SystemId || facts.IsSimulation || facts.IsMerged
            || !StringComparer.OrdinalIgnoreCase.Equals(document.Snapshot.Computer.Name, Environment.MachineName)
            || (scope is not null && (facts.SystemId != scope.SystemId || facts.ScopedCollection is not { Complete: true })))
            throw new InvalidDataException("Monitor rebinding requires complete fresh local source evidence.");
        if (scope is not null && (facts.ScopedCollection!.Scope.SystemId != scope.SystemId
            || facts.ScopedCollection.Scope.OperationId != scope.OperationId || facts.ScopedCollection.Scope.StepId != scope.StepId
            || facts.ScopedCollection.Scope.Generation != scope.Generation || !facts.ScopedCollection.Scope.Targets.SequenceEqual(scope.Targets)
            || !facts.ScopedCollection.Scope.BeforeLocators.SequenceEqual(scope.BeforeLocators)))
            throw new InvalidDataException("Monitor rebinding returned evidence for another scoped request.");
        var system = localSystemIdentity is null ? facts.SystemId : await localSystemIdentity(cancellationToken).ConfigureAwait(false);
        if (scope is not null && system != scope.SystemId) throw new InvalidDataException("The monitor system changed during rebinding.");
        facts = facts with { SystemId = system };
        return ResolveFacts(request, facts, scope);
    }

    internal static MonitorTargetResolution ResolveFacts(MonitorRequest request, WinPoolFacts facts, StorageInventoryScope? scope)
    {
        facts.Validate();
        var targets = new List<MonitorTarget>();
        var unresolved = new List<MonitorEditTargetState>();
        var sources = facts.Sources.ToDictionary(x => x.Id);
        var objects = facts.Objects.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var throughputRequested = request.Metrics.Any(x => x is MonitorMetricKind.ActiveTimePercent or MonitorMetricKind.ReadBytesPerSecond
            or MonitorMetricKind.WriteBytesPerSecond or MonitorMetricKind.AverageQueueLength);
        var stateRequested = request.Metrics.Any(x => x is MonitorMetricKind.VirtualDiskActiveBytes or MonitorMetricKind.VirtualDiskMissingBytes
            or MonitorMetricKind.VirtualDiskStaleBytes or MonitorMetricKind.VirtualDiskNeedRegenerationBytes
            or MonitorMetricKind.VirtualDiskRegeneratingBytes or MonitorMetricKind.VirtualDiskPendingDeletionBytes);
        bool Requested(WinPoolSourceObject item) => request.Targets.Any(x => x.CounterIdentity == "*"
            || x.ObjectId.ProviderKey == item.Id) || scope is not null;
        foreach (var item in facts.Objects.Where(x => x.ObjectType is FactObjectType.PhysicalDisk or FactObjectType.VirtualDisk))
        {
            if (!Requested(item)) continue;
            if (!throughputRequested && (item.ObjectType != FactObjectType.VirtualDisk || !stateRequested)) continue;
            var kind = item.ObjectType == FactObjectType.PhysicalDisk ? StorageObjectKind.PhysicalDisk : StorageObjectKind.VirtualDisk;
            var id = new StorageObjectId(facts.SystemId, kind, item.Id);
            var uniqueId = Text(item, "UniqueId");
            var objectId = Text(item, "ObjectId");
            if (!item.HasReliableIdentity || sources[item.SourceRef].ReadState != FieldReadState.Returned
                || string.IsNullOrWhiteSpace(uniqueId) || string.IsNullOrWhiteSpace(objectId))
            {
                Unresolved(id, "monitor.identity.provider_identity_unavailable");
                continue;
            }
            var disks = facts.Relationships.Where(x => !x.IsRetained && x.Kind == "same-device" && x.FromId == item.Id)
                .Select(x => objects[x.ToId]).Where(x => x.ObjectType == FactObjectType.Disk && x.HasReliableIdentity
                    && sources[x.SourceRef].ReadState == FieldReadState.Returned).ToArray();
            long? diskNumber = null;
            if (disks.Length == 1 && disks[0].Field("Number") is { } number && number.TryGetInt64(out var index)
                && index >= 0 && index <= uint.MaxValue && !string.IsNullOrWhiteSpace(Text(disks[0], "UniqueId"))
                && !string.IsNullOrWhiteSpace(Text(disks[0], "ObjectId")))
                diskNumber = index;
            if (throughputRequested && diskNumber is { } throughputIndex)
                targets.Add(new(id, "disk-number:" + throughputIndex.ToString(CultureInfo.InvariantCulture))
                {
                    CounterSource = MonitorCounterSource.PhysicalDisk,
                    ProviderIdentity = uniqueId + "|" + objectId + "|" + Text(disks[0], "UniqueId") + "|" + Text(disks[0], "ObjectId"),
                    DisplayName = Text(item, "FriendlyName")
                });
            else if (throughputRequested) Unresolved(id, "monitor.identity.exact_os_disk_association_unavailable");
            if (stateRequested && item.ObjectType == FactObjectType.VirtualDisk)
            {
                var matches = GuidPattern().Matches(uniqueId);
                if (diskNumber is { } stateIndex)
                    targets.Add(new(id, "vd-disk-number:" + stateIndex.ToString(CultureInfo.InvariantCulture))
                    {
                        CounterSource = MonitorCounterSource.StorageSpacesVirtualDisk,
                        ProviderIdentity = uniqueId + "|" + objectId + "|" + Text(disks[0], "UniqueId") + "|" + Text(disks[0], "ObjectId"),
                        DisplayName = Text(item, "FriendlyName")
                    });
                else if (matches.Count == 1 && Guid.TryParse(matches[0].Value, out var guid))
                    targets.Add(new(id, "vd-guid:" + guid.ToString("D"))
                    {
                        CounterSource = MonitorCounterSource.StorageSpacesVirtualDisk,
                        ProviderIdentity = uniqueId + "|" + objectId,
                        DisplayName = Text(item, "FriendlyName")
                    });
                else Unresolved(id, "monitor.identity.virtual_counter_guid_unavailable");
            }
        }
        // An OS disk index may be reassigned, but cannot identify two current storage devices.
        var ambiguous = targets.Where(x => x.CounterIdentity.StartsWith("disk-number:", StringComparison.Ordinal)
                || x.CounterIdentity.StartsWith("vd-disk-number:", StringComparison.Ordinal))
            .GroupBy(x => x.CounterIdentity, StringComparer.Ordinal).Where(x => x.Select(y => y.ObjectId).Distinct().Count() != 1)
            .SelectMany(x => x).ToArray();
        foreach (var target in ambiguous) { targets.Remove(target); Unresolved(target.ObjectId, "monitor.identity.os_disk_binding_ambiguous"); }
        return new(facts.SystemId, facts, targets, unresolved.DistinctBy(x => (x.TargetId, x.ReasonCode)).ToArray());

        void Unresolved(StorageObjectId id, string reason) => unresolved.Add(new(facts.SystemId, id,
            scope?.OperationId ?? new OperationId(Guid.Empty), scope?.StepId ?? "initial", MonitorEditTargetStatus.NeedsSelection,
            reason, facts.InventoryCapturedAt));
    }

    private static string Text(WinPoolSourceObject item, string name) =>
        item.Field(name) is { ReadState: FieldReadState.Returned, Value: { ValueKind: JsonValueKind.String } value }
            ? value.GetString() ?? "" : "";

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    internal static partial Regex GuidPattern();
}
