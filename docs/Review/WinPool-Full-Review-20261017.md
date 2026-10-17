# WinPool 全量检视报告：性能、冗余与架构

日期：2026-10-17
基线：`main` / `74a40af`（工作树干净，仅未跟踪 `Ref/`）
检视方式：源码静态分析 + 本机实测（现有运行期遥测、隔离基准、真实 GUI 端到端）+ 全量构建与测试
检视范围：`Program/WinPool` 全部 `src`、`tests`、`docs`、构建脚本

---

## 0. 结论摘要

本报告确认用户提出的两个问题，并给出可度量的根因与修改方案。

**问题一（性能）不是"某个操作慢"，而是同一个结构性缺陷被每一步重复放大。**

> 每次"读一次存储拓扑"都新起一个 `powershell.exe` 进程，而**冷进程的第一次存储查询本身就是最贵的一步**。
> 本机实测：同一组完整存储查询，5 个新进程共 **11 068 ms**；同一个常驻进程连做 5 次共 **872 ms**。
> **12.7 倍差距，全部来自"每次读都重开进程"。**

在这个单价下，WinPool 的每一次用户操作要串行执行 **5～64 次**这样的读取：

| 场景 | 拓扑读取次数 | PowerShell 进程数 | 实测墙钟 |
| --- | ---: | ---: | ---: |
| 单步操作（改名类，无恢复轮询） | 5 | 6 | **30.1 s** |
| 单步操作（改名类，含恢复轮询重放） | 19 | 19 | **113.1 s** |
| 三步操作（删 VD→删层→删池） | 15 | 18 | **113.1 s** |
| 分层 MAX 虚拟磁盘 | 64 | 64 | **244.1 s** |

（前两行是同一个单步操作在"是否进入恢复轮询"两种状态下的实测值，两者相差 3.8 倍，说明 P3 的失控循环会把单步操作直接推到三位数秒。两者都取自现网遥测，见 §2/P3。）

而其中真正调用 Windows 完成变更的那一次子进程，实测中位数只有 **1.1～2.0 秒**；`pwsh` 里一条 `Set-StoragePool -NewFriendlyName` 是 1～2 秒级。差距的 90% 以上是 WinPool 自己的重复枚举与进程开销。

**问题二（冗余）同样成立，并且已经产生了可观测的损害。**

- 同一份能力普遍存在 2～4 条并行实现路径：读本机清单 3 套、拓扑采集 4 条、监控记录服务 3 个、PowerShell 启动器 2 套。
- `src` 中 **80 个接口**：52 个在 `src` 中只有 1 个实现类，**17 个在 `src` + `tests` 全仓库没有任何实现类**。
- 一个真实存在的**无限重试缺陷**，在 10 分钟内产生了 **1 947 次拓扑采集、210 个新 OperationId**，持续写入诊断日志且不会自行停止（本报告 §2/P3 已现场复现并留证）。
- 测试套件**当前不通过**：2 455 项中 **34 项失败**（32 项确定性 + 2 项偶发）。确定性红灯已滞留 1～3 天，而对应提交的 `docs/Quality.md` 均写着"零警告/零错误、未跑全量测试"。
- 文档总量：现行文档 **285 765 字符**，`docs/Archive` 另有 **1 387 780 字符**（196 个 md、53 个归档目录）；`CHANGELOG` 75 条、`Quality.md` 31 节约 3/4 是逐日流水。这些文字没有阻止上述问题，反而掩盖了它——`docs/WinPool-Operation-Latency-20261008.md` 已经测出 15.437 秒并把它写成"改善 16.43%"。

**一句话：WinPool 用一个每步只读一次都会失败的价格，做了 5～64 次同样的只读。**

---

## 1. 证据基础

### 1.1 数据来源

| 来源 | 内容 | 位置 |
| --- | --- | --- |
| 现网运行期遥测 | 2 333 → 5 127 条分阶段计时记录（2026-10-13 → 10-16） | `%LOCALAPPDATA%\WinPool\Diagnostics\operation-timing.jsonl(.1)` |
| 隔离基准 | 冷进程 vs 常驻进程、单 cmdlet 计时、内嵌脚本分阶段插桩 | 本报告 §1.2、§2/P1 |
| 构建 | Release 全解决方案，`dotnet build -m:1`，72.62 s / 0 warning / 0 error | `artifacts/test-results/review-20261017/build-release.log`、`build-project-timings.json` |
| 测试 | 12 个测试项目逐程序集 TRX，2 455 项 / 269.86 s | `artifacts/test-results/review-20261017/trx-per-project/`、`test-durations.csv` |
| GUI 端到端 | 真实 App + Agent 管理员会话，实测确认/复现 | `artifacts/test-results/review-20261017/operation-timing-runaway.jsonl*` |

### 1.2 实测的拓扑采集单价

对 `src/WinPool.Infrastructure.Windows/EmbeddedStorageInventoryScript.cs` 内嵌脚本（GZip+Base64，解码后 35 128 字符）做分阶段插桩，本机（3 物理盘 / 2 池 / 1 VD / 2 层 / 8 分区）结果：

| 阶段 | 耗时 |
| --- | ---: |
| 进程启动 + 脚本装载 | ~750 ms |
| `Get-CimInstance Win32_OperatingSystem/ComputerSystem` | 9 ms |
| `Get-StorageSubSystem` | 292 ms |
| **`Get-PhysicalDisk`（冷 provider 缓存，首次调用）** | **877 ms** |
| `Get-StoragePool` | 79 ms |
| `Get-Disk` | 32 ms |
| `Get-Partition` | 32 ms |
| `Get-Volume` | 35 ms |
| `Win32_LogicalDisk` | 45 ms |
| `Get-VirtualDisk` / `Get-StorageTier` | 30 / 32 ms |
| 池成员循环（每池一次 `Get-PhysicalDisk -StoragePool`） | 166 ms |
| `disk \| Get-PhysicalDisk` 循环 | 77 ms |
| 其余对象/关系投影与 JSON 序列化（输出 165 KB） | ~1 200 ms |
| **脚本内部合计** | **3 245 ms** |

同一进程内第二次 `Get-PhysicalDisk` 只要 **22 ms**。也就是说：**每次新进程都要重新支付一次 0.9～1.2 秒的 provider 冷启动，而 WinPool 每一步操作要付 5～64 次。**

### 1.3 现网单价（与上表独立互证）

| phase | 次数 | 合计 | p50 | p90 | max |
| --- | ---: | ---: | ---: | ---: | ---: |
| `inventory.provider.collect` | 734 | 2 422.7 s | 3 098 ms | 4 121 ms | 16 898 ms |
| `real.capture.scoped` | 376 | 1 053.5 s | 2 668 ms | 3 757 ms | 4 689 ms |
| `real.capture.full` | 68 | 224.2 s | 3 264 ms | 3 830 ms | 4 140 ms |
| `real.prepare` | 70 | 255.9 s | 3 631 ms | 4 582 ms | 5 935 ms |
| `real.accept.preflight` | 66 | 223.8 s | 3 351 ms | 4 031 ms | 4 403 ms |
| `real.step.preflight` | 80 | 240.1 s | 2 980 ms | 3 983 ms | 4 509 ms |
| `real.step.execute` | 79 | 763.4 s | 9 219 ms | 13 062 ms | 24 532 ms |
| `real.provider`（**真正的 Windows 变更调用**） | 14 | 33.5 s | **2 035 ms** | 4 826 ms | 4 826 ms |
| `real.ui_scoped_refresh` | 79 | 233.7 s | 2 952 ms | 3 959 ms | 5 273 ms |
| `inventory.persist` / `merge` / `project` | 142 | 7.7 / 1.3 / 0.3 s | 37 / 3 / 0 ms | — | — |

**采集类阶段合计 4 992 秒，全部其他阶段合计 1 218 秒。时间预算的 80.4% 花在重复枚举同一份存储拓扑上。**

（口径：`inventory.*` 与 `real.capture.*`、`monitor.identity.collect` 记为采集类；`real.prepare` / `real.accept.preflight` / `real.step.preflight` / `real.step.execute` / `real.provider` / `real.ui_scoped_refresh` 记为其他。各阶段存在嵌套包含关系，**不求和为总墙钟**，此处只用于比较两类工作的量级比例。该 5 127 条样本取自已轮转归档的 `operation-timing.jsonl.1`，不含下文 §2/P3 的失控循环数据。）

### 1.4 逐步采集计数（源码推导，逐条核对）

`real.accept` 之后，一个 N 步操作的真实发生次数：

| # | 触发点 | 类型 | 位置 |
| --- | --- | --- | --- |
| 1 | 计划准备 | full | `WindowsRealOperationPlanner.cs:76` |
| 2 | 接受前预检（重复准备期校验） | full（scope 未注册） | `AgentRealOperationService.cs:485` → `WindowsRealStorageBackend.cs:142` |
| 3~5 | 每步：步前预检 / 调用前 / 调用后验证 | scoped ×3 | `…Backend.cs:142`、`:263`、`:372` |
| 6 | 每步：监控重绑定 | scoped | `MonitoringSessionEdits.cs:150` |
| 7 | 计划收尾对账 | scoped | `WindowsRealStorageBackend.cs:495` |
| 8 | App 侧结果刷新 | scoped | `EditorPageBase.cs:367,478` → `WorkspaceRealOperations.cs:79` |
| 9 | 写进程本身 | 写 | `WindowsRealStorageCommandAdapter.cs:55` |

**1 步 = 5 次读取 + 1 次写 = 6 个 PowerShell 进程；3 步 = 15 次读取 + 3 次写 = 18 个进程。** 与现网实测计数完全一致。

---

## 2. 性能根因（按贡献排序）

### P1 — 每次读取都新起进程，冷 provider 缓存被反复支付（贡献 ≈ 75%）

全仓库 `Runspace` / `System.Management.Automation` / `PowerShell.Create` **零匹配**：没有任何常驻宿主或 Runspace 复用。

- 读：`InventoryPipeline.cs:32-61` 每次 `new Process{...}` + `Start()`
- 写：`WindowsRealStorageCommandAdapter.cs:382-408` 同样每次新建，且脚本内 `WindowsRealStoragePowerShellScript.cs:353` 每次都执行 `Import-Module Storage`
- 脚本经 stdin 全文传输（读 47～58 KB 未压缩；写 53 KB 脚本先 gzip+base64 再入 stdin）
- stdout 全缓冲：`ReadToEndAsync`，进程退出后才能解析

**隔离实测（本机，同一组完整查询）：**

| 模型 | 5 次完整查询总耗时 | 单次 |
| --- | ---: | ---: |
| A：5 个新进程各做 1 次（现行 WinPool） | **11 068 ms** | 2 214 ms |
| B：1 个常驻进程连做 5 次 | **872 ms**（含一次进程启动） | ~25 ms |

### P2 — 同一步内把同一份拓扑读了 3～7 次，且大多数没有带来新信息（贡献 ≈ 15%）

最明显的是第 2 项与第 3 项紧邻：

```
AgentRealOperationService.cs:1210  real.step.preflight   → 1 次 scoped 采集 + 安全巡检 + 指纹核对
AgentRealOperationService.cs:1248  TransitionStepAsync   → 仅 SQLite 状态迁移
WindowsRealStorageBackend.cs:263   real.step.execute 内  → 又一次 scoped 采集 + 同一指纹核对
```

两者之间只隔一次数据库写入，第二次采集在数学上不可能观察到新事实（`WindowsRealStorageTopology.cs:41` 允许 2 分钟新鲜度）。代价：**每步 2.2～2.7 秒**。

同类问题：
- 接受前预检（#2）与准备期 `ValidateCurrentStepAsync` 用同一套输入重复执行，中间只隔一次用户确认——**约 3.3 秒**。
- 能力/范围查询各做前后两次 full 采集（`WindowsRealOperationPlanner.cs:152/344`、`:202/253`），第二次只为证明"读取期间未变化"——每次操作 **1～2 次 full 采集**。
- 计划收尾对账（#7）在每一步都已验证通过的情况下仍全量重采。
- App 侧 UI 刷新（#8）与监控重绑定（#6）在同一操作末尾各采一次，内容高度重叠——**约 2.0～3.0 秒**。

### P3 — 对账重放无节流、无上限：10 分钟 1 947 次采集（贡献 ≈ 8%，但会失控）

这是本次检视中**唯一会自行无限放大**的缺陷，已现场复现。

`AgentRealOperationService.cs:849-870`：

```csharp
if (reconciliationTasks.ContainsKey(operationId)) return;
var task = Task.Run(() => ReconcilePersistedAsync(operation.Plan));
reconciliationTasks[operationId] = task;
_ = task.ContinueWith(completedTask => {
    lock (reconciliationTasks) reconciliationTasks.TryRemove(operationId, out _);   // :867
}, ...);
```

去重标记在任务**完成后立即移除**，而 `ReconcilePersistedAsync`（`:872-922`）在 `CanReleaseWriteBarrier == false` 时**只记诊断然后返回**（`:884-888`），`OutcomeUnknown` 状态保持。于是：

```
ListRecoverableAsync (:273-277)  对每个 OutcomeUnknown 操作调 ScheduleReconciliation
QueryAsync        (:586)         同样调用
App 恢复轮询 (WorkspaceRealOperations.cs:258-265)  while(IsBlocked) 1s→2s→…→5s，无总时限
  → 每 1～5 秒发起 2 个请求 → 每轮至少 1 次对账 → 每次对账 1 次 scoped 采集
```

**现场实测（2026-10-16 23:16–23:28）：**

| 指标 | 实测值 |
| --- | ---: |
| 窗口内 `real.capture.full` 次数 | 1 947（占全部采集 98.7%） |
| 全为 0～2 ms 的即时失败 | 1 947（100%） |
| 窗口内不同 OperationId 数 | 210（10 分钟） |
| 日志增长速度 | 约 157 KB / 分钟，永不停止 |

所有失败采集的 scope 标记均为 `source-scoped-unavailable`，即 `WindowsRealStorageBackend.cs:1538-1540` 的 full 回退分支。这些采集在 `CaptureAsync` 内立即抛出（早于 PowerShell 进程创建），因此循环本身不慢，但它**持续占用控制通道与 SQLite 写锁、持续写盘、并让写屏障永远无法解除**。用户感知就是"点了没反应，等几百秒"。

### P4 — 固定等待与轮询叠加（贡献 ≈ 2%）

| 位置 | 值 | 说明 |
| --- | --- | --- |
| `WindowsRealStorageBackend.cs:90-91` | 60 s 窗口 / 2 s 轮询 | **每轮循环体里是一次 scoped 采集（2.2～3.8 s），不是 2 s**；最坏 30 轮 ≈ 66～115 s |
| `EditorPageBase.cs:447-449` | 250 ms × ≤1200 = 300 s | Accept 后的状态轮询；每次一个控制往返 + Agent 侧 2 次 SQLite 读 |
| `WorkspaceRealOperations.cs:258-265` | 1 s→5 s，无上限 | 见 P3 |
| `WindowsRealStorageBackend.cs:842/914/1288` | 固定 `Task.Delay(2 s)` | 作为"两次一致观察"的间隔，每次 +2 s +1 次采集 |
| `WindowsRealStorageBackend.MaximumCapacity.cs:218/263` | 额外 2 s | MAX 每候选容量 |
| `IpcProtocol.cs:17` | 25 s 空闲后强制重连 | 重连 = 读 endpoint + 进程核验 + 控制握手 + 事件握手 + **一份全量快照**（4 个往返） |

### P5 — 后台稳态噪声与用户操作争抢同一条串行通道（贡献 ≈ 2%，但影响体感）

- 控制通道是**单连接 + 严格串行**：`AgentControlServer.cs:322-354`（一次只服务一个连接）、`:528-603`（一次只处理一个请求）；客户端另有三道闩 `NamedPipeAgentConnection.cs:113-116`。
- 但快照轮询是 **≥6 次/秒**：`MonitoringService.cs:691-716`（默认 5 Hz）+ `MonitorAlertObserver.cs:38`（1 Hz，`MainWindow.xaml.cs:429` 无条件启动）。每份快照携带**最多 1 200 个监控样本**（`MonitoringSessionCoordinator.cs:104`）。
- 每个请求还要付：2 次进程完整性读取（`AgentControlServer.cs:549-561`）+ **1 次 SQLite 写**（`:563-573`）；而 SQLite **禁用了连接池**（`WinPoolSqliteStore.cs:49` `Pooling=false`），每次打开重跑 4 条 PRAGMA（`:148-163`）。
- 结论：用户点击必须先排在这条被 ≥6 次/秒 占满的管道后面。

### P6 — 数据在 App 与 Agent 之间反复传输（贡献 ≈ 1%，但完全可以消除）

| 现象 | 位置 |
| --- | --- |
| 同一份 165 KB 清单文档**传输 2 次** | 响应 `ManageInventoryCaptureResponse.Document`（`AgentInventoryCoordinator.cs:212`）+ 事件 `AgentInventoryUpdatedEvent`（`:207`） |
| 同一份文档**解码 3 次** | Agent 自校验（`AgentInventoryCoordinator.cs:192`）、App 响应路径（`WorkspaceRealOperations.cs:84`）、App 事件路径（`LocalInventoryObserver.cs:44`） |
| 一次采集内**编解码 3 次** | `Decode(cached)`（`:119`）→ `Encode`（`:190`）→ `Decode` 仅校验 SystemId（`:192`） |
| 每帧 JSON **生成/解析各 3 次** | `SerializeToElement`（`AgentControlServer.cs:174`）→ `SerializeToElement`（`:190`）→ `SerializeToUtf8Bytes`（`IpcFrameCodec.cs:19`）；读侧对称 |
| 整个 `SourceFacts` 序列化 **2 次**做字符串比较 | `RealScopedInventoryRefreshPolicy.cs:42` |
| 监控样本**双通路**，且事件通路无人消费 | `AgentMonitorSampleEvent` 被序列化 3 次、传输、解析 3 次后丢弃；App 只用快照轮询 |

---

## 3. 架构问题

### A1 — 数据所有权正确，但"每一步都向 Windows 重新求证"被写进了架构

`docs/Development.md` 的数据所有权表是对的（App 管前台偏好、Agent 管库与采集）。问题不在所有权，而在**验证模型**：

真实操作计划里保存了 `inventoryVersion`、`targetFingerprint`、`physicalMemberFingerprint`（`RealOperationPlanFactory.cs:44-70`）。这些指纹**已经**是"事实未变"的可比较凭据。但实现选择了"每次重新枚举整台机器的存储拓扑，再比对指纹"，而不是"枚举一次，之后用轻量身份探针 + 指纹比对"。

`WindowsRealStorageTopology.cs:41` 自己就写明 `now - facts.InventoryCapturedAt > TimeSpan.FromMinutes(2)` 才算不新鲜——**产品规则允许 2 分钟，实现却要求 2 秒。** 这是架构与自身规则不一致，不是安全需求。

### A2 — "捕获全部事实"与"证明一个具体条件"没有分离

`WindowsRealStorageTopology.cs` 的构造要求 5 个 MSFT_* 类全部 `Returned`（`:17-21`），`RequireSinglePhysicalClosure`（`:120-231`）要求闭包内每个对象的每个安全字段都可读（`:186-208`）。为了证明"这个池名可以改"，系统采集了整台机器的完整存储事实。

正确做法是把两种能力分开：
- **捕获**：一次 full 采集，产出快照与指纹（操作开始时一次）。
- **证明**：针对 `plan.Targets` 的窄查询（`Get-StoragePool -UniqueId <x>` 等，毫秒级），用于写前那一刻的现场核对。

现在两者共用同一重量级实现，于是"证明"被迫付出"捕获"的价格。

### A3 — 恢复/对账没有终止条件

`OutcomeUnknown` 是一个**合法终态**（设计上要求保留写屏障直到只读对账能给出结论）。但系统把"终态"实现成了"永不停止的重试循环"（P3）。缺少三样东西：重试次数或时间上限、退避、以及"重试用尽后转为可见的终态失败并释放屏障"的路径。

用户可见后果：一次未决操作之后，App 会**永久**每 1～5 秒重放一轮对账，直到用户重启。

### A4 — 校验分层重复，而不是分层收窄

`RealOperationValidator.Validate` 在 `src` 中被调用 **29 次**：

`RealOperationProposalFactory.cs`（20 处，每次构造提案）→ `EditorPageBase.cs:301` → `RealOperationPlanFactory.cs:39` → `WindowsRealOperationPlanner.cs:68` → `WindowsRealStorageBackend.cs:183`（`ValidateCurrentStepAsync`）→ `WindowsRealStorageCommandAdapter.cs:46`（`IsExactTarget` + `IsSupportedParameters` + `IsPartitionRoleCompatible`）。

`WindowsRealStorageBackend.RequireFrozenStep`（`:1517`）与 `AgentRealOperationService.cs:1184` 还会在**每次状态迁移**时调用 `RealOperationValidator.IsValid(plan)`，而 `IsValid` 内部会**重跑完整校验并重算 PlanHash**（`RealOperationPlanFactory.cs:236-271`、`OperationPlanHasher.Compute`）。

这类重复不产生新的安全保证——计划在准备阶段冻结并哈希一次即可，之后只需比对哈希。它产生的是**每次操作数十次整类枚举与反序列化**（`RequireFrozenStep` 里还有 `JsonSerializer.Serialize` 比较步骤对象，`:1522-1523`）。

### A5 — 文档与流程约束被写进了测试

`tests/WinPool.Architecture.Tests/ArchitectureBoundaryTests.cs` 断言的是**文件系统与文档文本**，不是代码结构：

```
:58-59   Assert.Contains("DEBT-01", currentPlan) / Assert.Contains("205 compatibility IDs", currentPlan)
:130-144 Assert.False(Directory.Exists(root/"Plan"))；docs 下必须存在指定文件名；归档 md 数量必须 == 16
:148-149 Assert.DoesNotContain("Do not create `Docs/docs`", operationalRules)
:157-169 Assert.False(File.Exists("README_CN.md"))；断言 AGENTS.md 含特定中文子句
```

其中一个（`MainWindowPreviewsSavedWorkspaceBeforeAgentRestore`）本次运行失败，原因是源码文本里出现了不该出现的子串。**这类断言让文档编辑直接导致测试红，也把"文档正确"和"代码正确"混为一谈。** 它同时解释了为什么文档会膨胀：文档本身是被强制的交付物。

---

## 4. 冗余与"同一功能多入口"清单

### 4.1 已确认的多实现（应各保留一条）

| 能力 | 现有实现 | 建议 |
| --- | --- | --- |
| 读本机清单 | `NativeWindowsInventoryProvider`（WMI，637 行）、`EmbeddedPowerShellInventoryProvider`（404 行）、`WindowsHardwareInventoryProvider`（PowerShell 子进程）、`ReadOnlyLocalInventoryReader`（直读 SQLite） | 保留 PowerShell 采集 + 只读 SQLite 启动预览两条；删掉前两个 |
| 清单 IPC 入口 | `CaptureAgentManageInventoryRequest`（主用）、`CaptureAgentManageScopedInventoryRequest`（主用）、`CaptureAgentInventoryRequest`（**App 侧零发送**）、`LoadAgentManageInventoryRequest`（仅 App 启动扫描 + `MachineRecordService`） | 保留前两条；合并后两条 |
| 拓扑采集 | `WindowsRealOperationPlanner.PrepareAsync`、`WindowsRealStorageBackend.CaptureOperationTopologyAsync`（full/scoped）、`WindowsMonitorTargetIdentityResolver.ResolveAsync`、`AgentInventoryCoordinator.CaptureManageCoreAsync` | 统一为**一个** `IStorageTopologyReader`，四种用途只是不同参数 |
| 机器记录服务 | `LocalMachineRecordService`（**零实例化**）、`AgentBackedMachineRecordService`、`EphemeralMachineRecordService` | 删掉第一个 |
| PowerShell 启动器 | `WindowsPowerShellRunner`（stdin 全文脚本）、`WindowsPowerShellStorageWriteRunner`（`-EncodedCommand` + gzip + stdin JSON） | 合并为一套宿主协议 |
| 权限/身份核验 | `WindowsAgentProcessLiveness`、`WindowsProcessIncarnationVerifier`、`WindowsProcessIntegrity` | 保留 1 套，删除重复的 2 套 |
| 真实操作状态 | `QueryAgentRealOperationRequest`（单条）、`ListAgentRecoverableRealOperationsRequest`（列表内逐个 `StatusAsync`）；App 每轮同时发两条 | 只保留列表查询 |
| 快照通路 | 拉取 `GetAgentSnapshotRequest` vs 推送 `AgentStateReseedEvent` | 只保留事件推送 + 断线时一次拉取 |
| 清单通路 | 响应体 vs `AgentInventoryUpdatedEvent` | 只保留事件推送（响应只回回执） |
| 监控样本通路 | `AgentMonitorSampleEvent`（**App 无消费者**）vs `AgentSnapshot.LatestMonitorSamples` | 删掉事件通路 |

### 4.2 已确认的死代码

| 项 | 规模 | 证据 |
| --- | --- | --- |
| `src/WinPool.Inventory` 整个项目 | 1 文件 / 149 行 / 独立 csproj | 只有 `InventoryComparer`；`IInventoryComparer` 实现仅此一处，生产路径无调用者 |
| `NativeWindowsInventoryProvider` | 637 行 | 只在 `Agent/Program.cs:223` 实例化，作为 `nativeProvider` 传入——该参数只在 `CaptureInventoryAsync` 使用，而该请求 App 从不发送 |
| `EmbeddedPowerShellInventoryProvider` | 404 行（`Project` 静态方法仍在用） | 类本身作为 `legacyProvider` 只服务同一死路径 |
| `AgentControlMessageTypes.CaptureInventory` / `LoadManageInventory` | 2 个消息类型 + 2 个 handler + 2 个 client 分派 | 全链路可往返，但 `new CaptureAgentInventoryRequest` 在 `src` 中零出现 |
| `LocalMachineRecordService` | 约 57 行 | `new LocalMachineRecordService` 在 `src` 中零出现 |
| 未实现接口 | **17 个全仓库零实现** | `IAgentPreferencesReader`、`IAgentShutdownTerminalActions`、`IManage{Command,Comparison,Details,Navigation,System}Projector`（5 个）、`IMonitoringDatabaseAccess`、`IMonitoringPersistenceBackgroundDiagnostics`、`IMonitorSessionPersistenceDiagnostics`、`IOperationExecutor`、`IOperationInventoryVersionSource`、`IOperationPlanner`、`IReplayEventSource`、`ISimulationEditCoordinator`、`IStructuredSimulationEditRepository`、`IUserPreferencesReader`（另 6 个只在测试中有替身） |

### 4.3 接口与防御式编程密度

| 指标 | 值 |
| --- | ---: |
| `src` 总行数（.cs/.xaml，11 个项目） | 92 010 |
| 接口数 | 80 |
| 只有 1 个实现类的接口（`src`） | **52** |
| 全仓库零实现类的接口（`src` + `tests`） | **17** |
| `throw` 语句 | 1 462 |
| `try` / `catch` 块 | 511 / 480 |
| `tests` 总行数（12 个项目） | 46 327 |
| `RealOperationValidator.Validate` 调用点 | 29 |
| 单文件最大 | `StorageStructurePage.xaml.cs` 4 881 行；`DiskPartitionPage.xaml.cs` 2 815 行 |

**判断：** 92 010 行实现一个"只读展示 + 受控改写本机存储"的桌面工具，其中真正的 Windows 交互逻辑（`WindowsRealStoragePowerShellScript.cs` 746 行 + 内嵌采集脚本约 646 行）不到 1 500 行。其余约 90 000 行是模型、契约、校验、指纹、重现、恢复、日志、本地化与 UI 管线。这不是"防御式编程"的必要成本，而是**把同一件事在多层各写一遍**。

---

## 5. 验证能力检查

### 5.1 构建

| 项 | 结果 |
| --- | --- |
| `dotnet restore WinPool.slnx` | 7.68 s，exit 0 |
| `dotnet build WinPool.slnx -c Release --no-restore -m:1` | **72.62 s，exit 0，0 warning / 0 error** |
| 最慢项目 | `WinPool.App` 51.58 s（其余均 < 8 s；12 个测试项目各 0.21–1.09 s） |
| 运行树合并 | 288 共享 / 299 App 专属 / 10 Agent 专属，0 冲突 |

构建本身健康。**注意：`dotnet build` 会把上一个可运行树移到 `Rubbish/` 后替换 `artifacts/Release`，这是既有行为，不是缺陷。**

### 5.2 测试

执行方式：12 个测试项目逐个 `dotnet test <csproj> -c Release --no-build -m:1 --logger trx`，累计墙钟 **269.86 s**（整解决方案一次跑为 292.31 s）。

| 项目 | total | passed | failed | 跳过 | 耗时 |
| --- | ---: | ---: | ---: | ---: | ---: |
| WinPool.Infrastructure.Tests | 971 | **941** | **30** | 0 | **3 m 06 s** |
| WinPool.App.Tests | 445 | 445 | 0 | 0 | 0.57 s |
| WinPool.Application.Tests | 433 | 433 | 0 | 0 | 0.58 s |
| WinPool.Persistence.Tests | 192 | 187 | **2** | 3 | 47 s |
| WinPool.Agent.Tests | 139 | 139 | 0 | 0 | 16 s |
| WinPool.Execution.Tests | 116 | 116 | 0 | 0 | 0.21 s |
| WinPool.Monitoring.Tests | 53 | 53 | 0 | 0 | 0.59 s |
| WinPool.Architecture.Tests | 48 | **46** | **2** | 0 | 0.28 s |
| WinPool.Domain.Tests | 23 | 23 | 0 | 0 | 39 ms |
| WinPool.Agent.Client.Tests | 22 | 22 | 0 | 0 | 4 s |
| WinPool.Ipc.Tests | 11 | 11 | 0 | 0 | 0.15 s |
| WinPool.Inventory.Tests | 2 | 2 | 0 | 0 | 13 ms |
| **合计** | **2 455** | **2 418** | **34** | **3** | 269.86 s |

**失败归因（逐条定位到提交）：**

| # | 失败 | 数量 | 根因 | 引入提交 |
| --- | --- | ---: | --- | --- |
| 1 | `WindowsRealStorageBackendTests.PoolCreationRecovery*` / `SingleReturnedPoolCreation*` | 30 | 全部同一个异常 `NotSupportedException: The exact subsystem does not verify the requested capability/member count: SupportsStoragePoolCreation`，抛出点 `WindowsRealOperationPlanner.MemberSets.cs:312`，调用点 `:129`。测试的 `Fixture` 构造 planner 时**未传 `capabilities:`**（`WindowsRealStorageBackendTests.cs:2954-2956`），因此缺少 `SupportsStoragePoolCreation` 与 `PhysicalDisksPerStoragePoolMin` 证据；生产侧新增该门后 fixture 未同步。 | `b39ac42`（2026-10-14） |
| 2 | `ArchitectureBoundaryTests.EditorPagesOwnTheirSurfacesWithoutSectionTitles` | 1 | 断言 `DiskPartitionPage.xaml` 含 `StartOffsetValue`（`:572`），页面已重构，标识符不存在 | `8f939d6` / `0c6aea7`（2026-10-16） |
| 3 | `ArchitectureBoundaryTests.MainWindowPreviewsSavedWorkspaceBeforeAgentRestore` | 1 | 断言 `NotificationCard.xaml` 不含 `<Border`（`:1023`），控件已重构出 Border | 同上 |
| 4 | `BuildOperationGuardTests.StoppedUnknownResultAllowsRepairWithoutChangingDatabaseOrWriteBarrier`（`:91`）、`…MissingCorruptAndAmbiguousRootsRefuseButPristineRootPasses`（`:40`） | 2（必现 flaky） | 每次 probe 都 `Process.Start("pwsh")` 执行 `build/Assert-RealOperationIdle.ps1`（`BuildOperationGuardTests.cs:206`），断言的是**外部进程退出码**，因此结果依赖机器状态（本机 `%LOCALAPPDATA%\WinPool` 留有 2026-09-21 的遗留 `storage-location.json`）。对 `Persistence.Tests` 单独连跑 3 次：失败 **2 / 3 / 2**。反例：整解第一次跑时这两项通过——即它们与同一 class 内其它用例的执行顺序相关。 | — |
| 5 | `StorageLocationManagerTests.ProductionRoundTripMovesBothDatabasesAndArchiveLifecycleWithoutLeavingOldRootReferences` | 0/1（偶发） | `:1255` `Assert.True(background.FailedArchives > 0)`；真实调用随包 `7za.exe`，`WaitForIdleAsync(TimeSpan.FromSeconds(30))`（`:1149`）之后仍偶发后台失败归档未及时记账 | — |

**测试覆盖面的事实：**

- **没有任何 UI 测试。** `WinPool.App.Tests` 通过 `<Compile Include="..\..\src\WinPool.App\Services\*.cs" Link=...>`（`WinPool.App.Tests.csproj:23-46`）源码链接来测 App 的服务层；`Architecture.Tests` 以**文本方式读取 XAML 与 csproj**（`:235`、`:1312`）。全仓库没有 `Application.Start` / UIAutomation / DispatcherQueue。WinUI 渲染与交互零自动覆盖。
- **没有任何提权或硬件门控。** 无 `[Trait("Requires…")]`、无 `runsettings`、无 `xunit.runner.json`、无 assembly 级属性。
- **唯一的真实硬件只读测试被静默跳过。** `tests/WinPool.Infrastructure.Tests/RealAdmissionReadOnlyTests.cs:16-17` 在 `WINPOOL_READ_ONLY_ADMISSION != "1"` 时直接 `return`——xUnit 记为 **Passed**。另有 6 处 `if (!OperatingSystem.IsWindows()) return;`（`BuildOperationGuardTests.cs:14/38/63/83/105/119`）在非 Windows 上同样静默通过。
- 显式跳过的只有 3 条：`MonitoringArchiveMeasurementTests` 的 `[EnvironmentEnabledFact("WINPOOL_RUN_1GIB_ARCHIVE_MEASUREMENT")]` 等，属性在发现期设置 `Skip`（`:888-900`）。
- **高危路径全部打在合成替身上。** `WindowsRealStorageBackendTests` 使用 `SyntheticFactSource` / `SyntheticAdapter` / `SyntheticSafetyInspector`（`:2948-2953`），断言的常态是 `Assert.Equal(0, fixture.Adapter.CallCount)`（该文件出现 **57 次**），即"没有真的调用 Windows"。
- **没有任何性能或延迟回归测试。** 无 BenchmarkDotNet、无基线耗时、无 CI 性能门。全套只有 2 处粗略上界：`InfrastructureTests.cs:30-32`（磁盘查找 < 5 s）与 `ControlledProcessRunnerTests.cs:61`（< 10 s）。**这正是"删除 VD 30.94 s、新建 MAX VD 247.69 s、provider 采集占约 80%"能通过全部工程门的原因。**
- `WinPool.Inventory.Tests` 是占位项目：1 个文件 / 103 行 / 2 个 `[Fact]` / 7 次 `Assert`，测的是 `WinPool.Application` 的 `InventoryComparer`，**从不触及自己引用的 `WinPool.Inventory` 项目**。
- 测试运行期间会执行**真实 Windows 存储采集**：`InfrastructureTests.cs:131 RealReadOnlyScanReturnsUsableInventory`（14.79 s）与 `WinPoolFactCaptureTests.cs:310`（16.26 s）都会启动真实 PowerShell；本次运行中 testhost PID 4348 在 98 秒内产生 563 条生产遥测、522 次 `real.capture.full`，并写入 `%LOCALAPPDATA%\WinPool\Diagnostics\operation-timing.jsonl`。**测试与现网诊断数据混流。**

**最慢 5 项：** 16.51 s（持续生产者多轮轮转精确多重集）、16.26 s（真实硬件采集）、14.79 s（真实只读普查）、11.30 s（写入器重复失败后暂停与恢复）、10.18 s（WAL 足迹触发轮转）。无任何测试 > 30 s。

### 5.2.1 测试对现网数据与证据目录的副作用

- 三个无门控的真实主机接触点在默认运行下就会执行：`InfrastructureTests.cs:131`（真实 Windows PowerShell 存储普查，14.79 s）、`WinPoolFactCaptureTests.cs:310`（真实 storage + hardware 采集，16.26 s）、`StorageLocationManagerTests.cs:1118`（真实随包 `7za.exe`）。
- 运行期间会向 `%LOCALAPPDATA%\WinPool\Diagnostics\` 写入生产诊断：本次运行中 testhost PID 4348 在 98 秒内产生 563 条生产遥测记录、522 次 `real.capture.full`。
- 同时在测试证据目录就地写出 `agent-control.jsonl`、`monitor-edit.jsonl`、`operation-timing-runaway.jsonl*`。

**这三条合起来意味着：不能把 `%LOCALAPPDATA%\WinPool\Diagnostics\operation-timing.jsonl` 当作纯粹的生产遥测来分析。** 本次检视必须先把测试进程与 App/Agent 进程的 `processId` 分开统计，才能得到 §1.3 的干净口径。

### 5.3 真实环境 GUI 端到端

已按开发授权在管理员会话中启动 `artifacts/Release/WinPool.App.exe`（真实 Agent、真实存储），完成：

1. 启动 App → 提权 UAC "打开文件 - 安全警告" → 接受 → Agent 进程起来 → 主窗口可交互。
2. 导航到存储结构编辑页，选中池 `Q`，开启真实编辑（二次确认）。
3. 在属性栏把池名改为 `Q2` 并尝试提交。

**结果：提交后 283 秒内视图未产生新结果；同时诊断日志暴露了 §2/P3 的失控循环。**

根因不是 UI 卡住，而是：**该会话启动时 Agent 正持有上一次未决的 MAX 操作**，App 进入恢复轮询（`WorkspaceRealOperations.cs:255-270`），每 1～5 秒重放对账，每次对账触发一次即时失败的 full 采集。用户的每一次点击都在和这个循环抢同一条串行控制管道与同一把数据库写锁。

现场留证：

- `artifacts/test-results/review-20261017/operation-timing-runaway.jsonl`（219 389 字节）
- `artifacts/test-results/review-20261017/operation-timing-runaway.jsonl.1`（1 048 678 字节）
- `artifacts/test-results/review-20261017/agent-control.jsonl`

**这一步同时证实了 §2.3：这不是理论问题，是用户日常会遇到的状态。**

---

## 6. 文档与维护流程

### 6.1 体量

| 范围 | 字符数 | 文件数 |
| --- | ---: | ---: |
| 现行文档（`docs` 下非 Archive 的 md） | **285 765** | 21 |
| `docs/Archive` | **1 387 780** | 196 md / 53 个归档目录 |
| 单文件最大 | `docs/Quality.md` 132 603；`docs/Development.md` 104 642；`docs/CHANGELOG.md` 90 171 | — |
| `CHANGELOG` 顶级条目 | **75** 条 | — |
| `Quality.md` 顶级节 | 31 节，其中 **约 24 节是 2026-09-15 → 10-16 的逐日流水** | — |

### 6.2 具体问题

1. **`Quality.md` 承担了 CHANGELOG 的职责。** 它应当只写"验证范围与验收条件"（文件开头 `选择验证范围 / 自动门 / 人工与设备证据` 才是它的主题），但 3/4 的篇幅是按日期的完成记录，已经出现在 `CHANGELOG.md` 里。
2. **`Development.md` 的"统一事实与采集链路"一节包含 28 个段落、单段最长逾 1 500 字**，把实现细节（控件 DIP 尺寸、按钮样式、动画周期、Tooltip 文案）与架构约束混在一起。任何接手者必须逐段读完才能知道哪句是约束、哪句是历史。
3. **文档被测试强制。** §A5 已列：`docs/Plan.md` 必须包含 `DEBT-01` 与 `205 compatibility IDs`，`docs/Archive` 的 md 数必须等于 16。文档编辑会导致测试红。
4. **延迟被写成了成绩。** `docs/WinPool-Operation-Latency-20261008.md` 与 `docs/Development.md:189-191` 把"接受→界面就绪中位数从 18.472 s 降到 15.437 s（−16.43%）"作为优化结果记录，并用三段文字论证该数据的可比性边界。**但从第一性原理看，这个指标衡量的是"5 次 PowerShell 枚举的耗时"，改善 3 秒等于把缺陷从 5 次枚举降到 5 次略快的枚举。** 报告花在讨论统计有效性上的篇幅，远超花在"为什么需要 5 次"上的篇幅。
5. **`AGENTS.md` 的协作规则本身成为开销。** 92 行规则里包含模型选择建议、子代理并发上限、文档归档路径、根目录保留名单、PPT 页数上限等。规则越细，每个任务的前置阅读与合规检查越重——这直接对应"代码编写之外的额外工作量"。

### 6.3 与用户诉求的关系

用户要求"大幅削减代码编写之外的额外工作量"。当前架构下，一个功能改动的固定开销是：

1. 读 `AGENTS.md` → 读 `docs/Product.md` / `Development.md` / `Quality.md`（共 285 K 字符）。
2. 若跨模块，写 `docs/Plan.md`（它会被 `Architecture.Tests` 断言）。
3. 改代码（`src` 92 010 行，单页最大 4 881 行）。
4. 跑全量测试（2 263 项，3.5 分钟，32 项预先为红）。
5. 更新 `docs/Quality.md` 加一节流水 + `docs/CHANGELOG.md` 加一条。
6. 归档旧 Plan，维护 `docs/Archive/README.md` 索引。
7. 若碰到延迟话题，追加一段"该测量不构成普遍保证"的免责论证。

**第 1、2、4、5、6、7 步都不产生代码价值，且第 4 步当前无法给出可信的绿灯。**

---

## 7. 修改方案

原则：**能简单拆分就不复杂拆分；AB 可行就不要 ABCD。** 下面的每一步都独立可交付，且都在"减行数"。

### 第 0 阶段（当天可完成，纯删除 + 几行改动，无需重构）

| # | 动作 | 位置 | 预期收益 |
| --- | --- | --- | --- |
| 0.1 | **给对账加终止条件。** 在 `ReconcilePersistedAsync` 加"同一 OperationId 连续 N 次（建议 3）未释放屏障后，转为显式终态 `Failed/OutcomeUnknown-exhausted` 并释放写屏障"，并把 `ScheduleReconciliation` 的触发改为仅在**显式用户查询**时，删除 `ListRecoverableAsync`（`:277`）里的自动触发 | `AgentRealOperationService.cs:277, 849-922` | 消除失控循环；日志增速归零；写屏障可解除 |
| 0.2 | **停止 App 侧的无限恢复轮询。** `PollRealOperationRecoveryAsync` 加总时长上限（建议 60 s）与"无进展即停"，并只在用户可见真实编辑页时运行 | `WorkspaceRealOperations.cs:247-270` | 后台不再持续抢占串行通道 |
| 0.3 | **删掉重复的调用前采集。** `ExecuteStepAsync` 直接复用步前预检的拓扑与指纹（两者之间只有 SQLite 迁移） | `WindowsRealStorageBackend.cs:263-284` | **每步 −2.2～2.7 s** |
| 0.4 | **删掉接受前预检的重复校验。** `AcceptAsync` 复用 `PrepareAsync` 已产出的计划校验结果，不再重跑 `PreflightStepAsync` | `AgentRealOperationService.cs:485` | **每次操作 −3.3 s** |
| 0.5 | **调用后验证改为先查一次、不成立再退避。** 把"循环里做整份拓扑采集"改为"第一次仍采集；之后改为 250 ms 起的退避 + 单对象查询，仍不成立才升级为完整采集" | `WindowsRealStorageBackend.cs:367-423` | 最坏情况由 66～115 s 降到 < 10 s |
| 0.6 | **把静默跳过的真实硬件测试改成显式 Skip。** `RealAdmissionReadOnlyTests` 用 `[EnvironmentEnabledFact]` 而非裸 `return`；6 处 `if (!OperatingSystem.IsWindows()) return;` 同样处理 | `RealAdmissionReadOnlyTests.cs:16-17`；`BuildOperationGuardTests.cs:14/38/63/83/105/119` | 测试报告不再把"没跑"记为"通过" |
| 0.7 | **删掉 2 个 XAML/源码文本断言**（`Assert.Contains("StartOffsetValue", …)`、`Assert.DoesNotContain("<Border", …)`）与全部文档断言 | `ArchitectureBoundaryTests.cs:572, 1023, 148-149, 130-144` | 测试不再因控件重构或文档编辑而红 |
| 0.8 | **修 30 项夹具腐化。** 给 `WindowsRealStorageBackendTests` 的 `Fixture` 补上 `capabilities:`（`SupportsStoragePoolCreation`、`PhysicalDisksPerStoragePoolMin`） | `WindowsRealStorageBackendTests.cs:2954-2956`；对照 `WindowsRealOperationPlanner.MemberSets.cs:309-312` | 测试恢复绿灯 |
| 0.9 | **消除 2 项必现 flaky。** 不再用"启动真实 `pwsh` 探测脚本 + 断言退出码"的方式测试构建守卫，改为在隔离夹具目录上直接调用被测逻辑，禁止读取 `%LOCALAPPDATA%\WinPool` | `BuildOperationGuardTests.cs:40, 91, 206` | 测试结果不再依赖机器状态；不再与现网数据混流 |
| 0.10 | **消除 1 项偶发 flaky。** `ProductionRoundTrip…` 改为等待归档记账完成而非固定 30 s 后断言 | `StorageLocationManagerTests.cs:1149, 1255` | 去掉随包 `7za.exe` 的时序竞争 |
| 0.11 | **固定运行成本。** App 自身编译 51.58 s 占全解的 71%（`MergeLocalAppAndAgentRuntime` 24.98 s、`CoreCompile` 18.20 s、`MarkupCompilePass2` 9.89 s、`XamlPreCompile` 8.09 s）；本阶段不改，但应记录为该仓库的主要构建成本 | `WinPool.App.csproj` / `Directory.Build.targets` | 后续决定是否拆分 App 程序集 |

第 0 阶段完成后，预期的单步操作时间：**15～30 s → 约 5～8 s**。

### 第 1 阶段（本阶段的核心：常驻 PowerShell 宿主）

**这是唯一必须做的性能改动。** 实测 12.7 倍差距全部来自这里。

- 新建 **一个** 常驻宿主：Agent 启动时创建一个长时间运行的 `powershell.exe` 子进程（或进程内 Runspace），通过长度前缀帧协议接收"命令 + 参数 JSON"，返回"结果 JSON"。
- 采集脚本与写脚本保持不变（仍内嵌于程序集），只在宿主内**加载一次**。`Update-StorageProviderCache` 只在宿主首次启动或显式请求时执行。
- 超时/取消/宿主崩溃时的重建语义集中在这一处实现一次。
- 删除 `WindowsPowerShellStorageWriteRunner` 与 `WindowsPowerShellRunner` 两套不同的传输协议，只留一套。

**收益（本机实测外推）：** 单次采集由 2 214 ms → 约 175 ms（warm 视角 872 ms / 5 次，含一次启动）。5 次采集的固定开销由 11.1 s → 约 1.0 s。

第 1 阶段完成后，预期的单步操作时间：**5～8 s → 约 1.5～2.5 s**（即 `pwsh` 量级）。

### 第 2 阶段（接口收口：同一功能只留一条路径）

| # | 动作 | 删除量 |
| --- | --- | --- |
| 2.1 | 删除 `src/WinPool.Inventory` 项目及其 csproj、`WinPool.Inventory.Tests`；`IInventoryComparer` 若仍需，落到 `WinPool.Application` | 149 行 + 1 项目 + 103 行测试 |
| 2.2 | 删除 `NativeWindowsInventoryProvider`（637 行）与 `EmbeddedPowerShellInventoryProvider` 的类壳（保留 `Project` 静态方法并移入 `WindowsHardwareInventoryProvider` 或 Application） | 约 1 000 行 |
| 2.3 | 删除 `CaptureAgentInventoryRequest` + `LoadAgentManageInventoryRequest` 两条 IPC 消息、两个 handler、两个 client 分派；`MachineRecordService` 的启动扫描改用 `LoadAgentManageInventory` 的替代者（直接读 Agent 缓存事件或简化后的单一"取缓存清单"请求） | 约 300 行 + 2 消息类型 |
| 2.4 | 删除 `LocalMachineRecordService`（零实例化）与 17 个全仓库零实现的接口（含 5 个 `IManage*Projector`） | 约 200 行 |
| 2.5 | 把 4 条拓扑采集路径统一为 1 个 `IStorageTopologyReader`（参数化 scope），删除 `RequireFrozenStep`/`IsValid` 的每次迁移重算 | 约 400 行 |
| 2.6 | 清单只走事件推送；响应只回"已接受 + SnapshotId" | 每操作省 1 次 165 KB 传输 + 1 次解码 |
| 2.7 | 删除无人消费的 `AgentMonitorSampleEvent` 通路 | 约 100 行 |
| 2.8 | 快照轮询：5 Hz → **0 Hz**（纯事件推送），仅在断线重连时拉一次；删除 `MonitorAlertObserver` 的 1 Hz 轮询 | 稳态每请求负载由 ≥6 次/秒降到 0 |
| 2.9 | 合并 3 套进程身份核验为 1 套 | 约 300 行 |

### 第 3 阶段（把"证明一个条件"与"捕获全部事实"分开）

- `CaptureAsync` 保持现状（一次，操作开始时）。
- 新增 `ProbeTargetAsync(target)`：对**单一**目标做窄查询（`Get-StoragePool -UniqueId`、`Get-Disk -Number`、`Get-Partition -Guid` 等），毫秒级，返回身份 + 关键指纹。
- 写前核对改用 `ProbeTargetAsync` + 与冻结指纹比对；只有探针失败或指纹不符时才升级为完整采集。
- 复用现有 2 分钟新鲜度规则（`WindowsRealStorageTopology.cs:41`），把实现对齐到产品已声明的阈值。

**收益：** 每步的 3 次 scoped 采集降为 0～1 次，且那 1 次是毫秒级窄查询。

### 第 4 阶段（削减防御式编程与层级）

| # | 动作 |
| --- | --- |
| 4.1 | 接口由 80 收敛到 **约 15**：只保留真正有多实现或需要测试替身的边界（`IRealStorageBackend`、`IAgentConnection`、`IMonitorSource`、`IStorageLocationManager`、`IControlledProcessRunner` 等）。其余 52 个单实现接口改为具体类——**C# 里需要抽象时再加，是零成本的。** |
| 4.2 | 校验分层由"每层重跑全量校验"改为"冻结一次 + 比对哈希"。保留：提案构造时的形状校验、计划冻结时的安全性校验、写前一步的指纹核对。删除：每次状态迁移的 `IsValid`、adapter 层对 planner 已校验类型与参数的重复校验。 |
| 4.3 | `WindowsRealStorageTopology` 的闭包校验（`:120-231`、`:239-417`）保留**安全必需**部分（保护角色、系统盘、加密、闭包唯一性），删除"每个字段都必须 Returned"的全量要求——改为只对参与本次决策的字段强制。 |
| 4.4 | 拆分 4 881 行的 `StorageStructurePage.xaml.cs`。按现有 partial 命名（已有 `StorageStructurePage.DraftUi.cs`）继续按职责拆到 ≤600 行/文件。**这纯粹是可维护性，不涉及设计变更。** |
| 4.5 | 删除约 480 个 `catch` 中"只记录不改行为"的部分：把可恢复异常集中到少数边界处理，其余上抛。保留：写前/写后边界、IPC 边界、SQLite 边界、文件处置边界。 |

### 第 5 阶段（文档与流程瘦身）

| # | 动作 | 目标 |
| --- | --- | --- |
| 5.1 | `Quality.md` 只保留验收口径（现有开头 `证据边界`→`人工与设备证据` 的骨架），逐日完成记录移入 `CHANGELOG.md`（若已在则删除） | 132 603 → 约 25 000 字符 |
| 5.2 | `Development.md` 按"约束 / 实现说明 / 历史"三分。控件尺寸、动画周期、Tooltip 文案移入 `docs/DesignTables/`；历史移入 `CHANGELOG` | 104 642 → 约 35 000 字符 |
| 5.3 | `docs/Archive` 从现行文档树**移出**到仓库根 `Old`（AGENTS.md 已定义该位置），`docs/` 只留现行文档 | 196 个文件不再参与检索 |
| 5.4 | 删除 `Architecture.Tests` 中所有文档断言（与 0.7 同一动作） | 文档编辑不再触发测试红 |
| 5.5 | 新增**唯一**一项性能回归测试：断言"单步真实操作的 provider 采集进程数 ≤ 1、拓扑读取次数 ≤ 2"，用一个合成 fixture 即可，不需要真实硬件。现有全套只有 2 处粗略上界（`InfrastructureTests.cs:30-32` 磁盘查找 < 5 s、`ControlledProcessRunnerTests.cs:61` < 10 s），必须换成会失败的预算断言 | 让"15.4 秒"这类结果**不可能**再次通过验收 |
| 5.6 | 把 `WinPool-Operation-Latency-20261008.md` 的结论改写为问题陈述（现状：把 15.4 秒写成改善）；数字保留，结论改为"存在 5 次冗余枚举，目标 ≤ 1 次" | 避免后续接手者把它当成绩基线 |

### 阶段与预期

| 阶段 | 单步操作 | 三步操作 | 分层 MAX | src 行数 | 文档字符 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 现状 | 15～30 s | 113 s | 244 s | 92 010 | 285 765 |
| 第 0 阶段后 | 5～8 s | 30～40 s | 约 80 s | 约 91 500 | 285 765 |
| 第 1 阶段后 | **1.5～2.5 s** | **6～9 s** | **约 15 s** | 约 91 000 | 285 765 |
| 第 2+3 阶段后 | **≤ 1.5 s** | **≤ 4 s** | **约 10 s** | 约 86 000 | 285 765 |
| 第 4+5 阶段后 | ≤ 1.5 s | ≤ 4 s | 约 10 s | **约 55 000** | **约 60 000** |

---

## 8. 哪些检查必须保留

以下不是冗余，删除会降低安全性。改造时应逐条确认仍在：

| 检查 | 位置 | 为什么必需 |
| --- | --- | --- |
| 写前现场身份与指纹核对 | `WindowsRealStorageBackend.cs:276-284`、写脚本内 `Assert-Exact`（`WindowsRealStoragePowerShellScript.cs:21-26`） | 唯一证明"计划里的目标就是当前的真实对象"的手段 |
| 物理成员闭包 | `WindowsRealStorageTopology.cs:120-155` | 防止动到同池其他成员 |
| 受保护角色判定（Boot/System/PageFile/CrashDump/Offline/ReadOnly/MSR/EFI/Recovery） | `WindowsRealStorageSafetyInspector.cs:325-360, 399-558` | 防止抹掉系统盘 |
| 加密状态证明（BitLocker + VDS） | `WindowsRealStorageSafetyInspector.cs:629-784` | 防止破坏加密卷 |
| 写后验证（至少第一次） | `WindowsRealStorageBackend.cs:372-411` | provider 返回 ≠ 操作成功 |
| 结果未知时只读对账（单次） | `WindowsRealStorageBackend.cs:461-667` | 不重放已发出的写调用 |
| `CallIssued` 的持久化屏障 | `AgentRealOperationService.cs:1269-1278` | 崩溃后不重复写入 |
| 采集脚本固定内嵌、经 stdin 执行 | `InventoryPipeline.cs:29, 47-55` | 不可成为外部执行输入 |
| App/Agent 单实例、SID 绑定握手、进程化身核验 | `AgentControlServer.cs:393-416` | 防跨用户/跨进程控制 |

---

## 9. 结论

1. **性能问题有一个单点根因，且已被实测量化：每次读存储拓扑都新起进程、重付 provider 冷启动。** 冷 2 214 ms vs 常驻 175 ms，12.7 倍。把一个常驻宿主做好，单步操作就能从 15～30 秒回到 `pwsh` 量级。
2. **其余 3～6 倍来自同一步内的重复读取。** 这些读取在数学上不可能带来新信息，纯属实现选择，删掉不影响任何安全属性。
3. **存在一个会失控的无限重试缺陷**，会在真实使用中持续抢占控制通道与写锁，制造"几百秒无响应"的体感。已现场复现并留证。
4. **冗余是结构性的，不是风格问题。** 80 个接口里 52 个在 `src` 中只有一个实现、17 个全仓库没有任何实现；同一能力普遍有 2～4 条路径；一个 149 行的项目只服务测试；两条 IPC 消息全链路可用但无人发送。这些行数没有换来安全性，只换来了维护面。
5. **现有验证体系无法发现上述任何一条，而且它当前是红的。** 2 455 项测试中 34 项失败（32 项确定性夹具/文本断言腐化 + 2 项偶发），3 项手工测量跳过；高危路径全部打在合成替身上（`Assert.Equal(0, fixture.Adapter.CallCount)` 出现 57 次，即"确实没有调用 Windows"）；唯一接触真实硬件的普查测试静默通过；**没有任何一项会因性能退化而失败**。红灯已滞留 1～3 天，而同期 `docs/Quality.md` 写着"零警告／零错误、未跑全量测试"。**这正是"15.4 秒"能被当成改善的原因。**
6. **文档与流程约束已被写进测试**，使文档编辑可导致测试红，也让"文档正确"冒充"代码正确"。这直接构成用户所要求的"代码编写之外的额外工作量"。

建议执行顺序：**第 0 阶段立即做（当天、纯删除、无需重构），第 1 阶段作为本轮性能目标，第 2～5 阶段随后收口。** 第 0 + 1 阶段即可达成"`pwsh` 里几秒的操作，WinPool 也应是几秒"的预期。
