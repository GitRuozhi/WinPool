# WinPool V0.34 缺陷收口执行计划

## 0. 状态与授权

- 状态：`confirmed_execution`
- 用户确认：2026-08-11；授权本计划范围内的实现、验证与本地 Git 提交
- 目标项目版本：V0.34（`c=4`）
- 基线版本：V0.33
- 代码基线：`de6bae8ee530829e1e341896b7786167e23d132f`
- 计划来源：`docs/V0.34修BUG.md`
- IPC 目标协议：3（由当前 2 只提升一次）
- SQLite 目标 schema：12（新的严格数据合同；不提供旧 schema 迁移）
- remote gate：`not_required`

本文件是 V0.34 唯一的当前权威执行计划，基于对手工计划的代码核验形成。原始手工计划保持不动，不能把本计划写回 V0.33 归档历史。本次授权只覆盖本地实现、验证和有明确范围的本地 Git 提交；推送、打标签、GitHub Release、二进制上传和部署仍需单独授权。

当前工作树包含用户预先进行的 V0.33 文档迁移和未跟踪的原始 V0.34 计划。用户已授权先以这些文档、当前执行计划和必要入口文档形成恢复提交。后续每个提交仍必须路径限定，不得顺带纳入、撤销或覆盖无关改动。

## 1. 评审结论

原计划列出的十项缺陷均有合理修复价值，其中以下问题已在当前代码中直接确认：

1. Local Manage/Comparison capture 由调用方创建随机 `SystemId`，与 Local document identity 分叉。
2. TestWorker 与 ElevatedBroker 的 started callback 位于进程清理域之外。
3. TestWorker 的 `receiveBatch`、completion callback 和外层 scheduling restoration 都可能阻塞清理。
4. Main App handshake 由 Agent 临时创建 `ProcessInstanceId`；Registry 更新仍主要按 PID。
5. Storage pointer 提交后，`finally` 中目录删除异常可以覆盖成功结果。
6. Simulation repository 自增数据库 revision，而 Decode 用 payload revision 覆盖 JSON revision。
7. Agent event hub 和 Agent client 本地队列都使用 `DropOldest` 或忽略 `TryWrite` 失败。
8. reconnect 取得 snapshot 后只投影 process delta，没有完整 state replacement。
9. live progress 虽有两个 Decoder，却共用一个文本 buffer；`Complete` 直接丢弃 decoder 尾部。
10. `ShutdownPending` 已在 Agent 内部存在，但 snapshot 仍以 tray bool 表达 lifecycle。

原计划不能原样执行。本草稿补齐了真实 callback 范围、InstanceId 全链路、客户端 backpressure、旧数据明确拒绝策略、最小测试 seam，以及明确的 IPC/schema 目标版本。

## 2. 边界与非目标

- 不增加产品功能，不增加新项目，不引入 Actor、service bus 或通用进程框架。
- 不拆分整个 `DesktopAgentRuntime`；只允许为生命周期状态、进程 witness、清理和测试建立小型 internal helper/interface。
- 不启用真实磁盘、分区、卷、Storage Pool、Storage Tier 或 Virtual Disk 修改。
- 不增加 durable event replay，也不兼容 V0.33/V0.34 混合二进制。
- 不重新引入 `WinPool.Core`。
- 不重写 V0.33 归档和历史验收结果。
- 不迁移、修复、导入或自动删除 V0.33 及更早的 SQLite 数据库。V0.34 使用干净数据根；发现旧 schema 时明确拒绝并保持原文件不变。
- 不合并或删除既有 Local inventory 历史证据；V0.34 只保证当前 canonical identity 和此后新采集不再产生分叉。
- 不顺手处理与十项缺陷无关的 presentation 或架构债。

## 3. 必须保持的不变量

### 3.1 Child ownership

`Process.Start` 成功后立即进入 outer cleanup scope。started、batch、completion、IPC、持久化、取消或恢复失败均不能阻止：cooperative grace、process-tree kill、bounded final wait 和 terminal state 记录。

### 3.2 Process incarnation

`ProcessInstanceId` 是唯一生命周期身份；PID 只是 OS lookup key。Registry 的 heartbeat、stopping、terminalize 和 persistence 更新必须携带 InstanceId，并同时核对 expected PID。旧生命周期的迟到 callback 不得修改复用同一 PID 的新生命周期。

`ProcessRegistration.StartedAtUtc` 明确定义为 OS process birth witness（`Process.StartTime` 的 UTC 值），而不是 handshake/registration 时间。V0.34 优先复用现有字段，不为同一事实新增数据库列。

### 3.3 Local identity

对于 V0.34 当前 Local document 及此后新建的 native、legacy、Manage snapshot：

```text
document.SystemId == snapshot.SystemId == systems.system_id
```

同一 machine binding 的 refresh、App restart 和 Agent restart 必须复用该 identity。Local 转 Simulation 必须创建新的 Simulation `SystemId`，来源只作为 provenance。

V0.33 及更早的历史 snapshot 不静默重写、不删除。测试中的“无重复 Local row”限定为从干净数据库开始的 V0.34 连续采集，以及升级后不再新增随机 Local row。

### 3.4 Commit boundary

pointer commit 前失败可以 rollback 并返回 `Failed`/`Cancelled`/`Rejected`；pointer commit 后 active mode 必须是 target，清理失败只能返回 target state 加 `PartiallyCompleted`/warning，不能退化为 pre-commit failure。

### 3.5 Simulation revision

schema 12 的所有写入必须始终满足：

```text
typed document Revision
== payload Revision
== JSON Revision
== simulation_documents.revision
```

schema 11 及更早数据库不属于 V0.34 支持输入。初始化只能创建全新的 schema 12，或打开已经是 schema 12 的数据库；发现旧 schema 必须返回稳定、可诊断的 unsupported-data result，不能自动改写版本号、修复内容或删除文件。

### 3.6 Recovery and event loss

断线、server subscriber overflow 或 client subscriber backpressure 后，客户端必须收到一个完整的 snapshot replacement boundary，再处理后续 delta。任何队列都不得静默 `DropOldest`。

### 3.7 Shutdown authority

Agent lifecycle authority 是 Agent session state，不是 tray 私有 bool。外部状态为 `Running`、`ShuttingDown`、`ShutdownPending`、`Stopped`；Pending 保持 control transport，允许 snapshot 和串行 shutdown retry，拒绝新工作。

### 3.8 Tool stream isolation

每个 tool invocation 的 stdout/stderr 各自拥有 decoder 和 text buffer。EOF 必须 flush 两个 decoder；raw bytes 始终是最终证据。

## 4. 实施包 A：Local `SystemId`

1. 从 `CaptureAgentManageInventoryRequest` 和表示当前机器采集的 `CaptureAgentInventoryRequest` 删除 caller-provided `SystemId`。
2. Agent 先取得采集后的 sanitized Local document，并以其 `SystemId` 为 canonical identity。
3. 若已持久化 Local document 的 machine binding 与新采集一致，复用其 `SystemId`；仅首次采集或明确 machine-binding 不一致时采用新的 canonical identity。
4. Manage、native 和 legacy projection 全部接收同一个 canonical ID。
5. 保存前验证 document/snapshot identity；不一致时 fail closed，不写 SQLite。
6. 审计 App、Infrastructure、Development page 和 tray 中所有 Local capture 调用，禁止 UI/App mint Local identity。
7. 保留历史随机 system rows 及其 snapshot 引用，不做证据破坏式 merge。

Focused tests：连续 10 次 capture、Agent restart、native/legacy/Manage 一致、升级数据库后不再新增随机 Local row、Local→Simulation identity/provenance。

## 5. 实施包 B：Storage location commit boundary

1. 将 apply 明确分为 `PreCommit`、`Committed`、`PostCommitCleanup`。
2. 只有 pointer committer 成功后进入 `Committed`，立即移除 issued plan 并固定 target state。
3. post-commit cleanup 单独捕获并返回：

```text
ApplicationStatus.PartiallyCompleted
value = target StorageLocationState
message = storage.location.cleanup_pending
```

4. 引入最小 internal cleanup seam，使 staging/rollback 删除失败可确定性注入；不得建立通用文件系统框架。
5. 下次 manager 初始化或切换前只允许清理 configured target parent 下、名称严格匹配本次 manager 生成规则的 `.winpool-stage-*`/`.winpool-rollback-*` sibling root。必须再次验证 parent、完整路径和 reparse point；禁止接收 caller path 或通配删除。
6. pre-commit rollback 失败与 post-commit cleanup warning 分开报告；不得用 cleanup exception 覆盖 commit 事实。

Focused tests：pointer 前失败 rollback、pointer 后 staging/rollback 删除失败、target state/response 一致、遗留临时 root 安全重试、stale source/target、SQLite logical identity、Standard↔Portable exact roundtrip。

## 6. 实施包 C：Worker/Broker ownership 与 bounded callbacks

### 6.1 公共小型结构

允许增加一个 internal `StartedChildProcess`/等价 record，包含 InstanceId、PID、OS start witness 和 process handle；允许增加 bounded callback helper。不得增加新项目或通用 orchestration framework。

callback timeout 必须使用独立 lifecycle token，不能复用已经取消的用户 token。timeout 后先记录 diagnostic，再继续 reap。callback 实现必须接受并传递 token；late completion 不得把 terminal persistence 改回 Running。

Worker process persistence 必须增加单调状态约束：`Exited`/`Failed` 不能被迟到的 `Starting`/`Running` upsert 覆盖。

### 6.2 TestWorker

以下全部位于 outer cleanup scope 内并有明确 timeout：

- `workerStarted`
- `receiveBatch`
- `workerCompleting`
- outer scheduling restoration retry

`receiveBatch` 不再使用 `CancellationToken.None`。`TestProcessSchedulingScope.RestoreAsync` 增加 token 并由 bounded lifecycle deadline 调用；timeout/failure 时保留 recovery entry。

顺序：

```text
StartWorker
→ create child context
→ enter outer try/finally
→ started callback
→ auth/execute/batch callbacks
→ completing callback
finally → bounded restoration → bounded process-tree reap → terminalize by InstanceId
```

### 6.3 ElevatedBroker

当前 Broker host 只有 `brokerStarted`，没有 completion callback；计划不得虚构该接口。将 `brokerStarted` 移入 outer cleanup scope并设 timeout。Broker 结果返回前继续执行现有 bounded reap；Desktop runtime 的 terminalize 改为 InstanceId + expected PID。

为自动测试增加最小 broker launcher/process seam，避免单元测试触发真实 UAC；真实 UAC 仍属于 manual gate。

Focused tests：started throw/cancel/timeout、batch callback timeout、completion timeout、scheduling restore timeout、Completed-but-hangs、terminal state不回退、Broker started failure/timeout、无 orphan child。

## 7. 实施包 D：Main App incarnation 与 Registry 全链路

1. `NamedPipeAgentConnection` 在对象/进程生命周期内创建一次 client `ProcessInstanceId`，所有 reconnect 复用；新 App process 产生新 ID。
2. `AgentHandshakeRequest` 增加该 ID；Agent 不再为 Main App connection生成临时 ID。
3. `IpcProtocol.CurrentVersion` 从 2 一次提升到 3；V0.34 内不再二次提升。
4. Agent 通过可替换的小型 process-witness reader 读取 PID、规范化 executable path 和 OS start UTC；自动测试使用 fake，不依赖真实 PID reuse。
5. Registry 增加按 InstanceId 的 lookup/update/terminalize API；所有 PID-only mutation call site 必须迁移。PID index 只用于冲突检测和 OS lookup。
6. 判定：
   - 同 InstanceId + 同 PID + 同 birth witness：reconnect。
   - 同 InstanceId + 不同 PID：拒绝。
   - 不同 InstanceId + 同 PID + witness 仍等于旧注册：拒绝伪造的新 instance。
   - 不同 InstanceId + 同 PID + 旧 witness 已不存在/不同，且 incoming pipe PID/image/witness 有效：terminalize 旧 instance 后注册新 instance。
7. control pipe EOF 不等于 process exit；heartbeat sweep 与 shutdown 做 stale reconciliation。
8. 任何直接 signal/terminate 前重新验证 InstanceId 对应的 PID/image/start witness。不匹配时只 terminalize stale registration，绝不操作该 PID。

Focused tests：same-process reconnect、new process、same instance/different PID、PID reuse、stale image/start time、旧 callback 不影响新 instance、terminal diagnostics bounded。

## 8. 实施包 E：Simulation revision 与 schema 12 clean break

1. 新建要求 payload/JSON revision 均为 1。
2. 更新要求 payload/JSON revision 等于数据库 revision + 1，同时校验 previous SHA 和 previous revision。
3. SQL 使用显式 `$targetRevision`/`$previousRevision`，禁止 repository 自行 `revision + 1` 后忽略 caller payload。
4. Decode 验证 JSON revision 与 payload/DB revision；不一致直接 `InvalidDataException`，不再用 `with { Revision = ... }` 隐藏。
5. 审计所有 producer。尤其 built-in simulation refresh、import、普通 Save 和 structured edit 都必须显式生成连续 revision。
6. `WinPoolSqliteStore.InitializeAsync` 实施 clean-break policy：
   - 数据库文件不存在，或新建数据库确认没有任何用户表：创建全新 schema 12；
   - 已有数据库缺少 `schema_info`：视为 unknown legacy database，明确拒绝，不能把它当作空数据库初始化；
   - schema == 12：正常打开；
   - schema < 12：返回/抛出稳定的 `storage.schema.legacy_not_supported`，保持数据库、WAL、SHM 和 pointer 原样不动；
   - schema > 12：继续按 newer-schema fail closed；
   - 禁止把既有 schema 11 只改版本号伪装成 schema 12。
7. Standard/Portable location 中若存在旧数据库，location lookup 可以报告路径，但 Agent writer 不得打开或迁移其内容。用户应选择/准备一个干净数据根；该动作不得由 V0.34 静默执行。

Focused tests：create 1、reject create 0/2、1→2、reject jump、stale SHA/revision、JSON mismatch、roundtrip、fresh database creates schema 12、schema 12 reopen、schema 11 rejected without byte/file changes、newer schema rejected。

## 9. 实施包 F：外部 `ShutdownPending`

1. 在 Application 定义稳定 `AgentLifecycleState` 和 `AgentShutdownStatus`；failed steps 使用 Application-owned typed codes 或稳定 code strings，不引用 Agent implementation enum。
2. `AgentSnapshot` 增加 lifecycle/shutdown status；删除 `IsShuttingDown` 作为 authority 的用途。
3. 用一个小型 Agent lifecycle state store 供 `AgentSessionCoordinator` 写、snapshot/tray 读，避免 tray bool 与 coordinator 分叉。
4. `Running` 正常处理；`ShuttingDown` 只允许 snapshot，重复 shutdown 加入同一 gate；`ShutdownPending` 只允许 snapshot 和 shutdown retry；`Stopped` 才关闭 control transport。
5. status 至少包含 state、attempt time、failed-step codes、remaining process registrations/IDs、`CanRetry`。
6. tray label、retry availability 和 blocked commands 全部来自 authoritative status。

Focused tests：Running→ShuttingDown→Stopped、incomplete→Pending、snapshot details、Pending 拒绝新工作、并发 retry 只运行一个 workflow、成功 retry 最终退出。

## 10. 实施包 G：Event reseed 与两端 backpressure

1. `AgentSnapshot` 补齐 recovery 所需的 current tool states 和 shutdown status；保留 active test、monitor session、processes、latest samples 与 health events。
2. 增加明确的 `AgentStateReseedEvent(AgentSnapshot, reason, occurredAt)`（或等价稳定合同）。消费者收到后执行整体 replace，不把 snapshot 拆成 process delta。
3. reconnect 顺序：

```text
control handshake
→ event handshake/subscription established
→ pause delta projection
→ GetAgentSnapshot
→ publish one reseed replacement
→ release queued deltas in order
→ publish Reconnected
```

初始 connect 也必须缓存最新 snapshot，使之后才订阅的页面先获得 current reseed。

4. Agent `AgentEventHub`：改为 `FullMode.Wait`；`Publish.TryWrite == false` 时完成并移除该 subscriber，使 event pipe 最终 EOF。
5. Client：移除 `observedEvents` 的 `DropOldest` 和所有未检查的 `TryWrite`。建立小型 per-watcher fan-out；每个当前 watcher 收到同一事件。client-side backpressure 使用 awaited write，向 wire reader 传播；取消 watcher 必须解除阻塞。
6. 任一 event gap 都必须产生可观察 transport state，随后完整 reseed。不得出现 `Connected` 但缺 terminal state。
7. 不实现 durable replay；monitor sample coalescing留待后续。

Focused tests：EOF 状态序列、reconnect期间 test/monitor/tool/shutdown 改变、snapshot/delta 边界竞态、Agent subscriber overflow、client subscriber backpressure、两个 watcher 均收到同一 terminal event、overflow 后 reseed 恢复 terminal state。

## 11. 实施包 H：Tool progress stream isolation

1. parser state 以 `(RunId, StepId)` 分组，并在组内为 stdout/stderr 分别保存 Decoder、CodePage 和 StringBuilder。
2. percentage 匹配只在对应 stream buffer 内进行；两条流可共享 last-published fraction/time 去重状态，但不得共享可拼接 token 的文本。
3. `Complete` 分别调用两个 decoder 的 flush，将尾部文本送入对应 parser，再移除 state。
4. `Complete` 返回零个或多个 progress result；projector 在 `tool.process.exited` delta 前发布 EOF 产生的合法 progress。
5. live parser 不修改、替换或丢弃 raw bytes。

Focused tests：UTF-8/UTF-16LE/CP936/OEM chunk split、stdout `4` + stderr `2%` 不得产生 `42%`、两流独立 progress、EOF incomplete sequence flush、live/final decoder 对同一 raw fixture 的文本一致。

## 12. 实施顺序

1. V34-00：确认基线、记录 dirty worktree、确认本 Plan；先添加能在旧代码上失败的 regression tests。
2. V34-01：Local identity，停止产生新错误数据。
3. V34-02：storage commit boundary。
4. V34-03：child context、Registry InstanceId API、Main App handshake；在第一次 wire change 时将 IPC 设为 3。
5. V34-04：TestWorker 全 callback/restore bounded ownership。
6. V34-05：Broker ownership 和 test seam。
7. V34-06：建立 schema 12 clean-break policy 和严格 revision contract；旧数据库只拒绝、不迁移。
8. V34-07：authoritative shutdown snapshot。
9. V34-08：完整 snapshot/reseed 和 Agent/client backpressure。
10. V34-09：tool stream isolation/EOF flush。
11. V34-10：focused + full integration regression，不加入 unrelated refactor。
12. V34-11：candidate gate 通过后才把 `WinPoolVersionIteration` 从 3 改为 4，并重新执行完整 gate/staging。

每个实现 commit 必须 buildable/testable。commit 数量按真实边界决定，不为固定数量拆分。本计划授权本地 commit；push、tag 或 release 仍需另行取得用户授权。

## 13. 自动、静态与数据合同验收

从 repository root 执行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

记录实际 test count；skipped/unavailable 不得写 passed。Build 要求 0 errors；warning 必须修复、解释或取得 exception。

静态 gate：

- `WinPool.Core` 继续不存在。
- App 不写 SQLite；Agent 仍是唯一正常 writer。
- Domain/Application dependency direction 不回退。
- Worker/Broker 使用 typed IPC；无 free-form command。
- 所有 Registry mutation 使用 InstanceId + expected PID，不存在 PID-only terminalize。
- Local identity 不由 App/UI mint。
- 两端 event queue 均无 silent `DropOldest`/unchecked `TryWrite`。
- fresh database 直接创建 schema 12；schema 11 和 newer schema 均 fail closed，且拒绝过程不修改旧数据库及 sidecar files。
- real storage mutation、raw-device write、standalone inventory `.ps1` 继续禁止。
- redaction、external-tool adapter 和 matched-binary fail-closed 边界不变。

## 14. Native/manual targeted checks

固定根目录：`D:\WinPool-V03-Manual-Test`。

- M01：制造 incomplete shutdown；tray 保留、Pending 可见、新工作拒绝、retry 成功退出。
- M02：App open/close/reopen 与 event reconnect；无 ghost MainApplication。
- M03：registered test start/cancel；Worker 退出且 scheduling recovery 恢复或保留可恢复记录。
- M04：允许的 Broker support action；UAC 正确、Broker one-shot 退出、无残留 elevated process。
- M05：多次 Refresh + App/Agent restart；当前 Local system 不产生新 identity。
- M06：monitoring 中断开/恢复 event transport；reseed 后 UI 为当前状态。
- M07：Standard→Portable→Standard exact roundtrip；cleanup warning 情况下 UI/current pointer 仍一致。

没有用户/设备证据的 case 必须保持 `unverified`。V0.33 原有十项 manual case 不自动变为 passed。

## 15. Candidate staging 与文档收口

使用新目录运行 `build/Publish-Staged.ps1`。布局必须同时包含 App、Agent、TestWorker、ElevatedBroker，四者协议均为 3、product candidate 均显示 V0.34；不得混入 V0.33 child、数据库、日志、test results、external tools、local-only assets 或重复 child executable。

候选完成时：

- `Directory.Build.props` iteration 为 4。
- `WinPoolSqliteStore.CurrentSchemaVersion` 为 12。
- `IpcProtocol.CurrentVersion` 为 3。
- 修正 Development 中旧的 SQLite v10 描述，记录实际 schema 12；同步必要 reading copy。
- CHANGELOG 只记录实际发生的修复和 gate 结果。
- 用户确认前 README 只能称 `V0.34 candidate`，不能称 current user-confirmed version。

用户明确确认 V0.34 后，才更新 current version、把本 Plan 按真实最终状态冻结到 `docs/Archive/V0.34/`、更新 Archive index、移除 active Plan，并在另有 commit 授权时创建本地 checkpoint。push、tag、GitHub Release、binary upload、deployment 均需另外授权。

## 16. 完成定义

十项 defect 全部 closed；P1 不 defer，本计划 P2 也不默认 defer。完整 Release automatic gate、architecture gate、schema/data-contract gate 和 staging gate 通过。实际 manual case 如实记录，未执行为 `unverified`。

如果实现证明以下任一假设不成立，必须暂停并修订计划后取得用户决定：

- 现有 `StartedAtUtc` 无法安全承载 OS birth witness；
- clean-break 初始化无法在不改写旧数据库的前提下可靠识别 schema 11；
- client fan-out 需要改变 App 页面生命周期或扩大为通用事件框架；
- bounded callback 仍允许 terminal state 被 late write 回退；
- cleanup retry 无法仅凭 manager-owned、验证后的临时 root 安全执行。
