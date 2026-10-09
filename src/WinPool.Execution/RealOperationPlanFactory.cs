using WinPool.Domain;

namespace WinPool.Execution;

public static class RealOperationPlanFactory
{
    public const int FormatVersion = 1;
    public const string AdapterVersion = "windows-storage-1";
    public static readonly AlgorithmIdentity Algorithm = new(
        "ALGO-REAL-PLAN-001", "1.0.0", AlgorithmConfidence.Derived, "docs/Archive/20261007-real-edit-stage1/Plan-history.md#6-类型化计划实时校验与适配器");

    public static OperationPlan Create(
        RealOperationIntentRequest proposal,
        OperationId operationId,
        EnvironmentProfile environment,
        TrustedRealSession session,
        string inventoryVersion,
        string targetFingerprint,
        string physicalMemberFingerprint,
        string supportEvidence,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(session);
        if (environment.Kind != EnvironmentKind.LocalMachine || !session.IsArmed || !session.IsWellFormed ||
            operationId.Value == Guid.Empty || proposal.SystemId.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(environment.MachineBinding) ||
            string.IsNullOrWhiteSpace(inventoryVersion) ||
            string.IsNullOrWhiteSpace(targetFingerprint) ||
            string.IsNullOrWhiteSpace(physicalMemberFingerprint) ||
            string.IsNullOrWhiteSpace(supportEvidence) ||
            expiresAt <= createdAt || expiresAt > createdAt.Add(InMemoryOperationAuthority.MaximumLifetime))
        {
            throw new ArgumentException("A current local environment, armed session, identity, evidence and short expiry are required.");
        }

        RealOperationValidator.Validate(proposal);
        var definition = OperationSecurityCatalog.Get(proposal.Intent);
        var risk = proposal.Steps.Any(step => step.Command is ClearDiskCommand or DeletePoolCommand)
            ? RiskLevel.R5IrreversibleOrBroadDestruction
            : definition.MinimumRisk;
        var request = new OperationRequest(
            operationId, environment.Id, proposal.SystemId, proposal.Intent,
            proposal.Targets.ToArray(), new Dictionary<string, string>(), createdAt);
        var descriptionSteps = proposal.Steps.Select(step =>
            new PlanStep(step.Id, step.Command.GetType().Name, step.DependsOn.ToArray(), true));
        var plan = OperationPlan.Create(
            request, definition.RequiredCapabilities, risk, inventoryVersion,
            ["Re-read authoritative live inventory and resolve exact identities before each call."],
            descriptionSteps, null, definition.ImpactScope,
            "Completed real storage steps remain in effect; stop and reconcile before any repair plan.",
            string.Join("; ", proposal.Steps.Select(step => step.DataLoss).Where(value => !string.IsNullOrWhiteSpace(value))),
            Algorithm, createdAt);
        var real = new RealOperationSpecification(
            FormatVersion, AdapterVersion, environment.MachineBinding, session.Binding,
            targetFingerprint, physicalMemberFingerprint, supportEvidence, proposal.ExpectedFinalState, expiresAt,
            proposal.Steps.Select(step => step with
            {
                DependsOn = step.DependsOn.ToArray(),
                Command = step.Command is CreateTieredVirtualDiskCommand { CapacityTiers: { } tiers } tiered
                    ? tiered with { CapacityTiers = tiers.ToArray() } : step.Command
            }).ToArray());
        plan = plan with { RealOperation = real };
        return plan with { PlanHash = OperationPlanHasher.Compute(plan) };
    }
}

public static class RealOperationValidator
{
    public const string ClearDiskExpectedFinalState = "Disk is RAW with zero partitions";

    public static void Validate(RealOperationIntentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperationSecurityCatalog.IsStorageStructureMutation(request.Intent) ||
            request.Intent is OperationIntent.RepairStorageObject or OperationIntent.RawDeviceWrite ||
            request.Targets is null || request.Targets.Count == 0 ||
            request.Targets.Any(target => target.System != request.SystemId) ||
            (request.Targets.Count(target => target.Kind == StorageObjectKind.PhysicalDisk) > 1 &&
                request.Steps?.Any(step => step.Command is CreateTieredVirtualDiskCommand
                    { UseMaximumSize: true, CapacityTiers.Count: > 1 }) != true) ||
            request.Targets.Distinct().Count() != request.Targets.Count ||
            request.Steps is null || request.Steps.Count == 0 ||
            string.IsNullOrWhiteSpace(request.ExpectedFinalState))
        {
            throw new ArgumentException("The real operation proposal has an unsupported intent, target or expected state.");
        }

        if (request.Intent == OperationIntent.ClearDisk)
        {
            var diskTargets = request.Targets.Where(target => target.Kind == StorageObjectKind.OsDisk).ToArray();
            if (diskTargets.Length != 1 || request.Targets.Count is < 1 or > 2 ||
                request.Targets.Any(target => target != diskTargets[0] && target.Kind != StorageObjectKind.PhysicalDisk) ||
                request.Steps.Count != 1 ||
                request.Steps[0].Command is not ClearDiskCommand
                {
                    RemoveOem: false,
                    Disk: { CreatedByStep: null, Existing: { } }
                } clear ||
                clear.Disk.Existing != diskTargets[0] ||
                request.Steps[0].DependsOn is not { Count: 0 } ||
                !StringComparer.Ordinal.Equals(request.ExpectedFinalState, ClearDiskExpectedFinalState))
            {
                throw new ArgumentException("A standalone clear requires one exact existing OS disk, one R5 clear step, and a RAW zero-partition result.");
            }
        }

        // Search recovery owns one parent macro. Other work (including partitions)
        // is prepared separately after its verified identities and final capacity.
        if (request.Steps.Count != 1 && request.Steps.Any(step => step.Command is
                CreateVirtualDiskCommand { MaximumCapacity: not null } or
                CreateTieredVirtualDiskCommand { MaximumCapacity: not null }))
            throw new ArgumentException("A maximum-capacity search must be its own confirmed plan.");

        var prior = new Dictionary<string, HashSet<StorageObjectKind>>(StringComparer.Ordinal);
        var commandsById = new Dictionary<string, RealStorageCommand>(StringComparer.Ordinal);
        string? previousStep = null;
        foreach (var step in request.Steps)
        {
            if (step is null || string.IsNullOrWhiteSpace(step.Id) || prior.ContainsKey(step.Id) ||
                step.Command is null || step.DependsOn is null ||
                string.IsNullOrWhiteSpace(step.BeforeCondition) ||
                string.IsNullOrWhiteSpace(step.AfterCondition) ||
                string.IsNullOrWhiteSpace(step.SupportEvidence) ||
                ((step.Command is ClearDiskCommand or DeletePoolCommand or DeleteVirtualDiskCommand or
                    DeletePartitionCommand or FormatVolumeCommand) && string.IsNullOrWhiteSpace(step.DataLoss)) ||
                step.DependsOn.Distinct(StringComparer.Ordinal).Count() != step.DependsOn.Count ||
                step.DependsOn.Any(dependency => !prior.ContainsKey(dependency)) ||
                (previousStep is not null && !step.DependsOn.Contains(previousStep, StringComparer.Ordinal)))
            {
                throw new ArgumentException("Every real step needs unique identity, known earlier dependencies, conditions and support evidence.");
            }

            if (!IsAllowed(request.Intent, step.Command))
            {
                throw new ArgumentException("The real step is not permitted for this operation intent.");
            }

            var (reference, expectedKind) = GetTarget(step.Command);
            ValidateTarget(reference, expectedKind, request, step, prior);
            if (step.Command is CreateTieredVirtualDiskCommand tiered)
            {
                ValidateTarget(tiered.Tier, StorageObjectKind.StorageTier, request, step, prior);
                if (tiered.CapacityTiers is { } tiers)
                {
                    if (!tiered.UseMaximumSize || tiers.Count < 2 || tiers[0].Tier != tiered.Tier ||
                        tiers.Select(item => item.Tier).Distinct().Count() != tiers.Count)
                        throw new ArgumentException("Multi-tier MAX needs distinct ordered exact templates and an agreeing first template.");
                    foreach (var item in tiers)
                    {
                        ValidateTarget(item.Tier, StorageObjectKind.StorageTier, request, step, prior);
                        ValidateMaximumCapacityPolicy(item.MaximumCapacity, true);
                    }
                }
            }
            ValidateCommand(step.Command);
            ValidateCreatedPartitionUse(step.Command, commandsById);
            prior.Add(step.Id, ProducedKinds(step.Command));
            commandsById.Add(step.Id, step.Command);
            previousStep = step.Id;
        }

        if (request.Steps.Count(step => step.Command is CreatePoolCommand) > 1 ||
            request.Steps.Count(step => step.Command is CreateVirtualDiskCommand or CreateTieredVirtualDiskCommand) > 1 ||
            request.Steps.Count(step => step.Command is DeletePoolCommand) > 1 ||
            (request.Steps.Any(step => step.Command is CreatePoolCommand) &&
             request.Targets.Count(target => target.Kind == StorageObjectKind.PhysicalDisk) != 1) ||
            (request.Intent == OperationIntent.DeleteStoragePool &&
             request.Steps.Count(step => step.Command is DeletePoolCommand) != 1) ||
            (request.Intent == OperationIntent.RebuildStoragePool &&
             !request.Steps.Any(step => step.Command is ClearDiskCommand or DeletePoolCommand or DeleteVirtualDiskCommand or DeleteTierCommand)))
        {
            throw new ArgumentException("This stage allows one physical member, one new virtual disk and explicit listed removal steps only.");
        }

        if (request.Intent == OperationIntent.RebuildStoragePool)
        {
            var firstRemoval = request.Steps.ToList().FindIndex(step =>
                step.Command is ClearDiskCommand or DeletePoolCommand or DeleteVirtualDiskCommand or DeleteTierCommand);
            var firstCreation = request.Steps.ToList().FindIndex(step =>
                step.Command is CreatePoolCommand or CreateVirtualDiskCommand or CreateTieredVirtualDiskCommand or CreateTierCommand);
            if (firstCreation >= 0 && firstCreation < firstRemoval)
            {
                throw new ArgumentException("A rebuild must list removal before replacement creation.");
            }
        }

        foreach (var created in request.Steps.Where(step =>
                     step.Command is CreatePartitionCommand { Role: RealPartitionRole.Efi or RealPartitionRole.Recovery }))
        {
            if (!request.Steps.Any(step => step.Command is FormatVolumeCommand format &&
                format.Partition.CreatedByStep == created.Id))
            {
                throw new ArgumentException("A new EFI or Recovery partition requires its explicit fixed format step.");
            }
        }

        if (request.Intent == OperationIntent.InitializeDisk)
            ValidateInitializationShape(request);
    }

    public static bool IsValid(OperationPlan plan)
    {
        var real = plan.RealOperation;
        if (real is null || real.FormatVersion != RealOperationPlanFactory.FormatVersion ||
            real.Steps is null || real.Steps.Any(step => step is null || step.Command is null) ||
            plan.Steps is null || plan.Steps.Any(step => step is null) || plan.Targets is null ||
            plan.Parameters is null || plan.Parameters.Count != 0 ||
            plan.PlannerAlgorithm != RealOperationPlanFactory.Algorithm ||
            real.AdapterVersion != RealOperationPlanFactory.AdapterVersion ||
            string.IsNullOrWhiteSpace(real.MachineBinding) ||
            string.IsNullOrWhiteSpace(real.SessionBinding) ||
            string.IsNullOrWhiteSpace(real.TargetFingerprint) ||
            string.IsNullOrWhiteSpace(real.PhysicalMemberFingerprint) ||
            string.IsNullOrWhiteSpace(real.SupportEvidence) ||
            real.ExpiresAt <= plan.CreatedAt ||
            real.ExpiresAt > plan.CreatedAt.Add(InMemoryOperationAuthority.MaximumLifetime) ||
            (real.Steps.Any(step => step.Command is ClearDiskCommand or DeletePoolCommand) &&
             plan.Risk < RiskLevel.R5IrreversibleOrBroadDestruction) ||
            plan.Steps.Count != real.Steps.Count)
        {
            return false;
        }

        try
        {
            Validate(new RealOperationIntentRequest(plan.Intent, plan.SystemId, plan.Targets, real.Steps, real.ExpectedFinalState));
            return plan.Steps.Select(step => step.Id).SequenceEqual(real.Steps.Select(step => step.Id), StringComparer.Ordinal) &&
                plan.Steps.Zip(real.Steps).All(pair =>
                    pair.First.DependsOn.SequenceEqual(pair.Second.DependsOn, StringComparer.Ordinal) &&
                    pair.First.Action == pair.Second.Command.GetType().Name && pair.First.IsCancellationBoundary);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateTarget(
        RealTargetReference reference,
        StorageObjectKind expected,
        RealOperationIntentRequest request,
        RealOperationStep step,
        Dictionary<string, HashSet<StorageObjectKind>> prior)
    {
        if (reference is null || reference.Kind != expected ||
            (reference.Existing.HasValue == !string.IsNullOrWhiteSpace(reference.CreatedByStep)))
        {
            throw new ArgumentException("A real target must have one exact source and the expected object kind.");
        }

        if (reference.Existing is { } existing)
        {
            if (existing.System != request.SystemId || existing.Kind != expected || !request.Targets.Contains(existing))
            {
                throw new ArgumentException("A real step refers to an unlisted existing target.");
            }
        }
        else if (!prior.TryGetValue(reference.CreatedByStep!, out var outputs) ||
                 !outputs.Contains(expected) ||
                 !step.DependsOn.Contains(reference.CreatedByStep, StringComparer.Ordinal))
        {
            throw new ArgumentException("A created target must reference a prior dependency with the right output kind.");
        }
    }

    private static (RealTargetReference, StorageObjectKind) GetTarget(RealStorageCommand command) => command switch
    {
        SetDiskOnlineCommand value => (value.Disk, StorageObjectKind.OsDisk),
        InitializeGptCommand value => (value.Disk, StorageObjectKind.OsDisk),
        ClearDiskCommand value => (value.Disk, StorageObjectKind.OsDisk),
        CreatePartitionCommand value => (value.Disk, StorageObjectKind.OsDisk),
        DeletePartitionCommand value => (value.Partition, StorageObjectKind.Partition),
        ResizePartitionCommand value => (value.Partition, StorageObjectKind.Partition),
        FormatVolumeCommand value => (value.Partition, StorageObjectKind.Partition),
        SetDriveLetterCommand value => (value.Partition, StorageObjectKind.Partition),
        RenameVolumeCommand value => (value.Volume, StorageObjectKind.Volume),
        CreatePoolCommand value => (value.PhysicalDisk, StorageObjectKind.PhysicalDisk),
        DeletePoolCommand value => (value.Pool, StorageObjectKind.StoragePool),
        RenamePoolCommand value => (value.Pool, StorageObjectKind.StoragePool),
        CreateVirtualDiskCommand value => (value.Pool, StorageObjectKind.StoragePool),
        DeleteVirtualDiskCommand value => (value.VirtualDisk, StorageObjectKind.VirtualDisk),
        ResizeVirtualDiskCommand value => (value.VirtualDisk, StorageObjectKind.VirtualDisk),
        RenameVirtualDiskCommand value => (value.VirtualDisk, StorageObjectKind.VirtualDisk),
        CreateTierCommand value => (value.Pool, StorageObjectKind.StoragePool),
        CreateTieredVirtualDiskCommand value => (value.Pool, StorageObjectKind.StoragePool),
        DeleteTierCommand value => (value.Tier, StorageObjectKind.StorageTier),
        ResizeTierCommand value => (value.Tier, StorageObjectKind.StorageTier),
        RenameTierCommand value => (value.Tier, StorageObjectKind.StorageTier),
        _ => throw new ArgumentException("Unknown real storage command.")
    };

    private static HashSet<StorageObjectKind> ProducedKinds(RealStorageCommand command) => command switch
    {
        ClearDiskCommand => [StorageObjectKind.OsDisk],
        InitializeGptCommand => [StorageObjectKind.OsDisk],
        CreatePartitionCommand => [StorageObjectKind.Partition],
        FormatVolumeCommand => [StorageObjectKind.Volume],
        CreatePoolCommand => [StorageObjectKind.StoragePool],
        CreateVirtualDiskCommand => [StorageObjectKind.VirtualDisk, StorageObjectKind.OsDisk],
        CreateTierCommand => [StorageObjectKind.StorageTier],
        CreateTieredVirtualDiskCommand => [StorageObjectKind.VirtualDisk, StorageObjectKind.OsDisk],
        _ => []
    };

    private static bool IsAllowed(OperationIntent intent, RealStorageCommand command) => intent switch
    {
        OperationIntent.SetDiskOnlineState => command is SetDiskOnlineCommand,
        OperationIntent.InitializeDisk => command is InitializeGptCommand or DeletePartitionCommand or CreatePartitionCommand or FormatVolumeCommand or SetDriveLetterCommand,
        OperationIntent.ConvertDisk => command is ClearDiskCommand or InitializeGptCommand or CreatePartitionCommand,
        OperationIntent.ClearDisk => command is ClearDiskCommand,
        OperationIntent.CreatePartition => command is CreatePartitionCommand or FormatVolumeCommand or SetDriveLetterCommand,
        OperationIntent.DeletePartition => command is DeletePartitionCommand,
        OperationIntent.ResizePartition => command is ResizePartitionCommand,
        OperationIntent.FormatVolume => command is FormatVolumeCommand,
        OperationIntent.SetDriveLetter => command is SetDriveLetterCommand,
        OperationIntent.SetVolumeLabel => command is RenameVolumeCommand,
        OperationIntent.CreateStoragePool => command is CreatePoolCommand or CreateVirtualDiskCommand or InitializeGptCommand or CreatePartitionCommand or FormatVolumeCommand or SetDriveLetterCommand,
        OperationIntent.DeleteStoragePool => command is DeleteVirtualDiskCommand or DeleteTierCommand or DeletePoolCommand,
        OperationIntent.CreateVirtualDisk => command is CreateVirtualDiskCommand or CreateTieredVirtualDiskCommand or InitializeGptCommand or CreatePartitionCommand or FormatVolumeCommand or SetDriveLetterCommand,
        OperationIntent.DeleteVirtualDisk => command is DeleteVirtualDiskCommand,
        OperationIntent.ResizeVirtualDisk => command is ResizeVirtualDiskCommand,
        OperationIntent.CreateStorageTier => command is CreateTierCommand,
        OperationIntent.DeleteStorageTier => command is DeleteTierCommand,
        OperationIntent.ResizeStorageTier => command is ResizeTierCommand,
        OperationIntent.RenameStorageObject => command is RenamePoolCommand or RenameVirtualDiskCommand or RenameTierCommand,
        OperationIntent.RebuildStoragePool => command is ClearDiskCommand or DeleteVirtualDiskCommand or DeleteTierCommand or DeletePoolCommand or CreatePoolCommand or CreateTierCommand or CreateVirtualDiskCommand or CreateTieredVirtualDiskCommand or InitializeGptCommand or CreatePartitionCommand or FormatVolumeCommand or SetDriveLetterCommand,
        _ => false
    };

    private static void ValidateInitializationShape(RealOperationIntentRequest request)
    {
        var disks = request.Targets.Where(target => target.Kind == StorageObjectKind.OsDisk).ToArray();
        var partitions = request.Targets.Where(target => target.Kind == StorageObjectKind.Partition).ToArray();
        if (disks.Length != 1 || request.Targets.Any(target => target.Kind is not
                (StorageObjectKind.OsDisk or StorageObjectKind.Partition or StorageObjectKind.PhysicalDisk)))
            throw new ArgumentException("Initialization requires one exact existing OS disk and its listed normalization target only.");
        var disk = RealTargetReference.ForExisting(disks[0]);
        var steps = request.Steps;
        if (steps[0].Command is InitializeGptCommand initialize)
        {
            // Retain the historical two-step shape for read-only recovery. New
            // initialization proposals should observe Windows' GPT result first.
            if (initialize.Disk != disk || partitions.Length != 0 || steps.Count > 2 ||
                (steps.Count == 2 && steps[1].Command is not CreatePartitionCommand
                    { Role: RealPartitionRole.Msr } ) ||
                (steps.Count == 2 && ((CreatePartitionCommand)steps[1].Command).Disk != disk))
                throw new ArgumentException("RAW initialization permits only GPT initialization and its explicit MSR step.");
            return;
        }

        // A separately confirmed continuation can normalize one provider MSR,
        // then build one optional MSR and one data partition on that same disk.
        // The Windows planner must prove the deleted target's live role,
        // geometry, absence of a file system, and sole-partition status.
        var index = 0;
        if (steps[index].Command is DeletePartitionCommand remove)
        {
            if (partitions.Length != 1 || remove.Partition != RealTargetReference.ForExisting(partitions[0]))
                throw new ArgumentException("GPT normalization deletes one exact listed provider MSR.");
            index++;
        }
        else if (partitions.Length != 0)
            throw new ArgumentException("Initialization cannot include unrelated existing partitions.");

        var hasMsr = false;
        if (index < steps.Count && steps[index].Command is CreatePartitionCommand { Role: RealPartitionRole.Msr } msr)
        {
            if (msr.Disk != disk) throw new ArgumentException("The replacement MSR must use the exact initialized disk.");
            hasMsr = true;
            index++;
        }
        string? dataStep = null;
        if (index < steps.Count && steps[index].Command is CreatePartitionCommand { Role: RealPartitionRole.BasicData } data)
        {
            if (data.Disk != disk || data.OffsetBytes != (hasMsr ? 17L : 1L) * 1048576)
                throw new ArgumentException("The initialization data partition must follow the selected MSR layout on the same disk.");
            dataStep = steps[index++].Id;
        }
        if (index < steps.Count && steps[index].Command is FormatVolumeCommand format)
        {
            if (dataStep is null || format.Partition != RealTargetReference.FromStep(StorageObjectKind.Partition, dataStep) ||
                format.FileSystem != RealFileSystem.Ntfs || format.ClusterBytes != 65536 || format.Full)
                throw new ArgumentException("Automatic initialization formats only its new data partition with quick NTFS/64 KiB.");
            index++;
        }
        if (index < steps.Count && steps[index].Command is SetDriveLetterCommand letter)
        {
            if (dataStep is null || letter.Partition != RealTargetReference.FromStep(StorageObjectKind.Partition, dataStep) ||
                letter.PreviousLetter is not null || letter.NewLetter is null)
                throw new ArgumentException("Automatic initialization assigns a letter only to its new data partition.");
            index++;
        }
        if (index != steps.Count)
            throw new ArgumentException("Initialization continuation must follow delete-MSR, create-MSR, create-data, format and letter order.");
    }

    private static void ValidateCommand(RealStorageCommand command)
    {
        const long mebibyte = 1024L * 1024;
        if (command is CreateVirtualDiskCommand ordinary)
            ValidateMaximumCapacityPolicy(ordinary.MaximumCapacity, ordinary.UseMaximumSize);
        if (command is CreateTieredVirtualDiskCommand tiered)
            ValidateMaximumCapacityPolicy(tiered.MaximumCapacity, tiered.UseMaximumSize);
        switch (command)
        {
            case ClearDiskCommand { RemoveOem: true }:
                throw new ArgumentException("RemoveOEM is outside the default real command set.");
            case CreatePartitionCommand value when value.OffsetBytes < mebibyte || value.OffsetBytes % mebibyte != 0 ||
                value.SizeBytes <= 0 || value.SizeBytes % mebibyte != 0 || value.OffsetBytes > long.MaxValue - value.SizeBytes ||
                !Enum.IsDefined(value.Role) ||
                (value.Role == RealPartitionRole.Msr && (value.OffsetBytes != mebibyte || value.SizeBytes != 16 * mebibyte)):
                throw new ArgumentException("Partition geometry must be explicit whole MiB and within representable bounds.");
            case ResizePartitionCommand value when value.SizeBytes <= 0 || value.SizeBytes % mebibyte != 0:
                throw new ArgumentException("Partition size must be a positive whole MiB.");
            case FormatVolumeCommand value when value.ClusterBytes != 65536 &&
                !((value.FileSystem == RealFileSystem.Fat32 || value.FileSystem == RealFileSystem.Ntfs) && value.ClusterBytes == 4096) ||
                (value.FileSystem == RealFileSystem.ReFs && value.Full) ||
                (value.FileSystem == RealFileSystem.Fat32 && value.Full) ||
                !Enum.IsDefined(value.FileSystem):
                throw new ArgumentException("This format parameter combination is not enabled for real execution.");
            case SetDriveLetterCommand value when
                (value.PreviousLetter is null && value.NewLetter is null) ||
                (value.PreviousLetter is { } previous && (previous < 'D' || previous > 'Z')) ||
                (value.NewLetter is { } next && (next < 'D' || next > 'Z')) ||
                (value.PreviousLetter is not null && value.PreviousLetter == value.NewLetter):
                throw new ArgumentException("The exact previous and requested drive letters must differ and be valid local letters.");
            case CreateVirtualDiskCommand value when (value.UseMaximumSize ? value.SizeBytes != 0 : value.SizeBytes <= 0) || value.InterleaveBytes != 65536 || value.DataColumns != 1 || string.IsNullOrWhiteSpace(value.Name):
                throw new ArgumentException("Only single-column 64 KiB Simple/Fixed virtual disks are enabled.");
            case ResizeVirtualDiskCommand value when value.SizeBytes <= 0:
                throw new ArgumentException("The virtual disk size is required.");
            case CreateTierCommand value when value.InterleaveBytes != 65536 || value.DataColumns != 1 || string.IsNullOrWhiteSpace(value.Name):
                throw new ArgumentException("Only single-column 64 KiB HDD tiers are enabled.");
            case CreateTieredVirtualDiskCommand value when (value.UseMaximumSize ? value.SizeBytes != 0 : value.SizeBytes <= 0) || string.IsNullOrWhiteSpace(value.Name)
                || !Enum.IsDefined(value.CreationMechanism)
                || value.CreationMechanism == TieredVirtualDiskCreationMechanism.WindowsAutomaticHdd && !value.UseMaximumSize:
                throw new ArgumentException("A tiered virtual disk needs an exclusive explicit size or maximum search intent and a name.");
            case ResizeTierCommand value when value.SizeBytes <= 0:
                throw new ArgumentException("A tier size is required.");
            case CreatePoolCommand value when string.IsNullOrWhiteSpace(value.Name):
                throw new ArgumentException("A pool name is required.");
            case RenamePoolCommand value when string.IsNullOrWhiteSpace(value.Name):
                throw new ArgumentException("A pool name is required.");
            case RenameVirtualDiskCommand value when string.IsNullOrWhiteSpace(value.Name):
                throw new ArgumentException("A virtual disk name is required.");
            case RenameTierCommand value when string.IsNullOrWhiteSpace(value.Name):
                throw new ArgumentException("A tier name is required.");
            case RenameVolumeCommand value when value.Label is null:
                throw new ArgumentException("A volume label is required.");
        }
    }

    private static void ValidateMaximumCapacityPolicy(MaximumCapacityPolicy? policy, bool useMaximumSize)
    {
        // An App proposal and historical plans may omit this. The Windows planner
        // must supply fresh inputs for all newly prepared MAX execution plans.
        if (policy is null) return;
        if (!useMaximumSize || policy.AlgorithmVersion != MaximumCapacityAlgorithm.Version ||
            policy.InitialCandidateBytes <= 0 ||
            policy.InitialCandidateBytes != MaximumCapacityAlgorithm.InitialCandidateBytes(policy.UpperBoundBytes) ||
            policy.PhysicalCapacityBytes <= 0 || policy.MaximumAttempts <= 0 ||
            string.IsNullOrWhiteSpace(policy.Source) || string.IsNullOrWhiteSpace(policy.SourceFingerprint) ||
            policy.CapturedAtUtc == default)
            throw new ArgumentException("The frozen maximum-capacity policy is invalid.");
    }

    private static void ValidateCreatedPartitionUse(
        RealStorageCommand command,
        IReadOnlyDictionary<string, RealStorageCommand> commandsById)
    {
        if (command is FormatVolumeCommand format)
        {
            if (format.Partition.CreatedByStep is { } source &&
                commandsById[source] is CreatePartitionCommand created)
            {
                var valid = created.Role switch
                {
                    RealPartitionRole.BasicData =>
                        (format.FileSystem is RealFileSystem.Ntfs or RealFileSystem.ExFat or RealFileSystem.ReFs) &&
                        format.ClusterBytes == 65536,
                    RealPartitionRole.Efi => format.FileSystem == RealFileSystem.Fat32 && format.ClusterBytes == 4096 && !format.Full,
                    RealPartitionRole.Recovery => format.FileSystem == RealFileSystem.Ntfs && format.ClusterBytes == 4096 && !format.Full,
                    _ => false
                };
                if (!valid)
                {
                    throw new ArgumentException("The file system and cluster size do not match the created partition role.");
                }
            }
            else if (format.Partition.Existing is not null &&
                     (format.FileSystem == RealFileSystem.Fat32 || format.ClusterBytes != 65536))
            {
                throw new ArgumentException("Existing EFI and Recovery formatting are outside this stage.");
            }
        }

        if (command is SetDriveLetterCommand letter &&
            letter.Partition.CreatedByStep is { } partitionStep &&
            commandsById[partitionStep] is CreatePartitionCommand createdPartition &&
            createdPartition.Role != RealPartitionRole.BasicData)
        {
            throw new ArgumentException("New EFI, MSR and Recovery partitions do not receive automatic drive letters.");
        }
    }
}
