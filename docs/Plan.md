# WinPool V0.35 缺陷收口执行计划

## 0. 状态、授权与基线

- 状态：`confirmed_execution`
- 用户确认：2026-08-12；授权本计划范围内的实现、验证与路径限定的本地 Git 提交
- 目标项目版本：V0.35（`c=5`）
- 基线版本：V0.34
- 代码基线：`5cc676b4873c29e7b00af9fd48e689627eb1dad4`
- 计划来源：`docs/V0.35补充.txt`（原始手工补充保持不改）
- IPC：保持 protocol 3
- SQLite：保持 schema 12；只增加只读结构验证，不迁移、不自动修复
- remote gate：`not_required`

本文件是 V0.35 唯一活动且权威的执行计划。它将手工补充中已确认的六项缺陷改写为可验证的
实现合同，并补齐对当前 V0.34 代码必要的并发、身份和提交边界。授权不包括 push、tag、
GitHub Release、二进制上传、部署或真实存储结构修改。

## 1. 范围与不变量

本轮只修复以下缺陷：

1. comparison-first 会造成 Local `SystemId` fragmentation；
2. 一个慢的 App-side event watcher 会阻塞 event transport；
3. `worker_processes` 的终态可被迟到持久化写回非终态；
4. shutdown step 的 timeout 只取消 token，不能保证 workflow 有界前进；
5. schema version 为 12 时未校验实际 schema 12 结构；
6. Main App shutdown 的最后存活检查退化为 PID-only。

不得增加项目、重构 App 页面、改变 IPC wire contract、改变 schema 12、迁移/合并/删除旧数据、
恢复 `WinPool.Core`，或启用任何真实磁盘、分区、卷、Pool、Tier、Virtual Disk 修改。

M01--M07、V0.34 原生/人工矩阵和本计划新增 M01--M04 在没有真实证据时均保持
`unverified`。

## 2. 已确认的实现决策

### 2.1 Local identity 的权威键与接管

当前 Native inventory 的 `MachineBinding` 仅基于 computer name，而 Embedded/Manage inventory
基于 stable ID + computer name；二者不能直接相等。因此 V0.35 不把 collector snapshot 的
`MachineBinding` 当作 Local identity authority。

- 新的 Local authority key 固定为
  `MachineBinding.Create([Environment.MachineName])`；它仅用于 Local `systems.machine_binding_hash`
  的 canonical 系统行，不重写 snapshot 自身的 provider binding。
- `InventorySnapshotRepository` 为 Local save 接收显式的 canonical system-binding 参数；Manage、
  Native、Legacy comparison 均以该 authority key 写其 `systems` 行。Simulation、Imported、Replay
  不使用此路径。
- `LocalSystemIdentityResolver` 位于 `WinPool.Infrastructure.Sqlite`，由 Agent 使用。它以事务读取
  Local rows，并返回唯一 canonical `SystemId`。首次建立与其系统行 insert 必须在同一 SQLite
  transaction 内完成；这取代仅靠进程内 `SemaphoreSlim` 的正确性保证。协调器仍使用一把
  `localCaptureGate` 覆盖收集完成、解析、snapshot persistence、Manage document persistence，防止
  比较结果和 document 的交叉写入。
- 接管顺序固定为：① 当前 Local document 解码出的 ID，前提是其 document computer name 等于
  当前 `Environment.MachineName`（不区分大小写）；② `kind=Local` 且 canonical authority key 匹配的
  rows；③ V0.34 遗留的 `kind=Local` 且 display name 等于当前 machine name 的 rows；④ 创建新 ID。
  多个候选按 `created_at_utc_ms`、`system_id` 稳定排序；document 指向候选时优先 document。
- 选择多个遗留候选之一时，Agent response 增加一条 warning `agent.inventory.local_identity_fragmented`。
  不删除、不 merge、不篡改历史 snapshot；从此以后新 capture 只使用被选择的 ID。
- `LocalInventoryDocument` 写入失败不得影响已建立的 Local identity；下次 capture 必须仍解析到
  同一 `systems` 行。Manage save 前仍严格验证 document、projected snapshot 与 persisted snapshot
  的 `SystemId` 一致。

### 2.2 Event watcher 隔离与唯一重连

- `AgentClientEventFanout.Publish` 为同步、非等待式：对每个 watcher 仅 `TryWrite`。溢出的 watcher
  从 fanout 移除并 complete；健康 watcher 不丢当前事件，transport reader 不等待任何 watcher。
- watcher overflow 是 connection-level event gap。fanout 返回 publish result；连接对象通过一个唯一的
  reconnect supervisor/gate 关闭当前 event transport、取得新的 control/event connection、请求
  authoritative snapshot，并按 `Disconnected → Reconnecting → AgentStateReseedEvent → Reconnected`
  发布给仍存活的 watcher。
- 同一代 stream 只能触发一次 recovery；旧 stream/read task 不得在新 generation 发布事件或再启动
  reconnect。connection dispose 取消 recovery。不得出现 `DropOldest` 或 transport reader 内等待
  watcher `WriteAsync`。
- latest snapshot 只代表连接或恢复时的 authoritative snapshot。新 watcher 先收到这个 reseed，随后
  接收新的 delta；不得把 delta 伪装成 snapshot，也不得承诺未实现的 durable replay。

### 2.3 `worker_processes` 的单调状态合同

`Exited` 与 `Failed` 是 absorbing。合法转移仅为：

```text
Starting → Running → Stopping → Exited
Starting → Failed
Running → Unresponsive → Running
Running/Stopping/Unresponsive → Failed
```

- `WorkerProcessRepository` 是最终裁决者，不信任调用者。
- 用一条 SQLite guarded UPSERT/UPDATE 原子判断转移，避免 `SELECT → validate → UPDATE` 竞态。
- save 返回 `Applied` 或 `IgnoredStale`；迟到 callback 的 stale 写入是无害 no-op，不抛异常也不改变
  registry 或 workflow 结果。
- 此合同适用于通过该 repository 持久化的 TestWorker、Broker、ExternalTool、MainApplication，不能只
  修一个 worker 调用点。

### 2.4 Shutdown 的 deadline 与 attempt fence

- 每个 shutdown execution 分配单调 `attemptId`。`RunBoundedShutdownStepAsync` 创建 operation task，
  以 deadline `WaitAsync` 等待；timeout 时取消 step token、记录 failed、附加只观察 continuation，
  随即让 workflow 前进，绝不等待不合作 operation。
- operation 本身的迟到完成不能写 workflow-local `restored`、`flushedCount` 或 completed/failed
  列表；这些结果只在 deadline 内、当前 attempt 的 wrapper 内提交。
- `CloseNamedPipes`、`RemoveTrayIcon`、`ExitAgent` 是 terminal actions。它们在不可逆动作前必须检查
  当前 attempt 的 commit fence 与 token；timeout、过期 attempt 或新的 retry 均禁止旧操作提交 terminal
  effect。可重复 safety actions 可在后台自然完成，但不能把 lifecycle 从 `ShutdownPending` 改成
  `Stopped`。
- `AgentSessionCoordinator.shutdownGate` 继续序列化 retry；只有成功的当前 execution 才可使 lifecycle
  进入 `Stopped`。

### 2.5 Schema 12 的只读结构合同

- `CurrentSchemaVerifier` 使用 immutable read-only connection；仅在 version=12 时运行。
- 合同在代码中精确枚举所有运行时依赖表、列（名称、类型、NOT NULL、PK）、关键 index（名称与列序）
  和关键 FK（来源列、目标、`ON DELETE`）。不得使用“至少检查”或只验证表存在。
- schema 12 但结构不符时抛出 stable code `storage.schema.current_corrupt` 并 fail closed；不得
  CREATE、ALTER、DROP、补列、改 `schema_info`、删除 DB，且 DB、`-wal`、`-shm` 均保持字节不变。

### 2.6 Main App process incarnation

- 建立可注入的 `IProcessIncarnationVerifier`，一次读取并比较 PID、标准化 image path 与 UTC start
  witness。预期 App path 的唯一来源为 Agent publish layout 的 `../WinPool.App.exe`。
- handshake 和 `CloseMainApplicationAsync` 使用同一 verifier。shutdown 中 PID 缺失、start witness 不同或
  image 不同均表示旧 registration 已退出/stale；只 terminalize 旧 registration，绝不 kill 或等待新进程。

## 3. 实施与提交顺序

1. V35-00：建立本 Plan、记录手工来源；先添加会在 V0.34 失败的 focused regression tests。
2. V35-01：Local authority-key resolver、原子首次建立、capture serialization 与 managed document
   consistency。
3. V35-02：非阻塞 watcher fanout、generation-safe gap recovery 与 reseed。
4. V35-03：worker-process guarded persistence state machine。
5. V35-04：bounded shutdown step、attempt fence 与 late-effect tests。
6. V35-05：统一 Main App process-incarnation verifier。
7. V35-06：schema-12 structural verifier 与 immutable no-change tests。
8. V35-07：完整 gate；通过后才把 iteration 由 4 改为 5，重新完整验证并执行 staging。

每个 implementation commit 必须 buildable/testable；只提交本计划路径。原始
`docs/V0.35补充.txt` 保持原样，不纳入历史改写。

## 4. 最小 regression matrix

- identity：comparison-first ×10；comparison→manage；manage→comparison；无 Manage document 的
  Agent restart；并发 Manage/Comparison；fragmented-history stable selection；Local≠Simulation。
- events：一个/多个不消费 watcher；健康 watcher 继续；overflow 触发唯一 reconnect/reseed；重新订阅
  首项为 cached reseed；连续 overflow 不生成双 reader/旧流写入。
- persistence：`Exited→Running` 与 `Failed→Starting` ignored；`Running→Unresponsive→Running` applied；
  terminal 后 late callback 与 registry/DB 一致。
- shutdown：完全不响应 token 的 operation；late completion；terminal action timeout 后不得退出 Agent；
  retry serialization。
- witness：same incarnation、PID missing、PID reused、image mismatch、start mismatch。
- schema：valid 12、缺 table/column/index/FK、legacy、future；所有拒绝 case 的 DB/`-wal`/`-shm` 未变。

## 5. 验收与收口

从 repository root：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

所有 deterministic test 必须完成；skipped/unavailable 不记为 passed；build 必须 0 errors，warning 必须
修复或解释。candidate staging 必须以新目录运行 `build/Publish-Staged.ps1`，并验证唯一的 App、Agent、
TestWorker、Broker 全部 ProductVersion=V0.35，IPC=3，schema=12，且不含 DB、日志、脚本、外部工具或
重复 child。

用户确认 V0.35 前，README 和 CHANGELOG 只能称 `V0.35 candidate`。确认后才更新 current version、冻结
本 Plan 至 `docs/Archive/V0.35/` 并删除 active Plan。push、tag、release、binary upload 和 deployment
仍需要新的明确授权。
