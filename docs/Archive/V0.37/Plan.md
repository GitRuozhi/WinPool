# WinPool V0.37 UI 事件与输入收口执行计划

## 0. 状态、目标与基线

- **计划状态**：implemented / automatic gates passed / native-manual unverified
- **创建日期**：2026-08-13
- **目标版本**：V0.37
- **基线版本**：V0.36
- **目标分支**：`main`
- **用户授权**：尚未批准执行。用户明确要求先阅读本计划，等待批准后再实施。
- **计划性质**：只修复当前代码中已确认的 UI 生命周期、异步异常处理、输入校验和低风险用户体验问题。不新增功能，不改变架构，不改变安全边界。
- **核心目标**：让 WinPool 在 V0.3 阶段更稳定地处理用户操作，避免重入循环、未观察异常、按钮卡死和无效输入造成的误导。

本计划不授权真实磁盘、分区、卷、Storage Pool、Storage Tier、Virtual Disk 的创建、删除、格式化、初始化、扩容、修复或移除。所有改动仍限定在 Simulation、只读采集、已注册目录测试、监控和既有 R3 支持动作范围内。

## 1. 不变边界

V0.37 明确不改变：

- IPC 协议版本，保持 `3`。
- Agent 持有的 SQLite schema，保持 `12`。
- App/Agent/TestWorker/Broker 四进程架构。
- Simulation-first 和 deny-by-default 执行策略。
- 外部工具只能通过 typed adapter 接入，不内置或重实现 DiskSpd、fio、Dite、RoboCopy、RAMMap。
- 执行模式不持久化，Real mode 或 UAC elevation 不等于真实存储修改授权。
- 产品版本规则 `Va.bc`；本次只推进到 `V0.37`，不创建 `V0.310` 或跳过迭代。

## 2. 缺陷清单与修复方式

### 2.1 P1：设置页语言切换重入

**位置**

`src/WinPool.App/SettingsPage.xaml.cs` 的 `LanguageOptions_SelectionChanged`。

**问题**

用户切换语言后，处理器会重新设置 `ItemsSource` 和 `SelectedIndex`。此时 `_ready` 已经为 `true`，且没有语言同步保护，重新设置 `SelectedIndex` 会再次触发同一个 SelectionChanged 事件，反复执行本地化、重建 ComboBox 和偏好保存。

**修复方式**

增加 `_updatingLanguage` 保护，并在本地化刷新期间禁止重入。优先采用更小改动：

```csharp
private bool _updatingLanguage;

private async void LanguageOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (!_ready || _updatingLanguage || LanguageOptions.SelectedIndex < 0)
    {
        return;
    }

    _updatingLanguage = true;
    try
    {
        var language = (LanguagePreference)LanguageOptions.SelectedIndex;
        await ViewModel.SetLanguageAsync(language);
        PopulateComboBoxes();
        ThemeOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Theme;
        AccentOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.AccentColor;
        LanguageOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Language;
        UpdateText();
        BuildExternalToolRows();
        _ = RefreshExternalToolsAsync();
        ((MainWindow)App.Window).RefreshChrome();
    }
    catch (Exception exception) when (
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
    {
        LanguageOptions.SelectedIndex = (int)ViewModel.CurrentPreferences.Language;
        ViewModel.NotificationService.PublishError(
            ViewModel.Localization["Error"],
            exception.Message,
            "settings",
            "settings-language-save-failed");
    }
    finally
    {
        _updatingLanguage = false;
    }
}
```

如果后续愿意做更干净的修复，可以改为不整体替换 `ItemsSource`，而是在本地化后更新现有 ComboBoxItem 的显示文本，从源头避免 SelectionChanged 重入。

**验收**

- 手动切换中文、英文和“跟随系统”各一次，不出现持续重建、界面闪烁或卡顿。
- 语言保存失败时 ComboBox 回到当前已持久化语言，并显示错误通知。

### 2.2 P1：未观察 Task 异常没有标记为已观察

**位置**

`src/WinPool.App/App.xaml.cs` 的 `TaskScheduler_UnobservedTaskException`。

**问题**

处理器只写日志，没有调用 `e.SetObserved()`。代码中大量使用 `_ = ...` 启动后台任务，如果这些任务异常，异常仍属于未观察状态，后续可能触发进程终止。

**修复方式**

保留完整异常证据，然后显式标记为已观察，避免把后台清理、事件流和刷新任务变成进程级崩溃源。

```csharp
private static void TaskScheduler_UnobservedTaskException(
    object? sender,
    UnobservedTaskExceptionEventArgs e)
{
    WriteCrashLog("UnobservedTask", e.Exception);
    e.SetObserved();
}
```

后续还要逐项审计 `_ = ...` 调用点，优先让每个后台任务都有明确状态、取消和异常出口。本轮至少修复测试页、设置页和开发页中已确认的高风险调用。

**验收**

- 触发一个受控未观察异常后，进程不会因为未观察异常终止。
- 异常仍写入 `last-crash.txt`，不会静默丢失证据。

### 2.3 P1：关键 async void 事件缺少异常兜底

**位置**

- `src/WinPool.App/TestPage.xaml.cs` 的 `PrepareButton_Click`、`StartButton_Click`、`CancelButton_Click`、`StatusTimer_Tick`
- `src/WinPool.App/EditPage.xaml.cs` 的多个编辑按钮
- `src/WinPool.App/SettingsPage.xaml.cs` 的主题、强调色、语言等偏好保存按钮

**问题**

这些 `async void` 事件处理器如果抛出异常，会进入 UI 调度器未处理异常路径。部分处理器还会先禁用按钮，但没有 `finally` 恢复状态，导致按钮永久卡住。

**修复方式**

为高风险按钮统一补齐 `try/catch/finally`：

- `StartButton`、`CancelButton`、`PrepareButton` 必须在 `finally` 中恢复可操作状态。
- `StatusTimer_Tick` 必须捕获 Agent 请求异常，记录诊断后保持下一轮轮询可继续。
- 设置页偏好保存失败时，控件应回滚到当前持久化值，并发布错误通知。
- 编辑页 `ApplyAsync` 的调用方捕获预期异常，并显示“模拟操作未完成”的统一提示。

推荐增加一个轻量私有帮助方法，避免每个事件重复相同逻辑：

```csharp
private async Task RunUiActionAsync(
    Func<Task> action,
    Action? restore = null,
    string errorKey = "OperationFailed")
{
    try
    {
        await action();
    }
    catch (Exception exception) when (
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or InvalidDataException)
    {
        ViewModel.NotificationService.PublishError(
            ViewModel.Localization["Error"],
            exception.Message,
            "ui",
            errorKey);
    }
    finally
    {
        restore?.Invoke();
    }
}
```

该帮助方法只处理可恢复异常，不吞掉 `OutOfMemoryException`、`StackOverflowException` 等不可恢复异常。

**验收**

- 测试页 Agent 断开时点击 Start/Cancel/Prepare，不会崩溃，按钮会恢复。
- 设置页偏好保存失败时，UI 会回到真实状态并显示错误。
- 编辑页模拟操作失败时，用户能看到明确提示。

### 2.4 P2：新建分区非法输入被当作“全部剩余空间”

**位置**

`src/WinPool.App/EditPage.xaml.cs` 的 `NewPartitionAsync`。

**问题**

用户输入了非空但无法解析的内容时，`bytes` 仍为 `null`，随后创建分区会使用全部剩余空间。用户没有意识到输入无效，也没有机会取消。

**修复方式**

把“留空表示使用全部剩余空间”作为唯一允许的空输入路径。任何非空输入必须通过严格数值解析和范围检查：

```csharp
long? bytes = null;
if (!string.IsNullOrWhiteSpace(size))
{
    if (!TryParseGigabytes(size, out var gb) || gb <= 0)
    {
        await ShowMessageAsync(
            Text("输入无效", "Invalid input"),
            Text("请输入大于 0 的 GB 数值，或留空使用全部剩余空间。",
                 "Enter a size in GB greater than zero, or leave the field blank to use all free space."));
        return;
    }

    bytes = checked((long)(gb * 1024L * 1024L * 1024L));
}
```

同时新增一个 `TryParseGigabytes` 帮助方法，统一处理浮点溢出、非数字、负数和超大值。

**验收**

- 输入 `abc`、`-1`、超大数字时不会创建分区。
- 留空仍表示使用全部剩余空间。
- 正常输入 `10` 会创建约 10 GiB 的分区。

### 2.5 P2：创建池缺少总预览和确认

**位置**

`src/WinPool.App/EditPage.xaml.cs` 的 `CreatePool_Click`。

**问题**

点击创建池后会连续执行创建池、虚拟磁盘、分区和格式化。没有统一预览，也没有在开始前确认。如果中途失败，前面的模拟修改已经提交，用户会看到半成品状态。

**修复方式**

先收集并校验所有参数，再显示一个确认对话框，明确列出：

- 池名称。
- 虚拟磁盘名称。
- 成员磁盘数量。
- Resiliency。
- Interleave。
- Cluster size。
- 虚拟磁盘大小。
- 后续会自动创建分区并格式化为 NTFS。

用户确认后才执行后续步骤。每一步都检查返回结果，如果任一步失败，停止继续执行，并向用户说明“已完成哪些模拟步骤、哪些未完成”。可提示用户使用“重置模拟数据”恢复到初始状态。

本轮不引入新的跨步骤模拟事务机制，以免扩大范围。

**验收**

- 创建池前会显示完整预览。
- 用户取消时不会产生任何模拟修改。
- 中途失败时不会继续静默创建后续对象。

### 2.6 P2：开发页事件流在连接异常时静默停止

**位置**

`src/WinPool.App/DevelopmentPage.xaml.cs` 的 `WatchAgentEventsAsync`。

**问题**

`WatchAgentEventsAsync` 只捕获 `OperationCanceledException`。连接断开、管道读取失败等异常会终止事件流，且通过 `_ = ...` 启动，用户不知道事件日志已经停止。

**修复方式**

增加预期传输异常捕获，更新开发页状态并发布通知：

```csharp
catch (Exception exception) when (
    exception is IOException
        or InvalidDataException
        or JsonException
        or InvalidOperationException)
{
    DispatcherQueue.TryEnqueue(() =>
    {
        _eventLines.Add($"Event stream stopped: {exception.Message}");
        EventScrollViewer.ChangeView(
            null,
            EventScrollViewer.ScrollableHeight,
            null,
            disableAnimation: true);
    });
}
```

**验收**

- 手动停止 Agent 后，开发页事件日志显示停止原因，而不是毫无变化。
- 正常离开页面时不会误报错误。

### 2.7 P2：开发页采集对照按钮没有恢复路径

**位置**

`src/WinPool.App/DevelopmentPage.xaml.cs` 的 `CompareInventoryButton_Click`。

**问题**

按钮先禁用，`SendAsync` 抛出异常时没有 `catch`，也没有 `finally` 恢复按钮。

**修复方式**

为该方法补齐 `try/catch/finally`。失败时显示错误，`finally` 中恢复按钮状态。

**验收**

- Agent 断开时点击对照按钮不会崩溃。
- 按钮会重新变为可点击。
- 错误会显示在 `InventoryComparisonStatus` 或全局通知中。

### 2.8 P2：单实例重定向无限等待

**位置**

`src/WinPool.App/Program.cs` 的 `RedirectActivationTo`。

**问题**

当前使用 `CoWaitForMultipleObjects(0, uint.MaxValue, ...)` 等待重定向完成，没有超时。重定向失败时第二个进程会无限等待。

**修复方式**

设置一个有限等待时间，例如 `10_000ms`。等待超时后不再继续阻塞，转为前台激活已有实例，并记录诊断日志：

```csharp
_ = CoWaitForMultipleObjects(
    0,
    10_000,
    1,
    [s_redirectEventHandle],
    out _);
```

关闭事件句柄前先检查句柄是否有效。该改动不改变正常单实例行为。

**验收**

- 正常二次启动仍会激活已有窗口。
- 如果重定向无法完成，第二次进程会在有限时间内退出，不会永久无响应。

### 2.9 P2：RoboCopy 输出解析缺少异常归一化

**位置**

`src/WinPool.Testing.Tools/RoboCopyAdapter.cs` 的 `ParseAsync`。

**问题**

RoboCopy 输出解析直接调用解析器，没有像 DiskSpd/fio 那样捕获格式、溢出等预期异常。外部输出异常时可能逃出异步枚举。

**修复方式**

在 `ParseAsync` 中捕获 `FormatException`、`OverflowException`、`InvalidDataException`，转换为 `ToolEventKind.Failed`，并附上稳定错误码 `robocopy.output.invalid`。

**验收**

- 使用畸形 RoboCopy 输出运行解析器时不会抛进程级异常。
- 测试运行会得到失败事件，而不是无结果。

### 2.10 P3：格式化分区使用自由文本框

**位置**

`src/WinPool.App/EditPage.xaml.cs` 的 `FormatAsync`。

**问题**

用户需要输入 `NTFS/ReFS/exFAT`，但后端只接受这三个值。自由文本既容易拼错，也不符合 Windows 管理工具的常见交互。

**修复方式**

将输入框改为 `ComboBox`，选项固定为 `NTFS`、`ReFS`、`exFAT`，默认 `NTFS`。用户无法输入无效文件系统。

**验收**

- 格式化对话框中只能选择三种文件系统。
- 默认值是 `NTFS`。

### 2.11 P3：打开资源管理器失败时静默返回

**位置**

`src/WinPool.App/MainPage.xaml.cs` 的 `OpenPartitionAsync`。

**问题**

目标分区路径不存在时直接返回，用户点击后没有任何反馈。

**修复方式**

调用现有的 `NotifyTargetMissing()` 或发布明确通知，告知用户该分区在本机已不存在。

**验收**

- 分区路径缺失时用户会看到提示。
- 正常可访问分区仍能打开资源管理器。

## 3. 明确不做什么

- 不修改 `NamedPipeAgentConnection` 的连接协议。
- 不升级 SQLite schema。
- 不引入新的真实存储操作执行器。
- 不重构 WinPool.Core 或重新引入已退役模块。
- 不新增外部工具安装类型。
- 不重新设计页面布局或视觉样式。
- 不扩大 V0.37 到 V0.4 的视觉打磨范围。
- 不提交、推送、打 tag 或发布，除非用户在后续单独明确批准。

## 4. 实施顺序

建议按以下顺序执行：

1. 先补确定性单元测试或受控测试，复现可测试的缺陷。
2. 修复 P1 语言重入和未观察异常。
3. 修复测试页、设置页、开发页的关键 async void 生命周期。
4. 修复输入校验和创建池预览。
5. 修复单实例重定向超时。
6. 修复 RoboCopy 解析异常归一化。
7. 修复 P3 用户交互问题。
8. 执行完整 Release 自动门和手工验证。
9. 更新 CHANGELOG、README 和文档一致性，归档本 Plan。

每个修复单独提交，便于回滚和评审。

## 5. 自动质量门

从 `Program\WinPool` 根目录执行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

要求：

- 所有测试通过。
- 0 个错误。
- 0 个警告，或每个警告有明确批准记录。
- 不允许 skipped test 被统计为 passed。
- 漏洞审计无未处置漏洞。
- 更新 CHANGELOG 中的实际测试总数，避免再次出现文档与结果不一致。

## 6. 手工验收

至少验证：

- 设置页连续切换语言、主题和强调色不会卡住或重入。
- 测试页 Agent 断开时 Start、Cancel、Prepare 仍能恢复。
- 开发页 Agent 事件流断开时会提示并停止。
- 新建分区输入非法内容时不会创建分区。
- 创建池前显示预览，取消不会产生修改。
- 格式化分区只能选择 NTFS、ReFS 或 exFAT。
- 打开不存在的分区路径会显示提示。

所有手工场景必须记录实际结果。未执行项保持 `unverified`。

## 7. 验收标准

V0.37 只有同时满足以下条件才可标记为完成：

- 第 2 节中的缺陷均有对应实现或明确批准的不适用说明。
- 新增和既有 Release 测试全部通过。
- Release build 0 error，0 warning 或 warning 已批准。
- 依赖审计通过。
- 手工验证结果如实记录，未验证项保持 `unverified`。
- IPC protocol 保持 `3`，SQLite schema 保持 `12`。
- 没有引入真实存储结构修改。
- 文档与实际测试总数、版本和状态一致。

## 8. 当前执行状态

本文件目前只是待批准计划。用户批准前：

- 不修改任何 `.cs`、`.xaml`、`Directory.Build.props`、README、CHANGELOG 或 Git 状态。
- 不执行构建、测试或 staging。
- 不创建新的 Git 提交。

## 9. 实施记录

2026-08-13 已获用户批准并完成实施：

- P1 语言重入、未观察异常和关键 async void 生命周期已修复。
- P2 分区输入、创建池预览、开发页事件/按钮恢复、单实例重定向超时和 RoboCopy 解析归一化已修复。
- P3 格式化文件系统选择器和缺失分区路径提示已修复。
- Release build 通过，0 warning，0 error。
- Release tests 通过 519/519，0 skipped。
- 依赖漏洞审计通过。
- 版本推进至 V0.37。
- 原生 UI、托盘、UAC、设备、外部工具和数据位置手工场景仍保持 `unverified`。
