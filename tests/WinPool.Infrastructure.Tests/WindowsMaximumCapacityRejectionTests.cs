using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsMaximumCapacityRejectionTests
{
    private static readonly RealStorageCommand Command = new CreateVirtualDiskCommand(
        RealTargetReference.ForExisting(new(SystemId.New(), StorageObjectKind.StoragePool, "pool")), "vd", 32L << 30, 65536, 1);

    private static WindowsStorageCommandResult Error(uint code, string? reason = null) => new(true,
        "provider.error-outcome-unknown", null, null, null, null, null, null, "original provider error",
        ProviderFailure: new(code, "cdxml-storagewmi-error-id", $"StorageWMI {code},New-VirtualDisk", 1, -1,
            "MSFT_WmiError", "raw evidence", 7, code, "StorageWMI", reason));

    [Fact]
    public void ExactLocalEligibleResourceReasonRequiresKnownLayoutAndStructuredIdentity()
    {
        var error = Error(1, "Not Supported\r\n\r\nExtended information:\r\n" + WindowsRealStorageBackend.EligibleResourceCapacityReason + "\r\n");
        Assert.True(WindowsRealStorageBackend.IsExplicitMaximumCapacityRejection(Command, error, true));
        Assert.False(WindowsRealStorageBackend.IsExplicitMaximumCapacityRejection(Command, error));
        Assert.False(WindowsRealStorageBackend.IsExplicitMaximumCapacityRejection(Command,
            error with { ProviderFailure = error.ProviderFailure! with { ErrorCategory = 6 } }, true));
    }

    [Theory]
    [InlineData(1, "Not Supported")]
    [InlineData(40002, "Insufficient resources")]
    [InlineData(2, "The storage pool does not have sufficient eligible resources for the creation of the specified virtual disk.")]
    public void OtherProviderErrorsAreNeverCapacityBoundaries(uint code, string reason) =>
        Assert.False(WindowsRealStorageBackend.IsExplicitMaximumCapacityRejection(Command, Error(code, reason), true));

    [Fact]
    public void ReturnedOutputOrJobCannotBeClassifiedAsUnchangedCapacityFailure()
    {
        var error = Error(40000);
        Assert.True(WindowsRealStorageBackend.IsExplicitMaximumCapacityRejection(Command, error));
        Assert.False(WindowsRealStorageBackend.IsExplicitMaximumCapacityRejection(Command, error with { UniqueId = "partial-object" }));
        Assert.False(WindowsRealStorageBackend.IsExplicitMaximumCapacityRejection(Command, error with { ProviderJobId = 7 }));
        Assert.False(WindowsRealStorageBackend.IsExplicitMaximumCapacityRejection(Command, error with { ProviderReturned = false }));
    }

    [Fact]
    public void FixedScriptHasOnlyExplicitCandidateWritesAndGrowOnlyGuards()
    {
        Assert.DoesNotContain("-UseMaximumSize", WindowsRealStoragePowerShellScript.Source);
        Assert.Contains("maximum-requires-journaled-explicit-attempt", WindowsRealStoragePowerShellScript.Source);
        Assert.Contains("resize-not-approved-integer-gib-grow", WindowsRealStoragePowerShellScript.Source);
        Assert.Contains("cannot-resize-pool-template", WindowsRealStoragePowerShellScript.Source);
        Assert.Contains("resize-requires-safe-raw-disk", WindowsRealStoragePowerShellScript.Source);
    }
}
