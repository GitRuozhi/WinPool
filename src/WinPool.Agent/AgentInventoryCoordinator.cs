using System.Collections.Concurrent;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent;

internal sealed class AgentInventoryCoordinator
{
    private readonly IInventoryProvider nativeProvider;
    private readonly IInventoryProvider legacyProvider;
    private readonly IHardwareInventoryProvider manageProvider;
    private readonly IPhysicalDiskDeviceResolver deviceResolver;
    private readonly IInventoryComparer comparer;
    private readonly InventorySnapshotRepository snapshots;
    private readonly InventoryComparisonRepository comparisons;
    private readonly LocalInventoryDocumentRepository localDocument;
    private readonly LocalSystemIdentityResolver localIdentity;
    private readonly ConcurrentDictionary<int, string> physicalDeviceIds = new();
    private readonly SemaphoreSlim localCaptureGate = new(1, 1);

    public AgentInventoryCoordinator(
        IInventoryProvider nativeProvider,
        IInventoryProvider legacyProvider,
        IHardwareInventoryProvider manageProvider,
        IInventoryComparer comparer,
        InventorySnapshotRepository snapshots,
        InventoryComparisonRepository comparisons,
        LocalInventoryDocumentRepository localDocument,
        LocalSystemIdentityResolver localIdentity,
        IPhysicalDiskDeviceResolver deviceResolver)
    {
        this.nativeProvider = nativeProvider ?? throw new ArgumentNullException(nameof(nativeProvider));
        this.legacyProvider = legacyProvider ?? throw new ArgumentNullException(nameof(legacyProvider));
        this.manageProvider = manageProvider ?? throw new ArgumentNullException(nameof(manageProvider));
        this.comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
        this.snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        this.comparisons = comparisons ?? throw new ArgumentNullException(nameof(comparisons));
        this.localDocument = localDocument ?? throw new ArgumentNullException(nameof(localDocument));
        this.localIdentity = localIdentity ?? throw new ArgumentNullException(nameof(localIdentity));
        this.deviceResolver = deviceResolver ?? throw new ArgumentNullException(nameof(deviceResolver));
    }

    public string? ResolvePhysicalDeviceId(int diskNumber)
    {
        var physicalDeviceId = physicalDeviceIds.GetValueOrDefault(diskNumber);
        if (!string.IsNullOrWhiteSpace(physicalDeviceId))
        {
            return physicalDeviceId;
        }

        physicalDeviceId = deviceResolver.ResolvePnpDeviceId(diskNumber);
        if (!string.IsNullOrWhiteSpace(physicalDeviceId))
        {
            physicalDeviceIds[diskNumber] = physicalDeviceId;
        }

        return physicalDeviceId;
    }

    public async Task<ApplicationResult<AgentResponse>> CaptureManageAsync(
        CaptureAgentManageInventoryRequest request,
        CancellationToken cancellationToken)
    {
        await localCaptureGate.WaitAsync(cancellationToken);
        try
        {
            var document = await manageProvider.CollectLocalAsync(cancellationToken);
            CachePhysicalDeviceIds(document);
            var sanitized = StorageSystemDocumentSanitizer.RedactSensitiveData(document);
            var provisional = EmbeddedPowerShellInventoryProvider.Project(
                sanitized.SystemId,
                sanitized.Snapshot,
                includeSensitiveValuesInMemory: false);
            var preferredSystemId = await TryReadPreferredLocalSystemIdAsync(cancellationToken);
            var identity = await localIdentity.ResolveAsync(
                Environment.MachineName,
                preferredSystemId,
                cancellationToken);
            var canonicalSystemId = identity.SystemId;
            sanitized = sanitized with { SystemId = canonicalSystemId };
            var projected = EmbeddedPowerShellInventoryProvider.Project(
                canonicalSystemId,
                sanitized.Snapshot,
                includeSensitiveValuesInMemory: false);
            var saved = await snapshots.SaveAsync(
                projected,
                PersistedSystemKind.Local,
                Environment.MachineName,
                cancellationToken,
                LocalSystemIdentityResolver.CreateAuthorityBinding(Environment.MachineName));
            var payload = LocalInventoryDocumentCodec.Encode(sanitized);
            if (LocalInventoryDocumentCodec.Decode(payload).SystemId != canonicalSystemId
                || projected.SystemId != canonicalSystemId
                || saved.Snapshot.SystemId != canonicalSystemId)
            {
                throw new InvalidDataException("The Local inventory identity is inconsistent.");
            }
            await localDocument.SaveAsync(saved.SnapshotId, payload, cancellationToken);
            return Succeeded(
                new ManageInventoryCaptureResponse(saved.SnapshotId, payload),
                request.CorrelationId,
                identity.HasFragmentedHistory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Cancelled,
                request.CorrelationId);
        }
        catch (Exception exception) when (
            exception is InventoryScanException
                or IOException
                or InvalidDataException
                or Microsoft.Data.Sqlite.SqliteException
                or UnauthorizedAccessException)
        {
            return Failed(request.CorrelationId, "agent.inventory.manage_capture_failed");
        }
        finally
        {
            localCaptureGate.Release();
        }
    }

    public async Task<ApplicationResult<AgentResponse>> LoadManageAsync(
        LoadAgentManageInventoryRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var persisted = await localDocument.LoadAsync(cancellationToken);
            return ApplicationResult<AgentResponse>.Succeeded(
                new ManageInventoryLoadedResponse(persisted?.SnapshotId, persisted?.Document),
                request.CorrelationId);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            return Failed(request.CorrelationId, "agent.inventory.cached_load_failed");
        }
    }

    public async Task<ApplicationResult<AgentResponse>> CaptureComparisonAsync(
        CaptureAgentInventoryRequest request,
        CancellationToken cancellationToken)
    {
        await localCaptureGate.WaitAsync(cancellationToken);
        var captureRequest = new InventoryRequest(
            SystemId.New(),
            InventoryCaptureReason.Comparison,
            IncludeSensitiveValuesInMemory: false);
        try
        {
            var native = await nativeProvider.CaptureAsync(captureRequest, cancellationToken);
            if (!native.IsSuccess || native.Value is null)
            {
                return new(native.Status, null, native.Messages, request.CorrelationId);
            }

            try
            {
                var preferredSystemId = await TryReadPreferredLocalSystemIdAsync(cancellationToken);
                var identity = await localIdentity.ResolveAsync(
                    Environment.MachineName,
                    preferredSystemId,
                    cancellationToken);
                var canonicalSystemId = identity.SystemId;
                var nativeSnapshot = Rebind(native.Value, canonicalSystemId);
                var savedNative = await snapshots.SaveAsync(
                    nativeSnapshot,
                    PersistedSystemKind.Local,
                    Environment.MachineName,
                    cancellationToken,
                    LocalSystemIdentityResolver.CreateAuthorityBinding(Environment.MachineName));
                if (!request.IncludeLegacyComparison)
                {
                    return Succeeded(
                        new InventoryCaptureResponse(
                            savedNative.SnapshotId,
                            savedNative.Snapshot,
                            null,
                            null,
                            null,
                            null),
                        request.CorrelationId,
                        identity.HasFragmentedHistory);
                }

                var legacy = await legacyProvider.CaptureAsync(captureRequest, cancellationToken);
                if (!legacy.IsSuccess || legacy.Value is null)
                {
                    return new(
                        ApplicationStatus.PartiallyCompleted,
                        new InventoryCaptureResponse(
                            savedNative.SnapshotId,
                            savedNative.Snapshot,
                            null,
                            null,
                            null,
                            null),
                        legacy.Messages,
                        request.CorrelationId);
                }

                var savedLegacy = await snapshots.SaveAsync(
                    Rebind(legacy.Value, canonicalSystemId),
                    PersistedSystemKind.Local,
                    Environment.MachineName,
                    cancellationToken,
                    LocalSystemIdentityResolver.CreateAuthorityBinding(Environment.MachineName));
                var comparison = comparer.Compare(savedLegacy.Snapshot, savedNative.Snapshot);
                var savedComparison = await comparisons.SaveAsync(
                    savedLegacy.SnapshotId,
                    savedNative.SnapshotId,
                    comparison,
                    cancellationToken);
                return Succeeded(
                    new InventoryCaptureResponse(
                        savedNative.SnapshotId,
                        savedNative.Snapshot,
                        savedLegacy.SnapshotId,
                        savedLegacy.Snapshot,
                        savedComparison.ComparisonId,
                        savedComparison.Comparison),
                    request.CorrelationId,
                    identity.HasFragmentedHistory);
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidDataException
                    or Microsoft.Data.Sqlite.SqliteException
                    or ArgumentException)
            {
                return ApplicationResult<AgentResponse>.FromStatus(
                    ApplicationStatus.Failed,
                    request.CorrelationId,
                    new ApplicationMessage(
                        "agent.inventory.persistence_or_comparison_failed",
                        "agent.inventory.persistence_or_comparison_failed",
                        exception.Message,
                        ApplicationMessageSeverity.Error,
                        []));
            }
        }
        finally
        {
            localCaptureGate.Release();
        }
    }

    private void CachePhysicalDeviceIds(StorageSystemDocument document)
    {
        physicalDeviceIds.Clear();
        foreach (var disk in document.Snapshot.PhysicalDisks)
        {
            if (disk.DeviceId is int diskNumber
                && !string.IsNullOrWhiteSpace(disk.PnpDeviceId))
            {
                physicalDeviceIds[diskNumber] = disk.PnpDeviceId;
            }
        }
    }

    private async Task<SystemId?> TryReadPreferredLocalSystemIdAsync(
        CancellationToken cancellationToken)
    {
        var persisted = await localDocument.LoadAsync(cancellationToken);
        if (persisted is not null)
        {
            var previous = LocalInventoryDocumentCodec.Decode(persisted.Document);
            if (StringComparer.OrdinalIgnoreCase.Equals(
                    previous.Snapshot.Computer.Name,
                    Environment.MachineName))
            {
                return previous.SystemId;
            }
        }

        return null;
    }

    private static InventorySnapshot Rebind(
        InventorySnapshot snapshot,
        SystemId systemId)
    {
        if (snapshot.SystemId == systemId)
        {
            return snapshot;
        }

        StorageObjectId RebindId(StorageObjectId id) =>
            new(systemId, id.Kind, id.ProviderKey);

        return snapshot with
        {
            SystemId = systemId,
            Objects = snapshot.Objects.Select(item => item with
            {
                Id = RebindId(item.Id),
                ParentId = item.ParentId is { } parent ? RebindId(parent) : null
            }).ToArray(),
            IdentityDiagnostics = snapshot.IdentityDiagnostics.Select(item => item with
            {
                ObjectId = RebindId(item.ObjectId)
            }).ToArray(),
            Relationships = snapshot.Relationships?.Select(item => item with
            {
                FromObjectId = RebindId(item.FromObjectId),
                ToObjectId = RebindId(item.ToObjectId)
            }).ToArray()
        };
    }

    private static ApplicationResult<AgentResponse> Failed(
        CorrelationId correlationId,
        string code) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Failed,
            correlationId,
            new ApplicationMessage(code, code, string.Empty, ApplicationMessageSeverity.Error, []));

    private static ApplicationResult<AgentResponse> Succeeded(
        AgentResponse response,
        CorrelationId correlationId,
        bool hasFragmentedHistory) =>
        hasFragmentedHistory
            ? new(
                ApplicationStatus.PartiallyCompleted,
                response,
                [new ApplicationMessage(
                    "agent.inventory.local_identity_fragmented",
                    "agent.inventory.local_identity_fragmented",
                    string.Empty,
                    ApplicationMessageSeverity.Warning,
                    [])],
                correlationId)
            : ApplicationResult<AgentResponse>.Succeeded(response, correlationId);
}
