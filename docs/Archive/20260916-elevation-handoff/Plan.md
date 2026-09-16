# 提权重启与 Agent 连接复发修复 Plan

状态：已完成并归档。日期：2026-09-16；计划基线：`ed688d6`（V0.53）。本记录的实现、自动门和隔离原生验证均已完成；未执行真实存储写操作。

## 目标与范围

修复用户报告的“打开本机真实操作 → 以管理员身份重启不完全 → Agent 弃连/握手异常”。按用户最新决定，采用整套退出、整套管理员重启：旧 App 和 Agent 全部退出后，才启动新一套管理员 App 和 Agent，不保留或复用旧 Agent。

本次重启后的 App 和 Agent 均以管理员身份运行，后台监控允许短暂中断；普通启动和自启权限策略不变。不新增真实存储操作、不重构无关模块；提权不等于存储操作授权，Real 同意不持久化。验证使用隔离运行树和数据，不干扰无关实例。

## 确定流程

1. 防止重复触发，发起 UAC；新管理员进程先进入等待阶段，不打开业务窗口、不连接旧 Agent，也不抢占旧单实例。
2. UAC 取消或新进程启动失败，旧软件不动。确认管理员等待进程已启动后，旧 App 保存工作区、停止本地任务；旧 Agent 结束监控并完成写入，释放数据库租约和管道，旧 App 关闭全部所属窗口并退出。
3. 管理员等待进程确认本次旧 App、旧 Agent 均已退出，再进入正常启动流程，创建新管理员 Agent，完成连接和工作区恢复。
4. 旧进程未退出则有界报错，不继续启动、不回接旧 Agent、不静默强杀。若旧软件已部分或全部退出，明确提示失败阶段及重新打开方式，不声称已回滚。

关闭范围仅为本次会话关联的旧 App、Agent 及其已确认所属进程；按 PID、启动时间、路径和会话核验，不按名称批量结束其他副本。复用现有退出与启动机制，不新增通用交接框架。

## 已知事实与待核实原因

| 位置 | 静态核对结果 |
| --- | --- |
| `WindowsElevationRestartService.RestartElevatedAsync` | `runas` 返回进程即报告 Started，只传旧 PID；不代表新实例已就绪。 |
| `MainWindow.RequestExecutionModeAsync` / `MainWindow_Closed` | Started 后调用 `Close()`；保存与监控脱离位于 `async void Closed`。此链路未显式等待共享 Agent 连接释放，不能把关窗等同于清理完成。 |
| `Program.WaitForProcessHandoff` | 最多等旧 PID 10 秒，但忽略 `WaitForExit` 返回值，随后仍进入单实例重定向；没有核对旧进程启动时间。 |
| `App.ConnectAgentAsync` / `NamedPipeAgentConnection` | 存在启动重试；连接包含控制握手、事件握手、恢复快照多个阶段，不能只检查控制管道接通。 |
| `AgentControlServer.RunAsync` | 控制连接串行服务，已有握手期限；已连接后的 `IOException` 被整体静默处理，需区分正常退出与真正错误。 |

`116ac08` 已处理关闭窗口后继续更新控件、跨完整性进程身份读取及正常弃连提示；其验收保留原 Agent。此次用户报告说明不能复用旧验收结论。上述代码风险尚不是本次现场根因：先排除运行旧二进制/混合运行树，再核实残留窗口或进程、连接未释放、单实例重定向、身份拒绝与事件订阅失败之间的实际顺序。

## 执行步骤

| 阶段 | 工作与交付 | 状态 |
| --- | --- | --- |
| P1 复现定位 | 已完成。初次隔离交接在 `ElevationHandoffVerify2` 复现“新 bootstrap 未进入等待态”；新增阶段日志记录为 `elevation.handoff.signal_invalid`。原因是 Windows 提权服务使用大写十六进制 SID 哈希，而 bootstrap 使用 IPC 的小写哈希校验同一事件名。另发现 `artifacts/Release` 曾因运行时锁定而混入旧 App 二进制，后续验证全部使用全新、完整的隔离并集运行树。 | 已验证 |
| P2 整套重启 | 已完成。新管理员 bootstrap 先验证双进程 witness 与同用户事件名、发出 ready 后等待 continuation；它在此之前不初始化 WinUI、不抢单实例、不启动或连接 Agent。旧 App 保存工作区后让旧 Agent 后台确认有序关闭，才释放 continuation 并关闭自己的全部窗口。新 bootstrap 按 PID、启动时间和路径核验旧 App、旧 Agent 都已消失才正常启动；身份不符、不可读或超时均不强杀、不接回旧 Agent。 | 已验证 |
| P3 新连接就绪 | 已完成。`NamedPipeAgentConnection` 只在控制握手、事件握手和恢复快照都完成后公开活动 handshake；新 App 以该实际 Agent PID/启动时间建立交接 witness。正常旧管道断开不弹误报；真实交接阶段写入隔离数据根的 `Diagnostics/elevation-handoff.jsonl`。 | 已验证 |
| P4 验证收口 | 已完成。完成三轮原生 WinUI 的普通→管理员整套交接、取消和 continuation 超时保护；运行 restore、586 项 Release 自动测试、Release 并集构建与依赖审计。 | 已验证 |

P1 用于定位和验证，不再重新讨论是否复用旧 Agent。后台监控按既有偏好在新 Agent 恢复；旧监控会话先明确结束，不冒充连续采样。新软件尚未完成连接时不宣布“重启成功”。

## 回归与验收

| 场景 | 必须观察的结果 |
| --- | --- |
| 普通 App 开启本机真实操作，接受 UAC | 旧 App 和 Agent 进程实例均消失；仅一套新管理员 App + Agent，新 Agent session；同一数据根，模式及工作区正确；控制握手、事件订阅、快照和一个只读请求成功。 |
| 同时打开欢迎窗口；连接/采集尚在进行；监控开启 | 旧窗口、任务、监控和写入全部收口；新 Agent 按偏好恢复监控，允许中断但无双会话或双写入者；无关闭后访问控件。 |
| 取消 UAC、管理员等待进程启动失败、重复点击 | 原窗口、模式和连接可用；无重复提权和遗留新实例。 |
| 旧软件退出后，新 Agent 启动或连接失败 | 不报告重启成功、不连接旧 endpoint；明确失败及恢复方式，不假称旧软件仍可用。 |
| 旧进程退出慢/超时、PID 失效或复用 | 有界失败且身份核验正确；不误等/误杀其他进程，不回到旧窗口后误报成功。 |
| 已提权 App 再开启选项；正常关窗后重开；数据位置交接 | 已是一套管理员进程时不重复重启；管理员 App 搭配旧普通 Agent 时仍执行整套重启。普通重开不继承真实操作同意，不破坏共用的等待和启动参数语义。 |
| 零字节/半帧弃连、旧客户端仍连接、事件握手中取消 | 连接释放后健康客户端可接入；不静默挂起、不使 Agent 监听退出；正常断开与超时/协议/身份错误分开记录。 |
| Agent 未启动、endpoint 陈旧、权限/身份核验失败 | 成功恢复或明确失败；不得绕过 SID、nonce、协议、进程身份和单写入者校验。 |

自动测试优先覆盖生产交接逻辑和真实命名管道，允许注入进程/UAC结果与时钟；保留已有弃连、半帧超时、跨权限身份及参数测试，补上其未覆盖的完整顺序。新增回归须能在修复前暴露对应缺陷，不能只断言源码含某个字符串。

原生验收从设置页和主窗口实际入口执行；在隔离运行树中连续完成至少三轮“普通启动 → 旧 App/Agent 全退出 → 管理员整套启动 → 连接恢复”，同时覆盖取消及退出超时。UAC 安全桌面由用户交互，不绕过系统确认。验证期间仅使用只读请求，不执行磁盘修改。缺少实际提权重启验证时标记 `unverified`，不得以自动测试通过宣告本问题已修复。

## 实际执行结果

- 隔离数据和运行树：`artifacts/ElevationHandoffVerify2` 至 `ElevationHandoffVerify6`；均为新建便携 `Data` 根，未接触 `%LocalAppData%/WinPool`，也未执行任何真实存储结构命令。
- 完整交接三轮均由设置页“本机真实操作 → 以管理员身份重启”触发：
  - `Verify3`：旧 App/Agent `36008`/`33072` 退出；新管理员 App/Agent `10760`/`35476`，窗口为 `WinPool [管理员]`，endpoint session 为 `20ec144b-f9e4-4694-aeb2-cc15a82aec8d`。
  - `Verify5`：旧 `29108`/`22120` 退出；新 `27204`/`36764`，endpoint session 为 `f84168c4-cd87-4146-beaa-8de11ca4fe2d`。
  - `Verify6`：旧 `37188`/`27816` 退出；新 `23640`/`21364`，endpoint session 为 `96e1a496-c9a0-49c1-bdc9-083382d4d6d2`。
- 取消确认时，`Verify4` 的 App/Agent `34108`/`37468` 均保持原 PID；无新增 WinPool 进程。有效等待协议不发送 continuation 时，bootstrap `35600` 发出 ready、没有业务窗口，约 30 秒后自行退出并记录 `elevation.handoff.continuation_timeout`；原 `34108`/`37468` 仍运行。
- 自动验证：`dotnet restore WinPool.slnx` 成功；`dotnet test WinPool.slnx -c Release --no-build --no-restore --maxcpucount:1 -m:1` 为 586/586；`dotnet build WinPool.slnx -c Release --no-restore -m:1` 为 0 警告、0 错误，运行树合并为 288 个共享、293 个 App 专有、7 个 Agent 专有文件且 0 碰撞；`dotnet list WinPool.slnx package --vulnerable --include-transitive` 未发现易受攻击包。

隔离运行使用默认关闭的连续监控，因此没有把“监控已开启时恢复采样”冒充为本轮人工通过；该产品场景保持 `unverified`，不影响本次已验证的整套进程交接、取消和超时边界。
