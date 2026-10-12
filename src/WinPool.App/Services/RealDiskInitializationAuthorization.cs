using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

/// <summary>One confirmed RAW-to-GPT action and only its receipt-proven reserved layout.</summary>
public sealed class RealDiskInitializationAuthorization : IRealOperationAuthorization
{
    private readonly StorageObjectId disk;
    private readonly bool createMsr;
    private RealOperationIntentRequest? expected;
    private AgentRealOperationResponse? prepared;
    private StorageObjectId? physical;
    private string? machine;
    private string? agentSession;
    private string? memberFingerprint;
    private string? postFingerprint;

    public RealDiskInitializationAuthorization(StorageObjectId disk, bool createMsr)
    {
        if (disk.Kind != StorageObjectKind.OsDisk) throw new ArgumentException("An exact OS disk is required.");
        this.disk = disk;
        this.createMsr = createMsr;
        expected = RealOperationProposalFactory.InitializeGpt(disk.System, disk);
    }

    public RealOperationIntentRequest Initialization => expected is { Steps.Count: 1 } proposal
        && proposal.Steps[0].Command is InitializeGptCommand ? proposal
        : throw new InvalidOperationException("Initialization has already finished.");

    public void ValidateProposal(RealOperationIntentRequest proposal)
    {
        RealOperationValidator.Validate(proposal);
        if (expected is null || proposal.SystemId != disk.System || proposal.Intent != expected.Intent
            || !proposal.Targets.ToHashSet().SetEquals(expected.Targets) || !SameSteps(proposal.Steps, expected.Steps))
            throw new InvalidDataException("The initialization action cannot change or repeat its confirmed scope.");
    }

    public void ValidateFrozen(RealOperationIntentRequest proposal, AgentRealOperationResponse frozen)
    {
        ValidateProposal(proposal);
        var plan = frozen.Plan;
        var real = plan.RealOperation;
        var anchors = plan.Targets.Where(target => target.Kind == StorageObjectKind.PhysicalDisk).ToArray();
        if (frozen.State != RealOperationState.Prepared || real is null || anchors.Length != 1
            || !RealOperationValidator.IsValid(plan) || plan.PlanHash != OperationPlanHasher.Compute(plan)
            || plan.SystemId != disk.System || plan.Intent != proposal.Intent
            || !plan.Targets.ToHashSet().SetEquals(proposal.Targets.Append(anchors[0]))
            || !SameSteps(real.Steps, proposal.Steps)
            || physical is { } previous && previous != anchors[0]
            || machine is not null && machine != real.MachineBinding
            || agentSession is not null && agentSession != real.SessionBinding
            || memberFingerprint is not null && memberFingerprint != real.PhysicalMemberFingerprint
            || postFingerprint is not null && postFingerprint != real.TargetFingerprint
            || prepared is not null && (prepared.Plan.OperationId != plan.OperationId || prepared.Plan.PlanHash != plan.PlanHash))
            throw new InvalidDataException("The frozen initialization plan changed its disk, commands or live binding.");
        physical ??= anchors[0];
        machine ??= real.MachineBinding;
        agentSession ??= real.SessionBinding;
        memberFingerprint ??= real.PhysicalMemberFingerprint;
        prepared = frozen;
    }

    public RealOperationIntentRequest? ObserveInitialized(AgentRealOperationResponse response, StorageSystemDocument fresh)
    {
        var proposal = Initialization;
        if (prepared is null || response.Plan.OperationId != prepared.Plan.OperationId
            || response.Plan.PlanHash != prepared.Plan.PlanHash || response.State != RealOperationState.Succeeded
            || response.RequiresReconciliation || response.Steps.Count != 1
            || response.Steps[0].StepId != proposal.Steps[0].Id
            || response.Steps[0].State != RealOperationStepState.Verified || fresh.SystemId != disk.System)
            throw new InvalidDataException("Initialization lacks a complete verified result.");
        ValidateFrozen(proposal, response with { State = RealOperationState.Prepared });
        var target = fresh.Snapshot.OsDisks.SingleOrDefault(item => item.StableId == disk.ProviderKey);
        if (target is null || !target.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase)
            || fresh.SourceFacts?.Objects.SingleOrDefault(item => item.Id == disk.ProviderKey) is not { HasReliableIdentity: true })
            throw new InvalidDataException("The initialized disk is missing from fresh GPT inventory.");
        using var evidence = JsonDocument.Parse(response.Steps[0].ResultEvidence ?? "{}");
        var root = evidence.RootElement;
        if (Read(root, "PhysicalMemberFingerprint") != memberFingerprint
            || Read(root, "PostFingerprint") is not { Length: > 0 } post
            || !root.TryGetProperty("GptInitialization", out var gpt)
            || Read(gpt, "OsDiskStableId") != disk.ProviderKey)
            throw new InvalidDataException("The initialization receipt does not prove this disk and result.");
        var partitions = fresh.Snapshot.Partitions.Where(item => item.OsDiskStableId == disk.ProviderKey).ToArray();
        var providerMsr = Read(gpt, "ProviderMsrStableId");
        StorageObjectId? msrTarget = null;
        if (!string.IsNullOrWhiteSpace(providerMsr))
        {
            if (partitions.Length != 1 || partitions[0] is not { IsStable: true } msr
                || msr.StableId != providerMsr || !msr.Type.Equals("MicrosoftReserved", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(msr.PartitionTypeId, out var type)
                || type != new Guid("e3c9e316-0b5c-4db8-817d-f92df00215ae")
                || msr.IsBoot || msr.IsSystem || msr.Offset != 17408 || msr.Size != 16759808
                || !string.IsNullOrWhiteSpace(msr.DriveLetter) || !string.IsNullOrWhiteSpace(msr.FileSystem)
                || !string.IsNullOrWhiteSpace(msr.FileSystemLabel)
                || !Guid.TryParse(msr.Guid, out var partitionGuid)
                || !Guid.TryParse(Read(gpt, "ProviderMsrGuid"), out var receiptGuid) || partitionGuid != receiptGuid
                || !gpt.TryGetProperty("ProviderMsrOffsetBytes", out var offset) || offset.GetInt64() != msr.Offset
                || !gpt.TryGetProperty("ProviderMsrSizeBytes", out var size) || size.GetInt64() != msr.Size
                || fresh.Snapshot.Volumes.Any(volume => volume.PartitionStableId == msr.StableId))
                throw new InvalidDataException("Only the receipt-proven provider MSR may be normalized automatically.");
            msrTarget = new(disk.System, StorageObjectKind.Partition, providerMsr);
        }
        else if (partitions.Length != 0)
            throw new InvalidDataException("Unexpected partitions appeared after initialization.");
        postFingerprint = post;
        prepared = null;
        expected = RealOperationProposalFactory.ConfigureInitializedDisk(disk.System, disk, msrTarget, createMsr);
        return expected;
    }

    public void Stop() { expected = null; prepared = null; }

    private static string? Read(JsonElement value, string name) => value.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static bool SameSteps(IReadOnlyList<RealOperationStep> left, IReadOnlyList<RealOperationStep> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Id == pair.Second.Id
            && pair.First.DependsOn.SequenceEqual(pair.Second.DependsOn)
            && JsonSerializer.Serialize<RealStorageCommand>(pair.First.Command) == JsonSerializer.Serialize<RealStorageCommand>(pair.Second.Command));
}
