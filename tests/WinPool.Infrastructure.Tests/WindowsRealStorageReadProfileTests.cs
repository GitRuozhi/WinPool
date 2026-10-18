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
            Assert.True(profile.IncludesField("MSFT_Disk", name));
        Assert.True(profile.IncludesField("MSFT_Partition", "AccessPaths"));
        Assert.True(profile.IncludesField("MSFT_Volume", "AllocationUnitSize"));
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
