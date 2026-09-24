# Zcode 独立审查证据台账（2026-09-24）

环境：Windows 10 22H2 (10.0.19045) x64；.NET SDK 10.0.401；被审版本 V0.56（FileVersion 0.5.6.0），
Git 提交 5872374（`Bump WinPool version to V0.56`），工作区起点干净。

---

## 1. 构建（实测）

命令：`dotnet build WinPool.slnx -c Release`

结果：已成功生成。0 个警告，0 个错误。耗时 00:00:56.61。
Union merge: 288 shared, 294 App-only, 10 Agent-only, 0 collisions。

## 2. 自动化测试（实测）

命令：`dotnet test WinPool.slnx -c Release --no-build`

| 测试项目 | 通过 | 失败 | 跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| WinPool.Ipc.Tests | 9 | 0 | 0 | 9 |
| WinPool.Inventory.Tests | 2 | 0 | 0 | 2 |
| WinPool.Domain.Tests | 12 | 0 | 0 | 12 |
| WinPool.Execution.Tests | 42 | 0 | 0 | 42 |
| WinPool.Application.Tests | 291 | 0 | 0 | 291 |
| WinPool.Monitoring.Tests | 26 | 0 | 0 | 26 |
| **WinPool.Architecture.Tests** | **41** | **6** | 0 | **47** |
| WinPool.Agent.Client.Tests | 17 | 0 | 0 | 17 |
| WinPool.Agent.Tests | 51 | 0 | 0 | 51 |
| WinPool.Infrastructure.Tests | 93 | 0 | 0 | 93 |
| WinPool.Persistence.Tests | 130 | 0 | 3 | 133 |
| **合计** | **714** | **6** | **3** | **723** |

失败的 6 项（全部为 `ArchitectureBoundaryTests`，全部为源码/XAML 文本断言）：

1. `DevelopmentPageRemainsGatedAndShowsOnlyInMemoryMessages`
   — `Assert.Contains() Failure ... Not found: "UpdateIssueStates("`（ArchitectureBoundaryTests.cs:1408）
2. `EditorPagesOwnTheirSurfacesWithoutSectionTitles`
   — `Not found: "StackPanel MinWidth=\"412\""`（同文件 line 547；该值在 V0.56 被有意移除）
3. `ProductFacingVersionUsesTheRepositoryVersionSource`
   — 断言 `<WinPoolVersionIteration>5</WinPoolVersionIteration>`，实际为 `6`（同文件 line 866）
4. `TitleBarProvidesStorageSystemSelector`
   — `Not found: "Width=\"320\""`（同文件 line 1013；选择器在 V0.56 改为 260 DIP）
5. `MonitorPageBackgroundModeHasStableKeyboardAccessKey`
   — `Not found: "SetRateAsync"`（同文件 line 1171）
6. `NotificationShellKeepsThreeSimpleCardsAndDeveloperOnlyDetails`
   — `Not found: "UpdateIssueStates("`（同文件 line 1408）

跳过的 3 项：`WinPool.Persistence.Tests.MonitoringArchiveMeasurementTests.*`（1 GiB 合成库测量，
按环境变量门控 Skip，文档口径诚实）。

`docs/CHANGELOG.md` 2026-09-24 条目自述：“标准 `artifacts/Release` 构建通过，0 警告、0 错误；
App 与 Agent 产物均显示 V0.56，未运行自动测试或原生界面验收。”——与本次实测失败状态互证。

## 3. 原生界面实测（实测，native-winui-control 技能：UIAutomation + user32 P/Invoke + 截图）

启动：`artifacts\Release\WinPool.App.exe` → 欢迎窗口“欢迎使用 WinPool ！”+ 主窗口“WinPool”
（WinUIDesktopWin32WindowClass），Agent 托盘进程同起。

页面覆盖：设置、管理、硬件、监控、测试、开发、存储结构编辑、磁盘分区编辑（8 项导航全部可达）。
截图：`shot-02-main.png`（设置）、`shot-03-manage.png`（管理）、`shot-04-hardware.png`（硬件）、
`shot-05-monitor.png`（监控）、`shot-06-test.png`（测试）、`shot-07-partition.png`（磁盘分区编辑）。

### 3.1 正常行为（实测，与文档声称一致）

- 版本：设置页“产品/版本”显示 WinPool / V0.56，与 `Directory.Build.props`（0,5,6）一致。
- 存储系统选择器 `ActiveSystemSelector` 提供 1 个本机系统 + 5 个模拟系统；`[本机]` / `[模拟]` 前缀区分。
- 管理页：拓扑树（`TopologyScrollViewer`）显示 `[本机] DESKTOP-BLE6H18`（1 存储池 3 物理磁盘
  1 网络磁盘 5.46 TiB）与 5 个模拟系统；下方五类对比表（系统/池/层/磁盘/分区）+ 设备列 + 值列；
  “刷新本机信息 / 转换本机到模拟 / 导入模拟系统 / 导出模拟系统 / 删除模拟系统”按钮齐备。
- 硬件页：十段只读报告（Computer, System, Mainboard, CPU, Memory, VirtualMemory, Storage, …）
  + “导出”按钮，与 `docs/Product.md` 声明一致。
- 监控页：真实会话运行中（“运行时长：00:05:15”），表头（图例/名称/池/卷/介质/容量/活动/读取速度/
  写入速度），真实设备行（WDC WD40EZAZ-00SF3B0 HDD 3726 GiB、Samsung SSD 980 PRO 1TB SSD 932 GiB、
  盘符 E:/C:），每行有“为 <设备> 选择曲线颜色”入口；状态为 0% / 0 KiB/s（空闲）。
- 开发页（消息列表）：`MessageList` 本次运行内存消息，实测到 3 条——
  2×「信息 / inventory / 采集成功」（自动采集：存储系统 → 自动采集：完整硬件，先后顺序正确）
  + 1×「警告 / monitor / 监控异常」（“上次监控异常结束：可能有数量未知的未保存样本”，
  对应审查期间被强制结束的前一实例，缺口报告诚实）。
- 存储结构编辑页：拓扑（Primordial + 8-partition NVMe + Empty-HDD + “新建池”占位）；
  “待应用动作：暂无待应用动作”；动作按钮正确呈现禁用态（撤销/重做/放弃结构修改/应用结构修改/
  解散池/退役物理磁盘/热备物理磁盘/创建虚拟磁盘和分区/删除虚拟磁盘和分区均 disabled，
  仅“创建池”可用）。
- 磁盘分区编辑页：拓扑（虚拟磁盘 + 两块磁盘）；8 个操作（联机/脱机/初始化磁盘/转换为 GPT/
  删除分区/扩展分区/压缩分区/打开资源管理器）全部禁用并给出原因；属性栏显示“—”占位
  （`StartOffsetUnavailableValue` 等），与 Product.md“缺失字段显示空白/未知按既有口径”一致。
- 测试页：双语路线说明（“磁盘测试 / Disk tests”“WinPool 2.0”），无可用控件，与 Product.md
  “测试页保持路线说明”一致。
- 无障碍：存在 `AutomationId` 的元素覆盖良好（按 ID 可稳定定位），禁用控件保持可发现。
- 单实例行为：已有实例运行时再次启动，新实例立即以退出码 0 退出并将激活转交现有窗口（实测 2 次）。

### 3.2 无障碍缺陷（实测）

ListItem 的 UIA `Name` 泄漏 .NET 类型名/record 的 `ToString()`，屏幕阅读器会朗读内部表示：

- 主导航 8 项：`name='WinPool.App.ViewModels.ShellNavigationItem'`（显示文本在子元素 Text 中）。
- 管理页类别列表 5 项：`name='CategoryItem { Category = System, Title = 系统, Glyph = ?? }'`
  （Glyph 显示为乱码字符 `??`）。
- 开发页消息列表：`name='SessionMessageItem { Id = 938559…, CreatedAt = 11:42:37, Severity = 信息,
  Source = inventory, Title = 采集成功, Notification = GlobalNotification { … } }'`
  （整对象序列化，且含乱码段）。

### 3.3 视觉观察（截图 + 视觉分析）

- 管理页：1440×900 窗口下属性表最右列（如“可访问卷”）被右边界截断，未见明显横向滚动提示；
  拓扑区纵向截断（属滚动容器内正常行为，但首屏可读性受影响）。
- “真实编辑”开关位于标题栏，无就近说明文本（`LocalRealOperationsLabel` 仅“真实编辑”四字），
  切换后的后果只能在 Product/设计文档中了解。
- 整体视觉一致、密度适中、深浅主题表面使用统一（设置页分组卡片、编辑页分区栏、通知单层卡片）。

## 4. 运行期稳定性专项（实测 + 第三方日志交叉印证）

### 4.1 受控存活实验（本机独占期）

| 轮次 | 时间 | 条件 | 结果 |
| --- | --- | --- | --- |
| A | 10:58–11:10 | 无干预 | 存活 ≥ 300 s（Experiment 记录 PHASE3 alive at 300s） |
| B | 11:05–11:10 | 完整 UIA 导航（硬件、监控）+ 截图 + 前台切换 | 存活 ≥ 300 s |
| C | 11:42–11:47 | 完整 UIA 导航 + 截图 + 前台切换 | 存活 ≥ 300 s（PHASE3 alive at 300s） |
| D | 10:58 | 已有实例运行时启动第二实例 | 2 s 内退出，退出码 0（单实例激活转发，预期行为） |

前两轮多代理并发窗口内（10:54–11:00）曾观察到实例连同 Agent 一起消失：
`app-crash.jsonl` 无新增、无对应 WER 记录；用户随后确认当时“有多个 Agent 抢夺真实软件控制”。
C 轮起为独占环境，未再复现。

### 4.2 WER 崩溃报告（`C:\ProgramData\Microsoft\Windows\WER\ReportArchive`）

共 7 份 `AppCrash_WinPool.App.exe_*`：

| 时间（本地） | 版本 | 故障模块 | 异常代码 | 偏移 |
| --- | --- | --- | --- | --- |
| 2026-09-22 11:44:05 | 0.5.5.0 | combase.dll | 80004003 | 0x261d2 |
| 2026-09-23 13:27:09 | 0.5.5.0 | combase.dll | 80004003 | 0x261d2 |
| 2026-09-23 14:43:04 | 0.5.5.0 | combase.dll | 80004003 | 0x261d2 |
| 2026-09-23 20:17:02 | 0.5.5.0 | combase.dll | 802b000a | 0x261d2 |
| 2026-09-24 10:14:08 | 0.5.5.0 | combase.dll | 80004003 | 0x261d2 |
| **2026-09-24 11:17:14** | **0.5.6.0** | combase.dll | 80004003 | 0x261d2 |
| **2026-09-24 11:34:04** | **0.5.6.0** | combase.dll | 80004003 | 0x261d2 |

11:34:04 报告细节（实测读取 `Report.wer`）：

```
Sig[1].Value=0.5.6.0              应用程序版本
Sig[3].Value=combase.dll          故障模块名称
Sig[4].Value=10.0.19041.7725      故障模块版本
Sig[6].Value=80004003             异常代码（E_POINTER）
Sig[7].Value=00000000000261d2     异常偏移
Response.BucketId=575b8f4275f9df767507b0011c01be63
UI[2]=D:\Coding\Research03_WinPool\Program\WinPool\artifacts\Release\WinPool.App.exe
LoadedModule[0]=…\artifacts\Release\WinPool.App.exe
```

即：两次崩溃都发生在本次审查构建的 0.5.6.0 版本、运行于标准 `artifacts\Release` 运行树。

### 4.3 对照实验与时间线（实测）

- **强制结束不产生 WER 报告**：本轮审查在 11:05:44 与 11:42:23 两次以 `Stop-Process -Force`
  结束 WinPool.App/Agent，WER 目录中对应时刻无任何新增报告 → 4.2 的两次崩溃是真实的进程内故障，
  不是强杀产物。
- **运行树在实例运行期间被改写**：`artifacts/Release/WinPool.App.{exe,dll}` 时间戳
  2026-09-24 10:56:40；`artifacts/Release` 10:57:00；`artifacts/trees` 10:56:23。
  审查期第一个实例约 10:53 启动、10:56–10:57 消失，时间窗与运行树重写完全重合。
- **并发审查代理活动**：`artifacts/test-results/` 下存在
  `GPT-review-20260924`（10:57:07）与 `grok-independent-review-20260924`（11:18:41）目录，
  说明同期有其他自动代理在跑测试/构建（构建会写入并替换 `artifacts\Release`）。
- **App 自有崩溃日志盲区**：`%LocalAppData%\WinPool\Diagnostics\app-crash.jsonl` 仅 1 条
  （2026-09-23T12:17:01Z，`XamlUnhandled` / `XamlParseException: Cannot locate resource from
  'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'`），对应 WER 09-23 20:17:02 那条；
  其余 6 条（含 2 条本机构建版本）无任何自有日志。
- 代码依据：`src/WinPool.App/App.xaml.cs:64-67` 注册了三个处理器
  （`UnhandledException` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`），
  `App_UnhandledException`（同文件 96-109 行）只写日志、未设置 `e.Handled = true`。
  COM/原生访问冲突不经过托管 `AppDomain` 异常通道，故不落盘。

## 5. 其他核对（实测）

- `docs/CHANGELOG.md` 与 `Quality.md` 的口径：CHANGELOG 主动记录“未运行自动测试”“旧运行树未替换”等
  不利事实，可追溯性好。
- README（中英）与 `docs/Product.md` 均包含根 `AGENTS.md` 要求的 64K+64K 研究结论原文，措辞一致。
- 真实写操作不可达：`主体为静态审查结论（子代理 + 抽检）`，见主报告第 2 章；
  本轮审查未触发任何真实存储写操作，未切换“真实编辑”开关，未删除或重建作者数据。
