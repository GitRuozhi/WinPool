# WinPool 接手审查

日期：2026-10-17。产品版本以 `Directory.Build.props` 为准，界面关于页显示 V0.60。仓库 `Program\WinPool`，在线仓库 https://github.com/GitRuozhi/WinPool。本文是代码和文档的接手审查与改造方案。这次没有改产品行为，没有提交，没有写入磁盘。

审查结束时 `Directory.Build.props` 已恢复。为了让测试能跑起来而临时使用的输出目录在本机 Temp，仓库里的联接目录 `artifacts\medium-audit` 已去掉。

## 结论

慢是结构性的。Windows 存储查询本身不慢。这次在已有进程里执行 `Import-Module Storage` 用了 993ms，随后 `Get-Disk`、`Get-Partition`、`Get-Volume`、`Get-PhysicalDisk`、`Get-StoragePool`、`Get-VirtualDisk` 合计 419ms。冷启动一个 Windows PowerShell 5.1 进程做完这两段，墙钟 2435ms。

WinPool 把同一种只读采集包进新的 `powershell.exe`，并且在一次操作里重复做。正在运行的 Agent（2026-10-16 23:21 启动，进程 10544）留下的 35 条计时里，每次 `inventory.provider.collect` 都超过 0.5 秒，10 次的中位是 5068ms，最长 13515ms。其中一次真实操作的外层阶段是准备 5547ms、接受前核对 4482ms、步骤前核对 5171ms、执行 12605ms，合计 27805ms。这四段已经包住内部采集，不能再把里面 4.4–5.2 秒的采集加进去。执行段没有单独的 `real.provider` 记录，12605ms 对得上写前和写后的采集，对不上“Windows 写命令本身要十几秒”。

2026-10-16 已记录的真实操作更长：删除虚拟磁盘 30.94 秒，新建 MAX 虚拟磁盘加自动布局 247.69 秒；60 轮 provider 采集 223.53 秒，约占 278.20 秒阶段总和的 80%。出处是 `docs/Quality.md` 当天的耗时调查。那次写操作这次没有重做。

冗余和慢是同一件事。同一功能有多套计划器、多条采集入口，模拟编辑还把整份文档、计划和事件再送过命名管道。按第一性原理只留两条路：

- App 持有草稿和模拟，改动留在内存，防抖后只保存文档。
- Agent 是唯一的特权写入者。一个步骤只做一次新鲜读取、一次写入、一次读回，然后把终态推给 App。

同一功能不保留第二套实现。安全检查收成一次身份、角色和写后状态核对。结果未知时不重放写入。

当前存储试验结论不变：

```text
64K interleave + 64K NTFS cluster = current tested recommendation.
Windows 11 has not yet received equivalent testing because current storage hardware prices and the author's practical budget do not allow a second full test platform.
```

`64K + 64K` 在现有试验中稳定。`64K + 256K` 是两阶段 NTFS 损坏：先看起来恢复，随后永久写失败。`256K + 64K` 已经出现过失败，目前不推荐。`256K + 256K` 是直接 NTFS 损坏，Event 55。

## 范围

读了 `AGENTS.md`、`docs/Development.md`、`docs/Quality.md`、`docs/WinPool-Operation-Latency-20261008.md` 和 `docs/Review/README.md`。源码按调用链读了采集、真实执行、模拟提交、IPC 和界面等待。没有逐篇改历史文档。`docs/Quality.md` 仍链到工作区里已经不在的 `docs/Review/Pool-VD-Timing-20261016.md` 等四份文件；那些删除不是这次做的，本文不恢复它们。历史数字以 `docs/Quality.md` 为准。

源码 291 个 `.cs`/`.xaml`，86937 行。测试 136 个 `.cs`，46327 行。静态检查覆盖 App、Agent、Infrastructure.Windows、Application、Execution、Ipc。

这次允许的本机动作是只读存储查询、启动已有的 Release App、用 UI Automation 切换页面，以及在 Medium 目录里跑测试。真实编辑开关保持关闭。没有点 Apply，没有新建或删除池、虚拟磁盘和分区。

## 现在的结构

App 提出类型化计划。Agent 重新采集 Windows 状态，冻结计划，做步骤前核对，用嵌入脚本经标准输入调用一次新的 PowerShell，再读回来验证。模拟编辑在进程内算出新文档后，把整份文档、计划、事件和提交号经命名管道交给 Agent，写入 SQLite。IPC 版本 13，SQLite schema 19。

这条链在 `docs/Development.md` 里是当前契约，架构测试也在锁它。要变短，必须同时改代码、`docs/Development.md` 和 `tests/WinPool.Architecture.Tests`。

模块边界本身可以留下：Domain 和 Application 不引用界面、数据库和进程 API，这次纯层测试是通过的。不合理的是边界里面又拆了第二套、第三套同一件事，并且每次跨边界都传整份状态。

## 性能是怎么堆出来的

### 每次采集都是新进程

`WindowsPowerShellRunner`（`src/WinPool.Infrastructure.Windows/InventoryPipeline.cs`）每次 `RunInventoryAsync` 都启动

`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoProfile -NonInteractive`，脚本从标准输入喂入，超时 60 秒。

真实写入的 `WindowsPowerShellStorageWriteRunner`（`WindowsRealStorageCommandAdapter.cs` 约 364–408 行）同样每个写命令启动一个新进程。脚本是 gzip/base64 嵌入的 `WindowsRealStoragePowerShellScript`，载荷是第二行 JSON。进程启动之后这段调用不再接受取消。

`WindowsRealStorageTopologyReader.CaptureAsync`（`WindowsRealStorageTopology.cs` 942–949 行）不使用界面已经拿到的快照。它调用 `WindowsRealStorageFactSource`，默认再构造一个 `WindowsHardwareInventoryProvider`，于是再开一个 PowerShell。

### 一个简单步骤外面有多轮采集

`WindowsRealStorageBackend.CaptureOperationTopologyAsync`（1528–1540 行）在能定位范围时走 `real.capture.scoped`，否则走 `real.capture.full`。`ExecuteStepAsync` 在写之前采集一次，写返回之后进入 60 秒窗口、每 2 秒再采集，直到 `VerifyAfter` 成功或超时（90–91 行、367–426 行）。

Agent 侧把这些采集再包一层：

- `real.prepare`：`AgentRealOperationService` 调用 `PrepareAsync`
- `real.accept.preflight`：接受时再 `PreflightStepAsync`
- `real.step.preflight`：执行循环里再来一次
- `real.step.execute`：里面还有写前采集、写入、写后轮询

进程 10544 上那一次操作的五个采集点（一次 full、四次 scoped）分别是 4449、4664、5150、4882、4974ms。外层四段合计 27805ms。和“PowerShell 里六条查询 419ms”相比，时间花在重复启动进程和重复采集，不是花在单次 cmdlet。

App 还在操作被接受之后空转等待。`EditorPageBase` 447–449 行最多查询 1200 次，每次间隔 250ms，上限约 300 秒，然后 `RefreshRealInventoryAsync` 再采集一次。Agent 已经在 2 秒一轮地核对，App 再以 250ms 一轮地问“好了没有”。完成没有推送。

启动连接也是轮询。`App.xaml.cs` 的 `ConnectAgentAsync` 以 60 秒为期限，单次尝试 12 秒，Agent 处于 Starting 或 Recovering 时还会把期限往后再拨。

### 诊断日志里的中位数不能直接用

`%LOCALAPPDATA%\WinPool\Diagnostics\operation-timing.jsonl` 和轮转文件 `.1` 共 3718 条。`real.capture.full` 有 2764 条，中位只有 0.5ms。这些亚毫秒记录来自别的进程，包括今天 11:34 测试进程 26124 在基础设施测试期间写入的记录。它们是假的或立即返回的采集，写进了用户诊断日志。

只看 Agent 10544 时，存储采集全部不低于 500ms。硬件采集单次可以到 14216ms，并且 `inventory.collect` 与内部的 `inventory.provider.collect` 是嵌套的，13215ms 量级的 provider 时间已经包含在 collect 里。监控身份的第一次采集 5605ms。界面一次 `real.ui_scoped_refresh`（另一个进程）5209ms。

因此：原始文件的中位数描述的是测试污染，不是产品。产品路径上的一次采集是数秒，一次真实步骤是多轮采集相加。

### 模拟慢是因为把执行栈和整份文档又走了一遍

`SimulationEditCoordinator`（文件头注释写明它是 V0.13 文档模型接到 V0.2 计划器、策略、一次性授权和模拟执行器的过渡适配器）对一次模拟编辑构造 `OperationRequest`，跑 `DefaultOperationPlanner`、策略、授权和 `ExecutorGate`，再 `commitDocument`。

`WorkspaceViewModel.CommitSimulationDocumentAsync`（1070–1094 行）调用 `SaveEditAsync` 或 `SaveSimulationAsync`，然后 `RebuildTopology`、`RebuildObjects`、`BuildDetails`、`RebuildComparisonColumns`。

`StorageSystemRepository.SaveEditAsync`（415–443 行）把整份 `StorageSystemDocument` 编码后，连同期望哈希、计划、事件和提交号放进 `CommitAgentSimulationEditRequest`。结果未知时再发 `LookupAgentSimulationCommitRequest`。Agent 对模拟不做第二套业务规则，但这条提交路径仍然存在。本地草稿编辑不需要特权进程，也不需要把整份文档传来传去。

### 界面切换不慢

已运行的 Release App（进程 16840，`artifacts\Release\WinPool.App.exe`，11:21 启动）上，导航点击返回时目标页已经换好。点击耗时：

| 页面 | 点击返回 | 随后找到的控件 |
| --- | ---: | --- |
| 设置 | 523ms | 470ms 内读到关于版本 V0.60 |
| 监控 | 367ms | 连续监控开关为开，337ms |
| 磁盘分区 | 383ms | `VolumeLabelBox`，303ms |
| 存储结构 | 317ms | 池名 Pool01，326ms |
| 开发 | 353ms | 消息列表，289ms |
| 设置，第二次 | | 251ms |
| 存储结构，第二次 | | 253ms |
| 管理 | 344ms | 936ms 的检查里已有分类列表和“刷新本机信息” |
| 测试 | 313ms | 之后约 21 秒是整棵树检查，不是页面加载 |
| 硬件 | 246ms | 同上 |

“管理”页第一次用 `TopologySystemsControl` 这个名字等了 93477ms，找到 0 个控件。这个名字没有暴露给 UIA。93 秒是找错控件，不是页面加载。测试页和硬件页的约 21 秒同样是整树检查。

结构页在真实编辑关闭时，池名、虚拟磁盘名、卷名输入和新建、应用、撤销都是禁用的。`StorageStructurePage.UpdateButtonState` 要求 `CanEditDraft`、已选非 primordial 池、磁盘在线，并且已有真实虚拟磁盘不超过一个。这次没有强行改这些控件，因此没有做一次模拟提交的端到端计时，也没有做 Apply。

“刷新本机信息”用 InvokePattern 点下去，888ms 返回。随后 90 秒内原按钮选择器没有回到可用状态，诊断日志也没有出现 11:45 之后的新采集。不能把这 90 秒记成一次成功刷新。没有再点第二次。

真实编辑在整个 GUI 过程结束时仍是关闭。App 留在存储结构页。

ShellExecute 启动这个 Low 标签上的 `WinPool.App.exe` 会一直挂起并且不创建进程。`UseShellExecute=false` 的 CreateProcess 约 10ms 就能启动。这是本机强制完整性标签的现象，见下一节。

## 同一功能有多条实现

计划器现在有五处，必须互相一致：

- `RealStructureDraftPlanner`（App）
- `RealOperationProposalFactory`（App）
- `WindowsRealOperationPlanner`（Agent 侧，对新采集重新准备）
- `SimulationDraftPlanner`（Application）
- `DefaultOperationPlanner`（Execution），模拟提交时仍会跑到

读取现在有多条 IPC，定义在 `src/WinPool.Ipc/AgentControlMessageTypes.cs`：`GetSnapshot`、`CaptureInventory`、`CaptureManageInventory`、`LoadManageInventory`、`CaptureManageScopedInventory`，再加上真实拓扑读取器自己的 PowerShell。`AgentInventoryCoordinator.CaptureComparisonAsync`（265 行起）先跑 native，在 `IncludeLegacyComparison` 时再跑 legacy，两份都存，再做比较。

真实操作的 IPC 又拆成 prepare、accept、query、stop、两种范围查询、结构支持查询、可恢复操作列表。等待终态靠 query 轮询。

这些入口里，用户能感知的功能只有：读一组对象、改一份模拟草稿、对冻结后的命令做一次特权写入、看监控。其余是同一功能的第二实现或第二传输。

`docs/WinPool-Operation-Latency-20261008.md` 已经把重命名从约七轮采集降到仍约五轮范围采集、中位仍约 15.4 秒，而 Windows provider 调用约 1 秒。执行链优化没有做完。`docs/Quality.md` 写明“执行链优化尚未实施”。这次审查同意那个判断，并把它收成下面的方案。

架构测试把拆分和历史文案写成了必须继续存在的事实。例子：`EditorPagesOwnTheirSurfacesWithoutSectionTitles` 仍要求 XAML 里有子串 `StartOffsetValue`；`MainWindowPreviewsSavedWorkspaceBeforeAgentRestore` 不允许某段标记里出现 `<Border`，而当前标记里有。这种测试会在简化时整批失败。简化时改测试所锁的契约，不保留旧字符串来换通过。

## 本机完整性标签

`D:\Coding\Research03_WinPool` 带有显式的 `Mandatory Label\Low Mandatory Level:(OI)(CI)(NW)`。`Program` 和 `artifacts\Release\WinPool.App.exe` 继承 Low。在这个树里生成的 `testhost.exe` 也是 Low。vstest 父进程是 Medium。Low 的 testhost 在 `ProcessHelper.SetExitCallback` 里对父进程 `OpenProcess` 得到 Win32 拒绝访问（5），整套测试在任何用例之前退出。清掉 `DOTNET_DbgEnableMiniDump` 不能解决这个问题。

本次测试因此改到 Medium 目录输出。Temp 上的 `testhost.exe` 标签是 Medium，套件得以执行。直接输出到 Temp 时，架构测试和一部分持久化测试用 `AppContext.BaseDirectory` 向上找 `WinPool.slnx`，找不到仓库根，分别 47 败和 36 败。这两次结果作废。随后用仓库内指向 Temp 的联接目录重跑这两个项目，宿主仍是 Medium，且能找到仓库根。联接和 `Directory.Build.props` 上的临时输出覆盖都已撤掉，产品工程没有留下这个开关。

这个标签比 WinPool 宽，打在整个研究目录上。去掉它要动研究根，不在这次审查里做。产品 exe 带 Low 标签时，普通双击会以 Low 启动；当前 Agent 10544 是 High。管道安全描述符在 `CurrentUserPipeFactory` 里只给当前用户 FullControl，没有完整性 ACE。Windows 不允许低完整性客户端写入高完整性对象。这次 Low 的 App 仍然打开了主窗口并显示 Pool01，界面上没有看到连接失败。没有单独做管道写入探测，所以不把“已经连上”或“一定连不上”写成定论。

## 测试

Release，`dotnet` 10.0.401，12 个测试项目。下面是能找到仓库根、宿主为 Medium 的结果。架构和持久化是第二次运行，其余是第一次运行。

| 项目 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| WinPool.Agent.Client.Tests | 22 | 0 | 0 |
| WinPool.Agent.Tests | 139 | 0 | 0 |
| WinPool.App.Tests | 445 | 0 | 0 |
| WinPool.Application.Tests | 433 | 0 | 0 |
| WinPool.Architecture.Tests | 46 | 2 | 0 |
| WinPool.Domain.Tests | 23 | 0 | 0 |
| WinPool.Execution.Tests | 116 | 0 | 0 |
| WinPool.Infrastructure.Tests | 940 | 31 | 0 |
| WinPool.Inventory.Tests | 2 | 0 | 0 |
| WinPool.Ipc.Tests | 11 | 0 | 0 |
| WinPool.Monitoring.Tests | 53 | 0 | 0 |
| WinPool.Persistence.Tests | 176 | 13 | 3 |
| 合计 | 2406 | 46 | 3 |

合计 2455。Theory 会展开成多条，所以这不是源码里的 `[Fact]`/`[Theory]` 属性个数。

基础设施 31 败里，30 败是同一句 `NotSupportedException`：`The exact subsystem does not verify the requested capability/member count: SupportsStoragePoolCreation`。抛出处是 `WindowsRealOperationPlanner.MemberSets.cs` 的 `RequireExactMemberCapabilityAsync`（312 行）。失败用例是池创建恢复的 Theory，夹具没有给出这份能力证明，计划器就拒绝准备。这是计划器与测试夹具的契约已经分叉，也是“每一步都再证明一遍子系统能力”的成本。

另 1 败是 `WinPoolFactCaptureTests.StorageRefreshOmitsHardwareAndFullRefreshReturnsTypedProcessorFacts`：`TimeoutException: Read-only inventory exceeded 60 seconds`。这是测试里一次真实的只读全量采集脚本，不是六条 `Get-*`。它说明当前嵌入采集脚本在这台机器上可以超过 60 秒上限。它没有写磁盘。

架构 2 败是文案契约漂移，不是编译失败：

- `EditorPagesOwnTheirSurfacesWithoutSectionTitles` 要求磁盘页 XAML 含 `StartOffsetValue`，当前文件里没有这串。
- `MainWindowPreviewsSavedWorkspaceBeforeAgentRestore` 要求某段标记不含 `<Border`，当前标记含有。

持久化 13 败分成三组：

- 7-Zip 创建临时包退出码 2，错误是“拒绝访问”。随后多条用例期望归档状态 `SourceReleased`，实际仍是 `Sealed`。先把这组看成同一次归档没有完成，而不是先改状态机。7-Zip 若从带 Low 标签的路径启动，会写不了 Medium 的 Temp。这次没有单独取 7z 进程的完整性。
- `BuildOperationGuardTests` 两条断言期望 0，实际 1（`StoppedUnknownResultAllowsRepairWithoutChangingDatabaseOrWriteBarrier`、`MissingCorruptAndAmbiguousRootsRefuseButPristineRootPasses`）。
- 轮转监控：连续生产者没有跨过三份已密封库；轮转后的样本集合为空；两条例约在 30 秒超时（轮转没有发生，账本重试条件没有出现）。

Domain、Application、App、Agent、Execution、Ipc、Inventory、Monitoring 全绿。全绿不表示执行链快。这些项目里的真实存储后端大多用假 provider，假采集可以在 1ms 内返回，所以它们不衡量本文说的进程启动成本。

## 修改方案

幅度按能删则删。AB 够用，就不做 ABCD。不保留第二实现当备用。

### A. App 只拥有草稿和模拟

模拟编辑变成内存里的文档差分。

删掉这条链：`SimulationEditCoordinator` 里的 `DefaultOperationPlanner`、策略评估、`InMemoryOperationAuthority`、`ExecutorGate`，以及每次编辑的 `CommitAgentSimulationEditRequest`（整份文档 + 计划 + 事件）。

留下：

1. 一个草稿计划器产生差分。结构页和分区页都叫它，不各写一套。
2. 差分直接改 App 持有的 `StorageSystemDocument`。
3. 界面从这份文档刷新被改过的对象。
4. 防抖之后只把文档保存到 Agent 或本地库。保存失败就留在内存并标明未落盘。不因为保存失败再跑一套执行器。

现有对象的改名、新建草稿池、撤销，都走这一条。真实编辑关闭时，这些操作不接触 Agent 的写入脚本。

### B. Agent 只做特权读写

Agent 进程里放一个长期存活的 PowerShell 5.1 runspace，模块只导入一次。采集和写入都进这个 runspace。禁止再为每次调用启动 `powershell.exe`。

采集只留一个实现：按范围读取。整机读取就是范围等于这台机器，不是第二份脚本、第二套 provider、第二轮 legacy 对比。`CaptureComparisonAsync` 的 legacy 分支从正常路径删除。IPC 的五个读取名字收成一个 `Read(scope)`。`GetSnapshot` 若只是读缓存，可以留下，但它不再触发采集。

一个真实步骤固定为三拍：

1. 读受影响对象，核对身份和角色。对不上就停止，不写。
2. 执行已经冻结的那一条命令。
3. 再读一次。对得上就成功。对不上就 `OutcomeUnknown`，不重发写入。

删掉：

- `PostCallWindow` 60 秒和 `PostCallPoll` 2 秒的循环。provider 已经返回时，一次读回就是核对。
- prepare、accept、step 三处各自的全量或范围采集。步骤开始的那一次读取同时充当准备和前核对。
- `EditorPageBase` 的 1200 次、250ms 查询。Agent 在终态时推送。App 只在推送丢失时查一次。
- `WindowsRealOperationPlanner` 作为第二计划器的职责。它不再根据新采集重新编排命令。冻结命令由 App 的那一个计划器产生。Agent 只检查这条命令的目标、身份、角色和写后状态。
- `RequireExactMemberCapabilityAsync` 这种在每个成员上重复查询子系统能力的证明，收成步骤开始那一次读取里的字段。测试夹具提供同一次读取的结果，不再要求另一套能力对象。

`WindowsRealStorageTopologyReader` 使用同一次读取的结果，不再自己新开 provider。

### 功能只留一个入口

| 功能 | 留下的入口 | 删掉的并行路径 |
| --- | --- | --- |
| 读存储对象 | Agent 的一个 runspace，`Read(scope)` | 五个 IPC 采集、legacy 对比、拓扑读取器自带的 PowerShell |
| 编辑模拟草稿 | App 内存差分 + 防抖保存文档 | 执行栈、整文提交、提交查找 |
| 真实写入 | 冻结命令的读、写、读回 | 第二计划器、60 秒轮询、App 的 250ms 轮询 |
| 能力与范围 | 同一次 `Read(scope)` 的字段 | 单独的范围查询和结构支持查询各开一轮采集 |
| 操作结束 | Agent 推送终态 | `QueryRealOperation` 作为主路径 |
| 监控采样 | 现有监控会话 | 不把监控身份采集做成第二次全量库存 |

监控、设置、导航保持现状。它们的页面切换已经在 1 秒内。

### 文档和测试要一起改

`docs/Development.md` 里“每步重新采集、App 轮询、模拟走执行栈”改成上面的 A/B。历史测量段落保持原样。

`docs/Quality.md` 的“执行链优化尚未实施”在改造落地后改成指向实施记录。2026-10-16 的 30.94 秒和 247.69 秒不改写。

架构测试改锁新契约：一个写入进程、一个读取实现、一个计划器、模拟不发送整份执行计划。删除对旧控件名和 `<Border` 这种标记细节的锁定，除非那个细节仍是用户可见的稳定行为。

测试不得再写入 `%LOCALAPPDATA%\WinPool\Diagnostics\operation-timing.jsonl`。诊断日志只留给真实 App 和 Agent。否则中位数继续被亚毫秒假采集淹没。

在研究根的 Low 标签去掉之前，测试输出放到 Medium 目录，并且该目录的路径要能向上找到 `WinPool.slnx`，否则架构测试和持久化测试会因为找不到仓库根而失败。不要把输出开关留在 `Directory.Build.props`。

### 预期

一次只读采集应靠近这次的基线：进程已在时不到 1 秒的查询，冷进程约 2.4 秒。嵌入脚本若仍比六条 `Get-*` 重，先删掉脚本里重复的对象遍历，而不是再包一层进程。全量脚本这次有一条超过 60 秒的测试，改造时用同一次 runspace 对它计时，超过数秒的查询要删到范围读取。

一次真实步骤的墙钟应是一次读、一次 Windows 写、一次读回。按今天的采集中位，三拍大约是十秒量级，加上写命令本身。它不应该再是五次采集加两层轮询，也不应该再出现 247 秒这种由几十轮采集相加得到的结果。

一次模拟编辑应在 App 进程内完成，不经过命名管道上的整份文档。页面切换保持现在的亚秒，不把库存刷新绑到每次导航上。

这些数字是目标量级，不是改造前的承诺。落地后用同一种计时日志重测一条只读刷新、一条模拟改名、一条已授权磁盘上的单步真实操作。

## 安全上留下的部分

- 写之前有一次新鲜的身份和角色核对。对不上就不写。
- 写之后只读一次。读不到确定结果就是 `OutcomeUnknown`，不重发。
- 真实写入仍只针对已授权的测试盘。三星盘保持保护。这次审查没有使用那份授权去写盘。
- 管道继续只允许当前用户。完整性标签作为本机环境问题单独处理，不在业务代码里再加一套降级写入。

不留的部分：第二计划器、第二采集实现、legacy 对比、写后的 60 秒重试环、App 侧 300 秒轮询、模拟路径上的授权执行器、测试写入用户诊断日志。

## 这次没有验证的部分

- 没有做成功的只读刷新计时。按钮点击返回了，但 90 秒内没有新的采集日志。
- 没有做模拟改名或新建草稿的往返。结构页控件在真实编辑关闭时是禁用的。
- 没有打开真实编辑，没有 Apply，没有写任何磁盘。
- 没有在去掉 Low 标签的前提下，从仓库目录直接 `dotnet test`。那样做今天会在 testhost 启动时崩溃。
- 没有证明 Low App 与 High Agent 的管道在这次会话里一定可写。
- 持久化失败里的 7-Zip 拒绝访问没有单独取进程完整性后重跑。
- 工作区里用户已经删除的四份 Review 文档没有恢复。

## 建议的落地顺序

1. 先做 Agent 内的长期 runspace，并把一次采集收成 `Read(scope)`。用现有只读刷新比较墙钟。这一步不改写入语义。
2. 真实步骤改成读、写、读回，终态改为推送。用一条已授权的单步操作比较墙钟。
3. 模拟改成内存差分和防抖保存。用一次改名比较墙钟。
4. 删掉空出来的 IPC、legacy 采集和第二计划器，并改 `docs/Development.md` 与架构测试。
5. 由目录所有者去掉研究根上的 Low 强制标签后，再在仓库目录里跑完整测试。在那之前不要把测试失败里的“找不到仓库根”和 7-Zip 拒绝访问当成产品逻辑回归。
