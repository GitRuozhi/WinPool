using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

/// <summary>Session-only evidence for continuing one automatic layout without replaying verified writes.</summary>
public sealed class RealAutomaticLayoutProgress
{
    private readonly RealOperationIntentRequest original;
    private readonly HashSet<string> verified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> created = new(StringComparer.Ordinal);
    public string VirtualDiskId { get; }
    public PoolEditIntent Intent { get; }
    public bool RequiresReconciliation { get; private set; }

    public RealAutomaticLayoutProgress(RealOperationIntentRequest layout, string virtualDiskId, PoolEditIntent intent)
    {
        RealOperationValidator.Validate(layout);
        if (layout.Intent != OperationIntent.InitializeDisk || layout.Steps.Any(step => step.Command is not
            (DeletePartitionCommand or CreatePartitionCommand or FormatVolumeCommand or SetDriveLetterCommand)))
            throw new ArgumentException("Only the fixed automatic layout can be continued.");
        original = layout;
        VirtualDiskId = virtualDiskId;
        Intent = intent;
    }

    public void Observe(RealOperationIntentRequest submitted, AgentRealOperationResponse response)
    {
        if (submitted.SystemId != original.SystemId || response.Plan.SystemId != original.SystemId
            || response.Plan.RealOperation is not { } real
            || submitted.Steps.Any(step => !original.Steps.Any(item => item.Id == step.Id)
                || !real.Steps.Any(item => item.Id == step.Id && item.Command == step.Command))
            || real.Steps.Count != submitted.Steps.Count)
            throw new InvalidDataException("Layout progress does not belong to the submitted exact plan.");
        RequiresReconciliation |= response.RequiresReconciliation || response.State == RealOperationState.OutcomeUnknown
            || response.Steps.Any(step => step.State == RealOperationStepState.OutcomeUnknown);
        foreach (var step in submitted.Steps)
        {
            var result = response.Steps.SingleOrDefault(item => item.StepId == step.Id);
            if (result?.State != RealOperationStepState.Verified) continue;
            if (step.Command is CreatePartitionCommand)
            {
                using var evidence = JsonDocument.Parse(result.ResultEvidence ?? "{}");
                if (!evidence.RootElement.TryGetProperty("CreatedObjectId", out var value)
                    || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    throw new InvalidDataException("A verified partition creation has no exact output identity.");
                var id = value.GetString()!;
                if (created.TryGetValue(step.Id, out var previous) && previous != id)
                    throw new InvalidDataException("A verified partition identity changed.");
                created[step.Id] = id;
            }
            verified.Add(step.Id);
        }
        var encounteredPending = false;
        foreach (var step in original.Steps)
        {
            if (!verified.Contains(step.Id)) encounteredPending = true;
            else if (encounteredPending) throw new InvalidDataException("Verified layout steps are not an ordered prefix.");
        }
    }

    private static bool MatchesRole(PartitionInfo partition, RealPartitionRole role)
    {
        var expected = role switch
        {
            RealPartitionRole.Msr => Guid.Parse("e3c9e316-0b5c-4db8-817d-f92df00215ae"),
            RealPartitionRole.BasicData => Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"),
            _ => Guid.Empty
        };
        return expected != Guid.Empty && Guid.TryParse(partition.PartitionTypeId, out var type) && type == expected
            && Guid.TryParse(partition.GptType, out var gpt) && gpt == expected;
    }

    public RealOperationIntentRequest? BuildNext(StorageSystemDocument fresh, PoolEditIntent current)
    {
        if (RequiresReconciliation) throw new InvalidOperationException("The layout outcome needs reconciliation before any further write.");
        if (fresh.SystemId != original.SystemId || current.VerifiedVirtualDiskId != VirtualDiskId
            || !current.PendingAutomaticLayout || !current.AutoCreatePartition
            || current.CreateMsr != Intent.CreateMsr || current.VolumeName != Intent.VolumeName
            || current.DriveLetter != Intent.DriveLetter || current.FileSystem != Intent.FileSystem
            || current.AllocationUnitSize != Intent.AllocationUnitSize || current.QuickFormat != Intent.QuickFormat
            || current.PartitionStyle != Intent.PartitionStyle)
            throw new InvalidOperationException("The remaining layout must keep this session's original verified target and parameters.");
        var diskTarget = original.Targets.Single(target => target.Kind == StorageObjectKind.OsDisk);
        var snapshot = fresh.Snapshot;
        var disk = snapshot.OsDisks.SingleOrDefault(item => item.StableId == diskTarget.ProviderKey);
        if (disk is null || disk.VirtualDiskStableId != VirtualDiskId || !disk.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase)
            || fresh.SourceFacts?.Objects.SingleOrDefault(item => item.Id == disk.StableId) is not { HasReliableIdentity: true })
            throw new InvalidDataException("The remaining layout has no exact fresh GPT disk associated with the verified VD.");
        var expectedPartitions = new HashSet<string>(created.Values, StringComparer.Ordinal);
        foreach (var step in original.Steps)
        {
            if (step.Command is DeletePartitionCommand { Partition.Existing: { } old })
            {
                var exists = snapshot.Partitions.Any(partition => partition.StableId == old.ProviderKey);
                if (verified.Contains(step.Id) && exists)
                    throw new InvalidDataException("The verified MSR deletion is not reflected in fresh facts.");
                if (!verified.Contains(step.Id)) expectedPartitions.Add(old.ProviderKey);
            }
            if (step.Command is not CreatePartitionCommand create || !verified.Contains(step.Id)) continue;
            var id = created[step.Id];
            var partition = snapshot.Partitions.SingleOrDefault(item => item.StableId == id);
            if (partition is not { IsStable: true } || partition.OsDiskStableId != disk.StableId
                || partition.Offset != create.OffsetBytes || partition.Size != create.SizeBytes
                || !MatchesRole(partition, create.Role)
                || fresh.SourceFacts?.Objects.SingleOrDefault(item => item.Id == id) is not { HasReliableIdentity: true })
                throw new InvalidDataException("A verified partition output is absent or has changed its exact association or geometry.");
        }
        if (snapshot.Partitions.Any(partition => partition.OsDiskStableId == disk.StableId && !expectedPartitions.Contains(partition.StableId)))
            throw new InvalidDataException("Unexpected partitions cannot be adopted into the remaining layout.");
        foreach (var step in original.Steps.Where(step => verified.Contains(step.Id)))
        {
            if (step.Command is FormatVolumeCommand format)
            {
                var partition = RequirePartition(format.Partition);
                if (!partition.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                    || partition.AllocationUnitSize != format.ClusterBytes || partition.FileSystemLabel != (format.Label ?? ""))
                    throw new InvalidDataException("The verified format no longer matches fresh facts; it will not be replayed.");
            }
            if (step.Command is SetDriveLetterCommand letter
                && RequirePartition(letter.Partition).DriveLetter.TrimEnd(':') != letter.NewLetter?.ToString())
                throw new InvalidDataException("The verified drive letter no longer matches fresh facts.");
        }
        var remaining = original.Steps.FirstOrDefault(step => !verified.Contains(step.Id));
        if (remaining is null) return null;
        RealTargetReference Resolve(RealTargetReference reference) => reference.CreatedByStep is { } source
            ? RealTargetReference.ForExisting(new(original.SystemId, reference.Kind,
                created.TryGetValue(source, out var output) ? output : throw new InvalidDataException("The exact verified creation output is unavailable.")))
            : reference;
        RealStorageCommand command = remaining.Command switch
        {
            DeletePartitionCommand delete => delete with { Partition = Resolve(delete.Partition) },
            CreatePartitionCommand create => create with { Disk = Resolve(create.Disk) },
            FormatVolumeCommand format => format with { Partition = Resolve(format.Partition) },
            SetDriveLetterCommand letter => letter with { Partition = Resolve(letter.Partition) },
            _ => throw new NotSupportedException("The remaining automatic layout command is unsupported.")
        };
        var intent = command switch
        {
            DeletePartitionCommand => OperationIntent.InitializeDisk,
            CreatePartitionCommand { Role: RealPartitionRole.Msr } => OperationIntent.InitializeDisk,
            CreatePartitionCommand => OperationIntent.CreatePartition,
            FormatVolumeCommand => OperationIntent.FormatVolume,
            SetDriveLetterCommand => OperationIntent.SetDriveLetter,
            _ => throw new NotSupportedException()
        };
        var target = command switch
        {
            DeletePartitionCommand delete => delete.Partition.Existing!.Value,
            CreatePartitionCommand create => create.Disk.Existing!.Value,
            FormatVolumeCommand format => format.Partition.Existing!.Value,
            SetDriveLetterCommand letter => letter.Partition.Existing!.Value,
            _ => throw new NotSupportedException()
        };
        StorageObjectId[] targets = command is DeletePartitionCommand ? [diskTarget, target] : [target];
        var next = new RealOperationIntentRequest(intent, original.SystemId, targets,
            [remaining with { Command = command, DependsOn = [] }], remaining.AfterCondition);
        RealOperationValidator.Validate(next);
        return next;

        PartitionInfo RequirePartition(RealTargetReference reference)
        {
            var id = reference.Existing?.ProviderKey ?? (reference.CreatedByStep is { } source
                ? created.GetValueOrDefault(source) : null);
            return snapshot.Partitions.SingleOrDefault(item => item.StableId == id)
                ?? throw new InvalidDataException("The verified layout partition is absent from fresh facts.");
        }
    }
}
