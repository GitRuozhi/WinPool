using System.Diagnostics;
using System.IO.Compression;
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

    private readonly IWindowsStorageWriteProcessRunner _runner;

    public WindowsRealStorageCommandAdapter() : this(new WindowsPowerShellStorageWriteRunner()) { }

    internal WindowsRealStorageCommandAdapter(IWindowsStorageWriteProcessRunner runner) =>
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public async Task<WindowsStorageCommandResult> ExecuteAsync(
        RealStorageCommand command,
        WindowsStorageCommandTarget target,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        token.ThrowIfCancellationRequested();

        if (command is CreateVirtualDiskCommand { UseMaximumSize: true } or CreateTieredVirtualDiskCommand { UseMaximumSize: true })
            return Rejected("adapter.maximum-requires-journaled-explicit-attempt");

        var kind = GetCommandKind(command);
        if (kind is null || !IsExactTarget(command, target) || !IsSupportedParameters(command))
        {
            return Rejected("adapter.closed-command-or-target-required");
        }

        var payload = JsonSerializer.Serialize(new { CommandKind = kind, Command = command, Target = target }, JsonOptions);
        WindowsStorageProcessResult process;
        try
        {
            process = await _runner.RunAsync(WindowsRealStoragePowerShellScript.Source, payload, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The production runner throws this only before the process starts.
            throw;
        }
        catch (Exception)
        {
            // The runner may have started a provider call before failing to report.
            return new WindowsStorageCommandResult(true, "adapter.process-outcome-unknown",
                null, null, null, null, null, null, "The write process outcome is unknown; reconcile read-only.");
        }

        if (string.IsNullOrWhiteSpace(process.StandardOutput))
        {
            return Unknown("The write process returned no structured result.");
        }

        try
        {
            var result = JsonSerializer.Deserialize<WindowsStorageCommandResult>(process.StandardOutput.Trim(), JsonOptions);
            if (result is null || string.IsNullOrWhiteSpace(result.Code))
            {
                return Unknown("The write process result was incomplete.");
            }

            if (process.ExitCode != 0)
            {
                return Unknown("The write process exited abnormally; a storage call may have changed the target.");
            }

            return result switch
            {
                { Code: "provider.returned", ProviderReturned: true } => result,
                { Code: "adapter.preflight-rejected", ProviderReturned: false } => result,
                { Code: "provider.error-outcome-unknown", ProviderReturned: true } => result,
                _ => Unknown("The write process returned an inconsistent result.")
            };
        }
        catch (JsonException)
        {
            return Unknown("The write process returned invalid structured data.");
        }
    }

    private static WindowsStorageCommandResult Rejected(string code) =>
        new(false, code, null, null, null, null, null, null, null);

    private static WindowsStorageCommandResult Unknown(string reason) =>
        new(true, "adapter.response-outcome-unknown", null, null, null, null, null, null, reason);

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
            CreatePoolCommand => StorageObjectKind.PhysicalDisk,
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
            !IsPartitionRoleCompatible(command, target))
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
                (value.ClusterBytes == 65536 ||
                 (value.ClusterBytes == 4096 &&
                  (value.FileSystem == RealFileSystem.Fat32 || value.FileSystem == RealFileSystem.Ntfs))) &&
                !((value.FileSystem is RealFileSystem.Fat32 or RealFileSystem.ReFs) && value.Full),
            SetDriveLetterCommand value => (value.PreviousLetter is not null || value.NewLetter is not null) &&
                value.PreviousLetter != value.NewLetter &&
                IsLetter(value.PreviousLetter) && IsLetter(value.NewLetter),
            CreateVirtualDiskCommand value => (value.UseMaximumSize ? value.SizeBytes == 0 : value.SizeBytes > 0) && value.InterleaveBytes == 65536 &&
                value.DataColumns == 1 && !string.IsNullOrWhiteSpace(value.Name),
            CreateTieredVirtualDiskCommand value => (value.UseMaximumSize ? value.SizeBytes == 0 : value.SizeBytes > 0) && !string.IsNullOrWhiteSpace(value.Name)
                && Enum.IsDefined(value.CreationMechanism)
                && (value.CreationMechanism != TieredVirtualDiskCreationMechanism.WindowsAutomaticHdd || value.UseMaximumSize),
            CreateTierCommand value => value.InterleaveBytes == 65536 && value.DataColumns == 1 &&
                !string.IsNullOrWhiteSpace(value.Name),
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
                format.ClusterBytes == 65536 && (format.FileSystem != RealFileSystem.ReFs
                    || !format.Full && !target.CreatedInThisPlan
                        && format.Partition.Existing is not null
                        && !string.IsNullOrWhiteSpace(target.RelatedUniqueId)
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

internal sealed class WindowsPowerShellStorageWriteRunner : IWindowsStorageWriteProcessRunner
{
    public async Task<WindowsStorageProcessResult> RunAsync(
        string fixedScript,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = WindowsPowerShellRunner.ExecutablePath;
        if (!StringComparer.Ordinal.Equals(fixedScript, WindowsRealStoragePowerShellScript.Source))
        {
            throw new InvalidOperationException("Only the embedded storage write script may run.");
        }
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Windows PowerShell 5.1 is required.", executable);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-EncodedCommand");
        process.StartInfo.ArgumentList.Add(EncodeFixedCompressedScript(fixedScript));

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start Windows PowerShell 5.1.");
        }

        // After process creation no caller cancellation or fixed timer kills a possible write.
        // The Agent persists CallIssued before this point and reconciles on lost output.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(payloadJson.AsMemory(), CancellationToken.None).ConfigureAwait(false);
        process.StandardInput.Close();
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return new WindowsStorageProcessResult(process.ExitCode,
            await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
    }

    internal static string EncodeFixedCompressedScript(string script)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(script));
        }

        var compressed = Convert.ToBase64String(buffer.ToArray());
        var bootstrap = "$b=[Convert]::FromBase64String('" + compressed +
            "');$m=[IO.MemoryStream]::new($b);$g=[IO.Compression.GZipStream]::new($m,[IO.Compression.CompressionMode]::Decompress);" +
            "$r=[IO.StreamReader]::new($g,[Text.UTF8Encoding]::new($false));" +
            "$s=$r.ReadToEnd();& ([ScriptBlock]::Create($s))";
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
    }
}
