using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinPool.Application;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;
using WinPool.Testing;
using CoreLanguagePreference = WinPool.Domain.LanguagePreference;

namespace WinPool_App;

public sealed partial class TestPage : Page
{
    private static readonly ToolChoice[] Tools =
    [
        new("DiskSpd", new ToolId("microsoft.diskspd")),
        new("fio", new ToolId("fio"))
    ];

    private static readonly ToolChoice DiteFileGenTool =
        new("Dite FileGen", new ToolId("dite.filegen"));

    private static readonly TestScenarioChoice[] Scenarios =
    [
        new("I/O 基准 / I/O benchmark", TestScenarioKind.IoBenchmark),
        new("生成、RoboCopy 复制与验证 / Generate, RoboCopy, verify", TestScenarioKind.CopyVerification),
        new("Dite 混合文件、RoboCopy 与验证 / Dite mixed files, RoboCopy, verify", TestScenarioKind.MixedFileCopyVerification)
    ];

    private static readonly RegisteredTestFileVerificationMode[] CopyVerificationModes =
    [
        RegisteredTestFileVerificationMode.Metadata,
        RegisteredTestFileVerificationMode.SampledContent,
        RegisteredTestFileVerificationMode.FullHash
    ];

    private static readonly PresetChoice[] Presets =
    [
        new("顺序读取 / Sequential read", IoAccessPattern.Sequential, 0, 1024, 1, 8),
        new("顺序写入 / Sequential write", IoAccessPattern.Sequential, 100, 1024, 1, 8),
        new("随机读取 / Random read", IoAccessPattern.Random, 0, 4, 4, 32),
        new("随机混合 70/30 / Random mixed 70/30", IoAccessPattern.Mixed, 30, 4, 4, 32)
    ];

    private static readonly SystemSupportChoice[] SystemSupportActions =
    [
        new(
            "WinPool 临时文件 / WinPool temporary files",
            ElevatedBrokerOperationKind.CleanTemporaryFiles,
            TemporaryFileScope.WinPoolTemporaryFiles),
        new(
            "当前用户临时文件 / Current-user temporary files",
            ElevatedBrokerOperationKind.CleanTemporaryFiles,
            TemporaryFileScope.CurrentUserTemporaryFiles),
        new(
            "Windows 普通临时文件 / Windows ordinary temporary files",
            ElevatedBrokerOperationKind.CleanTemporaryFiles,
            TemporaryFileScope.WindowsOrdinaryTemporaryFiles),
        new(
            "RAMMap 清理系统缓存/standby list / Clear system cache and standby list",
            ElevatedBrokerOperationKind.ClearSystemFileCache,
            UsesRamMap: true),
        new(
            "Flush 测试卷 / Flush test volume",
            ElevatedBrokerOperationKind.FlushVolume),
        new(
            "TRIM/Optimize 测试卷 / TRIM/Optimize test volume",
            ElevatedBrokerOperationKind.TrimOrOptimizeVolume)
    ];

    private WorkspaceViewModel viewModel = null!;
    private long availableBytes;
    private TestDefinition? preparedDefinition;
    private TestPlan? preparedPlan;
    private DispatcherTimer? statusTimer;
    private bool testWasRunning;
    private int comparisonGeneration;
    private bool statusPollInProgress;
    private IReadOnlyList<WindowsPowerPlanDescriptor> availablePowerPlans = [];
    private IReadOnlyList<UserTestPreset> customPresets = [];
    private CancellationTokenSource? eventWatchCancellation;
    private Task? eventWatchTask;

    public TestPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        viewModel = (WorkspaceViewModel)e.Parameter;
        ScenarioOptions.ItemsSource = Scenarios.Select(item => item.DisplayName).ToArray();
        ScenarioOptions.SelectedIndex = 0;
        VerificationOptions.ItemsSource = CopyVerificationModes
            .Select(item => item.ToString())
            .ToArray();
        VerificationOptions.SelectedItem =
            RegisteredTestFileVerificationMode.FullHash.ToString();
        VerificationOptions.IsEnabled = false;
        ToolOptions.ItemsSource = Tools.Select(item => item.DisplayName).ToArray();
        ToolOptions.SelectedIndex = 0;
        PresetOptions.ItemsSource = Presets.Select(item => item.DisplayName).ToArray();
        PresetOptions.SelectedIndex = 0;
        SystemSupportOptions.ItemsSource =
            SystemSupportActions.Select(item => item.DisplayName).ToArray();
        SystemSupportOptions.SelectedIndex = 0;
        SchedulingPriority.ItemsSource = Enum.GetValues<TestProcessPriority>()
            .Select(item => item.ToString())
            .ToArray();
        SchedulingPriority.SelectedItem = TestProcessPriority.AboveNormal.ToString();
        SchedulingProcessors.Text = string.Join(
            ",",
            Enumerable.Range(0, Environment.ProcessorCount));
        HistoryFilter.ItemsSource = Enum.GetValues<TestRunHistoryFilter>()
            .Select(item => item.ToString())
            .ToArray();
        HistoryFilter.SelectedIndex = 0;
        ExportFormat.ItemsSource = Enum.GetValues<TestExportFormat>()
            .Select(item => item.ToString())
            .ToArray();
        ExportFormat.SelectedIndex = 3;
        UpdateText();
        _ = LoadPowerPlansAsync();
        _ = LoadUserTestPresetsAsync();
        _ = LoadHistoryAsync();
        _ = LoadDiteHistoryAsync();
        StartAgentEventWatch();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        statusTimer?.Stop();
        statusTimer = null;
        eventWatchCancellation?.Cancel();
        eventWatchCancellation?.Dispose();
        eventWatchCancellation = null;
        eventWatchTask = null;
        base.OnNavigatedFrom(e);
    }

    private void UpdateText()
    {
        var zh = viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn;
        PageTitle.Text = zh ? "磁盘测试" : "Disk tests";
        TestIntro.Text = zh
            ? "使用外部 DiskSpd 或 fio 生成可审计测试计划。WinPool 只允许在用户明确选择的测试目录中创建和清理已登记文件。"
            : "Build an auditable test plan for external DiskSpd or fio. WinPool may create and clean only registered files under the directory explicitly selected by the user.";
        SafetyNotice.Title = zh ? "测试会写入文件" : "Tests write files";
        SafetyNotice.Message = zh
            ? "这不是存储池、分区或卷结构修改，但可能产生大量 I/O、占用空间并影响系统响应。实际执行前必须再次确认。"
            : "This does not modify pools, partitions, or volume structure, but may generate heavy I/O, consume space, and affect responsiveness. Execution requires another confirmation.";
        ConfigurationTitle.Text = zh ? "测试配置" : "Test configuration";
        CustomPresetName.Header = zh ? "自定义预设名称" : "Custom preset name";
        CustomPresetOptions.Header = zh ? "已保存预设" : "Saved presets";
        NewCustomPresetButton.Content = zh ? "新建" : "New";
        SaveCustomPresetButton.Content = zh ? "保存" : "Save";
        LoadCustomPresetButton.Content = zh ? "加载" : "Load";
        DeleteCustomPresetButton.Content = zh ? "移除" : "Remove";
        if (string.IsNullOrWhiteSpace(CustomPresetStatus.Title))
        {
            ShowPresetStatus(
                InfoBarSeverity.Informational,
                zh ? "自定义预设" : "Custom presets",
                zh
                    ? "只保存测试参数；测试目录和提权系统操作不会进入预设。"
                    : "Only test parameters are saved. The target directory and elevated system actions are never stored in a preset.");
        }
        ScenarioOptions.Header = zh ? "测试场景" : "Test scenario";
        VerificationOptions.Header = zh ? "复制验证" : "Copy verification";
        MixedFileCount.Header = zh ? "混合文件数" : "Mixed file count";
        ToolOptions.Header = zh ? "外部工具" : "External tool";
        PresetOptions.Header = zh ? "工作负载" : "Workload";
        FileSizeGiB.Header = zh ? "测试文件大小 (GiB)" : "Test file size (GiB)";
        BlockSizeKiB.Header = zh ? "块大小 (KiB)" : "Block size (KiB)";
        ThreadCount.Header = zh ? "线程数" : "Threads";
        QueueDepth.Header = zh ? "队列深度" : "Queue depth";
        DurationSeconds.Header = zh ? "持续时间 (秒)" : "Duration (seconds)";
        WritePercentage.Header = zh ? "写入比例 (%)" : "Write percentage (%)";
        CollectLatency.Content = zh ? "采集延迟分布" : "Collect latency distribution";
        WarmupSeconds.Header = zh ? "预热时间 (秒)" : "Warm-up (seconds)";
        CooldownSeconds.Header = zh ? "冷却时间 (秒)" : "Cool-down (seconds)";
        RepeatCount.Header = zh ? "重复次数" : "Repeat count";
        RamMapBatchExpander.Header =
            zh ? "可选 RAMMap 批次前置动作" : "Optional RAMMap pre-batch action";
        EnableRamMapBeforeBatches.Content = zh
            ? "每个外部测试工具批次前清理系统工作集和 standby list"
            : "Clear system working sets and the standby list before each external-tool batch";
        RamMapBatchHint.Text = zh
            ? "RAMMap 不随 WinPool 发布。生成计划时绑定已配置工具的路径、版本、签名和 SHA-256；执行前 Broker 再次验证，只允许固定 -Es/-Et，并保存批次证据。"
            : "RAMMap is not distributed with WinPool. Plan generation binds the configured path, version, signature, and SHA-256; the Broker revalidates them before execution, permits only fixed -Es/-Et, and stores per-batch evidence.";
        SchedulingExpander.Header =
            zh ? "可选进程调度（可恢复）" : "Optional process scheduling (recoverable)";
        EnableSchedulingPolicy.Content = zh
            ? "测试期间调整已登记 TestWorker 及其子进程"
            : "Adjust the registered TestWorker and its child processes during the test";
        SchedulingPriority.Header = zh ? "进程优先级" : "Process priority";
        SchedulingProcessors.Header = zh ? "逻辑处理器索引" : "Logical processor indices";
        SchedulingProcessors.PlaceholderText = zh ? "例如 0,1,2,3" : "For example: 0,1,2,3";
        SchedulingHint.Text = zh
            ? "只绑定 Agent 刚创建的 TestWorker，不接受任意 PID。应用前保存原优先级和 affinity；正常、取消、失败及下次 Agent 启动时恢复。"
            : "Binds only to the TestWorker just created by the Agent; arbitrary PIDs are not accepted. Original priority and affinity are saved before applying and restored after success, cancellation, failure, or on the next Agent start.";
        PowerPlanExpander.Header =
            zh ? "可选临时电源计划（可恢复）" : "Optional temporary power plan (recoverable)";
        EnableTemporaryPowerPlan.Content = zh
            ? "测试期间临时切换已安装的电源计划"
            : "Temporarily switch to an installed power plan during the test";
        TemporaryPowerPlan.Header = zh ? "已安装电源计划" : "Installed power plan";
        PowerPlanHint.Text = zh
            ? "计划列表通过固定只读 powercfg /list 获取。切换和恢复均通过一次性提权 Broker；可能分别显示 UAC。原活动计划会先写入恢复记录。"
            : "The list is read through fixed, read-only powercfg /list. Switching and restoration both use a one-shot elevated Broker and may each show UAC. The original active plan is persisted first.";
        CopyBatchFlushExpander.Header = zh
            ? "可选复制批次 Flush（会提权）"
            : "Optional copy-batch Flush (elevated)";
        EnableFlushBetweenCopyBatches.Content = zh
            ? "每个非末复制批次完成后 Flush 测试卷"
            : "Flush the test volume after each non-final copy batch";
        CopyBatchFlushHint.Text = zh
            ? "默认关闭，仅用于 Dite 混合文件复制。卷 GUID 快照和动作进入计划哈希；每次 Flush 通过一次性 Broker 重新核对当前卷并可能显示 UAC，然后才等待监测稳定。"
            : "Off by default and available only for Dite mixed-file copies. The volume-GUID snapshot and action enter the plan hash; each Flush uses a one-shot Broker to revalidate the current volume, may show UAC, and is followed by monitored settling.";
        TargetTitle.Text = zh ? "测试目录" : "Test directory";
        TargetPath.PlaceholderText =
            zh ? "请选择专用测试目录" : "Choose a dedicated test directory";
        ChooseTargetButton.Content = zh ? "选择目录" : "Choose folder";
        PrepareButton.Content = zh ? "检测并生成计划" : "Detect and build plan";
        OpenToolSettingsButton.Content = zh ? "外部工具设置" : "External tool settings";
        StartButton.Content = zh ? "开始测试" : "Start test";
        CancelButton.Content = zh ? "取消测试" : "Cancel test";
        ToolTipService.SetToolTip(
            StartButton,
            zh
                ? "确认后由 Agent 启动独立 TestWorker；关闭主界面不会停止测试。"
                : "After confirmation, Agent starts an isolated TestWorker; closing the main window does not stop the test.");
        PlanTitle.Text = zh ? "计划预览" : "Plan preview";
        LiveMetricsTitle.Text = zh ? "运行中指标" : "Live run metrics";
        NativeProgressText.Text = zh
            ? "等待工具进度事件"
            : "Waiting for tool progress events";
        LiveMetricsDetails.PlaceholderText = zh
            ? "测试开始后显示步骤状态、已落库指标和最新监控样本。"
            : "Step state, persisted metrics, and latest monitor samples appear after the test starts.";
        SystemSupportTitle.Text = zh ? "测试前系统操作" : "Pre-test system actions";
        SystemSupportDescription.Text = zh
            ? "这些操作不会修改分区、存储池或虚拟磁盘结构。WinPool 会先扫描或绑定目标，再生成一次性审阅令牌；发布版执行前必须明确确认，提权操作还会显示 UAC。"
            : "These actions do not modify partition, pool, or virtual-disk structure. WinPool first scans or binds the target and creates a one-time review token. Release execution requires explicit confirmation, and elevated actions also show UAC.";
        SystemSupportOptions.Header = zh ? "操作" : "Action";
        ReviewSystemSupportButton.Content = zh ? "审阅操作" : "Review action";
        SystemSupportStatus.Title = zh ? "尚未审阅系统操作" : "No system action reviewed";
        SystemSupportStatus.Message = zh
            ? "不会随生成测试计划自动执行。"
            : "No action runs automatically when a test plan is built.";
        PlanStatus.Title = zh ? "尚未生成计划" : "No plan yet";
        HistoryTitle.Text = zh ? "测试历史与比较" : "Test history and comparison";
        RefreshHistoryButton.Content = zh ? "刷新" : "Refresh";
        ExportRunButton.Content = zh ? "导出所选" : "Export selected";
        HistoryHint.Text = zh
            ? "现代运行与已保存 Dite 来源合计最多选择 4 项；统一比较当前 WinPool 指标与 Dite 的中位数/min/max。筛选和比较只读取 Agent 的 SQLite。"
            : "Select up to four modern runs and persisted Dite sources combined. The unified view compares current WinPool metrics with Dite median/min/max values and reads only the Agent's SQLite database.";
        DiteImportTitle.Text = zh
            ? "旧 Dite 结果导入"
            : "Legacy Dite result import";
        DiteImportHint.Text = zh
            ? "只读分析 Dite V23/V24 双语宽表 CSV。不会执行 Dite、跟随日志路径或接触日志引用的测试文件；Agent 会按来源 SHA-256 幂等保存到 SQLite。"
            : "Read-only analysis of Dite V23/V24 bilingual wide CSV files. WinPool does not execute Dite, follow log paths, or touch referenced test files. The Agent persists each source idempotently by SHA-256 in SQLite.";
        ImportDiteButton.Content = zh
            ? "选择 CSV 并分析"
            : "Choose CSV and analyze";
        DiteHistoryTitle.Text = zh
            ? "已保存的 Dite 来源"
            : "Persisted Dite sources";
        RefreshDiteHistoryButton.Content = zh ? "刷新" : "Refresh";
        if (string.IsNullOrWhiteSpace(DiteImportDetails.Text))
        {
            DiteImportStatus.Title = zh ? "尚未导入" : "Nothing imported";
            DiteImportStatus.Message = zh
                ? "文件必须是有效 UTF-8/UTF-8 BOM CSV，大小不超过 64 MiB。"
                : "The file must be valid UTF-8/UTF-8-BOM CSV and no larger than 64 MiB.";
        }
    }

    private void PresetOptions_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (PresetOptions.SelectedIndex < 0
            || PresetOptions.SelectedIndex >= Presets.Length)
        {
            return;
        }

        var preset = Presets[PresetOptions.SelectedIndex];
        BlockSizeKiB.Value = preset.BlockSizeKiB;
        ThreadCount.Value = preset.Threads;
        QueueDepth.Value = preset.QueueDepth;
        WritePercentage.Value = preset.WritePercentage;
        InvalidatePreparedPlan();
    }

    private void CustomPresetOptions_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (CustomPresetOptions.SelectedIndex >= 0
            && CustomPresetOptions.SelectedIndex < customPresets.Count)
        {
            CustomPresetName.Text =
                customPresets[CustomPresetOptions.SelectedIndex].Name;
        }

        UpdatePresetButtons();
    }

    private void NewCustomPresetButton_Click(object sender, RoutedEventArgs e)
    {
        CustomPresetOptions.SelectedIndex = -1;
        CustomPresetName.Text = string.Empty;
        UpdatePresetButtons();
        CustomPresetName.Focus(FocusState.Programmatic);
    }

    private async void SaveCustomPresetButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var zh = viewModel.Localization.EffectiveLanguage
                 == CoreLanguagePreference.ZhCn;
        if (viewModel.AgentConnection is null)
        {
            ShowPresetStatus(
                InfoBarSeverity.Error,
                zh ? "无法保存" : "Could not save",
                zh ? "Agent 未连接。" : "The Agent is not connected.");
            return;
        }

        var name = CustomPresetName.Text.Trim();
        if (name.Length is < 1 or > 80)
        {
            ShowPresetStatus(
                InfoBarSeverity.Error,
                zh ? "预设无效" : "Invalid preset",
                zh
                    ? "名称必须为 1–80 个字符。"
                    : "The name must contain 1–80 characters.");
            return;
        }

        if (!TryReadWorkload(
                out var workload,
                out var repeatCount,
                out var validation))
        {
            ShowPresetStatus(
                InfoBarSeverity.Error,
                zh ? "预设无效" : "Invalid preset",
                validation);
            return;
        }

        var existing = SelectedCustomPreset;
        var now = DateTimeOffset.UtcNow;
        var scenario = SelectedScenario switch
        {
            TestScenarioKind.IoBenchmark => TestPresetScenario.IoBenchmark,
            TestScenarioKind.CopyVerification => TestPresetScenario.CopyVerification,
            TestScenarioKind.MixedFileCopyVerification =>
                TestPresetScenario.MixedFileCopyVerification,
            _ => throw new InvalidOperationException("Unsupported test scenario.")
        };
        var verification = VerificationOptions.SelectedIndex >= 0
                           && VerificationOptions.SelectedIndex
                           < CopyVerificationModes.Length
            ? (TestPresetVerificationMode)VerificationOptions.SelectedIndex
            : TestPresetVerificationMode.FullHash;
        var preset = new UserTestPreset(
            existing?.PresetId ?? Guid.NewGuid(),
            name,
            scenario,
            scenario is TestPresetScenario.IoBenchmark
                && ToolOptions.SelectedIndex >= 0
                && ToolOptions.SelectedIndex < Tools.Length
                    ? Tools[ToolOptions.SelectedIndex].Id
                    : null,
            verification,
            checked((int)MixedFileCount.Value),
            workload.AccessPattern,
            workload.WritePercentage,
            workload.FileSizeBytes,
            workload.BlockSizeBytes,
            workload.ThreadCount,
            workload.QueueDepth,
            checked((int)workload.Duration.TotalSeconds),
            checked((int)workload.Warmup.TotalSeconds),
            checked((int)workload.Cooldown.TotalSeconds),
            repeatCount,
            workload.CollectLatency,
            existing?.CreatedAtUtc ?? now,
            now);
        var result = await viewModel.AgentConnection.SendAsync(
            new SaveUserTestPresetRequest(preset, CorrelationId.New()),
            CancellationToken.None);
        if (!result.IsSuccess
            || result.Value is not UserTestPresetSavedResponse saved)
        {
            ShowPresetStatus(
                InfoBarSeverity.Error,
                zh ? "无法保存" : "Could not save",
                result.Messages.FirstOrDefault()?.DiagnosticText
                ?? (zh ? "Agent 拒绝了预设。" : "The Agent rejected the preset."));
            return;
        }

        await LoadUserTestPresetsAsync(saved.Preset.PresetId);
        ShowPresetStatus(
            InfoBarSeverity.Success,
            zh ? "预设已保存" : "Preset saved",
            zh
                ? "目标目录和提权系统操作未保存。"
                : "The target directory and elevated system actions were not saved.");
    }

    private void LoadCustomPresetButton_Click(object sender, RoutedEventArgs e)
    {
        var preset = SelectedCustomPreset;
        if (preset is null)
        {
            return;
        }

        ApplyUserTestPreset(preset);
        var zh = viewModel.Localization.EffectiveLanguage
                 == CoreLanguagePreference.ZhCn;
        ShowPresetStatus(
            InfoBarSeverity.Success,
            zh ? "预设已加载" : "Preset loaded",
            zh
                ? "请重新审阅目标目录、系统操作并生成不可变计划。"
                : "Review the target directory and system actions, then build a new immutable plan.");
    }

    private async void DeleteCustomPresetButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var preset = SelectedCustomPreset;
        if (preset is null || viewModel.AgentConnection is null)
        {
            return;
        }

        var zh = viewModel.Localization.EffectiveLanguage
                 == CoreLanguagePreference.ZhCn;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = zh ? "移除自定义预设？" : "Remove custom preset?",
            Content = preset.Name,
            PrimaryButtonText = zh ? "移除" : "Remove",
            CloseButtonText = zh ? "取消" : "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        var result = await viewModel.AgentConnection.SendAsync(
            new DeleteUserTestPresetRequest(
                preset.PresetId,
                CorrelationId.New()),
            CancellationToken.None);
        if (!result.IsSuccess
            || result.Value is not UserTestPresetDeletedResponse { Deleted: true })
        {
            ShowPresetStatus(
                InfoBarSeverity.Error,
                zh ? "无法移除" : "Could not remove",
                result.Messages.FirstOrDefault()?.DiagnosticText
                ?? (zh ? "预设不存在或 Agent 拒绝操作。" : "The preset no longer exists or the Agent rejected the operation."));
            return;
        }

        await LoadUserTestPresetsAsync();
        CustomPresetName.Text = string.Empty;
        ShowPresetStatus(
            InfoBarSeverity.Success,
            zh ? "预设已移除" : "Preset removed",
            preset.Name);
    }

    private async Task LoadUserTestPresetsAsync(Guid? selectPresetId = null)
    {
        if (viewModel.AgentConnection is null)
        {
            customPresets = [];
            CustomPresetOptions.ItemsSource = Array.Empty<string>();
            UpdatePresetButtons();
            return;
        }

        var result = await viewModel.AgentConnection.SendAsync(
            new ListUserTestPresetsRequest(CorrelationId.New()),
            CancellationToken.None);
        if (!result.IsSuccess
            || result.Value is not UserTestPresetListResponse response)
        {
            var zh = viewModel.Localization.EffectiveLanguage
                     == CoreLanguagePreference.ZhCn;
            ShowPresetStatus(
                InfoBarSeverity.Error,
                zh ? "无法读取预设" : "Could not load presets",
                result.Messages.FirstOrDefault()?.DiagnosticText ?? string.Empty);
            return;
        }

        customPresets = response.Presets;
        CustomPresetOptions.ItemsSource =
            customPresets.Select(item => item.Name).ToArray();
        CustomPresetOptions.SelectedIndex = selectPresetId is { } id
            ? customPresets.ToList().FindIndex(item => item.PresetId == id)
            : -1;
        UpdatePresetButtons();
    }

    private void ApplyUserTestPreset(UserTestPreset preset)
    {
        ScenarioOptions.SelectedIndex = preset.Scenario switch
        {
            TestPresetScenario.IoBenchmark => 0,
            TestPresetScenario.CopyVerification => 1,
            TestPresetScenario.MixedFileCopyVerification => 2,
            _ => 0
        };
        if (preset.ToolId is { } toolId)
        {
            var toolIndex = Array.FindIndex(
                Tools,
                item => item.Id == toolId);
            if (toolIndex >= 0)
            {
                ToolOptions.SelectedIndex = toolIndex;
            }
        }

        VerificationOptions.SelectedIndex = (int)preset.VerificationMode;
        MixedFileCount.Value = preset.MixedFileCount;
        var builtInIndex = Array.FindIndex(
            Presets,
            item => item.Pattern == preset.AccessPattern);
        PresetOptions.SelectedIndex = builtInIndex >= 0 ? builtInIndex : 0;
        FileSizeGiB.Value = preset.FileSizeBytes / (1024d * 1024d * 1024d);
        BlockSizeKiB.Value = preset.BlockSizeBytes / 1024d;
        ThreadCount.Value = preset.ThreadCount;
        QueueDepth.Value = preset.QueueDepth;
        DurationSeconds.Value = preset.DurationSeconds;
        WritePercentage.Value = preset.WritePercentage;
        CollectLatency.IsChecked = preset.CollectLatency;
        WarmupSeconds.Value = preset.WarmupSeconds;
        CooldownSeconds.Value = preset.CooldownSeconds;
        RepeatCount.Value = preset.RepeatCount;
        InvalidatePreparedPlan();
    }

    private UserTestPreset? SelectedCustomPreset =>
        CustomPresetOptions.SelectedIndex >= 0
        && CustomPresetOptions.SelectedIndex < customPresets.Count
            ? customPresets[CustomPresetOptions.SelectedIndex]
            : null;

    private void UpdatePresetButtons()
    {
        var selected = SelectedCustomPreset is not null;
        LoadCustomPresetButton.IsEnabled = selected;
        DeleteCustomPresetButton.IsEnabled = selected;
    }

    private void ShowPresetStatus(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        CustomPresetStatus.Severity = severity;
        CustomPresetStatus.Title = title;
        CustomPresetStatus.Message = message;
    }

    private void ScenarioOptions_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        VerificationOptions.IsEnabled =
            SelectedScenario is not TestScenarioKind.IoBenchmark;
        MixedFileCount.IsEnabled =
            SelectedScenario is TestScenarioKind.MixedFileCopyVerification;
        EnableFlushBetweenCopyBatches.IsEnabled =
            SelectedScenario is TestScenarioKind.MixedFileCopyVerification;
        ToolOptions.IsEnabled =
            SelectedScenario is not TestScenarioKind.MixedFileCopyVerification;
        InvalidatePreparedPlan();
    }

    private void PlanInput_Changed(object sender, object e) =>
        InvalidatePreparedPlan();

    private async void ChooseTargetButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        try
        {
            InvalidatePreparedPlan();
            var root = Path.GetPathRoot(folder.Path)
                ?? throw new InvalidOperationException("The selected path has no volume root.");
            var drive = new DriveInfo(root);
            availableBytes = drive.AvailableFreeSpace;
            TargetPath.Text = Path.GetFullPath(folder.Path);
            TargetDetails.Text =
                $"{drive.Name} · {FormatBytes(availableBytes)} available";
            PlanStatus.Title =
                viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn
                    ? "目录已选择，请生成计划"
                    : "Folder selected; build the plan";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException)
        {
            TargetPath.Text = string.Empty;
            availableBytes = 0;
            TargetDetails.Text =
                viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn
                    ? "无法读取所选目录的卷和可用空间。"
                    : "The selected folder's volume and free space could not be read.";
        }
    }

    private async void PrepareButton_Click(object sender, RoutedEventArgs e)
    {
        var zh = viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn;
        StartButton.IsEnabled = false;
        PlanDetails.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(TargetPath.Text)
            || availableBytes <= 0
            || ToolOptions.SelectedIndex < 0
            || PresetOptions.SelectedIndex < 0)
        {
            ShowPlanFailure(
                zh
                    ? "请先选择工具、工作负载和专用测试目录。"
                    : "Choose a tool, workload, and dedicated test directory first.");
            return;
        }

        if (!TryReadWorkload(
                out var workload,
                out var repeatCount,
                out var validation))
        {
            ShowPlanFailure(validation);
            return;
        }

        var tool = SelectedScenario
                   is TestScenarioKind.MixedFileCopyVerification
            ? DiteFileGenTool
            : Tools[ToolOptions.SelectedIndex];
        var toolState = await DetectToolAsync(tool.Id);
        if (toolState is null || toolState.Availability != ToolAvailability.Available)
        {
            ShowPlanFailure(
                zh
                    ? $"{tool.DisplayName} 不可用（{toolState?.Availability.ToString() ?? "Agent error"}）。请在设置中安装或配置路径。"
                    : $"{tool.DisplayName} is unavailable ({toolState?.Availability.ToString() ?? "Agent error"}). Install it or configure its path in Settings.");
            return;
        }

        if (SelectedScenario is not TestScenarioKind.IoBenchmark)
        {
            var roboCopy = await DetectToolAsync(new ToolId("windows.robocopy"));
            if (roboCopy is null
                || roboCopy.Availability is not ToolAvailability.Available)
            {
                ShowPlanFailure(
                    zh
                        ? "Windows RoboCopy 不可用，无法生成复制测试计划。"
                        : "Windows RoboCopy is unavailable, so the copy test plan cannot be built.");
                return;
            }
        }

        var definition = BuildDefinition(tool, workload, repeatCount);
        var systemId = SystemId.New();
        var target = new TestTarget(
            systemId,
            new StorageObjectId(
                systemId,
                StorageObjectKind.Partition,
                HashTargetIdentity(Path.GetPathRoot(TargetPath.Text) ?? TargetPath.Text)),
            TargetPath.Text,
            availableBytes,
            IsWriteAllowed: true);
        var supportActions = new List<SystemSupportAction>();
        if (EnableRamMapBeforeBatches.IsChecked == true)
        {
            var ramMapIdentity = await DetectRamMapIdentityAsync();
            if (ramMapIdentity is null)
            {
                ShowPlanFailure(
                    zh
                        ? "RAMMap 不可用或身份验证失败。请先在设置中安装或配置可信路径。"
                        : "RAMMap is unavailable or failed identity validation. Install it or configure a trusted path in Settings.");
                return;
            }

            supportActions.Add(
                new ClearSystemFileCacheAction(
                    RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
                    ramMapIdentity));
        }
        if (EnableSchedulingPolicy.IsChecked == true)
        {
            if (!TryReadSchedulingPolicy(out var schedulingPolicy, out var schedulingError))
            {
                ShowPlanFailure(schedulingError);
                return;
            }

            supportActions.Add(schedulingPolicy);
        }
        if (EnableTemporaryPowerPlan.IsChecked == true)
        {
            if (TemporaryPowerPlan.SelectedIndex < 0
                || TemporaryPowerPlan.SelectedIndex >= availablePowerPlans.Count)
            {
                ShowPlanFailure(
                    zh
                        ? "请选择一个已安装的临时电源计划。"
                        : "Choose an installed temporary power plan.");
                return;
            }

            supportActions.Add(
                new UseTemporaryPowerPlanAction(
                    availablePowerPlans[TemporaryPowerPlan.SelectedIndex]
                        .PowerPlanId));
        }
        if (SelectedScenario is TestScenarioKind.MixedFileCopyVerification
            && EnableFlushBetweenCopyBatches.IsChecked == true)
        {
            var root = Path.GetPathRoot(target.TestRootDirectory)
                ?? throw new InvalidOperationException(
                    "The test target has no volume root.");
            var snapshot = WindowsVolumeIdentityProbe.Resolve(
                target.VolumeId,
                root);
            if (snapshot is null)
            {
                ShowPlanFailure(
                    zh
                        ? "无法把测试目录绑定到稳定卷 GUID，不能启用批次 Flush。"
                        : "The test directory could not be bound to a stable volume GUID, so batch Flush cannot be enabled.");
                return;
            }

            supportActions.Add(
                new FlushVolumeAction(target.VolumeId, snapshot));
        }

        var compiled = new TestPlanCompiler().Compile(
            definition,
            target,
            supportActions,
            CorrelationId.New());
        if (!compiled.IsSuccess || compiled.Value is null)
        {
            ShowPlanFailure(
                compiled.Messages.FirstOrDefault()?.DiagnosticText
                ?? (zh ? "无法生成测试计划。" : "The test plan could not be built."));
            return;
        }

        var plan = compiled.Value;
        preparedDefinition = definition;
        preparedPlan = plan;
        StartButton.IsEnabled = true;
        PlanStatus.Severity = InfoBarSeverity.Success;
        PlanStatus.Title = zh ? "安全计划已生成" : "Safety plan ready";
        PlanStatus.Message = zh
            ? "开始前仍需用户明确确认；Agent 会重新验证计划哈希、工具身份和安全边界。"
            : "Explicit confirmation is still required; Agent revalidates the plan hash, tool identity, and safety boundary.";
        PlanDetails.Text = string.Join(
            Environment.NewLine,
            [
                $"RunId: {plan.RunId.Value:N}",
                $"Scenario: {SelectedScenario}",
                $"Tool: {tool.DisplayName}",
                $"Risk: {plan.Risk}",
                $"Estimated write: {FormatBytes(plan.EstimatedWriteBytes)}",
                $"Available: {FormatBytes(availableBytes)}",
                $"Run directory: {plan.Workspace.RunDirectory}",
                $"Registered files: {plan.Workspace.RegisteredFiles.Count}",
                $"Registered directories: {plan.Workspace.RegisteredDirectories.Count}",
                $"System support actions: {(plan.SupportActions.Count == 0 ? "none" : string.Join(", ", plan.SupportActions.Select(item => item.Kind)))}",
                $"Algorithm: {plan.PlannerAlgorithm.Id} {plan.PlannerAlgorithm.Version} ({plan.PlannerAlgorithm.Confidence})",
                $"PlanHash: {plan.PlanHash}"
            ]);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var zh = viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn;
        if (preparedDefinition is null
            || preparedPlan is null
            || viewModel.AgentConnection is null)
        {
            ShowPlanFailure(
                zh
                    ? "计划或 Agent 连接不可用，请重新生成计划。"
                    : "The plan or Agent connection is unavailable; build the plan again.");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = preparedPlan.SupportActions.Count > 0
                ? zh ? "确认开始测试和系统辅助操作" : "Confirm test and system support action"
                : zh ? "确认开始写入型测试" : "Confirm file-writing test",
            Content = zh
                ? $"目标：{preparedPlan.Target.TestRootDirectory}\n预计写入：{FormatBytes(preparedPlan.EstimatedWriteBytes)}\n系统辅助操作：{FormatSupportActions(preparedPlan)}\n\n测试可能产生大量 I/O、占用空间并影响系统响应。RAMMap 会改变当前内存/缓存状态；调度和电源计划会先保存原状态并恢复；可选批次 Flush 会刷新所选测试卷写缓存并可能短暂降低响应。这些动作可能显示 UAC，但不会修改分区、卷或存储池结构。"
                : $"Target: {preparedPlan.Target.TestRootDirectory}\nEstimated write: {FormatBytes(preparedPlan.EstimatedWriteBytes)}\nSystem support actions: {FormatSupportActions(preparedPlan)}\n\nThe test may generate heavy I/O, consume space, and affect responsiveness. RAMMap changes current memory/cache state; scheduling and power-plan actions save and restore their original state; optional batch Flush drains the selected test volume's write cache and may briefly reduce responsiveness. These actions may show UAC but will not modify partition, volume, or storage-pool structure.",
            PrimaryButtonText = zh ? "确认开始" : "Start",
            CloseButtonText = zh ? "取消" : "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        StartButton.IsEnabled = false;
        var response = await viewModel.AgentConnection.SendAsync(
            new StartAgentTestRequest(
                preparedDefinition,
                preparedPlan,
                UserConfirmedWrite: true,
                CorrelationId.New()),
            CancellationToken.None);
        if (!response.IsSuccess)
        {
            StartButton.IsEnabled = true;
            ShowPlanFailure(
                response.Messages.FirstOrDefault()?.DiagnosticText
                ?? response.Messages.FirstOrDefault()?.Code
                ?? (zh ? "Agent 拒绝启动测试。" : "The Agent rejected the test."));
            return;
        }

        testWasRunning = true;
        LiveMetricsDetails.Text = string.Empty;
        NativeProgressBar.Value = 0;
        NativeProgressText.Text = zh
            ? "已连接 Agent 事件流，等待外部工具报告原生进度。"
            : "Connected to the Agent event stream; waiting for native tool progress.";
        CancelButton.IsEnabled = true;
        PlanStatus.Severity = InfoBarSeverity.Warning;
        PlanStatus.Title = zh ? "测试正在运行" : "Test running";
        PlanStatus.Message = zh
            ? "关闭主界面不会停止测试；可从本页取消，托盘退出会执行完整清理。"
            : "Closing the main window will not stop the test. Cancel here, or use tray Exit for complete cleanup.";
        StartStatusTimer();
    }

    private void StartAgentEventWatch()
    {
        if (viewModel.AgentConnection is null || eventWatchTask is { IsCompleted: false })
        {
            return;
        }

        eventWatchCancellation = new CancellationTokenSource();
        eventWatchTask = WatchAgentEventsAsync(eventWatchCancellation.Token);
    }

    private async Task WatchAgentEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in viewModel.AgentConnection!
                               .WatchAsync(cancellationToken))
            {
                if (item is AgentEventTransportStateEvent transport)
                {
                    DispatcherQueue.TryEnqueue(() =>
                        ApplyEventTransportState(transport));
                    continue;
                }

                if (item is not AgentTestEvent testEvent)
                {
                    continue;
                }

                DispatcherQueue.TryEnqueue(() => ApplyTestEvent(testEvent.TestEvent));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ApplyEventTransportState(AgentEventTransportStateEvent transport)
    {
        var zh = viewModel.Localization.EffectiveLanguage
                 == CoreLanguagePreference.ZhCn;
        NativeProgressText.Text = transport.State switch
        {
            AgentEventTransportState.Disconnected => zh
                ? "Agent 事件连接已断开；断线期间可能存在事件缺口。"
                : "The Agent event connection was lost; events may be missing during the gap.",
            AgentEventTransportState.Reconnecting => zh
                ? "正在重新连接 Agent 事件通道…"
                : "Reconnecting the Agent event channel…",
            AgentEventTransportState.Reconnected => zh
                ? "Agent 事件通道已恢复，并已重新同步当前状态。"
                : "The Agent event channel recovered and current state was reseeded.",
            _ => transport.DiagnosticCode
        };
    }

    private void ApplyTestEvent(TestEvent testEvent)
    {
        if (preparedPlan is null || testEvent.RunId != preparedPlan.RunId)
        {
            return;
        }

        var taskEvent = testEvent.TaskEvent;
        var zh = viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn;
        if (testEvent.Kind == TestEventKind.Progress &&
            taskEvent.ProgressFraction is { } fraction)
        {
            var percent = Math.Clamp(fraction * 100d, 0, 100);
            NativeProgressBar.Value = percent;
            NativeProgressText.Text = zh
                ? $"步骤 {taskEvent.StepId ?? "-"}：工具原生进度 {percent:0.###}%"
                : $"Step {taskEvent.StepId ?? "-"}: native tool progress {percent:0.###}%";
            return;
        }

        if (testEvent.Kind == TestEventKind.StateChanged)
        {
            NativeProgressText.Text = zh
                ? $"步骤 {taskEvent.StepId ?? "运行"}：{taskEvent.State}"
                : $"Step {taskEvent.StepId ?? "run"}: {taskEvent.State}";
            if (taskEvent.StepId is null && taskEvent.State is
                    ApplicationTaskState.Succeeded or
                    ApplicationTaskState.Failed or
                    ApplicationTaskState.Cancelled)
            {
                statusTimer?.Stop();
                statusTimer?.Start();
            }
        }
    }

    private async void ReviewSystemSupportButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var zh = viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn;
        if (viewModel.AgentConnection is null ||
            SystemSupportOptions.SelectedIndex < 0 ||
            SystemSupportOptions.SelectedIndex >= SystemSupportActions.Length)
        {
            ShowSystemSupportFailure(
                zh ? "Agent 或操作选择不可用。" : "The Agent or action selection is unavailable.");
            return;
        }

        ReviewSystemSupportButton.IsEnabled = false;
        try
        {
            SystemSupportStatus.Severity = InfoBarSeverity.Informational;
            SystemSupportStatus.Title = zh ? "正在扫描并绑定目标" : "Scanning and binding target";
            var execution = await BuildSystemSupportRequestAsync(
                SystemSupportActions[SystemSupportOptions.SelectedIndex]);
            var reviewResult = await viewModel.AgentConnection.SendAsync(
                new ReviewAgentSystemSupportRequest(
                    execution,
                    CorrelationId.New()),
                CancellationToken.None);
            if (!reviewResult.IsSuccess ||
                reviewResult.Value is not SystemSupportReviewResponse review)
            {
                ShowSystemSupportFailure(
                    reviewResult.Messages.FirstOrDefault()?.Code
                    ?? (zh ? "Agent 拒绝审阅该操作。" : "The Agent rejected the review."));
                return;
            }

            var details = review.Operation == ElevatedBrokerOperationKind.CleanTemporaryFiles
                ? zh
                    ? $"候选文件：{review.CandidateCount}\n候选大小：{FormatBytes(review.CandidateBytes)}{BatchLimitNotice(review, true)}"
                    : $"Candidate files: {review.CandidateCount}\nCandidate bytes: {FormatBytes(review.CandidateBytes)}{BatchLimitNotice(review, false)}"
                : review.Operation == ElevatedBrokerOperationKind.ClearSystemFileCache
                    ? zh
                        ? "工具：已配置并重新验证身份的 RAMMap\n模式：固定 -Es、-Et 白名单"
                        : "Tool: configured RAMMap with revalidated identity\nMode: fixed -Es and -Et allowlist"
                    : zh
                    ? $"目标：当前所选测试目录所在卷\n操作：{review.Operation}"
                    : $"Target: volume containing the selected test directory\nAction: {review.Operation}";
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = zh ? "确认执行系统操作" : "Confirm system action",
                Content = zh
                    ? $"{details}\n\n此操作会影响系统状态。临时文件删除不可撤销；RAMMap 会清空系统工作集和 standby list；Flush 或 TRIM/Optimize 可能暂时影响响应速度。不会修改或删除分区、存储池、Tier 或 VirtualDisk。"
                    : $"{details}\n\nThis action affects system state. Temporary-file deletion is irreversible; RAMMap clears system working sets and the standby list; Flush or TRIM/Optimize may temporarily affect responsiveness. It will not modify or delete partitions, pools, tiers, or virtual disks.",
                PrimaryButtonText = zh ? "确认执行" : "Execute",
                CloseButtonText = zh ? "取消" : "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                SystemSupportStatus.Title = zh ? "已取消" : "Cancelled";
                SystemSupportStatus.Message =
                    zh ? "一次性审阅令牌将自动过期。" : "The one-time review token will expire automatically.";
                return;
            }

            var executionResult = await viewModel.AgentConnection.SendAsync(
                new ExecuteAgentSystemSupportRequest(
                    review.ReviewId,
                    UserConfirmed: true,
                    CorrelationId.New()),
                CancellationToken.None);
            if (!executionResult.IsSuccess ||
                executionResult.Value is not SystemSupportExecutionResponse completed)
            {
                ShowSystemSupportFailure(
                    executionResult.Messages.FirstOrDefault()?.Code
                    ?? (zh ? "系统操作未完成。" : "The system action did not complete."));
                return;
            }

            SystemSupportStatus.Severity = InfoBarSeverity.Success;
            SystemSupportStatus.Title = zh ? "系统操作完成" : "System action completed";
            SystemSupportStatus.Message = completed.Result.Code;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
        {
            ShowSystemSupportFailure(exception.Message);
        }
        finally
        {
            ReviewSystemSupportButton.IsEnabled = true;
        }
    }

    private async Task<ElevatedBrokerExecutionRequest> BuildSystemSupportRequestAsync(
        SystemSupportChoice choice)
    {
        if (choice.Scope is { } scope)
        {
            var roots = CreateTemporaryCleanupRoots();
            var port = new WindowsTemporaryFileCleanupPort(roots);
            var policy = new TemporaryCleanupPathPolicy(roots);
            var candidates = await Task.Run(
                async () => await port.ScanAsync([scope], CancellationToken.None));
            var approved = candidates
                .Select(policy.Evaluate)
                .Where(decision => decision.IsAllowed)
                .Select(decision => decision.Candidate)
                .Take(
                    ElevatedBrokerExecutionValidator
                        .MaximumTemporaryCleanupCandidates)
                .ToArray();
            if (approved.Length == 0)
            {
                throw new InvalidOperationException(
                    viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn
                        ? "没有符合安全策略的临时文件候选。"
                        : "No temporary-file candidates satisfy the safety policy.");
            }

            return BrokerRequest(
                choice.Operation,
                ComputeSystemSupportPlanHash(
                    choice.Operation,
                    approved.Select(item => item.Id.Value)),
                temporaryCleanupCandidates: approved);
        }

        if (choice.UsesRamMap)
        {
            var identity = await DetectRamMapIdentityAsync();
            if (identity is null)
            {
                throw new InvalidOperationException(
                    viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn
                        ? "RAMMap 不可用。请先在设置中安装或配置自定义路径。"
                        : "RAMMap is unavailable. Install it or configure its custom path in Settings.");
            }

            return BrokerRequest(
                choice.Operation,
                ComputeSystemSupportPlanHash(
                    choice.Operation,
                    [identity.PathBindingHash, identity.Sha256]),
                ramMapMode:
                    RamMapCacheClearMode.EmptySystemWorkingSetAndStandbyList,
                plannedRamMapIdentity: identity);
        }

        if (string.IsNullOrWhiteSpace(TargetPath.Text))
        {
            throw new InvalidOperationException(
                viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn
                    ? "请先选择测试目录，以绑定要维护的卷。"
                    : "Choose the test directory first so the volume can be bound.");
        }

        var root = Path.GetPathRoot(TargetPath.Text)
            ?? throw new InvalidOperationException("The target has no volume root.");
        var volumeId = preparedPlan?.Target.VolumeId
            ?? new StorageObjectId(
                SystemId.New(),
                StorageObjectKind.Partition,
                HashTargetIdentity(root));
        var snapshot = WindowsVolumeIdentityProbe.Resolve(volumeId, root)
            ?? throw new InvalidOperationException(
                "The selected volume could not be resolved to a Windows volume identity.");
        return BrokerRequest(
            choice.Operation,
            ComputeSystemSupportPlanHash(
                choice.Operation,
                [snapshot.StableIdentity, snapshot.DisplayIdentity]),
            volumeTarget: snapshot);
    }

    private static ElevatedBrokerExecutionRequest BrokerRequest(
        ElevatedBrokerOperationKind operation,
        string planHash,
        IReadOnlyList<TemporaryCleanupCandidate>? temporaryCleanupCandidates = null,
        VolumeTargetSnapshot? volumeTarget = null,
        RamMapCacheClearMode? ramMapMode = null,
        RamMapToolIdentity? plannedRamMapIdentity = null) =>
        new(
            Guid.Empty,
            Guid.Empty,
            0,
            string.Empty,
            planHash,
            DateTimeOffset.UtcNow.AddMinutes(2),
            operation,
            TemporaryCleanupCandidates: temporaryCleanupCandidates,
            VolumeTarget: volumeTarget,
            RamMapMode: ramMapMode,
            PlannedRamMapIdentity: plannedRamMapIdentity);

    private static TemporaryCleanupRoots CreateTemporaryCleanupRoots()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new(
            Path.Combine(
                WinPool.Infrastructure.Windows.StorageDataLocations.CurrentRoot,
                "temp"),
            Path.GetTempPath(),
            Path.Combine(windows, "Temp"),
            windows,
            []);
    }

    private static string ComputeSystemSupportPlanHash(
        ElevatedBrokerOperationKind operation,
        IEnumerable<string> identities)
    {
        var material = $"{operation}\n{string.Join("\n", identities)}";
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private static string BatchLimitNotice(
        SystemSupportReviewResponse review,
        bool chinese) =>
        review.WarningCode == "system-support.warning.candidate-batch-limit"
            ? chinese
                ? "\n本次审阅达到 2,000 项 IPC 安全上限；如仍有候选，请完成后重新审阅下一批。"
                : "\nThis review reached the 2,000-item IPC safety limit. Review another batch afterward if candidates remain."
            : string.Empty;

    private void ShowSystemSupportFailure(string message)
    {
        SystemSupportStatus.Severity = InfoBarSeverity.Error;
        SystemSupportStatus.Title =
            viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn
                ? "系统操作不可执行"
                : "System action cannot run";
        SystemSupportStatus.Message = message;
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (preparedPlan is null || viewModel.AgentConnection is null)
        {
            return;
        }

        CancelButton.IsEnabled = false;
        await viewModel.AgentConnection.SendAsync(
            new CancelAgentTestRequest(
                preparedPlan.RunId,
                CorrelationId.New()),
            CancellationToken.None);
    }

    private void StartStatusTimer()
    {
        statusTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        statusTimer.Tick -= StatusTimer_Tick;
        statusTimer.Tick += StatusTimer_Tick;
        statusTimer.Start();
    }

    private async void StatusTimer_Tick(object? sender, object e)
    {
        if (statusPollInProgress
            || viewModel.AgentConnection is null
            || preparedPlan is null)
        {
            return;
        }

        statusPollInProgress = true;
        try
        {
            var response = await viewModel.AgentConnection.SendAsync(
                new GetAgentSnapshotRequest(CorrelationId.New()),
                CancellationToken.None);
            var snapshot = (response.Value as AgentSnapshotResponse)?.Snapshot;
            var active = snapshot?.ActiveTestRunId;
            if (active == preparedPlan.RunId)
            {
                await UpdateLiveMetricsAsync(snapshot!);
                return;
            }

            if (testWasRunning)
            {
                statusTimer?.Stop();
                testWasRunning = false;
                CancelButton.IsEnabled = false;
                StartButton.IsEnabled = true;
                var zh =
                    viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn;
                var resultResponse = await viewModel.AgentConnection.SendAsync(
                    new GetAgentTestResultRequest(
                        preparedPlan.RunId,
                        CorrelationId.New()),
                    CancellationToken.None);
                var result = (resultResponse.Value as TestRunResultResponse)?.Result;
                RenderCompletedResult(result, zh);

                await LoadHistoryAsync();
            }
        }
        finally
        {
            statusPollInProgress = false;
        }
    }

    private async Task UpdateLiveMetricsAsync(AgentSnapshot snapshot)
    {
        if (preparedPlan is null || viewModel.AgentConnection is null)
        {
            return;
        }

        var resultResponse = await viewModel.AgentConnection.SendAsync(
            new GetAgentTestResultRequest(
                preparedPlan.RunId,
                CorrelationId.New()),
            CancellationToken.None);
        var result = (resultResponse.Value as TestRunResultResponse)?.Result;
        var lines = new List<string>
        {
            $"UTC {DateTimeOffset.UtcNow:HH:mm:ss}",
            $"State: {result?.State ?? "starting"}"
        };
        if (result is not null)
        {
            lines.AddRange(result.Steps.Select(step =>
                $"Step {step.StepId}: {step.State}"));
            lines.AddRange(result.Metrics.TakeLast(12).Select(metric =>
                $"{metric.MetricId}: {metric.Value.ToString("0.###", CultureInfo.InvariantCulture)} {metric.Unit} ({metric.Aggregation})"));
        }

        foreach (var sample in (snapshot.LatestMonitorSamples ?? [])
                     .GroupBy(item => item.TargetId)
                     .Select(group => group.MaxBy(item => item.SampledAtUtc)!)
                     .Take(8))
        {
            var values = sample.Values.ToDictionary(item => item.Kind, item => item.Value);
            values.TryGetValue(MonitorMetricKind.ReadBytesPerSecond, out var read);
            values.TryGetValue(MonitorMetricKind.WriteBytesPerSecond, out var write);
            values.TryGetValue(MonitorMetricKind.ActiveTimePercent, out var active);
            values.TryGetValue(MonitorMetricKind.AverageQueueLength, out var queue);
            lines.Add(
                $"{sample.TargetId}: R {FormatBytesPerSecond(read)} · W {FormatBytesPerSecond(write)} · Active {active:0.##}% · Q {queue:0.###}");
        }

        LiveMetricsDetails.Text = string.Join(Environment.NewLine, lines);
    }

    private void RenderCompletedResult(TestRunResultSummary? result, bool zh)
    {
        var succeeded = string.Equals(
            result?.State,
            "Completed",
            StringComparison.Ordinal);
        PlanStatus.Severity = succeeded
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
        PlanStatus.Title = result is null
            ? zh ? "测试进程已结束" : "Test process ended"
            : zh ? $"测试状态：{result.State}" : $"Test state: {result.State}";
        PlanStatus.Message = result is null
            ? zh
                ? "暂时无法读取最终结果。"
                : "The final result could not be read yet."
            : zh
                ? $"已保存 {result.Metrics.Count} 项标准化指标和 {result.Artifacts.Count} 个原始证据附件。"
                : $"{result.Metrics.Count} normalized metrics and {result.Artifacts.Count} raw evidence artifacts were saved.";
        if (result?.Metrics.Count > 0)
        {
            PlanDetails.Text += Environment.NewLine
                + Environment.NewLine
                + (zh ? "标准化指标：" : "Normalized metrics:")
                + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    result.Metrics.Select(metric =>
                        $"{metric.MetricId}: {metric.Value.ToString("0.###", CultureInfo.InvariantCulture)} {metric.Unit} ({metric.Aggregation})"));
        }
        if (result?.Steps.Count > 0)
        {
            PlanDetails.Text += Environment.NewLine
                + Environment.NewLine
                + (zh ? "步骤状态：" : "Step states:")
                + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    result.Steps.Select(step =>
                        $"{step.StepId}: {step.State} ({step.ToolId?.Value ?? "-"})"));
        }
        if (result?.Artifacts.Count > 0)
        {
            PlanDetails.Text += Environment.NewLine
                + Environment.NewLine
                + (zh ? "原始证据附件：" : "Raw evidence artifacts:")
                + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    result.Artifacts.Select(artifact =>
                        $"{artifact.RelativePath} ({artifact.ByteLength} bytes, SHA-256 {artifact.Sha256})"));
        }

        LiveMetricsDetails.Text = result is null
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                result.Steps.Select(step => $"{step.StepId}: {step.State}"));
    }

    private async void RefreshHistoryButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await LoadHistoryAsync();

    private async void ImportDiteButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var zh =
            viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn;
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".csv");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        ImportDiteButton.IsEnabled = false;
        DiteImportStatus.Severity = InfoBarSeverity.Informational;
        DiteImportStatus.Title = zh ? "正在分析" : "Analyzing";
        DiteImportStatus.Message = Path.GetFileName(file.Path);
        DiteImportDetails.Text = string.Empty;
        try
        {
            var result = await Task.Run(
                () => new DiteLegacyResultImporter().ImportAsync(
                    file.Path,
                    CancellationToken.None));
            DiteImportStatus.Severity = InfoBarSeverity.Success;
            DiteImportDetails.Text = FormatDiteImportResult(result, zh);
            if (viewModel.AgentConnection is null)
            {
                DiteImportStatus.Severity = InfoBarSeverity.Warning;
                DiteImportStatus.Title = zh
                    ? "分析完成，Agent 不可用"
                    : "Analyzed; Agent unavailable";
                DiteImportStatus.Message = zh
                    ? $"{result.Runs.Count} 次运行，{result.Summaries.Count} 项指标汇总；当前未写入 SQLite。"
                    : $"{result.Runs.Count} runs and {result.Summaries.Count} metric summaries; not written to SQLite.";
                return;
            }

            var persisted = await viewModel.AgentConnection.SendAsync(
                new PersistDiteLegacyImportRequest(
                    file.Path,
                    result.SourceSha256,
                    CorrelationId.New()),
                CancellationToken.None);
            if (!persisted.IsSuccess
                || persisted.Value is not DiteLegacyImportPersistenceResponse saved)
            {
                DiteImportStatus.Severity = InfoBarSeverity.Error;
                DiteImportStatus.Title = zh
                    ? "分析完成，持久化失败"
                    : "Analyzed; persistence failed";
                DiteImportStatus.Message =
                    persisted.Messages.FirstOrDefault()?.DiagnosticText
                    ?? (zh
                        ? "Agent 未能保存 Dite 导入。"
                        : "The Agent could not save the Dite import.");
                return;
            }

            DiteImportStatus.Title = saved.AlreadyExisted
                ? (zh ? "来源已存在" : "Source already imported")
                : (zh ? "导入并保存完成" : "Imported and persisted");
            DiteImportStatus.Message = zh
                ? $"{saved.RunCount} 次运行、{saved.MetricCount} 个指标值；Import ID：{saved.ImportId:N}"
                : $"{saved.RunCount} runs and {saved.MetricCount} metric values; import ID: {saved.ImportId:N}";
            await LoadDiteHistoryAsync();
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            DiteImportStatus.Severity = InfoBarSeverity.Error;
            DiteImportStatus.Title = zh ? "无法导入 CSV" : "CSV import failed";
            DiteImportStatus.Message = exception.Message;
        }
        finally
        {
            ImportDiteButton.IsEnabled = true;
        }
    }

    private async void HistoryFilter_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (viewModel is not null)
        {
            await LoadHistoryAsync();
        }
    }

    private async void HistoryRuns_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        var selected = HistoryRuns.SelectedItems
            .OfType<HistoryRunView>()
            .Take(4)
            .ToArray();
        ExportRunButton.IsEnabled = selected.Length == 1;
        await RenderUnifiedComparisonAsync();
    }

    private async void DiteHistory_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        await RenderUnifiedComparisonAsync();

    private async Task RenderUnifiedComparisonAsync()
    {
        var generation = ++comparisonGeneration;
        if (viewModel.AgentConnection is null)
        {
            ComparisonDetails.Text = string.Empty;
            return;
        }

        var modern = HistoryRuns.SelectedItems
            .OfType<HistoryRunView>()
            .Take(4)
            .ToArray();
        var legacy = DiteHistory.SelectedItems
            .OfType<DiteHistoryView>()
            .Take(Math.Max(0, 4 - modern.Length))
            .ToArray();
        if (modern.Length == 0 && legacy.Length == 0)
        {
            ComparisonDetails.Text = string.Empty;
            return;
        }

        var results = new List<TestRunResultSummary>();
        foreach (var item in modern)
        {
            var response = await viewModel.AgentConnection.SendAsync(
                new GetAgentTestResultRequest(item.Run.RunId, CorrelationId.New()),
                CancellationToken.None);
            if (response.Value is TestRunResultResponse result)
            {
                results.Add(result.Result);
            }
        }

        var legacyResults = new List<(
            DiteLegacyImportHistoryItem Import,
            IReadOnlyList<DiteLegacyMetricSummary> Summaries)>();
        foreach (var item in legacy)
        {
            var response = await viewModel.AgentConnection.SendAsync(
                new GetDiteLegacyImportSummaryRequest(
                    item.Import.ImportId,
                    CorrelationId.New()),
                CancellationToken.None);
            if (response.Value is DiteLegacyImportSummaryResponse summary)
            {
                legacyResults.Add((item.Import, summary.Summaries));
            }
        }

        if (generation != comparisonGeneration)
        {
            return;
        }

        var lines = new List<string>();
        foreach (var result in results)
        {
            lines.Add($"{ProductInformation.Version} {result.RunId.Value:N} [{result.State}]");
            lines.AddRange(result.Metrics.Select(metric =>
                $"  {metric.MetricId} = {metric.Value.ToString("0.###", CultureInfo.InvariantCulture)} {metric.Unit} ({metric.Aggregation})"
                + FormatMetricSemantic(metric.Semantic)));
        }

        foreach (var legacyResult in legacyResults)
        {
            lines.Add(
                $"Dite {legacyResult.Import.SourceFileName} "
                + $"[{legacyResult.Import.ImportId:N}]");
            lines.AddRange(legacyResult.Summaries.Select(summary =>
                $"  {summary.MetricId} = "
                + $"{summary.Median.ToString("0.###", CultureInfo.InvariantCulture)} "
                + $"{summary.Unit} (median, n={summary.Count}, "
                + $"min={summary.Minimum.ToString("0.###", CultureInfo.InvariantCulture)}, "
                + $"max={summary.Maximum.ToString("0.###", CultureInfo.InvariantCulture)})"));
            lines.AddRange(
                legacyResult.Summaries
                    .Where(summary => summary.Semantic is not null)
                    .Select(summary =>
                        $"    → {summary.Semantic!.CanonicalMetricId} [{summary.Semantic.WorkloadKey}]"
                        + (summary.Semantic.ComparableAcrossTools
                            ? string.Empty
                            : $" ! {summary.Semantic.LimitationCode}")));
        }

        ComparisonDetails.Text = string.Join(Environment.NewLine, lines);
    }

    private static string FormatMetricSemantic(TestMetricSemantic? semantic) =>
        semantic is null
            ? string.Empty
            : $"\n    → {semantic.CanonicalMetricId} [{semantic.WorkloadKey}]"
              + (semantic.ComparableAcrossTools
                  ? string.Empty
                  : $" ! {semantic.LimitationCode}");

    private async void RefreshDiteHistoryButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await LoadDiteHistoryAsync();

    private async void ExportRunButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var selectedItems = HistoryRuns.SelectedItems
            .OfType<HistoryRunView>()
            .Take(2)
            .ToArray();
        if (selectedItems.Length != 1
            || viewModel.AgentConnection is null
            || ExportFormat.SelectedIndex < 0)
        {
            return;
        }

        var selected = selectedItems[0];
        var format = (TestExportFormat)ExportFormat.SelectedIndex;
        var extension = format switch
        {
            TestExportFormat.Csv => ".csv",
            TestExportFormat.Json => ".json",
            TestExportFormat.Markdown => ".md",
            TestExportFormat.EvidencePackage => ".zip",
            _ => throw new ArgumentOutOfRangeException()
        };
        var picker = new FileSavePicker
        {
            SuggestedFileName =
                $"WinPool-{selected.Run.RunId.Value:N}-{format}"
        };
        picker.FileTypeChoices.Add(format.ToString(), [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        ExportRunButton.IsEnabled = false;
        var response = await viewModel.AgentConnection.SendAsync(
            new ExportAgentTestRunRequest(
                selected.Run.RunId,
                format,
                file.Path,
                UserConfirmedOverwrite: true,
                CorrelationId.New()),
            CancellationToken.None);
        ExportRunButton.IsEnabled = true;
        var zh = viewModel.Localization.EffectiveLanguage
                 == CoreLanguagePreference.ZhCn;
        if (response.Value is ExportArtifactResponse exported)
        {
            PlanStatus.Severity = InfoBarSeverity.Success;
            PlanStatus.Title = zh ? "测试结果已导出" : "Test result exported";
            PlanStatus.Message =
                $"{exported.DestinationPath} · SHA-256 {exported.Sha256}";
        }
        else
        {
            ShowPlanFailure(
                response.Messages.FirstOrDefault()?.DiagnosticText
                ?? (zh ? "测试结果导出失败。" : "The test result export failed."));
        }
    }

    private async Task LoadHistoryAsync()
    {
        if (viewModel?.AgentConnection is null)
        {
            return;
        }

        var selected = HistoryFilter.SelectedIndex >= 0
            ? (TestRunHistoryFilter)HistoryFilter.SelectedIndex
            : TestRunHistoryFilter.All;
        var response = await viewModel.AgentConnection.SendAsync(
            new ListAgentTestRunsRequest(
                selected,
                Limit: 100,
                CorrelationId.New()),
            CancellationToken.None);
        if (response.Value is not TestRunHistoryResponse history)
        {
            return;
        }

        HistoryRuns.ItemsSource = history.Runs
            .Select(item => new HistoryRunView(
                item,
                $"{item.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {item.State} · {item.RunId.Value:N}"))
            .ToArray();
        ComparisonDetails.Text = string.Empty;
    }

    private async Task LoadDiteHistoryAsync()
    {
        if (viewModel?.AgentConnection is null)
        {
            return;
        }

        var response = await viewModel.AgentConnection.SendAsync(
            new ListDiteLegacyImportsRequest(
                Limit: 100,
                CorrelationId.New()),
            CancellationToken.None);
        if (response.Value is not DiteLegacyImportHistoryResponse history)
        {
            return;
        }

        DiteHistory.ItemsSource = history.Imports
            .Select(item => new DiteHistoryView(
                item,
                $"{item.ImportedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · "
                + $"{item.SourceFileName} · {item.RunCount} runs · "
                + $"{item.SourceSha256[..12]}…"))
            .ToArray();
    }

    private async Task<ToolState?> DetectToolAsync(ToolId toolId)
    {
        if (viewModel.AgentConnection is null)
        {
            return null;
        }

        var response = await viewModel.AgentConnection.SendAsync(
            new DetectAgentToolRequest(toolId, CorrelationId.New()),
            CancellationToken.None);
        return (response.Value as ToolStateResponse)?.ToolState;
    }

    private async Task<RamMapToolIdentity?> DetectRamMapIdentityAsync()
    {
        var tool = await DetectToolAsync(
            new ToolId("microsoft.sysinternals.rammap"));
        if (tool is not
            {
                Availability: ToolAvailability.Available,
                ExecutablePath: not null
            })
        {
            return null;
        }

        var identity = await new WindowsRamMapExecutableIdentityProbe()
            .ProbeAsync(tool.ExecutablePath, CancellationToken.None);
        return identity is
        {
            SignatureTrusted: true,
            RequiresElevation: true
        } ? identity : null;
    }

    private async Task LoadPowerPlansAsync()
    {
        try
        {
            availablePowerPlans = await new WindowsPowerPlanCatalog(
                    new ProcessWindowsCommandRunner())
                .ListAsync(CancellationToken.None);
            TemporaryPowerPlan.ItemsSource = availablePowerPlans
                .Select(item =>
                    $"{item.DisplayName} · {item.PowerPlanId:D}"
                    + (item.IsActive ? " · active / 当前" : string.Empty))
                .ToArray();
            TemporaryPowerPlan.SelectedIndex = availablePowerPlans.Count == 0
                ? -1
                : Math.Max(
                    0,
                    availablePowerPlans
                        .Select((item, index) => (item, index))
                        .FirstOrDefault(pair => !pair.item.IsActive)
                        .index);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            availablePowerPlans = [];
            TemporaryPowerPlan.ItemsSource = Array.Empty<string>();
            PowerPlanHint.Text = exception.Message;
        }
    }

    private bool TryReadSchedulingPolicy(
        out TestProcessSchedulingPolicyAction policy,
        out string validation)
    {
        var zh = viewModel.Localization.EffectiveLanguage
                 == CoreLanguagePreference.ZhCn;
        if (SchedulingPriority.SelectedItem is not string priorityText
            || !Enum.TryParse<TestProcessPriority>(
                priorityText,
                ignoreCase: false,
                out var priority)
            || !Enum.IsDefined(priority))
        {
            policy = null!;
            validation = zh
                ? "请选择有效的 TestWorker 进程优先级。"
                : "Choose a valid TestWorker process priority.";
            return false;
        }

        var tokens = SchedulingProcessors.Text.Split(
            ',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var processors = new List<int>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!int.TryParse(
                    token,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var processor)
                || processor < 0
                || processor >= Environment.ProcessorCount
                || processors.Contains(processor))
            {
                policy = null!;
                validation = zh
                    ? $"逻辑处理器索引必须是 0 到 {Environment.ProcessorCount - 1} 之间且不重复的逗号分隔整数。"
                    : $"Logical processor indices must be unique comma-separated integers from 0 through {Environment.ProcessorCount - 1}.";
                return false;
            }

            processors.Add(processor);
        }

        if (processors.Count == 0)
        {
            policy = null!;
            validation = zh
                ? "至少选择一个逻辑处理器。"
                : "Select at least one logical processor.";
            return false;
        }

        policy = new(priority, processors);
        validation = string.Empty;
        return true;
    }

    private static string FormatSupportActions(TestPlan plan) =>
        plan.SupportActions.Count == 0
            ? "none / 无"
            : string.Join(
                ", ",
                plan.SupportActions.Select(action =>
                    action switch
                    {
                        TestProcessSchedulingPolicyAction scheduling =>
                            $"scheduling {scheduling.Priority} CPU[{string.Join(",", scheduling.LogicalProcessorIndices)}]",
                        UseTemporaryPowerPlanAction power =>
                            $"temporary power plan {power.PowerPlanId:D}",
                        ClearSystemFileCacheAction =>
                            "RAMMap fixed -Es/-Et before each external-tool batch",
                        FlushVolumeAction flush =>
                            $"Flush volume {flush.PlannedTarget?.DisplayIdentity ?? flush.VolumeId.ProviderKey} between copy batches",
                        _ => action.Kind.ToString()
                    }));

    private TestScenarioKind SelectedScenario =>
        ScenarioOptions.SelectedIndex >= 0
        && ScenarioOptions.SelectedIndex < Scenarios.Length
            ? Scenarios[ScenarioOptions.SelectedIndex].Kind
            : TestScenarioKind.IoBenchmark;

    private TestDefinition BuildDefinition(
        ToolChoice tool,
        TestWorkload workload,
        int repeatCount)
    {
        var definitionParameters = new Dictionary<string, TestParameter>
        {
            ["repeatCount"] = new(
                "repeatCount",
                TestParameterKind.Integer,
                repeatCount.ToString(CultureInfo.InvariantCulture),
                "test.parameter.repeat_count"),
            ["scenario"] = new(
                "scenario",
                TestParameterKind.Choice,
                SelectedScenario.ToString(),
                "test.parameter.scenario")
        };
        if (SelectedScenario is TestScenarioKind.IoBenchmark)
        {
            var taskId = TestTaskId.New();
            var schedule = Enumerable.Range(1, repeatCount)
                .Select(index =>
                {
                    var stepId = $"io-{index:D3}";
                    IReadOnlyList<string> dependencies = index == 1
                        ? []
                        : [$"io-{index - 1:D3}"];
                    return new TestScheduleStep(
                        stepId,
                        taskId,
                        dependencies,
                        IsCancellationBoundary: true);
                })
                .ToArray();
            return new(
                TestDefinitionId.New(),
                $"{tool.DisplayName} {Presets[PresetOptions.SelectedIndex].DisplayName}",
                "1.0.0",
                definitionParameters,
                [
                    new(
                        taskId,
                        "io",
                        TestActionKind.RunIo,
                        tool.Id,
                        workload,
                        new Dictionary<string, TestParameter>())
                ],
                schedule,
                AlgorithmConfidence.Derived);
        }

        var verificationMode = VerificationOptions.SelectedIndex >= 0
                               && VerificationOptions.SelectedIndex
                               < CopyVerificationModes.Length
            ? CopyVerificationModes[VerificationOptions.SelectedIndex]
            : RegisteredTestFileVerificationMode.FullHash;
        definitionParameters["verificationMode"] = new(
            "verificationMode",
            TestParameterKind.Choice,
            verificationMode.ToString(),
            "test.parameter.verification_mode");
        if (SelectedScenario
            is TestScenarioKind.MixedFileCopyVerification)
        {
            return BuildMixedDirectoryDefinition(
                tool,
                workload,
                repeatCount,
                verificationMode,
                definitionParameters);
        }

        var sourceTaskId = TestTaskId.New();
        var tasks = new List<TestTaskDefinition>
        {
            new(
                sourceTaskId,
                "generate-source",
                TestActionKind.GenerateFile,
                tool.Id,
                workload with
                {
                    Warmup = TimeSpan.Zero,
                    Cooldown = TimeSpan.Zero,
                    AccessPattern = IoAccessPattern.Sequential,
                    WritePercentage = 100
                },
                new Dictionary<string, TestParameter>())
        };
        var scheduleSteps = new List<TestScheduleStep>
        {
            new("generate-source", sourceTaskId, [], IsCancellationBoundary: true)
        };
        var previousStep = "generate-source";
        for (var index = 1; index <= repeatCount; index++)
        {
            var copyTaskId = TestTaskId.New();
            var verifyTaskId = TestTaskId.New();
            tasks.Add(
                new(
                    copyTaskId,
                    $"copy-{index:D3}",
                    TestActionKind.Copy,
                    new ToolId("windows.robocopy"),
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["sourceTaskId"] = TaskIdParameter(
                            "sourceTaskId",
                            sourceTaskId),
                        ["copyMode"] = new(
                            "copyMode",
                            TestParameterKind.Choice,
                            "default",
                            "test.parameter.copy_mode"),
                        ["threadCount"] = new(
                            "threadCount",
                            TestParameterKind.Integer,
                            workload.ThreadCount.ToString(CultureInfo.InvariantCulture),
                            "test.parameter.thread_count"),
                        ["retryCount"] = new(
                            "retryCount",
                            TestParameterKind.Integer,
                            "0",
                            "test.parameter.retry_count"),
                        ["retryWaitSeconds"] = new(
                            "retryWaitSeconds",
                            TestParameterKind.Integer,
                            "0",
                            "test.parameter.retry_wait_seconds"),
                        ["copyBatchThresholdMiB"] = new(
                            "copyBatchThresholdMiB",
                            TestParameterKind.Integer,
                            "131072",
                            "test.parameter.copy_batch_threshold_mib"),
                        ["copyBatchMaximumFiles"] = new(
                            "copyBatchMaximumFiles",
                            TestParameterKind.Integer,
                            "10000",
                            "test.parameter.copy_batch_maximum_files")
                    }));
            tasks.Add(
                new(
                    verifyTaskId,
                    $"verify-{index:D3}",
                    TestActionKind.Verify,
                    null,
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["sourceTaskId"] = TaskIdParameter(
                            "sourceTaskId",
                            sourceTaskId),
                        ["copyTaskId"] = TaskIdParameter(
                            "copyTaskId",
                            copyTaskId),
                        ["verificationMode"] = new(
                            "verificationMode",
                            TestParameterKind.Choice,
                            verificationMode.ToString(),
                            "test.parameter.verification_mode"),
                        ["sampleCount"] = new(
                            "sampleCount",
                            TestParameterKind.Integer,
                            "16",
                            "test.parameter.sample_count")
                    }));
            var copyStep = $"copy-{index:D3}";
            var verifyStep = $"verify-{index:D3}";
            scheduleSteps.Add(
                new(copyStep, copyTaskId, [previousStep], IsCancellationBoundary: true));
            scheduleSteps.Add(
                new(verifyStep, verifyTaskId, [copyStep], IsCancellationBoundary: true));
            previousStep = verifyStep;
        }

        return new(
            TestDefinitionId.New(),
            $"{tool.DisplayName} → RoboCopy ({verificationMode})",
            "1.0.0",
            definitionParameters,
            tasks,
            scheduleSteps,
            AlgorithmConfidence.Derived);
    }

    private TestDefinition BuildMixedDirectoryDefinition(
        ToolChoice tool,
        TestWorkload workload,
        int repeatCount,
        RegisteredTestFileVerificationMode verificationMode,
        Dictionary<string, TestParameter> definitionParameters)
    {
        var targetCount = checked((int)MixedFileCount.Value);
        var totalMiB = checked((int)Math.Ceiling(
            workload.FileSizeBytes / (1024d * 1024d)));
        var maximumBytes = DiteFileGenerationBounds.CalculateMaximumBytes(
            totalMiB,
            targetCount);
        definitionParameters["targetCount"] = new(
            "targetCount",
            TestParameterKind.Integer,
            targetCount.ToString(CultureInfo.InvariantCulture),
            "test.parameter.target_count");
        definitionParameters["totalMiB"] = new(
            "totalMiB",
            TestParameterKind.Integer,
            totalMiB.ToString(CultureInfo.InvariantCulture),
            "test.parameter.total_mib");
        definitionParameters["maximumFileCount"] = new(
            "maximumFileCount",
            TestParameterKind.Integer,
            checked(targetCount + DiteFileGenerationBounds.ManifestFileCount)
                .ToString(CultureInfo.InvariantCulture),
            "test.parameter.maximum_file_count");
        var sourceTaskId = TestTaskId.New();
        var tasks = new List<TestTaskDefinition>
        {
            new(
                sourceTaskId,
                "generate-mixed-source",
                TestActionKind.GenerateFile,
                tool.Id,
                workload with
                {
                    FileSizeBytes = maximumBytes,
                    Warmup = TimeSpan.Zero,
                    Cooldown = TimeSpan.Zero,
                    AccessPattern = IoAccessPattern.Sequential,
                    WritePercentage = 100,
                    CollectLatency = false
                },
                new Dictionary<string, TestParameter>
                {
                    ["outputKind"] = new(
                        "outputKind",
                        TestParameterKind.Choice,
                        "directory",
                        "test.parameter.output_kind"),
                    ["profile"] = new(
                        "profile",
                        TestParameterKind.Choice,
                        "mixed",
                        "test.parameter.profile"),
                    ["totalMiB"] = new(
                        "totalMiB",
                        TestParameterKind.Integer,
                        totalMiB.ToString(CultureInfo.InvariantCulture),
                        "test.parameter.total_mib"),
                    ["targetCount"] = new(
                        "targetCount",
                        TestParameterKind.Integer,
                        targetCount.ToString(CultureInfo.InvariantCulture),
                        "test.parameter.target_count"),
                    ["maximumFileCount"] = new(
                        "maximumFileCount",
                        TestParameterKind.Integer,
                        checked(
                            targetCount
                            + DiteFileGenerationBounds.ManifestFileCount)
                            .ToString(CultureInfo.InvariantCulture),
                        "test.parameter.maximum_file_count"),
                    ["poolMiB"] = new(
                        "poolMiB",
                        TestParameterKind.Integer,
                        "64",
                        "test.parameter.pool_mib")
                })
        };
        var schedule = new List<TestScheduleStep>
        {
            new(
                "generate-mixed-source",
                sourceTaskId,
                [],
                IsCancellationBoundary: true)
        };
        var previousStep = "generate-mixed-source";
        for (var index = 1; index <= repeatCount; index++)
        {
            var copyTaskId = TestTaskId.New();
            var verifyTaskId = TestTaskId.New();
            tasks.Add(
                new(
                    copyTaskId,
                    $"copy-mixed-{index:D3}",
                    TestActionKind.Copy,
                    new ToolId("windows.robocopy"),
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["sourceTaskId"] = TaskIdParameter(
                            "sourceTaskId",
                            sourceTaskId),
                        ["copyMode"] = new(
                            "copyMode",
                            TestParameterKind.Choice,
                            "default",
                            "test.parameter.copy_mode"),
                        ["threadCount"] = new(
                            "threadCount",
                            TestParameterKind.Integer,
                            workload.ThreadCount.ToString(CultureInfo.InvariantCulture),
                            "test.parameter.thread_count"),
                        ["retryCount"] = new(
                            "retryCount",
                            TestParameterKind.Integer,
                            "0",
                            "test.parameter.retry_count"),
                        ["retryWaitSeconds"] = new(
                            "retryWaitSeconds",
                            TestParameterKind.Integer,
                            "0",
                            "test.parameter.retry_wait_seconds")
                    }));
            tasks.Add(
                new(
                    verifyTaskId,
                    $"verify-mixed-{index:D3}",
                    TestActionKind.Verify,
                    null,
                    null,
                    new Dictionary<string, TestParameter>
                    {
                        ["sourceTaskId"] = TaskIdParameter(
                            "sourceTaskId",
                            sourceTaskId),
                        ["copyTaskId"] = TaskIdParameter(
                            "copyTaskId",
                            copyTaskId),
                        ["verificationMode"] = new(
                            "verificationMode",
                            TestParameterKind.Choice,
                            verificationMode.ToString(),
                            "test.parameter.verification_mode"),
                        ["sampleCount"] = new(
                            "sampleCount",
                            TestParameterKind.Integer,
                            "32",
                            "test.parameter.sample_count")
                    }));
            var copyStep = $"copy-mixed-{index:D3}";
            var verifyStep = $"verify-mixed-{index:D3}";
            schedule.Add(
                new(
                    copyStep,
                    copyTaskId,
                    [previousStep],
                    IsCancellationBoundary: true));
            schedule.Add(
                new(
                    verifyStep,
                    verifyTaskId,
                    [copyStep],
                    IsCancellationBoundary: true));
            previousStep = verifyStep;
        }

        return new(
            TestDefinitionId.New(),
            $"Dite mixed {targetCount} → RoboCopy ({verificationMode})",
            "1.0.0",
            definitionParameters,
            tasks,
            schedule,
            AlgorithmConfidence.Derived);
    }

    private static TestParameter TaskIdParameter(
        string key,
        TestTaskId taskId) =>
        new(
            key,
            TestParameterKind.Text,
            taskId.Value.ToString("D"),
            $"test.parameter.{key}");

    private bool TryReadWorkload(
        out TestWorkload workload,
        out int repeatCount,
        out string validation)
    {
        var zh = viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn;
        var numericValues = new[]
        {
            FileSizeGiB.Value,
            BlockSizeKiB.Value,
            ThreadCount.Value,
            QueueDepth.Value,
            DurationSeconds.Value,
            WritePercentage.Value,
            WarmupSeconds.Value,
            CooldownSeconds.Value,
            RepeatCount.Value,
            MixedFileCount.Value
        };
        if (numericValues.Any(value => !double.IsFinite(value))
            || ThreadCount.Value != Math.Truncate(ThreadCount.Value)
            || QueueDepth.Value != Math.Truncate(QueueDepth.Value)
            || WritePercentage.Value != Math.Truncate(WritePercentage.Value)
            || RepeatCount.Value != Math.Truncate(RepeatCount.Value)
            || SelectedScenario is TestScenarioKind.MixedFileCopyVerification
            && MixedFileCount.Value != Math.Truncate(MixedFileCount.Value))
        {
            workload = null!;
            repeatCount = 0;
            validation = zh
                ? "测试参数必须是有效数字，线程数、队列深度、写入比例和重复次数必须是整数。"
                : "Test parameters must be finite numbers; threads, queue depth, write percentage, and repeat count must be integers.";
            return false;
        }

        try
        {
            var fileBytes = checked((long)Math.Round(
                FileSizeGiB.Value * 1024d * 1024d * 1024d,
                MidpointRounding.AwayFromZero));
            var blockBytes = checked((int)Math.Round(
                BlockSizeKiB.Value * 1024d,
                MidpointRounding.AwayFromZero));
            var threads = checked((int)ThreadCount.Value);
            var queueDepth = checked((int)QueueDepth.Value);
            var duration = TimeSpan.FromSeconds(DurationSeconds.Value);
            var warmup = TimeSpan.FromSeconds(WarmupSeconds.Value);
            var cooldown = TimeSpan.FromSeconds(CooldownSeconds.Value);
            var write = checked((int)WritePercentage.Value);
            repeatCount = checked((int)RepeatCount.Value);
            workload = new(
                fileBytes,
                blockBytes,
                threads,
                queueDepth,
                warmup,
                duration,
                cooldown,
                Presets[PresetOptions.SelectedIndex].Pattern,
                write,
                SoftwareCacheMode.Enabled,
                WriteThroughMode.Disabled,
                CollectLatency.IsChecked == true);
            validation = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is OverflowException
                or ArgumentOutOfRangeException)
        {
            workload = null!;
            repeatCount = 0;
            validation = zh
                ? "测试参数超出允许范围。"
                : "One or more test parameters are outside the allowed range.";
            return false;
        }
    }

    private void ShowPlanFailure(string message)
    {
        PlanStatus.Severity = InfoBarSeverity.Error;
        PlanStatus.Title =
            viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn
                ? "无法生成计划"
                : "Plan could not be built";
        PlanStatus.Message = message;
    }

    private void InvalidatePreparedPlan()
    {
        if (preparedPlan is null)
        {
            return;
        }

        preparedPlan = null;
        preparedDefinition = null;
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        PlanStatus.Severity = InfoBarSeverity.Informational;
        PlanStatus.Title =
            viewModel.Localization.EffectiveLanguage == CoreLanguagePreference.ZhCn
                ? "配置已改变，请重新生成计划"
                : "Configuration changed; rebuild the plan";
    }

    private void OpenToolSettingsButton_Click(object sender, RoutedEventArgs e) =>
        ((MainWindow)App.Window).ShowSettings();

    private static string HashTargetIdentity(string value) =>
        Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(
                        value.Trim().ToUpperInvariant())))
            .ToLowerInvariant();

    private static string FormatBytes(long value)
    {
        var size = Math.Max(0, value);
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var index = 0;
        var display = (double)size;
        while (display >= 1024 && index < units.Length - 1)
        {
            display /= 1024;
            index++;
        }

        return $"{display.ToString(index == 0 ? "0" : "0.##", CultureInfo.InvariantCulture)} {units[index]}";
    }

    private static string FormatBytesPerSecond(double value) =>
        $"{FormatBytes((long)Math.Max(0, Math.Round(value)))} /s";

    private static string FormatDiteImportResult(
        DiteLegacyImportResult result,
        bool zh)
    {
        var lines = new List<string>
        {
            $"{(zh ? "来源" : "Source")}: {result.SourceFileName}",
            $"SHA-256: {result.SourceSha256}",
            $"{(zh ? "运行数" : "Runs")}: {result.Runs.Count}",
            $"{(zh ? "指标汇总" : "Metric summaries")}: {result.Summaries.Count}",
            string.Empty
        };
        if (result.Summaries.Count == 0)
        {
            lines.Add(zh
                ? "CSV 中没有可解析的数值指标。"
                : "No numeric metrics could be parsed from the CSV.");
            return string.Join(Environment.NewLine, lines);
        }

        lines.Add(zh
            ? "指标 | 数量 | 最小值 | 中位数 | 最大值"
            : "Metric | Count | Minimum | Median | Maximum");
        foreach (var summary in result.Summaries)
        {
            var unit = string.IsNullOrWhiteSpace(summary.Unit)
                ? string.Empty
                : $" {summary.Unit}";
            lines.Add(
                $"{summary.MetricId} | {summary.Count} | "
                + $"{FormatDiteMetric(summary.Minimum)}{unit} | "
                + $"{FormatDiteMetric(summary.Median)}{unit} | "
                + $"{FormatDiteMetric(summary.Maximum)}{unit}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatDiteMetric(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record ToolChoice(string DisplayName, ToolId Id);

    private enum TestScenarioKind
    {
        IoBenchmark,
        CopyVerification,
        MixedFileCopyVerification
    }

    private sealed record TestScenarioChoice(
        string DisplayName,
        TestScenarioKind Kind);

    private sealed record SystemSupportChoice(
        string DisplayName,
        ElevatedBrokerOperationKind Operation,
        TemporaryFileScope? Scope = null,
        bool UsesRamMap = false);

    private sealed record HistoryRunView(
        TestRunHistoryItem Run,
        string DisplayText);

    private sealed record DiteHistoryView(
        DiteLegacyImportHistoryItem Import,
        string DisplayText);

    private sealed record PresetChoice(
        string DisplayName,
        IoAccessPattern Pattern,
        int WritePercentage,
        int BlockSizeKiB,
        int Threads,
        int QueueDepth);
}
