using System.Collections.Immutable;
using System.Text.Json;
using System.Diagnostics;
using System.Text;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsRealStorageReadProfileTests
{
    [Theory]
    [InlineData(null, WindowsRealStorageReadProfile.Os, null)]
    [InlineData(WindowsRealStorageReadProfile.Os, WindowsRealStorageReadProfile.Required, WindowsRealStorageReadProfile.Required)]
    [InlineData(WindowsRealStorageReadProfile.Required, null, null)]
    [InlineData(WindowsRealStorageReadProfile.OsResize, WindowsRealStorageReadProfile.Required, null)]
    [InlineData(WindowsRealStorageReadProfile.Required, WindowsRealStorageReadProfile.OsResize, null)]
    public void ProfileTransitionObservationMustCoverBothOldAndNewEvidence(string? previous, string? next, string? expected)
    {
        var observed = WindowsRealStorageReadProfile.CoveringBoth(previous, next);
        Assert.Equal(expected, observed);
        Assert.True(WindowsRealStorageReadProfile.Covers(observed, previous));
        Assert.True(WindowsRealStorageReadProfile.Covers(observed, next));
        Assert.True(WindowsRealStorageReadProfile.Covers(null, next));
    }

    [Fact]
    public async Task FixedFullHardwareScopedAndHostTemplatesParseWithoutExecutingStorageCommands()
    {
        var system = SystemId.New();
        var target = new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "physical");
        var scope = new StorageInventoryScope(system, OperationId.New(), "rename", [target], ["physical"],
            [new(target, "MSFT_PhysicalDisk", "UniqueId", "uid'\\selector")]) { EvidenceProfile = WindowsRealStorageReadProfile.Metadata };
        var scripts = new[] { EmbeddedStorageInventoryScript.ForPurpose(CollectionPurpose.Storage),
            EmbeddedStorageInventoryScript.Source, EmbeddedStorageInventoryScript.ForScope(scope), EmbeddedStorageInventoryScript.HostTemplate };
        const string harness = """
$ErrorActionPreference='Stop'
$scripts = [Console]::In.ReadToEnd() | ConvertFrom-Json
foreach ($text in $scripts) {
    $tokens = $null; $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseInput([string]$text, [ref]$tokens, [ref]$errors)
    if (@($errors).Count -ne 0) { throw (@($errors | ForEach-Object { $_.Message }) -join '; ') }
}
'parsed'
""";
        var start = new ProcessStartInfo(WindowsPowerShellRunner.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(PowerShellFixtureScriptCodec.EncodeFixedCompressedScript(
            "[Console]::InputEncoding=[Text.Encoding]::UTF8; [Console]::OutputEncoding=[Text.Encoding]::UTF8;\n" + harness));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(scripts)); process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        Assert.Equal("parsed", (await output).Trim());
    }

    [Fact]
    public void WholePlanUsesTheUnionAndMaximumRetainsFullComponentEvidence()
    {
        var system = SystemId.New();
        RealTargetReference Target(StorageObjectKind kind) => RealTargetReference.ForExisting(new(system, kind, "target"));
        RealOperationStep Step(string id, RealStorageCommand command) => new(id, command, [], "before", "after", "", "support");
        var rename = Step("rename", new RenameVolumeCommand(Target(StorageObjectKind.Volume), "name"));
        var resize = Step("resize", new ResizePartitionCommand(Target(StorageObjectKind.Partition), 1024));
        Assert.Equal(WindowsRealStorageReadProfile.Metadata, WindowsRealStorageReadProfile.ForSteps([rename])!.Id);
        Assert.Equal(WindowsRealStorageReadProfile.OsResize, WindowsRealStorageReadProfile.ForSteps([rename, resize])!.Id);
        var offline = Step("offline", new SetDiskOnlineCommand(Target(StorageObjectKind.OsDisk), false));
        Assert.Equal(WindowsRealStorageReadProfile.Os, WindowsRealStorageReadProfile.ForSteps([offline])!.Id);
        var maximum = Step("maximum", new CreateVirtualDiskCommand(Target(StorageObjectKind.StoragePool), "max", 0, 65536, 1, UseMaximumSize: true));
        Assert.Null(WindowsRealStorageReadProfile.ForSteps([maximum]));
        Assert.True(WindowsRealStorageReadProfile.Covers(null, WindowsRealStorageReadProfile.Resize));
        Assert.True(WindowsRealStorageReadProfile.Covers(WindowsRealStorageReadProfile.Resize, WindowsRealStorageReadProfile.Metadata));
        Assert.False(WindowsRealStorageReadProfile.Covers(WindowsRealStorageReadProfile.Required, null));
        Assert.False(WindowsRealStorageReadProfile.Covers(WindowsRealStorageReadProfile.Metadata, WindowsRealStorageReadProfile.Resize));
    }

    [Fact]
    public void SimpleMetadataIgnoresUnrelatedCapacityButKeepsAllWindowsProtectionAndIdentity()
    {
        var profile = WindowsRealStorageReadProfile.ForId(WindowsRealStorageReadProfile.Metadata)!;
        Assert.False(profile.RequiresProjectedField(FactObjectType.PhysicalDisk, "CanPool"));
        Assert.False(profile.RequiresProjectedField(FactObjectType.StoragePool, "AllocatedSize"));
        Assert.False(profile.RequiresProjectedField(FactObjectType.VirtualDisk, "NumberOfColumns"));
        foreach (var name in new[] { "SerialNumber", "IsBoot", "IsSystem", "IsPageFile", "IsCrashDump", "Usage", "HealthStatus" })
            Assert.True(profile.RequiresProjectedField(FactObjectType.PhysicalDisk, name));
        foreach (var name in new[] { "IsBoot", "IsSystem", "IsReadOnly", "IsOffline", "IsClustered", "Path", "Guid" })
            Assert.True(profile.IncludesFingerprintField("MSFT_Disk", name));
        Assert.True(profile.IncludesFingerprintField("MSFT_Partition", "AccessPaths"));
        Assert.True(profile.IncludesFingerprintField("MSFT_Volume", "AllocationUnitSize"));
    }

    [Theory]
    [InlineData(WindowsRealStorageReadProfile.Os, "8aa08e96e9c2c77e25b9f260783c0b77d76d0b6ea975764205162761b80e8b5c")]
    [InlineData(WindowsRealStorageReadProfile.OsResize, "679c8c63241e14c75e0a23f625e2897a9d617869886bcaf8e5fd05ed195fb7f3")]
    [InlineData(WindowsRealStorageReadProfile.Metadata, "106ab56fde0bd993a28a56aee7941a273b3b20404eb8fdd3b7dc2089635228db")]
    public void ProjectionDependenciesPreserveFrozenV1FingerprintsWithoutMaskingOriginalSafetyChanges(string profileId, string frozenHash)
    {
        var profile = WindowsRealStorageReadProfile.ForId(profileId)!;
        WinPoolSourceField Text(string name, string value) => WinPoolSourceField.Returned(name, value, FactValueType.String, "source");
        WinPoolSourceField Number(string name, long value) => WinPoolSourceField.Returned(name, value, FactValueType.Int64, "source");
        WinPoolSourceField Flag(string name, bool value) => WinPoolSourceField.Returned(name, value, FactValueType.Boolean, "source");
        WinPoolSourceObject[] original =
        [
            new("physical", FactObjectType.PhysicalDisk, "source", "physical-source", true,
            [Text("UniqueId", "physical-uid"), Text("ObjectId", "physical-object"), Text("FriendlyName", "HDD"),
                Text("SerialNumber", "serial"), Number("Size", 1000000), Flag("IsBoot", false), Flag("IsSystem", false),
                Flag("IsPageFile", false), Flag("IsCrashDump", false)]),
            new("virtual", FactObjectType.VirtualDisk, "source", "virtual-source", true,
            [Text("UniqueId", "virtual-uid"), Text("ObjectId", "virtual-object"), Text("FriendlyName", "VD")]),
            new("disk", FactObjectType.Disk, "source", "disk-source", true,
            [Text("UniqueId", "disk-uid"), Text("ObjectId", "disk-object"), Text("FriendlyName", "VD"), Number("Size", 1000),
                Number("PartitionStyle", 0), Flag("IsBoot", false), Flag("IsSystem", false), Flag("IsOffline", false),
                Flag("IsReadOnly", false), Flag("IsClustered", false)])
        ];
        // Frozen hashes above predate the projection dependency additions. The
        // same original safety fields must retain their persisted v1 encoding.
        string Hash(IReadOnlyList<WinPoolSourceObject> values) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
                WindowsRealStorageTopology.CanonicalComponent("machine", values, [],
                    returnedNullAsJsonNull: true, powerShellStrings: true, profile: profile)))).ToLowerInvariant();
        Assert.Equal(frozenHash, Hash(original));
        WinPoolSourceField[] virtualDependencies =
        [Number("Size", 1000), Number("AllocatedSize", 1000), Number("FootprintOnPool", 1000), Number("WriteCacheSize", 0),
            Text("ResiliencySettingName", "Simple"), Number("ProvisioningType", 2), Number("NumberOfColumns", 1),
            Number("Interleave", 65536), Number("NumberOfDataCopies", 1), Number("PhysicalDiskRedundancy", 0)];
        var enriched = original.Select(item => item.Id switch
        {
            "physical" => item with { Fields = item.Fields.Add(Number("MediaType", 3)) },
            "virtual" => item with { Fields = item.Fields.AddRange(virtualDependencies) },
            "disk" => item with { Fields = item.Fields.Add(Text("SerialNumber", "serial")) },
            _ => item
        }).ToArray();
        Assert.Equal(frozenHash, Hash(enriched));
        foreach (var (id, changed) in new[]
        {
            ("physical", Text("UniqueId", "replacement-uid")),
            ("physical", Text("SerialNumber", "replacement-serial")),
            ("disk", Flag("IsBoot", true)),
            ("virtual", Text("FriendlyName", "outside-rename")),
            ("disk", Number("PartitionStyle", 2))
        })
        {
            var changedFacts = enriched.Select(item => item.Id == id ? item with
            {
                Fields = item.Fields.Select(field => field.Name == changed.Name ? changed : field).ToImmutableArray()
            } : item).ToArray();
            Assert.NotEqual(frozenHash, Hash(changedFacts));
        }
    }

    [Theory]
    [InlineData(WindowsRealStorageReadProfile.Required)]
    [InlineData(WindowsRealStorageReadProfile.Resize)]
    public void NativeDiskSerialReadDependencyKeepsFrozenDomainAndPhysicalSerialIdentity(string profileId)
    {
        var profile = WindowsRealStorageReadProfile.ForId(profileId)!;
        WinPoolSourceField Serial(string value) => WinPoolSourceField.Returned("SerialNumber", value, FactValueType.String, "source");
        WinPoolSourceObject[] original =
        [
            new("physical", FactObjectType.PhysicalDisk, "source", "physical-source", true, [Serial("physical-serial")]),
            new("disk", FactObjectType.Disk, "source", "disk-source", true,
                [WinPoolSourceField.Returned("UniqueId", "disk-uid", FactValueType.String, "source")])
        ];
        string Hash(IReadOnlyList<WinPoolSourceObject> values) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
                WindowsRealStorageTopology.CanonicalComponent("machine", values, [],
                    returnedNullAsJsonNull: true, powerShellStrings: true, profile: profile))));
        var frozenHash = Hash(original);
        var enriched = new[] { original[0], original[1] with { Fields = original[1].Fields.Add(Serial("physical-serial")) } };
        Assert.Equal(frozenHash, Hash(enriched));
        Assert.Equal(frozenHash, Hash([enriched[0], enriched[1] with
            { Fields = enriched[1].Fields.SetItem(1, Serial("changed-os-serial")) }]));
        Assert.NotEqual(frozenHash, Hash([enriched[0] with { Fields = [Serial("changed-physical-serial")] }, enriched[1]]));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ExplicitClassAndFieldCoverageDoesNotInventAnUnqueriedEmptyClass(bool missingQueriedClass, bool complete)
    {
        var system = SystemId.New();
        var target = new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "physical");
        var scope = new StorageInventoryScope(system, OperationId.New(), "rename", [target], [target.ProviderKey],
            [new(target, "MSFT_PhysicalDisk", "UniqueId", "provider")]) { EvidenceProfile = WindowsRealStorageReadProfile.Metadata };
        string[] queried = ["MSFT_StorageSubSystem", "MSFT_PhysicalDisk", "MSFT_StoragePool", "MSFT_Disk"];
        var json = JsonSerializer.Serialize(new
        {
            ScannedAt = DateTimeOffset.UtcNow, Computer = new { Name = Environment.MachineName },
            ScopeQueriedClasses = missingQueriedClass ? queried.Append("MSFT_Partition").ToArray() : queried,
            ScopeClosureComplete = true,
            SourceQuerySuccesses = queried.Select(name => new { ClassName = name, Namespace = "root/Microsoft/Windows/Storage" }),
            SourceObservations = Array.Empty<object>()
        });
        var document = await new WindowsHardwareInventoryProvider(new ClosedRunner(json)).CollectScopedAsync(scope, default);
        Assert.Equal(complete, document.SourceFacts!.ScopedCollection!.Complete);
        Assert.DoesNotContain(document.SourceFacts.Sources, source => source.ClassName == "MSFT_Partition");
        var physical = Assert.Single(document.SourceFacts.Sources, source => source.ClassName == "MSFT_PhysicalDisk");
        Assert.Equal(complete, physical.Coverage!.Complete);
        Assert.Contains("SerialNumber", physical.Coverage.FieldNames!.Value);
        Assert.DoesNotContain("CanPool", physical.Coverage.FieldNames.Value);
        var restored = WinPoolFactsCodec.Decode(WinPoolFactsCodec.Encode(document.SourceFacts));
        Assert.Equal(physical.Coverage.FieldNames.Value.ToArray(), restored.Sources.Single(source => source.Id == physical.Id).Coverage!.FieldNames!.Value.ToArray());
    }

    private sealed class ClosedRunner(string json) : IReadOnlyInventoryCommandRunner, IScopedInventoryCommandRunner
    {
        public Task<ReadOnlyCommandResult> RunInventoryAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No whole-machine or real query is allowed.");
        public Task<ReadOnlyCommandResult> RunInventoryAsync(StorageInventoryScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(new ReadOnlyCommandResult(0, json, "", TimeSpan.Zero));
    }
}
