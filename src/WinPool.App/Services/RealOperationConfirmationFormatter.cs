using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.App.Services;

/// <summary>Formats only the Agent-frozen plan used by the final confirmation.</summary>
public static class RealOperationConfirmationFormatter
{
    private static readonly JsonSerializerOptions DisplayJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Format(OperationPlan plan, bool chinese)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var real = plan.RealOperation ?? throw new ArgumentException(
            "The Agent response does not contain a real operation plan.", nameof(plan));
        string Label(string zh, string en) => chinese ? zh : en;
        var lines = new List<string>
        {
            $"OperationId: {plan.OperationId.Value}",
            $"Plan hash: {plan.PlanHash}",
            $"{Label("机器绑定", "Machine binding")}: {Quoted(real.MachineBinding)}",
            $"{Label("物理成员指纹", "Physical member fingerprint")}: {Quoted(real.PhysicalMemberFingerprint)}",
            $"{Label("准确目标", "Exact targets")}:"
        };
        lines.AddRange(plan.Targets.Select((target, index) =>
            $"  {index + 1}. {Target(target)}"));
        lines.Add($"{Label("预计末态", "Expected final state")}: {Quoted(real.ExpectedFinalState)}");
        lines.Add($"{Label("不可逆影响", "Irreversible effects")}: {Quoted(plan.IrreversibleEffects)}");
        lines.Add($"{Label("过期时间", "Expires")}: {real.ExpiresAt.LocalDateTime:G}");
        lines.Add($"{Label("有序步骤", "Ordered steps")}:");
        foreach (var (step, index) in real.Steps.Select((value, index) => (value, index)))
        {
            lines.Add($"  {index + 1}. {step.Id}: {Command(step.Command, chinese)}");
            lines.Add($"     {Label("实时目标事实", "Live target facts")}: {Quoted(step.BeforeCondition)}");
            lines.Add($"     {Label("步骤后态", "Step result")}: {Quoted(step.AfterCondition)}");
            lines.Add($"     {Label("数据损失", "Data loss")}: {Quoted(step.DataLoss)}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string Command(RealStorageCommand command, bool chinese) => command switch
    {
        SetDiskOnlineCommand value => $"SetDiskOnline target={Target(value.Disk)} online={value.Online}",
        InitializeGptCommand value => $"InitializeGpt target={Target(value.Disk)}",
        ClearDiskCommand value => $"ClearDisk target={Target(value.Disk)} removeOem={value.RemoveOem}",
        CreatePartitionCommand value => $"CreatePartition target={Target(value.Disk)} role={value.Role} offsetBytes={Number(value.OffsetBytes)} sizeBytes={Number(value.SizeBytes)}",
        DeletePartitionCommand value => $"DeletePartition target={Target(value.Partition)}",
        ResizePartitionCommand value => $"ResizePartition target={Target(value.Partition)} sizeBytes={Number(value.SizeBytes)}",
        FormatVolumeCommand value => $"FormatVolume target={Target(value.Partition)} fileSystem={value.FileSystem} clusterBytes={Number(value.ClusterBytes)} full={value.Full} label={Quoted(value.Label)}",
        SetDriveLetterCommand value => $"SetDriveLetter target={Target(value.Partition)} previous={value.PreviousLetter?.ToString() ?? "none"} next={value.NewLetter?.ToString() ?? "none"}",
        RenameVolumeCommand value => $"RenameVolume target={Target(value.Volume)} label={Quoted(value.Label)}",
        CreatePoolCommand value => $"CreatePool member={Target(value.PhysicalDisk)} name={Quoted(value.Name)}",
        DeletePoolCommand value => $"DeletePool target={Target(value.Pool)}",
        RenamePoolCommand value => $"RenamePool target={Target(value.Pool)} name={Quoted(value.Name)}",
        CreateVirtualDiskCommand value => $"CreateVirtualDisk pool={Target(value.Pool)} name={Quoted(value.Name)} {CreationSize(value.SizeBytes, value.UseMaximumSize, chinese, value.MaximumCapacity)} interleaveBytes={Number(value.InterleaveBytes)} dataColumns={Number(value.DataColumns)} Simple/Fixed",
        DeleteVirtualDiskCommand value => $"DeleteVirtualDisk target={Target(value.VirtualDisk)}",
        ResizeVirtualDiskCommand value => $"ResizeVirtualDisk target={Target(value.VirtualDisk)} sizeBytes={Number(value.SizeBytes)}",
        RenameVirtualDiskCommand value => $"RenameVirtualDisk target={Target(value.VirtualDisk)} name={Quoted(value.Name)}",
        CreateTierCommand value => $"CreateTier pool={Target(value.Pool)} name={Quoted(value.Name)} interleaveBytes={Number(value.InterleaveBytes)} dataColumns={Number(value.DataColumns)}",
        CreateTieredVirtualDiskCommand value => $"CreateTieredVirtualDisk pool={Target(value.Pool)} name={Quoted(value.Name)} {CreationSize(value.SizeBytes, value.UseMaximumSize, chinese, value.MaximumCapacity)} {TieredCreationMechanism(value, chinese)}{TierSearchInputs(value, chinese)}",
        DeleteTierCommand value => $"DeleteTier target={Target(value.Tier)}",
        ResizeTierCommand value => $"ResizeTier target={Target(value.Tier)} sizeBytes={Number(value.SizeBytes)}",
        RenameTierCommand value => $"RenameTier target={Target(value.Tier)} name={Quoted(value.Name)}",
        _ => throw new ArgumentException("The Agent plan contains an unsupported real command.", nameof(command))
    };

    private static string Target(RealTargetReference reference) =>
        reference.Existing is { } existing
            ? Target(existing)
            : $"{reference.Kind} createdByStep={Quoted(reference.CreatedByStep)}";

    private static string Target(StorageObjectId target) =>
        $"{target.Kind} id={Quoted(target.ProviderKey)} system={target.System.Value}";

    private static string Quoted(string? value) =>
        value is null ? "null" : JsonSerializer.Serialize(value, DisplayJson);

    private static string CreationSize(long bytes, bool useMaximumSize, bool chinese, MaximumCapacityPolicy? policy = null) => policy is { }
        ? (chinese
            ? $"WinPool MAX 算法={Quoted(policy.AlgorithmVersion)} A={Number(policy.UpperBoundBytes)} bytes；减去 4,000,000 bytes 后取严格更小整数 GiB；C={Number(policy.InitialCandidateBytes / MaximumCapacityAlgorithm.GiB)} GiB；首次失败每次减 1 GiB 至成功，首次成功每次加 1 GiB 至失败；仅确认无变化的容量拒绝计为边界，未知立即停止；最多 {Number(policy.MaximumAttempts)} 次；来源={Quoted(policy.Source)} 来源指纹={Quoted(policy.SourceFingerprint)} 时间={policy.CapturedAtUtc:O}"
            : $"WinPool MAX algorithm={Quoted(policy.AlgorithmVersion)} A={Number(policy.UpperBoundBytes)} bytes; subtract 4,000,000 bytes then take the strictly smaller whole GiB; C={Number(policy.InitialCandidateBytes / MaximumCapacityAlgorithm.GiB)} GiB; first failure: descend 1 GiB until success; first success: ascend 1 GiB until rejection; only unchanged capacity rejection proves a boundary, unknown stops; at most {Number(policy.MaximumAttempts)} attempts; source={Quoted(policy.Source)} fingerprint={Quoted(policy.SourceFingerprint)} captured={policy.CapturedAtUtc:O}")
        : useMaximumSize
        ? $"{(chinese ? "容量=最大容量（由 Windows 决定实际容量）" : "capacity=MAX (actual capacity determined by Windows)")} UseMaximumSize=true"
        : $"sizeBytes={Number(bytes)}";

    private static string TierSearchInputs(CreateTieredVirtualDiskCommand command, bool chinese) =>
        command.CapacityTiers is not { Count: > 1 } tiers ? string.Empty :
            (chinese ? "; 同一 VD 先按各层 0.5×C 创建，再按以下顺序逐层搜索：" : "; seed one VD with 0.5×C per tier, then search in this order: ") +
            string.Join("; ", tiers.Select((tier, index) => $"{index + 1}. {Target(tier.Tier)} " +
                CreationSize(0, true, chinese, tier.MaximumCapacity)));

    private static string TieredCreationMechanism(CreateTieredVirtualDiskCommand command, bool chinese) =>
        command.CreationMechanism switch
        {
            TieredVirtualDiskCreationMechanism.ExactTemplate =>
                $"creationMechanism=ExactTemplate tier={Target(command.Tier)}",
            TieredVirtualDiskCreationMechanism.WindowsAutomaticHdd => chinese
                ? $"原计划请求方式=WindowsAutomaticHdd（MediaType=HDD + UseMaximumSize）；目标要求实际 HDD 层，不能把普通 VD 视为成功；选中模板仅为布局约束={Target(command.Tier)}（不传入创建命令）；该候选未通过实机核验，暂不接受新执行"
                : $"requested creationMechanism=WindowsAutomaticHdd (MediaType=HDD + UseMaximumSize); the goal requires an actual HDD tier, not an ordinary VD; selected template is a layout constraint only={Target(command.Tier)} (not passed to the creation command); this candidate failed native verification and new execution is blocked",
            _ => throw new ArgumentException("The Agent plan contains an unsupported tiered virtual-disk creation mechanism.", nameof(command))
        };

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
