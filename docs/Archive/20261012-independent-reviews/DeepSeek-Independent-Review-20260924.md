# WinPool 独立审查报告（DeepSeek）

| 项目 | 内容 |
| --- | --- |
| 审查对象 | `Program\WinPool`（GitHub: [GitRuozhi/WinPool](https://github.com/GitRuozhi/WinPool)） |
| 审查基线 | `main` @ `58723748ad3591ec12c9b16dc142142e1330376b`（V0.56），工作树干净，HEAD == `origin/main` |
| 审查日期 | 2026-09-24（本地时区 +08:00） |
| 审查方式 | 独立第三方审查：静态代码审查 + 真实软件运行测试 + 产品设计评价 + 文档/流程核对 |
| 审查环境 | Windows 10 Pro for Workstations 22H2 build 19045.7463 x64；.NET SDK 10.0.401；WinUI 3 / Windows App SDK；主显示器 3440×1440 @100%，副显示器 768×1024 |
| 自我声明 | 本次审查未参与 WinPool 的任何设计与实现。审查者与项目无利益关系。 |
| 主要产出 | 本报告；原始证据见同目录 `DeepSeek-Independent-Review-20260924-evidence\` |

## 审查方法、可信度与证据分级

本次审查由一名主审查官与四个并行子审查代理完成，分工与证伪方式如下：

| 维度 | 执行者 | 证据形式 | 说明 |
| --- | --- | --- | --- |
| 架构、分层、代码质量、测试质量 | 子代理 A | 只读静态 | 未构建、未运行 |
| 安全、信任边界、破坏性操作门禁 | 子代理 B | 只读静态 | 未构建、未运行、未连接管道 |
| 持久化、数据完整性、并发、生命周期 | 子代理 C | 只读静态 | 未构建、未运行、未触碰真实数据库 |
| App 层 VM/XAML/无障碍/本地化 | 子代理 D | 只读静态（后由主审查官补原生实测） | 未构建、未运行 |
| 产品与交互设计评价 | 子代理 E | 只读静态 | 未构建、未运行 |
| 构建、自动测试、原生 UI 实测、数据根取证、归档取证 | 主审查官 | **真实运行证据** | 独立执行，见第 3 章 |

证据分级（本报告全文沿用）：

- `实测`：主审查官在本机对 HEAD 构建产物真实执行并观察到结果。
- `复现`：通过最小可执行程序或工具输出直接复现的行为。
- `静态`：仅由源码阅读/机械统计得出，未运行。
- `推断`：由间接证据推论，未直接验证。
- `未核实`：本次未验证，报告中说明验证方式。

**一次被推翻的自我纠正（保留记录）**：子代理 D 曾判定 `LocalizationService` 静态字典重复键 `FileSystem` 会导致启动即崩溃（P0）。主审查官用两种方式证伪：(1) 从 HEAD 完整重建后原生启动，主窗口正常，崩溃日志无新增；(2) 在本机 .NET 10 上最小复现同一集合初始化器模式，结果为 `Count=2` 且不抛异常 —— .NET Core 3.0 起 `Dictionary` 索引器初始化器对重复键静默取最后一个值（`复现`）。该条已降级为 P3 代码卫生问题。此举意在说明：**本报告中的 P0/P1 结论均经过运行验证或可复现的代码链验证，未经验证的推断一律标注。**

---

## 1. 执行摘要

### 1.1 总体判断

WinPool 是一个**工程纪律明显高于同类个人项目平均水平**的 Windows 桌面存储工具：解决方案结构清晰、依赖方向基本正确、真实存储写入被设计为默认拒绝且有负向测试保护、监控归档在真实 1 GiB 规模上确实跑通并留下了可独立校验的产物、文档对"未验证"的标注习惯相当诚实。

但当前 `main`（V0.56）**不具备可交付状态**，原因不是某个孤立缺陷，而是三类系统性问题：

1. **质量门已经变红且无人知晓。** 在 HEAD 上执行项目自己规定的自动门，`WinPool.Architecture.Tests` 47 项中 **6 项失败**（实测）。同一批失败所对应的 `docs/Quality.md` 仍写着 `Architecture 47/47 passed`。也就是说，"验证记录"与"仓库现状"已经脱钩，而团队仍在据此宣称通过。
2. **"文档即事实"的假设不成立。** 抽查的关键声明中，至少 5 处与代码/实测矛盾：右栏 320–420 DIP 自适应（代码里是固定 320）、测试页双倍分隔（源码无对应物）、架构门 47/47、开发页"区域标题已被移除"（实现里 3 个区域 Border 都带 `TextBlock` 标题）、构建脚本对 `artifacts\Release` 的破坏性替换（会连便携模式数据根一起删除）。
3. **无障碍（Accessibility）基本未落地。** 8 个主导航项的 UIA 名称全部是视图模型类型名 `WinPool.App.ViewModels.ShellNavigationItem`；设置页 30 个可交互控件中 9 个没有可访问名称；结构编辑页 3827 行只有一个 `AutomationProperties.SetName`；两个语义完全不同的开关共用同一个可访问名称「真实编辑」。对一个宣称"键盘导航、高对比度"支持的产品，这是实质性缺口。
4. **App 反复崩溃，而产品自己的崩溃日志看不见。** `WinPool.App.exe` 在 2026-09-22 至 09-24 之间至少崩溃 **6 次**，其中 5 次签名完全一致（`combase.dll` + `0x80004003` E_POINTER + 偏移 `0x261d2`），跨 0.5.5.0 与 0.5.6.0 两个版本。而 `%LocalAppData%\WinPool\Diagnostics\app-crash.jsonl` 同期**只记录了 1 条**，且是另一类异常 —— 因为 `App_UnhandledException` 只覆盖 XAML 层异常，COM 级崩溃绕过它。**"日志干净"被误当成"运行稳定"，这是本次审查最重要的更正**（见 3.6）。

### 1.2 结论性评级

| 维度 | 评级 | 依据 |
| --- | --- | --- |
| 架构与分层 | **B** | 依赖图无环、方向正确、无循环依赖；但文档规则只被 7/11 项目覆盖，App 层规则泄漏 10 处 |
| 代码质量 | **B-** | 无 TODO/FIXME/NotImplemented、异常吞噬克制、无 `Task.Result` 死锁风险；但存在 3827 行的巨型页面、多处重复实现同一条规则、确认的死代码 |
| 测试体系 | **C+** | 约 638 个测试用例、无 mock 框架、负向测试扎实；但架构测试 98.5% 是源码字符串匹配，**当前 6 项失败**，App 层（34% 源码）无测试项目 |
| 安全性（当前发布态） | **B+** | 真实写入确实不可达（默认拒绝闭合、有负向测试）；但提权 Agent 存在任意绝对路径 `.csv` 写入原语、管道请求循环无读超时 |
| 数据完整性与持久化 | **B** | 归档 21 步状态机 19 步保护到位且真实跑通；但数据根迁移可能静默丢失 `winpool.db` 的 WAL 已提交帧 |
| 运行稳定性 | **D+** | 冷启动、8 页导航、语言/主题/开发者模式切换、1 GiB 轮换与 7z 归档均实测正常；但 `WinPool.App.exe` 在 3 天内至少崩溃 6 次（5 次同一签名，`combase.dll` + `E_POINTER`），且项目自有崩溃日志对此**不可见**（见 3.6） |
| 无障碍与可用性 | **D** | 见 1.1 第 3 点 |
| 产品设计与定位 | **C+** | 定位清晰、安全叙事克制；但欢迎页宣称"开源免费"与"支持 win 原生软 Raid"与仓库现状/产品现状矛盾 |
| 文档与流程成熟度 | **C** | 文档结构优秀、unverified 标注习惯罕见地诚实；但存在与代码矛盾的关键声明，且质量门失效未被发现 |
| **可交付性（V0.56）** | **不可交付** | 质量门红灯 + 文档与实现对不上 + 无障碍缺口 |

### 1.3 P0 / P1 清单（详见第 2、3 章）

本报告未发现"当前发布态即可被利用造成真实数据破坏"的 P0（依据：真实存储写入在 `src/` 中确实不可达，见 2.2）。以下 10 项为 P1，按建议修复顺序排列：

| # | 级别 | 问题 | 证据 | 类型 |
| --- | --- | --- | --- | --- |
| 1 | P1 | 架构测试门在 HEAD 上 6/47 失败，而 `Quality.md` 仍记录 47/47 通过 | `实测` TRX | 质量门/流程 |
| 2 | P1 | 标准构建会**删除并重建** `artifacts\Release`，而该目录同时是便携模式数据根；`Clean-WinPool.ps1` 还会删除 `artifacts\test-results`（已归档的验证证据） | `实测`（构建时因 App 运行而失败，旧树被部分删除）+ `静态` | 数据安全/证据保全 |
| 3 | P1 | 提权 Agent 的 `ExportAgentMonitorCsvRequest` 是任意绝对路径 `.csv` 写入原语（可配合 NTFS ADS 写任意宿主文件流） | `静态` 完整调用链 | 安全 |
| 4 | P1 | Agent 控制管道握手后有超时，但**帧读取无超时**；单个同用户客户端可长期独占唯一管道，使 App 全部 IPC 失联 | `静态`（C 与 B 双方独立得出同一结论） | 可用性/安全 |
| 5 | P1 | 未认证的畸形握手帧可让 Agent 进入永久 Failed 生命周期：进程存活但拒绝除快照外的一切请求 | `静态` 完整调用链 | 可用性 |
| 6 | P1 | 数据根迁移把 `winpool.db` 主文件当作完整副本，迁移清单排除 `-wal`，核心库又从不 checkpoint；补偿审计只比较行数/主键哈希，不比较非主键负载 | `静态` 完整调用链 | 数据完整性 |
| 7 | P1 | 8 个主导航项的 UIA 可访问名称是类型名 `WinPool.App.ViewModels.ShellNavigationItem` | `实测` | 无障碍 |
| 8 | P1 | 错误卡无条件指引用户去"开发页消息列表"，而该页在开发者模式关闭时不可见（死胡同）；通知淘汰不区分严重级别，"悬停暂停"仅有设计文档而无实现 | `静态` + 文档对照 | 交互/预期管理 |
| 9 | P1 | `System` 分区结构编辑页的操作区因自定义 `FixedWrapPanel` 忽略全部 `Grid.Row/Column`，实际渲染与设计不符 | `静态` | 界面正确性 |
| 10 | P1 | **`WinPool.App.exe` 反复崩溃且项目自有日志不可见**：2026-09-22 至 09-24 至少 6 次崩溃，其中 5 次签名完全一致（`combase.dll` + `0x80004003` E_POINTER + 偏移 `0x261d2`）；而 `app-crash.jsonl` 同期只记了 1 条 | `实测` Windows 事件日志 + WER 报告 + 代码 | 稳定性/可观测性 |

### 1.4 值得记录的强项（不是客套）

- **真实存储写入确实不可达。** `LocalStorageMutationExecutor` 无条件 `Rejected` 且在 `src/` 下零调用点；`SkippedForSimulation` 全仓库只有一处、纯审计说明、不参与判定（`实测`+`静态` 双重确认）。产品宣称"不含任何真实存储写操作"是**属实**的。
- **归档轮换在真实规模上跑通了。** 数据根中有一个真实的 1 GiB 封存→归档产物：`monitoring-20260921T105522Z-20260923T190101Z-….7z`（47,491,336 字节）。我用随附的 `7za.exe t` 独立校验：`Everything is Ok`，`Files: 2, Size: 1073743571`，即 1 GiB 压缩到 46 MiB 且内容完整（`实测`）。
- **自动采集链路生效。** 冷启动实测：窗口出现、Agent 自动完成"存储采集 + 完整硬件采集"两阶段、开发页消息列表出现两条「信息 / inventory / 采集成功」（`实测`）。**注意**：初稿曾把"崩溃日志零新增"一并列为稳定性证据，该推论已被推翻 —— `WinPool.App.exe` 在 3 天内至少崩溃 6 次而自有日志只记了 1 条，见 3.6。
- **文档的 unverified 标注习惯。** 项目在 `Quality.md` 中主动记录未验证项（如"截图全黑，不能作为视觉证据"、"拖拽效果不计为已验证"）。这在个人项目中非常罕见，是真实的可信度资产。
- **本地化开关、主题、开发者模式门实测全部正确。** 开发者模式关闭后导航项从 8 个降为 5 个（隐藏硬件/测试/开发），重新打开后恢复 8 个，且偏好正确落盘（`实测`）。
- **禁用态原因提示覆盖良好。** 分区页所有禁用控件都带具体可访问 HelpText（`实测`）。

---

## 2. 静态审查

> 本章汇总四份子代理静态报告。每条的完整推导、`file:line` 证据链与建议改法见证据目录中的 `A-architecture-static.md` / `B-security-static.md` / `C-persistence-concurrency-static.md` / `D-appui-static.md` / `E-product-design.md`（合计 2,911 行）。第 4 章的产品评价同样取自 E 报告，并由主审查官对其中最高影响的结论逐条回源核对。

### 2.1 项目与依赖重建

| 层 | 项目 | 源码量（.cs+.xaml 行） |
| --- | --- | --- |
| 表现/适配 | `WinPool.App` | 22,804（60 文件） |
| | `WinPool.Agent` | 3,771（14 文件） |
| | `WinPool.Agent.Client` | 1,193 |
| | `WinPool.Infrastructure.Windows` | 6,640（29 文件） |
| | `WinPool.Infrastructure.Sqlite` | 9,948（24 文件） |
| 应用/契约 | `WinPool.Application` | 12,784（48 文件） |
| | `WinPool.Execution` | 1,279 |
| | `WinPool.Inventory` | 149 |
| | `WinPool.Monitoring` | 904 |
| | `WinPool.Ipc` | 416 |
| 领域 | `WinPool.Domain` | 544 |
| 测试 | 11 个测试项目 | 约 24,500 行 / 70 文件 |

依赖方向与 `docs/Development.md:21` 的声明一致：无循环依赖，`Domain` 与 `Ipc` 是叶子节点。本次审查**未发现架构性错误**，这是该项目最扎实的部分之一。

**但规则覆盖不完整**：`ArchitectureBoundaryTests.cs:8-18` 的 `AllowedReferences` 白名单只覆盖 11 个项目中的 7 个（缺 `App`、`Agent`、`Infrastructure.Windows`、`Infrastructure.Sqlite`）；且 `:27-30` 使用 `if (!File.Exists(...)) continue;`，**项目被改名或删除时会静默跳过而不是失败**（`静态`）。另有 3 条文档未记录的依赖边：`Application → Execution`、`Infrastructure.Windows → Execution`、`Infrastructure.Sqlite → Monitoring`。

### 2.2 安全与信任边界

**结论：当前发布态无 P0。** 这一结论有两重独立确认：破坏性执行器不可达，且提权链路之外无写入面。命令/脚本注入面干净 —— 全部外部进程调用使用 `ArgumentList` 而非字符串拼接；内嵌 PowerShell 经 stdin 执行、脚本体是 GZIP 常量、零变量插值；WMI 查询参数用 `int`；SQL 全参数化；7z 校验全部走 `-so` 流式，因此没有 zip-slip 写入面；`EnsureWithinRoot` 使用 `Path.GetRelativePath` 而非字符串前缀比较（写法正确）。

门禁设计是本仓库最强的一环：`OperationPolicyEvaluator` 的 13 步判定顺序正确、默认拒绝闭合、plan hash 在提交时**真正重算**、目标绑定到 `SystemId`；`ExecutorGate` 先重校验策略、再校验执行器能力、最后才消费令牌；`SimulationOperationExecutor` 用精确相等而非位与判定。

发现的问题（详见 `B-security-static.md`）：

| 编号 | 级别 | 问题 | 关键证据 |
| --- | --- | --- | --- |
| B3-1 | P1 | 提权 Agent 的 CSV 导出是任意绝对路径写入原语。`ExportAgentMonitorCsvRequest` 在消息白名单内，`DestinationPath` 由调用方给定；`MonitorCsvExporter` 仅校验扩展名必须为 `.csv` 且不得等于本机监控库，随后 `File.Move(..., overwrite: true)`。配合 NTFS ADS（`…\SAM:payload.csv`，`Path.GetExtension` 只取最后一段）可写任意宿主文件的流 | `ProcessCoordinationContracts.cs:152-157` → `DesktopAgentRuntime.cs:499-503` → `MonitorCsvExporter.cs:41-59,139` |
| B2-2 / C6-3 | P1 | 握手有 5 秒期限，但握手后的帧读取只受 Agent 级生命周期令牌约束。管道为 `maxNumberOfServerInstances: 1` + `FirstPipeInstance`；已握手客户端发 4 字节长度前缀后只发一半载荷并挂起，即独占唯一管道，App 侧 5 秒连接超时 → 托盘/清单/工作区/模拟提交全部不可用。**该风险在项目自己的归档里已被记录过**（`docs/Archive/20260915-static-review-fixes/…7cd2b7c.md:128,136-137`），当前修复只覆盖了握手阶段 | `AgentControlServer.cs:287-320,326-341,488-524`；`CurrentUserPipeFactory.cs:31`；`IpcProtocol.cs:15` |
| B2-1 | P1 | 非 `agent.handshake.request` 的消息类型使 `DecodeHandshake` 抛 `InvalidDataException`，该异常既不是 `IOException` 也不触发取消 → 落到"只上报不改变状态"的兜底分支 → 循环继续且永远无法完成握手。并行路径 `Program.cs` 在监听任务 faulted 时 `MarkFailed`，而 `AgentLifecycleStateStore.MarkFailed` **没有回到 Running 的路径** → 此后除 `GetSnapshot` 外全部拒绝 | `AgentControlServer.cs:287-320,342`；`IpcFrameCodec.cs:42`；`AgentLifecycleStateStore.cs:82-90`；`AgentSessionCoordinator.cs:158-173` |
| B6-2 | P2 | 真实崩溃点：`ConservativeCapacity.PlanLogicalUpperBound` 的 `checked` 乘法可抛 `OverflowException`，而所有 UI/规则调用点只 `catch ArgumentException`（`OverflowException` 不是其子类）→ 逃逸到 WinUI → `App_UnhandledException` 只写日志、**不设 `e.Handled = true`** → 进程终止。触发需要导入一个 `Size` 为极大值（如 9e18）的文档，而导入校验不检查数值上界 | `StorageCapacity.cs:101,114,115,118,127,128`；`StorageEditRules.cs:1482`；`StorageStructurePage.xaml.cs:3620`；`EditWorkspace.cs:1556,2146`；`App.xaml.cs:96-109`；`DesktopExportService.cs:138-158` |
| B4-2 | P2 | 用户可配置的 7-Zip 路径只校验"完全限定 + 文件存在"，随后在每次归档轮换时自动执行。Agent 提权时即以 High 完整性执行用户指定的任意二进制 | `DesktopAgentRuntime.cs:401-407`；`ControlledProcessRunner.cs:480-516,495-497,521-523` |
| B5-2 | P2 | 真实写解锁前的门禁缺口：`InMemoryOperationAuthority` 每次调用新建，使 `ExpiresAt` 与一次性语义形同虚设 | `SimulationEditCoordinator.cs:125,308` |
| B5-3 | P2 | IPC 边界不重算 plan hash，跨进程审计链在信任边界处断裂 | `SimulationDocumentRepository.cs:409-423` |
| B7-4 | P2 | 缺少 `packages.lock.json`，使"依赖审计无已知漏洞包"不可复现；`Directory.Build.props:18-21` 又把 NU1900 降级为警告 | `Directory.Build.props:18-21`；`docs/CHANGELOG.md:157` |

**供应链（本次实测补充）**：主审查官实际执行了 `dotnet list WinPool.slnx package --vulnerable --include-transitive` → **22 个项目全部"没有易受攻击的包"，exit 0**（`实测`）。因此 `CHANGELOG.md:157` 的"依赖审计无已知漏洞包"这一**结论属实**，问题只在于仓库内没有留档证据、结论不可复现。随附 7-Zip 为 26.03，`License.txt`（5,188 B）与 `NOTICE.txt`（555 B）均已随产物落盘于 `artifacts\Release\Tools\7zip\`（`实测`），但 `NOTICE.txt` 只记录下载包的 SHA-256，未记录 `7za.exe` 自身的哈希。版本全部精确、无浮动版本、无自定义 feed、无 `nuget.config`（正面项）。

### 2.3 持久化、数据完整性与并发

**归档轮换状态机是本项目工程质量的高点。** 子代理 C 逐步核对 21 步流程后确认 19 步保护到位：PendingSeal 记录先落盘、同卷改名、发布前校验、删除原库前先校验、TRUNCATE checkpoint 校验 `busy`/`log`/`checkpointed` 三项。这与主审查官实测到的 1 GiB 归档产物相互印证。

同时确认的正面项：偏好文件的"原子保存"**确实实现**（`WriteThrough` + `Flush(flushToDisk: true)` + `File.Replace`），且比文档描述更严格；"正常 250 ms 攒批不是丢样"、2 秒延迟阈值、队列拒绝与已接受未保存分离计数、按写入器身份只计一次、单调计时、缺失指标写 NULL 而真实零保持为零、CommitId 五元绑定，全部逐条成立。

发现的问题：

| 编号 | 级别 | 问题 | 关键证据 |
| --- | --- | --- | --- |
| C2-7 / C5-1 | P1 | 数据根迁移可静默回退核心库数据。`wal_checkpoint` 全仓库只出现在监控库；`winpool.db` **永不显式 checkpoint**；迁移文件清单把 `winpool.db-wal/-shm/-journal` 一律排除；`DrainSourceDatabaseHandles` 只做连接池清理 + 排他探针，不 checkpoint；补偿控制 `SqliteMigrationAuditor.HasSameLogicalIdentity` 只比较 schema 版本/表数/**行数**/主键哈希，**不比较非主键负载**，因此 `workspace_state`、`local_inventory_document`、`simulation_documents` 的 UPDATE 全部逃逸。触发前提（迁移时刻 WAL 非空）本身为 `未核实`，但代码无任何拦截 | `StorageLocationManager.cs:963-987,741-773,922-949`；`SqliteMigrationAuditor.cs:23-52`；`MonitoringSqliteStore.cs:95` |
| C4-1 | P2 | 轮换期内存缓冲（8192 条）在 `rotating || paused` 期间承接采样，崩溃即丢；`CompleteAsync` 只在"停止时仍 paused"时按 `durable:false` 计入内存诊断，`DisposeAsync` 直接丢弃缓冲，持久化侧只记 `dropped_samples + 1`。真实缺口可达数千条、账上只有 1 | `RotatingMonitorSessionPersistence.cs:1044-1052,1096-1120,1133-1158`；`MonitorRepositories.cs:196-204` |
| C4-3 | P2 | 归档/ledger 的任何不可恢复不一致会让**整个 Agent** 启动失败退出，而不只是监控不可用 | `Program.cs:149-154,299-306` |
| C3-1 | P2 | 单写者实际是进程内的：`AgentWriteOwnerLease` 是静态字典，跨进程只靠 `Local\` 命名空间互斥量，该命名空间按 Terminal Services 会话隔离 → 同用户双会话可双写同一数据根。项目自己在归档中把此列为 unverified | `AgentWriteOwnerLease.cs:13-15`；`Program.cs:30-38`；`docs/Archive/20260921-monitoring-rotation/README.md:31` |
| C7-1 | P2 | 监控库只读侦察的复制块在 `try` 之外，复制期间异常会永久泄漏一个含整库副本的 `%TEMP%\WinPool-monitoring-inspect-<guid>\` | `MonitoringSqliteStore.cs:266-285` |
| C5-3 | P2 | 切换数据根成功后仍在运行的 App 继续使用静态缓存的旧根，"不访问旧根"在进程级不成立 | `StorageDataLocations.cs:17,27-39`；`docs/Development.md:129` |
| C8-1 | P2 | 提权交接时限不一致（bootstrap 25 s vs Agent 关机步 15 s / 内部 8 s），失败时跳过终态步骤，可能留下"无 App + 旧 Agent 存活" | `AgentShutdownWorkflow.cs:174-202` |
| C6-5 | P2 `未核实` | 消息循环退出后用 `GetAwaiter().GetResult()` 阻塞异步收尾，若 WinForms 同步上下文未卸载则退出卡死 | `Program.cs:228,250,277` |

### 2.4 测试体系质量

先记录正面项：仓库**零 mock 框架**、零 `Assert.True(true)` 占位、唯一的 3 个 `Skip` 是有文档说明的手工性能测量（由本地 `EnvironmentEnabledFactAttribute` 门控）；Execution 的真实写入默认拒绝有完整负向测试（7 项授权绑定全部负向覆盖）；Ipc 的 ACL/帧测试扎实；`RotatingMonitorSessionPersistenceTests` 使用精确的多重集守恒。

问题：

| 编号 | 级别 | 问题 | 关键证据 |
| --- | --- | --- | --- |
| A5-1 | P1 | 架构门在 HEAD 上就是红的（见第 3.2 节实测） | `ArchitectureBoundaryTests.cs:866` vs `Directory.Build.props:6` |
| A5-3 | P2 | 47 个架构用例本质是**源码文本扫描器**：563 条断言中 474 条是 `Contains`/`DoesNotContain`（84%），112 次 `File.ReadAllText`，45 个方法中只有 2 个执行生产代码。这与项目自己的 `Quality.md:128`「不以扫描源代码字符串代替行为验证」直接冲突 | 全文件 |
| A5-5 | P2 | 「App 不自行实现存储规则或写 SQLite」这条规则**没有任何断言**，而 App 仍保留第二条持久化路径 `LocalStorageSystemRepository`（写 `%LocalAppData%\WinPool\Systems\*.json`）。该路径从发布入口不可达，属"死但上膛"的代码 | `MainWindow.xaml.cs:156-158`；`StorageSystemRepository.cs:64-82`；`App.xaml.cs:317-331` |
| A5-7 | P2 | 同一条规则多层重复：格式化资格在 `StorageEditRules.cs:352`、`DiskPartitionPage.xaml.cs:557-558`、`ManageCommandProjector.cs:104` 三处实现（而同文件 `:107` 的删除规则**却**复用了共享实现，说明共享本是意图） | 同上 |
| A5-8 | P2 | 巨型文件：`StorageStructurePage.xaml.cs` 实测 **3,827 行**、`WorkspaceViewModel.cs` 2,122 行、`DiskPartitionPage.xaml.cs` 1,888 行、`MainWindow.xaml.cs` 1,798 行、`EditWorkspace.cs` 2,4xx 行 | 逐文件统计 |
| A5-6 | P2 | 已复核的死代码：`WorkspaceState`、`LocalMachineRecordService`、`LocalWorkspaceStateService`、`TheoreticalPoolCapacity`、`AlgorithmIdentity.RequiresUnverifiedLabel`，以及 4 处不可达的 `MainWindow` 空分支 | A5-6 逐项 |
| A5-9 | P2 | 测试质量缺陷（可核实）：`V048SimulationSemanticsTests.cs:69` 的恒真析取；`EditWorkspaceTests.cs:917-941` 断言一个并不存在的 4 GiB 阈值；`SimulationOperationTests.cs:1233-1236` 用生产公式计算期望值；`StorageLocationManagerTests.cs:989-1029` 无法检出它命名的那类回归；`StorageSystemImportJsonValidatorTests.cs:54-74` 实际上什么都没断言。另有约 130 个 `storage.rule.*` 稳定原因码中只有 11 个被断言，而 30+ 处断言绑定在非契约的英文消息文本上 | 各处 |
| A5-10 | P2 | 标准构建流程整目录替换/删除 `artifacts`（见第 3.1 节实测），会连带删除 `artifacts\test-results` 这一已归档证据目录，与仓库根 `AGENTS.md` 硬规则 4「不得因为测试证据看起来旧就移动或清理」冲突 | `Merge-RuntimeTrees.ps1:117-131`；`Clean-WinPool.ps1:102-107` |
| A5-10b | P2 | `artifacts\Release` 同时是**便携模式数据根**（`StorageDataLocations.cs:23`），而构建会整体删除重建该目录 → 便携模式下构建即销毁用户数据 | `StorageDataLocations.cs:23`；`Merge-RuntimeTrees.ps1:117-131` |
| — | P2 | 覆盖缺口：`WinPool.App`（21,731 行，占 `src` 的 34%）**没有测试项目**，其"覆盖"仅来自架构测试的字符串扫描；`SimulationEditKind.OptimizePool`/`OptimizeDrive` 零行为测试，而 `V048SimulationSemanticsTests.cs:496-501` 宣称覆盖完整操作矩阵 | A.4 |

### 2.5 App 层（VM / XAML / 布局 / 本地化）

状态流总览：这个 App **不是以 XAML 绑定为主的 MVVM**。页面通过 `Frame.Navigate(typeof(Page), ViewModel)` 注入视图模型，页面在 `OnNavigatedTo` 手工订阅事件后用 C# 构建可视树（`HardwarePage.xaml` 只有 4 行；`TestPage.xaml.cs` 只有 9 行）。真实绑定集中在 `TopologyNodeControl.xaml`（44 处 `Mode=OneWay`）。

| 编号 | 级别 | 问题 | 关键证据 |
| --- | --- | --- | --- |
| D3-12 | P1 | `Process.Start(new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true })` 在 `async void` 处理器中**无任何 try/catch**，唯一守卫是 `Directory.Exists`（TOCTOU：卷在两次检查之间卸载时，本该走友好提示的分支会抛 `Win32Exception`）→ 经 `App.xaml.cs:96-109`（只写日志、不设 `e.Handled`）终止进程，未保存草稿丢失 | `DiskPartitionPage.xaml.cs:1907,1899-1903,1884`；`App.xaml.cs:96-109` |
| D4-3 | P1 | 操作区父级是自定义 `FixedWrapPanel`，而子项上写了 20 处 `Grid.Column="1..5"`/`Grid.Row="1"`；`FixedWrapPanel` **只按子项索引** arrange，全文件不读取任何 Grid 附加属性 → 所有 `Grid.Row/Column` 被静默忽略，作者设计的 3 列×2 行操作区实际渲染为"每 2 子项一列"的顺序流 | `StorageStructurePage.xaml:91-97`；`FixedWrapPanel.cs:56-73` |
| D5-2 | P1 | `StorageStructurePage.xaml.cs` 3,827 行中只有 1 处 `AutomationProperties.SetName`；`AddFormRow` 只生成同级 `TextBlock` 标签、未设 `LabeledBy` → 屏幕阅读器用户 Tab 到容量/副本/列数框只听到"编辑框"。（同 App 的 `DiskPartitionPage` 有 9 处） | 各处 |
| D6-L2 | P1 | CSV 写入失败被空 `catch` 吞掉；会话文件创建失败时把 writer/路径置空后既不记录也不发布，而 `LastError = null` 在 `StartSessionFile()` **之前**执行、随后 `IsRunning = true` → 界面显示"正在监控"、曲线正常，**实际没有录制任何数据**，导出时才发现，且导出路径还会报成功 | `MonitoringService.cs:875-881,908-912,149,152`；`MonitorPage.xaml.cs:932` |
| D2-4 | P2 | 结构页自动创建分区只取 `GetPartitionCreateGeometry(0, vdiskSize).DefaultSizeBytes`，**丢弃对齐起点且整条请求不传 `OffsetBytes`**（该字段存在于 `SimulationEditContracts.cs:69`），而分区页 `:1668` 传了 → 两页行为分叉，违反 `Development.md:89`；草稿预览的容量/位置可能与实际生成不一致 | `StorageStructurePage.xaml.cs:1948-1957`；`EditWorkspace.cs:783,806-808,892-901`；`DiskPartitionPage.xaml.cs:1668` |
| D2-4a | P2 `未核实` | 对话框自算的对齐上下界与规则引擎实际接受范围不一致：页面把规则层返回的**未对齐**字节下界四舍五入到整 MiB 后用于**放行**确认按钮，提交时被 `StorageEditRules` 拒绝，且拒绝原因指向的不是真实原因 | `DiskPartitionPage.xaml.cs:862-884,924,938-945`；`StorageEditRules.cs:626,835,1017-1018` |
| D4-1 | P2 | `docs/Quality.md:25` 与 `docs/Development.md:91` 声称右栏宽度"随窗口宽度在 320–420 之间调整"，代码里两页都是**固定** `Width="320" MinWidth="240"`（8 DIP 可拖分隔槽确实存在，只有自适应宽度这一条不成立） | `StorageStructurePage.xaml:11-15`；`DiskPartitionPage.xaml:14` vs 两处文档 |
| D6-1a | P2 | 英文界面下标题栏启动瞬间显示中文：`MainWindow.xaml:154` 的 `Text="真实编辑"` 默认值要等 `RefreshChrome` 才被覆盖，而 `RefreshChrome` 由 `RootGrid_Loaded` 触发、其前置 `await InitializePreferencesAsync()` 之前不执行 | `MainWindow.xaml:154`；`MainWindow.xaml.cs:245,364,659` |
| D6-1b | P2 | `TestPage.xaml` 的中英并排文案**永不覆盖**（`TestPage.xaml.cs` 仅 9 行），因此中文界面下测试页恒为中英混排 | `TestPage.xaml:15,22` |
| D6-2b | P2 | `DiskActivityGraphControl` 硬编码 `"R "`/`"W "`，而 `LocalizationService` **已有** `ReadLabelShort`/`WriteLabelShort` 键 → 中文界面曲线末端仍显示 R/W。这是可直接证明的"字典有键但没调用" | `DiskActivityGraphControl.xaml.cs:154-155`；`LocalizationService.cs:309-311` |
| D2-8 | P2 | 监控页用单一硬编码单位 `$"{bytes / 1073741824.0:F0} GiB"` → 小于 1 GiB 的盘显示为 `0 GiB`，把"有值但很小"渲染成确定的零；大于 1 TiB 显示为 `2048 GiB` | `MonitorPage.xaml.cs:988` |
| D2-9 | P2 | 以 PDH 解析出的 `DiskNumber` 作为监控行与磁盘的关联键，违反 `Development.md:31`「DiskNumber 不能单独作为持久身份」→ 可能与存储空间虚拟磁盘等对象撞号，把另一对象的名称/容量/介质贴到同一行 | `MonitorPage.xaml.cs:89,737-741`；`MonitoringService.cs:92-97` |
| D2-1 | P2 | 结构页自行筛选成员盘并推导 copies/columns/parity 来算 MAX 值，与 `EditWorkspace.NormalizeTierCapacities` 逐字同构 → MAX 按钮、变更点、"应用预览"三处数字可能分歧 | `StorageStructurePage.xaml.cs:3595-3612`；`EditWorkspace.cs:1548-1554` |
| D2-3 | P2 | 至少 8 处裸 GiB↔bytes 算术，违反 `Development.md:48`「单位转换只在输入/显示边界进行」 | `StorageStructurePage.xaml.cs:3216,1541,1643,3495,3527,3547,3690,3722` |
| D3-8 | P2 | `MainWindow` 的 `UISettings.ColorValuesChanged` 与 `RootGrid.ActualThemeChanged` **从未退订** | `MainWindow.xaml.cs` |
| D8-3 | P3 `未核实` | 最小窗口设为 480×300，而管理页最小高度需求约 504 DIP；300 这个数字在任何文档里都找不到 | `MainWindow.xaml.cs:194-199`；`MainPage.xaml:15-17` |
| D6-6 | P3 | `LocalizationService.cs:140` 与 `:400` 重复定义 `FileSystem`（值逐字节相同）。当前行为正确，但未来改一处会静默按位置取胜，且因架构测试不引用 `WinPool.App` 而无法被任何测试发现 | `LocalizationService.cs:140,400` |

规则泄漏（违反 `Development.md:18`「App 不自行实现存储规则」）共 10 条，其中 D2-4 与 D2-4a 最值得先修。

---

## 3. 真实软件测试

本章全部为主审查官在本机对 HEAD 构建产物的实测结果，原始日志与截图见证据目录。

### 3.1 构建

**第一次构建（增量，App 正在运行）：失败。**

```
dotnet build WinPool.slnx -c Release --no-restore -m:1
→ Union merge: 288 shared, 294 App-only, 10 Agent-only, 0 collisions
→ Remove-Item: build\Merge-RuntimeTrees.ps1:127
  Access to the path '...\artifacts\Release\Assets\CAppIcon.ico' is denied.
→ error MSB3073 (Merge-RuntimeTrees.ps1 退出代码 1)
→ 0 警告 1 错误
```

这是**最重要的构建发现**。`Merge-RuntimeTrees.ps1:117-131` 的替换流程是"先 `Remove-Item -Recurse -Force` 整个目标目录，再 `Move-Item` 新树"。因此当目标目录中有文件被占用时，脚本会在**删除进行到一半**时失败，留下一个**混合了旧文件与部分已删除状态**的运行树，且没有回滚。本次实测中 `artifacts\Release` 仍有 575 个文件（对比完整树 592 个），说明确实发生了部分删除。

后果：(a) 一个正在运行的 App 会让构建失败，且失败后运行树处于不一致状态；(b) 若删除阶段全部成功而 `Move-Item` 失败，运行树将完全消失；(c) 便携模式下该目录同时是数据根（`StorageDataLocations.cs:23`），因此这条路径会销毁用户数据；(d) `Clean-WinPool.ps1` 会连同 `artifacts\test-results`（已归档的验证证据）一起删除，与仓库根 `AGENTS.md` 硬规则 4 冲突。

**第二次构建（增量，App 已关闭）：失败。**

```
CSC : error CS2012: 无法打开 "...\artifacts\obj\WinPool.App\Release\intermediatexaml\WinPool.App.dll" 以进行写入
      ... 文件可能被 'Microsoft.UI.Xaml.Markup.Compiler' (38844) 锁定
XamlCompiler error WMC0001: Unknown type 'BooleanToVisibilityConverter' in XML namespace 'using:WinPool_App'
XamlCompiler error WMC0001: Unknown type 'NotificationSeverityConverter' in XML namespace 'using:WinPool_App'
Xaml Internal Error error WMC9999: 未将对象引用设置到对象的实例。
→ 1 警告 4 错误
```

这条同样重要：前一次失败的构建留下了锁住 XAML 中间产物的编译器进程，导致**下一次构建报出 4 条误导性的 XAML 类型错误**（`BooleanToVisibilityConverter` 等类型在源码里明明存在）。开发者极易把这类错误误判为代码问题而浪费时间。诊断信息中没有一处提示"这是残留进程锁"。

**第三次构建（完整清理重建脚本）：成功。**

```
pwsh -File .\build\Rebuild-WinPool.ps1 -Configuration Release -SkipDesktopShortcut
→ 已成功生成。 0 个警告 0 个错误 （用时 41.28 s）
→ Shortcut: D:\...\Program\WinPool\WinPool.lnk
→ Rebuilt Release tree: D:\...\artifacts\Release
→ WinPool.App.exe / WinPool.Agent.exe
```

即：**只有走完整清理重建才能得到干净结果**。项目文档确实提示了"构建前确认已有 WinPool 进程未占用输出目录"（`README.zh-CN.md:54`），但它既没有覆盖"残留编译器进程"这一情况，也没有覆盖"失败后运行树处于半删除状态"这一后果。建议把"停止进程 → 清理 → 构建 → 校验"做成单一入口，并让合并脚本改为**先构建到临时目录、全部成功后再原子替换**（当前实现是先删除目标再移动）。

### 3.2 自动测试

执行项目规定的自动门（`dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1`）：

| 测试项目 | 通过 | 失败 | 跳过 | 总计 |
| --- | --- | --- | --- | --- |
| WinPool.Architecture.Tests | 41 | **6** | 0 | 47 |
| WinPool.Persistence.Tests | 130 | 0 | 3 | 133 |
| WinPool.Infrastructure.Tests | 93 | 0 | 0 | 93 |
| WinPool.Execution.Tests | 42 | 0 | 0 | 42 |
| WinPool.Monitoring.Tests | 26 | 0 | 0 | 26 |
| WinPool.Domain.Tests | 12 | 0 | 0 | 12 |
| WinPool.Ipc.Tests | 9 | 0 | 0 | 9 |
| WinPool.Inventory.Tests | 2 | 0 | 0 | 2 |
| **合计（以上 8 项）** | **355** | **6** | **3** | **364** |

**`dotnet test` 退出码 1。质量门为红。** 详细 TRX 见证据目录 `deepseek-arch-review.trx`。

6 项失败的完整清单（TRX 提取行号）：

| 测试 | 失败行 | 断言 | 判定 |
| --- | --- | --- | --- |
| `ProductFacingVersionUsesTheRepositoryVersionSource` | `:866` | 期望 `<WinPoolVersionIteration>5</WinPoolVersionIteration>`，实际为 `6` | **测试过期**。`5872374`（V0.56 版本提升）只改了 props/README/CHANGELOG/Product/Development，未更新该断言 |
| `TitleBarProvidesStorageSystemSelector` | `:1013` | 期望 `Width="320"` | **测试过期**。标题栏系统选择器现为 260 DIP（`MainWindow.xaml:123`），且 `Development.md:81` 与 `docs/Quality.md:9` 都记录为 260 |
| `EditorPagesOwnTheirSurfacesWithoutSectionTitles` | `:547` | 期望 `StackPanel MinWidth="412"` | **测试过期**。`Development.md:91` 明确记录"不再强制 412 DIP 内容宽"，该写法已被有意移除 |
| `MonitorPageBackgroundModeHasStableKeyboardAccessKey` | `:1171` | 期望 `MonitorPage.xaml.cs` 含 `SetRateAsync` | **测试过期**。该标识在源码中 0 次出现 |
| `NotificationShellKeepsThreeSimpleCardsAndDeveloperOnlyDetails` | `:1408` | 期望 `MainWindow.xaml.cs` 含 `UpdateIssueStates(` | **测试过期**。该标识在源码中 0 次出现；同一断言的上一行还在断言 `DoesNotContain("DismissMonitorIssue")`，说明改名属于有意重构 |
| `DevelopmentPageRemainsGatedAndShowsOnlyInMemoryMessages` | `:1313` | 期望详情浮层的直接子元素是 `TextBox` | **测试过期**。`4825240` 为了给文本框加不透明底板而引入 `Border` 包裹（`DevelopmentPage.xaml:183-206`），把直接子元素变成了 `Border`。**该提交同时改了 XAML 和测试文件，但没有改这条断言** |

**判定与严重度**：6 条**均为测试侧的过期断言**，不是产品缺陷 —— 产品行为与当前文档一致，是测试在冻结已被有意废弃的实现细节。每一条的"肇事提交"已用 `git log` 定位：

| 失败断言 | 被哪个提交打破 |
| --- | --- |
| `:866` 版本号期望 5 | `5872374`（2026-09-24 10:42，唯一改动是 `Directory.Build.props`：iteration 5 → 6） |
| `:547` `StackPanel MinWidth="412"` | `24828d8`（2026-09-24 10:24，"Keep partition offsets on one line and align controls"） |
| `:1013` 选择器 `Width="320"` | `22e7b92`（2026-09-23 20:40，"Unify WinPool control sizing and draggable page splits"，其中在选择器区域把宽度改为 260 DIP） |
| `:1171` `SetRateAsync` | `d11e235`（2026-09-24 10:01，"Fix editor property sizing and global monitor alerts"） |
| `:1408` `UpdateIssueStates(` | `d11e235`（同上；同一断言的紧邻行还在断言 `DoesNotContain("DismissMonitorIssue")`，说明改名属于有意重构） |
| `:1313` 详情浮层直接子元素须为 `TextBox` | `4825240`（2026-09-23 21:54，"Fix message surfaces and compact settings layout"，为文本框加不透明底板而引入 `Border` 包裹）。**该提交同时改了 XAML 和测试文件，却没有改这条断言** |

这反而更值得关注：它暴露出三件事：

1. **质量门没有被执行。** `docs/Quality.md` 的最新记录（2026-09-23，由提交 `40d8a9b` 写入）写 `Architecture 47/47 passed、0 failed、0 skipped`。从该提交到 HEAD 共 **13 个提交**（`f89c9e3`…`5872374`），其中至少 6 个直接打破了上述断言。而 `5872374` 的 `CHANGELOG.md` 自己写着"未运行自动测试"。因此"记录在案的通过"与 HEAD 之间存在一段**未验证窗口**，且窗口内多次产生红灯而无人发现。
2. **架构测试的形态决定了它会不断制造这类噪声。** 98.5% 的断言是源码文本匹配，任何格式化、重命名或包裹一层容器都会破坏它们，即使行为完全正确；反之，"保留字符串但删除逻辑"却可以通过（例如 `:822-824` 用 `IndexOf` 排序，token 被删除时 `-1 < n` 仍成立）。这类断言既误报又漏报。建议把版本号断言改为从 `Directory.Build.props` **读取**而非硬编码，把布局断言改为可实例化的最小宿主验证，并保留真正有价值的结构性检查（csproj 引用图、纯层纯净性、单写者、运行树布局）。
3. **归档的验证记录不能当作当前状态的证明。** 本报告建议：在任何"已通过"的记录旁强制标注对应的 commit SHA，并在 CI 或提交钩子中运行架构测试 —— 仓库内目前**找不到任何 CI 配置**。

另外，`Persistence` 的 3 个跳过项是项目自己文档化的手工性能测量（1 GiB 阈值轮换、归档参数、压缩参数比较），本次未执行，不记为通过也不记为失败。

### 3.3 原生界面实测

工具链：UIAutomation（`UIAutomationClient`/`UIAutomationTypes`）+ `winapp ui`（`inspect`/`screenshot`/`list-windows`）+ 真实鼠标注入。测试对象为 `artifacts\Release\WinPool.App.exe`（HEAD 构建）。

**启动与生命周期**

| 项 | 结果 |
| --- | --- |
| 首次冷启动 | 成功。主窗口 `WinPool` 1440×900，另有独立欢迎窗口 `欢迎使用 WinPool ！` 720×480 |
| 单实例 | 正确。第二次启动的进程立即以 exit code 0 退出，激活已有窗口 |
| 自动采集 | 成功。开发页消息列表出现两条「信息 / inventory / 采集成功」 |
| 崩溃日志 | `%LocalAppData%\WinPool\Diagnostics\app-crash.jsonl` 全程无新增 —— **但这一条不能证明没有崩溃，见 3.6 的重要更正** |
| 关闭 | `CloseMainWindow` 关闭 `WinPool` 主窗口后 App 退出。**注意：这不是"App 退出即 Agent 退出"的证据 —— Agent 是独立托盘进程，App 的关闭路径不通知它（见 3.6）** |

**8 页导航逐页核对**（每页点击后轮询页面特征文本，250–500 ms 内全部就绪）

| 页面 | 结果 | 观察到的关键内容 |
| --- | --- | --- |
| 管理 | 通过（250 ms） | 拓扑、系统摘要表、命令区（5 个按钮，禁用态带原因提示） |
| 存储结构编辑 | 通过（250 ms） | 拓扑、`+ 新建池`、待应用动作区、撤销/重做/放弃/应用/创建池/解散池/显示热备层/显示退役层 |
| 磁盘分区编辑 | 通过（250 ms） | 磁盘与 8 个分区、联机/脱机/初始化/转换 GPT/删除/扩展/压缩/打开资源管理器全部禁用并带具体原因 |
| 测试 | 通过（250 ms） | 「磁盘测试 / Disk tests」「WinPool 2.0」路线说明（中英并排，见 D6-1b） |
| 监控 | 通过（500 ms） | 运行时长 `00:03:32`、两张曲线、3 块磁盘行、刷新率、自动配色、事件、导出 |
| 开发 | 通过（特征文本匹配方式不同，人工确认） | 消息列表 3 列 × 4 条、`人工智能入口，功能正在开发中。` |
| 设置 | 通过（250 ms） | 主题/主题色/语言/欢迎页面/开机启动/开发者模式/真实编辑/创建 MSR/分区缝隙隐藏阈值/数据存储位置/7Z/恢复默认 |
| 硬件 | 通过（250 ms） | Computer / System / Mainboard / CPU … 十段报告，刷新与导出按钮 |

> 说明：早期一次快照曾显示"点击硬件后正文仍是测试页"，经复核为**测试脚本在页面完成渲染前读取了 UIA 树**的度量伪影，不是产品缺陷。改用以页面特征文本轮询后，8 页全部在 250–500 ms 内正确呈现。此项记录在此以避免后续审查者重复误判。

**语言与主题**

- 语言下拉展开实测，选项为 `跟随系统 / 中文 / English`。
- 切换到 `English` 成功，界面切换为英文（如导航「Settings」）。
- 切回中文成功。
- 开发者模式门（实测两次往返）：
  - 关闭 → 导航项 8 → **5**，可见标签 `管理,存储结构编辑,磁盘分区编辑,监控,设置`（硬件/测试/开发被隐藏）
  - 打开 → 恢复 **8** 项 `硬件,管理,存储结构编辑,磁盘分区编辑,测试,监控,开发,设置`
  - `%LocalAppData%\WinPool\app-settings.json` 的 `DeveloperMode` 正确持久化
- 结论：`docs/Development.md:135` 描述的开发者模式门**行为属实**，且是本次实测中最干净的功能之一。

> 关于导航项刷新：子代理 D 曾静态推断 `MainWindow.xaml:56` 的 `x:Bind ShellNavigationItems`（默认 OneTime）配合 `BuildShellNavigation` 的 `Clear()/Add()` 可能导致列表不刷新。上述实测证明**列表确实会刷新**，该推断不成立，已撤销。

**无障碍实测（发现的主要问题）**

| 问题 | 实测证据 |
| --- | --- |
| 导航项可访问名称是类型名 | 8 个导航 `ListItem` 的 UIA `Name` 全部为 `WinPool.App.ViewModels.ShellNavigationItem`（`className = ListViewItem`）。可见标签只作为子 `Text` 元素存在。**切换到英文后仍为类型名**，说明与本地化无关。屏幕阅读器会把同一个类型名播报八次。根因已定位：导航项模板的 `Grid` 根（`MainWindow.xaml:80-86`）没有设 `AutomationProperties.Name`，UIA 因此回退到 `ToString()` |
| 设置页 9/30 控件无可访问名称 | `ThemeOptions`、`AccentOptions`、`LanguageOptions`、`WelcomeButton`、`StartupAgentSwitch`、`MsrSwitch`、`PartitionGapBox`、`DataLocationOptions`、`ResetAllButton` |
| 两个不同开关共用同一名称 | 标题栏 `LocalRealOperationsSwitch` 与设置页 `SettingsExecutionModeSwitch` 的 UIA `Name` 都是「真实编辑」，但语义与作用完全不同。根因是两处从**同一个本地化键** `["LocalRealOperations"]` 取名（`MainWindow.xaml.cs:660-662` 与 `SettingsPage.xaml.cs:1007-1009`），而 `["ExecutionMode"]` 键已存在，可直接用于区分 |
| 结构页表单无可访问标签 | 3,827 行中仅 1 处 `AutomationProperties.SetName`；动态生成的属性行只产生同级 `TextBlock` 标签，未设 `LabeledBy` |

**正面项**：禁用态原因提示覆盖很好 —— 分区页每个禁用控件都带具体 HelpText（如「请选择一个模拟磁盘、分区或未分配空间。」「请选择一个未初始化的模拟磁盘。」）；管理页「刷新本机信息」带「只有本机系统可以重新扫描；模拟系统没有可扫描的本机存储。」。设置页当时 0 个控件被禁用，因此上面的"无名称"不是禁用态造成的。

**布局实测（最小宽度）**

把窗口强制到 480 DIP 客户端宽度后，**唯一**超出客户区的元素是标题栏 `ActiveSystemSelector`（ComboBox，x=1339，宽 260，右边缘 1599 vs 窗口右边缘 1480）。该控件在 XAML 中是固定 `Width="260"` 套在 `MinWidth=180 MaxWidth=280` 的宿主里（`MainWindow.xaml:107-130`），宿主无法把它压窄。静态复核确认：`Development.md:81` 与 `DesignTables/Controls.md:12` 只固定了 260 DIP 这个宽度，**没有定义任何窄窗降级规则**，`Quality.md:15` 又把 480 DIP 窄窗记为未完成原生核对。因此这应判定为**未处理的情形**，而不是"已文档化的窄窗行为"。恢复 1440×900 后布局正常。`Quality.md:25` 记录的 `partition-480.png` 场景本次未复现（当时是分区页，本次是设置/硬件页），因此只报告这一条可重复的观察，不断言分区页宽窗行为。

**多显示器/DPI 观察（未核实归因）**

本机主显示器 3440×1440、副显示器位于 `X=3440` 起的 768×1024。启动时应用把主窗口放在 `(1000, 270)`，尺寸 1440×900，物理边界完全落在主显示器内，**未观察到跨屏错位或裁剪**。因此本次未发现 DPI 缺陷。但需要指出：`docs/Quality.md` 从未做过 DPI/多显示器矩阵（多次记录为 unverified），本次同样只在单一 DPI 配置下验证，**不构成 DPI 结论**。

### 3.4 监控与归档（真实 1 GiB 规模）

数据根中存在一次**真实完成**的 1 GiB 轮换归档：

- 归档包：`MonitoringArchives\packages\monitoring-20260921T105522Z-20260923T190101Z-d5b3177ef746419aac38f46ab09ddc9b.7z`，47,491,336 字节
- 账本：`archive-ledger.json` 记录 `stage: 5`、`sampleCount: 3403464`、`databaseBytes: 1073741824`、`metadataComplete: true`、40 个 `sessionIds`、`lastError: null`
- `sealed\` 目录已空，"发布后释放原库"这一步确实执行了
- **独立校验（实测）**：用随附的 `artifacts\Release\Tools\7zip\7za.exe t` 校验归档：

```
7-Zip (a) 26.03 (x64)
Type = 7z    Method = LZMA2:26    Solid = +    Blocks = 1
Everything is Ok
Files: 2    Size: 1073743571    Compressed: 47491336
```

即 1 GiB（3,403,464 条样本）压缩到 46 MiB，内容完整、可解压。**归档链路的核心承诺在真实规模上成立**，这是本次审查中最有说服力的正面证据。

同时实测到两处与监控相关的问题：

1. **历史崩溃证据**：`Diagnostics\monitor.jsonl` 中有 2026-09-21 的两条记录 —— `DiskActivityGraphControl.RenderCore()` 与 `MonitorPage.UpdateDiagnosticsAndStorageEvents()` 抛 `COMException (0x8000FFFF)`。日志的存在说明页面级异常被捕获并记录（良好），但这两条是真实发生过的渲染/轮询异常。
2. **当前监控正在连续写入**：`monitoring.db` 从 501 MB 增长到 511 MB（约 11 分钟内），符合 20 Hz 采样的预期，未观察到写入停滞。

### 3.5 数据根取证

`%LocalAppData%\WinPool` 实测内容：

| 项 | 观察 |
| --- | --- |
| `winpool.db` | 16,994,304 B，持续更新 |
| `monitoring.db` | 501 → 511 MB，持续增长（阈值 1 GiB） |
| `agent-settings.json` / `app-settings.json` | 结构正确，`FormatVersion: 1` |
| `storage-location.json` | `{"mode":"Standard"}` |
| `MonitoringArchives\` | 1 个归档包 + 账本，`sealed` 与 `packages` 结构符合文档 |
| `Diagnostics\` | `app-crash.jsonl`（1 条历史）、`monitor.jsonl`（2 条历史） |
| `Runtime\agent-endpoint.json` | 协议版本 11、管道名含 SID 哈希与随机 nonce、PID |
| **`app-settings.json.tmp-*` 孤儿文件** | **6 个**，日期跨 2026-09-21 至 2026-09-24 |

最后一项是一个**新发现**（本次审查首个独立发现）。`WindowsServices.cs:205-249` 的 `SaveAsync` 是"写临时文件 → `File.Replace`/`File.Move`"的原子保存实现，但**临时文件的清理只依赖成功路径的 `Replace`/`Move`**：`try` 块内任何一步抛异常（序列化、`FlushAsync`、`Flush(true)`、`Replace`、`Move`），临时文件都会永久留在数据根，且没有任何清理逻辑、没有日志、没有用户可见提示。

观察到的事实：6 个孤儿文件的日期分布在 4 天内（09-21 一个、09-23 两个、09-24 两个、外加一个 09-23 19:12），说明这不是一次性事故而是**反复发生的失败写入**。字节数（241/350/358）显示它们是被完整写入的偏好文件内容，即失败发生在写入之后、`Replace` 阶段。可能原因包括与另一进程并发写同名目标、杀毒/索引占用、或进程在替换窗口内退出。无论原因为何，当前实现"失败即静默泄漏 + 无诊断"是不合格的。

严重度 P2。建议：`try/finally` 中删除临时文件（失败也删）、把失败写入 `Diagnostics`、并在启动时清理超过一定年龄的孤儿临时文件。

**一个曾被怀疑、现已排除的现象**：本次测试中曾观察到 `WinPool.App.exe` 已退出而 `WinPool.Agent.exe` 仍在运行并继续写库（6 秒 +118 KB）。这**不是缺陷**，而是设计行为，且已被独立复现确认：先启动托盘 Agent、再启动 App、然后只用 `CloseMainWindow()` 关掉主窗口 → App 在 **266 ms** 内干净退出（无崩溃、无事件日志条目），而同一个 Agent（PID 不变）在其后 90 秒的全部 30 次轮询中始终存活、`monitoring.db` 单调增长（523,005,952 → 524,693,504 B），endpoint 文件仍指向该 PID。

依据：Agent 是**每用户独立托盘进程**（`Development.md:19`、`Product.md:80`），不是 App 的子进程（`NamedPipeAgentConnection.cs:87-99` 的 `Process.Start{UseShellExecute=true}` + `Dispose()`，`src/` 下无任何 `CreateJobObject`/`AssignProcessToJobObject`）；`AppExitSignal` 方向为 **Agent → App**；`AgentShutdownWorkflow.cs:174-202` 更把"仍有受管进程存活"作为**故意不退出**的条件。这一点在 `docs/Archive/V0.2/08_多进程托盘与IPC架构.md:7` 就有原始设计：「关闭主界面后，托盘图标仍存在，监控可以继续。」

因此本报告**不把"孤儿 Agent"列为缺陷**，也不建议改变该行为。

### 3.6 `WinPool.App.exe` 反复崩溃，且项目自有崩溃日志对它是盲的（P1，本报告最重要的运行期发现）

这一节是初稿写完后，由独立验证者指出、并经主审查官亲自核实的更正与新发现。

**证据来源**（均为标准 Windows 机制，与 App 代码无关，因此不受 App 自身日志缺陷影响）：

`C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_WinPool.App.exe_*` 共 6 份报告，事件日志 `Application/Application Error 1000` 一一对应：

| 时间（本地） | 版本 | 故障模块 | 异常代码 | 异常偏移 | WER BucketId |
| --- | --- | --- | --- | --- | --- |
| 2026-09-22 11:44:05 | 0.5.5.0 | `combase.dll` | `80004003` | `0x261d2` | `3412c41e…` |
| 2026-09-23 13:27:09 | 0.5.5.0 | `combase.dll` | `80004003` | `0x261d2` | `3412c41e…` |
| 2026-09-23 14:43:04 | 0.5.5.0 | `combase.dll` | `80004003` | `0x261d2` | `3412c41e…` |
| 2026-09-23 20:17:02 | 0.5.5.0 | `combase.dll` | `802b000a` | `0x261d2` | `6c1f360d…` |
| 2026-09-24 10:14:08 | 0.5.5.0 | `combase.dll` | `80004003` | `0x261d2` | `3412c41e…` |
| 2026-09-24 11:17:14 | **0.5.6.0** | `combase.dll` | `80004003` | `0x261d2` | `575b8f42…` |

关键事实：

1. **5 次崩溃签名完全一致** —— 同一故障模块、同一异常代码、同一偏移（`0x261d2`）、同一 BucketId。异常代码 `0x80004003` 即 `E_POINTER`（无效指针）。这是**同一个缺陷反复发生**，跨 0.5.5.0 与 0.5.6.0 两个版本、跨 3 天。
2. **崩溃发生在正常使用过程中，不是只在启动时** —— 时间点分散在上午、下午与晚间。
3. **项目自有崩溃日志对此完全不可见。** 同期 `%LocalAppData%\WinPool\Diagnostics\app-crash.jsonl` **只记录了 1 条**（09-23 20:17，且内容是一类 `XamlParseException`，与上面 6 次 COM 崩溃都不同）。原因是 `App_UnhandledException`（`App.xaml.cs:96-109`）只覆盖 XAML 层未处理异常，COM 级访问冲突不会走到它。
4. **该处理器还不能阻止进程终止** —— 它写入日志后**不设置 `e.Handled = true`**（`App.xaml.cs:96-109` 全文已核对）。因此即使是它能捕获的异常，App 也会照常崩溃。这一点与子代理 B 的 `B6-2` 独立结论一致（那条讲的是 `OverflowException` 逃逸路径）。

**对产品的影响**：这是本次审查中**唯一有直接用户可见后果的稳定性缺陷** —— 用户会遇到"WinPool 已停止工作"对话框，而产品自己的诊断目录里什么都没有，用户和开发者都无法定位。同时它解释了 `docs/Quality.md` 里多次出现的"全黑截图""录屏提取帧全黑"等现象的部分环境噪声来源。

**严重度判定为 P1 而非 P0**，理由：崩溃发生在 `combase.dll` 的 COM 调用边界，**不涉及真实存储写入**（该能力本就不存在），也未观察到数据损坏（`winpool.db`/`monitoring.db` 在崩溃后仍可正常打开与增长）。

**处置建议（按优先级）**：
1. 让崩溃可观测：加 `AppDomain.CurrentDomain.UnhandledException` 与 `TaskScheduler.UnobservedTaskException` 处理器，并考虑注册 Windows 错误报告本地转储（`LocalDumps`）以便拿到真实调用栈；仅在确实可恢复时设置 `e.Handled = true`。
2. 用 WER 报告中的 BucketId 与偏移 `0x261d2` 作为回归基线；拿到一次完整调用栈就能定位是哪个 COM 调用（`combase.dll` 的 `E_POINTER` 常见于向 WinRT/COM 接口传了未初始化的接口指针，或 `GetActivationFactory`/`RoGetActivationFactory` 的结果未检查）。
3. 把"自有崩溃日志是否覆盖该崩溃类型"写成一条可核对的预期，避免再次出现"日志干净所以以为稳定"。

**未核实项**：崩溃的复现步骤与具体调用栈（本次未取得转储）；`0x261d2` 偏移对应的具体 `combase` 函数。建议在有转储的条件下补齐。

---

## 4. 产品设计评价

> 本节由子代理 E 基于 `docs/Product.md`、`docs/UnifiedModel.md`、`docs/DesignTables/*`、`docs/Design/*`、两份 README、`CHANGELOG`、`Quality` 与源码交叉核对得出，主审查官补充了三处原生实测证据。

### 4.1 定位与价值主张

产品定位（`Product.md:9`）是"Windows 存储结构管理软件，以统一桌面界面呈现存储拓扑、监控、管理和可审阅的编辑"，并明确"不创造 Windows 无法实现的存储结构，不以界面能画出来作为配置合法性的依据"。这个定位是**清晰且诚实的**，而且与代码现状一致：V0.56 只读采集 + 模拟编辑，真实写入明确关闭。

与用户手上已有的工具相比，WinPool 的差异化在于**关系呈现**而不是能力：

| 现有工具 | 覆盖 | WinPool 的差异 |
| --- | --- | --- |
| `diskmgmt.msc` | 分区/卷操作（含真实写） | WinPool 把池/层/物理盘/虚拟盘/OS 盘/分区/卷放进同一关系视图，但**不能写** |
| 设置 → 存储空间 | 池/层/虚拟盘（含真实写） | WinPool 跨"真实事实 + 模拟草稿"统一呈现，且能表达池/层/盘/分区的关系 |
| `diskpart` / PowerShell | 全能力（真实写） | WinPool 提供可审阅的净差异预览与规则原因 |
| CrystalDiskInfo / WizTree | 硬件与容量盘点 | WinPool 额外提供十段硬件只读报告与来源追溯 |
| Windows Admin Center / S2D | 企业级池管理 | 面向单机、便携、无服务、无安装 |

**结论**：在"不能写"的前提下，WinPool 当前提供的价值主要是**理解与规划**（看清结构、试算容量、验证配置合法性），而不是**执行**。这是一个自洽但**受众窄**的定位：它假设用户已经拥有存储空间/分层存储，并且愿意先在一套模拟器里推演。产品文档没有回答"谁在什么场景下必须用 WinPool 而不是 `diskmgmt` + 设置页"。

### 4.2 宣称与实现的对照（抽查）

| 宣称（含位置） | 实测/代码 | 判定 |
| --- | --- | --- |
| `README.zh-CN.md:7` 当前版本 V0.56 | `Directory.Build.props:4-9` → 0.5.6 | **属实** |
| `README.zh-CN.md:7` 真实存储结构修改尚未开放 | `LocalStorageMutationExecutor` 无条件 `Rejected`、生产零调用点 | **属实** |
| `README.zh-CN.md:21` 监控记录达 1 GiB 轮换并以随附 7-Zip 归档 | 数据根中真实存在 1 GiB → 46 MiB 的归档产物，`7za t` 校验通过 | **属实**（本次最有力的正面证据） |
| `README.zh-CN.md:24` CSV 仅导出当前活动监控库 | UI HelpText 明确：「仅导出当前活动监控数据库；已归档的历史记录不会包含在此导出中。」 | **属实且提示到位** |
| `README.zh-CN.md:30` 软件不查看故障日志文件内容 | 开发页只投影内存消息，不读取 `Diagnostics` | **属实** |
| `Quality.md` 最近记录 `Architecture 47/47 passed` | HEAD 上 41/47，6 项失败 | **已失效**（见 3.2） |
| `Development.md:91` / `Quality.md:25` 右栏在 320–420 DIP 间随窗口调整 | 两页 XAML 均为固定 `Width="320" MinWidth="240"` | **与实现矛盾** |
| `Quality.md:9` 开发页「消息列表无表头」 | 静态复核发现 `DevelopmentPage.xaml` 的 3 个区域 Border 内均有 `TextBlock` 标题（`MessageList`/`EmptyMessageListText` 等），而架构测试 `:1296` 只断言不存在 `MessageListHeader` 这一**特定名字** | **表述与实现不一致**：断言用名字而非结构来定义"无表头"，于是标题存在也能通过。属断言设计问题叠加文档表述问题（见 4.5） |
| `Quality.md:11` 当前 `TestPage.xaml` 只有静态规划说明，没有子块或分隔槽 | 与源码一致 | **属实**（这一条反而是诚实记录的范例） |
| `Product.md:20` 不包含任何真实存储写操作 | 同第 2 行 | **属实** |
| `Product.md:80` 采集、显示、缓存与导出保持原值，不做脱敏 | 未发现脱敏/隐私开关代码 | **属实** |
| `CHANGELOG.md:157` 依赖审计无已知漏洞包 | `dotnet list package --vulnerable --include-transitive` → 22 项目全部无漏洞，exit 0 | **结论属实，但仓库内无留档证据，不可复现** |

### 4.3 交互设计逐项评价

| 设计决定 | 用户可见后果 | 评价 |
| --- | --- | --- |
| 已有对象按 Enter 独立提交改名、无撤销（`Product.md:5`） | 误改名无法回退，且改名的"提交"与其他结构修改的"草稿-应用"模型不同 | **有风险**。身份用可靠 ID、名称仅用于显示，这降低了技术风险，但用户仍会感到不一致：同一页面上有的改动要"应用"，有的立即生效且不可撤销 |
| 结构页净差异动作面板 vs 分区页及时提交（`Product.md:57`） | 两个编辑页提交模型不同，用户需要记住"这一页要先预览后应用，那一页点了就生效" | **可辩护但不友好**。分页的复杂度不同（结构变更涉及多对象约束，分区操作局部），但产品未在界面上解释这一差异 |
| 全部编辑无 undo/redo（结构页有撤销/重做，分区页无） | 分区页误操作只能反向再操作一次 | **缺口**。`Product.md:57` 明确分区页"每次动作单独提交"，但没有补偿机制 |
| 最多 3 张卡、普通 8 秒 / 错误 20 秒、不合并（`Development.md:103`） | 短时间内多条消息会挤掉最早的一张；重复消息各自占位 | **部分缺陷**。三条子问题：(a) 淘汰**不区分严重级别**（`GlobalNotificationService.cs:307-321`），一条错误卡会被三条后续信息卡挤掉，而这正是错误最需要留存的时候；(b) "悬停或键盘焦点停留时暂停自动关闭"是**设计文档已采纳但代码不存在**的行为 —— `docs/Design/WinPool-Information-System-Plan.md:45` 与 `CHANGELOG.md:81` 都称已实现，实际 `MainWindow.xaml.cs:1529-1535` + `GlobalNotificationService.cs:251-261` 只按固定时间到期；(c) 被挤掉的消息仍保留在历史里，这一缓解措施是有效的 |
| 错误卡提示去"开发页消息列表"看详情，而开发页默认隐藏（`Product.md:78,84`） | 开发者模式关闭时，错误卡指引用户去一个看不见的页面 | **缺陷（死胡同）**。错误卡正文与 tooltip **无条件**写「完整详情在开发页消息列表」（`NotificationCard.xaml.cs:133,173-178`），而该页默认隐藏，`Development.md:107` 自己也承认"不另加开启模式判断"。要么文案应区分模式，要么错误详情应有另一个可达入口 |
| 删除分区的确认框只在分区**含已用数据**时出现（`DiskPartitionPage.xaml.cs:1864-1871`） | 空的 EFI / MSR / 新建分区**一键即删**，无确认、无撤销 | **风险**。`Plan.md` 即将接入真实执行，而确认真实破坏性操作的唯一门控是"该分区是否含数据"这一启发式判断；空的 EFI 系统分区被误删在真实环境下同样不可逆 |
| 存储 1 GiB 监控数据库但**不提供历史查看**，也无占用可见性与清理入口（`Product.md:92,96`） | 用户积累了大量数据却无法在软件内回看，也看不到它占了多少盘、无法在软件内清理，只能导出 CSV 或手工解开 7z | **最大的产品悖论**。这是"为研究/取证而设计"的痕迹：数据被完整保存、严格校验、诚实记账，但产品不为用户提供消费或管理它的路径。作为 1.x 的范围裁剪可以理解，但应当在 UI 上把"这是为后续版本/外部工具准备的数据"以及当前占用讲清楚 |
| 欢迎页 meme 图（`assets/welcome/*.png`，8 张）与欢迎窗弹出策略 | 首次启动显示随机表情图与"最好的 开源免费的 Win 平台存储系统工具。"；且在没有托盘 Agent 时**每次冷启动都会弹出**，`UserPreferences`（`Preferences.cs:38-49`）中**没有**"不再显示"字段 | **与定位冲突且无法关闭**。产品其余部分（文档、提示文案、安全叙事）极其严谨克制，欢迎页的营销式措辞与表情图包属于另一种语域（见 4.4）；每次冷启动重现同一个无关闭选项的窗口会加速用户对它的免疫 |
| 开发者模式默认关闭、开启后显示硬件/测试/开发三页（`Product.md:78`） | 普通用户看不到硬件报告，但 App 启动时**照样自动执行完整硬件采集** | **实测行为正确，但性价比可疑**：**硬件页是纯只读的**（`Product.md:80`），把它与"测试""开发"一起隐藏在开发者模式后面，等于把一项普通用户也会需要的只读信息（CPU/内存/GPU/网络）藏起来；而默认用户仍承担了完整硬件采集的成本（`Development.md:73` 规定 Agent 每次启动自动执行存储+完整硬件两次采集），却没有出口。产品文档没有解释为什么只读硬件报告需要开发者模式 |
| 标题栏常驻「真实编辑」开关 | 在一个明确"不包含任何真实存储写操作"的版本里，标题栏最显眼处有一个叫"真实编辑"的开关 | **有误导风险，且有一个更硬的表达层冲突**。开关的实际作用是请求以管理员身份重启（`MainWindow.xaml.cs:828-855`），`CanUseRealMode` 依赖提权状态；但三点叠加使问题加重：(a) `:671` **无条件** `IsEnabled = true`；(b) 以提权参数启动时 `:167-170` 直接 `TrySetExecutionMode(Real)` 而**不再询问**；(c) 打开它**解锁零个功能**。这与 `Product.md:27`「不得预选或持久化同意」在表达层直接冲突 |

### 4.4 信任、安全与预期管理

产品在安全叙事上是**克制且具体**的：明确区分模拟/真实执行（`SimulationShort`/`RealShort`/`LocalRealOperations`）、预览警告文案写明"本机存储写操作尚未开放，所有本机修改按钮仍保持禁用"、`Plan.md` 把未来真实操作的目标锁定到单一物理机与单一磁盘、`Product.md:27` 明确"UAC 和 Real 模式本身不是授权"。这套设计比多数同类项目严谨。

但实测发现两处**与安全叙事直接冲突的用户可见内容**：

1. **欢迎页宣称"最好的 开源免费的 Win 平台存储系统工具"**（`LocalizationService.cs:277-281`，实测原文）。而 `README.zh-CN.md:66` 明确写「本仓库未对 WinPool 自有代码授予任何许可证，保留所有权利」，且线上仓库为 private。因此"开源"与"免费"两个词在产品自己的法律声明面前都不成立。
2. **欢迎页宣称"支持 win 原生软 Raid 和高性能分层存储池"**。当前 V0.56 不包含任何真实存储写操作（`Product.md:20`），测试页自己说完整测试工作区属于 2.0。因此这句话描述的是路线图而非可用能力，出现在**首次启动的第一个界面**上。

这两条不是代码缺陷，而是**预期管理缺陷**：用户在第一屏获得的印象（开源免费、能管软 RAID 和分层池）与产品随后提供的现实（私有、未授权、只读+模拟）之间的落差，是后续所有信任问题的源头。

**一处需要澄清的误判（避免后续审查者重复）**：实测时硬件页在"模拟系统"选中态下显示 `Computer → 主机名 = 超多分区系统盘`（模拟机名），而 `System → 名称/版本/内部版本` 显示 `Windows 10 Pro / 22H2 / 19045.7463`（本机真实系统），看起来像"真机数据泄漏进模拟报告"。经代码核对，**这不是数据泄漏**：`HardwareReportProjector.Project` 只读取**选中系统**的文档（`HardwareReportProjector.cs:14-16`），模拟系统的事实由 `WinPoolSimulationFacts.cs:101-105` 从模拟模型写出；之所以数字像本机，是因为内置模拟夹具被**刻意做成与本机一致**（`SimulationLayouts.cs:14-15,40,114` 的 `Windows10Pro_22H2` = 10.0.19045/22H2/7463，`CHANGELOG.md:173` 记录过"与本机一致"这次改动）。

因此真实风险是**作用域预期管理**：同一页上，主机名是模拟值、OS 版本是真值，用户无法分辨哪些数字属于哪个作用域。修法是报告头部加常驻作用域标识，而不是回退夹具 —— 夹具与本机一致本身是有意的测试便利。

另外，`Plan.md` 计划允许对特定物理盘做破坏性操作。这是一项**开发阶段**的授权（`Plan.md:11-14`），范围写得清楚；但它与"产品内真实操作仍须由用户当次明确选择、预览并确认"之间的边界，在文档里是靠一句话区分的。真实写入解锁前至少有三个用户可见前置缺口：

1. 删除确认只由"分区是否含已用数据"决定（`DiskPartitionPage.xaml.cs:1864-1871`），空的 EFI/MSR/新建分区一键即删、无撤销。
2. 没有要求用户在确认框中比对**目标对象的稳定标识**（磁盘序列号/唯一 ID），只显示人类可读名称。
3. 没有"操作后重新采集核对"的用户可见闭环设计。

考虑到本项目的文档-实现漂移史（见 4.2），建议把这条边界做成可执行的检查（例如真实执行器只能由声明了目标身份的白名单配置启用），而不是仅靠文档约定。

### 4.5 信息架构与发现性

- 导航顺序（实测）：`硬件 | 管理 | 存储结构编辑 | 磁盘分区编辑 | 测试 | 监控 | 开发 | 设置`。开发者模式下 8 项、非开发者模式下 5 项 —— 与 `Development.md:135` 一致。
- **测试页是一个静态占位页**（`TestPage.xaml.cs` 仅 9 行），只显示"完整测试工作区不属于 WinPool 1.x，计划作为 WinPool 2.0 功能推出"。把一个纯说明页放进主导航，会持续让用户点进去再退出来。更经济的做法是把它并入开发页或关于对话框。
- **开发页**在非开发者模式下不可见，但错误卡会引导用户去那里看详情（见 4.3 的缺陷项）。
- **硬件页被开发者模式门控**（见 4.3）。

### 4.6 文档与流程成熟度

**强项**：文档分层清晰（Product 管产品、Development 管技术、Quality 管验证、Plan 管阶段、CHANGELOG 管结果、Design 管未排期、Reference 管方法、Archive 管历史），单事实单归属；`Archive` 索引完整；`Quality.md` 主动记录 unverified 项并给出原因（"截图全黑，不能作为视觉证据"）。这套纪律在个人项目中属于上乘。

**弱项**：

1. **文档与实现的漂移没有被机制性检测。** 本次抽查 12 条关键声明，1 条已失效（47/47）、2 条与实现矛盾（320–420 DIP、开发页无表头）、1 条不可复现（依赖审计）。漂移本身可以理解，问题是没有任何检查会发现它。
2. **文档锚点过期。** `Program/WinPool/AGENTS.md` 安全段仍写「V0.49 不加入真实写操作」，而产品已是 V0.56（结论未变，锚点过期）。
3. **`CHANGELOG.md:157` 的"依赖审计无已知漏洞包"没有留档证据**，而 `Directory.Build.props:18-21` 又把 NU1900 降级为警告 —— 结论正确但过程不可复现。
4. **版本号被硬编码进测试**（`ArchitectureBoundaryTests.cs:866`），使每次版本提升都会破坏质量门 —— 这正是本次红灯的直接原因。
5. **`docs/Quality.md` 的"已通过"记录缺少 commit SHA**，导致无法判断记录对应哪一版代码。第 3.2 节的整段分析都建立在这个缺口上。

### 4.7 设计文档一致性

- `DesignTables/` 的 4 份文档（`Controls.md`、`Notifications.md`、`SimulationRules.md`、`README.md`）与 2 份 HTML 输出在内容上互相引用一致，抽样 5 组核对与代码一致，未发现自相矛盾的内容错误；主要问题是**状态日期失真**（表头写 2026-09-23，内容已在 09-24 由 `24828d8` 更新）。
- `Design/Control-Size-Consistency.md:58` 记设置页标签列 160 DIP、主题/强调色/语言下拉 220 DIP、路径/7Z 下拉 180 DIP。实测：`ThemeOptions`/`AccentOptions`/`LanguageOptions` 为 228 DIP、`DataLocationOptions`/`SevenZipOptions` 为 188 DIP。**与设计表存在 8 DIP 系统偏差**（可能是样式内边距所致），属文档精度问题而非功能缺陷。
- `Design/WinPool-Distribution-Plan.md` 的"便携版与安装版同功能、不划分功能版本"与 `Product.md:123` 一致。
- **已识别的实质性矛盾（C1–C8）**，其中最严重的三条是 `Design/WinPool-Information-System-Plan.md` **以"V0.55 已实施并收口"的身份描述了已被移除或从未实现的界面**：(a) 开发页的故障日志路径与"清空/复制"按钮（`Development.md:107,109` 明确说这些已被移除）；(b) 通知"悬停或键盘焦点停留时暂停自动关闭"（`:45`，代码中不存在）；(c) 重复消息合并（代码中明确不合并，`Development.md:103`）。这类"设计文档声称已实现"的条目比"声称未实现"危险得多，因为读者会把它当作现状依据。
- 另两条：`Design/Control-Size-Consistency.md` 与 `Design/README.md` 指向"当前 Plan"，而 `Plan.md` 现已改为仅含本机真实编辑阶段；工作区根 `README.md:12-14` 仍写 V0.41。
- **上游可见性与版本**：线上仓库最新 Release 为 V0.51，落后当前 V0.56 五个迭代，而设置页存在"查看更新"入口 —— 用户点进去会看到比本地更旧的版本。

### 4.8 产品级建议

| 优先级 | 建议 | 理由 |
| --- | --- | --- |
| P0 | 修正欢迎页的「开源免费」与「支持 win 原生软 Raid」两处表述 | 首屏承诺与仓库许可现状、产品能力现状直接冲突，是信任问题的源头 |
| P0 | 回答"谁必须用 WinPool"：在 README/Product 中给出 2–3 个具体使用场景 | 当前价值主张偏抽象，用户难以判断是否值得安装一个 592 文件的便携包 |
| P1 | 让错误卡在非开发者模式下有可达的详情入口 | 当前指引无条件指向一个隐藏页面，是确定的死胡同 |
| P1 | 真实写入解锁前补齐三个用户可见前置：目标稳定标识比对、含数据与否之外的确认策略、操作后核对闭环 | `Plan.md` 即将执行，而当前唯一门控是"是否含数据"的启发式判断 |
| P1 | 重新考虑硬件页的门控：只读硬件报告不应与"测试/开发"一起藏在开发者模式后 | 硬件页是纯只读且对普通用户有价值，而默认用户仍在承担完整硬件采集成本 |
| P1 | 把"真实编辑"开关改名或加上显式状态说明，并移除提权后自动置位 | 标题栏最显眼位置的名字应与能力一致；`Product.md:27` 明确不得预选同意 |
| P1 | 给硬件报告加常驻作用域标识 | 同一页混合模拟主机名与真实 OS 版本，用户无法分辨作用域 |
| P1 | 给欢迎窗加"不再显示"，或仅在首次运行显示 | 无托盘 Agent 时每次冷启动都会弹出且无法关闭 |
| P1 | 明确"监控数据给谁用"：要么在 UI 里说明数据为后续版本/外部工具准备，要么把 1.x 的数据保留策略讲得更直白；同时给出归档占用与清理入口 | 存储 1 GiB 却不提供读取路径、也不显示占用，是最大的产品悖论 |
| P1 | 修复通知生命周期：淘汰区分严重级别、实现或删除"悬停暂停"的设计声明 | 当前错误卡会被普通信息卡挤掉，且设计文档描述了不存在的保护 |
| P2 | 测试页占位内容并入开发页或关于对话框 | 纯说明页占用主导航位 |
| P2 | 清理 `Design/WinPool-Information-System-Plan.md` 中"已实施"但实际不存在的条目，或明确标注其已被取代 | 防止后续读者据以判断现状 |
| P2 | 为下一次真实写入解锁预先给出"用户当次授权"的具体界面流程设计 | `Plan.md` 已锁定开发边界，但产品侧授权流程尚未设计 |

---

## 5. 文档与流程审查

### 5.1 文档声明核查汇总

见 4.2 的对照表（12 条抽查，1 条失效 / 2 条矛盾 / 1 条不可复现 / 8 条属实）。

### 5.2 质量门与验证记录

这是本次审查**最严重的流程问题**，值得单独强调：

- `docs/Quality.md` 的最新记录（2026-09-23，随提交 `40d8a9b` 写入）写 `Architecture 47/47 passed、0 failed、0 skipped`。
- 该提交到 HEAD 之间共 **13 个提交**，HEAD 实测 41/47。
- 其中 `5872374`（V0.56 版本提升）的 `CHANGELOG.md` 自己写着未运行自动测试。
- 仓库内**没有任何 CI 配置**（未找到 `.github\workflows`、`azure-pipelines.yml` 等）。
- 因此"47/47"这个数字在文档里持续存在，却与仓库现状不符，而这一点在本次审查之前**没有被任何机制发现**。

建议：(a) 架构测试改为从 `Directory.Build.props` 读取版本号而非硬编码；(b) 在任何"已通过"记录旁标注 commit SHA；(c) 加一个最小的提交前钩子或 CI 跑架构测试（47 项本地仅需 219 ms）；(d) 把"未运行自动测试"的提交在 CHANGELOG 中显式标记为未验证状态。

### 5.3 文件处置规则遵守情况

仓库根 `AGENTS.md` 规定：不直接删除文件、被替代内容移入项目根 `Old`、低价值生成物移入 `Rubbish/YYYYMMDD_reason`、不得清理测试证据。

- **遵守良好**：`docs/Archive` 索引完整，`Old/` 保留了被移除的两个源文件（`WinPoolFactSanitizer.cs`、`ManageCategoryExport.cs`），`docs/Archive` 下大量历史阶段归档齐全。
- **一处冲突**：`Clean-WinPool.ps1:102-107` 会删除整个 `artifacts` 目录，其中包含 `artifacts\test-results` —— 而 `docs/Quality.md` 多处把该目录下的 TRX 与日志当作验证证据引用（如 `Quality.md:17,37,65,95`）。生成物可重建，但**验证证据不是生成物**。这与硬规则 4 冲突。
- **一处残留**：仓库根有 `WinPool (2).lnk`（目标指向 `%LocalAppData%\WinPool`，2026-09-15 创建），与构建脚本生成的 `WinPool.lnk` 并存。`.gitignore` 已忽略 `*.lnk`，因此不入库，但属于可清理的遗留物（按规则应移入 `Rubbish`，不直接删除）。

### 5.4 仓库卫生

- `git status` 干净（唯一 untracked 是先前另一位审查者创建的 `docs/Review/`）。
- 跟踪 552 个文件，其中 `src` 219、`docs` 198、`tests` 81、`assets` 38。生成物、数据库、日志、`OriginArtWork/`、`temp/` 均正确排除。
- `.gitattributes` 设置 `* text=auto eol=lf`，并对 `docs/Archive/V0.2/**` 关闭文本转换 —— 处理得当。
- `skills/native-winui-control/SKILL.md` 是**第三方技能的仓库内副本**（6,879 B），而本机 `.codex` 中的同名技能为 9,398 B 并已版本推进到 V0.56 语境；仓库副本仍写「Verified against WinPool V0.39」、标题栏 6 个标签（`0 管理 … 5 设置`，实际为 8 个）、并提到「Welcome dialog buttons」。**使用该副本的代理会被误导**。建议删除或更新，并说明其来源与许可（该技能自带 MIT LICENSE，仓库内副本未附）。

---

## 6. 评估结论与修复优先级

### 6.1 结论

WinPool 在**工程意图**上明显高于其**工程可交付性**。它做对了很多困难的事：真实写入的默认拒绝是真的、归档状态机是真的、文档分层是真的、"未验证"的诚实标注是真的。它的主要问题不是能力不足，而是**验证与记录机制失效后无人察觉**：质量门已经红了三天以上，而文档仍在报告全绿；文档承诺的若干界面行为在代码里从未实现；无障碍支持停留在设计表里；App 在三天内崩溃至少 6 次，而项目自己的崩溃日志只捕获了其中 1 次 —— 于是"日志干净"被当成了"运行稳定"。

在修复 1.3 表中的 10 项 P1 之前，V0.56 不应被视为可交付或可发布。修完之后，这个项目的基础是好的 —— 缺的是"把文档说的和代码做的对齐，并且用自动化持续守住"这一层。

值得单独指出的是：本次审查的两处最重要更正（崩溃可观测性、Agent 生命周期）都不是由项目自身的记录发现的，而是需要动用 Windows 事件日志、WER 报告和直接读代码才暴露出来。这本身就是对"自有诊断足够"这一假设的反驳。

### 6.2 修复优先级（建议）

**第 1 批 —— 恢复可信度与可观测性（2–3 天）**

1. 修复 6 条过期架构断言，并把版本号断言改为从 `Directory.Build.props` 读取；确认 `dotnet test` 恢复 exit 0。
2. 在 `Quality.md` 中把"47/47"更正为真实状态并标注 commit SHA；把 `5872374` 标记为未验证提交。
3. **补齐崩溃可观测性**：加 `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` 处理器，启用 WER `LocalDumps` 取一次完整调用栈，按 `combase.dll` 偏移 `0x261d2` 定位（见 3.6）。
4. 把 `Merge-RuntimeTrees.ps1` 的替换流程改为"先构建临时树 → 全部成功 → 原子替换"，并在目标被占用时给出明确诊断而不是部分删除；让 `Clean-WinPool.ps1` 不触碰 `artifacts\test-results`；明确便携模式数据根不与构建输出共用目录。

**第 2 批 —— 安全与数据完整性（3–5 天）**

5. 给 `ExportAgentMonitorCsvRequest` 加根路径约束 + 拒绝 ADS/非常规扩展名 + 显式确认；给 7-Zip 自定义路径加哈希/签名校验。
6. 给控制管道的请求循环加读超时（复用握手期的 5 秒期限模式），并把 `InvalidDataException` 纳入可恢复分支；让 `MarkFailed` 有回到 Running 的路径或让 failed 只影响该连接。
7. 数据根迁移前对 `winpool.db` 执行 `TRUNCATE` checkpoint，并在检测到 sidecar（`-wal`/`-shm`/`-journal`）时拒绝迁移；把 `SqliteMigrationAuditor` 的比较从"行数+主键哈希"扩展到内容哈希。
8. 为所有 `checked` 容量乘法加 `OverflowException` 捕获（或在导入校验中加数值上界），并让 `App_UnhandledException` 仅在确实可恢复时设置 `e.Handled = true`。

**第 3 批 —— 无障碍与交互一致性（3–5 天）**

9. 为 8 个导航项设置基于本地化标签的 `AutomationProperties.Name`；补齐设置页 9 个控件的可访问名称；区分两个「真实编辑」的名称；给结构页动态表单加 `LabeledBy`。
10. 修复 `FixedWrapPanel` 忽略 `Grid.Row/Column` 的问题（要么实现附加属性，要么改回 `Grid`）—— 这直接决定操作区是否按设计渲染。
11. 给 `Process.Start(explorer.exe)` 加 try/catch；给监控会话文件创建失败加可见的失败状态（不能显示"正在监控"却什么都没录）。
12. 修正欢迎页的「开源免费」与「支持 win 原生软 Raid」表述；给非开发者模式下的错误卡提供可达的详情入口。

**第 4 批 —— 一致性清理（1 周）**

13. 消除文档-实现漂移：右栏宽度（固定 320 还是 320–420，二选一并同步文档与代码）、开发页区域标题、`MsrSwitch` 文案、`DesignTables` 中的 8 DIP 偏差。
14. 收敛重复实现（格式化资格 3 处、裸 GiB 算术 8 处、App 层 10 处规则泄漏），把规则计算迁回 Application 层。
14. 更新或移除仓库内的 `skills/native-winui-control/SKILL.md` 副本。

---

## 附录 A：审查执行记录

### A.1 执行过的命令（关键项）

```powershell
# 环境
dotnet --version                          # 10.0.401
dotnet --list-sdks                        # 10.0.400 / 10.0.401

# 构建（三次，见 3.1）
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1          # 失败（App 运行中）
dotnet build WinPool.slnx -c Release --no-restore -m:1          # 失败（残留编译器锁）
pwsh -File .\build\Rebuild-WinPool.ps1 -Configuration Release -SkipDesktopShortcut   # 成功 0 警告 0 错误

# 测试
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1                # exit 1
dotnet test tests\WinPool.Architecture.Tests\WinPool.Architecture.Tests.csproj `
    -c Release --no-restore -m:1 --logger "trx;LogFileName=deepseek-arch-review.trx" # 41/47

# 依赖审计
dotnet list WinPool.slnx package --vulnerable --include-transitive                   # 22 项目无漏洞

# 归档校验
artifacts\Release\Tools\7zip\7za.exe t <1GiB归档包>.7z                                # Everything is Ok

# 原生测试：UIAutomation + winapp ui（inspect/screenshot/list-windows）+ 真实鼠标注入
# 脚本见证据目录；覆盖启动、8 页导航、语言、主题、开发者模式门、480 DIP 窄窗
```

### A.2 未核实项（本次未验证，附验证方式）

| 项 | 验证方式 |
| --- | --- |
| 数据根迁移时 WAL 非空导致数据回退（C2-7/C5-1） | 在有活动写入时执行迁移，比对迁移前后 `winpool.db` 的 `PRAGMA integrity_check` 与非主键负载 |
| 请求循环半帧占用是否真的阻塞第二个客户端（B2-2/C6-3） | 写一个同用户客户端：完成握手 → 发送长度前缀 + 半载荷 → 挂起 → 观察 App 是否连接超时 |
| 畸形握手帧是否真的使 Agent 永久 Failed（B2-1） | 发送 `MessageType="x"` 一帧，观察托盘状态与后续请求是否被拒 |
| CSV 导出覆盖是否真的替换目标 ACL（B3-1） | 对目标文件设置显式 ACL，导出后读回 ACL |
| 自定义 7-Zip 路径是否真的被执行（B4-2） | 放置一个写标记文件的假 `7za.exe`，触发轮换 |
| 管道完整性标签实际值、低完整性同用户进程能否连接 | `GetNamedPipeSecurityInfo` / 低完整性进程实测连接 |
| `Program.Main` 退出是否卡死（C6-5） | 计时从关窗到进程消失，重复多次；构造 WinForms 同步上下文未卸载场景 |
| 管理页最小高度 300 vs 需求 504（D8-3） | 在 480×300 窗口下逐页检查是否出现无法触达的控件 |
| 扩缩容对话框自算范围与规则引擎分歧（D2-4a） | 在模拟系统中构造 `SizeRemaining` 使两者分歧的样本，观察是否出现"允许后拒绝" |
| DPI / 多显示器 / 高对比度矩阵 | 在 125%/150% 缩放、双显示器、高对比度主题下重复 3.3 的测试 |
| 长时间运行（数小时）的采样连续性 | 让监控连续运行并跨越一次 1 GiB 轮换，按样本身份核对多重集守恒 |

### A.3 证据文件清单

`docs\Review\DeepSeek-Independent-Review-20260924-evidence\`：

| 文件 | 内容 |
| --- | --- |
| `shot-00-initial.png` | 首次启动：主窗口 + 欢迎窗口全景 |
| `shot-page-*.png`（7 张） | 管理/存储结构/磁盘分区/测试/监控/开发/设置 各页截图 |
| `p3-hardware.png` | 硬件页（模拟系统选中态） |
| `p4-language-open.png` | 语言下拉展开 |
| `p4-system-selector-open.png` | 存储系统选择器展开（1 本机 + 5 模拟） |
| `p4-english-settings.png` | 切换到 English 后的设置页 |
| `p6-devmode-off.png` / `p6-devmode-on.png` | 开发者模式关闭/开启的导航对比 |
| `native-test-log.txt` | 启动计时、窗口清单、初步 UIA 树 |
| `navcheck-log.txt` | 8 页导航正确性逐页判定（含等待时间与页面特征文本） |
| `phase3-log.txt` | 硬件页详情、设置页全部可交互控件、480 DIP 窄窗测量 |
| `phase4-log.txt` | 无障碍审计（30 控件、9 无名称）、语言切换、本机硬件页 |
| `phase5-log.txt` | 开发者模式门切换 |
| `tree-main.csv` | 主导航状态的完整 UIA 树（222 节点） |
| `p3-hardware.csv` / `p4-settings.csv` | 硬件页 / 设置页 UIA 节点导出 |
| `test-run-full.log` | 架构测试失败详情 |
| `deepseek-arch-review.trx` | 架构测试 TRX（含 6 项失败的断言行号） |
| 审查工作目录 | `D:\Coding\Research03_WinPool\Rubbish\20260924_deepseek-review-work\20260924_deepseek-review\evidence\` | 五份子代理静态报告全文（A/B/C/D/E，合计 2,911 行）及原生测试脚本；本报告正文引用但未复制的页面级 CSV 与中间日志。按仓库根 `AGENTS.md` 规则 1 于审查结束后移入 `Rubbish`，未删除 |

### A.4 子代理静态报告索引

| 文件 | 行数 | 覆盖 |
| --- | --- | --- |
| `A-architecture-static.md` | 309 | 依赖图、文档规则一致性、架构测试质量、测试套件质量、代码质量、死代码 |
| `B-security-static.md` | 842 | 信任边界图、命名管道、提权、命令注入、真实写入门禁、输入校验、数据保护、供应链 |
| `C-persistence-concurrency-static.md` | 368 | 事务边界、单写者、21 步轮换状态机、数据根迁移、并发、资源释放、进程生命周期 |
| `D-appui-static.md` | 755 | 页面/VM 清单、规则泄漏 10 条、绑定与线程、XAML 与布局规则偏差、无障碍、本地化、通知与对话框、窗口生命周期 |
| `E-product-design.md` | 637 | 定位与竞品对照、宣称-实现对照、交互逐项、信息架构、信任与预期管理、文档成熟度、设计文档矛盾 C1–C8 |

### A.5 本次实际使用的工具与技能（含未使用项）

为避免后续审查者误以为测试覆盖面比实际更广，此处如实记录工具链：

**使用的技能（以读取 `SKILL.md` 并遵循其方法的方式，而非通过会话技能目录调用）**

本次会话的技能目录**未暴露**任何 WinUI/WinApp 技能，因此无法通过技能入口调用；主审查官改为直接从磁盘读取技能正文并按其方法执行：

| 技能 | 使用方式 | 效果 |
| --- | --- | --- |
| `native-winui-control` | 读取 `C:\Users\Admin\.codex\skills\native-winui-control\SKILL.md` 全文（256 行），按其工作流手写 UIA 脚本 | **直接有效**。其"`ListItem` 的 UIA Name 可能是视图模型类型名"的警告恰好命中本次最重要的无障碍发现；`SetProcessDPIAware`、`SetForegroundWindow` 后再注入鼠标、不复用旧 `BoundingRectangle` 等规则全部被遵循 |
| `winapp-ui-automation` | 仅读取前 60 行（全文 453 行） | 部分有效，但**暴露了两个执行缺口**（见下） |

**未使用的相关技能**：`winapp-find-ui`、`winapp-find-api`、`winapp-package`、`winapp-identity`、`winapp-manifest`、`winapp-signing`、`winapp-sandbox`、`winapp-troubleshoot`、`winapp-frameworks`、`winapp-setup`（均存在于 `C:\Users\Admin\.codex\skills\`，本次未使用）。

**实际调用的 `winapp` 子命令**：仅 `ui screenshot`（6 次）、`ui inspect`、`ui list-windows`、`--help` / `--version`（CLI 版本 0.6.1，与 `docs/Quality.md:85` 记录一致）。

**执行缺口（应在后续审查中补齐）**

1. 未设置 `WINAPP_UI_WORKFLOW_ID`，也从未调用 `winapp ui yield` —— 按 `winapp-ui-automation` 的技能约定，这会使每次调用各自成为一个互相争用的工作流。本次因操作是串行且每次独占桌面，结果未受影响，但违反了该技能的协作模型。
2. `--capture-screen` 的技能约定要求**恰好一个窗口**，多窗口匹配时应先 `list-windows` 再以 `-w <hwnd>` 重试；本次始终用 `-a WinPool --capture-screen`，在欢迎窗与主窗同时存在时它**并未失败**，而是输出了一张包含两个窗口的 PNG（2168×928）。这说明**该技能描述的协作/前置校验行为与已安装的 0.6.1 CLI 不一致**；同时 `winapp ui yield` 在 0.6.1 中**不存在**（`ui --help` 的命令清单里没有该项），故 `docs/Quality.md:85` 的判断是对的、技能文档描述的是更新版本。本次因此未纠正用法。
3. 大量分析不得不手写 UIAutomationClient 代码（约 380 行，分布在 6 个脚本），原因是 `winapp ui inspect -i` 只返回可交互元素、**丢掉全部 Text 节点**，而本次测试的核心断言正是页面文本。手写 UIA 还额外暴露了两个两份技能都未提示的坑：WinUI 虚拟化项与未布局元素的 `BoundingRectangle` 可能为 `Infinity`/`NaN`，直接转 `int` 会抛 `InvalidArgument`（本次前三次脚本因此失败）。

**未覆盖的测试类型（本次未做，不得据本报告推断为通过）**

- **性能与资源**：未测冷启动耗时的可靠数字、工作集/内存占用、句柄与线程增长、长时间（小时级）连续运行的内存泄漏与采样连续性。
- **平台矩阵**：未做 125%/150% DPI、双显示器跨屏、高对比度主题。
- **打包与安装**：WinPool 当前为无打包便携版，未使用 `winapp package`/`identity`/`signing` 相关能力；未验证 MSIX 路线（产品也尚未提供）。
- **动效**：未使用 `winapp ui record` 录制视频，因此通知卡片的进入/退出动画仍然 `未核实`（这也与 `docs/Quality.md` 自述的"录屏提取帧全黑"一致）。
- **持久化故障注入**：未注入崩溃/断电/磁盘满，因此 2.3 节中的崩溃语义结论全部来自静态推理。

因此本报告的"真实软件测试"应理解为**功能性与 UI 层面的真实运行验证**，而非性能、平台、打包或故障注入级别的测试。

### A.6 声明

本次审查**未执行任何真实存储结构写入**，未对任何物理磁盘、分区、池或卷做修改。所有原生测试只做只读导航、只读信息采集、界面开关与偏好切换；测试过程中改动的偏好（语言、开发者模式门）已在结束时恢复原值（`Language = SystemDefault`、`DeveloperMode = true`）。测试期间未删除用户数据；`artifacts\Release` 因项目自身构建脚本的行为被重建（见 3.1），重建后 592 个文件完整。

审查者对仓库**既有的任何文件均未修改**；新增内容仅为 `docs\Review\` 下的本报告与证据目录。审查工作目录按仓库根 `AGENTS.md` 规则 1 移入 `Rubbish\20260924_deepseek-review-work\` 保留，未删除。

---

### A.7 审查过程中的两处自我更正（保留记录）

本报告在初稿完成后被独立验证者推翻了两条结论。两者都记录在此，因为它们直接影响报告的结论强度，而且第二处暴露了本项目一个**从未被发现的系统性缺陷**。

**更正一：`app-crash.jsonl` 不能作为"未崩溃"的证据。**

初稿在 3.3 节写「崩溃日志全程零新增」，并据此把"运行稳定"列为强项。经独立验证者指出后核实：该结论**无效**。Windows 事件日志与 WER 报告显示，`WinPool.App.exe` 在 2026-09-22 至 09-24 之间**至少崩溃 6 次**，其中 5 次签名完全一致（故障模块 `combase.dll`、异常代码 `0x80004003`/`E_POINTER`、偏移 `0x261d2`）。而 App 自己的 `app-crash.jsonl` 在同期**只记录了 1 条**（2026-09-23 20:17，且是另一类 `XamlParseException`）。

原因：`App_UnhandledException`（`App.xaml.cs:96-109`）只在 XAML 层未处理异常时被调用，而 COM 级别的访问冲突（`0x80004003`）不会走到它；同时该处理器**不设置 `e.Handled`**，因此即使被调用也无法阻止进程终止。结论：**项目自有的崩溃日志对 COM 层崩溃是盲的**，6 次真实崩溃中 5 次没有任何自有日志。

这条更正是新发现（见 P1-10），也是本次审查中唯一由"审查者被审查"产出的缺陷。

**更正二：3.3 节初稿的「Agent 随后自动退出」不成立 —— 而且项目自己早就写过这条更正。**

代码核实：`MainWindow_Closed`（`MainWindow.xaml.cs:503-572`）只落盘 UI 状态、`DetachAsync`（其实现是 `_loopCts?.Cancel(); return Task.CompletedTask;`，`MonitoringService.cs:466-470`，**不含 IPC**），**没有任何通知 Agent 的调用**。Agent 的关闭只发生在显式的 `RequestAgentShutdownRequest`（托盘退出 `TrayApplicationContext.cs:279-288`、提权重启 `MainWindow.xaml.cs:853-858`、数据位置切换 `SettingsPage.xaml.cs:186-187`，门控在 `AgentSessionCoordinator.cs:217-268`）。而 `AppExitSignal` 的方向是 **Agent → App**（`Local\WinPool.App.Exit.{sidHash[..24]}`，`DesktopAgentRuntime.cs:566-580` 设置，`App.xaml.cs:348-371` 监听并关窗），即 **Agent 才是这对进程的主导方**。

**最值得记录的一点**：项目自己在 `docs/Reference/开发踩坑记录.md:51` 就写过这条更正 —— 「…只看 `DetachAsync` 名称便声称会通知后台停止监控；实际方法只取消 UI 本地循环、不发送 IPC，监控继续由托盘管理。」我在写初稿时**没有读这份踩坑记录**，于是重复了它已经警告过的错误。这说明该文件有效，但也说明它没有被放到审查者会读到的路径上。

另外，Agent **不是 App 的子进程**：`Process.Start{UseShellExecute=true}` 后立即 `Dispose()`（`NamedPipeAgentConnection.cs:87-99`），`src/` 下没有任何 `CreateJobObject`/`AssignProcessToJobObject`。因此 App 崩溃或被杀都不会带走 Agent —— 与"Agent 是独立托盘进程"一致。
