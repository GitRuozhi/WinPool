# 变更记录

[English](CHANGELOG.md) | [简体中文（仅供阅读）](CHANGELOG.zh-CN.md)

> 本文件仅为中文阅读副本；实际变更历史以无 `.zh-CN` 后缀的
> [CHANGELOG.md](CHANGELOG.md) 为准。

本文件只记录已经实际发生的结果。活动阶段的计划工作保留在 `Plan.md`，历史计划保留在 `Archive`。

## V0.39 — 2026-08-13

- 开发页诊断刷新增加超时、可见状态和重复刷新保护。
- 开发页刷新异常时显示明确信息并恢复控件。
- 完成 V0.4 视觉阶段前的 V0.3 稳定性收口。

V0.39 已通过 520 项 Release 自动测试（无 skipped）、零警告 Release 构建和依赖审计。原生/人工用例继续保持 `unverified`。用户本次仅授权本地 Git 提交；未授权推送、tag、二进制上传、GitHub Release 或部署。

## V0.38 — 2026-08-13

- 新增 Agent endpoint 进程身份验证。`agent-endpoint.json` 的 PID 被无关 Windows 进程复用后，会被视为陈旧端点。
- `ConnectAsync` 会通过现有 launcher 启动替代 Agent，而不是继续等待已经失效的 named pipe。
- 新增陈旧端点身份恢复的回归测试。

V0.38 已通过 520 项 Release 自动测试（无 skipped）、零警告 Release 构建和依赖审计。原生/人工用例继续保持 `unverified`。用户本次仅授权本地 Git 提交；未授权推送、tag、二进制上传、GitHub Release 或部署。

## V0.37 — 2026-08-13

- 关闭 Settings 语言 SelectionChanged 的重入路径，并为主题、强调色、语言、MSR 与硬件 ID 偏好保存增加失败恢复。
- 在写入崩溃证据后将未观察任务异常标记为已观察。
- 为测试页 prepare/start/cancel/status 轮询和编辑页模拟变更补齐异常恢复与控件状态恢复。
- 非空但无效的分区大小输入会被拒绝，不再静默使用全部剩余空间。
- 模拟创建存储池前增加参数预览和确认。
- Development 事件流和采集对照在传输异常时会显示结果并恢复控件。
- 单实例激活重定向等待改为有限时间，不再无限等待。
- 畸形 RoboCopy 输出会归一化为失败工具事件。
- 格式化文件系统由自由文本改为固定 NTFS/ReFS/exFAT 选择，分区路径缺失时给出提示。

V0.37 已通过 519 项 Release 自动测试（无 skipped）、零警告 Release 构建和依赖审计。原生/人工用例继续保持 `unverified`。用户本次仅授权本地 Git 提交；未授权推送、tag、二进制上传、GitHub Release 或部署。

## V0.36 — 2026-08-12

- 在 `Product.md` 记录用户确认的 V0.1--V1.0 开发路线，并说明 V0.5 是首个可增加受控真实存储结构修改的阶段。开发 Agent 需要开发者对每次操作的批准；产品用户在当前会话显式选择“本机真实修改”即授权执行 V0.5 的受控真实操作。这不改变当前 V0.3 边界，也不授权发布。
- schema-12 验证新增表约束/定义和索引元数据/定义比较，覆盖 singleton `CHECK` 约束；损坏的 current 数据库仍不会进入修改路径。
- `NamedPipeAgentConnection` 在释放共享资源前会取消并等待活动连接/请求；畸形 handshake JSON 统一返回 `agent.connect.failed`。
- 正常 watcher 退订不再被误记为有界通道溢出，从而避免错误的全局 event-gap recovery。
- worker process 的 identity 字段不可变，heartbeat 单调，Stopping deadline 只建立一次并在终态持久化中保留。
- 即使历史 Local 行的名称或 binding metadata 已过期，经验证的 Local document ID 仍是权威选择。

V0.36 已通过 519 项 Release 自动测试（无 skipped）、零警告 Release 构建、依赖审计和全新四进程 self-contained staging。原生/人工用例继续保持 `unverified`。用户本次只授权本地 Git checkpoint；未授权推送、tag、二进制上传、GitHub Release 或部署。

## V0.35 — 2026-08-12

- 将 Agent 持有的 Local system identity 固定为 SQLite 权威记录，comparison-first capture 不再创建新的 Local `SystemId`。
- 隔离卡顿的 App-side event watcher，将队列溢出作为明确 event gap，并在恢复后为健康 watcher 重新提供 snapshot。
- `worker_processes` 的终态持久化改为不可回退，迟到写入会被原子忽略。
- shutdown operation 即使忽略取消也会有界结束，迟到 terminal effect 受 attempt fence 约束。
- schema-12 数据库的表、列、索引或外键与只读 current-schema contract 不符时会被拒绝。
- Main App 的 handshake 与 shutdown 统一检查 PID、可执行文件镜像和进程启动 incarnation witness。

用户明确确认 V0.35：507 项 Release 自动测试、零警告 Release 构建、依赖审计，以及
`D:\WinPool-V035-Candidate-Staging-Final-20260812` 的四进程 self-contained staging 均已通过。
原生/人工用例继续保持 `unverified`；确认不代表将其写为通过。该决定授权文档归档、本地 checkpoint、
`main` 推送和本机 portable 部署；未授权 tag、二进制上传或 GitHub Release。

## V0.34 — 2026-08-11

- 所有受监督进程更新均绑定进程实例 ID、PID 与 OS 启动时间 witness；IPC 协议提升为 3。
- Local inventory identity 改为由 Agent 负责；数据位置 pointer 提交后的清理会报告部分完成；schema 12 采用 clean break，旧数据库会被拒绝且不被改写。
- 新增 authoritative shutdown status、事件 gap 后整份 snapshot reseed、显式 event backpressure，以及 stdout/stderr 隔离并在 EOF flush 的进度解码。
- V0.34 已通过 494 项 Release 自动测试、零警告 Release 构建、依赖审计，以及
  `D:\WinPool-V034-Candidate-Staging-Final` 的四进程 self-contained staging。

用户明确确认 V0.34，并授权对应的文档归档、Git checkpoint、`main` 推送和本机 portable 部署。
原生/人工用例继续保持 `unverified`；确认不代表将其写为通过。未授权 tag、二进制上传或
GitHub Release。

## V0.33 — 2026-08-11

- 用户明确确认 V0.33，并授权归档文档、提交 Git 和推送 `main`。

- 将 `WinPool.Core` 收敛进权威 Application 模型，并保留系统/文档身份、模拟、投影、
  启动、通知和布局行为。
- 强化 Agent、Worker、Broker、Control IPC 和 Event IPC 生命周期：可重试关闭、有界
  进程终止、typed abort、坏客户端隔离、断线恢复、snapshot reseed 和明确 event-gap 状态。
- 增加进程实例身份、有界 terminal diagnostics 和真实 SQLite v10→v11 历史迁移；
  V0.33 唯一一次 wire protocol bump 为 2。
- 外部工具路径改由 Agent 持有；每次工具调用只解析一次 numeric output code page，
  stdout/stderr 分别进行 stateful decoding，同时保留原始字节。
- storage-location 从覆盖复制改成同卷精确 staging 事务：捕获源和目标，只 drain 源
  store，验证 manifest 与 SQLite identity，在取消或失败时恢复旧目标，并移除陈旧的
  managed target payload。
- 将测试状态、系统支持和库存所有权拆入三个聚焦的 Agent coordinator，
  `DesktopAgentRuntime` 继续作为 request facade。
- 全部 486 项 Release 测试、无警告 Release 构建、传递依赖审计、Markdown 检查和
  V0.33 四进程 staging 均通过。
- 十项原生/人工用例继续保持 `unverified`；版本确认不代表这些用例通过。

V0.33 是已确认的项目版本，不是 tag、二进制发布或 GitHub Release。
实现提交范围：`6b66c68` 至 `0dcd22a`；版本提交 `38ff043`；验收文档提交
`e148b61`。这些提交已存在于 `origin/main`。

## V0.32 — 2026-08-10

- 用户在审阅 V0.31 重构后明确指定 V0.32。
- 按用户规定的 `Va.bc` 规则将唯一项目版本设为 V0.32。
- 为英文项目文档增加非权威 `.zh-CN.md` 阅读副本；无后缀文档保持控制权。
- 将软件引用的 `assets` 纳入 Git，并将用户手动管理的 `OriginArtWork` 排除在 Git 外。
- 11 项尚未执行的原生/人工用例继续标记为 `unverified`；版本指定不代表这些用例通过。
- 重新验证全部 458 项 Release 测试和 V0.32 四进程嵌套 staging；四个可执行文件均报告项目版本 V0.32。
- 删除错误引入的 `TechnicalVersion` 概念。.NET/Windows 必需的数字字段是派生编译元数据，不是另一套项目版本。

V0.32 是当时确认的项目版本，不是 tag、二进制发布或 GitHub Release。
提交：`dc5e263`、`7b7a798`（已推送到 `origin/main`）。

### V0.31 文档架构修正 — 2026-08-10

- 用规定的 `docs` 信息架构替换错误的根 `Plan` 布局。
- 恢复用户批准的仓库内文档归档策略。
- 将错误 V0.31 计划保存为已替代的审计历史，不改写或 force push Git 历史。
- V0.32 人工验收保持未验证。
- 前向修正提交：`236eb3f`（已推送到 `origin/main`）。

本次修正不是 tag、二进制发布或 GitHub Release。

## V0.31 源码集成 — 2026-08-10

- 增加共享 V0.31 版本源。该提交也曾错误地把数字编译元数据命名为技术版本；V0.32 后续修正了该语义。
- 增加可复现四进程发布 staging 和真实布局验证。
- 更新源码和自动架构检查。
- 提交：`6cf68e3`、`8d7fb25`。

上述提交中的原文档归档决定无效，并由前述修正替代。

## V0.21 — 2026-08-09

- 发布采用 V0.13 视觉基线的 V0.2 多进程架构集成。
- 在 `ec8b34a` 修复无打包部署打包基线。
- 发布提交：`fcebb67`。
