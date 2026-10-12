using System.Text.Json;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool_App;

public partial class EditorPageBase
{
    protected async Task<bool> SubmitInitializeDiskAndLayoutAsync(OsDiskInfo disk, bool createMsr)
    {
        if (!ViewModel.CanSubmitRealOperation
            || ViewModel.ActiveDocument.SourceFacts?.Objects.SingleOrDefault(item => item.Id == disk.StableId) is not { HasReliableIdentity: true }
            || !disk.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase)
            || ViewModel.ActiveDocument.Snapshot.Partitions.Any(item => item.OsDiskStableId == disk.StableId))
            return false;
        var system = ViewModel.ActiveDocument.SystemId;
        var session = ViewModel.RealProductSessionId;
        var target = new StorageObjectId(system, StorageObjectKind.OsDisk, disk.StableId);
        var authorization = new RealDiskInitializationAuthorization(target, createMsr);
        operationChainDepth++;
        try
        {
            ReportRealActivity(Text("等待确认初始化", "Waiting for initialization confirmation"));
            var description = RealOperationConfirmationFormatter.FormatTarget(target,
                ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn, ViewModel.ActiveDocument.SourceFacts)
                + Environment.NewLine + Text("初始化为 GPT。", "Initialize as GPT.") + Environment.NewLine
                + (createMsr
                    ? Text("自动核对系统生成的 MSR，并配置为起点 1 MiB、容量 16 MiB 的保留分区。", "Automatically verify the provider MSR and configure a 16 MiB reserved partition at 1 MiB.")
                    : Text("本次不保留 MSR；系统自动生成的 MSR 将在核验后自动删除。", "Do not retain an MSR; any provider-created MSR will be verified and removed automatically."))
                + Environment.NewLine + Text("上述步骤连续完成；不创建数据分区。", "These steps run continuously; no data partition is created.");
            if (!await ConfirmAsync(Text("确认初始化磁盘", "Confirm disk initialization"), description)) return false;
            ValidateContext();
            if (!await SubmitRealAsync(authorization.Initialization, authorization, ValidateContext)
                || LastRealOperationResponse is not { } initialized || !LastRealInventoryRefreshSucceeded) return false;
            ValidateContext();
            var continuation = authorization.ObserveInitialized(initialized, ViewModel.ActiveDocument);
            return continuation is null || await SubmitRealAsync(continuation, authorization, ValidateContext);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException)
        {
            PublishOperationException(Text("初始化未完成", "Initialization incomplete"), "real", exception,
                "real.initialization.incomplete");
            return false;
        }
        finally
        {
            authorization.Stop();
            operationChainDepth--;
            EndRealActivity();
        }

        void ValidateContext()
        {
            if (!ViewModel.IsRealMode || !ViewModel.IsLocalSystem || ViewModel.ActiveDocument.SystemId != system
                || ViewModel.RealProductSessionId != session)
                throw new InvalidOperationException("The confirmed disk initialization context changed.");
        }
    }

    protected IDisposable BeginPageActivity(string phase)
    {
        operationChainDepth++;
        ViewModel.SetRealOperationActivity(true, true, phase, GetType().FullName);
        return new PageActivityScope(this);
    }

    private sealed class PageActivityScope(EditorPageBase owner) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.operationChainDepth--;
            owner.EndRealActivity();
        }
    }
}
