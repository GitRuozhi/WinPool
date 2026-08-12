# WinPool V0.36 缺陷收口执行计划

## 0. 状态、目标与基线

* **计划状态**：implemented / automatic gates passed / native-manual unverified
* **用户确认**：2026-08-12；授权将本文件作为唯一活动 `docs/Plan.md`，执行本计划，并在完成后创建 Git 提交。
* **目标版本**：V0.36
* **基线版本**：V0.35
* **目标分支**：`main`
* **计划性质**：V0.35 后续缺陷收口，不增加新功能，不扩大产品范围。
* **核心目标**：修复当前代码中仍可确认的并发、持久化、Schema 验证和历史身份兼容缺陷，并补齐对应 regression tests。
* **版本原则**：保持现有 IPC protocol、SQLite schema 版本和产品架构不变；本轮修复不得借机引入新的协议或数据迁移。

本计划不授权真实存储结构修改；所有实现和验证继续使用模拟、临时数据库、命名管道测试服务器和受控本地目录。

V0.35 已记录完成 507/507 Release 自动测试、0 warning / 0 error 构建、依赖审计和四进程 staging，但人工/原生场景仍有未验证项，因此 V0.36 的目标不是重新设计系统，而是关闭目前还能证明存在的实现边界。

**实施记录（2026-08-12）**：六项修复与确定性 regression 均已完成；完整
Release 测试为 511/511（0 skipped），Release build 为 0 warning / 0 error，
依赖审计未发现漏洞，并通过新的四进程 self-contained staging：
`D:\Coding\Research03_WinPool\Rubbish\20260812_winpool_v036_staging\Program\WinPool`。
第 7 节的 Windows 原生/人工场景本轮未执行，状态保持 `unverified`；本计划因此不是
用户验收或发布授权记录。

---

## 1. 本轮修复范围

V0.36 固定处理以下六项。

### W36-01：补全 SQLite schema 12 的结构完整性验证

**当前问题**

`CurrentSchemaVerifier` 当前只比较：

* tables
* columns
* indexes
* foreign keys

现有 `TableContract` 没有覆盖 table-level `CHECK` 等约束。

当前 schema 中已经存在：

```sql
singleton INTEGER PRIMARY KEY CHECK(singleton = 1)
```

等表级约束，因此 schema version 为 12 并不代表数据库真的满足完整 schema 12 contract。

**修复要求**

1. 为 schema verifier 增加表级结构合同。
2. 至少显式验证：

   * `CHECK` constraints；
   * `schema_info.singleton CHECK(singleton = 1)`；
   * `local_inventory_document.singleton CHECK(singleton = 1)`。
3. 评估并明确以下信息是否属于 schema 12 contract：

   * column collation；
   * index column order；
   * index sort direction；
   * expression/partial index；
   * generated/hidden columns。
4. 如果某类结构是运行时依赖的一部分，则必须纳入 verifier。
5. 任意 contract mismatch 继续：

   * 抛出 `CurrentSqliteSchemaCorruptException`；
   * 使用稳定错误码 `storage.schema.current_corrupt`；
   * 不 CREATE / ALTER / DROP / 自动修复；
   * 不修改 DB、`-wal`、`-shm`。

**Regression tests**

新增至少：

```text
schema12_without_schema_info_singleton_check_is_rejected
schema12_without_local_document_singleton_check_is_rejected
schema12_constraint_mismatch_preserves_database_bytes
```

现有缺 table / column / index / FK 测试继续保留。

---

### W36-02：关闭 `NamedPipeAgentConnection` Dispose 生命周期竞态

**当前问题**

`DisposeAsync()` 当前会直接 dispose：

```text
connectionGate
requestGate
eventRecoveryGate
```

而 `ConnectAsync()`、`SendConnectedAsync()` 和 event recovery 可能仍然持有或即将释放这些 semaphore。

存在：

```text
operation 获得 semaphore
→ DisposeAsync() dispose semaphore
→ operation finally Release()
→ ObjectDisposedException
```

以及 operation 已通过 `ThrowIfDisposed()` 后继续创建新 stream 的风险。

**修复要求**

1. 给 `NamedPipeAgentConnection` 增加 connection lifetime cancellation source。
2. `DisposeAsync()` 固定顺序：

   1. 原子标记 disposing/disposed；
   2. cancel lifetime；
   3. 阻止新 operation；
   4. 等待 Connect / Send / recovery / event reader 完成；
   5. dispose streams；
   6. 最后 dispose semaphore 和 CTS。
3. 所有长时间异步 operation 使用：

   * caller cancellation；
   * connection lifetime cancellation；
     的 linked token。
4. `ThrowIfDisposed()` 只能作为入口保护，不能作为整个生命周期正确性的唯一保证。
5. Dispose 必须幂等。
6. Dispose 与 recovery 同时发生时，不允许：

   * 创建新 control connection；
   * 创建新 event stream；
   * 发布新的 reconnect 状态；
   * 在 dispose 后重新设置 `stream` / `eventStream`。

**Regression tests**

新增确定性竞态测试：

```text
dispose_during_connect_does_not_throw_object_disposed_from_release
dispose_during_send_completes_cleanly
dispose_during_event_recovery_does_not_reconnect
double_dispose_is_idempotent
no_transport_is_created_after_lifetime_cancel
```

测试不得依赖随机 `Task.Delay()` 碰竞态，使用 `TaskCompletionSource` / injectable hook 精确控制时序。

---

### W36-03：区分 watcher 正常退订与真正 fanout overflow

**当前问题**

`AgentClientEventFanout.Publish()` 先复制 subscriber snapshot，随后在锁外 `TryWrite()`。

如果 watcher 在 snapshot 后正常 Dispose，其 channel 会完成；之后 `TryWrite()` 返回 false，目前会直接增加 `overflowed`。

连接层将 `HasEventGap` 解释为全局 event transport gap，并启动 reconnect。

因此正常 unsubscribe 有机会被误判为 watcher overflow。

**修复要求**

1. `Remove(Guid, Channel?)` 改为返回是否真正移除了当前 active subscription。
2. Publish 只有满足：

   * `TryWrite == false`
   * 且该 channel 仍是当前 active subscriber
   * 且本次 Publish 成功负责将其移除
     时，才增加 `OverflowedSubscriberCount`。
3. 正常 Dispose / unsubscribe：

   * 不增加 overflow；
   * 不设置 `HasEventGap`；
   * 不触发 connection recovery。
4. 真正容量溢出继续：

   * complete 慢 watcher；
   * 健康 watcher 保留当前事件；
   * connection 执行 authoritative reseed recovery。

**Regression tests**

```text
unsubscribe_racing_with_publish_is_not_reported_as_overflow
normal_watcher_dispose_does_not_trigger_reconnect
real_full_channel_still_reports_event_gap
one_disposed_and_one_slow_watcher_only_counts_real_overflow
healthy_watcher_continues_during_other_watcher_dispose
```

---

### W36-04：补全 `WorkerProcessRepository` 的单调持久化合同

**当前问题**

V0.35 已经让 `Exited` / `Failed` 成为 absorbing state，但同一个非终态的重复保存仍然允许覆盖全部字段。

例如：

```text
Running heartbeat 12:00:20 先写
Running heartbeat 12:00:10 后到
```

迟到写入仍可使 `last_heartbeat_utc_ms` 倒退。

同一个 `process_instance_id` 的 identity 字段理论上也可以被后来的 Save 覆盖。

**修复要求**

`process_instance_id` 一旦存在后：

### 不可变字段

必须保持完全一致：

* `process_id`
* `agent_session_id`
* `process_kind`
* `correlation_id`
* `started_at_utc_ms`
* `owns_job_object`，除非当前设计明确允许合法改变。

出现 identity mismatch 时不得静默改写。

建议返回新的 typed result，例如：

```text
Applied
IgnoredStale
RejectedIdentityMismatch
```

如不希望扩大 public contract，也至少保证数据库不会被改写，并产生明确 diagnostic。

### 单调字段

`last_heartbeat_utc_ms`：

```text
new = MAX(existing, incoming)
```

不得倒退。

`shutdown_deadline_utc_ms` 合同固定如下：

* 未进入 `Stopping` 时保持 `NULL`；
* `Running → Stopping` 时首次建立非空 deadline；
* `Stopping` 内只允许完全相同的 deadline，重试不得延长、缩短或清空它；
* `Stopping → Exited/Failed` 保留已建立的 deadline；
* 不得由迟到 `Running` 或其他写入清空、覆盖或延长 deadline。

新的 shutdown retry 应建立新的 shutdown attempt，不能改写已持久化的旧 attempt deadline。

### 状态

继续维持当前合法状态转移：

```text
Starting → Running → Stopping → Exited
Starting → Failed
Running → Unresponsive → Running
Running/Stopping/Unresponsive → Failed
```

终态继续 absorbing。

**Regression tests**

```text
same_state_older_heartbeat_cannot_move_timestamp_backward
same_state_newer_heartbeat_advances_timestamp
late_running_write_cannot_clear_stopping_deadline
same_instance_cannot_change_process_id
same_instance_cannot_change_agent_session
same_instance_cannot_change_started_at
terminal_state_remains_absorbing
```

---

### W36-05：修复 Local SystemId preferred document 的历史兼容漏洞

**当前问题**

Coordinator 已正确读取 Local document，并且仅当 document computer name 与当前机器一致时，才将其 `SystemId` 作为 preferred ID。

但 resolver 当前先按：

```sql
machine_binding_hash = current authority
OR display_name = current machine
```

筛 candidates，之后才从 candidates 里找 preferred ID。

如果历史 `systems` 行 metadata 已过时，但 Local document 明确指向该 SystemId，则 preferred identity 仍可能被排除。

**修复要求**

candidate resolution 顺序改成：

1. 如果存在 validated `preferredSystemId`：

   * 先按 `system_id = preferred` 且 `kind = Local` 独立查询；
   * 找到则作为最高优先级 canonical identity。
2. 再查 canonical authority binding。
3. 再查 legacy machine display name。
4. 最后才创建新 SystemId。

额外要求：

* preferred row 不要求旧 `display_name` 或旧 binding 已经符合当前规则；
* 不删除或 merge 历史 rows；
* 不修改历史 snapshots；
* 新 capture 从此使用 selected canonical ID；
* fragmented history warning 继续保留。

**Regression tests**

```text
preferred_document_id_wins_even_when_legacy_row_name_changed
preferred_document_id_wins_even_when_old_binding_is_noncanonical
preferred_id_of_non_local_system_is_rejected
missing_preferred_row_falls_back_to_authority_candidate
fragmented_history_selection_remains_deterministic
restart_without_document_keeps_canonical_authority_identity
```

---

### W36-06：统一 `ConnectAsync()` 的协议异常归一化

**当前问题**

`ConnectAsync()` 当前会将：

```text
IOException
InvalidDataException
InvalidOperationException
UnauthorizedAccessException
```

归一化为 `agent.connect.failed`，但 JSON payload 解析仍可能抛出 `JsonException`。

`SendConnectedAsync()` 已经处理 `JsonException`，两条 API 路径行为不一致。

**修复要求**

1. `ConnectAsync()` 把以下协议/环境级异常归一化：

   * `JsonException`
   * `NotSupportedException`
   * 当前已有 connection/protocol exceptions。
2. 所有失败都必须先释放部分创建的：

   * control stream；
   * event stream；
   * handshake state。
3. 不捕获：

   * `OutOfMemoryException`
   * `StackOverflowException`
   * `AccessViolationException`
     等不可恢复异常。
4. 保持 caller cancellation 返回 `Cancelled`。

**Regression tests**

```text
malformed_handshake_json_returns_agent_connect_failed
unsupported_payload_returns_agent_connect_failed
partial_event_connection_is_disposed_after_decode_failure
caller_cancellation_remains_cancelled
```

---

## 2. 明确不在 V0.36 做的事情

本轮禁止顺手扩大范围。

不做：

* IPC protocol 升级；
* SQLite schema 13；
* 数据迁移；
* 自动修复损坏数据库；
* 合并或删除历史 Local system rows；
* 新 Agent 功能；
* App 页面重构；
* storage model 大改；
* `WinPool.Core` 恢复；
* 新测试工具集成；
* 真实磁盘/分区/卷/Storage Spaces 修改能力；
* 与六项缺陷无关的大规模命名、格式或项目结构整理。

发现其它问题时：

* P0 / P1 且会阻塞本轮正确性的，可加入当前 Plan，但必须单独记录理由；
* P2 / P3 默认登记到下一轮，不扩大 V0.36。

---

## 3. 实施顺序

推荐按风险和依赖关系执行。

### V36-00：建立 regression baseline

先添加能够稳定复现本计划问题的失败测试。

要求：

* 测试必须先在 V0.35 当前实现上失败；
* 不允许一边改 production code 一边才补测试；
* race tests 必须 deterministic。

提交建议：

```text
test: reproduce V0.36 defect closure cases
```

---

### V36-01：Schema contract 补全

实现 W36-01。

原因：

* 独立性最高；
* 不影响 Agent runtime；
* 可优先确认数据库 fail-closed 边界。

提交建议：

```text
fix: complete schema 12 structural verification
```

---

### V36-02：Connection lifetime / Dispose

实现 W36-02。

先解决 connection lifetime ownership，再改 fanout/recovery，可避免两项并发修复互相干扰。

提交建议：

```text
fix: make Agent client connection disposal lifetime-safe
```

---

### V36-03：Watcher unsubscribe / overflow 判定

实现 W36-03。

提交建议：

```text
fix: distinguish event watcher disposal from overflow
```

---

### V36-04：Worker persistence monotonicity

实现 W36-04。

提交建议：

```text
fix: enforce monotonic worker process persistence
```

---

### V36-05：Local identity preferred-row fallback

实现 W36-05。

提交建议：

```text
fix: honor validated Local document identity across legacy metadata
```

---

### V36-06：Connect exception normalization

实现 W36-06。

改动较小，放在 connection 主体稳定后处理。

提交建议：

```text
fix: normalize Agent connect protocol failures
```

---

### V36-07：完整验证与版本收口

只有前六项全部通过后才：

1. 更新产品版本至 V0.36；
2. 执行完整 Release gate；
3. staging；
4. 更新 README / CHANGELOG；
5. 归档 Plan。

---

## 4. 最小 Regression Matrix

### SQLite

```text
valid schema 12
missing table
missing column
missing index
missing FK
missing CHECK
legacy version
future version
DB/WAL/SHM byte preservation
```

### Agent client lifecycle

```text
connect normally
send normally
dispose normally
dispose × connect
dispose × send
dispose × reconnect
dispose × event reader
double dispose
```

### Event fanout

```text
single healthy watcher
single slow watcher
multiple watchers
normal unsubscribe
unsubscribe × publish
slow watcher × healthy watcher
overflow → one recovery
old stream cannot publish after recovery
```

### Worker process persistence

```text
Starting → Running
Running → Stopping
Stopping → Exited
Running → Unresponsive → Running
terminal absorbing
older heartbeat ignored
newer heartbeat accepted
identity mutation rejected
late callback after terminal ignored
```

### Local identity

```text
comparison-first ×10
manage-first
comparison → manage
manage → comparison
restart with document
restart without document
legacy display-name candidate
canonical binding candidate
preferred document + stale row metadata
fragmented-history deterministic selection
Local != Simulation
```

### Connect failure handling

```text
bad control JSON
bad event JSON
bad message type
bad correlation
unsupported payload
server disconnect
caller cancellation
```

---

## 5. 自动质量门

从 repository root 执行：

```powershell
dotnet restore WinPool.slnx

dotnet test WinPool.slnx `
  -c Release `
  --no-restore `
  --maxcpucount:1 `
  -m:1

dotnet build WinPool.slnx `
  -c Release `
  --no-restore `
  -m:1

dotnet list WinPool.slnx package `
  --vulnerable `
  --include-transitive
```

要求：

* 全部 deterministic tests 通过；
* 0 errors；
* 0 warnings，或每一个 warning 有明确批准的解释；
* 不允许 skipped test 被统计为 passed；
* vulnerability audit 不出现未处置漏洞。

---

## 6. 并发专项压力验证

V0.36 比 V0.35 更需要做 targeted stress。

建议增加一个不进入普通单测的本地重复门：

```text
Dispose × Connect        1000 次
Dispose × Send           1000 次
unsubscribe × Publish    10000 次
worker stale persistence 10000 次随机顺序
Local identity capture   100 次 mixed order
```

验收条件：

```text
0 unhandled exception
0 ObjectDisposedException from semaphore Release
0 duplicate event recovery
0 stale heartbeat regression
0 Local SystemId fragmentation
```

随机压力测试只是补充，不能替代 deterministic regression test。

---

## 7. Windows 原生人工验证

V0.35 尚未验证的人工/原生场景不能继续默认继承为“通过”。

V0.36 至少手工验证：

1. App 正常启动 → Agent 连接。
2. 连续打开/关闭主窗口。
3. 连续进入/离开 Monitor 页面。
4. watcher 大量订阅/退订时 Agent 不重连。
5. App 退出过程中 Agent 正在 reconnect。
6. Agent tray exit。
7. active test 状态下 exit confirmation。
8. Agent restart 后 App 自动恢复连接。
9. Manage / Comparison 顺序交换后 Local SystemId 保持不变。
10. storage location 读取和切换 smoke，不进行任何真实存储结构修改。

所有未执行项明确记录 `unverified`。

---

## 8. Staging 验证

使用全新的 staging 目录。

检查：

* 唯一 `WinPool.App.exe`
* 唯一 `WinPool.Agent.exe`
* 唯一 TestWorker
* 唯一 Broker
* 所有 WinPool binary ProductVersion = `V0.36`
* IPC version 保持当前值
* SQLite schema 保持 `12`
* 不含：

  * `winpool.db`
  * `-wal`
  * `-shm`
  * 用户日志
  * 临时文件
  * 外部测试工具
  * `.ps1` 采集脚本
  * 重复 child binaries

---

## 9. 验收标准

V0.36 只有同时满足以下条件才可标记 accepted：

* [x] W36-01 完成并有 CHECK/constraint regression。
* [x] W36-02 Dispose × Connect/Send/Recovery deterministic tests 通过。
* [x] W36-03 正常 watcher unsubscribe 不再触发 recovery。
* [x] W36-04 heartbeat 和 identity persistence 保持单调。
* [x] W36-05 preferred Local document ID 可接管旧 metadata row。
* [x] W36-06 malformed protocol 不再逃出未归一化异常。
* [x] 完整 Release tests 全通过（511/511，0 skipped）。
* [x] Release build 0 error。
* [x] warnings 已清零（0 warning）。
* [x] dependency vulnerability gate 通过。
* [x] 新 staging 通过，四个 process executable 均为 `V0.36`。
* [x] 人工/原生验证结果如实记录，未验证项保持 `unverified`。
* [x] README / CHANGELOG 与真实状态一致。
* [x] 没有借 V0.36 引入 schema / IPC / feature scope 扩张。

---

## 10. 收口原则

本轮最重要的不是继续无限扫描，而是把已经发现的边界真正封死。

V0.36 完成后：

* 如果没有新的 P0/P1 证据，不继续以“寻找所有可能 Bug”为理由无限扩大版本；
* 后续缺陷进入新的 Plan；
* 性能优化、代码美化、命名整理和架构偏好不得伪装成 defect fix；
* 自动测试通过只代表自动门通过，不代表未执行的 Windows 原生/人工场景通过；
* 所有历史错误和修复过程继续如实保留，不重写为“从未存在过”。

V0.36 的完成定义不是“相信代码已经没有 Bug”，而是：

> **当前已知且能够稳定证明的六项缺陷全部获得实现修复、确定性 regression、完整自动门和真实状态记录。**
