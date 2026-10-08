using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
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
    private readonly AgentLocalSystemIdentity localIdentity;
    private readonly ConcurrentDictionary<int, string> physicalDeviceIds = new();
    private readonly SemaphoreSlim localCaptureGate = new(1, 1);
    private readonly Action<AgentEvent> publish;
    private int startupStarted;

    public AgentInventoryCoordinator(
        IInventoryProvider nativeProvider,
        IInventoryProvider legacyProvider,
        IHardwareInventoryProvider manageProvider,
        IInventoryComparer comparer,
        InventorySnapshotRepository snapshots,
        InventoryComparisonRepository comparisons,
        LocalInventoryDocumentRepository localDocument,
        LocalSystemIdentityResolver localIdentity,
        IPhysicalDiskDeviceResolver deviceResolver,
        Action<AgentEvent>? publish = null)
    {
        this.nativeProvider = nativeProvider ?? throw new ArgumentNullException(nameof(nativeProvider));
        this.legacyProvider = legacyProvider ?? throw new ArgumentNullException(nameof(legacyProvider));
        this.manageProvider = manageProvider ?? throw new ArgumentNullException(nameof(manageProvider));
        this.comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
        this.snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        this.comparisons = comparisons ?? throw new ArgumentNullException(nameof(comparisons));
        this.localDocument = localDocument ?? throw new ArgumentNullException(nameof(localDocument));
        ArgumentNullException.ThrowIfNull(localIdentity);
        this.localIdentity = new AgentLocalSystemIdentity(localDocument, localIdentity);
        this.deviceResolver = deviceResolver ?? throw new ArgumentNullException(nameof(deviceResolver));
        this.publish = publish ?? (_ => { });
    }

    public async Task CaptureStartupAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref startupStarted, 1) != 0) return;
        foreach (var purpose in new[] { CollectionPurpose.Storage, CollectionPurpose.Hardware })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await CaptureManageAsync(new CaptureAgentManageInventoryRequest(CorrelationId.New(), purpose), cancellationToken, isAutomatic: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // A native provider failure must not fault an unobserved startup task
                // or prevent the independent full-hardware attempt.
                System.Diagnostics.Trace.TraceError("agent.inventory.startup_failed: {0}", exception);
                publish(new AgentInventoryFailedEvent(purpose, "agent.inventory.startup_failed", DateTimeOffset.UtcNow, IsAutomatic: true));
            }
        }
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

    public Task<ApplicationResult<AgentResponse>> CaptureManageAsync(
        CaptureAgentManageInventoryRequest request,
        CancellationToken cancellationToken,
        bool isAutomatic = false) => CaptureManageCoreAsync(request, cancellationToken, isAutomatic, null);

    public Task<ApplicationResult<AgentResponse>> CaptureManageScopedAsync(StorageInventoryScope scope,
        CorrelationId correlationId, CancellationToken cancellationToken) =>
        CaptureManageCoreAsync(new(correlationId, CollectionPurpose.Storage), cancellationToken, false, scope);

    private async Task<ApplicationResult<AgentResponse>> CaptureManageCoreAsync(
        CaptureAgentManageInventoryRequest request, CancellationToken cancellationToken, bool isAutomatic,
        StorageInventoryScope? scope)
    {
        var queuedAt = Stopwatch.GetTimestamp();
        await localCaptureGate.WaitAsync(cancellationToken);
        var gateAt = Stopwatch.GetTimestamp();
        StorageOperationTiming.Record("inventory.queue", queuedAt, scope?.OperationId, scope?.StepId, scope?.Key ?? request.Purpose.ToString());
        try
        {
            if (!Enum.IsDefined(request.Purpose)) return Failed(request.CorrelationId, "agent.inventory.invalid_purpose");
            var identity = await localIdentity.ResolveAsync(cancellationToken);
            var canonicalSystemId = identity.SystemId;
            var cached = await localDocument.LoadAsync(cancellationToken);
            var previous = cached is null ? null : LocalInventoryDocumentCodec.Decode(cached.Document);
            var generation = checked((previous?.SourceFacts?.Sources.Select(x => x.CaptureGeneration).DefaultIfEmpty(0).Max() ?? 0) + 1);
            if (scope is not null)
            {
                scope.Validate();
                if (scope.SystemId != canonicalSystemId || previous?.SourceFacts is not { } authority
                    || authority.SystemId != canonicalSystemId)
                    return Failed(request.CorrelationId, "agent.inventory.scope_system_or_authority_missing");
                // The client can request IDs; it cannot substitute a raw selector for a current target.
                // Persisted before locators are retained only when the object has already disappeared.
                var locators = scope.Targets.Select(target => authority.Objects.Any(x => x.Id == target.ProviderKey)
                    ? StorageInventoryScopeFactory.Resolve(authority, target)
                    : scope.BeforeLocators.Single(x => x.Target == target)).ToArray();
                var presentTargets = scope.Targets.Where(target => authority.Objects.Any(x => x.Id == target.ProviderKey)).ToArray();
                IReadOnlyList<string> beforeIds = presentTargets.Length == 0 ? [] : StorageInventoryScopeFactory.Create(authority,
                    scope.OperationId, scope.StepId, presentTargets, generation, includeRetainedRelationships: true).BeforeObjectIds;
                // Historical authority defines the absence domain; caller-supplied IDs never enlarge it.
                scope = scope with { BeforeLocators = locators, BeforeObjectIds = beforeIds, Generation = generation };
                scope.Validate();
                if (manageProvider is not IScopedHardwareInventoryProvider)
                    return Failed(request.CorrelationId, "agent.inventory.scope_provider_unavailable");
            }
            publish(new AgentInventoryStartedEvent(request.Purpose, DateTimeOffset.UtcNow, isAutomatic));
            var captureAt = Stopwatch.GetTimestamp();
            var document = scope is not null
                ? await ((IScopedHardwareInventoryProvider)manageProvider).CollectScopedAsync(scope, cancellationToken)
                : request.Purpose == CollectionPurpose.Hardware
                ? await manageProvider.CollectHardwareAsync(cancellationToken)
                : await manageProvider.CollectLocalAsync(cancellationToken);
            var capturedAt = Stopwatch.GetTimestamp();
            StorageOperationTiming.Record("inventory.collect", captureAt, scope?.OperationId, scope?.StepId, scope?.Key ?? request.Purpose.ToString(), 1);
            if (scope is not null && (document.SystemId != canonicalSystemId
                || document.SourceFacts?.ScopedCollection?.Scope != scope || document.SourceFacts.IsMerged))
                throw new InvalidDataException("Scoped inventory returned mismatched or merged evidence.");
            document = document with
            {
                SystemId = canonicalSystemId,
                SourceFacts = document.SourceFacts is null ? null : document.SourceFacts with
                {
                    SystemId = canonicalSystemId,
                    Sources = document.SourceFacts.Sources.Select(x => x with { CaptureGeneration = generation }).ToImmutableArray()
                }
            };
            var incomplete = scope is not null && document.SourceFacts?.ScopedCollection?.Complete != true;
            if (previous is not null && document.SourceFacts is not null)
            {
                if (previous.SystemId == canonicalSystemId && previous.SourceFacts is not null)
                    document = document with { SourceFacts = WinPoolFactRefresh.Merge(previous.SourceFacts, document.SourceFacts) };
            }
            CachePhysicalDeviceIds(document);
            StorageOperationTiming.Record("inventory.merge", capturedAt, scope?.OperationId, scope?.StepId, scope?.Key ?? request.Purpose.ToString());
            var projectionAt = Stopwatch.GetTimestamp();
            var projected = EmbeddedPowerShellInventoryProvider.Project(
                canonicalSystemId,
                document.Snapshot);
            StorageOperationTiming.Record("inventory.project", projectionAt, scope?.OperationId, scope?.StepId, scope?.Key ?? request.Purpose.ToString());
            var persistenceAt = Stopwatch.GetTimestamp();
            var saved = await snapshots.SaveAsync(
                projected,
                PersistedSystemKind.Local,
                Environment.MachineName,
                cancellationToken,
                LocalSystemIdentityResolver.CreateAuthorityBinding(Environment.MachineName));
            var payload = LocalInventoryDocumentCodec.Encode(document);
            if (LocalInventoryDocumentCodec.Decode(payload).SystemId != canonicalSystemId
                || projected.SystemId != canonicalSystemId
                || saved.Snapshot.SystemId != canonicalSystemId)
            {
                throw new InvalidDataException("The Local inventory identity is inconsistent.");
            }
            await localDocument.SaveAsync(saved.SnapshotId, payload, cancellationToken);
            StorageOperationTiming.Record("inventory.persist", persistenceAt, scope?.OperationId, scope?.StepId, scope?.Key ?? request.Purpose.ToString());
            Trace.WriteLine(JsonSerializer.Serialize(new { Event = "inventory.coordinator", Purpose = scope is null ? request.Purpose.ToString() : "ScopedStorage",
                OperationId = scope?.OperationId.Value, StepId = scope?.StepId, Scope = scope?.Key, Generation = generation,
                QueueMilliseconds = Stopwatch.GetElapsedTime(queuedAt, gateAt).TotalMilliseconds,
                CaptureMilliseconds = Stopwatch.GetElapsedTime(captureAt, capturedAt).TotalMilliseconds,
                MergeProjectionPersistenceMilliseconds = Stopwatch.GetElapsedTime(capturedAt).TotalMilliseconds,
                Complete = !incomplete }));
            publish(new AgentInventoryUpdatedEvent(request.Purpose, payload, DateTimeOffset.UtcNow, isAutomatic));
            if (incomplete)
                return new(ApplicationStatus.PartiallyCompleted, new ManageInventoryCaptureResponse(saved.SnapshotId, payload),
                    [new ApplicationMessage("agent.inventory.scope_incomplete", "agent.inventory.scope_incomplete",
                        "The target closure was not completely refreshed; prior facts remain retained.", ApplicationMessageSeverity.Warning, [])], request.CorrelationId);
            return Succeeded(
                new ManageInventoryCaptureResponse(saved.SnapshotId, payload),
                request.CorrelationId,
                identity.HasFragmentedHistory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            publish(new AgentInventoryFailedEvent(request.Purpose, "agent.inventory.cancelled", DateTimeOffset.UtcNow, isAutomatic));
            return ApplicationResult<AgentResponse>.FromStatus(
                ApplicationStatus.Cancelled,
                request.CorrelationId);
        }
        catch (Exception exception) when (
            exception is InventoryScanException
                or IOException
                or InvalidDataException
                or InvalidOperationException
                or ArgumentException
                or Microsoft.Data.Sqlite.SqliteException
                or UnauthorizedAccessException)
        {
            publish(new AgentInventoryFailedEvent(request.Purpose, "agent.inventory.manage_capture_failed", DateTimeOffset.UtcNow, isAutomatic));
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
            InventoryCaptureReason.Comparison);
        try
        {
            var native = await nativeProvider.CaptureAsync(captureRequest, cancellationToken);
            if (!native.IsSuccess || native.Value is null)
            {
                return new(native.Status, null, native.Messages, request.CorrelationId);
            }

            try
            {
                var identity = await localIdentity.ResolveAsync(cancellationToken);
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
