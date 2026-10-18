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
    private readonly IHardwareInventoryProvider manageProvider;
    private readonly IPhysicalDiskDeviceResolver deviceResolver;
    private readonly LocalInventoryDocumentRepository localDocument;
    private readonly AgentLocalSystemIdentity localIdentity;
    private readonly ConcurrentDictionary<int, string> physicalDeviceIds = new();
    private readonly SemaphoreSlim localCaptureGate = new(1, 1);
    private readonly Action<AgentEvent> publish;
    private int startupStarted;

    public AgentInventoryCoordinator(
        IHardwareInventoryProvider manageProvider,
        LocalInventoryDocumentRepository localDocument,
        AgentLocalSystemIdentity localIdentity,
        IPhysicalDiskDeviceResolver deviceResolver,
        Action<AgentEvent>? publish = null)
    {
        this.manageProvider = manageProvider ?? throw new ArgumentNullException(nameof(manageProvider));
        this.localDocument = localDocument ?? throw new ArgumentNullException(nameof(localDocument));
        this.localIdentity = localIdentity ?? throw new ArgumentNullException(nameof(localIdentity));
        this.deviceResolver = deviceResolver ?? throw new ArgumentNullException(nameof(deviceResolver));
        this.publish = publish ?? (_ => { });
    }

    public async Task CaptureStartupAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref startupStarted, 1) != 0) return;
        StorageSystemDocument? startupBatch = null;
        if (manageProvider is AgentInventoryCollector collector)
        {
            try
            {
                startupBatch = await collector.CollectStartupAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                Trace.TraceError("agent.inventory.startup_batch_failed: {0}", exception);
                publish(new AgentInventoryFailedEvent(CollectionPurpose.Hardware, "agent.inventory.startup_failed",
                    DateTimeOffset.UtcNow, IsAutomatic: true, Detail: FormatFailureDetail("inventory.startup", exception)));
                publish(new AgentInventoryFailedEvent(CollectionPurpose.Storage, "agent.inventory.startup_failed",
                    DateTimeOffset.UtcNow, IsAutomatic: true, Detail: FormatFailureDetail("inventory.startup", exception)));
            }
        }
        if (startupBatch is not null)
        {
            var result = await CaptureManageCoreAsync(new(CorrelationId.New(), CollectionPurpose.Storage),
                cancellationToken, true, null, startupBatch).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                // Both pages receive the same persisted observation/body hash.
                // The event reader coalesces their one required body transfer.
                var saved = (await localDocument.LoadAsync(cancellationToken).ConfigureAwait(false))!.Document;
                publish(new AgentInventoryStartedEvent(CollectionPurpose.Hardware, DateTimeOffset.UtcNow, true));
                publish(new AgentInventoryUpdatedEvent(CollectionPurpose.Hardware,
                    new(saved.DocumentId, saved.Sha256, saved.CapturedAtUtc), DateTimeOffset.UtcNow, true));
            }
            return;
        }
        foreach (var purpose in new[] { CollectionPurpose.Storage, CollectionPurpose.Hardware })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (startupBatch is not null)
                    await CaptureManageCoreAsync(new(CorrelationId.New(), purpose), cancellationToken, true, null, startupBatch);
                else if (manageProvider is not AgentInventoryCollector)
                    await CaptureManageAsync(new CaptureAgentManageInventoryRequest(CorrelationId.New(), purpose), cancellationToken, isAutomatic: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // A native provider failure must not fault an unobserved startup task
                // or prevent the independent full-hardware attempt.
                System.Diagnostics.Trace.TraceError("agent.inventory.startup_failed: {0}", exception);
                publish(new AgentInventoryFailedEvent(purpose, "agent.inventory.startup_failed", DateTimeOffset.UtcNow,
                    IsAutomatic: true, Detail: FormatFailureDetail("inventory.startup", exception)));
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

    // A verified Windows post-state is raw evidence from the shared collector.
    // Publishing it must not issue another provider call or feed merged UI facts
    // back into a real preflight.
    public async Task PublishFreshAsync(StorageSystemDocument document, StorageInventoryScope? scope,
        CancellationToken cancellationToken = default)
    {
        if (document.Kind != StorageSystemKind.Local || document.SourceFacts is not { IsSimulation: false, IsMerged: false })
            throw new InvalidDataException("Only raw local post-state facts can refresh inventory.");
        var binding = await localIdentity.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (document.SystemId != binding.SystemId || document.SourceFacts.SystemId != binding.SystemId)
            throw new InvalidDataException("The verified post-state belongs to another local authority.");
        var result = await CaptureManageCoreAsync(new(CorrelationId.New(), CollectionPurpose.Storage),
            cancellationToken, true, scope, document).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InventoryScanException("The verified post-state could not be published.",
                result.Messages.FirstOrDefault()?.DiagnosticText ?? result.Status.ToString());
    }

    private async Task<ApplicationResult<AgentResponse>> CaptureManageCoreAsync(
        CaptureAgentManageInventoryRequest request, CancellationToken cancellationToken, bool isAutomatic,
        StorageInventoryScope? scope, StorageSystemDocument? suppliedDocument = null)
    {
        var queuedAt = Stopwatch.GetTimestamp();
        await localCaptureGate.WaitAsync(cancellationToken);
        var gateAt = Stopwatch.GetTimestamp();
        var failureStage = "inventory.prepare";
        StorageOperationTiming.Record("inventory.queue", queuedAt, scope?.OperationId, scope?.StepId, scope?.Key ?? request.Purpose.ToString());
        try
        {
            if (!Enum.IsDefined(request.Purpose)) return Failed(request.CorrelationId, "agent.inventory.invalid_purpose");
            failureStage = "local_identity.resolve";
            var identity = await localIdentity.ResolveAsync(cancellationToken);
            var canonicalSystemId = identity.SystemId;
            failureStage = "local_document.load";
            var cached = await localDocument.LoadAsync(cancellationToken);
            failureStage = "local_document.decode";
            var previous = cached is null ? null : LocalInventoryDocumentCodec.Decode(cached.Document);
            var generation = checked((previous?.SourceFacts?.Sources.Select(x => x.CaptureGeneration).DefaultIfEmpty(0).Max() ?? 0) + 1);
            if (scope is not null && suppliedDocument is null)
            {
                failureStage = "inventory.scope.resolve";
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
            failureStage = "inventory.provider.collect";
            var document = suppliedDocument ?? (scope is not null
                ? await ((IScopedHardwareInventoryProvider)manageProvider).CollectScopedAsync(scope, cancellationToken)
                : request.Purpose == CollectionPurpose.Hardware
                ? await manageProvider.CollectHardwareAsync(cancellationToken)
                : await manageProvider.CollectLocalAsync(cancellationToken));
            var capturedAt = Stopwatch.GetTimestamp();
            StorageOperationTiming.Record("inventory.collect", captureAt, scope?.OperationId, scope?.StepId, scope?.Key ?? request.Purpose.ToString());
            failureStage = "inventory.capture.validate";
            if (scope is not null && (document.SystemId != canonicalSystemId
                || document.SourceFacts?.ScopedCollection?.Scope != scope || document.SourceFacts.IsMerged))
                throw new InvalidDataException("Scoped inventory returned mismatched or merged evidence.");
            // The verified raw batch keeps its observation generation. This copy
            // joins the display cache's persisted sequence, including its scope
            // marker and source coverage, so it supersedes the previous result.
            var cacheScope = scope is null ? null : scope with { Generation = generation };
            document = document with
            {
                SystemId = canonicalSystemId,
                SourceFacts = document.SourceFacts is null ? null : document.SourceFacts with
                {
                    SystemId = canonicalSystemId,
                    Sources = document.SourceFacts.Sources.Select(x => x with
                    {
                        CaptureGeneration = generation,
                        Coverage = cacheScope is not null && x.Coverage is { } coverage
                            ? coverage with { Generation = generation } : x.Coverage
                    }).ToImmutableArray(),
                    ScopedCollection = cacheScope is not null && document.SourceFacts.ScopedCollection is { } collected
                        ? collected with { Scope = cacheScope } : document.SourceFacts.ScopedCollection
                }
            };
            scope = cacheScope;
            var incomplete = scope is not null && document.SourceFacts?.ScopedCollection?.Complete != true;
            failureStage = "inventory.facts.merge";
            if (previous is not null && document.SourceFacts is not null)
            {
                if (previous.SystemId == canonicalSystemId && previous.SourceFacts is not null)
                    document = document with { SourceFacts = WinPoolFactRefresh.Merge(previous.SourceFacts, document.SourceFacts) };
            }
            failureStage = "inventory.device_ids.cache";
            CachePhysicalDeviceIds(document);
            StorageOperationTiming.Record("inventory.merge", capturedAt, scope?.OperationId, scope?.StepId, scope?.Key ?? request.Purpose.ToString());
            var persistenceAt = Stopwatch.GetTimestamp();
            var snapshotId = Guid.NewGuid();
            failureStage = "inventory.local_document.encode";
            var payload = LocalInventoryDocumentCodec.Encode(document);
            failureStage = "inventory.local_document.save";
            await localDocument.SaveAsync(snapshotId, payload, canonicalSystemId, Environment.MachineName,
                LocalSystemIdentityResolver.CreateAuthorityBinding(Environment.MachineName), cancellationToken);
            StorageOperationTiming.Record("inventory.persist", persistenceAt, scope?.OperationId, scope?.StepId, scope?.Key ?? request.Purpose.ToString());
            Trace.WriteLine(JsonSerializer.Serialize(new { Event = "inventory.coordinator", Purpose = scope is null ? request.Purpose.ToString() : "ScopedStorage",
                OperationId = scope?.OperationId.Value, StepId = scope?.StepId, Scope = scope?.Key, Generation = generation,
                QueueMilliseconds = Stopwatch.GetElapsedTime(queuedAt, gateAt).TotalMilliseconds,
                CaptureMilliseconds = Stopwatch.GetElapsedTime(captureAt, capturedAt).TotalMilliseconds,
                MergeProjectionPersistenceMilliseconds = Stopwatch.GetElapsedTime(capturedAt).TotalMilliseconds,
                Complete = !incomplete }));
            publish(new AgentInventoryUpdatedEvent(request.Purpose, new LocalInventoryDocumentReference(payload.DocumentId, payload.Sha256, payload.CapturedAtUtc), DateTimeOffset.UtcNow, isAutomatic));
            if (incomplete)
                return new(ApplicationStatus.PartiallyCompleted, new ManageInventoryCaptureResponse(snapshotId, payload.DocumentId, payload.Sha256, generation),
                    [new ApplicationMessage("agent.inventory.scope_incomplete", "agent.inventory.scope_incomplete",
                        "The target closure was not completely refreshed; prior facts remain retained.", ApplicationMessageSeverity.Warning, [])], request.CorrelationId);
            return Succeeded(
                new ManageInventoryCaptureResponse(snapshotId, payload.DocumentId, payload.Sha256, generation),
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
                or UnauthorizedAccessException
                or TimeoutException)
        {
            const string code = "agent.inventory.manage_capture_failed";
            var detail = FormatFailureDetail(failureStage, exception);
            publish(new AgentInventoryFailedEvent(request.Purpose, code, DateTimeOffset.UtcNow, isAutomatic, detail));
            return Failed(request.CorrelationId, code, detail);
        }
        finally
        {
            localCaptureGate.Release();
        }
    }

    public async Task<ApplicationResult<AgentResponse>> LoadManageAsync(
        LoadAgentInventoryCacheRequest request,
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

    private static ApplicationResult<AgentResponse> Failed(
        CorrelationId correlationId,
        string code,
        string? diagnostic = null) =>
        ApplicationResult<AgentResponse>.FromStatus(
            ApplicationStatus.Failed,
            correlationId,
            new ApplicationMessage(code, code, diagnostic ?? string.Empty, ApplicationMessageSeverity.Error, []));

    private const int MaximumFailureDetailLength = 2048;

    private static string FormatFailureDetail(string stage, Exception exception)
    {
        var details = new List<string> { $"Stage: {stage}" };
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            details.Add($"{current.GetType().FullName}: {current.Message}");
            if (current is InventoryScanException scanException
                && !string.IsNullOrWhiteSpace(scanException.Diagnostic))
            {
                details.Add($"Inventory diagnostic: {scanException.Diagnostic}");
            }
        }

        var detail = string.Join(Environment.NewLine, details);
        return detail.Length <= MaximumFailureDetailLength
            ? detail
            : detail[..MaximumFailureDetailLength];
    }

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
