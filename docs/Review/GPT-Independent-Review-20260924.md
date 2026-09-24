# GPT 独立审查报告：WinPool V0.56

> 审查日期：2026-09-24（Asia/Taipei）。本文为独立审查，包含源码检查、自动回归、生产程序集负例验证与原生 Windows 软件测试。审查期间不修改产品实现，不执行真实存储结构变更。

## 1. 结论与审查边界

**V0.56 已具备有实际价值的只读存储观察和模拟编辑能力，但尚不满足正式发布的质量条件。** 主要风险集中在数据位置切换恢复、导入数据的结构合法性、监控导出和无障碍访问；自动测试门也存在确定失败。当前没有真实存储写功能是明确的产品边界，不作为缺陷。

本轮归纳 **1 项 P1、7 项 P2**，其中包含一项质量门缺陷；偏好竞态仅有静态证据，其余关键交互问题有原生实测或生产程序集的隔离验证。构建最终通过，依赖审计未检出已知漏洞包；测试为 **714 通过、6 失败、3 跳过**。

审查对象为本地 `Program/WinPool` 与其 [GitHub 仓库](https://github.com/GitRuozhi/WinPool)。用户给出的另一种本地路径未找到，实际工作目录为 `D:\Coding\Research03_WinPool\Program\WinPool`。

- 原始基线：`main` / `58723748ad3591ec12c9b16dc142142e1330376b`，V0.56；开始时工作区干净，`git ls-remote` 确认远端 main 与该提交一致。
- 恢复审查时 HEAD：`8dbb9f6`。相对原基线的已提交变化仅为其他审查文档；产品代码、构建配置和测试源码未变。本报告不引用其他审查报告的结论。
- 环境：Windows 10 22H2 x64，OS build 19045；.NET SDK 10.0.401，符合 `global.json` 10.0.400 + `latestPatch`。Windows 11、其他 DPI、其他设备矩阵未在本轮覆盖。
- 工作分工：主审负责范围、证据复核、原生交互和报告；三个子代理分别负责安全/持久化、产品/业务、构建/自动测试。原生桌面在恢复审查后由主审独占。
- 只检查当前源码和相关权威文档，没有声称逐行审计整个仓库，也没有将自动测试覆盖率等同于代码覆盖率。

### 证据强度

本文区分原生实测、真实程序集的隔离验证、静态可达路径和未验证事项。P1 表示应优先修复的严重数据可靠性问题；P2 表示影响正常工作流、质量门或可访问性的缺陷。没有发现可据本轮证据定为 P0 的问题，这不构成不存在其他缺陷的保证。

原始 `artifacts/test-results` 曾在多个审查任务并行时被外部重建，原日志和 TRX 随之消失。保留了终端恢复摘要，并以现有测试 DLL 串行重跑、重新保存 TRX；没有伪造原始文件。随后证据统一写入 Git 忽略的 `local-assets/GPT-review-20260924/evidence/`。并发阶段的跳页不作为软件缺陷或有效交互通过证据。

## 2. 主要发现

### GPT-01 · P1 · Portable→Standard 迁移中断可能让启动读取错误的数据根

**证据：静态完整调用链 + 生产 DLL 的隔离状态验证；未对用户数据库制造崩溃。**

位置指针固定放在 Standard 根中。Portable→Standard 切换时，目标替换先把整个 Standard 根移到 rollback，再把 staging 放回目标，最后提交指针。因此第一步也会移走位置指针。在该目录改名之后、后续提交之前发生进程崩溃或断电，下一次启动可能找不到指针。

运行时缺失指针的回退逻辑只把旧名 `Data/settings.json` 当成 Portable 标志；当前使用的是 `app-settings.json` 和 `agent-settings.json`。隔离探针在 Portable 放置这两种当前偏好文件及 `winpool.db` 标记，模拟目标改名后调用真实程序集的解析方法，得到：

```text
pointer_at_fixed_path=False
pointer_in_rollback=True
selected_standard=True
latest_portable_marker_exists=True
```

**影响：** 最新 Portable 数据仍物理存在，但应用可能转而读取或初始化空的 Standard 根，造成用户数据暂时不可见。后续规划迁移还会按名称/GUID 模式直接清理 rollback，失去旧 Standard 目标的恢复副本。不能把此问题表述成“最新 Portable 数据必然被删除”。

代码定位：[StorageLocationManager.cs](../../src/WinPool.Infrastructure.Sqlite/StorageLocationManager.cs) 的指针初始化（137）、规划清理（200–201）、目录替换（426、797–813）、清理逻辑（851 起）；[StorageDataLocations.cs](../../src/WinPool.Infrastructure.Windows/StorageDataLocations.cs) 的解析与旧文件名回退（107–129）。

建议：把权威指针置于两个可替换根之外，或在解析数据位置前根据持久化事务状态完成恢复；只有确认提交完成后才回收 rollback。验收需覆盖两种迁移方向、每个目录改名和指针提交边界的进程终止，而不只验证可捕获异常。

### GPT-02 · P2 · 导入入口接受物理不可能的重叠分区

**证据：生产程序集的隔离负例及完整原生导入/编辑/导出链均已复现；与合法但暂不支持编辑的已有 Windows 结构不同。**

从生产内置 `SimulationLayouts.ManyPartitions()` 构造合法文档，原结构审计结果为 0。把同一 OS Disk 的第二分区 Offset 从 `105906176` 改为第一分区的 `1048576`，保持对象身份和引用完整。实际运行结果：

```text
JSON shape validator: accepted
imported document metadata/facts: accepted
Agent payload encode: accepted, 118999 chars
simulation auditor findings: 1  [overlap]
```

导入只校验 JSON 形状、版本、基础元数据和身份等；已有 `SimulationSnapshotAuditor` 能发现重叠，却没有接入这条生产导入链。

独占原生复测进一步通过“导入模拟系统”的文件选择器导入该负例，系统选择器显示 `[模拟] GPT Review Overlap 20260924`。在分区页选择其 C: 后，卷标输入框处于启用状态；输入 `GPT_REVIEW` 并按 Enter，随后导出 JSON 确实包含该新卷标。它不仅能通过辅助校验器，也能被实际软件保存、选中和继续编辑。测试结束通过软件删除的仅是本次新建模拟样本；负例与导出文件仍保留。

**影响：** 用户可以把几何上无效的结构载入可编辑模拟系统，削弱“模拟只允许合法配置”的产品承诺。该发现不证明能写坏真实磁盘，V0.56 仍无真实存储写操作。

代码定位：[DesktopExportService.cs](../../src/WinPool.App/Services/DesktopExportService.cs)（101–162）、[WorkspaceViewModel.cs](../../src/WinPool.App/ViewModels/WorkspaceViewModel.cs)（965–983）、[SimulationSnapshotAuditor.cs](../../src/WinPool.Application/SimulationSnapshotAuditor.cs)（9–22、67 起）。

建议：在导入后的保存/激活边界检查重叠、越界等物理不可能条件，必要时拒绝编辑并明确错误。不能简单以当前编辑器的创建能力裁掉合法的已采集结构。保留本轮重叠 JSON 作为真实入口回归输入。

### GPT-03 · P2 · 停止监控后，已落库的会话数据失去 CSV 导出入口

**原生实测：failed。** 在 20 Hz、3 个本机设备的监控会话中，运行时成功导出 510,940 bytes 的 CSV。关闭“持续监控”并停止后，再次选择一个新的 CSV 路径，软件提示 `There is no monitoring data to export yet.`，未生成该文件。此时只读查询活动 `monitoring.db` 得到同一最新会话已有结束时间、state=4、**5,880 条样本、3 个设备、dropped_samples=0**。页面也显示 `Stopped`；不是空库、轮换归档或文件权限导致的失败。

**静态依据：** `StopAsync` 在停止确认后清空 `_remoteSessionId`，`ExportCsvAsync` 在该值为空时直接返回 false；页面仍打开保存对话框，之后提示没有可导出数据。常驻监控观察器在“持续监控”关闭时保持停止，重新进入监控页不会启动新会话；导出前刷新无活动会话的快照也会再次清空该 ID。

目标场景为：持续监控开启并已落样 → 关闭持续监控 → 确认停止 → 导出。停止本身不会删除活动数据库中的样本，Agent 按已知会话 ID 仍有读取能力。

代码定位：[MonitoringService.cs](../../src/WinPool.App/Services/MonitoringService.cs)（182–281、411–435、727–730）、[MonitorPage.xaml.cs](../../src/WinPool.App/MonitorPage.xaml.cs)（894–933）、[MonitorAlertObserver.cs](../../src/WinPool.App/Services/MonitorAlertObserver.cs)（66–93）。

建议：保留最近停止且仍位于活动库的会话用于导出，或明确提供活动库会话选择。仅在查询确实无样本时显示“无数据”，并让按钮说明与实际单会话/整库范围一致。

### GPT-04 · P2 · 多项设置异步保存使用完整旧快照，存在相互覆盖的竞态

**证据：静态可达交错；未测实际发生率，不声称已在真人快速操作中复现。**

开发者模式保存先读取完整偏好快照，异步保存成功后再写回内存。主题等入口则先更新内存并独立保存。底层 `SaveLock` 只串行文件写入，不保护上层“读取最新偏好—合并—保存—回填”的整体过程。

若开发者模式保存因异步 I/O 挂起，此时用户改主题，旧开发者模式快照完成后会重新带回旧主题；导航刷新还可能继续保存该旧快照。设置页并未在前一次保存期间禁用主题操作。因此两次合法输入可能只保留其中一次，并造成当次视觉与下次启动偏好不一致。

代码定位：[WorkspaceViewModel.cs](../../src/WinPool.App/ViewModels/WorkspaceViewModel.cs)（749–795、1160 起）、[WindowsServices.cs](../../src/WinPool.Infrastructure.Windows/WindowsServices.cs)（205–246）、[SettingsPage.xaml.cs](../../src/WinPool.App/SettingsPage.xaml.cs)（407、486 起）。

建议：所有 App 偏好更新通过同一个上层串行入口，在获取门后读取最新值、合并字段、保存成功后回填。用可控延迟偏好服务验证最终内存和磁盘一致性；不要靠增加 UI 延时降低发生概率。

### GPT-05 · P2 · 导航和多项设置缺少有意义的无障碍名称

UIA 在真实程序中读到多个壳导航项的 Name 都是 `WinPool.App.ViewModels.ShellNavigationItem`；设置页主题、主题色、语言下拉框，以及部分按钮/开关的 Name 为空。控件旁的可见标签与 HelpText 没有自动成为准确的控件名称。

这一结果在并发阶段和恢复后的独占标准 Release 运行中均出现。英语图标导航也没有形成能区分页面的 Name，因此不是单一中文资源缺失。

**影响：** 依赖可访问性树导航的用户和自动化工具不能通过名称区分主要页面或部分设置。此处是 UIA 属性实测，不等同于完成 Narrator 全旅程验收。

代码定位：[MainWindow.xaml](../../src/WinPool.App/MainWindow.xaml)（53–105）、[SettingsPage.xaml](../../src/WinPool.App/SettingsPage.xaml)（51–89 等）、[ContextHelp.cs](../../src/WinPool.App/Services/ContextHelp.cs)（83–92）。

建议：导航容器绑定本地化 Title 为 Name；设置使用本地化 Name 或 LabeledBy。结合 Tab 顺序、图标模式、中英文和屏幕阅读器验证，不只为测试工具添加 AutomationId。

### GPT-06 · P2 · 当前自动质量门失败，架构断言与当前实现不同步

保留的 11 份 TRX 共 723 项：**714 passed、6 failed、3 NotExecuted（跳过）**。6 项失败全部位于 Architecture.Tests。具体断言与处理建议见下一节。

至少产品版本仍断言 iteration=5，而产品已为 iteration=6；另有硬编码布局宽度、方法名和视觉树容器类型。应修复测试与明确需求的对应关系，保留真实边界测试，不能直接忽略红门，也不能为满足旧字符串把产品改回旧布局。

### GPT-07 · P2 · 允许的最小窗口宽度裁掉导航和当前系统上下文

**原生实测：failed。** 在 96 DPI、开发者模式开启、中文分区页，将主窗口从 1440×900 缩至 **480×700**。截图中设置入口不可见，右侧系统选择器与真实编辑状态也不能完整辨认。UIA 记录最后一个导航项 offscreen、倒数第二个只剩 6 像素宽；系统选择器从屏幕 x=1395 延伸到 1655，而窗口右边界为 1480。恢复 1440×900 后正常。

480 是程序显式允许的最小宽度（[MainWindow.xaml.cs](../../src/WinPool.App/MainWindow.xaml.cs):195）。紧凑模式只隐藏未选中导航项的文字，仍保留横向 ListView、选中页文字与固定最小宽的其他标题栏内容，未提供足够的溢出策略（同文件 1427 起；[MainWindow.xaml](../../src/WinPool.App/MainWindow.xaml):53–158）。

**影响：** 在合法窗口大小下，用户无法通过可见导航进入全部页面，也容易失去当前是本机还是模拟系统的上下文。快捷键仍有用，不能代替可见入口。建议提供导航溢出菜单、可收缩的系统上下文区域，或把最低宽度与实际可用布局对齐；应分别验证普通和开发者模式、中英文。

### GPT-08 · P2 · 硬件采集失败仍同时发布成功消息

**原生实测 + 静态链路：failed。** 独占桌面下点击硬件页“刷新”，12:09:11 的会话消息同时记录：

```text
inventory / workspace.scan.failed
采集失败；已保留上一次成功结果。

hardware / hardware.refresh.completed
硬件信息已刷新；已完成本机只读硬件刷新。
```

`ScanCoreAsync` 捕获异常后发布失败消息，但返回正常完成的 Task；`HardwarePage.Refresh_Click` await 该任务后无条件发布成功。因此失败被误报为已刷新。相同入口在扫描门未取得、直接 return 的情况下，也没有能让页面区分“未执行”与“成功”的结果。

代码定位：[WorkspaceViewModel.cs](../../src/WinPool.App/ViewModels/WorkspaceViewModel.cs)（1256–1325）、[HardwarePage.cs](../../src/WinPool.App/HardwarePage.cs)（393–405）。本轮同时观察到短暂 Agent 断连及后续恢复，但未确定断连根因；无论失败原因如何，都不能发布与之矛盾的成功消息。

**影响：** 用户可能把保留的旧硬件事实理解为最新结果。建议刷新返回成功/失败/跳过/取消的明确结果，或统一由 ViewModel 在文档成功应用后发出唯一的完成通知。

## 3. 构建与自动验证

### 构建与依赖

恢复独占测试后，执行标准 `dotnet build WinPool.slnx -c Release --no-restore -m:1`，**exit 0、0 warnings、0 errors，49.70 秒**。运行树为 288 shared + 294 App-only + 10 Agent-only = 592 文件，0 collisions。App/Agent FileVersion 均为 `0.5.6.0`，本轮原生测试使用这次标准 `artifacts/Release` 产物。

| 产物 | SHA-256 |
| --- | --- |
| WinPool.App.exe | `FC3E7EED19DF7A7C2774F949DDF26905EDE569D957C2B6072F064D97FD0BB65E` |
| WinPool.Agent.exe | `9B73C11ED1BBE57DBAA7B1DE420ABBB1709CC7685A5CE898838E53BFAF265E02` |

NuGet 直接与传递依赖漏洞审计 **exit 0**，22 个项目均未报告已知易受攻击包。这仅说明当时可访问的漏洞数据库未检出，不涵盖供应链签名、第三方二进制来源或未来新增公告。

首次并发阶段的构建在替换 Release 时因 `Assets/CAppIcon.ico` 访问被拒而失败，不能归因到某个特定外部进程。另有构建工具健壮性风险：`Merge-RuntimeTrees.ps1`（127 起）删除旧目录后才移动新树，删除中途失败没有事务回滚保证。最终标准构建成功解决了本轮运行阻碍，不代表该失败恢复设计已改变；该项作为工程风险保留，不另计一个用户功能缺陷。

### 回归结果

| 测试项目 | 总数 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: | ---: |
| Agent.Client | 17 | 17 | 0 | 0 |
| Agent | 51 | 51 | 0 | 0 |
| Application | 291 | 291 | 0 | 0 |
| Architecture | 47 | 41 | 6 | 0 |
| Domain | 12 | 12 | 0 | 0 |
| Execution | 42 | 42 | 0 | 0 |
| Infrastructure | 93 | 93 | 0 | 0 |
| Inventory | 2 | 2 | 0 | 0 |
| Ipc | 9 | 9 | 0 | 0 |
| Monitoring | 26 | 26 | 0 | 0 |
| Persistence | 133 | 130 | 0 | 3 |
| **合计** | **723** | **714** | **6** | **3** |

首次运行完整 solution test；原证据目录被外部重建后，串行执行固定哈希的已编译测试 DLL，使用独立结果目录恢复 TRX。两轮总结果一致。恢复回归不触发 MSBuild，不修改源码。

3 项跳过是两个 1 GiB 生产阈值归档测量及一个代表性压缩参数比较测量。它们没有通过，不能据其余测试推导生产规模的归档吞吐、长期连续性或崩溃零丢样保证。

| 失败测试（ArchitectureBoundaryTests） | 位置 | 实际失败点 |
| --- | --- | --- |
| ProductFacingVersionUsesTheRepositoryVersionSource | 866 | 期望 iteration=5，当前为 6 |
| TitleBarProvidesStorageSystemSelector | 1013 | 期望 `Width="320"` 字符串 |
| EditorPagesOwnTheirSurfacesWithoutSectionTitles | 547 | 期望 `StackPanel MinWidth="412"` 字符串 |
| DevelopmentPageRemainsGatedAndShowsOnlyInMemoryMessages | 1313 | 期望 TextBox，当前对象为 Border |
| MonitorPageBackgroundModeHasStableKeyboardAccessKey | 1171 | 期望 `SetRateAsync` 方法名字符串 |
| NotificationShellKeepsThreeSimpleCardsAndDeveloperOnlyDetails | 1408 | 期望 `UpdateIssueStates(` 字符串 |

以上记录断言失败的直接原因；不把它们解释成六种对应产品功能必然损坏。涉及布局和消息入口的项目仍需行为与原生证据，不能仅凭名字相似断言过时。

## 4. 真实软件测试

独占复测约 **12:03–12:12**，使用原生 WinUI、UIA、实际鼠标/键盘、Windows 文件选择器及托盘。`winapp-ui-automation` 与 `native-winui-control` 用于取证；本机安装的 CLI 缺少技能文档中部分新参数，遇到不支持的参数后改用已验证的 UIA API，工具调用失败不计为产品失败。默认窗口截图曾全黑，改用屏幕取证后人工检查有效图像；黑图不作为视觉证据。

| 场景 | 方法与观察 | 结果 |
| --- | --- | --- |
| 标准启动 | 无 App/Agent 时启动标准 EXE，主窗口和欢迎窗出现；关闭欢迎后可操作 | passed |
| 自动采集 | 消息列表记录存储、完整硬件两个自动阶段成功 | passed，仅当前本机 |
| 中英与主题 | 设置切 English / Light，再恢复 SystemDefault / System；英文页面、浅色画面实际可见 | passed，非全页翻译审计 |
| 开发者模式关闭 | 从 8 个导航项降为 5 个；Ctrl+6 未进入开发页；之后恢复开启 | passed |
| 本机只读边界 | 选择真实 C:，卷标、删除、分区动作等均 disabled；没有触发真实编辑 | passed，仅被检查入口 |
| 手动存储刷新 | 调用管理页刷新，消息记录 12:08:03 采集成功 | passed |
| 手动硬件刷新 | 同次记录失败与成功；原值保留 | failed，GPT-08 |
| 运行时监控 | 3 个真实设备、20 Hz，图表和表格有变化；运行中 CSV 实际落盘 | passed，短时观察 |
| 停止后监控导出 | 已落库 5,880 条，UI 仍提示无数据，无输出文件 | failed，GPT-03 |
| 模拟导入 | 重叠负例通过原生导入并成为选中系统 | failed，GPT-02 |
| 模拟卷标编辑/导出 | 对本轮样本改卷标并按 Enter，JSON 导出包含 `GPT_REVIEW` | passed，且佐证 GPT-02 可继续编辑 |
| 结构页与测试页 | 本机结构页呈现只读状态；测试页为明确的后续功能说明 | passed，仅显示/导航 |
| 消息详情 | 双击实际消息打开只读详情；读回来源、代码与内容 | passed；未验长文本复制完整性 |
| 无障碍名称 | UIA 导航重复内部类型名，多项设置 Name 空 | failed，GPT-05 |
| 窗口宽度 | 1440×900 可用；480×700 的标题栏/导航裁切；恢复尺寸正常 | failed，GPT-07 |
| App/Agent 生命周期 | 正常关闭主窗口后 Agent 继续；重新打开 App 使用同一 Agent PID；托盘“退出 WinPool”后两进程均退出 | passed |
| 设置与样本恢复 | App 偏好与测试前逐字段一致；监控原配置恢复；本轮模拟样本移除，导出证据保留 | passed |

本轮没有通过缩窗截图推断所有 DPI 都会出错，也没有将一次短时 CSV 成功解释成长时监控稳定性。最早一轮只读 `PRAGMA quick_check` 对核心库和监控库均为 `ok`；这只是当时 SQLite 一致性观察，不是故障恢复或存储介质验证。

## 5. 产品设计评价

**定位合理，产品语义比单纯界面原型更完整。** 池、层、磁盘、分区关系可统一观察，本机与模拟系统明确标记，命令预览与 Agent 受控提交有独立边界。V0.56 的价值在只读观察和模拟演练；不能把 Windows 10 上的这次软件测试外推成全部 Windows 存储组合的可靠性证明。

**安全设计存在明确优点。** 类型化请求、身份与进程校验、有界 IPC 帧、SQLite 单写入方、修订/哈希/提交回执检查，以及模拟协调器对本机编辑的拒绝，均比只依赖界面禁用可靠。此次审查没有发现一条已证实可以绕过当前边界执行自由形式真实存储命令的路径。该结论限于已检查范围，不是渗透测试认证。

**数据工作流应先于新增功能收口。** 对存储工具而言，数据在哪里、导入是否可信、停止后的记录能否带走，比继续增加页面更影响信任。GPT-01、GPT-02、GPT-03 都落在这些基本承诺上。

**可用性与可访问性需要分开验收。** 标准宽窗中的布局可辨认，不代表图标模式、最小窗口或屏幕阅读器可用。显式模拟标志和禁止真实修改的当前边界有助于降低误解；后续开放真实操作时，应在准确目标、预览、会话同意和结果复核处继续保持清晰区分。

本次宽窗实测中，拓扑、属性和动作分区清楚，原生文件选择器、即时模拟改名与托盘生命周期能组成完整旅程。但对错误状态的反馈还不够可靠：停止后的数据被说成不存在、采集失败又被说成成功。相比视觉细节，这两类矛盾更直接影响用户对存储工具的判断，应优先处理。

**工程基础较好，质量证据的维护落后于迭代。** 大量行为、持久化、IPC 和故障路径测试值得保留；硬编码方法名/布局字符串的架构测试容易随实现变化而失效。缺少覆盖矩阵的事项应保留未验证状态，而不是靠累积历史“通过”记录填补。

## 6. 优先处理建议与未验证事项

1. 优先修复数据根迁移的崩溃恢复，验证每个提交边界，确保旧指针/rollback 不会被当作垃圾提前清理。
2. 补齐导入结构审计和停止后 CSV 导出闭环，保留本轮负例与真实交互作为回归输入。
3. 修正硬件刷新成功判定，统一偏好更新入口，补本地化可访问性名称和窄窗导航策略，恢复自动质量门。
4. 再完成 Windows 10/11、DPI、高对比度、键盘/读屏、长时监控、真实 1 GiB 归档和数据位置往返的发布矩阵。

本轮不覆盖真实存储写入、Windows 11 等价平台测试、签名 MSIX/Store 安装、UAC 安全桌面交接、突断电恢复、生产规模长时间压测或完整屏幕阅读器旅程。没有执行的项目均为 `unverified`，不是 passed。

大文档导入的 64 MiB 文件限制与约 3 MiB 文档 IPC 限制存在差异，但本轮没有正常导出的大文档实样证明用户实际遇到此边界，因此只列待补测事项，不计入已确认缺陷。

研究表述保持原项目范围：

```text
64K interleave + 64K NTFS cluster = current tested recommendation.
Windows 11 has not yet received equivalent testing because current storage hardware prices and the author's practical budget do not allow a second full test platform.
```

## 7. 证据索引与复验

本地证据位于 `local-assets/GPT-review-20260924/evidence/`，没有作为公共原始硬件证据提交。报告本身包含基线、步骤、关键输出和结果；原始截图、TRX、程序集哈希、模拟 JSON、探针及数据库聚合观察留在本地供复核。

| 证据 | 内容 |
| --- | --- |
| `security-notes.md`、`security-repro/` | 迁移调用链、假根探针、实际程序集输出与哈希 |
| `product-notes.md`、`import-repro/` | 业务路径、模拟重叠 JSON、生产校验器/编码器运行结果 |
| `vstest-results-retry/*.trx` | 11 个测试项目的保留结果 |
| `test-dll-source-manifest.csv` | 恢复回归的测试程序集哈希 |
| `terminal-recovery-summary.txt` | 原始日志遗失后的终端摘要，不能代替原 TRX |
| `runtime-source-manifest.csv` | 并发阶段隔离运行副本的 592 文件哈希 |
| `runtime-database-observation-1.json` | 只读 SQLite quick_check 与监控聚合统计，不含硬件明细 |
| `build-test-notes.md`、`build-standard-final.log` | 独占标准构建、版本/哈希和架构断言复核 |
| `dependency-audit.log` | 本轮 NuGet 漏洞审计 |
| `exclusive-ui/README.md` | 独占复测证据说明、文件名偏差和最终恢复状态 |
| `exclusive-ui/05-monitor-stopped.*`、`exclusive-ui/monitor-stopped-db.json` | 停止后的 UI 提示、真实会话样本统计 |
| `exclusive-ui/06-overlap-imported.*`、`exclusive-ui/simulation-export.json` | 完整 UI 导入与改名后的导出结果 |
| `exclusive-ui/08-partition-480.*` | 96 DPI 最小宽度的截图与 UIA 边界 |
| `exclusive-ui/16-messages-final.json` | 同一手动硬件刷新产生的失败/成功通知及恢复记录 |
| `exclusive-ui/17-settings-final.json` | 独占设置页的无障碍属性 |
| `exclusive-ui/preferences-restored.json`、`exclusive-ui/20-reopen.json`、`exclusive-ui/21-tray-exit.json` | 设置恢复和 App/Agent 正常生命周期 |

可复验的标准命令：

```powershell
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

复验前停止准确路径下的 WinPool App/Agent，并避免其他任务并发重建同一个运行树。探针只使用隔离假数据；不得把本报告视为对真实磁盘变更的授权。
