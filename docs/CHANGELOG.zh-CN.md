# 变更记录

[English](CHANGELOG.md) | [简体中文（仅供阅读）](CHANGELOG.zh-CN.md)

> 本文件仅为中文阅读副本；实际变更历史以无 `.zh-CN` 后缀的
> [CHANGELOG.md](CHANGELOG.md) 为准。

本文件记录重要最终结果。活动阶段的计划工作保留在 `Plan.md`，历史计划保留在
`Archive`。施工过程由 Git 保存。新条目使用结果分段；不为格式一致而重写全部旧历史。

## V0.45 先出壳再填数据 — 2026-09-02

### Changed
- 主窗口在等待托盘 Agent 之前先画出标签页结构。
- 连接/扫描状态只走右下角全局通知，切换标签页仍保留。若有本机缓存则先投影，再由
  PowerShell 扫描原位替换。全局通知使用不透明主题底，避免叠在 Mica 上看不清。

### Verification
- 架构测试要求 `NavigateStartupPage` 早于 `InitialAgentConnectionTask`，且主窗口
  没有全屏 `ProgressRing`。
- 本地未打包启动：主窗口句柄非 0、标题为 `WinPool`，Agent 启动期间已能看到外壳
  标签和管理页分类。

### Known Limitations
- Agent 冷启动和嵌入 PowerShell 清单仍可能要数秒；等待不再留下空白 Frame。

## V0.45 本地运行时碰撞门 — 2026-09-01

### Changed
- 本地 App 与 Agent 先写到独立树，再复用 staging 的 SHA-256 并集。同名不同内容
  会使本地构建失败。

### Verification
- 架构测试要求共享合并脚本和分离的本地树。
- 产品版本仍为 V0.45。

### Known Limitations
- 继承的操作系统矩阵与完整人工 UI 用例仍为 `unverified`。

## V0.45 — 2026-09-01

### Changed
- 产品版本为 **V0.45**。
- portable App 与 Agent 共用一份经碰撞检查的运行时目录。
- 设置页开关改为滑块、每项一段文案，按钮带图标。

### Verification
- 架构版本源测试与 `Directory.Build.props` 一致。
- 继承的操作系统矩阵与完整人工 UI 用例仍为 `unverified`。

### Known Limitations
- 不会发生真实存储修改。

## V0.44 设置页卡片布局 — 2026-09-01

### Changed
- 设置页的外观、常规、关于三张卡片使用同一套布局：分区标题、标签列、间距
  和左对齐控件。

### Verification
- 布局改后对设置页做了目标原生截图。
- 产品版本仍为 V0.44。

### Known Limitations
- 完整双语、主题、DPI 和高对比度设置页复核仍为 `unverified`。

## V0.44 Agent 客户端映像路径 — 2026-09-01

### Changed
- Agent 现在要求 `WinPool.App.exe` 在自己旁边。扁平布局后仍检查
  `..\WinPool.App.exe` 会拒绝 App 握手，导致 IPC 和盘点失败。

### Verification
- 架构路径护栏要求客户端映像与 App 同目录。
- 产品版本仍为 V0.44。

### Known Limitations
- 完整 App/Agent UI 盘点冒烟在本次变更为 `unverified`。

## V0.44 本地 Agent 自包含运行树 — 2026-09-01

### Changed
- 本地 Agent 以自包含 `win-x64` 写到共享运行树。从 `WinPool.lnk` /
  `artifacts\Release` 启动时，App 拉起 Agent 不再弹出安装或升级 .NET 的对话框。

### Verification
- 本地 `WinPool.Agent.runtimeconfig.json` 使用 `includedFrameworks`。
- 从 `artifacts\Release` 直接启动 Agent 不再要求机器上的 .NET Runtime。

### Known Limitations
- 产品版本仍为 V0.44。
- 继承的操作系统矩阵与完整人工 UI 用例仍为 `unverified`。

## V0.44 共享运行时发行 — 2026-09-01

### Changed
- portable staging 是 App 与 Agent 两份独立 self-contained 发布经 SHA-256 检查
  的并集。相同文件只存一份。同名不同内容会使 staging 失败。
- 本地 `artifacts\$(Configuration)\` 使用同一扁平根目录：`WinPool.App.exe` 与
  `WinPool.Agent.exe` 彼此相邻。
- nested `Agent\` 运行时树已从 staging 和本地运行树移除。产品版本仍为 V0.44。

### Verification
- Release 解决方案构建：0 warnings，0 errors。
- Release 自动测试：353 passed，0 failed，0 skipped。
- 无已知易受攻击包。
- 并集合并：281 共享，288 仅 App，5 仅 Agent，0 冲突。
- portable staging：574 个文件，231.58 MiB，相对 nested V0.44 基线 779 个文件 /
  338.40 MiB。
- 布局：两个可执行文件都在 staging 根，产品版本 V0.44，无 PDB，PRI 与 XBF 仍在。
- 目标进程冒烟：从合并树冷启动 App 能从根路径拉起 Agent；Agent 也可直接启动。

### Known Limitations
- 完整 App/Agent UI 冒烟（导航、主题、语言、Picker、盘点、监控、托盘命令）在
  本次变更为 `unverified`。
- Win10 22H2、Win11 24H2/25H2 完整人工矩阵，以及继承的设备、UAC、DPI 与长期
  用例仍为 `unverified`。
- 不会发生真实存储修改。

## V0.44 平台升级与发行瘦身 — 2026-09-01

### Changed
- Windows App SDK 从 1.8 升级到 **2.4.0**。未使用的 AI、ML、Search、Widgets 载荷
  已从 publish 排除；portable 树不再包含 ONNX/DirectML 文件。
- 面向 Windows 的工程统一为 TFM `net10.0-windows10.0.26100.0`。钉死的 .NET SDK
  拒绝 28000 TFM（NETSDK1140）。`Microsoft.Windows.SDK.BuildTools` 为
  `10.0.28000.2705`。
- 对外最低操作系统为 Windows 10 22H2 x64。编译 SDK 不是该下限。
- 正式 staging 不含 PDB。构建产物仍保留符号。
- 曾尝试扁平化 App/Agent，因 5 个同名桌面程序集内容不同，仍保留 nested
  `Agent\` 布局。

### Verification
- Release 解决方案构建：0 warnings，0 errors。
- Release 自动测试：352 passed，0 failed，0 skipped。
- 无已知易受攻击包。
- portable staging：779 个文件，338.40 MiB，相对 V0.43 基线 853 个文件 /
  380.44 MiB。
- 目标原生冒烟：App 与 Agent 能从干净 staging 树启动。

### Known Limitations
- nested 布局下 App/Agent 公共 runtime 文件仍保存两份。
- Win10 22H2、Win11 24H2/25H2 完整人工矩阵，以及继承的设备、UAC、DPI 与长期
  用例仍为 `unverified`。
- 不会发生真实存储修改。

## V0.43 产品瘦身 — 2026-08-31

### Changed
- 磁盘测试、外部工具、开发/AI 诊断、TestWorker 和 ElevatedBroker 子系统已从 1.0
  发布路径移除，推迟到 1.x/2.0。
- 运行时收缩为 App + Agent。
- IPC 协议升到 4；SQLite schema 升到 14。

### Verification
- Release 解决方案构建：0 warnings、0 errors。
- Release 自动测试：350 passed、0 failed、0 skipped。

### Known Limitations
- “测试”和“开发”标签页继续作为路线占位页。
- 不发生真实存储结构修改。

## V0.42 范围收缩 — 2026-08-31

### Changed
- 将整个 WinPool 1.x 产品线收缩为拓扑、管理/编辑、监控、设置、数据安全和发布质量的
  最短发布路径。
- 保留“测试”和“开发”标签页入口，但以简短的双语说明取代完整工作区。完整产品界面与
  工作流不再属于任何 1.x 版本，调整为 V2.0 计划功能。
- 同步修改 V0.5 至 V2.0 路线与质量边界，使其符合收缩后的 1.x 范围。

### Verification
- 架构边界测试：31 passed、0 failed、0 skipped。
- Release 解决方案构建：0 warnings、0 errors。

### Known Limitations
- 为避免增加发布风险，现有内部测试、工作进程、持久化和诊断基础仍保留在代码中；它们
  不属于受支持的 1.x 产品界面。
- 本次变更尚未完成两个占位页的原生视觉与人工验收，状态为 `unverified`。

## V0.42 — 2026-08-28

### Added
- 基于单元的拓扑布局引擎：拓扑行改用整数 H/W 单元排版，取代原先按像素贪心折行的方式。
- 内置多种布局模拟系统，用于在代表性存储配置上验证拓扑布局。

### Changed
- 先按对齐高度填空，仍超宽时先把整个池换到下一行，最后才折单个池内的磁盘。
- 多池并排时，池可超出对齐高度最多 `max(H+1, 1.3H)`，超过才换池行。
- 收窄时先减无分区的磁盘，再动有分区的磁盘；并排的分层池和「池 → 磁盘 → 分区」三层结构保持最小宽 2 格，简单两层池与独占一行的池不受此限。
- 拓扑区横向滚动改为自动。

## V0.41 — 2026-08-14

### Added
- 已确认 V0.41 计划中的启动欢迎、持续监控、持久化、托盘和基础交互。

### Changed
- 语言、外部工具、数据位置以及 Edit/Settings 布局。

### Fixed
- 偏好加载卡住导致 Agent 一直处于 `Recovering` 的问题。
- 持续监控的启动、关闭主窗、重开和接回。

### Compatibility
- Agent 持有的 SQLite 为 schema 13。更旧 schema 会被拒绝且不迁移。
- IPC 仍为协议 3。
- 用户偏好只保存在 `settings.json`；SQLite 不再保存用户偏好。

### Known Limitations
- 继承的拓扑右键定位、设备、UAC、DPI、外部工具执行和长期用例仍为 `unverified`。
- 未创建 commit、push、tag、Release、二进制上传或部署。

## V0.4 — 2026-08-14

### Changed
- 打开 V0.4 产品线。机械 .NET 版本元数据为 `0.4.0`。

### Known Limitations
- V0.4 版本定义已推送到 GitHub。未创建 tag、GitHub Release 或二进制上传。

## V0.39 架构治理 — 2026-08-14

### Changed
- 移除已确认无消费者合同与退役 UI 残留。
- 拆分过度集中的 Agent 和页面职责。
- 将 I/O、Copy、Mixed Directory 的闭合定义图移至 `WinPool.Testing`。

### Known Limitations
- 这是 V0.39 维护记录，不是产品发布。
- 其余原生、设备、UAC、安装器、迁移和长期用例保持 `unverified`。

## V0.39 — 2026-08-13

### Fixed
- 开发页诊断刷新增加超时、可见状态和重复刷新保护。
- 请求取消或超时后隔离控制管道，避免迟到响应被下一请求消费。
- 测试页 Start/Cancel 对丢失或畸形响应使用 `OutcomeUnknown`，并做 RunId 对账且禁止自动重试。

### Changed
- 完成 V0.4 视觉阶段前 V0.3 的最终最小修正。

### Known Limitations
- 继承的原生、设备、UAC 和长期运行矩阵继续保持 `unverified`。
- 用户已授权本地提交、GitHub 推送、V0.39 tag 和 GitHub Release。

2026-08-14 勘误：先前的 562 项测试表述无法由 V0.39 解决方案复现。完整 Release 命令输出
526 passed、0 failed、0 skipped；526 是当前 V0.39 自动测试的控制计数。冻结归档原文作为
历史证据保留，不改写。

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
