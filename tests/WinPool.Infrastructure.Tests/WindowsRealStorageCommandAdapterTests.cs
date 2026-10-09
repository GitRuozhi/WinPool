using System.IO.Compression;
using System.Text.Json;
using System.Diagnostics;
using System.Text;
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
        var name = "pool'; Remove-Disk -Number 0; $x = 1\nUnicode 纾佺洏";
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
    public async Task RefsFormatRequiresFrozenExistingVolumeAndOnlyAllowsQuick64KiB()
    {
        var runner = new FakeRunner(new WindowsStorageProcessResult(0, SuccessJson, ""));
        var adapter = new WindowsRealStorageCommandAdapter(runner);
        var partition = RealTargetReference.ForExisting(
            new StorageObjectId(SystemId.New(), StorageObjectKind.Partition, "partition"));

        var result = await adapter.ExecuteAsync(
            new FormatVolumeCommand(partition, RealFileSystem.ReFs, 65536, false, "REFS"),
            PartitionTarget(), CancellationToken.None);

        Assert.False(result.ProviderReturned);
        Assert.Equal(0, runner.Calls);
        var target = PartitionTarget() with { RelatedUniqueId = "volume-id", RelatedObjectId = "volume-object" };
        var command = new FormatVolumeCommand(partition, RealFileSystem.ReFs, 65536, false, "REFS");
        Assert.False((await adapter.ExecuteAsync(command with { Full = true }, target, CancellationToken.None)).ProviderReturned);
        Assert.False((await adapter.ExecuteAsync(command with { ClusterBytes = 4096 }, target, CancellationToken.None)).ProviderReturned);
        Assert.False((await adapter.ExecuteAsync(command, target with { CreatedInThisPlan = true }, CancellationToken.None)).ProviderReturned);
        Assert.Equal(0, runner.Calls);
        Assert.True((await adapter.ExecuteAsync(command, target, CancellationToken.None)).ProviderReturned);
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
    public async Task CreatePartitionUsesFixedScriptWithExactReadOnlyProviderPartitionEnumeration()
    {
        var script = WindowsRealStoragePowerShellScript.Source;
        Assert.Contains(
            "Get-CimInstance -Namespace 'root/Microsoft/Windows/Storage' -ClassName 'MSFT_Partition' -ErrorAction Stop",
            script, StringComparison.Ordinal);
        Assert.Contains(
            "[string]::Equals([string]$_.DiskId, [string]$disk.Path, [StringComparison]::Ordinal)",
            script, StringComparison.Ordinal);
        Assert.Contains(
            "$null -ne $_.DiskNumber -and [uint32]$_.DiskNumber -eq [uint32]$disk.Number",
            script, StringComparison.Ordinal);
        Assert.Contains("disk-partition-geometry-invalid", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Partition -DiskNumber $disk.Number -ErrorAction Stop",
            script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue",
            script, StringComparison.Ordinal);

        var runner = new FakeRunner(new WindowsStorageProcessResult(0, SuccessJson, ""));
        var adapter = new WindowsRealStorageCommandAdapter(runner);
        var disk = RealTargetReference.ForExisting(
            new StorageObjectId(SystemId.New(), StorageObjectKind.OsDisk, "disk"));
        var command = new CreatePartitionCommand(
            disk, RealPartitionRole.BasicData, 1L << 20, 128L << 20);

        var result = await adapter.ExecuteAsync(command, DiskTarget(), CancellationToken.None);

        Assert.True(result.ProviderReturned);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(WindowsRealStoragePowerShellScript.Source, runner.Script);
        using var payload = JsonDocument.Parse(runner.Payload!);
        Assert.Equal("CreatePartition", payload.RootElement.GetProperty("CommandKind").GetString());
        Assert.Equal(1L << 20,
            payload.RootElement.GetProperty("Command").GetProperty("OffsetBytes").GetInt64());
    }

    [Fact]
    public async Task NonzeroProcessExitCannotBeReportedAsProviderSuccess()
    {
        var runner = new FakeRunner(new WindowsStorageProcessResult(1, SuccessJson, "provider details"));
        var adapter = new WindowsRealStorageCommandAdapter(runner);
        var disk = RealTargetReference.ForExisting(
            new StorageObjectId(SystemId.New(), StorageObjectKind.OsDisk, "disk"));

        var result = await adapter.ExecuteAsync(new InitializeGptCommand(disk),
            DiskTarget(), CancellationToken.None);

        Assert.True(result.ProviderReturned);
        Assert.Equal("adapter.response-outcome-unknown", result.Code);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public void CompressedFixedScriptFitsWindowsCommandLine()
    {
        var encoded = WindowsPowerShellStorageWriteRunner.EncodeFixedCompressedScript(
            WindowsRealStoragePowerShellScript.Source);

        Assert.InRange(encoded.Length, 1, 30000);
    }

    [Fact]
    public void FixedScriptRechecksEmptyOldDriveLetterBeforeAddingAccessPath()
    {
        var script = WindowsRealStoragePowerShellScript.Source;
        var emptyOldCheck = script.IndexOf(
            "$old.Length -eq 0 -and -not (Test-UnassignedDriveLetter $partition.DriveLetter)",
            StringComparison.Ordinal);
        var addAccessPath = script.IndexOf("Add-PartitionAccessPath -InputObject $partition",
            StringComparison.Ordinal);

        Assert.True(emptyOldCheck >= 0);
        Assert.True(addAccessPath > emptyOldCheck);
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("char0", true)]
    [InlineData("empty", true)]
    [InlineData("char-letter", false)]
    [InlineData("space", false)]
    [InlineData("string-null", false)]
    [InlineData("array", false)]
    public async Task FixedDriveLetterCaseUsesExactProviderUnassignedValueBeforeFakeMutation(
        string valueKind, bool allowed)
    {
        var source = WindowsRealStoragePowerShellScript.Source;
        var helperStart = source.IndexOf("function Test-UnassignedDriveLetter", StringComparison.Ordinal);
        var helperEnd = source.IndexOf("function Exact-Disk", helperStart, StringComparison.Ordinal);
        var caseStart = source.IndexOf("'SetDriveLetter' {", StringComparison.Ordinal);
        var caseEnd = source.IndexOf("'RenameVolume' {", caseStart, StringComparison.Ordinal);
        Assert.True(helperStart >= 0 && helperEnd > helperStart && caseStart >= 0 && caseEnd > caseStart);
        // Execute only the production helper and this one production switch case.
        // Every storage command it can call is shadowed by a local fake function;
        // the test never loads or executes a real Windows storage command.
        var harness = """
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            $inputData = [Console]::In.ReadToEnd() | ConvertFrom-Json
            $letter = switch ($inputData.Kind) {
                'null' { $null }
                'char0' { [char]0 }
                'empty' { '' }
                'char-letter' { [char]'Q' }
                'space' { ' ' }
                'string-null' { [string][char]0 }
                'array' { ,@('') }
                default { throw 'invalid-fixture' }
            }
            $script:partition = [pscustomobject]@{ GptType='ebd0a0a2-b9e5-4433-87c0-68b6b72699c7'; DriveLetter=$letter }
            $script:addCalls = 0
            function Exact-Partition($t) { return $script:partition }
            function Get-Partition { param($ErrorAction) return @() }
            function Get-PSDrive { param($Name, $ErrorAction) return @() }
            function Add-PartitionAccessPath {
                param($InputObject, $AccessPath, $ErrorAction)
                if ($AccessPath -cne 'R:') { throw 'unexpected-fake-access-path' }
                $script:addCalls++
            }
            function Remove-PartitionAccessPath { throw 'unexpected-fake-removal' }
            function Set-Partition { throw 'unexpected-fake-reassignment' }
            $c = [pscustomobject]@{ PreviousLetter=$null; NewLetter='R' }
            $t = @{}
            $invoked = $false
            $outputObject = $null
            $failure = $null
            """ + "\n" + source[helperStart..helperEnd] + "\ntry { switch ('SetDriveLetter') {\n"
            + source[caseStart..caseEnd] + "\n} } catch { $failure = $_.Exception.Message }\n"
            + "[pscustomobject]@{ Invoked=$invoked; AddCalls=$script:addCalls; Failure=$failure } | ConvertTo-Json -Compress";
        var start = new ProcessStartInfo(WindowsPowerShellRunner.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "[Console]::InputEncoding=[Text.Encoding]::UTF8;[Console]::OutputEncoding=[Text.Encoding]::UTF8;\n" + harness)));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Kind = valueKind }));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await error);
        using var result = JsonDocument.Parse(await output);
        Assert.Equal(allowed, result.RootElement.GetProperty("Invoked").GetBoolean());
        Assert.Equal(allowed ? 1 : 0, result.RootElement.GetProperty("AddCalls").GetInt32());
        Assert.Equal(allowed ? null : "old-drive-letter-changed",
            result.RootElement.GetProperty("Failure").GetString());
    }

    [Theory]
    [InlineData("empty", true, 0, null)]
    [InlineData("ntfs", true, 0, null)]
    [InlineData("refs", true, 1, null)]
    [InlineData("query-failed", false, 0, "exact-association-read-failed")]
    [InlineData("refs-capability-failed", false, 1, "refs-capability-not-proven")]
    [InlineData("refs-shrink", false, 0, "refs-shrink-not-enabled")]
    [InlineData("duplicate", false, 0, "resize-file-system-not-enabled")]
    [InlineData("unsupported", false, 0, "resize-file-system-not-enabled")]
    public async Task FixedResizeCaseDistinguishesExactEmptyVolumeResultFromReadFailure(
        string kind, bool allowed, int refsCalls, string? expectedFailure)
    {
        var source = WindowsRealStoragePowerShellScript.Source;
        var caseStart = source.IndexOf("'ResizePartition' {", StringComparison.Ordinal);
        var caseEnd = source.IndexOf("'FormatVolume' {", caseStart, StringComparison.Ordinal);
        Assert.True(caseStart >= 0 && caseEnd > caseStart);
        // Run the exact production switch branch with every read/write command
        // replaced by a local fake. No storage module or device is accessed.
        var harness = """
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            $inputData = [Console]::In.ReadToEnd() | ConvertFrom-Json
            $script:partition = [pscustomobject]@{
                Guid='d7233fc8-3a1c-47bc-9011-0e9b8d251e7b'; Size=[uint64]8;
                GptType='ebd0a0a2-b9e5-4433-87c0-68b6b72699c7'; IsBoot=$false; IsSystem=$false
            }
            $script:resizeCalls = 0
            $capabilityEvidence = $null
            $script:refsCalls = 0
            $script:associationCalls = 0
            function Exact-Partition($t) { return $script:partition }
            function Get-CimAssociatedInstance {
                param($InputObject, $Namespace, $Association, $ResultClassName, $ErrorAction)
                if ($InputObject.Guid -cne $script:partition.Guid -or
                    $Namespace -cne 'root/Microsoft/Windows/Storage' -or
                    $Association -cne 'MSFT_PartitionToVolume' -or
                    $ResultClassName -cne 'MSFT_Volume' -or $ErrorAction -cne 'Stop') {
                    throw 'association-was-not-fixed-and-exact'
                }
                $script:associationCalls++
                switch ($inputData.Kind) {
                    'empty' { return @() }
                    'query-failed' { throw 'exact-association-read-failed' }
                    'duplicate' { return @([pscustomobject]@{FileSystem='NTFS'}, [pscustomobject]@{FileSystem='NTFS'}) }
                    'unsupported' { return [pscustomobject]@{FileSystem='FAT32'} }
                    'ntfs' { return [pscustomobject]@{FileSystem='NTFS'} }
                    default { return [pscustomobject]@{FileSystem='ReFS'} }
                }
            }
            function Get-Volume { throw 'unexpected-volume-fallback' }
            function Require-Refs($partition, $t) {
                $script:refsCalls++
                if ($inputData.Kind -eq 'refs-capability-failed') { throw 'refs-capability-not-proven' }
            }
            function Get-PartitionSupportedSize {
                param($InputObject, $ErrorAction)
                return [pscustomobject]@{ SizeMin=[uint64]4; SizeMax=[uint64]32 }
            }
            function Resize-Partition {
                param($InputObject, $Size, $ErrorAction)
                if ($InputObject.Guid -cne $script:partition.Guid -or $Size -ne 16) { throw 'unexpected-resize-target-or-size' }
                $script:resizeCalls++
            }
            $c = [pscustomobject]@{ SizeBytes=$(if ($inputData.Kind -eq 'refs-shrink') { 4 } else { 16 }) }
            $t = @{}
            $invoked = $false
            $outputObject = $null
            $failure = $null
            """ + "\ntry { switch ('ResizePartition') {\n" + source[caseStart..caseEnd]
            + "\n} } catch { $failure = $_.Exception.Message }\n"
            + "[pscustomobject]@{ Invoked=$invoked; ResizeCalls=$script:resizeCalls; RefsCalls=$script:refsCalls; AssociationCalls=$script:associationCalls; Failure=$failure } | ConvertTo-Json -Compress";
        var start = new ProcessStartInfo(WindowsPowerShellRunner.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "[Console]::InputEncoding=[Text.Encoding]::UTF8;[Console]::OutputEncoding=[Text.Encoding]::UTF8;\n" + harness)));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Kind = kind }));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await error);
        using var result = JsonDocument.Parse(await output);
        Assert.Equal(allowed, result.RootElement.GetProperty("Invoked").GetBoolean());
        Assert.Equal(allowed ? 1 : 0, result.RootElement.GetProperty("ResizeCalls").GetInt32());
        Assert.Equal(refsCalls, result.RootElement.GetProperty("RefsCalls").GetInt32());
        Assert.Equal(1, result.RootElement.GetProperty("AssociationCalls").GetInt32());
        Assert.Equal(expectedFailure, result.RootElement.GetProperty("Failure").GetString());
    }

    [Fact]
    public async Task FixedTierSizeHelperMatchesLegalProviderShapesAndRejectsBadEvidence()
    {
        var source = WindowsRealStoragePowerShellScript.Source;
        var startIndex = source.IndexOf("function Test-TierCreationSize", StringComparison.Ordinal);
        var endIndex = source.IndexOf("function Exact-Disk", startIndex, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        var harness = source[startIndex..endIndex] + "\n" + """
            $results = foreach ($kind in @('list-only','offset-range','unlisted','absolute-only',
                'method-failed','malformed-list','duplicate-list','incomplete-range','overflow','conflicting-forms')) {
                $range = [pscustomobject]@{ ReturnValue=[uint32]0; SupportedSizes=$null;
                    TierSizeMin=$null; TierSizeMax=$null; TierSizeDivisor=$null }
                $candidate = [long]160
                switch ($kind) {
                    'list-only' { $range.SupportedSizes = [uint64[]]@(160,224) }
                    'offset-range' { $range.TierSizeMin=[uint64]96; $range.TierSizeMax=[uint64]224; $range.TierSizeDivisor=[uint64]64 }
                    'unlisted' { $range.SupportedSizes=[uint64[]]@(224) }
                    'absolute-only' { $range.TierSizeMin=[uint64]96; $range.TierSizeMax=[uint64]224; $range.TierSizeDivisor=[uint64]64; $candidate=128 }
                    'method-failed' { $range.ReturnValue=[uint32]4; $range.SupportedSizes=[uint64[]]@(160) }
                    'malformed-list' { $range.SupportedSizes=[long[]]@(160) }
                    'duplicate-list' { $range.SupportedSizes=[uint64[]]@(160,160) }
                    'incomplete-range' { $range.TierSizeMin=[uint64]96; $range.TierSizeMax=[uint64]224 }
                    'overflow' { $range.SupportedSizes=[uint64[]]@([uint64]::MaxValue) }
                    'conflicting-forms' { $range.SupportedSizes=[uint64[]]@(128); $range.TierSizeMin=[uint64]96; $range.TierSizeMax=[uint64]224; $range.TierSizeDivisor=[uint64]64 }
                }
                $allowed = $false; $failure = $null
                try { $allowed = Test-TierCreationSize $range $candidate }
                catch { $failure = $_.Exception.Message }
                [pscustomobject]@{ Kind=$kind; Allowed=$allowed; Failure=$failure }
            }
            @($results) | ConvertTo-Json -Compress
            """;
        var start = new ProcessStartInfo(WindowsPowerShellRunner.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8;Set-StrictMode -Version Latest;$ErrorActionPreference='Stop';\n" + harness)));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await error);
        using var result = JsonDocument.Parse(await output);
        Assert.Equal(10, result.RootElement.GetArrayLength());
        foreach (var item in result.RootElement.EnumerateArray())
        {
            var kind = item.GetProperty("Kind").GetString()!;
            var allowed = kind is "list-only" or "offset-range";
            Assert.Equal(allowed, item.GetProperty("Allowed").GetBoolean());
            if (kind is "list-only" or "offset-range" or "unlisted" or "absolute-only")
                Assert.Equal(JsonValueKind.Null, item.GetProperty("Failure").ValueKind);
            else Assert.StartsWith("tier-creation-size-", item.GetProperty("Failure").GetString());
        }
    }

    [Theory]
    [InlineData("template", true, null)]
    [InlineData("instance", true, null)]
    [InlineData("wrong-pool", false, "tier-virtual-disk-pool-changed")]
    [InlineData("duplicate", false, "tier-not-unique")]
    [InlineData("no-association", false, "tier-not-unique")]
    [InlineData("wrong-owner", false, "tier-owner-changed")]
    [InlineData("extent-member", false, "tier-extent-member-changed")]
    [InlineData("extent-failed", false, "tier-extent-method-failed")]
    [InlineData("extent-zero-size", false, "tier-extent-size-unknown")]
    [InlineData("extent-missing-size", false, "tier-extent-size-unknown")]
    [InlineData("changed-vd", false, "tier-virtual-disk-object-id-changed")]
    public async Task CompressedFixedTierRenameResolvesFrozenTemplateOrExactVirtualDiskInstance(
        string shape, bool invoked, string? diagnostic)
    {
        var fakeCommands = """
            $script:fixtureShape='__SHAPE__'
            $script:fakeSetCalls=0
            $script:fakeSubsystem=[pscustomobject]@{ UniqueId='subsystem-unique';ObjectId='subsystem-object';
                SupportsStorageTierFriendlyNameModification=$true;PhysicalDisksPerStoragePoolMin=1 }
            $script:fakePool=[pscustomobject]@{ UniqueId='pool-unique';ObjectId='pool-object';IsPrimordial=$false }
            $script:fakePhysical=[pscustomobject]@{ UniqueId='physical-unique';ObjectId='physical-object';SerialNumber='serial' }
            $script:fakeVirtual=[pscustomobject]@{ UniqueId='vd-unique';ObjectId='vd-object' }
            $script:fakeTemplate=[pscustomobject]@{ UniqueId='template-unique';ObjectId='template-object' }
            $script:fakeInstance=[pscustomobject]@{ UniqueId='instance-unique';ObjectId='instance-object' }
            function Import-Module { param($Name,$ErrorAction) if($Name -ne 'Storage'){throw 'unexpected-fake-module'} }
            function Get-StorageSubsystem { param($ErrorAction) return $script:fakeSubsystem }
            function Get-StoragePool {
                param($StorageSubsystem,$VirtualDisk,$ErrorAction)
                if($null -ne $VirtualDisk){
                    if($VirtualDisk.UniqueId -ne 'vd-unique'){throw 'unexpected-parent-query'}
                    if($script:fixtureShape -eq 'wrong-pool'){return [pscustomobject]@{UniqueId='other-pool';ObjectId='other-object'}}
                }
                return $script:fakePool
            }
            function Get-PhysicalDisk { param($StorageSubsystem,$StoragePool,$ErrorAction) return $script:fakePhysical }
            function Get-VirtualDisk {
                param($StoragePool,$StorageTier,$ErrorAction)
                if($null -ne $StorageTier){
                    if($StorageTier.UniqueId -eq 'template-unique'){return @()}
                    if($StorageTier.UniqueId -ne 'instance-unique'){throw 'unexpected-owner-query'}
                    if($script:fixtureShape -eq 'wrong-owner'){return [pscustomobject]@{UniqueId='wrong-vd';ObjectId='wrong-vd-object'}}
                }
                if($script:fixtureShape -eq 'changed-vd'){return [pscustomobject]@{UniqueId='vd-unique';ObjectId='replaced-object'}}
                return $script:fakeVirtual
            }
            function Get-StorageTier {
                param($StoragePool,$VirtualDisk,$ErrorAction)
                if($null -ne $StoragePool){return $script:fakeTemplate}
                if($null -eq $VirtualDisk -or $VirtualDisk.UniqueId -ne 'vd-unique'){throw 'unexpected-instance-query'}
                if($script:fixtureShape -eq 'no-association'){return @()}
                if($script:fixtureShape -eq 'duplicate'){return @($script:fakeInstance,$script:fakeInstance)}
                return $script:fakeInstance
            }
            function Invoke-CimMethod {
                param($InputObject,$MethodName,$ErrorAction)
                if($InputObject.UniqueId -ne 'instance-unique' -or $MethodName -ne 'GetPhysicalExtent'){throw 'unexpected-fake-method'}
                $extents=@(0..63|ForEach-Object{
                    $extent=[pscustomobject]@{
                    StorageTierUniqueId='instance-unique';VirtualDiskUniqueId='vd-unique';
                    PhysicalDiskUniqueId=$(if($script:fixtureShape -eq 'extent-member'){'wrong-member'}else{'physical-unique'});
                    Size=[uint64]$(if($script:fixtureShape -eq 'extent-zero-size'){0}else{268435456});VirtualDiskOffset=[uint64]($_*268435456)
                    }
                    if($script:fixtureShape -eq 'extent-missing-size'){$extent.PSObject.Properties.Remove('Size')}
                    return $extent
                })
                return [pscustomobject]@{ReturnValue=[uint32]$(if($script:fixtureShape -eq 'extent-failed'){1}else{0});PhysicalExtents=$extents}
            }
            function Set-StorageTier {
                param($InputObject,$NewFriendlyName,$ErrorAction)
                $expected=$(if($script:fixtureShape -eq 'template'){'template-unique'}else{'instance-unique'})
                if($InputObject.UniqueId -ne $expected -or $NewFriendlyName -ne 'Renamed exact tier'){throw 'wrong-fake-set-target'}
                $script:fakeSetCalls++
            }
            """.Replace("__SHAPE__", shape, StringComparison.Ordinal);
        var template = shape == "template";
        var target = PoolTarget() with
        {
            Kind = StorageObjectKind.StorageTier, UniqueId = template ? "template-unique" : "instance-unique",
            ObjectId = template ? "template-object" : "instance-object", ParentUniqueId = "pool-unique",
            SerialNumber = "serial", StorageSubsystemObjectId = "subsystem-object", PhysicalMemberObjectId = "physical-object",
            RelatedUniqueId = template ? "" : "vd-unique", RelatedObjectId = template ? "" : "vd-object"
        };
        var payload = JsonSerializer.Serialize(new { CommandKind = "RenameTier", Target = target,
            Command = new { Name = "Renamed exact tier" } });
        var output = await RunCompressedFixedScriptWithFakesAsync(WindowsRealStoragePowerShellScript.Source,
            fakeCommands, payload, "[Console]::Out.WriteLine($script:fakeSetCalls)");
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var response = JsonDocument.Parse(lines[0]);
        Assert.Equal(invoked, response.RootElement.GetProperty("ProviderReturned").GetBoolean());
        Assert.Equal(invoked ? "provider.returned" : "adapter.preflight-rejected", response.RootElement.GetProperty("Code").GetString());
        Assert.Equal(diagnostic, response.RootElement.GetProperty("ProviderError").GetString());
        Assert.Equal(invoked ? "1" : "0", lines[1]);
        if (invoked)
        {
            Assert.Equal(target.UniqueId, response.RootElement.GetProperty("UniqueId").GetString());
            Assert.Equal(target.ObjectId, response.RootElement.GetProperty("ObjectId").GetString());
            Assert.True(response.RootElement.GetProperty("LiveCapabilityEvidence").GetProperty("RequiredValue").GetBoolean());
        }
    }

    [Theory]
    [InlineData("range", true, null)]
    [InlineData("list", true, null)]
    [InlineData("size-outside-range", false, "tier-creation-size-not-supported")]
    [InlineData("method-failed", false, "tier-creation-size-method-failed")]
    [InlineData("capability-false", false, "single-hdd-tier-capability-not-verified")]
    [InlineData("tier-only-max", true, null)]
    [InlineData("pool-grid-mismatch", true, null)]
    [InlineData("pool-grid-mismatch-four-units", true, null)]
    [InlineData("offset-range", true, null)]
    [InlineData("offset-range-wrong-grid", false, "tier-creation-size-not-supported")]
    [InlineData("native-max", false, "tiered-native-maximum-pending")]
    [InlineData("native-ordinary", false, "maximum-requires-journaled-explicit-attempt")]
    [InlineData("native-max-error", false, "tiered-native-maximum-pending")]
    [InlineData("native-auto", false, "tiered-native-maximum-pending")]
    [InlineData("native-auto-error", false, "tiered-native-maximum-pending")]
    [InlineData("unknown-mechanism", false, "tier-creation-mechanism-invalid")]
    public async Task CompressedFixedTieredCreationRetainsLiveEvidenceAcrossChildScope(
        string shape, bool invoked, string? diagnostic)
    {
        // Exercise the complete production fixed script through its production
        // compressed child-scope loader. All reachable storage functions are
        // local fakes, including Import-Module; no Windows storage API is loaded.
        var fakeCommands = """
            $script:fixtureShape = '__SHAPE__'
            $script:nativeMaximum = $script:fixtureShape -like 'native-*'
            $script:expectedSize = switch ($script:fixtureShape) {
                'tier-only-max' { [long]3999956729856 }
                'pool-grid-mismatch' { [long]3997809246208 }
                'pool-grid-mismatch-four-units' { [long]3998882988032 }
                'offset-range' { [long]167772160 }
                'offset-range-wrong-grid' { [long]134217728 }
                default { [long]17179869184 }
            }
            $script:fakeWriteCalls = 0
            $script:fakeSubsystem = [pscustomobject]@{
                UniqueId='subsystem-unique'; ObjectId='subsystem-object';
                SupportsStorageTieredVirtualDiskCreation=($script:fixtureShape -ne 'capability-false');
                PhysicalDisksPerStoragePoolMin=1
            }
            $script:fakePool = [pscustomobject]@{ UniqueId='pool-unique'; ObjectId='pool-object'; IsPrimordial=$false }
            $script:fakePhysical = [pscustomobject]@{ UniqueId='physical-unique'; ObjectId='physical-object'; SerialNumber='serial'; MediaType='HDD' }
            $script:fakeTemplate = [pscustomobject]@{
                UniqueId='template-unique'; ObjectId='template-object'; Size=[uint64]0; AllocatedSize=[uint64]0;
                MediaType='HDD'; ResiliencySettingName='Simple'; Interleave=[uint64]65536; NumberOfColumns=[uint16]1
            }
            function Import-Module { param($Name, $ErrorAction) if ($Name -ne 'Storage') { throw 'unexpected-fake-module' } }
            function Get-StorageSubSystem { param($ErrorAction) return $script:fakeSubsystem }
            function Get-StoragePool { param($StorageSubSystem, $ErrorAction) return $script:fakePool }
            function Get-PhysicalDisk { param($StorageSubSystem, $StoragePool, $ErrorAction) return $script:fakePhysical }
            function Get-StorageTier { param($StoragePool, $ErrorAction) return $script:fakeTemplate }
            function Get-VirtualDisk { param($StoragePool, $StorageTier, $ErrorAction) return @() }
            function Invoke-CimMethod {
                param($InputObject, $MethodName, $Arguments, $ErrorAction)
                if ($script:nativeMaximum) { throw 'native-max-must-not-query-reported-size' }
                if ($InputObject.UniqueId -notin @('template-unique','pool-unique') -or $MethodName -ne 'GetSupportedSize' -or
                    $Arguments.ResiliencySettingName -ne 'Simple') { throw 'unexpected-fake-method' }
                if ($InputObject.UniqueId -eq 'pool-unique') { throw 'tiered-must-not-query-generic-pool-range' }
                [uint64[]]$supportedSizes = @()
                if ($script:fixtureShape -eq 'list') { $supportedSizes = [uint64[]]@(17179869184) }
                return [pscustomobject]@{
                    ReturnValue=[uint32]$(if ($script:fixtureShape -eq 'method-failed') { 1 } else { 0 });
                    SupportedSizes=$supportedSizes;
                    TierSizeDivisor=[uint64]$(if ($script:fixtureShape -like 'offset-range*') { 67108864 } else { 268435456 });
                    TierSizeMin=[uint64]$(if ($script:fixtureShape -like 'offset-range*') { 100663296 } else { 268435456 });
                    TierSizeMax=[uint64]$(if ($script:fixtureShape -like 'offset-range*') { 234881024 } elseif ($script:fixtureShape -eq 'size-outside-range') { 8589934592 } else { 3999956729856 })
                }
            }
            function New-VirtualDisk {
                param($InputObject, $FriendlyName, $StorageTiers, $StorageTierSizes, $ResiliencySettingName,
                    $ProvisioningType, $NumberOfColumns, $Interleave, [switch]$UseMaximumSize, $Size, $MediaType, $ErrorAction)
                if ($InputObject.UniqueId -ne 'pool-unique' -or $ResiliencySettingName -ne 'Simple' -or
                    $ProvisioningType -ne 'Fixed' -or $NumberOfColumns -ne 1 -or $Interleave -ne 65536) {
                    throw 'unexpected-fake-create-parameters'
                }
                if ($script:nativeMaximum) {
                    if (-not $UseMaximumSize.IsPresent -or $PSBoundParameters.ContainsKey('Size') -or
                        $PSBoundParameters.ContainsKey('StorageTierSizes')) { throw 'native-max-has-explicit-size' }
                    if ($script:fixtureShape -eq 'native-ordinary') {
                        if ($PSBoundParameters.ContainsKey('StorageTiers')) { throw 'ordinary-max-has-tier' }
                    } elseif ($script:fixtureShape -like 'native-auto*') {
                        if ($MediaType -ne 'HDD' -or $PSBoundParameters.ContainsKey('StorageTiers')) { throw 'automatic-hdd-incorrect-provider-input' }
                    } elseif ($PSBoundParameters.ContainsKey('MediaType') -or $StorageTiers.Count -ne 1 -or $StorageTiers[0].UniqueId -ne 'template-unique') {
                        throw 'native-max-lost-exact-template'
                    }
                } elseif ($UseMaximumSize.IsPresent -or $StorageTiers.Count -ne 1 -or
                    $StorageTiers[0].UniqueId -ne 'template-unique' -or $StorageTierSizes.Count -ne 1 -or
                    $StorageTierSizes[0] -ne $script:expectedSize) { throw 'explicit-size-changed' }
                $script:fakeWriteCalls++
                if ($script:fixtureShape -like 'native-*-error') { throw 'native-max-fixture-error' }
                return [pscustomobject]@{ UniqueId='returned-vd-unique'; ObjectId='returned-vd-object' }
            }
            """.Replace("__SHAPE__", shape, StringComparison.Ordinal);
        var target = PoolTarget() with
        {
            SerialNumber = "serial", StorageSubsystemObjectId = "subsystem-object",
            PhysicalMemberObjectId = "physical-object", RelatedUniqueId = "template-unique",
            RelatedObjectId = "template-object"
        };
        var nativeMaximum = shape.StartsWith("native-", StringComparison.Ordinal);
        var automaticHdd = shape.StartsWith("native-auto", StringComparison.Ordinal);
        var poolReference = RealTargetReference.ForExisting(new StorageObjectId(SystemId.New(), StorageObjectKind.StoragePool, "pool"));
        var tierReference = RealTargetReference.ForExisting(new StorageObjectId(poolReference.Existing!.Value.System, StorageObjectKind.StorageTier, "constraint-template"));
        var bytes = shape switch
        {
            "tier-only-max" => 3999956729856L,
            "pool-grid-mismatch" => 3997809246208L,
            "pool-grid-mismatch-four-units" => 3998882988032L,
            "offset-range" => 167772160L,
            "offset-range-wrong-grid" => 134217728L,
            _ => nativeMaximum ? 0L : 16L << 30
        };
        RealStorageCommand command = shape == "native-ordinary"
            ? new CreateVirtualDiskCommand(poolReference, "Tiered VD", bytes, 65536, 1, nativeMaximum)
            : new CreateTieredVirtualDiskCommand(poolReference, tierReference, "Tiered VD", bytes, nativeMaximum,
                automaticHdd ? TieredVirtualDiskCreationMechanism.WindowsAutomaticHdd : TieredVirtualDiskCreationMechanism.ExactTemplate);
        if (shape == "unknown-mechanism") command = ((CreateTieredVirtualDiskCommand)command) with
            { CreationMechanism = (TieredVirtualDiskCreationMechanism)99 };
        // Obtain payload through the real adapter, including its private
        // JsonStringEnumConverter options, then execute that exact payload in
        // the complete production compressed script with all storage calls fake.
        var serializationRunner = new FakeRunner(new WindowsStorageProcessResult(0, SuccessJson, ""));
        var adapter = new WindowsRealStorageCommandAdapter(serializationRunner);
        var blockedTieredMaximum = nativeMaximum;
        var blockedDirectWire = blockedTieredMaximum || shape == "unknown-mechanism";
        var adapterResult = await adapter.ExecuteAsync(command, target, CancellationToken.None);
        Assert.Equal(!blockedDirectWire, adapterResult.ProviderReturned);
        Assert.Equal(blockedDirectWire ? 0 : 1, serializationRunner.Calls);
        if (blockedTieredMaximum) Assert.Equal("adapter.maximum-requires-journaled-explicit-attempt", adapterResult.Code);
        if (shape == "unknown-mechanism") Assert.Equal("adapter.closed-command-or-target-required", adapterResult.Code);
        // Historical wire contracts remain readable; even a direct fixed-script
        // invocation of that valid old payload must now fail before a Windows call.
        var payload = blockedDirectWire ? JsonSerializer.Serialize(new { CommandKind = shape == "native-ordinary" ? "CreateVirtualDisk" : "CreateTieredVirtualDisk", Command = command, Target = target },
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })
            : serializationRunner.Payload!;
        using (var serialized = JsonDocument.Parse(payload))
        {
            var serializedCommand = serialized.RootElement.GetProperty("Command");
            if (automaticHdd)
                Assert.Equal("WindowsAutomaticHdd", serializedCommand.GetProperty("CreationMechanism").GetString());
            else if (shape == "unknown-mechanism") Assert.Equal(99, serializedCommand.GetProperty("CreationMechanism").GetInt32());
            else Assert.False(serializedCommand.TryGetProperty("CreationMechanism", out _));
        }
        var output = await RunCompressedFixedScriptWithFakesAsync(WindowsRealStoragePowerShellScript.Source,
            fakeCommands, payload, "[Console]::Out.WriteLine($script:fakeWriteCalls)");
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var response = JsonDocument.Parse(lines[0]);
        var result = response.RootElement;
        Assert.Equal(invoked, result.GetProperty("ProviderReturned").GetBoolean());
        Assert.Equal(blockedDirectWire ? "adapter.preflight-rejected" : shape.EndsWith("-error", StringComparison.Ordinal) ? "provider.error-outcome-unknown"
            : invoked ? "provider.returned" : "adapter.preflight-rejected", result.GetProperty("Code").GetString());
        Assert.Equal(diagnostic, result.GetProperty("ProviderError").GetString());
        Assert.Equal(invoked ? "1" : "0", lines[1]);
        if (blockedDirectWire)
        {
            Assert.Equal(JsonValueKind.Null, result.GetProperty("TieredCreationInput").ValueKind);
            return;
        }
        var evidence = result.GetProperty("LiveCapabilityEvidence");
        if (shape.StartsWith("native-", StringComparison.Ordinal))
        {
            if (shape == "native-ordinary") Assert.Equal(JsonValueKind.Null, evidence.ValueKind);
            else
            {
                Assert.True(evidence.GetProperty("UseMaximumSize").GetBoolean());
                Assert.False(evidence.TryGetProperty("CreationSize", out _));
                Assert.False(evidence.TryGetProperty("PoolCreationSize", out _));
                var input = result.GetProperty("TieredCreationInput");
                Assert.True(input.GetProperty("UseMaximumSize").GetBoolean());
                Assert.Equal(0, input.GetProperty("SizeBytes").GetInt64());
                if (shape.StartsWith("native-auto", StringComparison.Ordinal))
                {
                    Assert.Equal(1, input.GetProperty("CreationMechanism").GetInt32());
                    Assert.Equal(1, evidence.GetProperty("CreationMechanism").GetInt32());
                    Assert.Equal("template-unique", input.GetProperty("ConstraintTemplateUniqueId").GetString());
                    Assert.Equal("template-object", input.GetProperty("ConstraintTemplateObjectId").GetString());
                    Assert.Equal(JsonValueKind.Null, input.GetProperty("TemplateUniqueId").ValueKind);
                    Assert.Equal(JsonValueKind.Null, input.GetProperty("ProviderTemplateUniqueId").ValueKind);
                    Assert.Equal(JsonValueKind.Null, input.GetProperty("ProviderTemplateObjectId").ValueKind);
                }
                else Assert.Equal("template-unique", input.GetProperty("TemplateUniqueId").GetString());
            }
            return;
        }
        Assert.Equal("subsystem-unique", evidence.GetProperty("SubsystemUniqueId").GetString());
        Assert.Equal("physical-object", evidence.GetProperty("PhysicalMemberObjectId").GetString());
        Assert.Equal(shape != "capability-false", evidence.GetProperty("RequiredValue").GetBoolean());
        if (shape != "capability-false")
        {
            var size = evidence.GetProperty("CreationSize");
            Assert.Equal(shape == "method-failed" ? 1U : 0U, size.GetProperty("ReturnValue").GetUInt32());
            Assert.Equal(shape == "list" ? 1 : 0, size.GetProperty("SupportedSizes").GetArrayLength());
        }
        if (invoked)
        {
            Assert.False(evidence.TryGetProperty("PoolCreationSize", out _));
            Assert.Equal(bytes, result.GetProperty("TieredCreationInput").GetProperty("SizeBytes").GetInt64());
            Assert.Equal("returned-vd-unique", result.GetProperty("UniqueId").GetString());
            Assert.Equal("template-unique", result.GetProperty("TieredCreationInput").GetProperty("TemplateUniqueId").GetString());
        }
    }

    [Fact]
    public async Task CompressedRefsHelperReturnsCurrentScopeCapabilityEvidence()
    {
        var source = WindowsRealStoragePowerShellScript.Source;
        var readStart = source.IndexOf("function Read-Property", StringComparison.Ordinal);
        var readEnd = source.IndexOf("function Exact-Subsystem", readStart, StringComparison.Ordinal);
        var refsStart = source.IndexOf("function Require-Refs", StringComparison.Ordinal);
        var refsEnd = source.IndexOf("function Require-TierCapability", refsStart, StringComparison.Ordinal);
        var script = """
            Set-StrictMode -Version Latest
            $ErrorActionPreference='Stop'
            $ProgressPreference='SilentlyContinue'
            [Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
            [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
            $capabilityEvidence=$null
            function Assert-One($values, $reason) { $items=@($values); if ($items.Count -ne 1) { throw $reason }; return $items[0] }
            function Assert-Exact($actual, $expected, $reason) { if ($actual -cne $expected) { throw $reason } }
            function Get-Volume { param($Partition, $ErrorAction) return [pscustomobject]@{ UniqueId='exact-volume'; ObjectId='volume-object' } }
            function Get-CimInstance { param($ClassName, $ErrorAction) if ($ClassName -ne 'Win32_OperatingSystem') { throw 'unexpected-class' }; return [pscustomobject]@{ Caption='Windows Server 2022' } }
            function Invoke-CimMethod {
                param($InputObject, $MethodName, $Arguments, $ErrorAction)
                if ($MethodName -eq 'GetSupportedFileSystems') { return [pscustomobject]@{ ReturnValue=[uint32]0; SupportedFileSystems=@('NTFS','ReFS') } }
                if ($MethodName -eq 'GetSupportedClusterSizes' -and $Arguments.FileSystem -eq 'ReFS') { return [pscustomobject]@{ ReturnValue=[uint32]0; SupportedClusterSizes=[uint32[]]@(4096,65536) } }
                throw 'unexpected-method'
            }
            """ + "\n" + source[readStart..readEnd] + source[refsStart..refsEnd] + """

            $partition=[pscustomobject]@{ Guid='exact-partition-guid' }
            $target=[pscustomobject]@{ RelatedUniqueId='exact-volume'; RelatedObjectId='volume-object' }
            $null=Require-Refs $partition $target ([ref]$capabilityEvidence)
            [Console]::Out.WriteLine(($capabilityEvidence | ConvertTo-Json -Compress -Depth 8))
            """;
        var output = await RunCompressedFixedScriptWithFakesAsync(script, "", "", "");
        Assert.False(string.IsNullOrWhiteSpace(output));
        using var evidence = JsonDocument.Parse(output);
        Assert.Equal("exact-volume", evidence.RootElement.GetProperty("VolumeUniqueId").GetString());
        Assert.Equal(0U, evidence.RootElement.GetProperty("FileSystems").GetProperty("ReturnValue").GetUInt32());
        Assert.Equal(65536, evidence.RootElement.GetProperty("ReFsClusterSizes").GetProperty("SupportedClusterSizes")[1].GetInt32());
    }

    private static async Task<string> RunCompressedFixedScriptWithFakesAsync(string script,
        string fakeCommands, string payload, string suffix)
    {
        // Only this short loader travels on the command line. Its first stdin
        // line contains the compressed closed fixture; the remaining bytes are
        // the unchanged typed JSON read by the production script. No script
        // file or Windows storage module is loaded by this local fake harness.
        var fixtureScript = fakeCommands + "\n& {\n" + script + "\n}\n" + suffix;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        // Read the first line byte by byte: Console.In.ReadLine can buffer typed
        // JSON ahead of the encoding reset performed by the fixed script.
        const string loader = "$i=[Console]::OpenStandardInput();$l=[IO.MemoryStream]::new();"
            + "while(($n=$i.ReadByte()) -ge 0 -and $n -ne 10){$l.WriteByte([byte]$n)};"
            + "$b=[Convert]::FromBase64String([Text.Encoding]::ASCII.GetString($l.ToArray()));$m=[IO.MemoryStream]::new($b);"
            + "$g=[IO.Compression.GZipStream]::new($m,[IO.Compression.CompressionMode]::Decompress);"
            + "$r=[IO.StreamReader]::new($g,[Text.UTF8Encoding]::new($false));$s=$r.ReadToEnd();& ([ScriptBlock]::Create($s))";
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(loader)));
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(fixtureScript));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(Convert.ToBase64String(buffer.ToArray()));
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await error);
        Assert.True(string.IsNullOrWhiteSpace(await error), await error);
        return await output;
    }

    [Fact]
    public void ProductionFixedScriptEncodedCommandFitsTheWindowsProcessCommandLine()
    {
        var encoded = WindowsPowerShellStorageWriteRunner.EncodeFixedCompressedScript(WindowsRealStoragePowerShellScript.Source);
        const string arguments = " -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";
        var completeLengthWithTerminator = WindowsPowerShellRunner.ExecutablePath.Length + 2 + arguments.Length + encoded.Length + 1;
        Assert.True(completeLengthWithTerminator <= 32767,
            $"Fixed production command line is {completeLengthWithTerminator} characters; Windows limit is 32767 including terminator.");
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
