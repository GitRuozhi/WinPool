using System.Text.Json;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsRealStorageCommandAdapterTests
{
    [Fact]
    public async Task TypedCommandUsesFixedScriptAndJsonDataOnly()
    {
        var runner = new FakeRunner(new WindowsStorageProcessResult(0, SuccessJson, ""));
        var adapter = new WindowsRealStorageCommandAdapter(runner);
        var name = "pool'; Remove-Disk -Number 0; $x = 1\nUnicode 磁盘";
        var command = new CreatePoolCommand(
            RealTargetReference.ForExisting(new StorageObjectId(SystemId.New(), StorageObjectKind.PhysicalDisk, "stable")),
            name);

        var result = await adapter.ExecuteAsync(command, PhysicalTarget(), CancellationToken.None);

        Assert.Equal("provider.returned", result.Code);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(WindowsRealStoragePowerShellScript.Source, runner.Script);
        Assert.DoesNotContain(name, runner.Script!, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Expression", runner.Script!, StringComparison.OrdinalIgnoreCase);
        using var parsed = JsonDocument.Parse(runner.Payload!);
        Assert.Equal(name, parsed.RootElement.GetProperty("Command").GetProperty("Name").GetString());
        Assert.Equal("CreatePool", parsed.RootElement.GetProperty("CommandKind").GetString());
        Assert.Equal("physical-unique", parsed.RootElement.GetProperty("Target").GetProperty("UniqueId").GetString());
    }

    [Fact]
    public async Task MissingUniqueIdentityAndWrongTargetKindNeverReachRunner()
    {
        var runner = new FakeRunner(new WindowsStorageProcessResult(0, SuccessJson, ""));
        var adapter = new WindowsRealStorageCommandAdapter(runner);
        var disk = RealTargetReference.ForExisting(new StorageObjectId(SystemId.New(), StorageObjectKind.OsDisk, "disk"));
        var command = new InitializeGptCommand(disk);

        var missing = await adapter.ExecuteAsync(command, DiskTarget() with { OsDiskUniqueId = "" }, CancellationToken.None);
        var wrong = await adapter.ExecuteAsync(command, PhysicalTarget(), CancellationToken.None);

        Assert.False(missing.ProviderReturned);
        Assert.False(wrong.ProviderReturned);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task InvalidParameterCombinationNeverReachesRunner()
    {
        var runner = new FakeRunner(new WindowsStorageProcessResult(0, SuccessJson, ""));
        var adapter = new WindowsRealStorageCommandAdapter(runner);
        var pool = RealTargetReference.ForExisting(new StorageObjectId(SystemId.New(), StorageObjectKind.StoragePool, "pool"));
        var invalid = new CreateVirtualDiskCommand(pool, "VD", 16L << 30, 262144, 1);

        var result = await adapter.ExecuteAsync(invalid, PoolTarget(), CancellationToken.None);

        Assert.False(result.ProviderReturned);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task RecoveryFormatRequiresCreatedPartitionAndFourKiBCluster()
    {
        var runner = new FakeRunner(new WindowsStorageProcessResult(0, SuccessJson, ""));
        var adapter = new WindowsRealStorageCommandAdapter(runner);
        var partition = RealTargetReference.ForExisting(
            new StorageObjectId(SystemId.New(), StorageObjectKind.Partition, "partition"));
        var format = new FormatVolumeCommand(partition, RealFileSystem.Ntfs, 4096, false, "REC");
        var recovery = PartitionTarget() with
        {
            PartitionTypeGuid = "de94bba4-06d1-4d40-a16a-bfd50179d6ac",
            CreatedInThisPlan = true
        };

        Assert.False((await adapter.ExecuteAsync(format, recovery with
        {
            CreatedInThisPlan = false
        }, CancellationToken.None)).ProviderReturned);
        Assert.False((await adapter.ExecuteAsync(format with { ClusterBytes = 65536 }, recovery,
            CancellationToken.None)).ProviderReturned);
        Assert.Equal(0, runner.Calls);

        Assert.True((await adapter.ExecuteAsync(format, recovery, CancellationToken.None)).ProviderReturned);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task DriveLetterOnEfiPartitionNeverReachesRunner()
    {
        var runner = new FakeRunner(new WindowsStorageProcessResult(0, SuccessJson, ""));
        var adapter = new WindowsRealStorageCommandAdapter(runner);
        var partition = RealTargetReference.ForExisting(
            new StorageObjectId(SystemId.New(), StorageObjectKind.Partition, "partition"));

        var result = await adapter.ExecuteAsync(new SetDriveLetterCommand(partition, null, 'R'),
            PartitionTarget() with { PartitionTypeGuid = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" },
            CancellationToken.None);

        Assert.False(result.ProviderReturned);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task MissingResultAfterPotentialCallIsOutcomeUnknown()
    {
        var runner = new FakeRunner(new WindowsStorageProcessResult(0, "not-json", "provider details"));
        var adapter = new WindowsRealStorageCommandAdapter(runner);
        var disk = RealTargetReference.ForExisting(new StorageObjectId(SystemId.New(), StorageObjectKind.OsDisk, "disk"));

        var result = await adapter.ExecuteAsync(new InitializeGptCommand(disk), DiskTarget(), CancellationToken.None);

        Assert.True(result.ProviderReturned);
        Assert.Equal("adapter.response-outcome-unknown", result.Code);
        Assert.DoesNotContain("provider details", result.ProviderError!, StringComparison.Ordinal);
    }

    [Fact]
    public void CompressedFixedScriptFitsWindowsCommandLine()
    {
        var encoded = WindowsPowerShellStorageWriteRunner.EncodeFixedCompressedScript(
            WindowsRealStoragePowerShellScript.Source);

        Assert.InRange(encoded.Length, 1, 30000);
    }

    private const string SuccessJson =
        "{\"ProviderReturned\":true,\"Code\":\"provider.returned\",\"UniqueId\":\"new-id\",\"ObjectId\":\"new-object\",\"PartitionGuid\":null,\"DiskNumber\":null,\"PartitionNumber\":null,\"ProviderJobId\":null,\"ProviderError\":null}";

    private static WindowsStorageCommandTarget DiskTarget() =>
        new(StorageObjectKind.OsDisk, "", "", "", "os-unique", "os-path", 7,
            null, "", null, null, "", "physical-unique", "", "fresh-fingerprint");

    private static WindowsStorageCommandTarget PhysicalTarget() =>
        new(StorageObjectKind.PhysicalDisk, "physical-unique", "physical-object", "serial", "", "", null,
            null, "", null, null, "", "physical-unique", "subsystem-unique", "fresh-fingerprint");

    private static WindowsStorageCommandTarget PartitionTarget() =>
        new(StorageObjectKind.Partition, "", "", "", "os-unique", "os-path", 7,
            1, "2f8ae502-1e4e-4d94-b190-6284ccb62bea", 1048576, 1073741824,
            "os-unique", "physical-unique", "", "fresh-fingerprint",
            PartitionTypeGuid: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");

    private static WindowsStorageCommandTarget PoolTarget() =>
        new(StorageObjectKind.StoragePool, "pool-unique", "pool-object", "", "", "", null,
            null, "", null, null, "subsystem-unique", "physical-unique", "subsystem-unique", "fresh-fingerprint");

    private sealed class FakeRunner(WindowsStorageProcessResult response) : IWindowsStorageWriteProcessRunner
    {
        public int Calls { get; private set; }
        public string? Script { get; private set; }
        public string? Payload { get; private set; }

        public Task<WindowsStorageProcessResult> RunAsync(string fixedScript, string payloadJson, CancellationToken cancellationToken)
        {
            Calls++;
            Script = fixedScript;
            Payload = payloadJson;
            return Task.FromResult(response);
        }
    }
}
