using System.Collections.Immutable;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Agent;

/// <summary>
/// Owns raw, non-merged Windows batches. A generation is an explicit observation
/// boundary, never a TTL; callers must advance it across a possible write.
/// </summary>
internal sealed class AgentInventoryCollector(
    IHardwareInventoryProvider provider,
    AgentLocalSystemIdentity identity) : IHardwareInventoryProvider, IScopedHardwareInventoryProvider, IWindowsRealStorageFactSource
{
    private readonly object sync = new();
    private readonly List<PendingBatch> batches = [];
    private VerifiedBatch? verified;
    private Task<StorageSystemDocument>? startupCapture;
    private long generation;
    public bool SupportsScopedCapture => provider is IScopedHardwareInventoryProvider;

    public long BeginGeneration()
    {
        lock (sync)
        {
            batches.Clear();
            verified = null;
            startupCapture = null;
            return ++generation;
        }
    }

    public Task<StorageSystemDocument> CollectLocalAsync(CancellationToken cancellationToken) =>
        CollectAsync(CollectionPurpose.Storage, null, BeginGeneration(), cancellationToken);

    public Task<StorageSystemDocument> CollectHardwareAsync(CancellationToken cancellationToken) =>
        CollectAsync(CollectionPurpose.Hardware, null, BeginGeneration(), cancellationToken);

    public Task<StorageSystemDocument> CollectScopedAsync(StorageInventoryScope scope, CancellationToken cancellationToken) =>
        CollectAsync(CollectionPurpose.Storage, scope, BeginGeneration(), cancellationToken);

    public Task<StorageSystemDocument> CaptureFreshAsync(CancellationToken cancellationToken) =>
        CollectLocalAsync(cancellationToken);

    public Task<StorageSystemDocument> CaptureFreshAsync(StorageInventoryScope scope, CancellationToken cancellationToken) =>
        CollectScopedAsync(scope, cancellationToken);

    public Task<StorageSystemDocument> CollectStartupAsync(CancellationToken cancellationToken)
    {
        lock (sync)
            return startupCapture ??= CollectAsync(CollectionPurpose.Hardware, null, BeginGeneration(), cancellationToken);
    }

    public Task<StorageSystemDocument> CaptureForPreparationAsync(IReadOnlyList<StorageObjectId> targets,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
            if (verified is { } observed && observed.Generation == generation &&
                targets.All(target => target.System == observed.Document.SystemId &&
                    observed.Document.SourceFacts!.Objects.Any(item => item.Id == target.ProviderKey)))
                return Task.FromResult(observed.Document);
        return CaptureFreshAsync(cancellationToken);
    }

    public StorageInventoryScope? ResolveScope(OperationId operationId, string stepId, IReadOnlyList<StorageObjectId> targets)
    {
        lock (sync)
            if (SupportsScopedCapture && verified is { } observed && observed.Generation == generation &&
                targets.All(target => target.System == observed.Document.SystemId &&
                    observed.Document.SourceFacts!.Objects.Any(item => item.Id == target.ProviderKey)))
                return StorageInventoryScopeFactory.Create(observed.Document.SourceFacts!, operationId, stepId, targets);
        return null;
    }

    public void AcceptVerifiedPostState(StorageSystemDocument document, StorageInventoryScope? scope)
    {
        if (document.Kind != StorageSystemKind.Local || document.SourceFacts is not { IsSimulation: false, IsMerged: false } facts
            || facts.SystemId != document.SystemId || scope is not null && scope.SystemId != document.SystemId)
            throw new InvalidDataException("Only consistent raw local post-state evidence can be shared.");
        var observedGeneration = facts.Sources.Select(x => x.CaptureGeneration).DefaultIfEmpty(0).Max();
        lock (sync)
            if (observedGeneration == generation) verified = new(document, scope, observedGeneration);
    }

    // Monitoring is an observation consumer. It can reuse a verified post-state;
    // a real preflight always calls CaptureFreshAsync and advances generation.
    public Task<StorageSystemDocument> CollectForMonitorAsync(StorageInventoryScope? scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifiedBatch? current;
        string reason;
        lock (sync)
        {
            current = verified;
            reason = current is null ? "no_verified_post_state" : current.Generation != generation ? "generation_changed"
                : scope is not null && scope.SystemId != current.Document.SystemId ? "binding_changed"
                : !CoversVerified(current, scope) ? "scope_not_covered"
                : !IsComplete(current.Document, CollectionPurpose.Storage) ? "source_incomplete" : "";
        }
        if (reason.Length == 0) return Task.FromResult(Project(current!.Document, scope));
        StorageOperationTiming.Record("monitor.identity.coverage_miss", System.Diagnostics.Stopwatch.GetTimestamp(),
            scope?.OperationId, scope?.StepId, reason);
        return scope is null ? CollectLocalAsync(cancellationToken) : CollectScopedAsync(scope, cancellationToken);
    }

    /// <summary>Concurrent consumers may explicitly share a generation and its complete coverage.</summary>
    public async Task<StorageSystemDocument> CollectAsync(CollectionPurpose purpose, StorageInventoryScope? scope,
        long requestedGeneration, CancellationToken cancellationToken)
    {
        scope?.Validate();
        var binding = await identity.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (scope is not null && scope.SystemId != binding.SystemId)
            throw new InvalidDataException("The inventory scope belongs to a different local authority.");
        PendingBatch batch;
        bool reused;
        lock (sync)
        {
            if (requestedGeneration <= 0 || requestedGeneration > generation)
                throw new InvalidOperationException("The inventory generation was not issued by this collector.");
            var existing = requestedGeneration == generation
                ? batches.FirstOrDefault(x => Covers(x.Purpose, x.Scope, purpose, scope)) : null;
            reused = existing is not null;
            batch = existing ?? new(purpose, scope, CaptureCoreAsync(purpose, scope, requestedGeneration, cancellationToken));
            if (!reused && requestedGeneration == generation) batches.Add(batch);
        }
        var document = await batch.Capture.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (reused && IsComplete(document, purpose)) return Project(document, scope);
        if (reused)
        {
            // Incomplete evidence belongs to the original caller. A later scope
            // request obtains its own fresh closure rather than claiming coverage.
            lock (sync) batches.Remove(batch);
            return await CollectAsync(purpose, scope, requestedGeneration, cancellationToken).ConfigureAwait(false);
        }
        if (IsComplete(document, purpose))
        {
            lock (sync)
                if (requestedGeneration == generation) verified = new(document, scope, requestedGeneration);
        }
        return document;
    }

    private async Task<StorageSystemDocument> CaptureCoreAsync(CollectionPurpose purpose, StorageInventoryScope? scope,
        long captureGeneration, CancellationToken cancellationToken)
    {
        var binding = await identity.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (scope is not null && scope.SystemId != binding.SystemId)
            throw new InvalidDataException("The inventory scope belongs to a different local authority.");
        var document = scope is not null
            ? provider is IScopedHardwareInventoryProvider scoped
                ? await scoped.CollectScopedAsync(scope, cancellationToken).ConfigureAwait(false)
                : throw new NotSupportedException("The Windows collector does not support scoped capture.")
            : purpose == CollectionPurpose.Hardware
                ? await provider.CollectHardwareAsync(cancellationToken).ConfigureAwait(false)
                : await provider.CollectLocalAsync(cancellationToken).ConfigureAwait(false);
        if (document.Kind != StorageSystemKind.Local || document.SourceFacts is not { IsSimulation: false, IsMerged: false } facts
            || facts.SystemId != document.SystemId || !StringComparer.OrdinalIgnoreCase.Equals(
                document.Snapshot.Computer.Name, Environment.MachineName))
            throw new InvalidDataException("The Windows collector returned inconsistent local raw facts.");
        return document with
        {
            SystemId = binding.SystemId,
            SourceFacts = facts with
            {
                SystemId = binding.SystemId,
                ObservationId = Guid.NewGuid(),
                Sources = facts.Sources.Select(x => x with { CaptureGeneration = captureGeneration }).ToImmutableArray()
            }
        };
    }

    private static bool Covers(CollectionPurpose existingPurpose, StorageInventoryScope? existing,
        CollectionPurpose purpose, StorageInventoryScope? requested) =>
        (existingPurpose == CollectionPurpose.Hardware || purpose == CollectionPurpose.Storage)
        && (existing is null || requested is not null && existing.SystemId == requested.SystemId
            && WindowsRealStorageReadProfile.Covers(existing.EvidenceProfile, requested.EvidenceProfile)
            && existing.BeforeObjectIds.Intersect(requested.BeforeObjectIds, StringComparer.Ordinal).Count() == requested.BeforeObjectIds.Count
            && existing.Targets.Intersect(requested.Targets).Count() == requested.Targets.Count);

    private static bool CoversVerified(VerifiedBatch batch, StorageInventoryScope? scope)
    {
        if (batch.Scope is null) return true;
        if (scope is null || batch.Scope.SystemId != scope.SystemId
            || !WindowsRealStorageReadProfile.Covers(batch.Scope.EvidenceProfile, scope.EvidenceProfile)) return false;
        // The exact closure also covers newly created objects observed after the
        // write. BeforeObjectIds alone cannot name those output identities.
        var covered = batch.Scope.BeforeObjectIds.Concat(batch.Document.SourceFacts!.Objects.Select(x => x.Id))
            .ToHashSet(StringComparer.Ordinal);
        return scope.BeforeObjectIds.All(covered.Contains) && scope.Targets.All(x => covered.Contains(x.ProviderKey));
    }

    private static bool IsComplete(StorageSystemDocument document, CollectionPurpose purpose)
    {
        var facts = document.SourceFacts!;
        if (facts.ScopedCollection is { Complete: false }) return false;
        string[] required = facts.ScopedCollection?.Scope.EvidenceProfile is not null
            ? facts.Sources.Where(source => source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)).Select(source => source.ClassName).Distinct().ToArray()
            : ["MSFT_StorageSubSystem", "MSFT_StoragePool", "MSFT_PhysicalDisk", "MSFT_VirtualDisk",
                "MSFT_StorageTier", "MSFT_Disk", "MSFT_Partition", "MSFT_Volume"];
        return required.All(name => facts.Sources.Any(x => x.ClassName == name && x.ReadState == FieldReadState.Returned))
            && facts.Sources.Where(x => purpose == CollectionPurpose.Hardware || x.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                || x.ClassName is "Windows.DiskRoles" or "Windows.StorageProviderDiscovery" or "Win32_DiskDrive")
                .All(x => x.ReadState is FieldReadState.Returned or FieldReadState.Unavailable);
    }

    private static StorageSystemDocument Project(StorageSystemDocument document, StorageInventoryScope? scope)
    {
        if (scope is null) return document;
        var facts = document.SourceFacts!;
        var present = scope.Targets.Where(target => facts.Objects.Any(x => x.Id == target.ProviderKey)).ToArray();
        IReadOnlyList<string> currentIds = present.Length == 0 ? [] : StorageInventoryScopeFactory.Create(facts,
            scope.OperationId, scope.StepId, present, scope.Generation).BeforeObjectIds;
        var ids = scope.BeforeObjectIds.Concat(currentIds).ToHashSet(StringComparer.Ordinal);
        // Keep the selected member's observed availability pool and its exact
        // subsystem parent without traversing that pool to unrelated siblings.
        foreach (var edge in facts.Relationships.Where(edge => edge.Kind == "pool-member" && ids.Contains(edge.ToId)))
            ids.Add(edge.FromId);
        foreach (var edge in facts.Relationships.Where(edge => edge.Kind == "subsystem-pool" && ids.Contains(edge.ToId)))
            ids.Add(edge.FromId);
        var objects = facts.Objects.Where(x => ids.Contains(x.Id)
            || x.ObjectType is FactObjectType.Computer or FactObjectType.OperatingSystem or FactObjectType.LogicalDisk or FactObjectType.NetworkDisk)
            .ToImmutableArray();
        var actualIds = objects.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var coverage = ids.Order(StringComparer.Ordinal).ToImmutableArray();
        return document with { SourceFacts = facts with
        {
            Sources = facts.Sources.Select(x => x.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                || x.ClassName is "Windows.DiskRoles" or "Win32_DiskDrive"
                ? x with { Coverage = new(scope.Key, coverage, x.ReadState == FieldReadState.Returned, scope.Generation)
                    { FieldNames = x.Coverage?.FieldNames } } : x).ToImmutableArray(),
            Objects = objects,
            Identities = facts.Identities.Where(x => actualIds.Contains(x.ObjectId)).ToImmutableArray(),
            Relationships = facts.Relationships.Where(x => actualIds.Contains(x.FromId) && actualIds.Contains(x.ToId)).ToImmutableArray(),
            Collections = [],
            ScopedCollection = new(scope, facts.InventoryCapturedAt, facts.InventoryCapturedAt, true)
        } };
    }

    private sealed record PendingBatch(CollectionPurpose Purpose, StorageInventoryScope? Scope, Task<StorageSystemDocument> Capture);
    private sealed record VerifiedBatch(StorageSystemDocument Document, StorageInventoryScope? Scope, long Generation);
}
