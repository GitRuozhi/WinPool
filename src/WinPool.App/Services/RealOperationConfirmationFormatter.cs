using System.Globalization;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

/// <summary>Turns the Agent-frozen typed plan into a concise, human-readable confirmation.</summary>
public static class RealOperationConfirmationFormatter
{
    public static string Format(OperationPlan plan, bool chinese, WinPoolFacts? sourceFacts = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var real = plan.RealOperation ?? throw new ArgumentException(
            "The Agent response does not contain a real operation plan.", nameof(plan));
        var culture = CultureInfo.GetCultureInfo(chinese ? "zh-CN" : "en-US");
        var createdTargets = BuildCreatedTargetNames(real.Steps, chinese, culture);
        string Target(RealTargetReference value) => ResolveTarget(value, sourceFacts, createdTargets, chinese);

        var lines = new List<string>
        {
            chinese ? "请核对本次目标、变更和影响：" : "Review the targets, changes, and effects:"
        };
        foreach (var step in real.Steps)
        {
            var change = DescribeChange(step.Command, Target, chinese, culture);
            var impact = DescribeImpact(step.Command, step.DataLoss, sourceFacts, chinese);
            lines.Add($"• {change}");
            if (!string.IsNullOrWhiteSpace(impact))
                lines.Add($"  {(chinese ? "数据影响" : "Data impact")}: {impact}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatStatus(
        AgentRealOperationResponse status, bool chinese, WinPoolFacts? sourceFacts = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        var real = status.Plan.RealOperation ?? throw new ArgumentException(
            "The Agent response does not contain a real operation plan.", nameof(status));
        var culture = CultureInfo.GetCultureInfo(chinese ? "zh-CN" : "en-US");
        var createdTargets = BuildCreatedTargetNames(real.Steps, chinese, culture);
        string Target(RealTargetReference value) => ResolveTarget(value, sourceFacts, createdTargets, chinese);
        var progressById = status.Steps.ToDictionary(item => item.StepId, StringComparer.Ordinal);
        var lines = new List<string>
        {
            $"{(chinese ? "操作状态" : "Operation status")}: {State(status.State, chinese)}",
            $"{(chinese ? "需要核对" : "Needs review")}: {(status.RequiresReconciliation ? (chinese ? "是" : "Yes") : (chinese ? "否" : "No"))}",
            chinese ? "本次变更：" : "Changes:"
        };
        foreach (var step in real.Steps)
        {
            progressById.TryGetValue(step.Id, out var progress);
            lines.Add($"• {DescribeChange(step.Command, Target, chinese, culture)} — {State(progress?.State, chinese)}");
            var impact = DescribeImpact(step.Command, step.DataLoss, sourceFacts, chinese);
            if (!string.IsNullOrWhiteSpace(impact))
                lines.Add($"  {(chinese ? "数据影响" : "Data impact")}: {impact}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatStopConfirmation(
        AgentRealOperationResponse status, bool chinese, WinPoolFacts? sourceFacts = null)
    {
        var summary = FormatStatus(status, chinese, sourceFacts);
        var caveat = chinese
            ? "停止只阻止尚未开始的后续步骤。正在执行的 Windows 调用可能继续，已完成的步骤不会回滚。"
            : "Stopping prevents only later steps from starting. A Windows call already in progress may continue, and completed steps are not rolled back.";
        return summary + Environment.NewLine + Environment.NewLine + caveat;
    }

    public static string FormatTarget(StorageObjectId target, bool chinese, WinPoolFacts? sourceFacts = null) =>
        ResolveExistingTarget(target, sourceFacts, chinese);

    private static string DescribeChange(
        RealStorageCommand command,
        Func<RealTargetReference, string> target,
        bool chinese,
        CultureInfo culture)
    {
        string Q(string? value) => string.IsNullOrWhiteSpace(value)
            ? (chinese ? "（未指定）" : "(unspecified)")
            : $"“{value}”";
        string Bytes(long value) => $"{value.ToString("N0", culture)} bytes";
        string Capacity(long value) => $"{Bytes(value)} ({(value / (1024d * 1024 * 1024)).ToString("0.##", culture)} GiB)";
        string MaxSize(bool useMaximum, long bytes, MaximumCapacityPolicy? policy)
        {
            if (!useMaximum)
                return Capacity(bytes);
            if (policy is null)
                return chinese ? "最大可用容量（执行时测定）" : "maximum available capacity (measured during the operation)";
            return chinese
                ? $"最大可用容量查找（起始候选 {Capacity(policy.InitialCandidateBytes)}）"
                : $"maximum-capacity search (starting candidate {Capacity(policy.InitialCandidateBytes)})";
        }
        string TierSearch(IReadOnlyList<MaximumCapacityTier>? tiers)
        {
            if (tiers is not { Count: > 1 }) return string.Empty;
            var details = tiers.Select(item => item.MaximumCapacity is { } policy
                ? $"{target(item.Tier)}: {MaxSize(true, 0, policy)}"
                : target(item.Tier));
            return (chinese ? "；各层目标：" : "; tier targets: ") + string.Join("；", details);
        }
        string Layout(int interleave, int columns) => chinese
            ? $"Simple/Fixed 布局，{Bytes(interleave)} 交错大小，{columns} 列"
            : $"Simple/Fixed layout, {Bytes(interleave)} interleave, {columns} column(s)";

        return command switch
        {
            SetDiskOnlineCommand value => chinese
                ? $"{target(value.Disk)}：设为{(value.Online ? "联机" : "脱机")}。"
                : $"Set {target(value.Disk)} {(value.Online ? "online" : "offline")}.",
            InitializeGptCommand value => chinese
                ? $"将 {target(value.Disk)} 初始化为 GPT。"
                : $"Initialize {target(value.Disk)} as GPT.",
            ClearDiskCommand value => chinese
                ? $"将 {target(value.Disk)} 的分区结构清空为 RAW{(value.RemoveOem ? "（包含 OEM 分区）" : "（保留 OEM 分区）")}。"
                : $"Clear the partition layout on {target(value.Disk)} to RAW{(value.RemoveOem ? " (including OEM partitions)" : " (keeping OEM partitions)")}.",
            CreatePartitionCommand value => chinese
                ? $"在 {target(value.Disk)} 上创建{Role(value.Role, true)}分区：起点 {Bytes(value.OffsetBytes)}，容量 {Capacity(value.SizeBytes)}。"
                : $"Create a {Role(value.Role, false)} partition on {target(value.Disk)} at {Bytes(value.OffsetBytes)}, with capacity {Capacity(value.SizeBytes)}.",
            DeletePartitionCommand value => chinese
                ? $"删除 {target(value.Partition)}。"
                : $"Delete {target(value.Partition)}.",
            ResizePartitionCommand value => chinese
                ? $"将 {target(value.Partition)} 的目标总容量调整为 {Capacity(value.SizeBytes)}。"
                : $"Set the total capacity of {target(value.Partition)} to {Capacity(value.SizeBytes)}.",
            FormatVolumeCommand value => chinese
                ? $"格式化 {target(value.Partition)} 为 {value.FileSystem}，分配单元 {Bytes(value.ClusterBytes)}，{(value.Full ? "完整格式化" : "快速格式化")}，卷标 {Q(value.Label)}。"
                : $"Format {target(value.Partition)} as {value.FileSystem}, with {Bytes(value.ClusterBytes)} allocation units, {(value.Full ? "full format" : "quick format")}, label {Q(value.Label)}.",
            SetDriveLetterCommand value => chinese
                ? $"将 {target(value.Partition)} 的盘符从 {Letter(value.PreviousLetter, true)} 改为 {Letter(value.NewLetter, true)}。"
                : $"Change the drive letter on {target(value.Partition)} from {Letter(value.PreviousLetter, false)} to {Letter(value.NewLetter, false)}.",
            RenameVolumeCommand value => chinese
                ? $"将 {target(value.Volume)} 的卷标改为 {Q(value.Label)}。"
                : $"Rename the volume label on {target(value.Volume)} to {Q(value.Label)}.",
            CreatePoolCommand value => chinese
                ? $"在 {target(value.PhysicalDisk)} 上创建存储池 {Q(value.Name)}。"
                : $"Create storage pool {Q(value.Name)} on {target(value.PhysicalDisk)}.",
            DeletePoolCommand value => chinese
                ? $"删除存储池 {target(value.Pool)}。"
                : $"Delete storage pool {target(value.Pool)}.",
            RenamePoolCommand value => chinese
                ? $"将存储池 {target(value.Pool)} 改名为 {Q(value.Name)}。"
                : $"Rename storage pool {target(value.Pool)} to {Q(value.Name)}.",
            CreateVirtualDiskCommand value => chinese
                ? $"在存储池 {target(value.Pool)} 中创建虚拟磁盘 {Q(value.Name)}，容量 {MaxSize(value.UseMaximumSize, value.SizeBytes, value.MaximumCapacity)}；{Layout(value.InterleaveBytes, value.DataColumns)}。"
                : $"Create virtual disk {Q(value.Name)} in {target(value.Pool)}, capacity {MaxSize(value.UseMaximumSize, value.SizeBytes, value.MaximumCapacity)}; {Layout(value.InterleaveBytes, value.DataColumns)}.",
            DeleteVirtualDiskCommand value => chinese
                ? $"删除虚拟磁盘 {target(value.VirtualDisk)}。"
                : $"Delete virtual disk {target(value.VirtualDisk)}.",
            ResizeVirtualDiskCommand value => chinese
                ? $"将虚拟磁盘 {target(value.VirtualDisk)} 的目标总容量调整为 {Capacity(value.SizeBytes)}。"
                : $"Set the total capacity of virtual disk {target(value.VirtualDisk)} to {Capacity(value.SizeBytes)}.",
            RenameVirtualDiskCommand value => chinese
                ? $"将虚拟磁盘 {target(value.VirtualDisk)} 改名为 {Q(value.Name)}。"
                : $"Rename virtual disk {target(value.VirtualDisk)} to {Q(value.Name)}.",
            CreateTierCommand value => chinese
                ? $"在存储池 {target(value.Pool)} 中创建存储层 {Q(value.Name)}；{Layout(value.InterleaveBytes, value.DataColumns)}。"
                : $"Create storage tier {Q(value.Name)} in {target(value.Pool)}; {Layout(value.InterleaveBytes, value.DataColumns)}.",
            CreateTieredVirtualDiskCommand value => chinese
                ? $"在存储池 {target(value.Pool)} 中创建 Simple/Fixed 虚拟磁盘 {Q(value.Name)}，容量 {MaxSize(value.UseMaximumSize, value.SizeBytes, value.MaximumCapacity)}，目标层 {target(value.Tier)}{TierSearch(value.CapacityTiers)}。"
                : $"Create a Simple/Fixed virtual disk {Q(value.Name)} in {target(value.Pool)}, capacity {MaxSize(value.UseMaximumSize, value.SizeBytes, value.MaximumCapacity)}, using target tier {target(value.Tier)}{TierSearch(value.CapacityTiers)}.",
            DeleteTierCommand value => chinese
                ? $"删除存储层 {target(value.Tier)}。"
                : $"Delete storage tier {target(value.Tier)}.",
            ResizeTierCommand value => chinese
                ? $"将存储层 {target(value.Tier)} 的目标总容量调整为 {Capacity(value.SizeBytes)}。"
                : $"Set the total capacity of storage tier {target(value.Tier)} to {Capacity(value.SizeBytes)}.",
            RenameTierCommand value => chinese
                ? $"将存储层 {target(value.Tier)} 改名为 {Q(value.Name)}。"
                : $"Rename storage tier {target(value.Tier)} to {Q(value.Name)}.",
            _ => throw new ArgumentException("The Agent plan contains an unsupported real command.", nameof(command))
        };
    }

    private static string? DescribeImpact(
        RealStorageCommand command, string dataLoss, WinPoolFacts? sourceFacts, bool chinese)
    {
        string? CurrentSize(RealTargetReference target) => target.Existing is { } existing
            ? NumberText(FindObject(existing, sourceFacts), "Size")
            : null;
        return command switch
        {
            ClearDiskCommand => chinese
                ? "现有分区和卷结构将无法继续访问。"
                : "Existing partitions and volumes will no longer be accessible.",
            InitializeGptCommand => chinese
                ? "将写入 GPT 分区表；若目标已有数据结构，该结构可能失效。"
                : "A GPT partition table will be written; any existing data layout may become unusable.",
            CreatePartitionCommand => chinese
                ? "所选未分配范围将被占用，不能再用于其他分区。"
                : "The selected unallocated range will no longer be available for another partition.",
            DeletePartitionCommand => chinese
                ? "分区及其中数据将无法继续访问。"
                : "The partition and its data will no longer be accessible.",
            FormatVolumeCommand => chinese
                ? "目标卷中的现有数据将被擦除。"
                : "Existing data on the target volume will be erased.",
            ResizePartitionCommand value when dataLoss.Contains("Shrinking", StringComparison.OrdinalIgnoreCase)
                || long.TryParse(CurrentSize(value.Partition), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var partitionSize) && value.SizeBytes < partitionSize => chinese
                    ? "压缩可能使新边界之外的数据无法访问。"
                    : "Shrinking may make data beyond the new boundary inaccessible.",
            ResizeVirtualDiskCommand value when dataLoss.Contains("Shrinking", StringComparison.OrdinalIgnoreCase)
                || long.TryParse(CurrentSize(value.VirtualDisk), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var diskSize) && value.SizeBytes < diskSize => chinese
                    ? "缩小可能使新容量之外的数据无法访问。"
                    : "Shrinking may make data beyond the new capacity inaccessible.",
            ResizeTierCommand value when dataLoss.Contains("Shrinking", StringComparison.OrdinalIgnoreCase)
                || long.TryParse(CurrentSize(value.Tier), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var tierSize) && value.SizeBytes < tierSize => chinese
                    ? "缩小可能使新容量之外的数据无法访问。"
                    : "Shrinking may make data beyond the new capacity inaccessible.",
            ResizePartitionCommand or ResizeVirtualDiskCommand or ResizeTierCommand => chinese
                ? "目标容量会改变；扩容不预期丢失数据。Agent 会在执行前核对支持范围。"
                : "Capacity will change; expansion is not expected to lose data. The Agent checks the supported range before execution.",
            CreatePoolCommand => chinese
                ? "物理磁盘上的现有分区和数据将无法继续访问。"
                : "Existing partitions and data on the physical disk will no longer be accessible.",
            DeletePoolCommand => chinese
                ? "存储池元数据将被移除；池内数据可能无法恢复。"
                : "Storage pool metadata will be removed; data in the pool may become inaccessible.",
            DeleteVirtualDiskCommand => chinese
                ? "虚拟磁盘及其分区、卷和文件将丢失。"
                : "The virtual disk and its partitions, volumes, and files will be lost.",
            DeleteTierCommand => chinese
                ? "存储层元数据将被移除。"
                : "Storage tier metadata will be removed.",
            CreateTieredVirtualDiskCommand { UseMaximumSize: true, CapacityTiers.Count: > 1 } => chinese
                ? "若后续容量测定停止，已完成的容量分配仍会保留，不会自动回滚。"
                : "If later capacity measurement stops, completed allocations remain in place and are not rolled back.",
            CreateVirtualDiskCommand or CreateTieredVirtualDiskCommand => chinese
                ? "所请求的容量将从存储池分配。"
                : "The requested capacity will be allocated from the storage pool.",
            CreateTierCommand => chinese
                ? "存储池容量将用于新存储层。"
                : "Storage pool capacity will be used by the new tier.",
            _ when dataLoss.Contains("No ", StringComparison.OrdinalIgnoreCase) => chinese
                ? "不预期造成额外数据丢失。"
                : "No additional data loss is expected.",
            _ => null
        };
    }

    private static string ResolveTarget(
        RealTargetReference target,
        WinPoolFacts? sourceFacts,
        IReadOnlyDictionary<string, string> createdTargets,
        bool chinese)
    {
        if (target.Existing is { } existing)
            return ResolveExistingTarget(existing, sourceFacts, chinese);
        if (target.CreatedByStep is { } stepId && createdTargets.TryGetValue(stepId, out var name))
            return name;
        return chinese ? $"本次新建的{Kind(target.Kind, true)}" : $"new {Kind(target.Kind, false)} from this operation";
    }

    private static string ResolveExistingTarget(StorageObjectId target, WinPoolFacts? facts, bool chinese)
    {
        var item = FindObject(target, facts);
        if (item is null)
            return chinese
                ? $"{Kind(target.Kind, true)}（{target.ProviderKey}）"
                : $"{Kind(target.Kind, false)} ({target.ProviderKey})";

        string? Field(string name) => Text(item, name);
        var parts = target.Kind switch
        {
            StorageObjectKind.PhysicalDisk => new[]
            {
                Field("DeviceId") is { } device ? $"{(chinese ? "磁盘" : "Disk")} {device}" : null,
                First(Field("Model"), Field("FriendlyName")),
                Field("SerialNumber") is { } serial ? $"{(chinese ? "序列号" : "S/N")} {serial}" : null
            },
            StorageObjectKind.OsDisk => new[]
            {
                Field("Number") is { } number ? $"{(chinese ? "磁盘" : "Disk")} {number}" : null,
                Field("FriendlyName"),
                Field("SerialNumber") is { } serial ? $"{(chinese ? "序列号" : "S/N")} {serial}" : null
            },
            StorageObjectKind.Partition => new[]
            {
                Field("DiskNumber") is { } disk ? $"{(chinese ? "磁盘" : "Disk")} {disk}" : null,
                Field("PartitionNumber") is { } partition ? $"{(chinese ? "分区" : "partition")} {partition}" : null,
                Field("DriveLetter") is { Length: > 0 } letter ? $"{letter}:" : null,
                Field("FileSystemLabel")
            },
            StorageObjectKind.Volume => new[]
            {
                Field("DriveLetter") is { Length: > 0 } letter ? $"{letter}:" : null,
                Field("FileSystemLabel"),
                Field("FileSystem")
            },
            StorageObjectKind.StoragePool or StorageObjectKind.StorageTier or StorageObjectKind.VirtualDisk
                or StorageObjectKind.StorageSubsystem => new[] { Field("FriendlyName") },
            _ => []
        };
        var description = string.Join(" · ", parts.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(description))
            return chinese
                ? $"{Kind(target.Kind, true)}（{target.ProviderKey}）"
                : $"{Kind(target.Kind, false)} ({target.ProviderKey})";
        if (target.Kind is StorageObjectKind.StoragePool or StorageObjectKind.StorageTier
            or StorageObjectKind.VirtualDisk)
        {
            var friendlyName = Field("FriendlyName");
            var sameNameCount = string.IsNullOrWhiteSpace(friendlyName)
                ? 0
                : facts?.Objects.Count(other => other.ObjectType == item.ObjectType
                    && StringComparer.OrdinalIgnoreCase.Equals(Text(other, "FriendlyName"), friendlyName)) ?? 0;
            if (string.IsNullOrWhiteSpace(friendlyName) || sameNameCount > 1)
                description += chinese ? $" · ID {target.ProviderKey}" : $" · ID {target.ProviderKey}";
        }
        return $"{Kind(target.Kind, chinese)} — {description}";
    }

    private static WinPoolSourceObject? FindObject(StorageObjectId target, WinPoolFacts? facts)
    {
        if (facts is null || facts.SystemId != target.System) return null;
        var expectedType = target.Kind switch
        {
            StorageObjectKind.StorageSubsystem => FactObjectType.StorageSubsystem,
            StorageObjectKind.StoragePool => FactObjectType.StoragePool,
            StorageObjectKind.StorageTier => FactObjectType.StorageTier,
            StorageObjectKind.PhysicalDisk => FactObjectType.PhysicalDisk,
            StorageObjectKind.VirtualDisk => FactObjectType.VirtualDisk,
            StorageObjectKind.OsDisk => FactObjectType.Disk,
            StorageObjectKind.Partition => FactObjectType.Partition,
            StorageObjectKind.Volume => FactObjectType.Volume,
            _ => (FactObjectType?)null
        };
        return expectedType is { } type
            ? facts.Objects.FirstOrDefault(item => item.ObjectType == type
                && StringComparer.Ordinal.Equals(item.Id, target.ProviderKey))
            : null;
    }

    private static string? Text(WinPoolSourceObject item, string fieldName)
    {
        var field = item.Field(fieldName);
        return field?.ReadState == FieldReadState.Returned
            && field.Value is { } value && value.ValueKind != System.Text.Json.JsonValueKind.Null
                ? field.DisplayValue()
                : null;
    }

    private static string? NumberText(WinPoolSourceObject? item, string fieldName) =>
        item is null ? null : Text(item, fieldName);

    private static IReadOnlyDictionary<string, string> BuildCreatedTargetNames(
        IReadOnlyList<RealOperationStep> steps, bool chinese, CultureInfo culture)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var description = step.Command switch
            {
                CreatePartitionCommand value => chinese
                    ? $"新建{Role(value.Role, true)}分区（起点 {value.OffsetBytes.ToString("N0", culture)} bytes，容量 {value.SizeBytes.ToString("N0", culture)} bytes）"
                    : $"new {Role(value.Role, false)} partition (offset {value.OffsetBytes.ToString("N0", culture)} bytes, capacity {value.SizeBytes.ToString("N0", culture)} bytes)",
                CreatePoolCommand value => chinese ? $"存储池“{value.Name}”" : $"storage pool “{value.Name}”",
                CreateVirtualDiskCommand value => chinese ? $"虚拟磁盘“{value.Name}”" : $"virtual disk “{value.Name}”",
                CreateTieredVirtualDiskCommand value => chinese ? $"虚拟磁盘“{value.Name}”" : $"virtual disk “{value.Name}”",
                CreateTierCommand value => chinese ? $"存储层“{value.Name}”" : $"storage tier “{value.Name}”",
                _ => null
            };
            if (description is not null)
                result[step.Id] = description;
        }
        return result;
    }

    private static string State(RealOperationState state, bool chinese) => state switch
    {
        RealOperationState.Prepared => chinese ? "等待确认" : "Awaiting confirmation",
        RealOperationState.Accepted => chinese ? "已接受" : "Accepted",
        RealOperationState.Running => chinese ? "执行中" : "Running",
        RealOperationState.Succeeded => chinese ? "已完成" : "Completed",
        RealOperationState.Rejected => chinese ? "未执行" : "Not applied",
        RealOperationState.Cancelled => chinese ? "已取消" : "Cancelled",
        RealOperationState.Failed => chinese ? "执行失败" : "Failed",
        RealOperationState.PartiallyCompleted => chinese ? "部分完成" : "Partially completed",
        RealOperationState.OutcomeUnknown => chinese ? "结果待核对" : "Needs review",
        _ => chinese ? "未知" : "Unknown"
    };

    private static string State(RealOperationStepState? state, bool chinese) => state switch
    {
        null => chinese ? "状态未返回" : "No status returned",
        RealOperationStepState.Pending => chinese ? "等待执行" : "Pending",
        RealOperationStepState.PreparingCall => chinese ? "正在准备" : "Preparing",
        RealOperationStepState.CallIssued or RealOperationStepState.WaitingForProvider => chinese ? "执行中" : "In progress",
        RealOperationStepState.Verifying => chinese ? "正在核对" : "Verifying",
        RealOperationStepState.Verified => chinese ? "已核实完成" : "Verified",
        RealOperationStepState.Failed => chinese ? "失败" : "Failed",
        RealOperationStepState.OutcomeUnknown => chinese ? "结果待核对" : "Needs review",
        RealOperationStepState.StoppedBeforeCall => chinese ? "尚未执行，已停止" : "Stopped before starting",
        _ => chinese ? "未知" : "Unknown"
    };

    private static string Role(RealPartitionRole role, bool chinese) => role switch
    {
        RealPartitionRole.BasicData => chinese ? "基本数据" : "basic data",
        RealPartitionRole.Efi => chinese ? "EFI 系统" : "EFI system",
        RealPartitionRole.Msr => chinese ? "Microsoft 保留" : "Microsoft Reserved",
        RealPartitionRole.Recovery => chinese ? "恢复" : "recovery",
        _ => role.ToString()
    };

    private static string Kind(StorageObjectKind kind, bool chinese) => kind switch
    {
        StorageObjectKind.PhysicalDisk => chinese ? "物理磁盘" : "physical disk",
        StorageObjectKind.OsDisk => chinese ? "磁盘" : "disk",
        StorageObjectKind.Partition => chinese ? "分区" : "partition",
        StorageObjectKind.Volume => chinese ? "卷" : "volume",
        StorageObjectKind.StoragePool => chinese ? "存储池" : "storage pool",
        StorageObjectKind.StorageTier => chinese ? "存储层" : "storage tier",
        StorageObjectKind.VirtualDisk => chinese ? "虚拟磁盘" : "virtual disk",
        StorageObjectKind.StorageSubsystem => chinese ? "存储子系统" : "storage subsystem",
        _ => kind.ToString()
    };

    private static string Letter(char? letter, bool chinese) => letter is { } value
        ? $"{char.ToUpperInvariant(value)}:"
        : chinese ? "无" : "none";

    private static string? First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
