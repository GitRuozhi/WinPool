# WinPool V0.3 最终最小稳定性修正计划

## 0. 状态与版本

- **计划状态**：implemented / automatic gates passed / target WinUI case passed
- **创建日期**：2026-08-13
- **基线提交**：`c6232f7ae4cd38472686a13187931d4ad95779b6`
- **当前产品版本**：V0.39
- **目标产品版本**：仍为 V0.39
- **阶段编号**：`V039-FIX-A`，仅用于工作识别，不是产品版本
- **阶段性质**：V0.3 的最终修正检查点；完成后停止 V0.3 修正并为 V0.40 建立独立计划

本计划创建文档本身不授权修改代码、更新版本、提交 Git、推送、打 tag、
创建 Release、上传二进制或部署。实施必须由用户另行明确批准。

## 1. 目标

本轮只关闭一条高风险链路：Agent 控制请求超时后，客户端不得复用可能残留
旧响应的命名管道；测试页 Start/Cancel 不得把超时误判为“操作未发生”并自动重试。

完成后：

- V0.39 保持为 V0.3 最终产品版本；
- 不继续清理其他一般性异步技术债务；
- 未进入本计划的事项转入 V0.8–V0.9 技术债务参考；
- 下一份产品实施计划使用 V0.40，进入 V0.4 视觉、现有功能和基础交互完善阶段。

## 2. 唯一实施范围

### 2.1 Agent.Client 超时后的控制管道隔离

当前控制连接在一个持久命名管道上串行执行“写请求—读响应”。如果请求可能已
写入后客户端取消等待，Agent 仍可能返回旧响应；继续复用该连接会使下一请求读到
错误响应。

实施要求：

1. 明确区分以下阶段：
   - 等待请求门时取消：请求确定未提交，不破坏当前连接；
   - 已进入写入或读取阶段后取消：结果可能未知；
   - 收到并验证匹配响应：结果确定。
2. 在请求可能已经提交后发生取消、timeout 或帧读取中断时，先标记并关闭当前控制
   连接，再释放该请求占用；后续请求必须重新握手连接。
3. 不允许下一请求消费前一请求的迟到响应。
4. 保留 disposal 与调用方 cancellation 的区别，不把正常关闭记录成操作 timeout。
5. 不修改 IPC protocol、消息 schema、SQLite schema 或 App/Agent 进程边界。

这里的 timeout 只限制客户端等待时间，不代表 Agent 端操作已停止，也不保证 Agent
能在原处理器返回前立即接受对账请求。

### 2.2 TestPage Start/Cancel 的未知结果与对账

仅处理测试运行的 Start、Cancel 和它们所需的状态查询，不扩展到 System Support、
工具安装、导入导出或其他页面。

实施要求：

1. Start 在发送前生成并保留稳定 `RunId`。
2. Start 或 Cancel 在可能提交后 timeout 时进入 `OutcomeUnknown`：
   - 不显示“确定失败”或“确定未执行”；
   - 不自动重发；
   - 不允许用户立即重复 Start/Cancel。
3. 重新建立 Agent 连接后，使用现有 snapshot、活动 RunId 和测试结果查询进行权威
   对账。
4. 只有在查询确认运行状态后，才恢复与该状态一致的按钮：
   - 已活动：显示活动运行并允许一次 Cancel；
   - 已完成、失败或取消：显示终态并恢复 Start；
   - 确认不存在对应运行且 Agent 可用：恢复 Start；
   - 仍无法确认：保持 Unknown，不自动重试，允许之后再次查询状态。
5. 状态查询是只读操作，可以在独立 timeout 后再次查询，但不得并发重入。
6. TestPage 使用页面生命周期 cancellation；离开页面后停止等待并忽略迟到 UI 更新。
   页面离开取消不得显示为 timeout，也不得访问已离开的页面控件。
7. timeout 使用有名称、集中定义的常量。Start/Cancel ACK 默认 15 秒，状态查询默认
   10 秒；如自动证据证明不合适，必须在实施记录中说明调整理由。

为便于自动测试，状态转换和重试决策应放在 `WinPool.Application` 或
`WinPool.Agent.Client` 的纯逻辑中；WinUI 页面只做薄适配。不得为本轮建立覆盖所有
页面、安装、文件操作和恢复逻辑的通用 Guard 框架。

## 3. 明确不做

- 不使用 `V0.39a`、`V0.39B`、`V0.3.10` 或其他新版本体系。
- 不推进产品版本到 V0.40。
- 不全局替换 `CancellationToken.None`。
- 不处理 Development、Settings、Manage、Monitor 或 System Support 的一般技术债务。
- 不建立统一日志框架、万能 `UiOperationGuard` 或万能 `AgentRequestGuard`。
- 不升级 IPC protocol 或 SQLite schema。
- 不新增页面、外部工具或产品能力。
- 不开放真实磁盘、分区、卷、Storage Pool、Storage Tier 或 Virtual Disk mutation。
- 不改变 Simulation-first、deny-by-default、数据脱敏或外部工具边界。
- 不修改 V0.39 已冻结的归档记录来重写历史。

## 4. 自动测试

至少新增以下确定性覆盖：

1. 等待请求门时取消不会破坏正在工作的控制连接。
2. 写入开始后取消会关闭当前控制连接。
3. 等待响应时 timeout 会关闭当前控制连接。
4. timeout 后下一请求重新连接，不能读到前一请求的迟到响应。
5. Start timeout 转入 Unknown，不产生第二个 Start 请求。
6. Cancel timeout 转入 Unknown，不产生第二个 Cancel 请求。
7. 对账确认活动、终态、不存在和仍未知时，分别得到正确 UI 决策。
8. 状态查询防止并发重入，并允许在查询 timeout 后安全重试。
9. 页面离开 cancellation 与 timeout 分离，迟到结果不更新页面。

WinUI 事件壳层无法稳定自动化的部分必须留给目标化人工验证，不得把未执行的
原生行为写成 `passed`。

## 5. 质量门

从 `Program\WinPool` 根目录执行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

要求：

- 全部确定性测试 `passed`，不得新增 skip；
- Release build 为 0 error、0 warning，或记录用户批准的明确例外；
- 依赖漏洞审计未发现已知漏洞；
- 架构、安全边界和版本来源保持一致。

## 6. 目标化人工验证

只验证本轮修改路径：

1. Agent 正常时，测试 Start、状态刷新、Cancel 和终态显示正常。
2. 在 Start 已发送但响应延迟时，UI 进入 Unknown，不重复启动；恢复连接后显示真实状态。
3. 在 Cancel 已发送但响应延迟时，UI 不重复取消；恢复连接后显示真实状态。
4. 状态查询失败或 Agent 暂时不可用时，页面保持可响应并能稍后重新查询。
5. 请求进行中离开 TestPage，不出现错误 timeout 提示、跨页面更新或未观察异常。

结果必须使用 `passed`、`failed`、`unverified`、`not_required` 或
`deferred_by_user`。本轮之外的既有原生、设备、UAC、托盘和长期运行案例继续保持
原状态，不得据此声称整个 V0.3 人工矩阵通过。

## 7. 停止条件与完成门

出现以下任一情况立即停止扩大实现，并回报用户：

- 必须修改 IPC 消息 schema 才能继续；
- 必须改变 Agent 并发服务模型才可实现最低正确性；
- 修复要求扩展到第二个产品页面或非测试类副作用命令；
- 发现真实存储 mutation、安全授权或数据脱敏边界需要改变；
- 目标自动测试无法放入现有可测试项目，且需要大规模 App 架构重构。

只有以下条件全部满足，才可标记本计划完成：

- 第 2 节两项修复完成，没有顺带实施技术债务清单；
- 第 4 节自动测试覆盖通过；
- 第 5 节自动质量门通过；
- 第 6 节目标化人工验证已有真实结果，未执行项如实标记；
- CHANGELOG 只记录实际发生的结果；
- 产品版本仍为 V0.39；
- 计划归档到 `docs/Archive/V0.39-final-correction/` 并更新归档索引；
- 没有未经授权的 push、tag、GitHub Release、二进制上传或部署。

本计划完成并归档后，不再接受新的 V0.3 一般性修正。新发现的非紧急问题进入
技术债务清单；只有安全、数据损坏或无法启动级别的事实才允许请求用户决定是否
打破该冻结。下一正常开发阶段是 V0.40。

## 8. 实施记录

用户于 2026-08-13 明确授权执行计划、完成测试、本机部署、WinUI 检查、GitHub
推送和 Release 发布。

已完成：

- Agent.Client 在请求写入或响应读取阶段取消时关闭当前控制连接，并返回
  `OutcomeUnknown`；等待请求门时取消不会破坏现有连接。
- TestPage 的 Start/Cancel 使用页面生命周期 cancellation 和集中 timeout；可能已经
  提交的请求不自动重试，并通过 Agent snapshot、RunId 结果查询进行对账。
- 抽取 `TestRunReconciliation` 纯状态决策并增加 Application 测试。
- 新增 Agent.Client 连接隔离、迟到响应保护和请求门取消测试。
- Release 自动测试：562 passed，0 failed，0 skipped。
- Release build：0 warning，0 error。
- 依赖漏洞审计：未发现已知漏洞。
- 自包含 staging：V0.39 四进程布局验证通过，证据目录为
  `D:\Coding\Research03_WinPool\Rubbish\20260813_winpool_v039_final_correction_staging`。
- WinUI 原生目标化检查：启动、Agent 子进程、欢迎对话框、六个标题栏标签、测试页
  控件状态和截图检查通过；未执行任何测试目录写入或真实存储结构操作。
- 既有完整原生/设备/UAC/长期运行矩阵仍为 `unverified`，没有被本次目标化检查替代。

本计划随后归档到 `docs/Archive/V0.39-final-correction/`。V0.39 是 V0.3 的最终
修正版本；下一正常开发阶段为 V0.40。
