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
            lines.Add($"  {index + 1}. {step.Id}: {Command(step.Command)}");
            lines.Add($"     {Label("实时目标事实", "Live target facts")}: {Quoted(step.BeforeCondition)}");
            lines.Add($"     {Label("步骤后态", "Step result")}: {Quoted(step.AfterCondition)}");
            lines.Add($"     {Label("数据损失", "Data loss")}: {Quoted(step.DataLoss)}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string Command(RealStorageCommand command) => command switch
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
        CreateVirtualDiskCommand value => $"CreateVirtualDisk pool={Target(value.Pool)} name={Quoted(value.Name)} sizeBytes={Number(value.SizeBytes)} interleaveBytes={Number(value.InterleaveBytes)} dataColumns={Number(value.DataColumns)} Simple/Fixed",
        DeleteVirtualDiskCommand value => $"DeleteVirtualDisk target={Target(value.VirtualDisk)}",
        ResizeVirtualDiskCommand value => $"ResizeVirtualDisk target={Target(value.VirtualDisk)} sizeBytes={Number(value.SizeBytes)}",
        RenameVirtualDiskCommand value => $"RenameVirtualDisk target={Target(value.VirtualDisk)} name={Quoted(value.Name)}",
        CreateTierCommand value => $"CreateTier pool={Target(value.Pool)} name={Quoted(value.Name)} interleaveBytes={Number(value.InterleaveBytes)} dataColumns={Number(value.DataColumns)}",
        CreateTieredVirtualDiskCommand value => $"CreateTieredVirtualDisk pool={Target(value.Pool)} tier={Target(value.Tier)} name={Quoted(value.Name)} sizeBytes={Number(value.SizeBytes)}",
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

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
