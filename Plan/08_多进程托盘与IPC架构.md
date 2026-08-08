# V0.2 多进程、托盘与 IPC 架构

## 1. 目标

监控不能依赖主界面进程存活。V0.2 使用可见托盘 Agent 作为用户会话内的后台协调者：

- 关闭主界面后，托盘图标仍存在，监控可以继续。
- 不安装 Windows Service。
- 不允许完全静默后台运行。
- 从托盘选择“退出 WinPool”后，完整退出所有 WinPool 进程及其外部工具子进程。
- 主界面与 Agent 通过命名管道通信。
- 架构允许按隔离、权限和稳定性需要增加其他短生命周期进程。

## 2. 进程清单

### 2.1 `WinPool.Agent`

常驻当前用户会话，拥有：

- 托盘图标和托盘菜单；
- Agent 单实例；
- 后台监控会话；
- SQLite 主写入队列；
- 进程监督；
- 命名管道服务器；
- 工具与 Worker 状态；
- 主界面启动/激活；
- 完整退出协调；
- 电源计划和临时系统状态的崩溃恢复。

Agent 不是 Windows Service，不跨用户会话，不使用 LocalSystem。

### 2.2 `WinPool.App`

WinUI 主界面，拥有：

- 标题栏和全部页面；
- UI 状态和展示；
- 用户计划审阅和确认；
- Agent 查询和事件订阅；
- 打开/关闭窗口。

关闭窗口只关闭 `WinPool.App`。只要 Agent 托盘仍存在，WinPool 仍在运行。

### 2.3 `WinPool.TestWorker`

按测试运行创建，拥有：

- 调用 DiskSpd、RoboCopy、fio 和其他不需要独立提权的已配置工具；
- 受限测试工作目录；
- stdout/stderr 和结构化结果收集；
- 进程树 Job Object；
- 解析器和运行级缓冲；
- 向 Agent 发送测试事件。

外部工具崩溃或解析器异常不应终止 Agent 或主界面。

允许一个 Worker 管理一个测试 run；未来并行测试可使用多个 Worker，但必须经过资源冲突检查。

### 2.4 `WinPool.InventoryWorker`

可选短生命周期进程，用于隔离：

- 当前 PowerShell 采集；
- KS 参考脚本；
- 后续原生采集器；
- 可能长时间运行或产生大量输出的诊断。

如果实测证明进程隔离没有价值，可与 Agent 合并；Application contract 不变。

### 2.5 `WinPool.ElevatedBroker`

一次性提权进程，不常驻，拥有严格受限的类型化能力：

- 安装用户确认的外部工具；
- 执行需要提升权限的 Flush、TRIM/Optimize；
- 执行已批准的 RAMMap 固定缓存/standby list 清理模式；
- 执行已批准的临时文件清理范围；
- 应用和恢复临时电源计划；
- 其他未来明确批准的 R3 动作。

禁止：

- 通用命令行；
- 任意 PowerShell；
- 存储结构修改；
- 在任务结束后继续驻留；
- 自行启动其他未登记 Broker。

## 3. 托盘行为

托盘菜单至少包含：

- 打开 WinPool；
- 当前监控状态；
- 开始/停止监控；
- 当前测试状态；
- 打开 Test/Monitor 页面；
- 暂停或取消测试（按工具能力显示）；
- 设置；
- 退出 WinPool。

要求：

1. Agent 运行时托盘图标必须可见。
2. 托盘图标创建失败时，Agent 不能静默运行；必须启动主界面报告错误或退出。
3. 鼠标悬停显示当前监控/测试摘要。
4. 双击托盘图标打开或激活主界面。
5. 关闭主界面不默认停止监控或测试。
6. Windows 登录启动是可选设置，不默认替用户开启。

## 4. 完整退出

托盘“退出 WinPool”是唯一的完整退出入口。

退出顺序：

```text
Tray Exit
  -> mark Agent as shutting down
  -> reject new UI/worker requests
  -> notify App and Workers
  -> if tests are active, show confirmation
  -> request test cancellation
  -> terminate external tool Job Objects after timeout
  -> stop monitoring
  -> restore priority/affinity/power plan
  -> flush SQLite queues
  -> close named pipes
  -> close App
  -> stop Workers/Broker
  -> remove tray icon
  -> exit Agent
```

如果存在活动测试：

- 托盘显示测试名称、步骤和已写入数据。
- 用户确认完整退出后必须取消测试。
- 取消失败时在固定超时后结束整个工具进程树。
- 已完成和部分完成证据在 Agent 退出前 flush。

Agent 下一次启动检查上次未完成 shutdown，并尝试恢复临时电源计划及标记孤儿测试。

## 5. 主界面启动和关闭

启动流程：

1. 启动器寻找当前用户 Agent 命名管道。
2. 不存在时启动 `WinPool.Agent`。
3. 完成握手后请求 Agent 打开 `WinPool.App`。
4. App 连接 Agent 并恢复工作区。
5. Agent 已有 App 时只激活现有窗口。

关闭流程：

1. App 保存 UI 状态。
2. 取消仅属于 App 的查询和订阅。
3. 断开命名管道。
4. App 退出。
5. Agent、监控、测试和托盘继续。

Execution mode 是否由 Agent 保存仍遵循“不跨完整启动持久化”。App 重新打开时从 Agent
当前会话读取；从托盘完整退出并重新启动后回到 Simulation。

## 6. 命名管道

### 6.1 管道

建议：

- `WinPool.Agent.Control.<UserSidHash>`
- `WinPool.Agent.Events.<UserSidHash>.<ConnectionId>`
- `WinPool.Worker.<RunId>`
- `WinPool.Broker.<Nonce>`

实际名称包含不可预测 nonce，公开前缀不承担身份验证。

### 6.2 访问控制

- ACL 仅允许当前用户 SID 和必要的已提权同一用户 token。
- 拒绝其他交互用户和远程访问。
- Broker 必须核对调用者 SID、nonce、计划哈希和过期时间。
- 每个连接先完成版本握手和进程身份验证。
- 不通过管道发送明文硬件序列号或可重放授权令牌。

### 6.3 消息封装

```csharp
internal sealed record IpcEnvelope(
    int ProtocolVersion,
    Guid MessageId,
    Guid CorrelationId,
    string MessageType,
    DateTimeOffset SentAt,
    JsonElement Payload);
```

内部 IPC 不冻结为公共 API。协议版本只用于同一次开发周期中的兼容检查。

消息类型包括：

- request/reply；
- subscribe/unsubscribe；
- monitor samples；
- test progress；
- notifications；
- tool state；
- process state；
- shutdown；
- heartbeat。

## 7. 断线与重连

- App 断线后指数退避重连 Agent。
- Agent 不因 App 断线停止监控。
- Worker 与 Agent 断线时先缓冲有限事件和原始工具输出。
- 缓冲达到上限时优先保留错误、状态变化和最终指标，记录丢弃数量。
- Agent 重启后不自动恢复正在写入的外部测试；标记 interrupted，等待用户处理。
- App 重连后先获取快照，再订阅增量事件，避免状态缺口。

当前实现为每个通过控制管道认证的 App 连接生成独立
`WinPool.Agent.Events.<UserSidHash>.<ConnectionId>.<Nonce>` 管道。事件管道再次核对协议、
ConnectionId、nonce、客户端声明 PID、命名管道实际客户端 PID、固定 App 映像和 30 秒连接
时效；控制连接断开时同步取消对应事件端点。Agent 使用每订阅者有界、丢弃最旧项的队列，
控制 request/reply 与异步事件帧不共用读取顺序。测试工具进度事件只携带运行/步骤身份、
固定代码、状态和 0–1 比例，不携带 stdout/stderr 原文或目标路径。

## 8. SQLite 所有权

- Agent 是数据库主写入者。
- App 通过 Agent 查询；必要的只读 SQLite 快照访问必须经过单独评估。
- Worker 不直接持有长期数据库写连接。
- Worker 把结构化批次发送给 Agent。
- Agent 使用单写入队列和批事务。
- Agent 完整退出前 flush。

这样可避免主界面关闭导致写入中断，也减少多进程 SQLite 写竞争。

完整退出的每个清理步骤必须有固定超时。单一步骤超时不得无限阻塞后续的监控停止、
恢复、SQLite flush 和进程回收；仍有受监督进程无法安全确认退出时，Agent 必须保留
可见托盘并报告“退出未完成”，不得移除图标后转为静默残留进程。

## 9. 外部工具进程树

Test Worker 为每个运行建立 Windows Job Object：

- DiskSpd、fio、RoboCopy 及其子进程加入同一 Job。
- 记录 PID、启动时间、路径、版本和 SHA-256。
- 取消先发送适合工具的正常终止请求。
- 超时后终止 Job Object。
- 主界面退出不终止 Job。
- Agent 完整退出必须终止所有 Job。

需要提权的 RAMMap 由一次性 Broker 在独立受监督 Job 中启动；Agent 把该 Job
绑定到同一测试 run。托盘完整退出时必须等待其证据 flush，超时后结束 RAMMap
进程树。RAMMap 路径、版本、SHA-256、固定参数、PID 和退出码进入同一运行审计。

自定义路径的工具同样遵守上述监督，不因用户选择路径而获得通用系统访问授权。

## 10. 提权 Broker

Broker 请求至少包含：

- Broker nonce；
- 调用用户 SID；
- `PlanHash`；
- 类型化操作；
- 目标；
- 参数；
- 过期时间；
- Agent PID 和会话。

Broker 内部维护允许动作表，不接受可执行文件路径和自由参数作为通用安装/执行请求。

工具安装是例外但仍受安装计划限制：

- 工具 ID；
- 官方来源；
- 下载文件哈希；
- 安装器类型；
- 目标位置；
- 安装参数模板。

RAMMap 执行不是通用工具执行例外。Broker 只接受 `RamMapCacheClearMode` 白名单值，
在内部解析已检测并锁定身份的 RAMMap 路径，生成固定参数数组；调用者不能提交
可执行文件路径、整行命令或额外参数。

## 11. 进程健康

Agent 维护进程注册表：

```text
process_id
process_kind
correlation_id
started_at
last_heartbeat
state
owned_job_object
shutdown_deadline
```

- App、Worker 每 5–15 秒心跳，具体值通过测试确定。
- Broker 生命周期短，不作为长期心跳对象。
- 失联 Worker 标记并回收进程树。
- Agent 不自动无限重启崩溃 Worker。
- 连续崩溃进入熔断并通知用户。

## 12. 验收

1. 关闭主界面后托盘图标继续存在。
2. 主界面关闭后监控继续采样并写入 SQLite。
3. 主界面重新打开后恢复当前监控图和测试状态。
4. 托盘图标创建失败时没有隐形 Agent。
5. 双击托盘只打开一个主界面。
6. 活动测试期间关闭主界面不杀死工具。
7. 托盘退出时提示活动测试并在确认后完整退出。
8. 托盘退出后不存在 Agent、App、Worker、Broker 或工具孤儿进程。
9. Agent 崩溃后下一次启动能识别 interrupted run 并恢复临时电源计划。
10. 其他用户不能连接命名管道。
11. 非法、过期和计划哈希不符的 Broker 请求被拒绝。
12. Agent 可连续后台监控至少一个目标长周期，内存和数据库 backlog 有界。
