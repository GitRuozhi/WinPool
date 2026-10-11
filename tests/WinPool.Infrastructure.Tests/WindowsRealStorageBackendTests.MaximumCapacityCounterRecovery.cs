using System.Collections.Immutable;
using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed partial class WindowsRealStorageBackendTests
{
    [Theory]
    [InlineData(3, "none", true)]
    [InlineData(6, "none", true)]
    [InlineData(3, "physical-counter", true)]
    [InlineData(3, "physical-read-state", false)]
    [InlineData(3, "physical-missing-counter", false)]
    [InlineData(3, "pool-counter", false)]
    [InlineData(3, "tier-counter", false)]
    [InlineData(3, "physical-identity", false)]
    public async Task MaximumRecoverySharesLiveCounterCanonicalizationAndPreservesSafetyFacts(
        int supportedGib, string alteration, bool expectedVerified)
    {
        var setup = await MaximumFixtureAsync(supportedGib, physicalAllocationCounter: true);
        var result = await setup.Fixture.Backend.ExecuteMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            setup.Preflight, setup.Journal, default);
        Assert.Equal(RealStepOutcome.Verified, result.Outcome);
        Assert.Equal("real.maximum.integer_gib_boundary_verified", result.Code);
        // Production reconciliation reads durable JSON, including quoted provider
        // strings and nullable fields, after the parent has already verified.
        var rows = JsonSerializer.Deserialize<MaximumCapacityAttemptRecord[]>(JsonSerializer.Serialize(setup.Journal.Records))!;
        var record = rows[0];
        var receipt = JsonSerializer.Deserialize<WindowsMaximumCapacityAttemptEvidence>(record.Result!.ResultEvidenceJson)!;
        if (alteration != "none")
        {
            var objectType = alteration switch
            {
                "pool-counter" => FactObjectType.StoragePool,
                "tier-counter" => FactObjectType.StorageTier,
                _ => FactObjectType.PhysicalDisk
            };
            receipt = receipt with { Before = receipt.Before with { Objects = receipt.Before.Objects.Select(item =>
            {
                if (item.ObjectType != objectType) return item;
                var fieldName = alteration == "physical-identity" ? "UniqueId" : "AllocatedSize";
                return item with { Fields = item.Fields.Where(field =>
                        alteration != "physical-missing-counter" || field.Name != fieldName)
                    .Select(field => field.Name != fieldName ? field : alteration == "physical-read-state"
                        ? field with { ReadState = FieldReadState.NotCollected }
                        : field with { Value = alteration == "physical-identity"
                            ? JsonSerializer.SerializeToElement("foreign-identity") : JsonSerializer.SerializeToElement(99L) })
                    .ToImmutableArray() };
            }).ToArray() } };
            rows[0] = record with { Result = record.Result! with { ResultEvidenceJson = JsonSerializer.Serialize(receipt) } };
        }
        var calls = setup.Fixture.Adapter.CallCount;
        var originalRows = JsonSerializer.Serialize(rows);
        var recovered = await setup.Fixture.Backend.ReconcileMaximumCapacitySearchAsync(setup.Plan, setup.Step,
            new(setup.Step.Id, RealOperationStepState.Verified, result.Code, setup.Preflight.TargetEvidenceJson,
                result.ResultEvidenceJson), rows, default);
        Assert.True(recovered.State == (expectedVerified ? RealOperationState.Succeeded : RealOperationState.OutcomeUnknown), recovered.Code);
        Assert.Equal(expectedVerified, recovered.CanReleaseWriteBarrier);
        Assert.Equal(calls, setup.Fixture.Adapter.CallCount);
        Assert.Equal(originalRows, JsonSerializer.Serialize(rows));
        if (expectedVerified)
        {
            var evidence = JsonSerializer.Deserialize<WindowsVerifiedStepEvidence>(Assert.Single(recovered.Steps).ResultEvidence!)!;
            Assert.Equal(supportedGib * MaximumCapacityAlgorithm.GiB, evidence.MaximumCapacity!.ActualSizeBytes);
        }
    }
}
