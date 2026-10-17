# WinPool 接手审查与完整检查（2026-10-17）

状态：**审查完成；当前自动测试门未通过；本文提出的产品改造尚未实施。**

## 1. 结论

**主要瓶颈是重复工作被放进每次操作的必经路径。** 一次真实池名或卷名修改要做 7 次 inventory，联机／脱机要做 8 次；本轮六次 GUI 实机操作约 38–43 秒，44 次 provider 采集累计 215.346 秒。已有创建与自动布局证据为 247.687 秒，同批八段操作的 80.35% 时间用于采集。数百秒现象有实际证据，不能只归因于 MAX 或单个异常命令。

模拟路径另有确定的结构问题：一次输入触发两遍完整预演、同一规则重复求值、完整模拟文档发给 Agent 后原样回传、相同进度反复重建拓扑、监控与普通操作共用串行控制请求。**这些是代码事实；本轮小型模拟场景没有复现数百秒停顿，不能把真实采集的耗时直接归给模拟。**

App 与 Agent 两个进程具有明确用途：交互呈现与持续后台工作／真实写入权威。问题是业务编排、数据所有权和刷新职责跨边界交错，而非“有两个进程”本身。建议保留这两个运行主体，使用同一套纯规则和投影；先减少重复采集、预演、传输与重建，再把真实结构编排收回 Agent。无需引入新的框架、消息总线或更多分层。

| 检查 | 结果 |
| --- | --- |
| Release 全解决方案构建 | 通过，0 warning／0 error |
| 已配置 .NET analyzers | 通过，0 待修改文件 |
| 12 个测试项目 | 最终有效结果 **2455 总数／2420 通过／32 失败／3 跳过** |
| 真实主机只读准入采集 | 显式启用后通过，来源与拓扑完整 |
| 原生 GUI | 完成管理／编辑／监控／设置／生命周期检查、4 次模拟提交、6 次真实提交；发现问题见 §5 |
| 真实存储恢复 | 前后所选稳定结构字段一致；两块 Samsung 未被写入 |
| NuGet 已知漏洞扫描 | 本次 23 项目扫描无报告命中，不覆盖非 NuGet 二进制 |
| 交付范围 | 新增本报告；未改生产代码、测试或依赖，未声称已完成性能优化 |

## 2. 基线、方法与覆盖

源码基线为 `main / 74a40af73e213eaf63d7267f6065422a05fec3bd`，版本 V0.60／0.6.0.0。[在线仓库](https://github.com/GitRuozhi/WinPool)的 origin/main 经只读查询与本地一致。环境为 Windows 10 专业工作站版 10.0.19045 x64、管理员进程、.NET SDK 10.0.401／运行时 10.0.12。

静态阅读覆盖 11 个生产项目的主要依赖和调用链、12 个测试项目、当前 README／Product／Development／Quality／Plan／CHANGELOG，并按需追溯历史性能证据。基线已跟踪 C#/XAML 为 291 文件／94,551 行；测试 C# 为 136 文件／50,359 行；现存已跟踪 Markdown 为 180 文件／30,188 行，后者包含归档。这些数量说明维护面，不单凭行数认定冗余，也不代表逐行人工证明了所有代码正确。

构建只执行一次；随后测试使用已构建产物，GUI 使用标准 `artifacts/Release`。静态审查与测试分工进行，桌面只由一个执行方操作。六次真实操作均经现有 WinPool GUI→Agent，未使用自由形式存储写命令。每次按准确硬件／provider 身份和关联核实 WDC 独占对象；盘号、盘符只作为显示值。

本轮开始与结束均为 WDC→单成员池 Q2→VD Q→OS Disk 3，在线 GPT，E: 卷标 Q、ReFS／4096-byte cluster；VD 约 4 TB，实际 HDD tier interleave 为 16 KiB。这是当前开发机状态，不是存储参数推荐。按本轮实际基线恢复，没有为此次审查重建旧文档中的 NTFS 末态。

用户已有的四份 `docs/Review` 删除与未跟踪 `Ref/` 原样保留；未整理其他子项目。原始日志、数据库查询结果、截图和 TRX 存于本地被忽略的 `artifacts/audit-20261017/`，不纳入提交。本文的代码链接对应上述基线；本地 evidence 链接在仅克隆 Git 仓库的机器上不一定存在。

## 3. 构建、静态分析和自动测试

### 3.1 执行结果

| 命令／检查 | 耗时 | 结果与口径 |
| --- | ---: | --- |
| `dotnet restore WinPool.slnx` | 2.641 s | exit 0 |
| `dotnet build WinPool.slnx -c Release --no-restore -m:1 -p:RunAnalyzersDuringBuild=true -p:RunAnalyzers=true` | 63.716 s | 0 warning／0 error；App、Agent 运行树合并 0 collision |
| `dotnet format analyzers WinPool.slnx --no-restore --verify-no-changes --severity warn --verbosity diagnostic` | 46.857 s | 23 项目、557 文件、0 修改；未额外开启全部 info／实验规则 |
| 12 项目串行 `dotnet test … -c Release --no-build --no-restore` | 289.930 s | 首轮 2418 通过／34 失败／3 跳过 |
| App／Agent 退出后仅重跑 2 项 guard | 7.079 s | 2／2 通过，原失败证据保留 |
| 显式启用 1 项本机只读 admission | 7.313 s | 实际采集通过，替代该用例首轮未启用时直接 return 的 Passed |

最终结果采用每个用例最后有效证据，**不是第二次全量测试**。共执行 2458 个条目，去重覆盖仍为 2455；不能重复累计为新增用例。471 个源码和配置文件从构建前至补验后 SHA-256 一致；4 个 App／Agent dll/exe 从本次构建完成至补验后哈希一致。

| 测试项目（省略 WinPool. 前缀） | 总数 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: | ---: |
| Agent.Client.Tests | 22 | 22 | 0 | 0 |
| Agent.Tests | 139 | 139 | 0 | 0 |
| App.Tests | 445 | 445 | 0 | 0 |
| Application.Tests | 433 | 433 | 0 | 0 |
| Architecture.Tests | 48 | 46 | 2 | 0 |
| Domain.Tests | 23 | 23 | 0 | 0 |
| Execution.Tests | 116 | 116 | 0 | 0 |
| Infrastructure.Tests | 971 | 941 | 30 | 0 |
| Inventory.Tests | 2 | 2 | 0 | 0 |
| Ipc.Tests | 11 | 11 | 0 | 0 |
| Monitoring.Tests | 53 | 53 | 0 | 0 |
| Persistence.Tests | 192 | 189 | 0 | 3 |
| **合计** | **2455** | **2420** | **32** | **3** |

测试性质必须区分：App.Tests 主要源链接 UI 无关 helper，不启动 WinUI；Architecture 多为源码断言；Agent／Client、Persistence 等包含实际管道、进程、SQLite 和临时目录集成测试；Infrastructure 使用合成事实与写 adapter 替身。它们不能替代下节的真实设备 GUI 验证。

### 3.2 失败、跳过和可执行修复

| 问题 | 证据与影响 | 最小修改方案 |
| --- | --- | --- |
| 2 项过时的 UI 源码断言 | `EditorPagesOwnTheirSurfacesWithoutSectionTitles` 仍要求 StartOffsetValue／EndOffsetValue，现用 OffsetMibRangeValue／OffsetAdaptiveRangeValue；`MainWindowPreviewsSavedWorkspaceBeforeAgentRestore` 禁止任意 Border，现有 ProgressSurface 使用 Border。[测试](../tests/WinPool.Architecture.Tests/ArchitectureBoundaryTests.cs#L572) | 更新到当前确认的 UI 行为；起止值、启动恢复与进度表面分别验证，去掉与行为无关的控件标签限制。无需恢复旧 UI 取悦测试 |
| 30 项恢复 fixture 在 setup 被拒绝 | `PoolCreationRecoveryKeepsBarrierForIncompleteOrMismatchedEvidence` 24 变体、`PoolCreationRecoveryRequiresAndPersistsFreshAggregateForNullPhysicalRoles` 5 变体、`SingleReturnedPoolCreationRecoversExactEmptyPoolWithoutAnotherCall` 1 项。fixture 未注入 capability reader，回落本机 WMI，synthetic subsystem 无法通过 SupportsStoragePoolCreation 门；目标恢复断言尚未执行 | 在 [WindowsRealStorageBackendTests](../tests/WinPool.Infrastructure.Tests/WindowsRealStorageBackendTests.cs) 注入与合成成员一致的能力 reader；fixture 禁止落入 host WMI。复验这 30 项及该恢复用例相关门，不修改产品准入规则 |
| 首轮 2 项 guard 环境冲突，补验通过 | `MissingCorruptAndAmbiguousRootsRefuseButPristineRootPasses`、`StoppedUnknownResultAllowsRepairWithoutChangingDatabaseOrWriteBarrier`；[guard](../build/Assert-RealOperationIdle.ps1#L196) 按全局进程名检查，与同时运行的 GUI 冲突 | 测试调度声明需要无 WinPool 进程；若改 guard，应按实际运行树／数据根识别实例，不能简单去掉进程保护。此次停 GUI 后 2／2 通过 |
| 3 项明确跳过 | 1 GiB archive 参数、1 GiB rotation 生命周期、7z 参数比较，均为显式手动性能测量 | 保持未验证标记；将其放到归档性能专项，不让日常布局修改触发 GiB 级工作 |
| opt-in 用例未启用也显示 Passed | `RealAdmissionReadOnlyTests` 普通 Fact 直接 return | 改为明确的 opt-in 分类／动态 skip；此次另显式启用完成真实只读验证，避免将空执行称为硬件覆盖 |

只读 admission 实际取得完整拓扑：8 个 MSFT 来源均 Returned，3 物理盘、2 池（含 Primordial）、1 VD、2 tier、3 OS disk、8 partition、6 volume。3 个物理 closure 可进入后续能力检查，不代表所有写操作被批准。该流程含既有 `Update-StorageProviderCache -DiscoveryLevel Level3` 服务发现缓存刷新，没有结构写入、Prepare／Accept／执行存储计划。

另发现测试和生产诊断共享 `%LocalAppData%/WinPool` 日志，fake 的 sub-ms capture 会混入生产 timing 并参与轮换。此次按 GUI App／Agent PID 和 OperationId 过滤，未把 fake 结果计作实机性能。修复应注入测试专用 diagnostics sink／目录，不再写产品全局日志。

NuGet 扫描覆盖 23 项目、43 个去重包版本（17 个直接包版本）：本次 advisory 无命中、无下载失败；35 个去重版本存在更新，其中 13 个直接依赖。没有自动升级。过期不等于漏洞，这也不证明内置 7-Zip 或操作系统无漏洞。性能整改优先级不应被无关的批量依赖升级挤占。

证据：[完整自动化结果](../artifacts/audit-20261017/automated-findings.md)、[最终去重统计](../artifacts/audit-20261017/automated/final-effective-summary.json)、[首轮失败与堆栈](../artifacts/audit-20261017/automated/failures.json)、[TRX](../artifacts/audit-20261017/automated/trx/)、[build.binlog](../artifacts/audit-20261017/automated/build.binlog)。

## 4. 性能实测

### 4.1 本轮真实 GUI 操作

计时为产品 `real.prepare` 起点到 App `real.ui_scoped_refresh` 结束。改名自动提交；联机／脱机剔除 Prepare 完成至 Accept preflight 开始的整段确认／交接等待，含少量 IPC 时间，因此“剔除后”不是纯 CPU 或纯 provider 时间。只累加最内层 `inventory.provider.collect`，不重复计入包围它的 capture／execute 阶段。

| 操作 | 原始墙钟 s | 剔除确认／交接 s | 剔除后 s | 采集次数 | 采集累计 s |
| --- | ---: | ---: | ---: | ---: | ---: |
| 池 Q2 → Audit_Q2 | 37.800 | 0 | 37.800 | 7 | 33.387 |
| 池恢复 Q2 | 42.130 | 0 | 42.130 | 7 | 37.766 |
| 卷 Q → Audit_Q | 43.192 | 0 | 43.192 | 7 | 38.956 |
| 卷恢复 Q | 39.283 | 0 | 39.283 | 7 | 35.293 |
| VD Q 对应磁盘脱机 | 49.827 | 10.027 | 39.800 | 8 | 35.047 |
| 恢复联机 | 47.755 | 5.002 | 42.753 | 8 | 34.896 |

池名、卷名、在线状态三组各两例的中位数分别为 39.965、41.237、41.276 秒。仅用于描述该小样本，不提供 p95 或稳定加速倍数。两次池改名与自动测试有负载重叠；后续联机／脱机在全量测试结束后仍约 40 秒。六条计划均 Completed，无本轮新 Unknown。

一次手动 Hardware 刷新实际 Agent queue→persist 为 **9.421 秒**，其中 provider 9.103 秒。GUI 的按钮未禁用，最初 IsEnabled 等待得到的 553 ms 已明确作废；不是刷新完成时间。初始 Agent 监控身份采集 4.886 秒、Storage 5.611 秒、Hardware 9.317 秒，三份重叠读取合计 19.814 秒。

**真实写 cmdlet 自身尚无准确分段计时。** 当前 exact-member 分支没有单独包裹写调用计时；`real.step.execute` 包含前后采集，不能称为写 cmdlet 耗时。软件每次采集启动新的 Windows PowerShell 5.1 `powershell.exe`；用户交互式 `pwsh` 热进程执行一个命令与完整 WinPool 操作并非同一口径。本轮没有自由存储写命令对照，不编造“慢多少倍”。

### 4.2 已有数百秒证据与本轮模拟结果

2026-10-16 同仓库已有设备证据：删除 VD、创建分层 VD、GPT 初始化、删除 provider MSR、两次创建分区、格式化、设置盘符，共 **278.204 秒／60 次采集／223.531 秒 provider 采集，占 80.35%**。其中创建至自动布局完成 247.687 秒。八次 UI persistence 合计 0.302 秒，merge 0.021 秒，projection 0.001 秒。由此应先改采集链，暂不优先更换 SQLite。该记录是历史现场证据，本轮没有重新格式化真实磁盘。

本轮模拟使用内置“建池与初始化”文档，拖入 RAW-HDD-1，创建 64 GiB 池／VD／分区，随后改卷名、缩到 32768 MiB，最后删除本轮模拟结构恢复基线。四次提交均 Completed。

| 模拟 GUI 动作 | 工具包围区间 |
| --- | ---: |
| 确认创建池、VD 和自动分区至 ready | 4.592 s |
| 修改卷名 | 0.682 s |
| 缩到 32768 MiB | 0.608 s |
| 移除本轮模拟结构 | 0.616 s |

这些时间含 CLI 调用／UI 等待开销，不是纯 Apply 方法 benchmark；创建耗时也不能全部归到模型算法。当前 13 份模拟文档总 JSON 约 2.67 MB，但本轮编辑对象是较小内置文档；未完成大对象高频键入 CPU／分配 profile。因此保留“模拟大场景慢”的待定位范围，同时给出下节已能直接消除的重复路径。

证据：[六次操作逐阶段数据](../artifacts/audit-20261017/gui-operation-timing-summary.json)、[分组统计](../artifacts/audit-20261017/gui-operation-timing-groups.json)、[Hardware 计时](../artifacts/audit-20261017/gui-hardware-refresh-timing.json)、[历史批次](../artifacts/test-results/20261016-label100-operation-timing/summary.json)、[模拟 GUI 记录](../artifacts/audit-20261017/gui/)。

## 5. 真实桌面 GUI 结果与限制

使用 WinApp CLI 的真实 Windows UI Automation，配合屏幕截图及独立只读 OS／SQLite 后态核实；没有以单元测试冒充 GUI。工具为 0.6.1，部分技能文档中的命令在此版本不可用；截图默认 WGC 黑屏后改用 capture-screen。此类工具限制不计为 WinPool 缺陷。

| 场景 | 结果／问题 |
| --- | --- |
| 初次启动和工作区恢复 | 实际出现 Windows“未知发布者”提示。App→Agent 约 65 秒空档含人工处理，排除为产品启动性能。启动未完整恢复时曾只显示本机、无模拟目录；保留界面证据，重开 App 后恢复。没有足够证据归因于某个异常处理分支 |
| 管理、结构、分区、Hardware 页面 | 导航、实际拓扑、对象选择、编辑表面可用；Hardware 刷新后有结果，刷新期间按钮状态不能用于判断完成 |
| 模拟闭环 | 新池／VD／自动分区 D:／NTFS 64 KiB、卷名、缩分区、清理均完成；初始与最终 13 份文档的对象／关系内容一致，修订、时间及数组顺序变化另计 |
| 真实属性与状态 | 池名往返、卷名往返、VD OS disk 脱机／联机六条完成；独立 OS 查询与计划后态一致 |
| 设置 | English＋Dark 真实切换可见，恢复 SystemDefault／System；App 设置全部恢复，Agent 设置仅 SavedAtUtc 时间戳变化 |
| 监控 | 开关、曲线和样本落盘可验证；两次 session 最终 Stopped、dropped_samples=0。第一段三设备约 10.002 Hz；第二段 Samsung 约 9.993 Hz，VD 因本轮脱机存在约 16.289 秒最大采样间隔，不能声称“全程无缺口” |
| 监控诊断 | WDC provider 身份可靠，但池成员没有独立 same-device→OS disk 关联；VD Q 有 Disk 3 关联。当前 wildcard 对 WDC 显示“身份无法唯一核实／重新选择”，混淆角色不支持与身份丢失，应修正文案和 resolver 结果分类 |
| 关闭和重开 App | 标题栏关闭使 App 退出，Agent 保留；重开连接原 Agent，模拟目录恢复 |
| 托盘退出 | 经真实任务栏托盘菜单退出，约 0.523 秒后 App／Agent 均消失；数据库 shutdown_clean=1。退出码未成功取得，不能报告 exit 0 |
| 开发／测试页 | 页面可进入；测试页为当前占位内容，不能因此宣称产品内测试功能已经实现 |

末态独立比较的 physical／pool／VD／OS disk／partition／volume 稳定字段一致；比较明确排除时间、采集耗时、自由空间 SizeRemaining 和数组顺序。这不等于逐扇区校验。两块 Samsung 所选结构字段一致，未作为写入目标。真实模式结束时关闭，原模拟系统重新选中，当前 App／Agent 已正常退出。

本轮没有重新验证真实建池／删池／格式化／MAX 搜索／真实扩缩分区；这些只引用已存历史记录，不冒充本轮 GUI 通过。多物理成员、SSD／SCM 分层、Mirror／Parity 的实机组合、Windows 11、高 DPI／高对比度、数据位置迁移、导入导出对话框、1 GiB 归档和长时间耐久也未覆盖。完整检查指本次工程检查与明确的本机 E2E 范围已执行完毕，不等于所有产品矩阵均通过。

证据：[前后结构对比](../artifacts/audit-20261017/native-restoration-check.json)、[模拟恢复](../artifacts/audit-20261017/simulation-restoration-check.json)、[偏好对比](../artifacts/audit-20261017/settings-comparison.json)、[监控会话](../artifacts/audit-20261017/monitor-sessions.json)、[监控角色分析](../artifacts/audit-20261017/gui-monitor-role-analysis.json)、[托盘退出](../artifacts/audit-20261017/gui/tray-exit.json)。

另有一项恢复偏差：workspace_state.activePage 从 3 变为 0，尽管最终 GUI 和 App 偏好均为 StorageStructure。已定位 [AgentBackedWorkspaceStateService](../src/WinPool.Infrastructure.Windows/AgentBackedWorkspaceStateService.cs#L46) 把 ShellPageKind 名称解析为旧 WorkspacePage，StorageStructure／DiskPartition／Hardware 均不存在于旧枚举，失败后静默写 Manage(0)。原值 3 在旧枚举中是 Monitor，不能手改回 3 来假装恢复。当前启动导航实际读取正确的 app-settings.LastActivePage，因此尚不能断言重启跳错页。该冗余字段确实丢失语义；建议导航只保留 LastActivePage 一个持久来源，workspace 只保存系统／对象选择。此次未修改数据库掩盖偏差。

## 6. 架构与性能问题：具体删并方案

优先级表示修改顺序：P0 为有实测支持的主要延迟，P1 为可直接确认的重复工作／状态归属问题，P2 为后续维护成本。以下均为建议，未实施。必要的正确性边界是准确目标、当前事实、原子提交及写入结果解释；同一事实被不同包装重复验证和传输不增加这些保证。

### 6.1 P0：一次真实操作采集 7–8 次

当前 exact-member 正常成功路径如下，无需出现失败或重试：

| 序号 | 阶段 | 实现位置 | 建议 |
| --- | --- | --- | --- |
| 1 | Prepare full capture | [WindowsRealOperationPlanner](../src/WinPool.Infrastructure.Windows/WindowsRealOperationPlanner.cs#L76)、AgentRealOperationService:375 | 保留一次，用于准确预览和冻结意图 |
| 2 | Accept preflight full capture | [AgentRealOperationService](../src/WinPool.Agent/AgentRealOperationService.cs#L485) | Accept 只验证会话／冻结授权并排入唯一执行工作流；当前事实核验放临写门 |
| 3 | 后台首步 preflight scoped capture | AgentRealOperationService:1210 | 与下一项合为一处临写事实采集与规则核验 |
| 4 | Execute exact-before 再次 capture | [WindowsRealStorageBackend.MemberSets](../src/WinPool.Infrastructure.Windows/WindowsRealStorageBackend.MemberSets.cs#L55) | 消费同一 typed StepContext，不再重采同一对象闭包 |
| 5 | 写后 exact-after／postcondition | 同文件:85 | 保留；返回已验证后态，不丢弃再取 |
| 6 | 全步骤 Verified 后统一 Reconcile | AgentRealOperationService:1370；MemberSets:118 | 正常成功使用第 5 项后态结束；异常／重启／未知结果才进入恢复用例 |
| 7 | App 完成后 scoped refresh | [AgentInventoryCoordinator](../src/WinPool.Agent/AgentInventoryCoordinator.cs#L148) | 发布第 5 项生成的 inventory revision／呈现数据，不再采集 |
| 额外 | 监控 Verified 后重新绑定身份 | [MonitoringSessionEdits](../src/WinPool.Monitoring/MonitoringSessionEdits.cs) | 消费同一已验证后态；本轮联机／脱机各额外采集一次已实证 |

**非 MAX 的正常单次写操作目标：Prepare 一次、临写一次、写后一次，共 3 次。** MAX 一个步骤内包含多个候选写入，应按候选统计；容量拒绝边界需要的两轮观察另计，不能把整个 MAX 宏限制为 3 次。每个阶段内部只持有一个 typed context；事实与 target 不应在同进程各层先转 JSON、再解析、再采集。执行返回的 VerifiedStepObservation 同时供终结、下一步规划、UI 和 monitor 使用。只有观察范围确实不足或发生外部变化才取必要新事实，不预设一串成功后“兜底刷新”。

用户长时间停在确认框后，旧 Prepare 事实不能代替临写事实。准确对象定位和调用前耐久记录、Unknown 不自动重放属于真实写入语义；应由一个执行门负责，而非删除语义或每层各做一套。减少四轮采集按当前约 4–5 秒／轮有显著潜力，但不能把理论减法写成已经达到的性能。

### 6.2 P1：真实结构由 App 反复接力编排

[RealStructureEditorFlow](../src/WinPool.App/RealStructureEditorFlow.cs#L55) 在 App 查询每成员能力、逐层范围，随后分段删除／建池／建层／建 VD／初始化／布局。每段经 Prepare→Accept→Query→Capture，再把完整事实传回 App 重映射新对象身份，才能启动下一段。用户一次 Apply 成了多个跨进程业务回合；Agent 的权威作业与 App 的结构状态机共同决定进度。

改为**唯一 Agent 结构 Apply 用例**：App 交付冻结的目标草稿并确认一次；Agent 在一个 OperationId 内解析依赖、保有新对象准确身份、推进内部步骤、复用已验证观察。App 只显示版本化状态和最终结果。创建后才能得知的实际容量、provider MSR、支持的 cluster 等仍在相应步骤读取，不提前虚构能力。

对新池、已有池、明确容量和 MAX 使用同一结构用例及同一执行器，不另保留“单盘流程”“多成员流程”“旧表单流程”。步骤类型的真实差异可以保留为数据／分支，不必为每种组合复制编排类。

### 6.3 P1：模拟输入和按钮刷新重复完整预演

[StorageStructurePage](../src/WinPool.App/StorageStructurePage.xaml.cs#L2653) 的字段变化进入 UpdateButtonState→RefreshPendingActions，先 SimulationDraftPlanner.Build，再 Precheck；[Build:461 与 Precheck:472](../src/WinPool.Application/SimulationDraftPlanner.cs#L461) 都进入完整 BuildPlanWithItems。每步外层 Evaluate 后，service.Apply 又 Evaluate；同时进行关系重建、SourceFacts／Snapshot 转换。这些发生在 WinUI 事件处理链中。

简化为 `BuildSteps → 补全意图 → PreviewPlan 一次`，由唯一 reducer 返回规则决策及结果。按 baselineRevision＋draftRevision 缓存；按钮可用性只读缓存，未改变事实的选择、展开和进度不重算。字段即时校验只处理该字段；需要完整计算时放后台，按 generation 仅应用最新结果。预演过程保持一个工作模型，最终边界再统一投影／验证，避免每个步骤来回转换整份模型。

此项应先做调用次数验证，再对同一大文档测 CPU／分配；不要增加另一套“快速预演”实现与原预演长期并存。

### 6.4 P1：没有变化的进度也重建整个界面

[EditorPageBase:447](../src/WinPool.App/EditorPageBase.cs#L447) 每 250 ms Query 后无条件 ReportRealActivity；[WorkspaceRealOperations:26](../src/WinPool.App/ViewModels/WorkspaceRealOperations.cs#L26) 每次发事件；StorageStructurePage:230 随即 RefreshTopology 和 UpdateButtonState。即使状态完全相同，也创建新拓扑 VM、替换 ItemsSource、再生成动作预览。1200 次循环只意味着间隔累计 300 秒，查询和重建还另加时间。

把状态更新分清三种实际变化：phase 更新进度，busy 边沿更新交互状态，document revision 更新模型。等值直接返回。操作状态以轻量 revision 事件发布；首次附着／重连才 Query 现状。相同状态返回 100 次应产生 0 次额外拓扑投影和 0 次预演。

[WorkspaceViewModel:1723](../src/WinPool.App/ViewModels/WorkspaceViewModel.cs#L1723) 又在单文档提交、Reset、切换系统和本机更新时重建全 catalog 拓扑；普通对象选择则重建 details／comparison，setter 副作用还会重复触发。按 documentId＋revision 缓存投影，只替换变更文档的 root；选择只更新 selection 及对应 details，不重建全部 comparison；语言变更才更新本地化。已有 Snapshot 缓存应复用，不再增加一层并行缓存体系。

### 6.5 P1：IPC 发送门扩大所有操作的等待

[NamedPipeAgentConnection:368](../src/WinPool.Agent.Client/NamedPipeAgentConnection.cs#L368) 在连接、发送、等响应和重连期间持 sendGate；当前是一帧请求等一帧响应。管理采集、Prepare、模拟提交、监控和设置共享它，一个慢请求可阻止其它调用开始。调用方 Task.WhenAll 无法消除此队头等待。

先移出高频全文查询和长任务同步等待：耗时任务短请求返回 identity，后续经同一状态通道通知。仍需要并发时再在唯一连接实现里增加 correlationId 分发与有界并发，不直接移除锁后并发读写同一 stream，不创建第二套长期备用连接实现。真实写串行门继续由 Agent 管理，不能与所有读取请求一起串行化。

### 6.6 P1：同一文档和证据反复跨进程

| 当前行为 | 具体代价 | 收敛后的协议 |
| --- | --- | --- |
| App 算完整模拟文档，发送 document＋plan＋events；Agent 存后返回同一 document；App 解码只取 hash | [StorageSystemRepository:440、572](../src/WinPool.Infrastructure.Windows/StorageSystemRepository.cs#L440)；正文序列化、转义、hash、校验、decode 多轮 | 模拟提交最终只传 documentId＋expectedRevision＋typed intent＋commitId；Agent 调用共享纯用例后 CAS 提交，返回 receipt 及必要变化。App 预览调用同一纯实现 |
| scoped inventory response 和 AgentInventoryUpdatedEvent 都携带整份合并后本机文档 | 同一次采集可以传输／decode 两遍；scoped 不等于响应是 delta | 选一个消息承载全文／变化；另一个只返回 revision receipt，同 revision 不再次应用 |
| QueryRealOperation 每次返回完整 plan、step targets、evidence；服务端还读取全部 history 求状态码 | [AgentRealOperationService:576、740](../src/WinPool.Agent/AgentRealOperationService.cs#L740) 每轮至少数次 DB 读取／解码，history 本身不在响应中；历史最大单 step evidence 3.62 MB | 常态状态仅 operationId＋revision＋state＋变化 steps；计划 Prepare 取一次，证据明确请求时分页读取 |
| 启动 List 模拟 metadata 后逐份串行 Load 正文 | 本机 13 文档至少 14 次 RPC，共约 2.67 MB，repository gate 覆盖整个加载 | 初始仅 metadata＋所选文档，其余按选择加载；确需批量时有字节预算分页，不无限全量 |

短期可先让模拟成功响应只回 id／revision／hash／commitId，立刻删除全文回显。随后迁移为 Agent intent 提交时，移除旧 CommitDocument 生产入口；纯预览和提交执行共用同一 Application reducer，保留边界输入校验、CAS 和事务原子性，删除模拟 Plan／one-shot Authority／executor 包装中没有独立语义的转发层。

当前导入文件可读上限 64 MiB，而模拟传输 payload 预算 3 MiB、IPC frame 4 MiB，用户可能在昂贵解析后才被拒绝。应在文件入口与传输边界使用同一可解释预算。历史证据接近 frame 上限是风险，未观察到本轮越界，不能据此认定它已经造成卡顿。

### 6.7 P1：监控高频拉取完整 Agent 快照

[MonitoringService:691](../src/WinPool.App/Services/MonitoringService.cs#L691) 按 Clamp(1000/rate,50,1000) 拉 GetAgentSnapshot；[MonitorAlertObserver:40](../src/WinPool.App/Services/MonitorAlertObserver.cs#L40) 另每秒拉一次，关闭监控仍运行。20 Hz 设置下理论请求上限约 21 次／秒，实际会被响应和发送门限制；快照包含进程、会话、样本、健康事件和诊断。MonitorPage 又有独立绘图及 500 ms 偏好 timer，绘图复制全部 series 的 60 秒数组。

当前 AgentMonitorSampleEvent 只有契约，**没有生产者或消费者**，不能描述成“已 push 又重复 poll”。建议只实现一条常态数据路径：Agent 有序批次发送样本增量／状态变化，App 共用一个 monitor cache；初连／重连快照一次，删除旧 monitor poll 和 alert poll。采样、传输、绘图频率独立；绘图仅读取有界窗口及变化 revision。

现有事件订阅队列容量 1024，任一 overflow 可导致 transport 重连，不能把每个磁盘每个样本逐条塞入它。使用 batch／sequence，明确样本流与操作终态的不同交付语义。Agent 现有 8192 队列、1000 样本批写和 250 ms batch delay 可继续使用，无需重造采样／存储框架。

同一 monitor resolver 应识别“可靠池成员身份但无独立 OS counter”这个角色结果，避免让用户反复重选不存在的映射；不得借用 VD 的 Disk 3 冒充物理 WDC 的独立吞吐计数器。

### 6.8 P1/P2：采集、启动、身份和 MAX 的额外工作

采集脚本目前解压后按字符串 anchor 补丁，再由 scoped 脚本做多处字符串替换；关联闭包遍历多次 Get-CimAssociatedInstance，full capture 又做 Level3 provider discovery。改为一个预构建的固定脚本模板＋封闭 scope 参数＋一次关系索引；仍内嵌通过 stdin 调用。discovery 与普通读分开，按确实会影响 provider 缓存的变化触发。先减调用次数，随后再评估常驻受控 PowerShell worker／CIM session；选定后移除旧生产实现，不保留新旧双路切换。

启动时 monitor identity、Storage、Hardware 分别采相近内容；不同模块也可能同时读同一 provider。由 Agent 合并同 generation／scope 的只读采集，完整硬件事实可投影 storage 与 monitor；初始呈现使用已有缓存和 metadata。不要为合并读取再加一个覆盖所有操作的全局大锁。真实 mutationGate 当前 busy 时立即拒绝，其本身不是长队列的证据。

每轮机器绑定读取 registry／WMI，[AgentLocalSystemIdentity](../src/WinPool.Agent/AgentLocalSystemIdentity.cs) 为一个 SystemId 读取并解码整份约 624 KB 本机文档；Manage 又加载一次。启动解析并持有 Agent lifetime 的机器绑定／SystemId，数据根切换或明确重新绑定时失效。目标盘身份与当前关联仍由临写门读取，不能把稳定机器身份和易变存储事实混成一种缓存。

MAX 继续遵守已确认的整数 GiB 搜索政策，不建议恢复 UseMaximumSize、固定扣 2 GiB／4 GiB，亦不擅自改为二分。候选数量与初始估计到边界的 GiB 差距相关；当前 single／multi runner 复制了前后采集与容量拒绝流程，single 成功后还多一次 max-verified capture。合并共同的 attempt runner，候选调度保留算法差异，复用成功后态；恢复只解释 journal，不把正常成功送入恢复流程。微软侧原生分层 MAX 限制与 WinPool 重复采集是不同问题。

超时也不等于总耗时：单次 inventory 最多 60 秒，postcondition 的 60 秒窗口在某些采集之后检查；写进程等待不因 UI 断开终止。应显示“provider 仍执行／正在核验”的事实。缩短 UI timeout 不会使写入变快，更不能把超时变成自动重发。

SQLite 改动排在上述工作之后。每连接重复 PRAGMA、snapshot 同存 JSON 和关系行、每次模拟存完整历史确有成本；先确认消费者和锁等待，再去掉未使用存储表示。不要为了减体积清理测试证据，也不要把正常数据库事务原子性当成冗余。

## 7. 实际功能入口与唯一实现路径

“唯一入口”指同一用例只有一个规则／执行所有者；菜单、按钮、快捷键可以调用它。UI 入口多个不等于实现必须复制，底层不同 Windows 命令也不必被伪装成一个无类型万能接口。

| 功能 | 当前实际入口／链 | 建议保留的唯一实现 |
| --- | --- | --- |
| App 启动、激活、工作区恢复 | App lifecycle／MainWindow／Agent connection／workspace repositories | 一个 lifecycle；页面偏好唯一，workspace 只存选择 |
| 本机管理、Hardware、手动刷新 | CaptureManageInventory／CaptureScopedManageInventory／AgentInventoryCoordinator | 一个带明确 scope／purpose 的采集用例，同 generation 合并；投影分视图 |
| 模拟目录、导入／导出／删除／重置 | Manage／WorkspaceViewModel／AgentBackedStorageSystemRepository | 一个 document repository 和 schema／预算；目录 metadata 与正文读取各有明确用途 |
| 模拟属性、结构、分区编辑 | 两编辑页→SimulationEditCoordinator／DraftPlanner | 一个 Application reducer，App 预览与 Agent 提交执行同一规则；一个 commit 用例 |
| 真实模式与冻结确认 | SetRealMode、Prepare、Accept，trusted session | 一个会话授权和冻结计划实现；真实／模拟选择不是第二个真实执行器 |
| 真实单属性、磁盘、分区动作 | ProposalFactory→EditorPageBase→Prepare／Accept／Query | 同一 Agent operation executor；类型化动作，状态查询无副作用 |
| 真实结构 Apply／重建／自动布局 | App RealStructureEditorFlow 多段推进 | Agent-owned 结构 Apply，内部复用上行 executor |
| 显式容量／MAX | 各创建 factory、single／multi runner | 一个容量策略入口＋一个 attempt runner；typed MAX，既定算法不变 |
| 准入、范围查询 | QueryStructureCreationSupport、QueryVirtualDiskCreationRange、QueryPartitionResizeRange | 共用准确目标／能力 reader；查询结果可用于呈现，不赋予写权限 |
| Unknown、启动恢复 | Recoverable 列表／Query／reconcile | 一个只读结果解释用例，仅异常／重启进入，不占正常成功路径 |
| 监控、采样率、告警、CSV | MonitoringService、AlertObserver、MonitorPage、Agent monitoring | 一个 monitor session owner、一个 cache／批次通道；CSV 读实际采样库 |
| 主题／语言／MSR／gap 等 App 设置 | Settings→preference coordinator→app-settings | App 偏好唯一入口，不为纯 UI 设置走存储采集 |
| 监控／开机／7z 等 Agent 设置 | SetAgentPreference→Agent settings | Agent 偏好唯一入口；已有事件／watcher reload 合并可保留 |
| 数据位置、提权、退出 | Settings／MainWindow／Shutdown／重连 | 一个受控 lifecycle handoff，不新建另一套存储编排 |
| Explorer／原生属性／链接 | Shell open；需要权限的属性经 OpenNativeProperties | 普通 shell 动作留 App，权限动作走准确目标 handler |
| 开发消息／测试占位页 | 会话 History／TestPage | 仅保留现有呈现，不为占位页发明后台框架 |

### 可直接进入删除清单的候选

以下已检索 src＋tests 引用，尚未删除。实施时确认调用可达性并统一编译；删除旧接口时连同 codec／client／handler／仅证明接口存在的测试一起移除，不保留废接口空壳。

| 候选 | 当前事实 | 保留路径 |
| --- | --- | --- |
| OpenMainWindowRequest | src 只有协议链，构造仅测试 | 实际 App activation／托盘启动 |
| CaptureAgentInventoryRequest／旧 compare handler | 无当前生产调用构造 | Manage inventory 用例；诊断比较若仍有用途可保留底层，不保留空 RPC |
| IStorageInventoryProvider／WindowsStorageInventoryProvider | 无生产消费／实例 | 当前实际 hardware／Agent-backed provider |
| CreateHddTierTemplate | 仅测试调用 | CreateTierTemplate |
| RenameHddTier | 仅测试调用 | 无实际 UI 用例则删 factory，不为保留方法新增功能 |
| CreateMultiTieredMaximumVirtualDisk | 仅定义 | CreateTieredVirtualDisk＋逐层 MAX 意图 |
| CreateSingleMemberPool／RebuildSingleMemberPool | 仅测试调用 | CreatePoolWithMembers＋统一结构 Apply |
| TryParseGigabytes 的字符串 MAX 路径 | 仅测试调用 | 数字输入＋独立 typed MAX；仍使用的 FormatGigabytes 保留 |
| SetAutoCreateVirtualDiskAsync／SetAutoCreatePartitionAsync | 无生产调用，新池策略已固定自动布局 | 删除无入口 setter；历史偏好读取按 schema 明确处理 |
| EffectiveActiveDocument／EffectiveActiveSnapshot | 仅别名 | ActiveDocument／ActiveSnapshot |
| SimulatedSnapshot | 仅定义 | 明确选择的模拟文档 |
| MainWindow 的 null-agent 本地存储／采集／监控路径 | 正常组成始终传入 Agent 连接，随后异步连接 | 生产组成要求 Agent；测试注入 fake IAgentConnection |

上述候选集中见 [ProposalFactory](../src/WinPool.App/Services/RealOperationProposalFactory.cs)、[MainWindow](../src/WinPool.App/MainWindow.xaml.cs#L145)、[ProcessCoordinationContracts](../src/WinPool.Application/ProcessCoordinationContracts.cs)、[App/契约检索记录](../artifacts/audit-20261017/app-contract-findings.md)。单个大文件不应机械拆成 ABCD；优先删除重复路径，再按“纯编辑规则／UI 呈现”“执行用例／Windows 适配”两个职责分开。

## 8. 文档与开发工作量

当前制度已经写了“一事实一位置”和“小任务最小闭环”，实际内容仍大量复制历史现场数据、阶段状态和实现细节。问题不在缺少文档规范，而在没有执行已有的归属规则。

| 发现 | 当前冲突／成本 | 具体修改 |
| --- | --- | --- |
| README 公开尺寸过时 | 中英仍描述 520／568 DIP、112／160 列；当前 Plan／代码为两语言统一 320 DIP | README 只介绍使用方式；精确尺寸归 Product 或 UI 设计一处，删除两份重复尺寸叙述 |
| 自动创建偏好描述漂移 | README 称 AutoVD／AutoPart 独立；当前新池固定自动生成，setter 已不可达 | 根据当前实际行为重写能力表，去掉用户无法操作的偏好承诺 |
| 活动计划已完成仍被称为唯一活动范围 | Plan 标“已执行完成”，README／Development 仍引导“正在实施” | 完成计划归档；无新执行阶段时不留假活动 Plan，本审查报告不自动变成实施计划 |
| 性能、MAX、真实末态在多处重复 | README／Product／Development／Quality／CHANGELOG 重复完整数字和历史过程，容易把旧现场当当前状态 | Product 保存当前语义与限制，Quality 链接具日期／基线的证据；README 短摘要，CHANGELOG 仅重要结果 |
| 当前工作树链接失效 | 18 处相对链接指向用户已删除的 4 份 Review 文件 | 报告现状；待相关内容处置明确后统一重定向／归档索引，不擅自恢复用户删除文件 |
| 行为规则复制进源码字符串测试 | 改合法结构、命名或格式就失败，真正重复调用却不被发现 | 架构只检验硬边界；功能执行真实用例断言状态／拒绝／调用数，不以字符串出现证明端到端 |
| 失败 fixture 借用本机 WMI | 30 项恢复测试根本没到恢复断言，Infrastructure 单项目耗时 189.412 秒 | 能力 provider 依赖显式注入，合成测试无 host 回落；真实 read-only 单独分类 |

建议只维持五类当前信息：README（用户入口）、Product（能力及语义）、Development（所有权／构建）、Quality（最小命令／最新状态及证据链接）、CHANGELOG（重要结果）。AGENTS 保留操作边界，专项模型文档只有独立价值时保留并由所属文档链接。历史报告留 Archive／原 evidence，不因“冗余”删测试证据或重写旧结论。

日常工作流程可缩成 **修改 → 相关验证 → 更新唯一事实位置 → 提交**。文案／可逆布局不新开全面计划或跑全设备门；跨模块合并才用一个活动计划，明确范围和完成条件。已有测试方已完成的门，主代理审阅证据，不再跑一遍。增加新测试仅保护真实行为与回归风险，避免为每个 wrapper 写镜像测试。

## 9. 建议实施顺序与验收

只拆为 A、B 两个阶段。先恢复可用的反馈和去掉确定重复，再完成所有权和旧入口删除；每一步都合并到现有模块，不为整改先造新框架。

### A. 去掉当前热路径的重复工作

1. 修正 30 项恢复 fixture 和 2 项过时断言，隔离测试日志；让当前测试门能解释真实行为，保留本报告原失败证据。
2. 单步执行返回 typed context／verified observation，删除 Accept／Execute 前重复 capture、成功 Reconcile 和结果刷新重采；监控消费同一后态。为实际 write adapter 补现有 timing 的一个独立阶段，区分采集与写入。
3. UI 同状态短路；拓扑按文档 revision 更新；BuildSteps＋一次 Preview，去掉 Evaluate 重复和 Reset／setter 的全量刷新。
4. 模拟响应先改 receipt，inventory 全文只走一条消息，operation 状态只读轻量结构；移除重复 monitor 全快照查询，再接唯一批次数据通道。

### B. 统一用例与删除旧路径

1. 真实结构 Apply 移到 Agent，一个 OperationId 内完成内部依赖；删除 App 阶段接力。模拟提交统一 Agent 调用共享 reducer，删除旧全文提交实现。
2. 采集模板／scope／关系索引统一，启动与同时读取共用事实；目录按需加载，稳定机器绑定进程内持有。减少调用后再测冷启动成本，决定是否替换为常驻 worker。
3. 合并 MAX attempt runner；按 §7 删除无生产调用接口、别名与 null-agent 路径；页面持久化保留一个来源。
4. 同步更新当前能力与文档归属，归档已完成 Plan；旧代码和已过时说明不作为长期 fallback 留存。

### 量化完成条件（目标，尚未达到）

| 项目 | 验收条件 |
| --- | --- |
| 非 MAX 正常单次真实写操作 | 无外部变化、无重试场景不超过 3 次 provider inventory；同一 verified 后态供终结／UI／monitor，不再各取一次。MAX 按候选统计，容量拒绝的两轮观察另计 |
| 结构 Apply | 用户一次 Apply 对应一个 Agent 作业，内部阶段无需 App 收全文再发下一步；目标／后态仍准确 |
| 预演和界面 | 一次有效草稿 revision 仅一次完整预演；100 次同进度通知额外拓扑重建为 0；选择变化不重投影全 catalog |
| 模拟／inventory 协议 | 正常模拟成功不回显已持有全文；同 inventory revision 全文只送一次；对 payload 字节数有同一预算 |
| 监控 | 正常采样不调用完整 GetAgentSnapshot poll；一个 sample batch source、一个 App cache；按设定采样率落盘并如实记录编辑缺口 |
| 启动 | metadata＋当前文档即可完成可用工作区；不逐份串行加载全部模拟正文，不为 monitor／Storage／Hardware 重复取同代事实 |
| 正确性 | 合法／非法草稿输出一致；准确身份、CAS、写前耐久边界、Unknown 不重放及重连语义通过行为测试 |
| 工程 | 32 项剩余失败处理后全量一次；按变更复验本轮六条真实闭环与模拟场景；代码稳定后不再无因重复全量 |

性能复测固定同硬件、同文档、同监控状态，自动测试与 GUI 计时串行，报告 Prepare／排队／采集／写／核验／UI 应用及 payload bytes。保留 raw wall time 和人工确认区间，不用工具 wait 或 xUnit 整测试耗时冒充方法耗时。现有 StorageOperationTiming 足够扩充这些字段，不需要先搭建一套可观测性平台。

验收优先看次数、字节和职责是否消失，再比较实际秒数。本轮仅证明改造前问题和可删并位置，未对“最终几秒完成”作未经实现的承诺。

## 10. 交接索引与最终状态

| 要接手的内容 | 入口 |
| --- | --- |
| 真实操作／采集重复链 | [后端审查及六次实机补证](../artifacts/audit-20261017/backend-findings.md)；AgentRealOperationService／WindowsRealStorageBackend.MemberSets／AgentInventoryCoordinator |
| App／模拟／IPC 调用与可达性 | [App 与契约审查](../artifacts/audit-20261017/app-contract-findings.md)；StorageStructurePage／WorkspaceViewModel／StorageSystemRepository／NamedPipeAgentConnection |
| 自动化命令、失败、依赖 | [自动化报告](../artifacts/audit-20261017/automated-findings.md)、[机器汇总](../artifacts/audit-20261017/automated-summary.json) |
| 本轮实际写入及状态 | [计划与事件后态](../artifacts/audit-20261017/operations-after.json)、[GUI 截图与时间](../artifacts/audit-20261017/gui/) |
| 当前文档规模与断链 | [仓库统计](../artifacts/audit-20261017/repository-metrics.json) |

本次交付只新增本文；没有生产改造、依赖升级或未经验证的删代码。所有测得问题与方案均以当前基线为准。App／Agent 正常退出，真实结构和模拟对象恢复，设置恢复；workspace 冗余 page 字段的已确认偏差单列保留。测试门仍为失败，不能作为发布通过记录。后续实施从 A 开始，完成后以同口径数据替换推断，而不是追加更多旁路与兜底。
