---
project: WinPool
phase: V0.3
architecture_version: V0.3
status: accepted
branch: main
confirmed_by_user: 2026-08-11
refactor_scope_approved_by_user: 2026-08-11
execution_authorized: true
started_from_commit: d4979fa84d516ebecff16a089590ee8af8d6cc76
started_on: 2026-08-11
code_gate: passed
native_integration_gate: unverified
manual_gate: unverified
remote_gate: passed
accepted_by_user: 2026-08-11
authority: docs/Archive/V0.33/Plan.md
---

# WinPool V0.33 架构收口与生命周期硬化计划

[权威归档](Plan.md) | 简体中文阅读副本

> 本文件仅为中文阅读副本；状态、任务和验收结论以无 `.zh-CN` 后缀的
> [Plan.md](Plan.md) 为准。权威 Plan 本身已经使用中文，本副本只为满足统一的
> 双文件阅读约定。

本文在 V0.33 执行期间是用户确认的唯一活动计划，合并并取代以下两份提案在该阶段的控制作用：

- `V0.33重构.md`；
- `V0.33重构补充.md`。

两份原始提案保留为用户输入记录。用户已于 2026-08-11 明确批准本计划并要求
开始执行，并于 2026-08-11 明确确认 V0.33。当前状态为 `accepted`。
本次授权允许按本文约束实施源码重构、严格限定的 Core 删除、验收文档归档、
Git 提交和推送；仍不授权 tag、GitHub Release、二进制上传或部署。

## 1. 阶段定位

V0.33 是 V0.3 架构线的第三个迭代版本：

```text
a = 0
b = 3
c = 3
```

本阶段不扩大产品能力边界，而是收口 V0.2/V0.3 迁移后遗留的双领域模型、
进程生命周期、IPC 故障恢复、配置所有权、工具输出编码和数据位置迁移问题。

V0.33 是项目版本，不自动创建 Git tag、GitHub Release、二进制发布、上传或部署。
正常 V0.33 迭代只要求本地提交；远端推送需要新的明确授权。

## 2. 当前进入基线

- 当前项目版本：V0.32。
- 当前 Git 基线：`d4979fa84d516ebecff16a089590ee8af8d6cc76`。
- `main` 与当前本地 `origin/main` 引用一致。
- 当前方案文件和本草稿尚未提交。
- 最近一次完整 Release 测试：458 项中 457 passed、1 failed、0 skipped。
- 失败项：
  `StorageLocationManagerTests.RealSqliteMigrationVerifiesLogicalIdentityBeforePointerCommit`。
- 单项连续复测三次均复现 SQLite `winpool.db` 文件句柄未释放导致的清理失败。
- 该失败属于 DEF-09/V33-10 的 SQLite quiescence 与数据迁移生命周期问题，
  不是 V0.33 之外的新阶段；在恢复 458/458 前不得开始大规模模型迁移。

所有结果只能使用：

```text
passed / failed / unverified / not_required / deferred_by_user
```

## 3. 已确认边界

### 3.1 产品和安全边界

- 不实现真实磁盘、分区、卷、Storage Pool、Tier 或 Virtual Disk 结构修改。
- storage structure 变更继续仅允许 Simulation。
- 不增加第五个长期进程。
- 不引入插件系统、消息队列、MediatR、actor framework、service bus 或额外 DI 容器。
- 不冻结公共 C# API 或公共 IPC API，不建立旧 IPC 的长期兼容层。
- 不更换 SQLite，不承诺数据库 downgrade。
- 不借本轮顺手重写无关的 Monitoring、Testing 或成熟 tool adapter。
- 不把未执行的 native/manual case 写成 passed。

### 3.2 Core 删除的本次特批

用户已经批准 V0.33 重构，并明确允许本次重构突破默认“不直接删除文件”规则，
删除范围严格限定为：

```text
src/WinPool.Core
tests/WinPool.Core.Tests
```

以及它们在 solution/csproj 中的引用。执行删除必须同时满足：

1. 对应能力已有明确的新所有者和替代实现；
2. 有效测试已经迁移，不得用删除测试掩盖回归；
3. semantic parity 和适用的 native/manual regression 已产生真实状态；
4. 删除发生在可独立构建、测试和审查的提交中；
5. 不扩大到其他源码、文档、资产、用户数据、证据或生成目录。

该特批不等于当前已经授权开始执行。执行仍等待本 Plan 的用户确认。

## 4. V0.33 必须关闭的缺陷

| ID | 问题 | 工作包 | V0.33 阻塞 |
|---|---|---|---|
| DEF-01 | incomplete shutdown 永久停在 `ShuttingDown` | V33-02 | 是 |
| DEF-02 | Worker terminal response 后无限等待退出 | V33-03 | 是 |
| DEF-03 | Broker terminal response 后无限等待退出 | V33-03 | 是 |
| DEF-04 | Worker tool 启动前存在取消死区 | V33-04 | 是 |
| DEF-05 | malformed control connection 可结束 server loop | V33-05 | 是 |
| DEF-06 | portable mode 的 tool-path 配置所有权分裂 | V33-08 | 是 |
| DEF-07 | progress/final parser 的 encoding resolution 不完整且不一致 | V33-09 | 是 |
| DEF-08 | PID tombstone 阻止复用并使 live registry 增长 | V33-07 | 是 |
| DEF-09 | storage migration merge stale target，且 SQLite handle 生命周期未闭合 | V33-10 | 是 |
| DEF-10 | event pipe 断开后客户端静默停止接收事件 | V33-06 | 是 |

任何一项都不得仅记录为 debt 后关闭 V0.33。

## 5. 权威模型决定

### 5.1 共享语言而非强制共享 document lifecycle

Domain 只保留跨 use-case 的纯模型：

- `SystemId`、`EnvironmentId`、`StorageObjectId`、`StorageObjectKind`；
- `OperationId`、`SessionId`；
- `ExecutionMode`、`PrivilegeState`；
- Preferences 和 `StorageLocationMode`；
- 纯 storage math、identity 和 algorithm confidence 规则。

Application 拥有内部 use-case contracts、inventory/query/read models、Manage
projection、workspace state、simulation edit、Agent request/response、process、tool、
monitoring 和 testing contracts。

Local 与 Simulation 共享 identity/storage-object 语言和可供 App 使用的 read
projection，但不预设它们必须共享一个 mutable runtime document：

- Local：read-only、capture-based、以 `InventorySnapshot` 生命周期为主；
- Simulation：editable、revisioned、conflict-checked、reloadable；
- App：消费统一 identity、source kind 和 projection，不拥有第二套领域模型。

### 5.2 身份和版本语义

- Local 被 clone/convert/import 为新 Simulation 时必须产生新的 `SystemId`。
- Local 与 Simulation 不因来源关系共享 `SystemId`。
- provenance 通过独立 metadata 表达。
- Document identity 与 `SystemId` 分离。
- Document revision 表示可编辑文档的乐观并发版本。
- Inventory version 表示 snapshot/state 版本，不能与 document revision 合并。
- Source kind 不参与 identity equality。

### 5.3 V33-SUP-A：实施前设计门

在迁移 Legacy `StorageSystemDocument` 之前，必须在 Plan 的执行状态中记录：

- 是否需要 Application aggregate document；
- 若需要，准确类型名、全部字段、identity/equality 规则；
- Local 与 Simulation 是否使用同一 runtime 类型；
- revision、inventory version 和 source kind 的 owner；
- runtime typed object、IPC/persistence payload、SQLite representation 的边界；
- mapper/codec 所属层；
- import/replay 的 document 类型；
- Local → Simulation 的新 identity 和 object mapping。

该门未关闭前，禁止创建临时 `Application.StorageSystemDocument` 后靠编译错误
猜字段。

#### 已确认实施决定（2026-08-11）

V33-SUP-A 已关闭，采用以下合同：

- Application 需要一个内部 `StorageSystemDocument` aggregate，作为 Manage
  projection、Simulation edit、import/export 与 persistence mapper 的结构化输入；
  它不是公共 API，也不是 Domain entity。
- aggregate 字段为：document schema、独立 document ID、明确 `SystemId`、source
  kind、display name、`StorageSnapshot`、hardware report、simulation jobs、document
  revision、updated time、source host 与可选 provenance document ID。
- Local 与 Simulation 可以共用上述**不可变结构 envelope**，以保持同一 projection
  语言；它们不共用 mutable lifecycle。Local 只能由 inventory capture replace，
  revision 固定为 0；Simulation 由 edit coordinator/repository 维护 revision 并执行
  optimistic conflict check。
- `SystemId` 由 Domain 定义并表示被观察/编辑的系统；document ID 只表示文档。
  新建、clone、convert 或 import 为 Simulation 时生成新的 document ID 和新的
  `SystemId`，来源关系只写入 provenance，不参与 equality。
- inventory version 归 Local capture/snapshot 所有；document revision 归 Simulation
  repository/edit lifecycle 所有；source kind 归 Application document envelope 所有。
- runtime 使用上述 typed aggregate；IPC/persistence 继续传递有边界的 payload，
  SQLite 继续保存经校验与脱敏的 JSON representation，不把 JSON 当业务模型。
- codec/mapper 归 Infrastructure serialization boundary；Manage projector 只消费
  Application aggregate/projection contract。import/replay 先解码为受校验的
  Application document，再按目标 lifecycle 建立新 identity。
- V0.32 payload 缺失显式 `SystemId`/revision 时由 codec 在边界执行一次兼容读取：
  根据旧 document ID 稳定派生旧 identity、revision 归一为 0；下次保存写入新合同。
  该兼容仅用于持久化迁移，不建立旧 Core 或旧 IPC 的长期运行时兼容层。

## 6. 工作包

### V33-00：确认 Plan、记录基线并恢复绿色基线

1. 用户确认本草稿可行后，将 `status` 改为 `confirmed`。
2. 实施开始时改为 `in_progress`，记录开始 commit 和工具版本。
3. 记录 Release test、build warning/error、package vulnerability、staging 和
   SQLite schema v10 基线。
4. 为 DEF-01～DEF-10 建立明确的 regression test/reproduction 名称。
5. 先关闭 DEF-09 中已经复现的 SQLite handle/pool 基线失败，恢复 458/458。
6. 在绿色基线前禁止开始 Core 大规模迁移。

### V33-01：收口 storage/simulation 模型并退役 Core

1. 先关闭 V33-SUP-A。
2. WorkspaceViewModel 改用 Application identity、Manage projection、navigation、
   details、comparison 和 workspace contracts。
3. Local inventory 与 Simulation 使用各自明确的 lifecycle，App 不再持有 Core
   `StorageSnapshot`、`StorageSystemDocument`、`StorageUnitRef`、
   `WorkspaceSelection` 或第二套 category。
4. `LegacyManage*Projector` 由基于新模型的正式 projector 取代。
5. `LegacySimulationEditCoordinator` 由 typed simulation runtime/edit/payload
   流程取代，不允许新模型再转换回 Core 继续业务运行。
6. Preferences 只保留 `WinPool.Domain` 定义。
7. startup、notification 和 layout 类型进入其明确的 App/Application 所属层。
8. 迁移有效 Core 测试，完成 semantic parity 后按特批删除 Core 项目及测试项目。

强制结构门：

```text
CoreProjectDoesNotExist
CoreTestsProjectDoesNotExist
NoProjectReferencesWinPoolCore
NoProductionSourceUsesWinPoolCoreNamespace
DomainHasNoApplicationDependency
AppManageStateUsesApplicationProjectionContracts
PreferencesHaveSingleDefinition
```

### V33-02：可恢复的 Agent shutdown 状态机

状态至少包括：

```text
Running → ShuttingDown → Stopped
                    └→ ShutdownPending → retry → ShuttingDown
```

- `ShuttingDown` 表示一个 workflow 正在执行。
- `ShutdownPending` 表示 workflow 已结束但仍有 terminal blocker。
- 同时最多一个真实 shutdown workflow。
- Pending 可观察上一轮 operation、步骤、失败、剩余进程和 retry capability。
- retry 只重做安全、幂等或尚未完成的步骤，不恢复为 Running。

Control listener 生命周期与业务状态分离：

- Running：正常请求；
- ShuttingDown：listener 继续存在，shutdown/status 请求 join/observe；
- ShutdownPending：listener 继续存在，只允许 snapshot/status/retry 等封闭请求；
- Stopped：才关闭 listener。

Shutdown 分为 quiesce phase 与 terminal exit phase。只有所有 terminal blocker 消失
后才允许 close remaining IPC、remove tray 和 exit Agent。

### V33-03：统一 Worker/Broker terminal lifecycle

收到 terminal IPC response 后统一执行：

```text
short exit grace → exited?
  yes → complete
  no  → kill process tree → bounded final wait → recorded outcome
```

- Worker 和 Broker 都不得使用无限 terminal `WaitForExitAsync(None)`。
- Broker 45 秒 deadline 覆盖整个生命周期。
- fault helper 覆盖 terminal-then-hang、crash、malformed response 等情况。
- fault helper 不得进入正式 staging。

### V33-04：关闭 Worker pre-start cancellation dead zone

- cancel 在 connect、handshake、start sent、tool preparing、tool running 各阶段都有效。
- tool 未启动时先发送 typed abort，grace 后终止 Worker tree。
- tool 已启动时发送 typed cancel，grace 后由 Job Object kill。
- caller cancellation 不得被无限 `ReadAsync(None)` 吞掉。

### V33-05：Control IPC 单连接故障隔离

一个 malformed/disconnected client 只能结束该连接。invalid frame、JSON、message
type、handshake、EOF 和 unsupported payload 必须记录诊断并继续 accept。只有 Agent
shutdown、listener 创建失败或明确不可恢复平台错误可以结束 server loop。

### V33-06：Event IPC 显式断线与恢复

- event reader 退出必须产生 disconnected/reconnecting/reconnected 状态。
- public watch channel 不得静默永久等待或终止。
- 在 reader task 外重建完整连接，避免 self-await deadlock。
- reconnect 后获取 snapshot 重新 seed 可恢复状态。
- 不实现 durable replay；断线期间的 gap 必须明确报告。

### V33-07：ProcessInstanceId 与有界 live registry

- PID 只用于 OS 查询，不再作为永久 identity。
- `ProcessInstanceId` 使用 Guid，并明确属于 Application process contract。
- live registry 以 instance ID 为主键，维护 PID → live instance 辅助索引。
- terminal process 从 live registry/PID index 移除，可进入有界诊断缓存。
- SQLite schema 从 v10 升级到 v11；保留旧 worker process 历史并为旧行生成唯一
  instance ID。
- migration 必须从真实 v10 数据库升级，保留 row count 和 evidence。

### V33-08：Tool 配置由 Agent 正常拥有

- 正常 App 通过封闭 request 设置/清除 tool path。
- Agent 验证 ToolId、绝对路径和 executable name，写入 active data root，重新检测
  并返回新的 ToolState。
- portable/standard 不产生两个活动配置副本。
- 只有明确 no-Agent development fallback 可以直接访问 active root JSON。

### V33-09：完整一致的工具输出编码

正确问题定义为：progress 路径已收到 encoding family，但 ANSI/OEM 仍按 UTF-8；
final parsing 还依赖 BOM/UTF-8 推断，两条路径不完整且不一致。

本轮必须建立：

- encoding family + resolved numeric code page；
- Worker 运行前一次解析并在同一 invocation 中固定；
- injectable code-page resolver；
- 固定位置注册需要的 code-page provider；
- stdout/stderr 各自独立的 stateful `Decoder`；
- EOF final flush 和确定的 invalid-sequence fallback；
- progress、final parser 和 human-readable evidence 使用同一次 resolution；
- raw stdout/stderr bytes 原样保存。

测试覆盖 UTF-8、UTF-16、ANSI/OEM DBCS 跨 chunk、中文 RoboCopy、stdout/stderr
交错和 EOF 才完成字符。

### V33-10：精确、可回滚且释放数据库句柄的数据迁移

完成后的 invariant：

```text
Target WinPool-managed payload manifest == Source manifest
```

迁移事务：

1. Plan：source manifest、count、bytes、per-file hash、manifest hash，并捕获 target。
2. Stage：在 target 同卷 sibling staging 完整复制并验证 path/size/hash/SQLite/reparse。
3. Quiesce：阻止新写入和新数据库连接，等待在途操作，flush SQLite。
4. Drain handles：关闭所有 active SQLite connection，释放**该 WinPool store 对应的**
   pooled/native handles，并验证 source database 可按迁移所需 share/exclusive 语义打开。
   不允许依赖进程级、并行测试不安全的全局 `ClearAllPools()` 作为产品正确性条件。
5. Re-snapshot：句柄释放后重新 snapshot；source 改变则 plan stale 并 abort。
6. Replace：旧 managed payload 移入 rollback，stage 移入 target，重新验证 exact manifest。
7. Commit：仅在 target 验证完成后 atomic commit `storage-location.json`。
8. Cleanup：保留 source；rollback/stage 清理由明确的 managed-path policy 处理。

未知或非 WinPool-managed target 内容不得静默删除；必须 abort 或进入可恢复的明确
quarantine/rollback，并留下诊断。

SQLite/handle 强制回归：

```text
QuiesceReleasesSourceDatabaseHandleBeforeSnapshot
MigrationDoesNotDependOnGlobalSqlitePoolCleanup
MigratedDatabaseCanBeReopenedImmediately
MigrationTemporaryRootsCanBeCleanedImmediately
SourceMutationAfterHandleDrainRejectsPlan
StaleOrdinaryTargetFileDoesNotSurviveManagedPayloadMigration
TargetManifestExactlyMatchesSource
PointerFailureRestoresPreviousTarget
CancellationRestoresPreviousTarget
HashMismatchRestoresPreviousTarget
SqliteVerificationFailureRestoresPreviousTarget
StandardToPortableToStandardRoundTripIsExact
```

### V33-11：适度拆分 DesktopAgentRuntime

在相应缺陷已有 regression tests 后，拆出三个内部 coordinator：

- `AgentTestCoordinator`；
- `AgentSystemSupportCoordinator`；
- `AgentInventoryCoordinator`。

不增加项目，不拆成几十个 handler。`DesktopAgentRuntime` 保留 request facade、
monitoring/tool delegation、event integration、shutdown adapter 和少量 aggregate
snapshot；`Program.cs` 只负责 construct、wire、start、shutdown、dispose。

## 7. Core retirement 独立验收类

### 7.1 Automatic semantic parity

使用同一组固定 fixture 对比 Legacy 与新实现的 system/object identity、relationship、
topology、category、ordering、display、selection、navigation、details、comparison 和
remembered-selection semantics。

Simulation 自动覆盖 create、edit、save、reload、edit again、conflict、delete、
Local → Simulation 新 `SystemId` 以及 payload round-trip。

Preferences 使用真实 V0.32 persisted values 验证 V0.33 load、UI reflect、modify、
restart、missing field 和 malformed/older value fallback。

Startup/single-instance 和 layout 的既有自动测试必须迁移保留。

### 7.2 Native/manual regression

Core migration 直接相关的人工门至少包括：

- Manage topology、category、selection、highlight、details、comparison、refresh；
- Simulation 创建、来源生成、编辑、保存、重启、重载、再编辑、删除；
- Manage/Edit 间 system/selection switching；
- Preferences 迁移后的 theme/language/value 保持；
- normal/second launch、startup arguments、activation、elevation handoff；
- 常见/narrow width、resize、high DPI 和 item count 变化后的 layout。

未执行项保持 `unverified`，是否接受该版本由用户决定。

## 8. 针对性 Native / Manual Gate

保留原 V33-M01～M10：

1. 正常 App + Agent 启动、连接、退出；
2. 活动测试时 tray Exit，确认后 Worker 正确取消并退出；
3. incomplete shutdown 后能够 retry 并完成退出；
4. UAC Broker 正常完成；
5. 用户取消 UAC 后 Agent 不进入失效状态；
6. Portable mode 设置 tool path 后 Agent 立即检测到；
7. Standard → Portable → Standard 数据位置精确往返；
8. target 有 stale managed file 时不存在 silent merge；
9. App 关闭而 Agent monitoring 持续时 event connection 行为正常；
10. event transport 重建后 UI 明确报告 gap/recovery。

上述项目与 Core retirement 独立验收类共同构成 V0.33 相关人工门，不要求顺带关闭
全部 V0.32 长期 UI/accessibility debt。

## 9. 实施顺序和提交边界

推荐顺序：

```text
V33-00 Plan/baseline
→ V33-10A SQLite handle/pool baseline restoration
→ V33-SUP-A model decision
→ V33-01 canonical models and Core retirement
→ V33-02/03/04/05/07 lifecycle and control/process identity
→ V33-06/08/09/10B event/config/encoding/exact migration
→ V33-11 runtime decomposition
→ closure gates and V0.33 verification
```

每个提交必须可独立 build/test。禁止一个提交先删除旧模型、下一提交才恢复编译。
路径移动、等价重构、行为修复、测试和最终版本/文档收口应保持可审查边界。

## 10. IPC、SQLite 和版本策略

- Wire shape 不兼容变化只在本阶段一次性 bump `IpcProtocol.CurrentVersion`。
- 不建立 v1/v2 adapter 或长期 legacy negotiation。
- App、Agent、Worker、Broker staging 必须使用匹配协议。
- Process identity migration 使用 SQLite schema v11，从真实 v10 事务升级。
- Schema revision 和 IPC version 是内部契约，不是另一套项目版本。
- 开发期间保持 V0.32；不得提前宣称 V0.33 是当前确认版本。
- V0.33 验收前将 `Directory.Build.props` iteration 从 2 改为 3，随后重跑
  完整门并验证四个 executable 都显示 V0.33。

## 11. 自动质量门

V0.33 验收版本必须运行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

并完成：

- `git diff --check`；
- Markdown relative-link validation；
- architecture boundary tests；
- fault-injection matrix；
- four-process staging；
- forbidden staging content validation；
- executable version verification。

要求 0 failed、0 skipped（环境门按 Quality 明确分类）、0 build warnings、0 build
errors、无已报告 vulnerable package；staging 不得包含 Core DLL、fault helper、重复
Worker/Broker、脚本、数据库、测试结果、外部工具或源艺术目录。

## 12. 执行停止条件

出现以下任一情况必须停止扩展并返回相应设计/实现步骤：

- 用户尚未确认本 Plan 或 `execution_authorized` 仍为 false；
- Simulation runtime/document exact contract 未确定；
- `SystemId` 与 document identity 再次成为同义字段；
- Local 与 Simulation 被迫共享 mutable revision lifecycle；
- `ShutdownPending` 后 Control pipe 不可连接；
- shutdown retry 可启动并发 workflow；
- ANSI/OEM 依赖测试机 locale 才能通过；
- stdout/stderr 仍无状态逐 chunk 解码；
- migration quiesce 后 SQLite 文件句柄仍未释放；
- Core 已删除但替代实现、有效测试或 parity evidence 不完整；
- version metadata 与文档/四进程显示不一致；
- 未获新授权却准备 push、tag、release、upload 或 deploy。

不得通过弱化 gate、删除有效测试或把失败改记为 debt 绕过停止条件。

## 13. Definition of Done

V0.33 进入版本评审前必须同时满足：

- Core 项目、测试项目和运行时依赖已按特批退役；
- Domain/Application/Execution ownership 和依赖方向正确；
- Local/Simulation identity、document、revision、payload 边界明确；
- Manage/Simulation/Preferences/Startup/Layout parity 有真实证据状态；
- shutdown pending 可观察、可 retry，terminal IPC 顺序正确；
- Worker/Broker terminal wait 有界，Worker 全阶段可取消；
- bad client 不终止 control server；event disconnect 可观察并恢复；
- PID reuse 安全，live registry 有界，SQLite v10→v11 保留历史；
- tool configuration 由 Agent 正常拥有；
- encoding resolution 在 progress/final/evidence 中一致且跨 chunk 正确；
- storage migration exact、可回滚、不会依赖未释放的 SQLite handle；
- 完整自动门达到要求；
- 所有 native/manual 项有 `passed/failed/unverified/...` 真实状态；
- 四进程版本一致为 V0.33；
- 未执行任何未授权 push/tag/release/upload/deploy。

## 14. 用户确认后的版本、文档和 Git 生命周期

只有用户明确接受 V0.33 版本后才能：

1. 把最终状态写入 README / README.zh-CN 和 CHANGELOG / reading copy；
2. 冻结 Plan 到 `docs/Archive/V0.33/`；
3. 更新 Archive 双语索引；
4. 没有下一活动阶段时移除 `docs/Plan.md`；
5. 创建正常 V0.33 本地版本提交。

任何未验证人工项继续保持 `unverified`。当前：

```text
local version commit: recorded by the rewritten V0.33 acceptance history
push: completed to origin/main
tag: not authorized
GitHub Release: not authorized
binary upload: not authorized
deployment: not authorized
remote_gate: passed
```

## 15. 执行记录

### 2026-08-11：V33-00 与 V33-10A

- 用户明确批准本 Plan 并要求开始执行；状态改为 `in_progress`。
- 开始基线：`d4979fa84d516ebecff16a089590ee8af8d6cc76`。
- 工具：.NET SDK `10.0.302`、PowerShell `7.6.4`、Git
  `2.54.0.windows.1`。
- 取消架构测试中“`docs/Plan.md` 必须不存在”的硬编码；活动 Plan 可选存在，
  存在时必须非空且仍由“最多一个活动 Plan”的目录结构保证唯一性。
- DEF-09 的进入基线为 Release 458 项中 457 passed、1 failed；失败来自
  `RealSqliteMigrationVerifiesLogicalIdentityBeforePointerCommit` 清理临时目录时
  `winpool.db` 仍被占用。
- V33-10A 关闭默认 SQLite pooling，迁移 quiesce 后对源数据库执行专属 pool
  drain 和独占读验证；回归测试不再调用进程级 `ClearAllPools()`。
- 针对性 `StorageLocationManagerTests`：12 passed、0 failed、0 skipped。
- 完整 Release test：458 passed、0 failed、0 skipped。
- Release build：0 warnings、0 errors。
- transitive vulnerable package audit：所有 33 个项目均无已报告易受攻击依赖。
- V33-10A 状态：`passed`。V33-10B 的 exact replacement、rollback 和 round-trip
  工作仍待执行。

### 2026-08-11：V33-SUP-A 与 V33-01 自动迁移门

- V33-SUP-A 已关闭，权威 aggregate、identity、revision、inventory version、source
  kind、payload 和 codec ownership 决定写入第 5.3 节。
- 原 `WinPool.Core` 的有效模型、投影、模拟、启动、通知与布局代码按职责迁入
  `WinPool.Application`；Domain 中的 execution/preferences/location 类型保持唯一。
- Manage projector、Simulation coordinator、PowerShell inventory provider 和 codec
  已移除 `Legacy*` 正常运行路径命名。
- `StorageSystemDocument` 现在显式区分 document ID、`SystemId`、document revision、
  inventory version、source kind 与 provenance。Local → Simulation 生成新的 document
  ID 和 `SystemId`。
- 有效 Core 测试迁入 `WinPool.Application.Tests`；solution、csproj 和生产源码均不再
  引用 `WinPool.Core`。
- `src/WinPool.Core` 与 `tests/WinPool.Core.Tests` 已从工作树移除。删除生成目录的
  shell 操作被本地安全策略拦截，因此仅剩的 `bin/obj` 已移动到可恢复的父项目
  `Rubbish/20260811_winpool_v033_core_generated/`，没有扩大删除范围。
- Application tests：38 passed；Infrastructure tests：39 passed；Architecture tests：
  27 passed；完整 Release test：459 passed、0 failed、0 skipped；Release build：
  0 warnings、0 errors。
- V33-01 automatic semantic/structure gate：`passed`。
- V33-01 native/manual regression：`unverified`，不得据此宣称 UI 视觉、DPI、启动、
  UAC 或设备行为已人工验收。

### 2026-08-11：V33-02～V33-05 生命周期与 Control IPC

- Agent shutdown 状态机新增 `ShutdownPending`。失败步骤或残留进程不再永久停在
  `ShuttingDown`；snapshot 保持可用，后续 shutdown request 可串行重试。
- named pipe、tray icon 和 Agent exit 移至 terminal phase；只有 quiesce 无失败且
  live process 清零后才执行，`Stopped` 才结束 Control listener。
- Worker/Broker 共用有界退出策略：短宽限、必要时终止进程树、最终有界等待；删除
  terminal response 后和清理路径中的无限 `WaitForExitAsync(None)`。
- Broker 的 45 秒 linked deadline 覆盖 terminal grace；最终 kill 后仍有独立的有界
  reap，不会因取消令牌使进程遗留。
- Worker transport connect/handshake 现在响应 caller cancellation；新增 typed
  `worker.command.abort`，覆盖 Start 前和 tool process 尚未启动阶段。发送 abort/cancel
  后等待 terminal response 也有 5 秒上限，超时进入进程树终止。
- 一个 invalid-length/malformed Control client 现在只结束当前连接；真实 named-pipe
  回归证明后续客户端仍能完成 handshake 和 snapshot request。
- Agent tests：67 passed、0 failed、0 skipped；Agent.Client tests：3 passed、0 failed、
  0 skipped。
- V33-02～V33-05 自动回归状态：`passed`；对应 native/manual 项仍为 `unverified`。

### 2026-08-11：V33-07 进程实例身份与 SQLite v11

- Application process contract 新增 Guid `ProcessInstanceId`；PID 只保留为 Windows
  process lookup 和当前 live instance 的辅助索引。
- Agent registry 以 instance ID 为主键。terminal instance 会立即离开 live registry
  和 PID index，因此同一 PID 后续可安全注册为新的 instance。
- terminal diagnostics 为独立的有界内存队列，最多保留 128 条；不会因长期运行而
  无界增长，也不会阻止 PID reuse。
- terminal TestWorker registration 仍会在离开 live registry 前形成完整结果并写入
  SQLite 历史，不以清理 live state 为代价丢失审计记录。
- SQLite schema 从 v10 升至 v11；`worker_processes` 以
  `process_instance_id` 为主键并保留 PID。真实 v10 结构迁移回归验证旧行获得唯一
  Guid、row count 与 session/correlation/ownership 字段保持不变。
- 本阶段唯一 wire bump 已执行：`IpcProtocol.CurrentVersion` 从 1 升至 2，覆盖新增
  process instance wire field 和 Worker Abort command；不建立长期 v1 negotiation。
- Agent tests：68 passed；Persistence tests：70 passed；IPC tests：10 passed；均为
  0 failed、0 skipped。V33-07 自动门：`passed`；native/manual 仍为 `unverified`。

### 2026-08-11：V33-06 Event IPC 断线恢复

- event reader 的异常 EOF、I/O、JSON 和 invalid frame 不再被静默吞掉；public watch
  channel 保持开放，并依次产生 `Disconnected`、`Reconnecting`、`Reconnected` 状态。
- transport 状态明确携带 `HasEventGap=true`，不伪造 durable replay 或声称补齐断线
  期间的事件。
- reader 先退出，再由 supervisor 在 reader 调用栈之外释放失败 transport、重建完整
  control/event connection，避免 self-await deadlock。
- 重连成功后主动请求 `GetAgentSnapshot`，通过既有 observe 流程重新 seed 可恢复状态，
  之后才报告 `Reconnected`。
- Test 页面显示断线/重连/已重新同步提示；Development 页面记录 transport state、
  gap 和诊断 code。
- 真实 named-pipe 回归会停止第一组 control/event server、启动替代 server，并验证
  三阶段状态顺序和 snapshot reseed。Agent.Client tests：4 passed、0 failed、0 skipped。
- V33-06 自动门：`passed`；V33-M09/M10 native/manual：`unverified`。

### 2026-08-11：V33-08 Agent-owned tool path configuration

- 新增封闭的 `ConfigureAgentToolPathRequest`；App 的选择、清除、Portable install 和
  MSI install 后路径登记均通过 Agent，不再由正常 App 直接写活动 JSON。
- Agent-side coordinator 校验 ToolId、绝对路径、登记的 executable filename 和文件
  存在性；随后原子写入 active data root 的 `tool-paths.json`、立即重新检测、持久化
  `ToolState` 并发布状态事件。
- Standard/Portable 的配置文件由 Agent 启动时解析出的唯一 active data root 决定，
  不再固定写 LocalAppData 副本。
- 仅在明确没有 Agent 的 development fallback 中允许 App 使用
  `StorageDataLocations.CurrentRoot` 直接访问 JSON。
- Portable installer 支持“只安装、不声明配置所有权”模式；正常 App 采用该模式后
  再交由 Agent 登记最终 executable path。既有独立 installer 场景默认行为不变。
- ToolManagement tests：28 passed；Agent tests：69 passed；均为 0 failed、0 skipped。
  V33-08 自动门：`passed`；V33-M06 native/manual：`unverified`。

### 2026-08-11：V33-09 一致的工具输出编码

- 新增 injectable `IToolOutputCodePageResolver`；Worker 在每次 invocation 启动前把
  UTF-8、UTF-16LE、System ANSI 或 OEM family 解析为固定 numeric code page，并把
  同一 resolution 附在该次 stdout/stderr 原始事件上。
- code-page provider 在唯一 decoder/resolver 边界注册；ANSI/OEM 不再回退为 UTF-8。
- stdout 与 stderr 各自使用独立、stateful `Decoder`，支持 multibyte character 跨
  chunk，并在 EOF 执行 final flush；非法尾序列统一使用 U+FFFD，结果确定且可测试。
- native progress parser 与 final adapter parser 都消费 Worker 记录的同一 numeric
  code page；SQLite result writer 从原始 Worker events 传递 resolution，不再靠 BOM/
  UTF-8 猜测。
- raw stdout/stderr bytes 的 Worker event、artifact 和 persistence 路径保持不变；
  文本只作为 progress/metric/evidence 的派生视图。
- 回归覆盖 CP936 中文 DBCS 字符中间分块、中文 RoboCopy summary、stdout/stderr
  交错、EOF invalid tail fallback 和 Worker resolution metadata。
- Testing.Tools tests：48 passed；TestWorker tests：10 passed；Persistence tests：
  70 passed；均为 0 failed、0 skipped。V33-09 自动门：`passed`。

### 2026-08-11：V33-10B 精确且可回滚的数据迁移

- `StorageLocationManager` 现在在目标同卷 sibling 目录构建 staging，捕获并复核源与
  目标，quiesce 后 drain 源 store 专属连接池并重新 snapshot，再把旧目标整体移入
  rollback、将 staging 原子移入目标。
- SQLite `-wal`、`-shm` 和 `-journal` 被明确视为 quiescence 控制的 transient sidecar，
  不进入 managed payload manifest；数据库本体和附件仍按 path、length、SHA-256 与
  manifest hash 精确验证，SQLite 另做 schema、row count、primary-key identity 检查。
- 目标替换后再次验证 exact manifest 与 SQLite identity，只有全部通过才提交
  `storage-location.json`；pointer、取消、hash 或 SQLite 校验失败都会恢复先前目标。
- 已覆盖计划列出的 12 项强制回归，包括 source mutation after drain、普通陈旧目标
  文件移除、临时目录即时清理和 Standard→Portable→Standard 精确往返。
- Persistence tests：81 passed、0 failed、0 skipped；完整 Release test：483 passed、
  0 failed、0 skipped；Release build：0 warnings、0 errors。
- V33-10B 自动门：`passed`；原生/人工数据位置迁移门仍为 `unverified`。

### 2026-08-11：V33-11 与 V0.33 验收版本

- 拆出 `AgentTestCoordinator`、`AgentSystemSupportCoordinator` 和
  `AgentInventoryCoordinator`。它们分别持有活动测试槽与取消状态、系统支持
  review/audit/elevated 流程、库存采集/比较与设备 ID 缓存。
- `DesktopAgentRuntime` 保留 request facade、monitoring/tool delegation、event
  integration、shutdown adapter 和测试执行 pipeline；没有新增项目或拆成 handler
  集合。新增 test-slot 状态回归和 coordinator 结构门。
- 按 `Va.bc` 规则把唯一项目 iteration 从 2 改为 3，因此当前项目版本为
  `V0.33`（`a=0`、`b=3`、`c=3`）；IPC 2 和 SQLite schema 11 仍只是内部契约。
- 串行执行 restore、Release test 和 Release build：486 passed、0 failed、0 skipped，
  0 warnings、0 errors；33 个项目的 transitive vulnerable package audit 无报告项。
- Markdown relative links 与 `git diff --check` 通过。四进程 staging 结构及禁止内容
  校验通过，App、Agent、TestWorker、Broker 的 ProductVersion 均为 `V0.33`、
  FileVersion 均为 `0.3.3.0`。
- staging 证据保存在父项目可恢复目录
  `Rubbish/20260811_winpool_v033_verification_staging/Program/WinPool/`，未纳入 Git。
- `code_gate: passed`；native/manual 项保持 `unverified`；状态进入
  `awaiting_acceptance`。未 push、tag、创建 GitHub Release、上传或部署。

### 2026-08-11：用户确认 V0.33

- 用户明确确认 V0.33，并授权归档活动 Plan、更新文档、提交 Git 和推送 `main`。
- V33-M01～M10 保持 `unverified`；确认版本不伪造原生 UI、托盘、UAC、
  设备、外部工具或数据位置人工证据。
- Plan 状态改为 `accepted`，权威归档位置为 `docs/Archive/V0.33/Plan.md`。
- 本次确认不授权 tag、GitHub Release、二进制上传或部署。
- 首次验收提交前 `remote_gate: pending`；推送成功后只补充远端证据并冻结归档。

### 2026-08-11：远端历史修正授权

- 用户要求从全部文档清除被错误引入的版本术语，并删除包含该错误的远端提交。
- 保留 V0.33 全部实现提交至 `0dcd22a`；版本提交重建为 `38ff043`，验收文档提交
  重建为 `e148b61`。
- 使用 `--force-with-lease` 保护远端并成功替换错误历史；`origin/main` 已指向修正后的
  V0.33 历史，`remote_gate: passed`。
- 未授权或创建 tag、GitHub Release，未上传发布包或部署。
