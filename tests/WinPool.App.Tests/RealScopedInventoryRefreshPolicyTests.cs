using System.Text.Json;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Tests;

public sealed class RealScopedInventoryRefreshPolicyTests
{
    [Theory]
    [InlineData("inventory.scope.incomplete")]
    [InlineData("The Agent local inventory document hash is invalid.")]
    public async Task InvalidScopedDataReturnsFailureAndReportsTheExactReason(string reason)
    {
        var reports = new List<InvalidDataException>();
        var error = new InvalidDataException(reason);
        var result = await RealScopedInventoryRefreshPolicy.GuardInvalidDataAsync(
            () => Task.FromException<bool>(error), reports.Add);
        Assert.False(result);
        Assert.Same(error, Assert.Single(reports));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BoundaryPreservesACompletedRefreshResult(bool completed)
    {
        Assert.Equal(completed, await RealScopedInventoryRefreshPolicy.GuardInvalidDataAsync(
            () => Task.FromResult(completed), _ => Assert.Fail("A completed refresh is not an invalid-data failure.")));
    }

    [Fact]
    public async Task BoundaryDoesNotSwallowUnrelatedProgrammingErrors()
    {
        await Assert.ThrowsAsync<NullReferenceException>(() => RealScopedInventoryRefreshPolicy.GuardInvalidDataAsync(
            () => Task.FromException<bool>(new NullReferenceException()), _ => Assert.Fail("Unexpected report.")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObserverAndReplyMayApplyTheExactCompleteBatchInEitherOrder(bool observerFirst)
    {
        var (batch, requested) = Batch();
        var catalog = new StorageSystemCatalog();
        var observer = RoundTrip(batch);
        var reply = RoundTrip(batch);
        Assert.True(catalog.TryReplaceLocalReport(observerFirst ? observer : reply));
        // The second delivery is deliberately rejected by the monotonic catalog.
        Assert.False(catalog.TryReplaceLocalReport(observerFirst ? reply : observer));
        Assert.True(RealScopedInventoryRefreshPolicy.IsCurrentCompleteBatch(catalog.Systems.Single(), reply, requested));
    }

    [Theory]
    [InlineData("different-operation")]
    [InlineData("different-step")]
    [InlineData("different-generation")]
    [InlineData("different-capture")]
    [InlineData("different-snapshot")]
    [InlineData("retained-cache")]
    [InlineData("source-evidence")]
    public void RejectedReplyCannotAuthorizeContinuationFromAnotherCurrentBatch(string difference)
    {
        var (reply, requested) = Batch();
        var facts = reply.SourceFacts!;
        var collection = facts.ScopedCollection!;
        var currentFacts = difference switch
        {
            "different-operation" => facts with { ScopedCollection = collection with
                { Scope = collection.Scope with { OperationId = OperationId.New() } } },
            "different-step" => facts with { ScopedCollection = collection with
                { Scope = collection.Scope with { StepId = "another-segment" } } },
            "different-generation" => facts with { ScopedCollection = collection with
                { Scope = collection.Scope with { Generation = collection.Scope.Generation + 1 } } },
            "different-capture" => facts with { InventoryCapturedAt = facts.InventoryCapturedAt.AddMilliseconds(1) },
            "different-snapshot" => facts with { InventoryVersion = "another-snapshot" },
            "retained-cache" => facts with { ScopedCollection = collection with { Complete = false, ReasonCode = "retained" } },
            "source-evidence" => facts with { IsMerged = !facts.IsMerged },
            _ => throw new ArgumentException(difference)
        };
        var active = reply with { SourceFacts = currentFacts, UpdatedAt = reply.UpdatedAt.AddSeconds(1) };
        var catalog = new StorageSystemCatalog();
        Assert.True(catalog.TryReplaceLocalReport(active));
        Assert.False(catalog.TryReplaceLocalReport(reply));
        Assert.False(RealScopedInventoryRefreshPolicy.IsCurrentCompleteBatch(catalog.Systems.Single(), reply, requested));
        // Even a timestamp collision must not turn different source batches into the same report.
        Assert.False(RealScopedInventoryRefreshPolicy.IsCurrentCompleteBatch(active with { UpdatedAt = reply.UpdatedAt }, reply, requested));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingCacheMustStillBeCompleteAndBelongToThisRequest(bool wrongRequest)
    {
        var (reply, requested) = Batch();
        if (wrongRequest) requested = requested with { OperationId = OperationId.New() };
        else reply = reply with { SourceFacts = reply.SourceFacts! with
            { ScopedCollection = reply.SourceFacts!.ScopedCollection! with { Complete = false } } };
        Assert.False(RealScopedInventoryRefreshPolicy.IsCurrentCompleteBatch(reply, reply, requested));
    }

    private static StorageSystemDocument RoundTrip(StorageSystemDocument value) =>
        JsonSerializer.Deserialize<StorageSystemDocument>(JsonSerializer.Serialize(value))!;

    private static (StorageSystemDocument, StorageInventoryScope) Batch()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var document = new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion,
            "local:refresh-race", StorageSystemKind.Local, "Refresh race", StorageSnapshot.Empty("Refresh race"), [], at);
        var physical = new StorageObjectId(document.SystemId, StorageObjectKind.PhysicalDisk, "physical:exact");
        var requested = new StorageInventoryScope(document.SystemId, OperationId.New(), "result",
            [physical], [physical.ProviderKey], [new(physical, "MSFT_PhysicalDisk", "UniqueId", "exact-uid")]);
        var captured = requested with { Generation = 42 };
        return (document with { SourceFacts = document.SourceFacts! with
        {
            InventoryVersion = "same-snapshot", InventoryCapturedAt = at,
            ScopedCollection = new(captured, at.AddSeconds(-1), at, true)
        } }, requested);
    }
}
