# WinPool V0.61 审查验收与 0.70 前公开测试准备度

日期：2026-10-18（Asia/Taipei）。审查源码：`main / f1e103f8d12a96612cf62a229d390891c66daf31`，产品 V0.61／0.6.1.0。对照本会话上一轮[仓库审查](WinPool-Repository-Audit-20261017.md)，并以用户随后批准的 [V0.61 实施计划](../Archive/20261018-v061-completed/Plan.md)确定采纳范围。本文是验收结果，不新建开发计划，也不代表发布批准。

## 1. 验收结论

**主要改造已完成，尚不能签为全部闭环；具备首个公开测试候选的基础，建议先完成一轮小范围收口，再对外发送。0.70 前交付公开测试版在工程范围上可行，无须再做一轮整体架构重写。**

| 用户关心的问题 | 本轮判定 |
| --- | --- |
| 有没有满足之前的审查？ | 满足了主要执行所有权、重复采集、模拟提交、IPC 与监控改造；仍有 UI 全量重建、旧监控分支、启动采集合并、字节预算等未关闭项。不能以 Plan 已归档代替逐项验收。 |
| 性能是否解决？ | 已证明多条重复链被移除，代表性普通真实单写达到三次采集。尚未证明“几乎所有操作都快”，数秒采集成本、大文档 UI 成本和启动重复仍在。 |
| 当前完整检查是否通过？ | 本轮新全量 **2504 项：2499 Passed、1 Failed、4 明确 Skip**；已配置静态 analyzers 通过。测试门仍红，失败是过期源码字符串断言，未发现它证明当前提权交接逻辑失效。 |
| 是否有新发现？ | 普通真实操作完成后，Agent 的 `verifiedPostStates` 持续持有整份采集结果；只有结构内部阶段会移除。应在公开测试前修正生命周期。 |
| 当前包能否直接作为“全部验收通过”的版本发布？ | 不建议。候选自身完整性通过，但它与本轮全量所用 Release 是不同 build；需要在收口后绑定一个最终 ZIP、源码和实际启动路径。 |
| 是否必须先做常驻 PowerShell、IPC 多路复用、全库重构？ | 不需要。这些已由批准计划明确延期。它们可以是后续性能选项，不能临时改成 V0.61 首稿验收的必选项。 |

本次静态审查没有找到可证明的新 P0／P1 错盘写入或 Unknown 自动重放缺陷；这不等于全部设备、恢复路径和长期运行已经获得正确性证明。以下区分确定缺陷、剩余设计工作和未验证边界，不用一个“通过率”混合计算。

## 2. 本轮实际执行及证据边界

本轮重新检查生产调用链、已退役入口、当前文档、测试与打包证据；独立运行了 11 个测试项目的完整集合及 analyzers。没有修改产品／测试源码，没有重打包，没有执行真实存储写入。初始用户文件 `Ref/` 和已有验收文档均保留；过程中另一会话产生的验收文档也未覆盖。本文是本任务唯一新增受控文档。

当前生产代码与 `d210a23` 的 R1–R3 修复基线相同，之后到本轮 HEAD 仅文档变化。当前 Release 的 21 个 portable PDB 与对应 DLL 配对，**461 个编译源文档校验和全部匹配**，无缺失或不匹配；本轮 480 个源码／构建配置输入前后哈希未变。因此复用当前 Release 执行 `--no-build --no-restore`，没有为本次审查重复构建。此前完整 Release 构建 0 warning／0 error 是既有证据，不能称本轮新 build。

证据集中在本地 [artifacts/acceptance-20261018](../../artifacts/acceptance-20261018/)：

| 范围 | 证据 |
| --- | --- |
| 自动化结果、命令、失败／skip | [自动检查说明](../../artifacts/acceptance-20261018/automated-verification.md)、[机器汇总](../../artifacts/acceptance-20261018/automated-summary.json)、[分项目命令](../../artifacts/acceptance-20261018/test-stages.json)、[TRX](../../artifacts/acceptance-20261018/trx/) |
| 源码与运行树对应 | [PDB／源码核对](../../artifacts/acceptance-20261018/pdb-source-verification.json)、[输入前后哈希](../../artifacts/acceptance-20261018/source-verification.json)、[运行文件清单](../../artifacts/acceptance-20261018/standard-manifest.json) |
| 调用链与残项 | [后端逐项核对](../../artifacts/acceptance-20261018/backend-verification.md)、[App／契约逐项核对](../../artifacts/acceptance-20261018/app-verification.md) |
| 包内容与不同 build | [候选校验](../../artifacts/acceptance-20261018/package-verification.json)、[程序集差异](../../artifacts/acceptance-20261018/assembly-difference-summary.json) |
| 文档链接 | [当前入口文档](../../artifacts/acceptance-20261018/document-links.json)、[归档 Plan](../../artifacts/acceptance-20261018/archived-plan-links.json) |

这些 artifacts 是本地证据，受 Git 忽略，不随本文自动发布；缺少附件的读者不能仅凭链接认定自己已重放验证。

### 2.1 新的 GUI 补验没有形成独占运行结果

本轮使用 WinApp UI Automation 尝试补验标准 Release。13:13:28 启动本轮 App（PID 16804）后，发现候选目录中的另一个 Agent（PID 29200）早在 13:11:15 已由其他父进程启动。记录见[进程路径与时间](../../artifacts/acceptance-20261018/gui/mixed-runtime-observation.json)及[初始 UI](../../artifacts/acceptance-20261018/gui/initial-ui.txt)。

这不是配套运行树的独占测试，不能把初始禁用状态记作当前产品故障，也不能把它记作 GUI 通过。已通过窗口关闭本轮启动的 App，未停止另一会话的 Agent，未继续模拟提交或真实写入。已请求运行协调；本文定稿时尚无回复。因此本轮 GUI 结论使用下文明确列出的既有、同生产源码主线的实机证据，**没有新增一次完整 GUI 端到端通过记录**。

本轮另做了原生只读现场采集：[native-before.json](../../artifacts/acceptance-20261018/gui/native-before.json)。读取时 WDC 为在线 GPT、16 MiB MSR、一个 NTFS／64 KiB 数据分区，`E:`／`WinPool_Test`，未处于具体池内；两块 Samsung 仅记录。由于本轮没有写入，不声称执行了“写入后恢复”或对另一会话后续操作作末态担保。

## 3. 对上一轮审查的逐项闭环

“已闭环”仅指本行具体要求；“部分”后文给出剩余代码和最小改法。代码位置均按上述源码基线。

| 上一轮要求 | 当前生产实现与证据 | 判定 |
| --- | --- | --- |
| 普通单写移除 Accept、Execute-before、正常 Reconcile、UI 后态的重复采集 | Accept 不采集；preflight 的 typed context 供 Execute；正常 Verified 直接成功；后态供 inventory／monitor。本机代表操作恰有 3 次底层采集 | 已闭环于约定场景 |
| 普通权限采集不因 discovery 失败丢掉成功枚举，避免无效 extent 回退 | 固定模板将 discovery 与枚举失败域分开，缺物理事实不进入 extent；已有普通权限候选刷新成功 | 已闭环于已测路径 |
| 真实结构 Apply 由 Agent 完成 | `AgentRealOperationService.StructureJobs` 持有整个作业的写入所有权，先耐久父作业／child／pending，再执行内部阶段；App 仅草稿与确认 | 已闭环 |
| MAX 共用 attempt，既定算法不变 | 普通／分层调度复用 `MaximumAttempt`，成功后态复用；保留整数 GiB 搜索及容量拒绝证明 | 已闭环于本机单 HDD 与合成范围 |
| Query／List 无恢复副作用，Unknown 不自动续写 | 只读查询与显式重查分离；启动恢复只解释已发生结果；App 旧 250 ms 轮询退出 | 已闭环；未冒充真实故障注入 GUI |
| 草稿一次有效 revision 一次完整预演 | BuildEdits→补齐意图→单次 Precheck；共享 reducer；`SimulationEditingSession` 以 baseline／binding／content revision 缓存 | 已闭环；大文档 CPU 上限未测 |
| 相同进度不重建界面；选择与单文档变化局部更新 | 相同真实进度短路已接通；普通选择／切系统／提交仍有全量投影和重复 setter 刷新 | 部分，见 R3 |
| 模拟编辑只发 typed intent 与轻量 receipt | App 预览、Agent 共享 reducer 提交；ID／时间冻结；CAS 与 commitId 同事务；正常路径不再发送 plan／events／已有全文 | 已闭环 |
| inventory 全文不在事件和响应重复发送 | 全文走 published event，响应为 receipt；缺事件时按 ID／hash 查询现有缓存 | 已闭环；一次必要恢复读取不算第二实现 |
| 真实操作状态轻量、证据按需有界读取 | 状态不携带完整 history；证据分页按 step 数；尚无实际 wire 字节上界，终态恢复仍依赖取证据 | 部分，见 R5 |
| monitor 只有一个生产 owner／批次源／App cache | Agent 批次、上游序号／gap 与事件重同步已接通，正常采样无完整 snapshot 轮询 | 生产链已闭环；App 旧本地实现未删，见 R4 |
| 启动只加载目录 metadata 与所选文档 | 惰性正文加载、选择 generation 拒绝迟到结果已接通 | 已闭环 |
| 唯一 collector，并发／启动同代事实复用 | 统一实例已接通，verified 后态可复用；生产公开读取每次自行 BeginGeneration，同代合并能力主要由测试直接调用 | 部分，见 R2 |
| 稳定身份不反复解码整份文档 | `AgentLocalSystemIdentity` 缓存 SystemId；机器 binding 的 Registry／WMI 仍重复读取 | SystemId 已闭环，额外成本保留 |
| 页面偏好一个来源 | LastActivePage 归 App settings；workspace 旧页字段仅兼容读取，不再作为另一写入来源 | 已闭环 |
| 删旧 RPC／factory／provider／null-agent 路径 | 多数点名入口及 Inventory 项目退出；字符串 MAX parser 与本地监控旧路径仍在 | 部分，见 R4 |
| 合成测试不偷读本机，未执行明确 skip，日志隔离 | fixture 注入能力，诊断 sink 隔离，4 项显式 skip；旧 32 失败中 31 通过、1 退役 | 旧失败已处理；当前另有 1 项失败，见 R6 |
| R1 草稿确认范围、R2 重开恢复终态、R3 monitor 迟到状态 | 摘要绑定原草稿；工作区恢复 watch 实际接通；相同 Agent revision 拒绝旧状态覆盖 | 修复已接通且定向回归通过 |
| 当前文档单一事实入口、完成计划归档 | 当前 8 个入口文档的文件链接可达；归档 Plan 链接未随移动调整，UI 完成口径过宽 | 部分，见 R7 |

### 3.1 实际功能接口的所有者

当前架构方向合理，可以继续沿用，不建议再按文件规模拆层。应保持以下用例边界：

| 功能 | 应保留的实现路径 | 尚需收口 |
| --- | --- | --- |
| 模拟编辑 | App 草稿／预览→Application reducer；提交 typed intent→Agent 同一 reducer＋CAS | 去掉外围重复 UI 投影；不要恢复另一套模拟 executor |
| 真实单步／结构作业 | App Prepare／确认→Agent operation／structure job→同一 Windows backend | 已验证后态的生命周期；没有必要让 App 再接力 child |
| MAX | typed MAX 意图→既定容量策略→共享 attempt | 删除 test-only 字符串 MAX parser，不改容量政策 |
| 本机事实／Hardware／monitor 身份 | Agent collector→明确 purpose／scope→各自投影 | 将同代并发复用接到生产启动 owner；新写边界仍重新观察 |
| 真实状态／恢复 | 轻状态＋事件；明确只读重查；证据按需读 | 终态不要依赖大证据成功下载 |
| 监控／CSV | Agent 唯一采样与持久化→批次事件→App cache | 删除 App 无 Agent 的 sampler／CSV 实现 |
| 导入／创建／导出／正文加载 | 一个 document repository 和一致的大小契约 | 统一 raw document／escaped IPC／文件入口预算 |
| 页面设置／提权／数据位置／退出 | App 偏好、Agent 偏好各有归属；现有 lifecycle handoff | 将关闭行为测试从私有变量拼写解耦 |

多个按钮调用同一用例不违反“唯一入口”；采集、读取缓存、查询能力也不是同一语义。不能为了接口数字减少，把它们改成一个无类型万能 `Read`，或将必要的输入／持久化／临写边界检查一并删掉。

## 4. 未闭环问题与具体修改方案

### R1：普通真实操作长期持有完整后态（确定缺陷，P2，建议首个公开包前修复）

[AgentRealOperationService.cs](../../src/WinPool.Agent/AgentRealOperationService.cs#L37) 的 `verifiedPostStates` 按 OperationId 存储 `RealVerifiedPostState`；第 1395–1396 行每次成功后加入完整对象。全生产源码唯一 `TryRemove` 位于 [StructureJobs.cs:629](../../src/WinPool.Agent/AgentRealOperationService.StructureJobs.cs#L629)，仅由结构内部阶段消费。普通 `RunAcceptedAsync` 的 finally（第 892–898 行）只通知、清 runningTasks、释放 gate，没有释放后态。

因此不同 ID 的普通真实操作不断完成时，Agent 会持续保留对应完整 facts 对象图，关 App 也不释放。此结论来自确定的引用生命周期，不是假定“看起来内存大”；本轮没有测增长 MB／次或 OOM 门槛。

**最小改法：**让 `RunStepsAsync` 直接返回供当前结构阶段使用的最后 verified 后态；若暂不改返回值，至少仅为结构阶段存交接值，并为普通完成及异常路径显式移除。不新增通用缓存／淘汰框架。同步检查 backend 的 `operationScopes` 生命周期，它也存在按 operationId 累积且未见移除的路径。

**验收：**多个不同 ID 的普通成功／失败操作结束后，服务不再持有它们的临时事实；结构阶段仍能消费一次正确后态，原三次采集预算不退化。

### R2：生产启动／并发读取没有共享同代批次（P2，原性能要求部分未完成）

[AgentInventoryCollector.cs:32–39](../../src/WinPool.Agent/AgentInventoryCollector.cs#L32) 的 Local／Hardware／Scoped 入口各自 `BeginGeneration()`；内部合并只对相同 generation 生效。生产调用者没有共享 generation 后再调用 CollectAsync 的接线。启动 Storage→Hardware，以及无 verified 后态的 monitor 恢复仍会分别采集。

**最小改法：**由现有 startup／read owner 为同一轮只读消费者提供一个完整批次及覆盖范围，Storage／Hardware／monitor 身份按需投影；临写、写后或明确新鲜刷新另开观察边界。不能用固定 TTL 或无限缓存取代新鲜性。用真实生产入口计数证明合并，不能只测手工传相同 generation 的 helper。

### R3：选择、切系统、单文档提交仍重建无关界面（P2，原 UI 要求未完成）

[WorkspaceViewModel.cs:1664–1689](../../src/WinPool.App/ViewModels/WorkspaceViewModel.cs#L1664) 每次清空并重建全部**已加载**文档的拓扑。SwitchSystem 和单文档提交仍调用它。对象选择第 372–373 行重建完整 comparison；commit 第 1096–1099 行先经 RebuildObjects／setter 触发 details／comparison，随后显式再做一次。语言未变时 inventory 应用也会进入 RefreshLocalizedContent。

**最小改法：**保留既有 VM，按 documentId／revision 只替换变更根；selection 只改高亮与当前详情；合并一次提交后的 UI 发布，删掉 setter 后重复刷新。不要新增 VM 框架。验收切换两个已加载未变文档、选择不同对象和提交一个文档时，无关拓扑／comparison 的重建计数为零。

这些代码证明仍有额外计算，不证明当前每次交互都卡数百秒。已有预演缓存测试也不能证明 Workspace 全量重建已经消失。

### R4：两个旧实现仍留在生产程序集（P2，冗余未清零）

[MonitoringService.cs](../../src/WinPool.App/Services/MonitoringService.cs#L86) 仍允许 null Agent，并保留本地 DiskPerformanceSampler、CSV 和独立 loop（第 970–1065 行）。MainWindow 正常组成已强制 Agent，故不能说目前 UI 总在双采样；问题是已替代实现仍未退出。另 [RealVirtualDiskCreationCapacityResolver.cs:26](../../src/WinPool.Application/RealVirtualDiskCreationCapacityResolver.cs#L26) 的 TryParseGigabytes 字符串 MAX 路径只有测试消费者，迁目录未构成删除。

**最小改法：**monitor service 必须传 Agent，测试使用 fake connection；删除本地 sampler／CSV 分支和 test-only parser。保留仍有生产消费者的 FormatGigabytes／ResolveAsync。迁移有价值的行为测试，不为旧私有实现维持存在性断言。

### R5：导入、正文和证据传输仍没有统一字节边界（P2，确定边界缺口，越界故障未实测）

导入上限为 [DesktopExportService.cs:112](../../src/WinPool.App/Services/DesktopExportService.cs#L112) 的 64 MiB，持久 document 是 [StorageSystemRepository.cs:29](../../src/WinPool.Infrastructure.Windows/StorageSystemRepository.cs#L29) 的 raw JSON 3 MiB，IPC 是 [IpcProtocol.cs:12](../../src/WinPool.Ipc/IpcProtocol.cs#L12) 的完整 frame 4 MiB。JSON 作为字符串再转义后，raw 3 MiB 不保证 wire 小于 4 MiB。部分输入会先昂贵解析，再在后续保存／发送处被拒绝。

真实证据 `LoadEvidenceAsync` 只限定 1–16 个 step，SQL LIMIT 不限制单条 target／evidence 字节；codec 在序列化后才拒绝过大 frame。[RealOperationStatusCache.cs:40–71](../../src/WinPool.App/Services/RealOperationStatusCache.cs#L40) 又在初次恢复／终态强制取证据，因此证据过大可能阻碍终态呈现。本机历史单条 evidence 已有 3,617,889 UTF-8 bytes，但它本身未超过 4 MiB；**本轮没有复现真实越界或据此造成的终态卡死**。

**最小改法：**先让终态以轻状态独立完成；导入／存储／传输使用一个明确、对称且按实际 wire 计算的限制，入口及时拒绝。证据页按字节缩页；单条超大证据采用小范围的分块或摘要＋按需读取。不要为此建设通用文件传输框架。先覆盖引号／转义密集文档、大单 step、恢复终态三个边界。若候选暂保留限制，应在 TESTING 明说，不承诺可导入 64 MiB 文档。

### R6：完整测试仍有一个过期源码断言（P2，工程门未通过）

[ArchitectureBoundaryTests.cs:730](../../tests/WinPool.Architecture.Tests/ArchitectureBoundaryTests.cs#L730) 要求 MainWindow 含字面量 `if (_closingForElevationHandoff)`。当前实现已改为统一的 `_closePreparationStarted`／`_closingAfterAgentShutdown`。

静态核对发现：成功 handoff 调用 RequestClose 后立即 return；RequestClose 同步设置关闭状态、禁用 UI、取消连接并停止事件源；finally 在关闭中不再 SyncModeSwitch；Settings 收到成功后立即退出。故本轮失败支持“测试契约过期”，不支持“当前提权必坏”。这仍是一个真实红门，不能直接忽略或把旧绿色汇总覆盖过来。

**最小改法：**更新现有测试，保护交接后不再刷新关闭 UI／不再调用已停 Agent 的行为；删除对私有字段拼写的依赖，不为过测试恢复旧变量。先跑 Architecture 与受影响 lifecycle 测试；涉及生产逻辑变化才补相应 GUI handoff。

### R7：文档归档断链与完成措辞过宽（P3，文档验收未完成）

当前 8 个入口文档的文件链接无缺失；但刚移动的 [V0.61 Plan](../Archive/20261018-v061-completed/Plan.md) 中 **40 处相对文件链接、36 个唯一目标全部失效**，仍按原 `docs/Plan.md` 位置写路径。原文件内容存在，不是证据被删除。将 `../src/`、`Quality.md`、`Review/` 等改为从归档目录可达的相对路径即可，不重写历史结论。

[Quality](../Quality.md) 的“选择改变只更新选择呈现”应与 R3 一致收窄；2500 Passed／4 Skip 是修复后按用例复用的组合证据，不是当前一次全量结果。保留它的历史真实性，同时把当前 2499／1／4 与未修项记在唯一当前结果处。不要再生成多份互相竞争的完成声明。

### R8：剩余性能成本与未测范围（不自动升级为首稿前置）

每次固定 PowerShell 5.1 子进程启动及 CIM 读取仍有成本；稳定机器 binding 仍每次 Registry／WMI；控制管道仍串行；SQLite 某些重复表示／PRAGMA 仍在。这些存在不等于它们都已被测成主要瓶颈。批准计划明确推迟常驻 PowerShell、控制多路分发和全库重构。

预演虽已单遍，仍在 UI 线程同步进行逐步模型转换；monitor render timer 仍随采样率运行并复制 60 秒窗口。后续应在实际大文档、多目标与高频交互上测 CPU／分配，再决定是否后台化或降刷新次数。exact-member 与 MAX adapter 还缺独立 `real.provider` 计时，应在已有 timing 中补一个阶段，以便区分采集与写命令本身，不先搭新观测平台。

## 5. 自动化与静态分析结果

11 个测试项目各运行一次 Release no-build/no-restore，总命令时间 **285.770 秒**。Infrastructure 的 187 秒是正常完成，不是中止；120 秒 inactivity guard 未触发。Inventory 项目已退役，不能继续用旧 12 项目数作为完整性要求。

| 项目 | Total | Passed | Failed | 明确 Skip |
| --- | ---: | ---: | ---: | ---: |
| Agent.Client | 24 | 24 | 0 | 0 |
| Agent | 156 | 156 | 0 | 0 |
| App | 461 | 461 | 0 | 0 |
| Application | 441 | 441 | 0 | 0 |
| Architecture | 44 | 43 | 1 | 0 |
| Domain | 23 | 23 | 0 | 0 |
| Execution | 113 | 113 | 0 | 0 |
| Infrastructure | 984 | 983 | 0 | 1 |
| IPC | 12 | 12 | 0 | 0 |
| Monitoring | 53 | 53 | 0 | 0 |
| Persistence | 193 | 190 | 0 | 3 |
| **合计** | **2504** | **2499** | **1** | **4** |

4 项 skip 是 host read-only admission 1 项，以及 1 GiB 归档／rotation／7z 参数测量 3 项，环境开关未启用。它们现在在 discovery 阶段明确跳过，不再以普通 Fact 提前 return 冒充通过。当前基础设施集成包含实际命名管道、进程、临时 SQLite／文件；真实存储写 adapter、目标与故障多为 fixture。App.Tests 不启动 WinUI，不能替代 GUI。

旧 32 失败逐名对照：**31 项本轮 Passed，1 项浅层 UI 源码测试退役**，不是“32 项都重新通过”。旧 R1–R3 的 711 条原始 TRX 结果按 testId 合并为 706 个有效 Passed，未将定向重跑重复累加。

静态分析命令 `dotnet format analyzers WinPool.slnx --no-restore --verify-no-changes --severity warn --verbosity diagnostic`：exit 0，41.598 秒，21 项目／552 文件／0 需修改文件。只代表当前已配置 analyzers，不是所有可能分析规则或安全证明。

当前依赖清单为 43 个去重包版本，与昨日 NuGet 扫描版本集合一致，复用昨日 0 报告命中的结果；**今天没有重新请求漏洞公告**，也未将非 NuGet 二进制纳入该扫描。

## 6. 实机性能与 GUI 证据能证明什么

下表是已保存并复核的既有 V0.61 主线证据，不冒充本轮新做的实盘测试。R1–R3 后续修改及其定向回归另有基线记录。

| 场景 | 可采用的结果 | 不能外推的结论 |
| --- | --- | --- |
| 普通单写 `40757c3c…`：`Q2`→`V061_Single` | [计时](../../artifacts/test-results/v061/real-single-timing.json)：provider 3.414／3.277／3.409 秒，正好三次；外层 prepare＋preflight＋execute 约 12.54 秒。随后另行恢复 `Q2`，恢复不计入此计时 | 不是本轮新测；不是完整 UI 墙钟或 write cmdlet 自身耗时；不与条件不同的旧约 40 秒计算提速百分比 |
| R1–R3 后候选 Medium Storage 手动刷新 | [计时](../../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/candidate-refresh-timing.json)：一次 provider 2.769 秒，merge 5.6 ms，persist 28.6 ms | 同日志另两条 2.729／2.612 秒属于更早启动时段，不能加成“一次手动刷新三采”；启动重复仍是 R2 |
| 真实结构 HDD MAX 与普通 MAX | 既有 10 阶段／7 阶段作业完成，MAX journal 与原生读回相符；受控清理完成；[139 项硬件核验](../../artifacts/test-results/v061/final-hardware-verification.json)通过 | 仅单 WDC 场景，不证明多成员、多层、SSD／SCM、Mirror／Parity |
| 模拟及开发数据 | 较大约 530 KB 文档草稿／撤销／重做，导入导出及测试副本提交；[13 个原模拟文档不变](../../artifacts/test-results/v061/final-simulation-integrity.json)；schema19→20 与标准／便携往返有记录 | 不证明更大文档的高频 UI 延迟，也不证明所有外部格式兼容 |
| 候选 App 关闭重开、monitor、正常退出 | R1–R3 后 [候选验收](../../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/candidate-verification-final.json)：同 Agent 10 Hz、1767 持久样本、终态4／drop0，正常退出无新增错误日志 | 短时且单机；不替代长期 20 Hz、多 target、归档压力或真实断电恢复 |
| R1 草稿确认范围 | 新候选真实 Prepare 后取消，child0、实际写0；范围扩张拒绝由定向行为回归覆盖 | 不是在真盘注入冲突后仍成功写入的证明 |

普通权限→提权的 UAC 安全桌面确认没有自动化完整覆盖；High 真实写通过与 Medium 候选启动通过不能互相替代。新一轮独占 GUI 未完成的原因见 §2.1。

## 7. 当前候选包与公开测试条件

当前登记候选是 `d210a23` 批次 [WinPool-V0.61-win-x64.zip](../../artifacts/packages/V0.61/20261018T041447677Z-900efa87aaf041d2ab0d311dda6f0ba4/WinPool-V0.61-win-x64.zip)，104,095,807 bytes，SHA-256：

```text
453ad2cacbb59bcdc94bcb7a09c1936331aba8f1f06e495b5f74ea09ff8ea45c
```

ZIP CRC 与 586 个文件逐项 hash＝包目录＝既有独立解压目录；585 runtime＋TESTING.md。自包含 .NET Core／Desktop 10.0.12、Windows App SDK、资源、7za 及 7-Zip License／NOTICE 存在，无 Data、数据库、日志、PDB、源码或脚本混入。这是完整候选，不是损坏包。

但当前标准 Release 与该候选的 **10 个 WinPool DLL 不同**。9 个类库的方法 IL／嵌入资源相同，App 的 4284 个方法中 9 个生成方法 IL 不同，其他方法 IL／资源相同，MVID 不同。当前 Release 的 SourceLink 指向 `f1e103f`，候选记录的源码基线为 `d210a23`；候选 ZIP 不含 PDB，未直接读取其 SourceLink。没有证据认定产品手写代码漂移，也不能宣称“只差 SourceLink”或字节一致。公开发送时只认一个最终批次；把定向回归、实际启动路径及 ZIP hash 绑定，不混用两个 build 的验收结论。无需追求 ZIP 时间戳逐字节可复现才允许首稿。

### 7.1 0.70 前的最小收口

只分 A、B 两步，沿现有模块执行，不再增加版本路线图或新的审批台账。

| 步骤 | 具体工作 | 完成条件 |
| --- | --- | --- |
| **A：局部收口** | 修 R1 临时事实生命周期、R6 过期测试；完成 R2／R3／R4 原承诺的生产去重与删旧；收 R5 大小边界，先确保轻状态不依赖证据；修 R7 文档链接和过宽措辞 | 当前红门消失，修改对应的直接行为回归通过；旧路径实际退出；不因修复回到全文往返／额外采集 |
| **B：一个最终候选** | 源码稳定后构建一次，打包一次；对实际发送包做独占启动／刷新／模拟提交／重开 monitor／正常退出；真实执行有改动才在获准 WDC 做代表写入和末态核验 | 记录单一源码基线、版本、ZIP hash、真实路径及通过／skip／未测范围；有界内存与三次预算不退化；随后交外部测试 |

R1、R6 和最终包身份是本审查建议的首包前优先项。R2–R5 的剩余部分若由用户明确接受为候选限制，可以先给专门测试者验证，但不能同时宣称“上一轮全部要求已关闭”。其中终态显示与证据下载解耦应优先于美化或新增功能。

本轮已有一次完整基线，不要求每修一个小项重跑全套。共享契约／生产路径有变化时在最终稳定批次收一次完整门；单纯修测试拼写或文档只跑受影响检查。失败后补相关范围，不不断追加全仓扫描。

### 7.2 对外首稿仍需补齐的短说明

现有 TESTING 已说明配套 App／Agent、全目录解压、托盘退出升级、数据位置、真实模式及不支持功能，基础可用。补在同一份说明即可：

- 目标 Windows 版本与**实际已测**版本／权限分开；标明单 WDC 实机覆盖与尚未覆盖的组合。反馈直链可复用 App 现有 [GitHub Issues](https://github.com/GitRuozhi/WinPool/issues)，附版本／包 hash／环境／复现步骤。
- 说明实际保留的导入大小限制、性能残项、Unknown 的只读核对与剩余结构目标需重新确认，不把未做的长期或 UAC 测试写为通过。
- 按最终可执行文件的实际签名状态说明首次启动体验。本轮未检查最终签名，不猜测每台机器都会出现何种提示；不要求先完成 Store／MSIX 才给首稿。
- 当前包仅检出 7-Zip 的许可／NOTICE 文件，应清点实际随包运行资产并补必要第三方说明。本轮是文件及 NuGet metadata 技术盘点，不是法律合规裁定；不要求将 WinPool 自有代码改为开源许可证。

ZIP 自包含交付本身合理：微软支持未打包应用随附运行时，.NET 与 Windows App SDK 的 self-contained 需分别满足，当前技术清单已覆盖这两部分。[微软自包含部署说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)

README 目前以 Windows 10 22H2 为支持目标。该产品兼容目标应与运行时上游支持区分：核对时 .NET 10 的官方 Windows 支持表未列 Windows 10 Pro 22H2，列出的 Windows 10 项为特定 Enterprise／IoT 版本；这不否定本机实际可运行结果，也不能称上游正式支持。公开说明应准确限定目标／实测／上游支持三者。[.NET 10 支持系统表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)

### 7.3 首轮外部测试重点

先覆盖没有开发环境依赖的启动／刷新／退出、模拟编辑与导入导出、monitor 重连与长期运行，再由有合适设备的测试者验证其明确授权的真实目标。Windows 11、SSD／SCM、多成员、冗余、多层 MAX、DPI 和长时归档仍是外部覆盖任务；不要求购买整套硬件、穷尽组合后才发给首批测试者。

公开测试版的标准是范围诚实、基本路径可重现、已知局部缺陷先收口、反馈可定位，不是宣称全部存储组合已获验证。版本号无需逐个经历 0.62–0.69；按仓库版本规则为实际发送批次编号即可。

## 8. 不同审查结论的取舍与交付状态

本轮不修改用户已有的其他验收文档。它们分别对照不同原报告，不能简单合并“满足／不满足”数量。本会话上一轮仓库审查本就将常驻 worker 放在去重之后决定；批准 Plan 更明确延期。因此，以“没有常驻 runspace”“仍是三次采集”“仍保留 60 秒观察窗口”“模拟提交仍经 Agent”为由，直接判已批准改造全部未执行，不符合当前验收合同。窗口仍存在也不等于每次正常成功固定等待 60 秒。

相反，“死代码全删干净”“所有选择只更新呈现”也超出当前证据：R3、R4 和新增 R1 是明确残项。模拟与真实规划器服务不同边界，存在多个名称不自动等于同一功能多实现；必须沿实际调用和所有者判断。

本次可确认的是：核心架构改造已经进入生产路径，旧的普遍冗余有明显实质削减；完整测试和若干生命周期／去重／预算项尚需收口。**建议状态：主要改造验收成立，完整闭环未通过；完成 §7 的小范围收口后，可在 0.70 前推出范围明确的公开测试候选。**

本文只交付本地验收结果；未修改产品／测试源码，未执行模拟提交或真实存储写入，未上传候选、推送或打 tag。后续记录应更新唯一当前质量结论，不以继续增加审查文档代替修复。
