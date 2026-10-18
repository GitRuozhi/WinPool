# 接手审查验收

日期：2026-10-18。

对照文件：`docs/Review/WinPool-Takeover-Review-20261017.md` 的「修改方案」「预期」「安全上留下的部分」。

观察对象：当前工作树 V0.61（`Directory.Build.props` 中 Major 0、Minor 6、Iteration 1）。本文件不按已归档的 V0.61 计划打分。该计划写明本版不建常驻 PowerShell，并保留非 MAX 单写的三次采集。那些决定不改变下面的判定。

总判定：**没满足**。

性能根因还在。采集和真实写入仍是每次新起 `powershell.exe`。一次普通单写仍是多次采集，写后仍有 60 秒、2 秒的核对窗口。模拟编辑仍每次经命名管道提交到 Agent。

## 判定

| # | 审查要求 | 判定 | 依据 |
| --- | --- | --- | --- |
| 1 | 删掉 `SimulationEditCoordinator` 上的 `DefaultOperationPlanner`、策略评估、`InMemoryOperationAuthority`、`ExecutorGate` | 满足 | `SimulationEditCoordinator.cs` 只冻结 `SimulationEditIntent`，再调用 `simulationEditor.Apply`。`src` 中没有 `new DefaultOperationPlanner`、`new ExecutorGate`。 |
| 2 | 每次编辑不再发送整份文档、执行计划和事件 | 满足 | `CommitAgentSimulationEditRequest` 只有 `SimulationCommitIntent` 和 `CorrelationId`。回执是 revision、hash 和 commitId。 |
| 3 | 结构页和分区页共用一个草稿计划器 | 没满足 | 生产路径同时使用 `SimulationDraftPlanner` 和 `RealStructureDraftPlanner`。`WindowsRealOperationPlanner` 仍由 `Program.cs` 构造。 |
| 4 | 模拟差分留在 App 内存，防抖后才保存文档；保存失败留在内存，不再跑执行器 | 没满足 | `WorkspaceViewModel.cs` 每次编辑调用 `SaveEditAsync`。该方法发送 `CommitAgentSimulationEditRequest`。`src\WinPool.App` 没有防抖。这条路径不调用真实存储写入脚本，但审查要求的是内存差分和防抖保存。 |
| 5 | Agent 内一个长期 PowerShell 5.1 runspace，模块只导入一次 | 没满足 | `src` 没有 `Runspace` 或 `PowerShell.Create`。 |
| 6 | 禁止每次调用再启动 `powershell.exe` | 没满足 | `InventoryPipeline.cs` 的 `WindowsPowerShellRunner.RunCommandAsync` 每次 `new Process`，参数为 `-NoLogo -NoProfile -NonInteractive`。`WindowsRealStorageCommandAdapter.cs` 的 `WindowsPowerShellStorageWriteRunner` 同样每次新起进程。 |
| 7 | 删除正常路径上的 legacy 对比采集 | 满足 | `src` 没有 `CaptureComparisonAsync`。`Development.md` 写明旧比较采集链和空的 `WinPool.Inventory` 已退出。 |
| 8 | 五个 IPC 读取收成一个 `Read(scope)` | 没满足 | `AgentControlMessageTypes.cs` 仍有 `GetSnapshot`、`CaptureManageInventory`、`LoadInventoryCache`、`CaptureManageScopedInventory`、`QueryRealPartitionResizeRange`、`QueryRealVirtualDiskCreationRange`、`QueryRealStructureCreationSupport`。 |
| 9 | `GetSnapshot` 可以留下，但不得再触发采集 | 满足 | `DesktopAgentRuntime.GetSnapshotAsync` 只组装内存中的监控、进程和健康事件。 |
| 10 | 一个真实步骤固定为：读受影响对象、执行一条已冻结命令、再读一次 | 没满足 | `artifacts/test-results/v061/real-single-timing.json` 中操作 `40757c3c-289f-45d6-a15e-8f35bc16075c` 有 3 次 `inventory.provider.collect`，耗时 3414 ms、3277 ms、3409 ms。`Quality.md` 记录外层 prepare、preflight、execute 合计约 12.54 秒。 |
| 11 | 删除 `PostCallWindow` 60 秒和 `PostCallPoll` 2 秒 | 没满足 | `WindowsRealStorageBackend.cs` 第 93–94 行仍是这两个值。`MemberSets.cs` 和 `MaximumAttempt.cs` 使用同一窗口。 |
| 12 | 删除 prepare、accept、step 各自的全量或范围采集；步骤开始的一次读取同时充当准备和前核对 | 没满足 | 同上，单写仍有 3 次 provider 采集。`Development.md` 把非 MAX、一次 Windows 写、首次后态成功的路径写成三次采集。 |
| 13 | 删除 `EditorPageBase` 的 1200 次、250 ms 查询；终态由 Agent 推送 | 满足 | `EditorPageBase.cs` 改为 `statusCache.WatchAsync`。源码中已无 1200 次、250 ms 的状态轮询。 |
| 14 | App 只在推送丢失时查询一次 | 没满足 | `RealOperationStatusCache.WatchAsync` 在等待事件前先 `QueryAsync`。`AgentStateReseedEvent` 且原因不是 `agent.events.latest_snapshot` 时再查一次。 |
| 15 | `WindowsRealOperationPlanner` 不再根据新采集重新编排命令 | 没满足 | `Program.cs` 仍把它交给 `WindowsRealStorageBackend`。范围和支持查询仍先 `topologyReader.CaptureAsync`，再在计划器里计算。 |
| 16 | `RequireExactMemberCapabilityAsync` 收成步骤开始那一次读取里的字段 | 没满足 | `WindowsRealOperationPlanner.MemberSets.cs` 仍定义该方法，池、层、虚拟磁盘的创建和删除路径仍调用它。 |
| 17 | `WindowsRealStorageTopologyReader` 使用同一次读取，不再自己新开 provider | 没满足 | 生产 `Program.cs` 把 `AgentInventoryCollector` 传进读取器。类的默认构造仍是 `new WindowsRealStorageFactSource()`，而 `WindowsRealStorageFactSource` 默认 `new WindowsHardwareInventoryProvider`。 |
| 18 | 能力与范围取自同一次 `Read(scope)`，单独的范围查询和结构支持查询不再各开一轮采集 | 没满足 | `ReadVirtualDiskCreationRangeAsync`、`ReadPartitionResizeRangeAsync`、`ReadStructureCreationSupportAsync` 各自调用 `CaptureAsync`。结构支持在检查前后各采集一次。 |
| 19 | 监控身份采集不做成第二次全量库存 | 满足 | `AgentInventoryCollector.CollectForMonitorAsync` 在 generation、绑定和 coverage 相符时返回已验证后态。不符时记录 `monitor.identity.coverage_miss` 后再采集。 |
| 20 | `Development.md` 改成审查方案的 App／Agent 分工 | 没满足 | 当前文本写的是三次采集预算、Agent 用 fresh 事实重算，以及每次模拟提交 intent。没有长期 runspace，也没有防抖的内存保存。 |
| 21 | 审查方案落地后，`Quality.md` 的「执行链优化尚未实施」改为指向该方案的实施记录 | 没满足 | 审查方案没有落地。这句话已不在当前 `Quality.md`，当前页也没有指向本审查方案的实施记录。 |
| 22 | 2026-10-16 的 30.94 秒和 247.69 秒不改写 | 满足 | `docs/Reference/Pool-VD-Timing-20261016.md` 仍是删除虚拟磁盘 30.94 秒，新建 MAX 虚拟磁盘加自动布局 247.69 秒。 |
| 23 | 架构测试改锁一个写入进程、一个读取实现、一个计划器，以及模拟不发送整份执行计划 | 没满足 | `ArchitectureBoundaryTests.cs` 没有这些断言。 |
| 24 | 删除对旧控件名和 `<Border` 标记的锁定 | 满足 | `ArchitectureBoundaryTests.cs` 已没有 `StartOffsetValue`，也没有禁止 `<Border` 的断言。 |
| 25 | 测试不得再写入用户目录的 `operation-timing.jsonl` | 满足 | 审查当时写入该日志的是 `WinPool.Agent.Tests` 和 `WinPool.Infrastructure.Tests`。两处 `TestDiagnostics` 都把 `StorageOperationTiming.Sink` 设为 `Trace.WriteLine`。 |
| 26 | 不把测试输出开关留在 `Directory.Build.props` | 满足 | 该文件没有 `WINPOOL_AUDIT`。 |
| 27 | 去掉研究根上的 Low 强制完整性标签 | 满足 | 2026-10-18 对 `D:\Coding\Research03_WinPool` 的 `icacls` 结果是 `Mandatory Label\Medium Mandatory Level:(OI)(CI)(NW)`。 |
| 28 | 标签去掉后，在仓库目录跑完整测试 | 没满足 | `Quality.md` 写明受影响项目 706 passed、累计 2500 passed 是沿用未受影响证据，整套没有重跑。本次验收没有从仓库目录重跑整套。 |
| 29 | 只读采集靠近基线：进程已在时不到 1 秒，冷进程约 2.4 秒 | 没满足 | `artifacts/test-results/v061-r1-r3-fixes-20261018-115854/candidate-refresh-timing.json` 里进程 1260 有 3 次 `inventory.provider.collect`：2729 ms、2612 ms、2769 ms。 |
| 30 | 一次模拟编辑在 App 进程内完成 | 没满足 | 编辑仍经 `SaveEditAsync` 进入 Agent。整份执行计划已经不再发送，这一条见第 2 项。 |
| 31 | 写之前保留一次新鲜的身份和角色核对，对不上就不写 | 满足 | 真实计划器仍在写入前读取拓扑并核对接目标、会话和成员。 |
| 32 | 写之后只读一次 | 没满足 | 第 11 项的 60 秒窗口还在。provider 返回后仍按 2 秒间隔继续核对。 |
| 33 | 读不到确定结果就是 `OutcomeUnknown`，不重发写入 | 满足 | 启动时把中断的 Accepted／Running 标为 `OutcomeUnknown`。`QueryAsync` 只返回轻量状态。显式 `RecheckAsync` 和启动恢复才做一次只读重查。写进程启动后，调用方取消不杀该进程。 |
| 34 | 真实写入仍只针对已授权测试盘，三星盘保持保护 | 满足 | `AGENTS.md` 和 `Product.md` 仍禁止两块 Samsung 成为写目标。`Quality.md` 记录 V0.61 末态 139 项通过，Samsung 与既有基线一致。 |
| 35 | 管道继续只允许当前用户；不在业务代码里再加一套按完整性标签降级的写入 | 满足 | `CurrentUserPipeFactory` 只给当前用户 SID `FullControl`。 |

上表 35 条：15 条满足，20 条没满足。

## 计时所证明的事

审查要求落地后重测一条只读刷新、一条模拟改名、一条已授权磁盘上的单步真实操作。

只读刷新和单步真实操作有记录，两条都没达到审查的形状：

- 刷新文件里同一次手动 Storage 刷新对应 3 次 provider 进程采集，单次约 2.6–2.8 秒。
- 单写 `40757c3c-289f-45d6-a15e-8f35bc16075c` 是 3 次 provider 采集，外层约 12.54 秒。这短于审查所引的 2026-10-16 记录，但仍是多次新进程采集，不是一次读、一次写、一次读回。

模拟改名没有一条「只在 App 进程内完成」的计时。当前代码每次编辑都把 intent 提交到 Agent。

2026-10-16 的 30.94 秒和 247.69 秒保持原记录，不拿这次 12.54 秒改写它们。

## 本次验收没有做的事

- 没有改产品代码，没有提交。
- 没有启动 App，没有打开真实编辑，没有写磁盘。
- 没有重测页面切换。审查当时的页面切换不在本次判定里另记为满足或没满足；本次没有新的耗时。
- 没有从仓库目录重跑整套 `dotnet test`。
