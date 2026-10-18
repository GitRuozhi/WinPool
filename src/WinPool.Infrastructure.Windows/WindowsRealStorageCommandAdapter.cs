using System.Diagnostics;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Windows;

internal sealed record WindowsStorageProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal interface IWindowsStorageWriteProcessRunner
{
    Task<WindowsStorageProcessResult> RunAsync(string fixedScript, string payloadJson, CancellationToken cancellationToken);
}

public sealed class WindowsRealStorageCommandAdapter : IWindowsRealStorageCommandAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IWindowsStorageWriteProcessRunner? _runner;
    private readonly WindowsPowerShellHost host;

    public WindowsRealStorageCommandAdapter(WindowsPowerShellHost? host = null) => this.host = host ?? WindowsPowerShellHost.Shared;

    internal WindowsRealStorageCommandAdapter(IWindowsStorageWriteProcessRunner runner)
    { _runner = runner ?? throw new ArgumentNullException(nameof(runner)); host = WindowsPowerShellHost.Shared; }

    public async Task<WindowsPowerShellWriteLease?> ReserveWriteAsync(CancellationToken token) =>
        _runner is null ? await host.ReserveWriteAsync(token).ConfigureAwait(false) : null;

    public Task<WindowsStorageCommandResult> ExecuteAsync(RealStorageCommand command,
        WindowsStorageCommandTarget target, CancellationToken token) => ExecuteAsync(command, target, null, token);

    public async Task<WindowsStorageCommandResult> ExecuteAsync(
        RealStorageCommand command,
        WindowsStorageCommandTarget target,
        WindowsPowerShellWriteLease? lease,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        token.ThrowIfCancellationRequested();

        if (WindowsRealStorageBackend.IsMaximumCapacityMacro(command))
            return Rejected("adapter.maximum-requires-journaled-explicit-attempt");

        var kind = GetCommandKind(command);
        if (kind is null || !IsExactTarget(command, target) || !IsSupportedParameters(command))
        {
            return Rejected("adapter.closed-command-or-target-required");
        }

        if (_runner is null && (lease is null || target.HostCall != lease.HostCall))
            return Rejected("adapter.durable-host-reservation-required");

        WindowsStorageCommandResult WithCompletion(WindowsStorageCommandResult result)
        {
            var completed = result with
            {
                // Completion comes from the validated protocol frame, never from provider JSON.
                CompletedHostCall = _runner is null && lease?.RequestCompleted == true ? lease.HostCall : null
            };
            RecordWriteCmdletTimings(completed, target, kind);
            return completed;
        }
        var payload = JsonSerializer.Serialize(new { CommandKind = kind, Command = command, Target = target }, JsonOptions);
        WindowsStorageProcessResult process;
        try
        {
            process = _runner is not null
                ? await _runner.RunAsync(WindowsRealStoragePowerShellScript.Source, payload, token).ConfigureAwait(false)
                : new WindowsStorageProcessResult(0,
                    await lease!.ExecuteAsync(new { CommandKind = kind, Command = command, Target = target }, token).ConfigureAwait(false), "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The production runner throws this only before the process starts.
            throw;
        }
        catch (Exception)
        {
            // The runner may have started a provider call before failing to report.
            return WithCompletion(new WindowsStorageCommandResult(true, "adapter.process-outcome-unknown",
                null, null, null, null, null, null, "The write process outcome is unknown; reconcile read-only."));
        }

        if (string.IsNullOrWhiteSpace(process.StandardOutput))
        {
            return WithCompletion(Unknown("The write process returned no structured result.", process));
        }

        try
        {
            var result = JsonSerializer.Deserialize<WindowsStorageCommandResult>(process.StandardOutput.Trim(), JsonOptions);
            if (result is null || string.IsNullOrWhiteSpace(result.Code))
            {
                return WithCompletion(Unknown("The write process result was incomplete.", process));
            }

            if (process.ExitCode != 0)
            {
                return WithCompletion(Unknown("The write process exited abnormally; a storage call may have changed the target.", process));
            }

            return WithCompletion(result switch
            {
                { Code: "provider.returned", ProviderReturned: true } => result,
                { Code: "adapter.preflight-rejected", ProviderReturned: false } => result,
                { Code: "provider.error-outcome-unknown", ProviderReturned: true } => result,
                _ => Unknown("The write process returned an inconsistent result.", process)
            });
        }
        catch (JsonException)
        {
            return WithCompletion(Unknown("The write process returned invalid structured data.", process));
        }
    }

    private static WindowsStorageCommandResult Rejected(string code) =>
        new(false, code, null, null, null, null, null, null, null);

    private static void RecordWriteCmdletTimings(WindowsStorageCommandResult result,
        WindowsStorageCommandTarget target, string commandKind)
    {
        if (result.CompletedHostCall is not { } call || call != target.HostCall
            || result.WriteCmdletTimings is not { ValueKind: JsonValueKind.Array } timings) return;
        var index = 0;
        foreach (var item in timings.EnumerateArray())
        {
            var cmdletIndex = index++;
            try
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("Cmdlet", out var name) || name.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(name.GetString())
                    || !item.TryGetProperty("StartedTimestamp", out var start) || !start.TryGetInt64(out var started)
                    || !item.TryGetProperty("ReturnedTimestamp", out var end) || !end.TryGetInt64(out var returned)
                    || !item.TryGetProperty("TimestampFrequency", out var clock) || !clock.TryGetInt64(out var frequency)
                    || started < 0 || returned < started || frequency <= 0
                    || !item.TryGetProperty("StartedAtUtc", out var startUtc) || !startUtc.TryGetDateTimeOffset(out var startedAtUtc)
                    || !item.TryGetProperty("ReturnedAtUtc", out var endUtc) || !endUtc.TryGetDateTimeOffset(out var returnedAtUtc)) continue;
                StorageOperationTiming.Sink(JsonSerializer.Serialize(new
                {
                    phase = "real.cmdlet", scope = commandKind, cmdlet = name.GetString(), cmdletIndex,
                    startedAtUtc, returnedAtUtc, startedTimestamp = started, returnedTimestamp = returned,
                    timestampFrequency = frequency, elapsedTicks = returned - started,
                    elapsedMs = (returned - started) * 1000.0 / frequency,
                    result.Code, hostIncarnation = call.Incarnation, correlationId = call.RequestId,
                    hostProcessId = call.ProcessId, hostStartedAtUtc = call.StartedAtUtc,
                    buildId = typeof(WindowsRealStorageCommandAdapter).Assembly.ManifestModule.ModuleVersionId,
                    processId = Environment.ProcessId
                }));
            }
            catch (Exception)
            {
                // Missing/invalid diagnostics stay unmeasured; never alter a provider result.
            }
        }
    }

    private static WindowsStorageCommandResult Unknown(string reason, WindowsStorageProcessResult process) =>
        new(true, "adapter.response-outcome-unknown", null, null, null, null, null, null,
            JsonSerializer.Serialize(new
            {
                Reason = reason,
                process.ExitCode,
                StandardError = DiagnosticTail(process.StandardError),
                StandardErrorLength = process.StandardError.Length,
                StandardErrorTruncated = process.StandardError.Length > ProcessDiagnosticLimit,
                StandardOutput = DiagnosticTail(process.StandardOutput),
                StandardOutputLength = process.StandardOutput.Length,
                StandardOutputTruncated = process.StandardOutput.Length > ProcessDiagnosticLimit
            }, JsonOptions));

    private const int ProcessDiagnosticLimit = 16_384;
    private static string DiagnosticTail(string text) =>
        text.Length <= ProcessDiagnosticLimit ? text : text[^ProcessDiagnosticLimit..];

    private static string? GetCommandKind(RealStorageCommand command) => command switch
    {
        SetDiskOnlineCommand => "SetDiskOnline",
        InitializeGptCommand => "InitializeGpt",
        ClearDiskCommand => "ClearDisk",
        CreatePartitionCommand => "CreatePartition",
        DeletePartitionCommand => "DeletePartition",
        ResizePartitionCommand => "ResizePartition",
        FormatVolumeCommand => "FormatVolume",
        SetDriveLetterCommand => "SetDriveLetter",
        RenameVolumeCommand => "RenameVolume",
        CreatePoolCommand => "CreatePool",
        SetPhysicalDiskUsageCommand => "SetPhysicalDiskUsage",
        DeletePoolCommand => "DeletePool",
        RenamePoolCommand => "RenamePool",
        CreateVirtualDiskCommand => "CreateVirtualDisk",
        CreateTieredVirtualDiskCommand => "CreateTieredVirtualDisk",
        DeleteVirtualDiskCommand => "DeleteVirtualDisk",
        ResizeVirtualDiskCommand => "ResizeVirtualDisk",
        RenameVirtualDiskCommand => "RenameVirtualDisk",
        CreateTierCommand => "CreateTier",
        DeleteTierCommand => "DeleteTier",
        ResizeTierCommand => "ResizeTier",
        RenameTierCommand => "RenameTier",
        _ => null
    };

    private static bool IsExactTarget(RealStorageCommand command, WindowsStorageCommandTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.ExpectedFingerprint))
        {
            return false;
        }

        var expected = command switch
        {
            SetDiskOnlineCommand or InitializeGptCommand or ClearDiskCommand or CreatePartitionCommand => StorageObjectKind.OsDisk,
            DeletePartitionCommand or ResizePartitionCommand or FormatVolumeCommand or SetDriveLetterCommand => StorageObjectKind.Partition,
            RenameVolumeCommand => StorageObjectKind.Volume,
            CreatePoolCommand or SetPhysicalDiskUsageCommand => StorageObjectKind.PhysicalDisk,
            DeletePoolCommand or RenamePoolCommand or CreateVirtualDiskCommand or CreateTieredVirtualDiskCommand or CreateTierCommand => StorageObjectKind.StoragePool,
            DeleteVirtualDiskCommand or ResizeVirtualDiskCommand or RenameVirtualDiskCommand => StorageObjectKind.VirtualDisk,
            DeleteTierCommand or ResizeTierCommand or RenameTierCommand => StorageObjectKind.StorageTier,
            _ => StorageObjectKind.LogicalGroup
        };
        if (target.Kind != expected || GetPrimaryReference(command) is not { } reference ||
            reference.Kind != expected || (reference.Existing is { } existing && existing.Kind != expected) ||
            (command is CreateTieredVirtualDiskCommand tiered &&
             (tiered.Tier.Kind != StorageObjectKind.StorageTier ||
              string.IsNullOrWhiteSpace(target.RelatedUniqueId) ||
              string.IsNullOrWhiteSpace(target.RelatedObjectId))) ||
            !IsPartitionRoleCompatible(command, target) || !ExactCreationInputsMatch(command, target))
        {
            return false;
        }

        return expected switch
        {
            StorageObjectKind.OsDisk => target.DiskNumber is >= 0 &&
                !string.IsNullOrWhiteSpace(target.OsDiskUniqueId) &&
                !string.IsNullOrWhiteSpace(target.OsDiskPath),
            StorageObjectKind.Partition => target.DiskNumber is >= 0 && target.PartitionNumber is > 0 &&
                !string.IsNullOrWhiteSpace(target.OsDiskUniqueId) &&
                !string.IsNullOrWhiteSpace(target.OsDiskPath) &&
                !string.IsNullOrWhiteSpace(target.PartitionGuid) &&
                Guid.TryParse(target.PartitionTypeGuid, out _) &&
                target.OffsetBytes is >= 0 && target.SizeBytes is > 0 &&
                !string.IsNullOrWhiteSpace(target.ParentUniqueId),
            StorageObjectKind.Volume => target.DiskNumber is >= 0 && target.PartitionNumber is > 0 &&
                !string.IsNullOrWhiteSpace(target.PartitionGuid) &&
                Guid.TryParse(target.PartitionTypeGuid, out _) &&
                target.OffsetBytes is >= 0 && target.SizeBytes is > 0 &&
                !string.IsNullOrWhiteSpace(target.OsDiskUniqueId) &&
                !string.IsNullOrWhiteSpace(target.OsDiskPath) &&
                !string.IsNullOrWhiteSpace(target.UniqueId) &&
                !string.IsNullOrWhiteSpace(target.ObjectId) &&
                !string.IsNullOrWhiteSpace(target.ParentUniqueId),
            StorageObjectKind.PhysicalDisk => !string.IsNullOrWhiteSpace(target.UniqueId) &&
                !string.IsNullOrWhiteSpace(target.ObjectId) &&
                !string.IsNullOrWhiteSpace(target.SerialNumber) &&
                !string.IsNullOrWhiteSpace(target.StorageSubsystemUniqueId),
            StorageObjectKind.StoragePool => !string.IsNullOrWhiteSpace(target.UniqueId) &&
                !string.IsNullOrWhiteSpace(target.ObjectId) &&
                !string.IsNullOrWhiteSpace(target.PhysicalMemberUniqueId) &&
                !string.IsNullOrWhiteSpace(target.StorageSubsystemUniqueId) &&
                !string.IsNullOrWhiteSpace(target.ParentUniqueId),
            StorageObjectKind.VirtualDisk or StorageObjectKind.StorageTier =>
                !string.IsNullOrWhiteSpace(target.UniqueId) &&
                !string.IsNullOrWhiteSpace(target.ObjectId) &&
                !string.IsNullOrWhiteSpace(target.ParentUniqueId) &&
                !string.IsNullOrWhiteSpace(target.PhysicalMemberUniqueId) &&
                !string.IsNullOrWhiteSpace(target.StorageSubsystemUniqueId),
            _ => false
        };
    }

    private static RealTargetReference? GetPrimaryReference(RealStorageCommand command) => command switch
    {
        SetDiskOnlineCommand value => value.Disk,
        InitializeGptCommand value => value.Disk,
        ClearDiskCommand value => value.Disk,
        CreatePartitionCommand value => value.Disk,
        DeletePartitionCommand value => value.Partition,
        ResizePartitionCommand value => value.Partition,
        FormatVolumeCommand value => value.Partition,
        SetDriveLetterCommand value => value.Partition,
        RenameVolumeCommand value => value.Volume,
        CreatePoolCommand value => value.PhysicalDisk,
        SetPhysicalDiskUsageCommand value => value.PhysicalDisk,
        DeletePoolCommand value => value.Pool,
        RenamePoolCommand value => value.Pool,
        CreateVirtualDiskCommand value => value.Pool,
        CreateTieredVirtualDiskCommand value => value.Pool,
        DeleteVirtualDiskCommand value => value.VirtualDisk,
        ResizeVirtualDiskCommand value => value.VirtualDisk,
        RenameVirtualDiskCommand value => value.VirtualDisk,
        CreateTierCommand value => value.Pool,
        DeleteTierCommand value => value.Tier,
        ResizeTierCommand value => value.Tier,
        RenameTierCommand value => value.Tier,
        _ => null
    };

    private static bool IsSupportedParameters(RealStorageCommand command)
    {
        const long mib = 1024L * 1024;
        return command switch
        {
            ClearDiskCommand value => !value.RemoveOem,
            CreatePartitionCommand value => Enum.IsDefined(value.Role) && value.OffsetBytes >= mib &&
                value.OffsetBytes % mib == 0 && value.SizeBytes > 0 && value.SizeBytes % mib == 0 &&
                value.OffsetBytes <= long.MaxValue - value.SizeBytes,
            ResizePartitionCommand value => value.SizeBytes > 0 && value.SizeBytes % mib == 0,
            FormatVolumeCommand value => Enum.IsDefined(value.FileSystem) &&
                FormatAllocationUnitPolicy.IsKnownClusterSize(value.FileSystem.ToString(), value.ClusterBytes) &&
                !((value.FileSystem is RealFileSystem.Fat32 or RealFileSystem.ReFs) && value.Full),
            SetDriveLetterCommand value => (value.PreviousLetter is not null || value.NewLetter is not null) &&
                value.PreviousLetter != value.NewLetter &&
                IsLetter(value.PreviousLetter) && IsLetter(value.NewLetter),
            CreateVirtualDiskCommand value => (value.UseMaximumSize ? value.SizeBytes == 0 : value.SizeBytes > 0) && ValidLayout(value.EffectiveLayout) && !string.IsNullOrWhiteSpace(value.Name),
            CreateTieredVirtualDiskCommand value => (value.UseMaximumSize ? value.SizeBytes == 0 : value.SizeBytes > 0) && !string.IsNullOrWhiteSpace(value.Name)
                && ValidLayout(value.EffectiveLayout) && (value.CapacityTiers is null || value.CapacityTiers.All(item => item.Layout is null || ValidLayout(item.Layout)))
                && Enum.IsDefined(value.CreationMechanism)
                && (value.CreationMechanism != TieredVirtualDiskCreationMechanism.WindowsAutomaticHdd || value.UseMaximumSize),
            CreateTierCommand value => ValidLayout(value.EffectiveLayout) && value.EffectiveMediaType is "HDD" or "SSD" or "SCM" &&
                !string.IsNullOrWhiteSpace(value.Name),
            SetPhysicalDiskUsageCommand value => value.Usage is PhysicalDiskUsage.AutoSelect or PhysicalDiskUsage.HotSpare or PhysicalDiskUsage.Retired,
            ResizeVirtualDiskCommand value => value.SizeBytes > 0,
            ResizeTierCommand value => value.SizeBytes > 0,
            CreatePoolCommand value => !string.IsNullOrWhiteSpace(value.Name),
            RenamePoolCommand value => !string.IsNullOrWhiteSpace(value.Name),
            RenameVirtualDiskCommand value => !string.IsNullOrWhiteSpace(value.Name),
            RenameTierCommand value => !string.IsNullOrWhiteSpace(value.Name),
            RenameVolumeCommand value => value.Label is not null,
            _ => true
        };
    }

    private static bool ValidLayout(StorageLayoutParameters layout)
    {
        try { layout.Validate(); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool ExactCreationInputsMatch(RealStorageCommand command, WindowsStorageCommandTarget target)
    {
        if (target.PhysicalMembers is { } members && (members.Count == 0
            || members.Any(item => string.IsNullOrWhiteSpace(item.StableId) || string.IsNullOrWhiteSpace(item.UniqueId)
                || string.IsNullOrWhiteSpace(item.ObjectId) || string.IsNullOrWhiteSpace(item.SerialNumber))
            || members.Select(item => item.StableId).Distinct(StringComparer.Ordinal).Count() != members.Count
            || members.Select(item => item.UniqueId).Distinct(StringComparer.Ordinal).Count() != members.Count
            || members.Select(item => item.ObjectId).Distinct(StringComparer.Ordinal).Count() != members.Count)) return false;
        if (command is CreatePoolCommand pool && pool.Members is not null)
            return target.PhysicalMembers is { } exact && pool.EffectiveMembers.All(item => item.Existing is { Kind: StorageObjectKind.PhysicalDisk })
                && pool.EffectiveMembers.Count == exact.Count && pool.EffectiveMembers.Select(item => item.Existing!.Value.ProviderKey).Order(StringComparer.Ordinal)
                    .SequenceEqual(exact.Select(item => item.StableId).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        if (command is SetPhysicalDiskUsageCommand usage)
            return target.PhysicalMembers is { Count: > 0 } usageMembers && !string.IsNullOrWhiteSpace(target.ParentUniqueId)
                && usage.PhysicalDisk.Existing is { } usageId && usageMembers.SingleOrDefault(item => item.StableId == usageId.ProviderKey) is { } usageMember
                && usageMember.UniqueId == target.UniqueId && usageMember.ObjectId == target.ObjectId && usageMember.SerialNumber == target.SerialNumber;
        if (command is CreateVirtualDiskCommand ordinary && target.Layout is { } layout && layout != ordinary.EffectiveLayout) return false;
        if (command is CreateTierCommand tier && target.Layout is { } tierLayout && tierLayout != tier.EffectiveLayout) return false;
        if (command is CreateTieredVirtualDiskCommand { CapacityTiers: { } inputs } vd)
        {
            if (target.TierInputs is not { } bound || bound.Count != inputs.Count || bound.Count == 0
                || bound.Select(item => item.TemplateStableId).Distinct(StringComparer.Ordinal).Count() != bound.Count
                || bound.Select(item => item.UniqueId).Distinct(StringComparer.Ordinal).Count() != bound.Count) return false;
            for (var i = 0; i < inputs.Count; i++)
                if (inputs[i].Tier.Existing?.ProviderKey != bound[i].TemplateStableId || inputs[i].Layout is { } requested && requested != bound[i].Layout
                    || inputs[i].MediaType is { } media && media != bound[i].MediaType
                    || inputs[i].SizeBytes is { } bytes && bytes != bound[i].SizeBytes || bound[i].SizeBytes <= 0) return false;
            try { if (bound.Aggregate(0L, (sum, item) => checked(sum + item.SizeBytes)) != vd.SizeBytes) return false; }
            catch (OverflowException) { return false; }
        }
        return true;
    }

    private static bool IsLetter(char? letter) => letter is null || letter is >= 'D' and <= 'Z';

    private static bool IsPartitionRoleCompatible(RealStorageCommand command, WindowsStorageCommandTarget target)
    {
        if (command is not (FormatVolumeCommand or ResizePartitionCommand or SetDriveLetterCommand))
        {
            return true;
        }

        if (!Guid.TryParse(target.PartitionTypeGuid, out var role))
        {
            return false;
        }

        var basic = Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
        if (command is ResizePartitionCommand or SetDriveLetterCommand)
        {
            return role == basic;
        }

        var format = (FormatVolumeCommand)command;
        if (role == basic)
        {
            return (format.FileSystem is RealFileSystem.Ntfs or RealFileSystem.ExFat or RealFileSystem.ReFs) &&
                FormatAllocationUnitPolicy.IsKnownClusterSize(format.FileSystem.ToString(), format.ClusterBytes)
                && !(format.FileSystem == RealFileSystem.ReFs && format.Full)
                && (format.ClusterBytes == 65536 && format.FileSystem != RealFileSystem.ReFs
                    || !string.IsNullOrWhiteSpace(target.RelatedUniqueId)
                        && !string.IsNullOrWhiteSpace(target.RelatedObjectId));
        }

        var efi = Guid.Parse("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
        if (role == efi)
        {
            return target.CreatedInThisPlan && format.FileSystem == RealFileSystem.Fat32 &&
                format.ClusterBytes == 4096 && !format.Full;
        }

        var recovery = Guid.Parse("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
        return role == recovery && target.CreatedInThisPlan &&
            format.FileSystem == RealFileSystem.Ntfs && format.ClusterBytes == 4096 && !format.Full;
    }
}
