# WinPool V0.6x 简化、修复与测试版发布计划

状态：**已编制，待执行**。本文件是可接入 docs/Plan.md 的完整方案；本次仅编制计划，没有修改产品、执行 Windows 测试或发布版本。

目标：在 **V0.70 之前发布一个 V0.6x 测试版**，完成本轮已确认问题及核心结构简化。以减少重复工作、消除错误控制流、缩短用户等待为交付；不以新增报告数量、接口数量指标或假设的秒数承诺代替交付。

基线：2026-10-17，main = **d56b4b4ac2f847cfd720dead251e26baa7ff1ed7**，当前产品 V0.60。相对上一轮判断使用的 dcc526f，新增两次提交只修改 Repository Audit 报告，补充普通权限启动与采集故障诊断；这些诊断还不是产品修复。

来源代号：

- **F**：[全量检视报告：性能、冗余与架构][F]，重点为 P1–P6、A1–A5、§4、§5、§6、§7 全部阶段。
- **R**：[仓库审查报告][R]，包括后补的 §§11–12。
- **T**：[接手审查报告][T]，包括 A/B 改造方案、测试、环境问题和未验证范围。
- **G**：[AGENTS 工作规则][G]；当前用户“减少防御性编程、过度工程和代码外工作”的要求优先。
- **D**：[Development][D]、[Product][P]、[Quality][Q]：现有所有权、产品能力、版本及验收约定。历史结果只证明当时范围。

## 1. 本轮判断与固定决定

软件具备可继续收敛的基础：App、Agent、纯逻辑与持久化已有明确边界，现有构建和若干真实、模拟闭环有证据。当前主要问题是同一用例在多个层重复执行、复制和验证，加上失真的测试反馈及不断累积的文档工作。现在需要集中修复和删并，不需要重新开展一轮全面审查。[F]、[R]、[T]

上一轮发布指导只部分符合减负要求：识别了若干报告误判，却安排了过多版本阶段和发布前检查，并将模拟执行栈、真实结构编排、监控及旧协议清理推后。本计划纠正为：**两轮代码修改、一次发布收口；一个活动 Plan；一次有范围的最终验证。**

固定决定如下：

1. **首先修已定位的普通权限采集错误。** 普通读取不默认刷新 provider 缓存；缓存发现与正常枚举分开；缺少物理成员前提时不跑注定失败的 extent 回退。使用现有开关，不先造新宿主。
2. **本轮完成核心简化。** 唯一采集实现、真实执行事实复用、Agent 结构 Apply、共享模拟 reducer、UI revision 更新、轻量 IPC、单一监控样本通道及旧链删除，均有明确交付位置。
3. **校验有一个负责者。** 输入进入系统、现场真实写入、原子持久化这些边界保留必要语义；进程内已建立的不变量不在每层重新计算。
4. **新路径落地时同时撤掉被替代的生产路径。** 不保留永久双路开关、影子规则或“以防万一”的本地执行回退。
5. **常驻 PowerShell、IPC 多路分发、SQLite 深改是条件项。** 条件在本轮对应工作包中判断，未触发即不做；不能挡住已明确的删除和修复。
6. **完整覆盖指每项建议都有处置。** 不盲从报告中的不安全改法、错误计数、过强性能外推和已被新证据关闭的事项。附录是覆盖索引，不另建审查台账。

## 2. 修改完成后的职责

| 位置 | 负责的事情 | 本轮消除的重复 |
| --- | --- | --- |
| App | 页面、偏好、选择、内存草稿、共享纯规则的预览；提交类型化意图，呈现 Agent 结果 | App 的真实结构阶段接力、生产 null-agent 本地库/采集/监控路径、每条进度全量重建 |
| Application / Domain | 一份纯编辑规则、容量与约束算法、类型化结果 | 模拟预演与提交各算一套；模拟操作套用通用授权和执行器 |
| Agent | 唯一真实执行所有者、结构作业、当前事实核验、持久化、恢复、采集及监控会话 | 正常成功再次恢复对账、多个模块各采同一后态、重复提交业务入口 |
| Windows 适配 | 一个带 scope / purpose 的固定采集实现；准确目标的类型化写调用 | legacy 比较链、字符串补丁拼采集脚本、多套 reader 各起采集 |
| IPC / SQLite | 必要的跨进程契约、提交回执、状态变化、监控批次；现有事务与 journal | 同文档双传、多轮 JSON 往返、自检编码解码、无消费者的协议和存储表示 |

这里的“一份规则”不等于一个万能类或一个无类型 Read/Execute 接口。提案呈现、Agent 权威决策、Windows 适配是不同职责；合并的是重复业务逻辑和调用，不是把不同职责揉成一个框架。[R §6–7][R]

## 3. 实施节奏与依赖

### 第一轮：修当前故障，并直接拆掉热路径重复

按以下顺序推进，可独立的文件工作并行：

1. 启用本计划的减负工作方式（W7），随首个代码包修必要 fixture 和诊断隔离（W6）；不等新一轮全量测试才开始写代码。
2. W1 修普通权限 discovery / 枚举 / extent 控制流；W4 修独立的 5 秒连接超时呈现。
3. W2 合并同一步重复采集、复用成功后态、停止无进展恢复轮询。
4. W3 做一次 revision 一次预演和同状态短路；W4 先减少响应正文与状态查询负载。
5. 随各条路径替换完成 W5 删除，不把“最后再删”变成永久债务。

这一轮的结果是日常启动、刷新、编辑与真实单步流程已有实际改善，且相关测试能提供可信反馈。它不是单独的版本审批门。

### 第二轮：完成所有权合并，撤掉旧实现

1. W1 收成一份采集模板和 scope 实现，合并同代读取；在此基础上决定是否需要常驻只读 worker。
2. W2 将真实结构 Apply 收入 Agent，并合并 MAX attempt runner。
3. W3 由同一 reducer 支持 App 预览和 Agent 原子提交，删除旧模拟执行栈及全文提交入口。
4. W4 完成监控批次、轻量状态与重连同步，撤掉旧轮询；完成目录按需加载、稳定机器身份缓存和生命周期收口。
5. W5 清完退役接口、工厂、别名和重复校验；W7 清理受影响的当前文档重复。

依赖：W2、W3 确定的结果和 revision 契约供 W4 使用；真实结构作业消费 W1 的统一事实；W5 与这些修改同步。共享契约和同一文件不能交给多个执行者同时修改。App 与 Agent 同包升级，不维持新旧生产协议并行。

### 发布收口：一次集中验证，生成便携测试包

代码稳定后完成 W6 最终验证和 W8 包验收。发现具体缺陷只修相关路径；只有修改影响全局或现有发布门要求时才重跑全量。

不为八个工作包分配八个版本。版本仍只由 Directory.Build.props 管理，同一迭代可以有多次提交；完成可交付增量才增号，尽早发布 V0.6x。V0.68、V0.69 留给范围收紧后的修复与发布收口，不创建 V0.610，也不为了用完版本号延迟首测。[G]、[D]

## 4. 八个工作包

工作包用于明确责任和覆盖，不是八份计划、八份报告或八次全量验收。

### W1 — 普通权限采集、唯一读取实现

**先做已证实的故障修复。**

R §12 中，同机、同嵌入脚本，Medium 默认路径 55.204 秒、7 条来源/关联失败；使用现有 ForPurpose(Storage, discoverProviderCache:false) 后为 3.389 秒、0 失败。原路径把 Update-StorageProviderCache 和 Get-PhysicalDisk 放在同一个失败域，导致可成功的枚举被跳过；随后 GetPhysicalExtent 耗时 49.145 秒，最后仍依赖先前的空物理盘集合。这是候选参数的诊断结果，尚未部署，不能写成已经加速。[R §12][R]

实施内容：

- 普通 inventory、monitor 身份读取使用现有无 discovery 路径。确实需要重新发现的受控变更阶段显式调用 discovery；发现失败不吞掉独立枚举结果。
- 物理成员集合缺失时直接返回准确原因，删除依赖同一缺失前提的 extent fallback。仍有独立用途的 extent 查询按准确目标保留，不做全文件机械删除。
- 统一固定内嵌脚本、封闭 scope / purpose 参数和关系索引；移除解压后按 anchor 补丁、多个 scoped 字符串替换副本及 legacy 比较生产入口。
- 所有真实拓扑、Manage、Storage、Hardware、monitor 身份读取使用同一生产采集实现。full 是范围，不是第二套脚本；缓存读取与触发采集仍可有不同的类型化请求。
- 合并同 generation、同/可兼容 scope 的并发读取；完整事实已覆盖 Storage / monitor 时直接投影。只有缺少所需事实才补采，不为复用增加全业务大锁。
- 能力、范围和准确成员关系尽量来自同一份当前事实；查询只取本次决策相关字段。稳定机器身份单独按 Agent 生命周期缓存，数据根切换或重绑定失效，实时磁盘身份不这样缓存。

**常驻宿主的决定：** 上述修改后，用现有计时在一个代表性固定脚本上比较残余冷启动成本。只有冷启动仍是主要剩余开销且复用收益明确，才在本轮改为一个兼容 Windows PowerShell 5.1 的受控常驻只读进程。采用就替换旧读取 runner，不加 worker 池、双宿主 fallback 或任意命令通道。读写可以共用传输组件，但写入发出后的等待/退出语义单独保留，不能因只读超时策略自动杀写进程或重发写命令。不默认将 PowerShell 5.1 直接嵌入当前 .NET 进程。

**完成条件：** discovery 失败仍能枚举；缺前提不进入无效回退；普通权限产品刷新完成且报告事实完整性；写后需要新发现时仍得到 fresh 后态；生产只剩一个采集实现。直接回归覆盖这几个分支，不建立通用故障注入平台。

主要位置：EmbeddedStorageInventoryScript、EmbeddedScopedStorageInventoryScript、InventoryPipeline、WindowsHardwareInventoryProvider、WindowsRealStorageTopology、AgentInventoryCoordinator、AgentLocalSystemIdentity。

### W2 — 真实执行、结构 Apply、MAX 与恢复

- 非 MAX、无外部变化、无重试的单次写操作收成 **Prepare 一次、临写一次、写后一次，按 OperationId 计最多 3 次 provider inventory**。Accept 检查会话和冻结授权，不另开一次重复 inventory；step preflight 与 execute-before 共用一个类型化上下文。
- 写后返回已验证的观察结果，供正常终结、下一步所需事实、UI 和 monitor 使用。观察范围不足或外部状态变化才再读；正常成功不再进入恢复 Reconcile，不追加多份“兜底刷新”。
- 保留 Agent 对真实计划的权威。App 交意图，Agent 用当前事实确定准确目标、能力、闭包和执行计划。删除同进程状态迁移中的全量 IsValid、重复 PlanHash、JSON 字符串比较；输入边界及持久化读回边界验证一次。
- 真实结构一次 Apply 对应一个 Agent 作业，内部完成删建、建层、VD、初始化与自动布局等已有依赖；新对象按真实返回关系继续解析。删除 App 读取全文后再启动下一段的结构状态机。
- 普通、单成员、多成员 MAX 共用 attempt runner、拒绝解释和已验证后态。候选调度保持用户确认的整数 GiB 搜索算法；不恢复 UseMaximumSize、固定扣 2/4 GiB 或擅改二分。
- Query / List 只读，不因每次查询自动再启动 reconciliation。恢复由启动恢复或明确的用户重查触发；自动过程无进展或到现有有界期限就结束，并停止后台轮询。结果仍未知时保留 Unknown 和写屏障，允许显式只读重查，不重放已发出的写调用。
- 写后立即查一次；provider 尚未收敛时允许有界、准确目标的只读观察，必要时才升级范围。复用现有期限并核实完整等待路径，删除无效固定间隔和不必要的双观察；不强制“所有写后只能读一次”，也不承诺把所有原生写入压到同一秒数。

**完成条件：** 正常单写满足 3 次预算；真实结构不再由 App 接力；MAX 按候选统计且算法不变；无进展的 Unknown 不在后台持续采集、写日志或占用通道；CallIssued 耐久记录、准确目标、受保护角色和 Unknown 不重放仍成立。用现有 fixture 验证必要边界，实机验证仅覆盖本次改变的动作家族。

主要位置：AgentRealOperationService、WindowsRealStorageBackend 及 MemberSets / MaximumCapacity、WindowsRealOperationPlanner、RealStructureEditorFlow、WorkspaceRealOperations、MonitoringSessionEdits。

### W3 — 模拟与 UI 的唯一规则

- 草稿构建步骤、补充容量/布局意图之后，只做一次完整 Preview。EnrichIntent 和 AppendPartition 可能改变最终意图，不能直接删掉后一次校验而仍使用旧预演。
- 一份草稿 revision 包含文档、用户输入、MAX、自动布局、撤销等实际影响因素；只应用最新 revision 的结果，Apply 消费已完成的最新预演。
- 同一 Application 纯 reducer 供 App 预览和 Agent 提交。合并 Evaluate / ApplyEvaluate、SourceFacts 构建和拓扑投影；删除 SimulationEditCoordinator 中通用 planner、一次性 authority、executor gate 等仅转发的过渡执行栈。
- App 持有未提交草稿；Agent 使用 documentId、expectedRevision、typed intent、commitId 调用同一 reducer 后 CAS 原子提交，返回 receipt 和必要变化。重复 commitId 返回同一结果。保存失败保留草稿并明确未提交；不改成 App 直接写 SQLite，也不把异步保存失败当已提交。
- 同一状态/进度通知先短路；Busy、操作状态、文档 revision 分开。只有文档实际改变才重建拓扑；选择变化只更新选择相关呈现，不重投影整个 catalog。
- 合并 commit / reset / setter 的重复刷新；页面确需拆分时按纯编辑逻辑与 UI 呈现拆，不追求每文件 600 行。

**完成条件：** 一次有效草稿 revision 一次完整预演；100 次相同进度通知新增拓扑重建为 0；合法/非法意图结果一致；预览、最终提交和撤销没有第二套规则；正常模拟提交不传整份执行计划或回显已有全文；CAS 和提交丢响应后的结果识别成立。

主要位置：SimulationDraftPlanner、SimulationEditCoordinator、StorageStructurePage、DiskPartitionPage、EditorPageBase、WorkspaceViewModel、StorageSystemRepository 及 Agent commit handler。

### W4 — IPC、监控、启动和生命周期

- 模拟成功先取消全文回显，最终随 W3 改成 intent / receipt；inventory 同一 revision 只由一个消息承载全文或变化，另一路只回 receipt。
- 真实操作状态只传 operationId、revision、state 和必要步骤变化；计划准备时取一次，详细 evidence 按需有界读取。状态查询不为一个状态码加载整段历史。
- 复用现有事件机制通知终态和状态变化；初连/重连做一次必要同步。去掉 250 ms × 1200 的常态查询及 App 无限恢复轮询。
- 监控由 Agent 有序发送 sample batch / sequence，App 共用一个 monitor cache；初连/重连快照一次，删除 MonitoringService 全快照轮询和 MonitorAlertObserver 独立轮询。
- 样本合并成有界批次，不逐盘逐点挤进 1024 事件队列；操作终态不被样本挤掉。采样、传输、绘图频率分开，图表读有界窗口及变化 revision；不靠将 20 Hz 降为 1 Hz 来“优化”或掩盖丢样。
- 没有独立 OS counter 的可靠池成员，显示准确角色结果；不报成身份丢失，不借用 VD 的 Disk 3 计数器。
- 启动先加载 metadata 与所选文档，其余正文按需加载。合并重复机器绑定/文档解码；导航持久化只保留 LastActivePage，workspace 保存系统/对象选择，移除旧 activePage 枚举转换。
- 将独立的 5 秒 Connect TimeoutException 在现有连接/UI 异步任务边界处理一次，明确未连接或未保存结果；不再逃入 XamlUnhandled，不叠加多层 catch / 重试。启动、恢复、提权、退出沿用一个 lifecycle，等待可取消，失败不无限延长期限，退出后不迟到发布 Ready。
- 保留现有 stream 请求/响应配对锁。长任务先回接受回执、状态用事件；如果仍有可复现的队头阻塞，才在唯一连接内实施 correlationId 分发和有界并发。不为“并发能力”额外造第二连接实现。
- 导入文件、模拟 payload、IPC frame 用一套可解释的预算，尽早校验。消除进程内重复 encode/decode 和为比较而序列化整个 SourceFacts；保留真实跨进程/持久化的必要编解码。

**完成条件：** 稳态监控无完整 GetAgentSnapshot 轮询；一个批次源与一个 cache；同 inventory revision 不双传/双应用；操作终态可靠可恢复；窗口开出后普通权限工作区和监控能得出明确结果；连接超时只呈现一次；导航、目录与偏好无新旧来源冲突。

主要位置：NamedPipeAgentConnection、AgentControlServer、IpcFrameCodec、AgentInventoryCoordinator、MonitoringService、MonitorAlertObserver、MonitorPage、MainWindow、AgentBackedWorkspaceStateService。

### W5 — 删除旧链、无用抽象和重复持久化

随 W1–W4 边改边删；删除候选的逐项清单见附录 B。

- 无生产调用的请求连同 codec、client、handler 和只证明其存在的测试一起撤掉；空项目退出 solution / project 引用。有独立行为价值的测试移到实际实现项目。
- 用实际构造、引用和组成关系确认可达性；接口仅有一个实现不自动等于应删。保留有效的替身边界与多实现边界，删除无语义转发接口。不以“80 → 15”或“源码 → 55,000 行”作指标。
- 同一 PID 的存活、化身和完整性核验共用一次必要系统查询，但保留三种判断各自语义；不把握手通过当成进程永远不变。
- 删除每层重复校验、重复日志后再抛出、无入口 setter、只换名的别名、MainWindow 的 null-agent 生产回退；已知可恢复失败由所属边界负责一次。
- SQLite 重复 PRAGMA / 连接设置及 JSON+关系表双表示，在消费路径收口后处理。无消费者的表示直接撤掉；仍有消费者但没有剩余性能证据的部分保持现状。不另做数据库框架、全库迁移或审计平台。
- 整个退役文件按现有 G 规则集中移出生产树至 Old；被替代文档按 W7 归档。删除代码成员和项目引用不需要逐项审批，也不为每个文件生成证明报告。默认搜索 src、tests 和当前相关文档，Old / Archive 不进入日常检索。

**完成条件：** 替代的旧生产链无调用者、无组成入口、无项目依赖；没有仅为保留旧架构而存在的测试；接口去留有实际用途，异常只有一个合适的呈现位置。重构完成度看旧责任是否消失，不按纯行数打分。

### W6 — 恢复可信的测试反馈，控制验证量

已知结果不能混成一个“产品 bug 数”：F 为 2418/2455 通过、34 失败、3 skip；R 首轮相同，停止 App/Agent 后两条 guard 通过，修正显式 admission 运行口径后的有效汇总为 2420 通过、32 失败、3 skip，**不是重新全量跑过**；T 为不同运行条件下的混合分项目结果 2406 通过、46 失败、3 skip。30 个 fixture 和 2 个文本断言是共同的确定失败；T 的额外超时、归档和轮转失败不能直接归为同一种问题。[F §5][F]、[R §3][R]、[T]

实施内容：

- 给真实后端恢复 fixture 显式注入一致的能力结果，合成测试不回落本机 WMI；不靠放宽生产能力校验换绿。
- 删除过时 XAML 子串及文档措辞/文件数断言；真正代码边界继续测，已退役代码的镜像测试跟随撤掉。
- 使用现有 EnvironmentEnabledFact 类方式显式 skip 实际未运行的硬件/平台用例；默认合成测试与主机只读测试分类，真实硬件用例由本轮相关验证显式启用。
- 测试诊断写隔离目录或注入现有 sink，不能流入用户 Diagnostics。检查直接相关的默认数据根使用，不为此发起全仓数据审计。
- 构建 guard 先在标准实例停止的正确条件下验证；仍存在错误根/全局进程状态耦合时才修准确运行树和数据根定位，不取消真正的写屏障保护。
- 归档断言等待既有完成任务/记账状态，保留有界超时；遇 7za 拒绝访问先核实该实际进程和输出根，不预设归档状态机错误。轮转失败做对应复验，不靠加长所有 timeout 处理。
- 原快捷方式 Medium 冷启动与 endpoint 可写已有新证据，不重做整棵目录 ACL 调查。修复脚本若仍假定固定研究目录布局或用 exit 0 隐藏失败项，修准确项目根及结果报告；不再递归触碰无关历史树。
- 为这次会复发的具体重复路径，在已有 fixture 中补少量行为预算：正常单写 3 次、有效草稿 1 次预演、相同进度 0 次重建、无进展恢复不自动再采。不得要求全操作只起 1 个进程或整个 MAX 只读 2 次。
- 本轮工作稳定后串行跑一次完整相关工程门；真实硬件、UI 与自动化计时不并行干扰。此处“完整”按剩余实际测试项目计，删除死代码后测试总数可以下降。

**日常验证规则：** 文案/低风险布局通常编译即可；行为改动做对应回归；采集/执行/持久化改变补对应边界和设备证据。不为每个 wrapper 写测试、不新增 UI 自动化框架、不为每项小改全量跑一遍。失败后只补具体缺口，后续改动未影响的通过证据继续有效。

### W7 — 文档和开发方式减负

本工作包从第一条代码任务起生效，而非最后写报告。

- 唯一活动 Plan 就是本计划；落入仓库时将当前已完成的 Plan 按现有规则归档一次。日常只更新当前工作包状态，不为每个修复写新计划或交接文档。
- 主执行者按需读 AGENTS、当前 Plan 的相关小节和相应代码；三份历史审查与附录不是每个任务的前置全文阅读材料。
- Quality 只保留命令、验收口径、最新短状态及证据链接。重复的逐日完成记录删除；有独立价值且未保存的历史集中归档一次，不再抄进另一份长 CHANGELOG。
- Development 保留所有权、当前必要约束和构建方式；产品行为放 Product，详细尺寸/文案仅在真正负责它的位置维护。删重复规则和历史转述，不新建多层 DesignTables 分类工程。
- AGENTS 精简重复说明、过时实现细节和长篇时效性模型示例；保留用户决定、授权、文件处置和 Git 边界的一份准确表述。任务中不新增通用“预防今后遗漏”条款。
- README 中英只讲当前使用方法和能力，取消重复 DIP 数字以及不可操作的 AutoVD / AutoPart 承诺。CHANGELOG 每个可交付变化写一条短结果，代码无相关产品变化时不强制更新多份文档。
- 纠正“已完成 Plan 仍正在实施”与旧导航/偏好描述。历史测量保留原数值和当时结论，在当前说明中标注已被后续工作取代，不重写历史证明整改正确。
- R 报告迁移后的源码/artifact 相对路径已修好，不重新立项。R §8 另列的现行文档指向用户删除的四份报告的链接，随受影响当前文档修正引用或移除无效引文；不恢复用户删除文件，不全仓改造 Archive。

**协作与交付：**

1. 主执行者负责范围、共享契约和集成；只把可独立完成的代码包交给其他 Agent。小任务直接做，不为用满并发拆任务。
2. 每份委派在消息中给目标、文件所有权、固定决定与相关验证即可，不另写任务说明文件。同文件串行，共享构建与 Windows 桌面只有一个操作方。
3. 实现、删旧链、相关验证一起交付；只反馈改了什么、删了什么、验证结果和实际剩余阻塞。主执行者不重跑已有效验证的检查。
4. 已授权范围连续推进；局部命名与实现不找人类审批。遇到真正新的产品决定/目标授权问题时只提出该问题，继续独立工作。
5. 按 G 直接在 main 做任务范围内的本地提交，不新增分支/PR 流程。推送、tag 和 Release 按用户实际发布指令执行，计划编制不冒充发布授权。
6. 日常闭环为：**改代码并删旧实现 → 相关验证 → 只更新变化事实的归属位置 → 提交。**

**完成条件：** 小改没有新增计划/长报告，没有全面重读文档和重复全量门；当前事实只维护一处；本轮只有这个 Plan 的状态与一份简短最终交付记录。

### W8 — 测试包和一次发布收口

**首测内容：** 现有单一 App + Agent 产品的便携 V0.6x 测试包，包含本轮修复与简化、现有模拟/真实编辑/监控能力及明确已知限制。签名 MSIX、商店发布、新功能家族、V2 测试工作区不进入本轮。

从最终标准 Release 运行树生成候选包，不混入旧 Agent / DLL / 协议版本。使用现有构建/打包脚本；确需补自动化时只补一个可复用入口，不先搭多环境 CI/CD。

最终验证只覆盖以下集合：

| 范围 | 要证明的结果 | 最小方式 |
| --- | --- | --- |
| 工程与反馈 | 构建成功；确定失败已处理；skip 与剩余失败口径清楚；测试不写用户诊断 | 稳定后一次现有完整工程门，直接引用已有输出 |
| 普通用户启动 | 从实际候选包普通权限启动，workspace / Storage / monitor 恢复得到明确结果，无新 unhandled | 一次冷启动并做一次刷新；不重做历史 ACL 专项调查 |
| 真实执行改动 | 单写预算、准确目标/能力、写后状态、结构 Apply、MAX 共用 runner 的实际路径正常 | 在既有明确授权的 WDC 及独占对象上做对应代表闭环；变更涉及建池/格式化/MAX 时补对应动作，未触及动作不全套重演 |
| 模拟与 UI | 创建/修改/撤销/提交；一次预演；同状态不重建；保存失败不假成功 | 相关行为回归加一组真实 GUI 场景；包含一个能覆盖原重复路径的较大文档，不做独立压力研究 |
| 监控和生命周期 | 采样、批次、持久化、重连/退出、终态及编辑缺口表示正确 | 一段跨重连和编辑的监控运行；顺便核实一次普通归档，记录实际计数，不要求 1 GiB 压测 |
| 升级与数据 | 现有已支持的 schema 迁移能走；拒绝的不支持格式不覆盖原件；模拟导入导出及数据根切换不走旧入口 | 复用已有格式 fixture；从最后公开包的代表性数据副本试升级/导入，检查实际包版本及错误结果 |
| 发布物 | 下载/解压结构完整、App/Agent 匹配、声明范围准确、用户能反馈问题 | 生成候选 zip、校验值、简短 Release notes 与已知问题列表 |

真实设备仍按已有准确目标授权和产品受控路径执行；WDC 的既有授权不因新 Plan 重问，两块 Samsung 不成为写目标。现有末态要求按实际执行任务沿用，不凭报告里的旧盘号写盘。[G]

没有现成环境的 Windows 11、多成员、SSD/SCM、Mirror/Parity 组合明确标为未完成实机覆盖；首测不以购置设备或完成所有平台组合为前置。若发布说明仍声称某项已全面验证，就必须提供对应证据。高 DPI/高对比度全面矩阵、8–24 小时耐久、1 GiB 归档专项留在后续质量工作，不阻塞这个范围清楚的测试包。

不预先承诺“单步 1.5 秒”“MAX 10 秒”。先确认重复调用/传输已消失，再用同硬件、同文档、同监控状态下的实际墙钟比较。现有计时只补缺少的写调用阶段和必要计数即可；不建立新的可观测性平台。

**发布条件：** W1–W5 的本轮确定项完成；W6/W7 使日常反馈和工作量正常；上表涉及本次改动的路径有有效结果；不存在已知会导致普通启动不可用、错误写目标、重复写入、提交丢失或主要编辑链不可用的未处理问题。条件项未触发不算欠账。测试版说明坦陈剩余环境覆盖，不用“测试版”掩盖已定位的主路径故障。

## 5. 执行状态与停止条件

开始执行时，在下表更新一行即可；不另开 issue 表、电子表格或每日状态报告。

| 包 | 初始状态 | 何时关闭 |
| --- | --- | --- |
| W1 采集 | 未开始 | 普通权限故障修复、唯一采集落地、worker 条件有明确结果 |
| W2 真实执行 | 未开始 | 单写 3 次、结构作业、MAX 与恢复收口 |
| W3 模拟/UI | 未开始 | 共享 reducer、一次预演、原子提交、旧执行栈退出 |
| W4 IPC/监控/启动 | 未开始 | 轻量状态、单一批次、按需加载、生命周期缺陷关闭 |
| W5 代码删并 | 未开始 | 附录 B 全部候选有删除/保留结论，旧生产链退出 |
| W6 测试/环境 | 未开始 | 已知失真处理、相关回归及一次最终工程门完成 |
| W7 文档/工作方式 | 未开始 | 唯一事实位置与小闭环实际生效 |
| W8 测试包 | 未开始 | 候选包、对应验收与发布说明可交付 |

允许状态仅为未开始、实施中、已实现待验证、已验证、受阻；“已编制”不是“已执行”。每包只需附提交/现有验证输出链接和真实阻塞。

达到上述完成条件即收口，不继续做可有可无的第四轮审查、性能平台、日志完善或全矩阵复验。工期由首轮实际完成量校准，不把未经实施的“当天完成”“几秒完成”写成承诺。进度落后时先取消未触发的条件改造和新功能；保留已定位故障修复及真正消除重复实现的核心交付。

---

# 附录 A：三份审查的逐项处置

本附录供首次对齐及查漏使用，不要求每个开发任务重读。采纳/修正采纳＝本轮确定工作；条件＝在所属工作包判断；不采纳＝给定方法不实施；已关闭＝沿用新证据。

## A1. F 报告 §7 全部修改项

| 原项 | 处置 | 对应包与具体决定 |
| --- | --- | --- |
| 0.1 对账终止 | 修正采纳 | W2 停自动无进展重试，Query/List 无副作用；拒绝 N 次失败后自动释放 Unknown 屏障 |
| 0.2 App 无限恢复轮询 | 采纳 | W2/W4 删除常态无限轮询，显式只读重查 |
| 0.3 execute-before 重采 | 修正采纳 | W2 合并临写上下文；不以“绝不可能变化”取消现场核验 |
| 0.4 Accept 重复采集/校验 | 修正采纳 | W2 Accept 授权与入队，当前事实由临写门负责；用户确认前的旧事实不能替代 |
| 0.5 写后验证/退避 | 修正采纳 | W2 首次立即观察，必要时有界轻量查询；不承诺所有最坏情况 <10 秒 |
| 0.6 静默 skip | 采纳 | W6 复用现有显式 skip 属性；实际硬件测试单独启用 |
| 0.7 两个 XAML 断言/文档断言 | 采纳 | W6 删除脆弱断言，保留行为/真实依赖边界 |
| 0.8 30 项 fixture | 采纳 | W6 注入一致能力结果，不放宽生产约束 |
| 0.9 构建 guard | 修正采纳 | W6 已证实例停止后可过；仅修实际根/进程耦合，不先重造守卫框架 |
| 0.10 归档时序 | 采纳 | W6 等待现有完成信号/记账，有界，不统一加 timeout |
| 0.11 App 构建成本 | 保留已有证据 | W7 不另测、不据此先拆程序集；不将嵌套阶段相加当总时间 |
| 阶段 1 常驻宿主 | 条件实施 | W1 先修控制流与次数，再判断单一只读 worker；拒绝“唯一必须改动”与 12.7 倍直接外推 |
| 2.1 Inventory 项目/测试 | 采纳 | W5 退出空项目；有独立价值的 comparer 测试移到实现所在项目 |
| 2.2 native/legacy provider 壳 | 采纳 | W1/W5 删除旧链，保留仍被使用的纯投影函数 |
| 2.3 Capture/LoadManage 旧 RPC | 修正采纳 | W1/W5 基于实际调用收成唯一采集用例，所需缓存读取保留明确用途 |
| 2.4 LocalMachineRecord/17 接口 | 修正采纳 | W5 按真实引用删除；“17 个全无实现”不成立 |
| 2.5 四条拓扑路径/重复重算 | 采纳 | W1/W2 一个 scope 实现，删除已建立不变量的重复重算 |
| 2.6 inventory 双传 | 采纳 | W4 一个消息带正文，另一个带 revision receipt |
| 2.7 monitor sample event | 修正采纳 | W4 现为无生产者/消费者的空契约；落实唯一批次通道后删旧契约，不把它当已实现推送 |
| 2.8 快照/告警轮询 | 采纳 | W4 稳态事件/样本批次，初连重连一次同步 |
| 2.9 三套进程核验 | 修正采纳 | W5 共享查询，保留存活、化身、完整性不同语义 |
| 阶段 3 窄查询 | 修正采纳 | W1/W2 准确目标闭包及相关字段，不仅按盘号；2 分钟 fresh 不等于期间不能变 |
| 4.1 接口 80→15 | 方法不采纳，删并采纳 | W5 保留真实抽象边界，拒绝数量配额 |
| 4.2 冻结/哈希/校验 | 修正采纳 | W2 输入与耐久读回边界校验，进程内复用；现场写前核验保留 |
| 4.3 topology 全字段要求 | 采纳 | W1/W2 只强制本次决策必需字段，准确关系/保护角色/加密语义保留 |
| 4.4 页面 ≤600 行 | 方法不采纳，职责拆分采纳 | W3/W5 先删重复，再分纯规则和呈现 |
| 4.5 catch 清理 | 修正采纳 | W5 边界处理一次，删重复日志/吞异常；不按 catch 数量删 |
| 5.1 Quality 瘦身 | 采纳 | W7 口径、命令、最新状态；历史单处归档，不机械转抄 |
| 5.2 Development 瘦身 | 修正采纳 | W7 当前所有权与构建，细节归属现有文档；不新增文档分类工程 |
| 5.3 Archive 全搬 Old | 不采纳 | W7 保留当前归档约定，默认检索排除历史即可 |
| 5.4 文档断言 | 采纳并去重 | 与 0.7 同一 W6 动作，不重复设任务 |
| 5.5 ≤1 进程/≤2 读取预算 | 修正采纳 | W6 正常非 MAX 单写 3 次 inventory；MAX 按候选，必要状态观察另列 |
| 5.6 改写旧延迟报告结论 | 不改历史，更新当前判断 | W7 保留旧报告；当前 Plan 写明冗余及本次结果 |
| §8 必须保留的检查 | 修正归并 | W2 保留准确身份、闭包/角色/加密、写后结果、CallIssued、Unknown 不重放；W1 固定脚本；W4/W5 当前用户握手及进程语义 |
| 预期时延/55,000 行/60,000 字符 | 不作承诺或硬门 | W8 只报告实际结果；W5/W7 以职责和维护位置消失判断简化 |

## A2. F 的发现章节及 R/T 非重复内容

| 来源 | 发现或建议 | 处置与归属 |
| --- | --- | --- |
| F P1–P2、A1–A2 | 冷启动、多轮 capture、全量事实与局部证明混用 | W1/W2；新 R §12 控制流故障优先；冷启动不是已经证明的唯一根因 |
| F P3–P4、A3 | 无界恢复、延迟和空闲重连 | W2/W4；无进展自动过程停止；25 秒空闲策略/重复 reseed 依实际连接生命周期收口 |
| F P5–P6 | 通道竞争、SQLite 请求成本、重复 JSON/正文 | W4 确定减量；W5 SQLite 深改有条件 |
| F A4–A5、§6 | 逐层证明、文档被测试锁定、制度成本 | W2/W5/W6/W7；删除同事实重复维护 |
| F §4.1 | 三类监控服务、两种 PS runner、多入口 | W1/W4/W5；采样/持久化/UI 不同职责可保留，重复请求和所有者撤掉 |
| F §5.1–5.2 | 构建正常、测试红、UI 自动化不足 | W6/W8；保留已有构建证据，不以缺 UI 框架为新专项 |
| F §5.2.1 | 默认测试触主机、污染诊断和证据膨胀 | W6 分类与隔离；不清理历史证据换“体积改善” |
| R §3 | 32 个有效剩余失败及 guard 条件 | W6；修共同根因，最终新结果单独记录 |
| R §4 | 六条真实操作 7–8 次采集；历史 MAX 247.687 秒 | W2；不重跑旧基线，不将工具等待/嵌套计时当写调用耗时 |
| R §4.2、§6.3 | 模拟四闭环可用，但重复预演/投影及大文档风险 | W3；选一个覆盖该重复路径的较大文档验证，不先做 CPU profile 专项 |
| R §5、§6.7 | 可靠池成员无独立 counter、编辑期间采样缺口 | W4 准确角色与缺口呈现；不冒称全程无缺口 |
| R §5 | workspace activePage 旧枚举丢语义 | W4 用 LastActivePage 唯一来源；不是已经证实启动跳错页 |
| R §5 | 目录恢复暂缺、启动提示人工等待 | W4/W8 确认新包普通启动结果；65 秒人工处理不当性能基线 |
| R §6.1 | exact-member 正常执行 7–8→3 | W2 确定交付 |
| R §6.2 | 真实结构 App 接力 | W2 一个 Agent 作业；本轮完成 |
| R §6.3–6.4 | 一次 revision 反复预演/刷新 | W3 确定交付 |
| R §6.5 | IPC 串行被长请求占用 | W4 先轻量接受/事件；多路分发条件实施，不裸删锁 |
| R §6.6 | 全文提交、双传、状态重数据、串行全加载、预算不一致 | W3/W4 全覆盖；Agent intent/CAS 与 metadata 按需加载 |
| R §6.7 | 快照+告警双轮询、空 sample event、队列约束 | W4 一个批次路径，保留真实采样和终态交付 |
| R §6.8 | 采集模板、同代事实、机器身份、MAX、超时、SQLite | W1/W2/W4/W5；避免全局大锁，分离读/写生命周期 |
| R §7 功能入口表 | 导入导出、设置、提权/退出、Shell、测试页 | W3/W4/W5 保留各用途唯一所有者；不把菜单多入口等同重复实现，不为测试占位页加框架 |
| R §7 删除候选 | 12 组候选 | 附录 B，W5 随所属包一起完成 |
| R §8 | README 尺寸/偏好、假活动计划、重复结果、断链 | W7；报告自身路径已修与用户删文档留下的引用分开处理 |
| R §9 | A/B 顺序及量化条件 | 本计划两轮代码加一次收口；W1 最新根因提前 |
| R §10 | 交接索引/ignored evidence | 沿用索引；不要求新 Agent 再生成同类报告或提交原始硬件材料 |
| R §11 | 原入口 Medium 冷启动、endpoint 权限 | 该点已关闭，沿用证据；W8 验新修改后的候选包，不重复 ACL 专项 |
| R §12.1–12.2 | discovery 失败跳过枚举、49 秒无效回退 | W1 首项产品修复；55.204→3.389 秒只是诊断对照，尚非产品结果 |
| R §12 末段 | 5 秒连接 TimeoutException 未处理 | W4 所属边界处理一次，独立于 60 秒采集 timeout |
| T A | App 内存模拟、删通用执行栈 | W3 修正采纳；保留 Agent 持久化权威、CAS/commitId，拒绝异步保存成功语义变化 |
| T B | 一个 runspace、单一 Read(scope) | W1 唯一采集确定做，runspace 条件做；拒绝默认 PS5.1 进程内直嵌 |
| T B | 真实读—写—读，删 App query | W2/W4；保留 Prepare 与临写区分及必要有界后态观察 |
| T B | App 冻结最终命令，Agent 不再计划 | 不采纳该权威转移；W2 共享纯规则并保留 Agent 最终决策 |
| T B | 能力合入同次读取 | W1/W2 采纳；保留准确子系统能力的必要字段，不用 fixture 问题否认生产语义 |
| T 功能表 | 一个采集/模拟/真实/能力/终态/monitor owner | W1–W4；不同类型用途仍有清楚契约 |
| T 文档/测试 | 新契约、日志隔离、保留历史 | W6/W7；不把“一个计划器/进程”换成新字符串配额测试 |
| T 完整性/测试 | Low、Medium 输出、7za、仓库根、46 失败 | R 新证据关闭原入口问题；W6 仅处理仍存在的归档/轮转和脚本定位问题，不永久保留临时输出覆盖 |
| T 未验证范围 | 刷新未完成、模拟/真实未做等 | 用 R 已做且未受新改动影响的证据补覆盖；W8 仅验本次实际改变的路径 |

## A3. 必须纠正的证据口径

| 原说法/风险 | 本计划采用的口径 |
| --- | --- |
| 1,947 次失败采集和 210 个 ID 均为生产恢复失控 | 源码支持无界调度风险，但该日志窗口有测试污染迹象，Query 也不会创建新操作 ID；不据此宣称生产频率或创建数 |
| 17 个接口全部零实现 | 已见 DefaultOperationPlanner / SimulationEditCoordinator / AgentBackedStorageSystemRepository 等实现；删除以实际引用为准 |
| 2 分钟新鲜度意味着两次读取之间不可能改变 | 新鲜度是允许使用条件之一，不证明外部状态不变；确认等待后仍有临写门 |
| postcondition 必定跑 30 轮、固定 66–115 秒 | 现有循环可即时成功，有期限；实际 capture 本身及期限检查位置影响上界，按完整调用路径测 |
| 12.7 倍原始查询差距等于完整生产脚本稳定加速 | 查询组合、脚本完整性和冷/热状态不同，只证明复用值得评估，不能外推最终秒数 |
| 嵌套 timing 的 80.4% 是端到端墙钟占比 | F 的聚合阶段有嵌套；使用 R 实际 provider 最内层计时和真实墙钟，不重复求和 |
| sample event 已生成、发送、解析后丢弃 | 当前只有空契约，无生产者/消费者；W4 要实现唯一批次路径 |
| 34/32/46 是三个不同产品 bug 总数 | 测试环境、聚合及重新运行范围不同；按根因修复，最终结果重新记录，不覆盖历史 |
| 7za 失败都由 Low 引起 | 有可能，但旧报告未测该实际子进程 token；按实际失败证据处理 |
| 修复脚本 exit 0 表示全部 ACL 处理成功 | R §11 记录 204,692 成功、1,005 失败；只认已核实关键路径状态，不宣称整树全成功 |
| “目录 Low 未解决”仍是当前普遍阻塞 | R §11 已验证标准 App/Agent Medium 冷启动及 endpoint 可写；当前首要故障是独立的采集控制流 |
| 旧真实 MAX/全部硬件矩阵本轮都已验证 | 历史 MAX 与 R 六次属性/状态操作范围不同；W8 按本次变更补必要验证并明确未覆盖环境 |

---

# 附录 B：删除与合并候选的执行清单

本表由对应包在修改时关闭，不安排一次独立全仓调查。整文件退役按 G 归档，成员/引用退出生产代码；实际仍有独立消费者的项记录一句保留理由即可。

| 候选 | 动作/保留路径 | 所属包 |
| --- | --- | --- |
| WinPool.Inventory / InventoryComparer 项目壳 | 退出空项目；仍有意义的 comparer 实现/测试归 Application 实际位置 | W1/W5 |
| WinPool.Inventory.Tests | 移有价值行为测试，删占位引用和项目壳 | W5/W6 |
| NativeWindowsInventoryProvider | 删除旧比较链组成，生产统一 reader | W1/W5 |
| EmbeddedPowerShellInventoryProvider 类壳 | 删除 legacy 壳；Project 纯函数有消费者则移到实际使用模块 | W1/W5 |
| CaptureAgentInventoryRequest / comparison handler | 退出请求、codec、client、handler 及仅锁旧协议的测试 | W1/W5 |
| LoadAgentManageInventoryRequest / LoadManageInventory | 所需缓存读取并入明确现行入口，无消费者链退出 | W1/W5 |
| LocalMachineRecordService | 删除无实例化服务，机器记录留现行 Agent-backed 用例 | W1/W5 |
| IStorageInventoryProvider / WindowsStorageInventoryProvider | 无生产消费的包装退出，沿用实际 hardware / Agent reader | W1/W5 |
| 各拓扑 reader / scoped script 副本 | 一个模板与 scope 实现，共同关系索引 | W1 |
| 两种 PowerShell runner 的重复传输代码 | 按实际读/写语义共用组件；worker 条件触发则替换旧读取生命周期 | W1 |
| OpenMainWindowRequest | 删除空协议链；保留实际 App activation / 托盘入口 | W4/W5 |
| AgentMonitorSampleEvent 空契约 | 唯一批次契约落地后退出旧空项 | W4/W5 |
| GetSnapshot 稳态 monitor / alert poll | 删除；仅初连/重连/明确状态重同步读取 | W4 |
| Query/List 自动 reconciliation 触发 | 删除查询副作用；保留一次明确的只读恢复用例 | W2 |
| App RealStructureEditorFlow 接力状态机 | Agent structure job 接管后退出 | W2 |
| single / multi / normal MAX runner 重复部分 | 共用 attempt、拒绝/后态解释；保留算法真正差异 | W2 |
| SimulationEditCoordinator 过渡执行栈 | 删 planner / policy / authority / executor 纯转发，纯 reducer + 一次 commit | W3 |
| CommitDocument / 整文+plan+events 模拟入口 | intent/CAS 提交落地后删除旧生产入口；保留 commitId 的结果识别用途 | W3/W4 |
| CreateHddTierTemplate | 由 CreateTierTemplate 代替，删仅测试入口 | W2/W5 |
| RenameHddTier | 无实际 UI 用例则退出，不为保留方法新增功能 | W2/W5 |
| CreateMultiTieredMaximumVirtualDisk | 由 CreateTieredVirtualDisk + typed MAX 代替 | W2/W5 |
| CreateSingleMemberPool / RebuildSingleMemberPool | 由 CreatePoolWithMembers + Agent 结构 Apply 代替 | W2/W5 |
| TryParseGigabytes 的字符串 MAX | typed MAX 代替；仍使用的 FormatGigabytes 保留 | W3/W5 |
| SetAutoCreateVirtualDiskAsync / SetAutoCreatePartitionAsync | 无入口 setter 退出；历史偏好按现行 schema 明确处理，当前自动布局行为不偷偷改变 | W3/W5/W7 |
| EffectiveActiveDocument / EffectiveActiveSnapshot | 仅别名则直接使用 ActiveDocument / ActiveSnapshot | W3/W5 |
| SimulatedSnapshot | 仅定义则退出，使用明确选中文档 | W3/W5 |
| MainWindow null-agent 本地库/采集/monitor | 生产要求 Agent connection；测试注入 fake IAgentConnection | W4/W5 |
| 旧 workspace_state.activePage / WorkspacePage 转换 | 不再写重复导航字段；LastActivePage 为唯一来源；历史数据读兼容只保留所需处置 | W4/W5 |
| IAgentPreferencesReader / IAgentShutdownTerminalActions | 无消费则退出，按实际组成判断 | W5 |
| IManageCommand/Comparison/Details/Navigation/SystemProjector | 删除无独立实现/用途的接口，保留实际纯投影实现 | W3/W5 |
| IMonitoringDatabaseAccess / IMonitoringPersistenceBackgroundDiagnostics / IMonitorSessionPersistenceDiagnostics | 依实际消费者删空接口或合并转发，不删除采样与持久化的有效边界 | W4/W5 |
| IOperationExecutor / IOperationInventoryVersionSource / IReplayEventSource / IUserPreferencesReader | 无生产或独立测试用途则退出，不靠类型名判断 | W5 |
| IOperationPlanner / ISimulationEditCoordinator / IStructuredSimulationEditRepository | 有实现，原统计不成立；随 W3 撤旧职责后再决定接口去留 | W3/W5 |
| 其他单实现接口和仅测试替身接口 | 删除无语义转发，保留真正需要替身或多实现的边界；不设数量配额 | W5 |
| WindowsAgentProcessLiveness / WindowsProcessIncarnationVerifier / WindowsProcessIntegrity | 共用系统查询与结果，保留不同判断 | W4/W5 |
| 逐迁移 IsValid / PlanHash / 冻结步骤 JSON 比较 | 边界验证后复用类型化对象，删除重复重算 | W2/W5 |
| SourceFacts 字符串比较 / inventory 自编码解码 / IPC 中间 JSON | 直接比必要字段或 revision，跨真实边界才序列化 | W1/W4/W5 |
| SQLite 无消费者的重复 snapshot 表示/整份历史副本 | 消费者收口后删无用途副本；journal、事务和确有查询用途的数据保留 | W5 |
| 重复 catch、日志、setter/Reset 刷新、全 catalog 投影 | 所属边界处理一次，文档/选择 revision 驱动最小更新 | W3/W4/W5 |
| 过时 Architecture 字符串/文档数量断言 | 删除；新增预算只针对真实重复行为 | W6 |
| 当前文档重复历史/已完成活动 Plan | 一次归档、单处事实、短状态；不全搬 Archive | W7 |

---

最终交付记录只需五项：**基线与版本；完成的行为和删除的旧链；相关验证及现有证据链接；真实未解决/未覆盖项；候选包及发布说明。** 无需另写第四份全量审查报告。

[F]: https://github.com/GitRuozhi/WinPool/blob/d56b4b4ac2f847cfd720dead251e26baa7ff1ed7/docs/Review/WinPool-Full-Review-20261017.md
[R]: https://github.com/GitRuozhi/WinPool/blob/d56b4b4ac2f847cfd720dead251e26baa7ff1ed7/docs/Review/WinPool-Repository-Audit-20261017.md
[T]: https://github.com/GitRuozhi/WinPool/blob/d56b4b4ac2f847cfd720dead251e26baa7ff1ed7/docs/Review/WinPool-Takeover-Review-20261017.md
[G]: https://github.com/GitRuozhi/WinPool/blob/d56b4b4ac2f847cfd720dead251e26baa7ff1ed7/AGENTS.md
[D]: https://github.com/GitRuozhi/WinPool/blob/d56b4b4ac2f847cfd720dead251e26baa7ff1ed7/docs/Development.md
[P]: https://github.com/GitRuozhi/WinPool/blob/d56b4b4ac2f847cfd720dead251e26baa7ff1ed7/docs/Product.md
[Q]: https://github.com/GitRuozhi/WinPool/blob/d56b4b4ac2f847cfd720dead251e26baa7ff1ed7/docs/Quality.md

