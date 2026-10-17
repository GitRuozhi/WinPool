# WinPool V0.61：减负、性能与首稿测试包

状态：**唯一活动计划；文档已编制，待用户审阅并批准代码与测试。** 激活只表示当前规划入口。本轮只读源码和改文档，没有编辑代码、改版本字段、构建、测试、启动程序或操作存储。

- 基线：2026-10-17，`main / d56b4b4ac2f847cfd720dead251e26baa7ff1ed7`；代码与 `Directory.Build.props` 仍为 **V0.60**。
- 用户确定：**本轮文档和下一轮全部代码工作都属于 V0.61**，不拆成多个版本。代码修改收口后才将版本源推进 V0.61。
- 优先级：**减负、减少冗余／防御性重复／过度工程 → 性能**。目标是在 V0.70 前交出第一稿公开测试版；本计划目标为 V0.61 便携候选包，不等待全部环境验证。
- 当前仅开发者环境；跨 Windows／设备组合交给专门测试人员。本计划不等于上传、tag、Release 或发送测试包的指令。
- 原[已完成 V0.60 Plan](Archive/20261017-v061-document-baseline/Plan-v060-completed.md)与用户提供的[V0.6x 原提案](Archive/20261017-v061-document-baseline/WinPool-V0.6x-Plan.md)原样归档。后者被本文件替代，未曾执行。

## 1. 可行性结论与修订

原提案总体可行，主要重复调用、预演和传输与当前源码吻合；需要补全入口、契约和迁移顺序后执行。这里是本轮静态结论，不是新性能实测。

| 原方案判断 | 源码现况与本计划处理 |
| --- | --- |
| 普通权限采集先修discovery | 成立。Storage默认启用；discovery与物理盘枚举处于同一失败域。scoped已关闭discovery、Hardware不注入该发现步骤。W1按用途修正，不能全产品盲删 |
| 单操作最多3次采集 | 需限定为一个实际Windows写调用且首次后态成功；多步OperationId、结构Apply、MAX不套这个总数。见§4 |
| 结构Apply移入Agent | 成立，但不能直接拼成一个steps数组。当前planner拒绝MAX混其它步骤、GPT初始化混后续分区；新身份／容量／MSR只能写后取得。W2迁移受限阶段编排 |
| 模拟纯reducer＋CAS／commitId | 方向成立；CAS、幂等及丢回执查询已经存在。W3复用并改轻量回执，处理lookup对旧plan表的依赖，冻结随机ID和时间 |
| 监控推送替换轮询 | 成立。现有sample event无生产者；上游订阅可丢样，事件队列溢出会关闭连接。W4补序列、gap和一次重同步，不能只加batch包装 |
| 删除旧RPC／provider | 部分成立。`LoadAgentManageInventoryRequest`仍用于启动缓存；`EmbeddedPowerShellInventoryProvider.Project`仍被调用。先迁消费者再退役，见W5 |
| 现有打包脚本生成便携包 | 不成立。已有自包含App／Agent树和merge，没有zip入口。W8只补一个脚本，不能把带开发Data的标准Release直接压缩 |
| 上个公开包升级／完整平台门 | 不适用当前阶段。使用现有受支持schema夹具和开发数据副本；外部测试覆盖环境差异，不以购置设备或完成所有组合阻塞首稿 |
| 两轮修改、一次收口、一个Plan | 保留。删除按路径替换同步做；文档只维护变化事实，不另建审查台账 |

诊断证据按需查：[仓库审查](Review/WinPool-Repository-Audit-20261017.md)、[全量检视](Review/WinPool-Full-Review-20261017.md)、[接手审查](Review/WinPool-Takeover-Review-20261017.md)、[VD耗时](Reference/Pool-VD-Timing-20261016.md)。执行者先读所属工作包和源码，不重新全文审查仓库。

## 2. 固定范围与执行规则

1. App负责页面、输入、选择和内存草稿；Application负责共享纯规则；Agent负责真实结构作业、权威提交、采集、恢复及持久化；Windows适配只做固定类型化读写。
2. 每条被替代生产链在新链接通的同包退出。保留实质替身／多实现边界，不以接口数、文件行数或报告数验收。没有调用者的成员直接删并，整文件按AGENTS移动，不直接删除文件。
3. 输入、持久化读回、临写现场保留必要校验；进程内复用有效结果。准确身份、权限／角色、CallIssued耐久、CAS、Unknown不重放不能作为“防御性编程”删除。
4. **本版不预建常驻PowerShell、控制请求多路分发或SQLite全库重构。** 原稿将其列为条件项；先消除已证实的重复，它们不是首稿前置。核心修改后若仍有独立瓶颈，在最终结果记一行具体计时和建议；扩大这些实现前由用户决定，不让执行者自行建立worker池、第二套连接或数据库框架。无消费者表示、重复PRAGMA的局部删并仍在W5内。
5. 保留固定内嵌脚本、现有PowerShell读／写生命周期。读超时可取消，已发出的写入不能因套用只读取消策略被强杀或自动重发。
6. 代码获批后，标准输出、WinPool开发数据和WDC操作沿用[AGENTS](../AGENTS.md)已有授权。两块Samsung禁止写，不使用自由形式存储命令；本轮不触碰这些运行状态。
7. App／Agent同包升级；协议破坏性调整在代码中统一推进一次IPC版本，不保留生产双协议。内部格式号与V0.61产品号不同。
8. 不增功能家族，不改变MAX算法、自动布局、格式化范围、系统分区保护、隐私规则和草稿交互。遇到必须改这些语义或发现无法满足本计划的前提，先指出具体问题询问用户，继续无关工作。

## 3. 执行顺序

收到用户代码执行批准后按以下顺序连续推进，不逐包重复请示：

| 顺序 | 工作 | 接续条件 |
| --- | --- | --- |
| 0 | 核对HEAD／用户改动，沿用W7规则；完成W6的fixture和诊断隔离修复 | 合成回归不借本机能力，相关失败可用于判断产品 |
| 第一轮A | W1步骤1–3，W4步骤1 | 普通权限故障控制流和独立连接超时已修 |
| 第一轮B | W2步骤1–4，W3步骤1–3 | 单写事实可复用；App一次revision一次完整预演，不同状态分别更新 |
| 第二轮A | W1步骤4–6，W2步骤5–7 | 唯一collector、Agent结构作业、MAX attempt及有界恢复接通 |
| 第二轮B | W3步骤4–6，W4步骤2–7 | 共享reducer轻量提交、事件与监控批次、按需正文、旧生产链退出 |
| 同步 | W5随各包删旧；受影响行为测试随包调整和定向运行 | 不留“以后再删”的双实现 |
| 收口 | W6最终门、W8升版／打包／开发机验收，更新短结果 | V0.61候选可交付，未覆盖环境写明 |

上述是依赖顺序，不是版本阶段或审批门。IPC与持久化契约由一个负责人集中修改；可独立文件才委派。共享构建、硬件和桌面操作串行。小范围构建／直接回归通过即继续，最终稳定后全套一次。

## 4. 公共契约与计量

以下是**待实现合同**，名称可沿用现有类型，字段含义和边界不可省略。

| 数据 | 最小内容与所有者 |
| --- | --- |
| 采集批次 | Agent本机绑定、generation、purpose/scope、准确目标、coverage／完整性、原始非merged事实、观察时间；collector产生，UI缓存合并另属coordinator |
| 临写上下文 | 已验证冻结计划／步骤、准确目标闭包、同次fresh事实和本步适用安全／能力结果；Agent进程内传递，不经IPC传Windows实现类型 |
| 已验证后态 | 本步结果、准确输出ID与关系、原始fresh事实及coverage；只有Verified才推进依赖阶段，显示缓存不能反向充当安全证据 |
| 结构作业 | jobId、冻结最终目标／成员／允许动作范围、阶段及子operation映射、已完成／剩余目标、revision／结果；复用现有journal存储，不造通用workflow |
| 模拟编辑请求 | documentId、expectedRevision、commitId、typed edits及一次冻结的创建ID／时间；不传整个候选文档、通用执行plan或events |
| 模拟回执 | commitId、documentId、before/afterRevision、意图摘要、状态和验证候选一致所需的after hash／必要结果；文档与回执同事务保存 |
| 真实状态 | operation/jobId、递增revision、state、必要步骤变化／结果；准备时取计划一次，详细evidence按需有界取 |
| 监控批次 | Agent实例、sessionId、首末sequence、有界samples、上游drop/gap；编号在采样接受位置或将此前丢样显式带出 |

**三次预算：**仅对非MAX、一个实际Windows写调用、无外部变化／重试、首次后态观察成功的用例，Prepare一次＋紧邻调用前一次＋写后一次。Accept、正常终结、同后态UI／monitor消费不再采集。整个结构作业、多步骤OperationId和MAX不硬压成三次；MAX按attempt计，multi seed也计。外部变化、provider未收敛或coverage不足的补采记录原因并计入该用户用例总数，不把成本转移到未计数通道。

采集次数只在底层实际provider invocation统计，关联operationId／stepId／reason；同次`inventory.provider.collect`、`inventory.collect`、`real.capture`嵌套记录不累加。新generation与写边界使旧事实失效，不用固定TTL或同一个Apply作为新鲜性证明。

## 5. 工作包

### W1 采集控制流与唯一实现

入口：[EmbeddedStorageInventoryScript](../src/WinPool.Infrastructure.Windows/EmbeddedStorageInventoryScript.cs)、[ScopedStorageInventoryScript](../src/WinPool.Infrastructure.Windows/ScopedStorageInventoryScript.cs)、[InventoryPipeline](../src/WinPool.Infrastructure.Windows/InventoryPipeline.cs)、[AgentInventoryCoordinator](../src/WinPool.Agent/AgentInventoryCoordinator.cs)、[Agent组成](../src/WinPool.Agent/Program.cs)、[AgentLocalSystemIdentity](../src/WinPool.Agent/AgentLocalSystemIdentity.cs)。原稿的`EmbeddedScopedStorageInventoryScript`不存在，使用上述实际文件。

1. `ForPurpose`／普通reader显式采用无discovery读取；保持Hardware和scoped既有用途。把确需重新发现的受控变更后的discovery改为显式选项；不得仅延长60秒超时。
2. 将discovery诊断与`Get-SourceSet MSFT_PhysicalDisk`枚举分开，发现失败仍枚举。不要把发现失败写成枚举失败使parser丢弃成功数据；需要新发现才能确认后态时，失败如实影响完整性。
3. `GetPhysicalExtent`前检查所需物理事实。缺失时直接返回准确原因；事实完整但单HDD快路径不适用时保留精确extent路径，不能按MediaType／名称猜成员，也不机械删掉全部extent功能。
4. 将完整／Storage／scope采集收成一份程序集内固定模板及封闭参数，保留目前字段、来源状态和准确关系规则。移除解压后anchor patch和scoped字符串替换副本；仍经stdin执行。迁移当前`Project`纯投影后撤legacy比较入口，见W5。
5. 在Agent组成处让真实执行、inventory和monitor身份读取使用同一collector。相同本机绑定／generation、已有批次完整覆盖所需scope时合并并发请求或投影；coverage不足才补采。写边界后必须新代后态，不借merged缓存。不要加全业务大锁；不把provider对象跨进程传输。
6. `AgentLocalSystemIdentity.ResolveAsync`缓存稳定系统绑定，数据根切换／重绑定失效；磁盘身份、角色和能力仍实时。删除为同一绑定反复加载／解码整个文档的路径。

完成条件：普通discovery失败仍可枚举；缺前提extent为零调用；写后发现与scope完整性保持；生产只剩一个采集实现，旧比较链退役。定向修改既有`WinPoolFactCaptureTests`、`ScopedInventoryProviderTests`、`EmbeddedStorageInventoryTierMemberTests`、`AgentInventoryCoordinatorTests`、`MonitorTargetIdentityTests`；覆盖上述分支即可，不造故障注入平台。最终普通权限冷启动／刷新放W8同一批验证。

### W2 真实单步、结构作业、MAX与恢复

入口：[AgentRealOperationService](../src/WinPool.Agent/AgentRealOperationService.cs)、[RealStorageBackendContracts](../src/WinPool.Application/RealStorageBackendContracts.cs)、[OperationAuthorization](../src/WinPool.Execution/OperationAuthorization.cs)、[WindowsRealStorageBackend](../src/WinPool.Infrastructure.Windows/WindowsRealStorageBackend.cs)及其`MemberSets`／`MaximumCapacity`分文件、[WindowsRealOperationPlanner](../src/WinPool.Infrastructure.Windows/WindowsRealOperationPlanner.cs)、[RealStructureEditorFlow](../src/WinPool.App/RealStructureEditorFlow.cs)。

1. 先扩展现有`RealStepPreflight`／结果的进程内类型化事实传递。保留Application依赖方向；不要将Windows基础设施类型塞入上层，或为了复用把内部事实编码成JSON再解码。
2. `AcceptAsync`只核实当前会话／机器绑定、冻结内容、确认期限和一次性令牌，再耐久接受。同步拆开`OperationAuthorization`中的冻结确认与实时`CurrentInventoryVersion/TargetFingerprint`检查：不能填旧值假装已作现场核验。真正现场检查只由下一步负责。
3. 将`RunStepsAsync`的step preflight与`ExecuteStepAsync`的before合为紧邻调用的一次捕获／解析／安全核验；随后先耐久`PreparingCall → CallIssued`才调固定adapter。执行消费同一个上下文，保留adapter准确目标检查。输入／持久化读回验证plan/hash一次，删除内部重复`RequireFrozenStep`全量验证与JSON步骤比较。
4. backend写后核验返回§4的Verified后态。步骤状态先耐久，再直接正常终结；正常成功不进恢复`Reconcile`。同一后态交下一阶段、UI和monitor按coverage使用；若下一个写调用需要重新确认角色／能力，仍做它的临写检查，不把前一步结果当永久授权。
5. **迁移结构编排：**将`RealStructureEditorFlow`以及`RealStructureApplyAuthorization`、`RealAutomaticLayoutProgress`中必要阶段规则移到Agent／Application。App保留草稿和一次完整目标确认；Agent接受一个job，冻结准确成员、删建／格式／盘符范围，内部根据Verified输出准备下一阶段。先布局能力预检，再允许破坏性阶段；MAX完成后才按实际OS容量布局，GPT完成后才识别／规范化MSR。沿用现有封闭阶段plan与journal；保存job→child operation、输出映射、完成前缀／剩余目标。jobId是一次Apply的幂等标识，同ID同冻结目标返回原作业，异目标拒绝；父作业与确认范围先耐久，每个子operation的父关联在其CallIssued之前耐久。整个作业持有现有真实写入调度所有权，阶段间不插入其它真实作业；内部阶段不重入外部Accept争抢非重入mutationGate，不另造锁框架。每个子阶段必须属于确认目标并且未发生冲突，不能通过内部令牌扩大范围。
6. **统一MAX attempt：**普通／单层／多层共用临写捕获、目标、安全、Prepare／CallIssued／Complete、adapter、观察和容量拒绝解释。候选调度仍用`MaximumCapacityAlgorithm`／`MaximumCapacitySearchState`；保留多层seed、逐层顺序和显式层不变。复用`ObserveMaximumAttemptAsync`已验证后态，撤额外`:max-verified:`捕获。容量拒绝所需的无变化、终态jobs和第二次观察是证明条件，不能一概删除；Unknown立刻停止。历史原生MAX恢复解析仍有持久消费者，保留其只读识别，不重解释或重放。
7. **恢复收口：**`QueryAsync`／`ListRecoverableAsync`只读，不再`ScheduleReconciliation`；启动沿用现有状态规则：取消尚未接受的Prepared，将中断的Accepted／Running置Unknown，只对需核对的Unknown启动一次只读恢复。另设明确只读重查请求一次。删除App的`while(IsBlocked)`恢复轮询；已有同operation互斥和不变诊断不追加机制继续复用。后态保留现有60秒窗口／必要间隔，用剩余总预算限制每次只读inventory，避免内外各60秒叠加失控；不套到已经发出的write runner。无进展到期保留Unknown与显式重查入口，不续写。

作业生命周期固定：App断流而Agent仍活着时，已接受作业可在原确认范围内继续；用户取消停止尚未发出的步骤，已发出调用仍观察；明确失败／Unknown停止后续写。Agent重启仅只读恢复，**不自动继续未发出的阶段**；再次Apply另确认剩余目标。完成才清草稿，中止／未知显示已完成与剩余目标，不把旧绑定的输出写入新草稿。

完成条件：§4普通单写预算成立；Accept和正常终结无额外inventory；App不再接力结构阶段；MAX算法和拒绝证明不变；Query/List零恢复副作用；未知不重放，期限真正覆盖只读观察。迁移现有`AgentRealOperationServiceTests`、`WindowsRealStorageBackendTests*`、`AgentMaximumCapacityBoundaryProofTests`、`WindowsMaximumCapacityRejectionTests`以及`RealStructureDraftPlanner/ApplyAuthorization/AutomaticLayoutProgress/ResultSynchronizationTests`的有价值行为到实际所有者，少量补次数断言。W8再做受影响WDC代表闭环。

### W3 共享模拟规则与UI按修订更新

入口：[SimulationDraftPlanner](../src/WinPool.Application/SimulationDraftPlanner.cs)、[SimulationEditingSession](../src/WinPool.Application/SimulationEditingSession.cs)、[StorageSystems](../src/WinPool.Application/StorageSystems.cs)、[SimulationEditCoordinator](../src/WinPool.Infrastructure.Windows/SimulationEditCoordinator.cs)、[StorageSystemRepository](../src/WinPool.Infrastructure.Windows/StorageSystemRepository.cs)、[SimulationDocumentRepository](../src/WinPool.Infrastructure.Sqlite/SimulationDocumentRepository.cs)、`StorageStructurePage.xaml.cs`、`DiskPartitionPage.xaml.cs`、`EditorPageBase.cs`、`WorkspaceViewModel.cs`。

1. 把草稿构建分成“生成typed edits → 补`EnrichPlanIntent`／`AppendPartitionIntents` → 单次完整预演”。当前Build、Precheck、Evaluate／Apply内Evaluate重复；先补齐最终意图再算一次，不直接删后一次而复用旧结果。
2. 复用`ContentRevision`、`BaselineRevision`、`BindingGeneration`，预演键为documentId＋baseline revision＋binding generation＋content revision。字段输入、PoolIntents、MAX、自动布局、撤销／重做及重新绑定实际影响意图时使键失效；无变化不增号。只应用当前键的结果；Apply若没有对应成功结果先完成它，不能用前一revision继续。
3. `SetRealOperationActivity`先比较实际busy／obscure／phase／owner；分开进度状态、文档revision与选择状态。相同通知直接返回；文档没变不重建拓扑，选择变化只更新选择呈现。合并commit/reset/setter重复刷新，不因拆页面重新造view model框架。
4. 抽取Application中一份无IO的reducer供App预演、Agent提交使用。保留现有规则与SourceFacts投影；一次调用只评估／应用一次。将创建pool／VD／OS disk／partition／volume等ID及相关时间在最终typed edits里一次冻结，优先复用`Allocated*Id`／`StableAllocatedId`；reducer内部不得自行`Guid.NewGuid`／`DateTimeOffset.Now`使两端候选不同。新用户意图生成新上下文，同一次重试保持原值。
5. **复用事务提交：**App发送§4请求；Agent先按commitId查回执，同ID同意图返回原收据，异意图拒绝；再加载当前document，核对expectedRevision，在Agent边界执行一次reducer，与文档／轻量回执同事务CAS提交。沿用`simulation_edit_commits`，同时修改`CommitEditAsync`和`FindByCommitIdAsync`，解除lookup对`operation_plans`／整份历史document的JOIN依赖。现表`operation_id`外键引用`operation_plans`且`sanitized_json`必填，故**本包必须在现有schema迁移内调整回执表**：解除新回执的plan外键和整文必填要求、保留commit_id唯一约束、迁移已有回执以继续按原commitId查询；已有真实journal与屏障保留，新旧真实计划不改语义。W2若需作业持久字段，与本包由同一负责人合入本版一次schema升级。App确认afterRevision/hash一致才发布缓存候选；不一致或丢缓存则按ID读权威document一次。保存失败保留草稿，回复丢失先lookup原commitId，不换ID重发。
6. 撤`SimulationEditCoordinator`中的通用planner／policy／一次性authority／executor过渡栈，以及正常编辑整文＋plan＋events提交链；有用纯函数归实际owner。导入／创建／导出和按需加载仍可传正文，不能把正常编辑的轻量约束误套为禁止所有全文接口。原子性、冲突和未知结果行为测试迁移，不为保留旧栈改回旧合同。

完成条件：一次App有效revision一次完整预演；100次相同进度新增拓扑重建为0；相同冻结意图的预览／Agent候选一致；CAS、幂等、丢回执和失败不假成功仍成立；正常编辑不传／回显已有全文，无旧模拟执行栈。

相关用例：Application模拟规则、自动布局、MAX／undo；`SimulationEditCoordinatorTests`的LostCommitReply／UnconfirmedCommit／ConfirmedCommitFailure；`SimulationDocumentRepositoryTests`的CAS／原子／commitId绑定；App的revision／通知行为。以现有用例改断言和入口，不为每个helper加镜像测试。

### W4 IPC、监控、启动与生命周期

入口：[NamedPipeAgentConnection](../src/WinPool.Agent.Client/NamedPipeAgentConnection.cs)、[AgentControlServer](../src/WinPool.Agent/AgentControlServer.cs)、[AgentEventServer](../src/WinPool.Agent/AgentEventServer.cs)、[AgentClientEventFanout](../src/WinPool.Agent.Client/AgentClientEventFanout.cs)、[DesktopAgentRuntime](../src/WinPool.Agent/DesktopAgentRuntime.cs)、[MonitoringSessionCoordinator](../src/WinPool.Monitoring/MonitoringSessionCoordinator.cs)、`MonitoringService.cs`、`MonitorAlertObserver.cs`、`MainWindow.xaml.cs`及W3仓储／catalog。

1. 在连接拥有者统一处理控制／event pipe的独立5秒`TimeoutException`，转现有连接失败结果，由UI异步边界呈现一次未连接／未保存；不让它逃入XamlUnhandled，不多层catch或盲重试。启动、提权、退出沿现有lifecycle，等待可取消，退出后不迟到Ready。
2. inventory正文选择**更新事件唯一承载**；手动请求只回documentId／revision／generation及成功失败receipt。App按revision应用一次；事件先后与receipt无关，手动通知由请求所属UI呈现一次。断流／缺失revision时通过现有load入口补读一次最新正文，失败保留旧文档；不保留常态双传。
3. 真实操作准备响应给计划，接受先回job／operation回执；状态用§4轻量事件，详细evidence按需有界读。终态先落库再发布，状态读取不加载整段历史。删除`EditorPageBase`的250 ms×1200常态查询，初连／重连／明确重查一次同步；W2恢复请求与普通状态查询分开。
4. Agent从`MonitoringSessionCoordinator.WatchAsync`形成有界sample batch，传输批次最多等待250 ms并受点数／wire预算限制；用户采样率包括20 Hz不变。发送与SQLite落库独立，不等待落库完成才推送；现有250 ms是落库等待，不是已经实现的传输批次。以实例＋session＋sequence去重，序列在丢样可能发生前分配或携带上游drop/gap；不可下游才编号并宣称连续。批次按真实wire预算拆分，时间／点数均有界，保留现有窗口上限。
5. App一个monitor cache供图表与告警，初连／重连同步一次窗口和session。撤`MonitoringService.RunRemotePollLoopAsync`全快照轮询与`MonitorAlertObserver`独立轮询；采样／传输／绘图分开。样本不能逐点挤入1024事件队列；发送端只合并有限批次，满时标记gap并通过现有断连／重连路径恢复，控制终态不能静默丢弃。App丢连接后一次同步当前操作状态＋监控窗口，未知采样段不伪造连续；终态可靠性来自耐久状态和重同步。
6. 复用现有metadata／按ID正文RPC，将`AgentBackedStorageSystemRepository.LoadSimulationsAsync`的逐文档加载改为metadata catalog；同步调整`StorageSystemCatalog`和标题选择的条目。启动只读metadata和所选document；选中、导出或编辑时加载所需正文，切换丢弃迟到加载结果。保留已有本机预显、模拟选择和草稿，不返回空文档假装已加载。导航只用`LastActivePage`，workspace只存系统／对象选择，撤重复`activePage`转换。
7. 统一真实wire预算检查，移除进程内自编码解码、全文JSON比较和中间重复序列化。请求／响应配对锁保留，现有事件pipe保留；本版不加多路复用。池成员没有独立OS counter时给准确不支持／角色结果，不报身份丢失、不借VD计数器。生产`MainWindow`要求Agent connection，删除null-agent本地SQLite／采集／监控回退，测试使用现有fake边界。

完成条件：稳态无GetAgentSnapshot监控轮询，无App无界恢复轮询；同revision正文只传／应用一次，事件丢失后可单次补读；样本上游缺口可见、终态可恢复；连接超时不unhandled；启动目录按需加载、导航无双来源。定向用`NamedPipeAgentConnectionTests`、`AgentEventHubTests`、`AgentClientEventFanoutTests`、`AgentBackedStorageSystemRepositoryTests`、`LocalInventoryObserverTests`及相关monitor／workspace测试；补批次序列、溢出重同步、一次超时呈现的行为，不造新UI自动化框架。

### W5 随替换删除旧链

查找默认只用`src`、`tests`、solution／project引用和受影响当前文档；不搜索Old／Archive来证明生产可达。每组在所属包关闭，确有实际消费者则保留并在最终结果一句说明，不再建删除台账。

| 候选 | 明确处置 |
| --- | --- |
| `CaptureAgentInventoryRequest`、`NativeWindowsInventoryProvider`、旧comparison组成 | 新collector接通后连请求／codec／client／handler／composition退出；有用比较纯逻辑迁入实际owner |
| `EmbeddedPowerShellInventoryProvider` | `Project`仍被coordinator调用，先迁纯投影再退类壳 |
| `LoadAgentManageInventoryRequest`／`LoadManageInventory` | **仍有启动缓存消费者**：MainWindow→AgentBackedMachineRecordService.LoadLocalScanAsync。先迁到统一缓存读取，再撤旧RPC |
| `LocalMachineRecordService`、`IStorageInventoryProvider`／`WindowsStorageInventoryProvider` | 核对实例化后移走无消费包装；后两者定义位于DesktopContracts／InventoryPipeline，不能猜独立文件 |
| `WinPool.Inventory`／其Tests | 比较旧链退出后，迁有价值逻辑／用例，空项目移出slnx与引用；不是无条件先删项目 |
| `OpenMainWindowRequest`、旧空`AgentMonitorSampleEvent` | 实际activation／批次路径接通后撤空协议链，不删托盘正常激活 |
| App结构接力、Query/List调度、MAX重复runner | W2替换同时退役；保留历史MAX只读解析和真正不同的候选调度 |
| 模拟全文执行plan入口／过渡executor | W3完成同事务receipt与lookup后退出；导入／创建正文入口继续有效 |
| `CreateHddTierTemplate`、`CreateMultiTieredMaximumVirtualDisk`、`CreateSingleMemberPool`／`RebuildSingleMemberPool` | 由现有通用tier／members／typed MAX及Agent作业替代；`RenameHddTier`无真实用例则退，不为保留入口增加功能 |
| 字符串MAX解析、`SetAutoCreateVirtualDiskAsync`／`SetAutoCreatePartitionAsync` | typed MAX与固定新建规则承接，撤无入口setter；合法格式化函数保留，旧偏好读回不复活旧行为 |
| `EffectiveActiveDocument`／`EffectiveActiveSnapshot`／`SimulatedSnapshot` | 纯别名改用ActiveDocument／ActiveSnapshot；仅定义者退出 |
| `IAgentPreferencesReader`／`IAgentShutdownTerminalActions`、`IManage*`纯转发、monitor diagnostics包装、`IOperationExecutor`等 | 以实际组成／替身用途判断，撤无语义中转；`IOperationPlanner`／`ISimulationEditCoordinator`等已有实现，不能按旧错误统计直接删 |
| `WindowsAgentProcessLiveness`／`WindowsProcessIncarnationVerifier`／`WindowsProcessIntegrity` | 同次同PID共用必要系统查询，存活／化身／完整性语义分别保留，不把握手当永久进程证明 |
| `IsValid`／PlanHash／SourceFacts JSON比较、自编码解码、重复catch／日志 | 边界验证后复用对象／revision，异常由拥有者呈现一次；移除无效内部重复 |
| SQLite JSON＋关系副本、历史snapshot副本、重复连接设置 | 只删已无消费者的表示／重复配置；仍有必要查询用途的保留。journal／事务不能因体积大直接删 |

整文件按AGENTS移到项目根Old并保留相对路径，移动前后核对；代码成员／引用可在获批代码任务内直接编辑，无逐项审批。结束时`rg`确认旧生产入口和project引用已无残留；测试总数可以下降，不设源码行数／接口数指标。

### W6 可信反馈与一次最终验证

前置修复随首个代码包做，不先重跑当前已知失真的全套：

1. `WindowsRealStorageBackendTests*`恢复fixture显式注入一致能力结果，消除合成本机WMI回落；不放宽生产能力核验。过时XAML／文档字符串断言删除，实质安全与依赖行为保留。
2. `RealAdmissionReadOnlyTests`环境变量未启用时改显式skip，参考已有`EnvironmentEnabledFactAttribute`；不在Fact内return假通过。现有ManualPerformance保持门控，不新建全仓分类工程。
3. 测试诊断注入现有sink或临时根，不能写用户Diagnostics；归档异步断言等待既有完成任务／记账，保持有界期限。7za失败先查该实际进程／目录原因；守卫在标准进程停止的条件验证；完整性修复脚本只有确认硬编码根或隐藏失败才修相关点，不递归重查整棵研究目录ACL。
4. W1–W4每包只跑对应现有测试类；必要次数断言限本轮真实重复：单写三次、Accept零次、一次revision一次预演、相同进度零重建、Query/List零恢复、批次不双应用。退役测试随旧实现退出，不为了维持历史总数保留死链。
5. 代码稳定并已升V0.61后，在标准路径执行[Quality](Quality.md#后续可用命令)的一次完整自动门及最终Release构建。依赖未变、既有审计适用时不重复审计。已定位失败须关闭；skip／未测如实记录，不能以“测试版”掩盖主链失败。

可直接复用的定向命令例子（仅获批代码后运行，按本包实际类过滤）：

```powershell
dotnet test tests/WinPool.Infrastructure.Tests/WinPool.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WinPoolFactCaptureTests|FullyQualifiedName~ScopedInventoryProviderTests|FullyQualifiedName~EmbeddedStorageInventoryTierMemberTests" -m:1
dotnet test tests/WinPool.Agent.Tests/WinPool.Agent.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentRealOperationServiceTests|FullyQualifiedName~AgentMaximumCapacityBoundaryProofTests|FullyQualifiedName~AgentInventoryCoordinatorTests" -m:1
dotnet test tests/WinPool.Infrastructure.Tests/WinPool.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SimulationEditCoordinatorTests|FullyQualifiedName~AgentBackedStorageSystemRepositoryTests" -m:1
dotnet test tests/WinPool.Persistence.Tests/WinPool.Persistence.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SimulationDocumentRepositoryTests" -m:1
```

新所属项目承接迁移测试后改对应filter，不运行已经退役的项目。实际命令和输出留现有artifacts位置即可；失败修复后只补相关验证，后续未影响的证据不重复跑。

### W7 文档与工作方式（本轮已处理）

- 唯一活动Plan为本文件；原方案／已完成计划、Quality长历史、旧README／Product／Development／CHANGELOG及已被替代设计原样[集中归档](Archive/20261017-v061-document-baseline/README.md)，不清测试证据、不恢复用户删除报告。
- README只讲使用；Product管产品，Development管技术／构建，Quality管方法／短状态与证据。精确尺寸／图标直接读共享源码，取消逐控件同步多份表的步骤；旧控件／规则／图标／样式表原样归档，当前代码V0.60与目标V0.61分开。
- 非核心资料按用途整理：[Review](Review/README.md)只保留本轮三份审查并区分基线与方法；统一对象、专项调查及源码入口归[Reference](Reference/README.md)；旧阶段调查／静态复审／拓扑诊断和通知表[原样归档](Archive/20261017-noncore-docs/README.md)。不再维护单独DesignTables目录。
- 小改闭环为实现并删旧→相关验证→只更新变化事实→本地提交。一个执行方验证；无新全面报告、日报、附加审批门或每提交全套回归。
- 委派只需消息中的目标、所有权、固定决定和验收；主代理负责共享合同及集成，不为用满并发拆分。本地main提交，发布仍按用户实际指令。

下一轮仅随着已实现行为更新所属位置及本Plan状态，不再次开展文档大整理。

<a id="W8"></a>

### W8 升版、候选包与开发机集中验收

1. W1–W5代码完成、直接回归有效后，将`Directory.Build.props`的`WinPoolVersionIteration`由0改1，保持major0／minor6；产品显示V0.61，数值版本按既有公式。不要本轮文档就改字段，也不为每包单独升版。
2. W6稳定全套门和最终Release构建后，新增唯一简单`build/Package-WinPool.ps1`。读取版本源，使用全新`artifacts/packages/V0.61/<唯一批次>/WinPool-V0.61-win-x64`；调用现有`Merge-RuntimeTrees.ps1 -AppDir artifacts/trees/Release/App -AgentDir artifacts/trees/Release/Agent -Destination <新暂存目录> -SkipPdb`。不调用完整Rebuild，不改merge路径保护，不覆盖已有包。两侧runtime文件与已验证标准树核对一致；不从携带Data的标准树整目录复制。
3. 保留App／Agent exe和dll、`.deps.json`、`.runtimeconfig.json`、App `.pri`／`.xbf`、Assets及`Tools/7zip`工具／License／NOTICE等运行内容。排除Data、数据库／WAL、日志、诊断、证据、PDB、脚本、源图和多余子运行树。发现输入树混入用户数据即停止该打包并报告，不能静默发布。
4. 包内附一份简短测试说明：版本／基线、启动／退出与数据位置、当前能力和已知限制、开发机已测范围、外部测试建议、反馈所需最小信息（环境／步骤／结果／相关错误）。不建立遥测、反馈系统或多环境CI。
5. 生成zip与SHA-256，解压到同批次另一新目录，以普通权限跑一次候选启动／刷新／退出。候选包检查App／Agent版本配套、资源和7z存在、用户数据未混入。该批次与下表GUI／设备验证串行，不与自动测试或计时互相干扰。

| 集中场景 | 最小覆盖与判断 |
| --- | --- |
| 普通启动／刷新 | 从实际候选普通权限冷启动，工作区、Storage、monitor恢复得到明确结果；手动刷新一次，无新增unhandled，失败可见且旧事实保留 |
| 模拟／UI | 一组新建／修改／MAX／撤销／提交；一个足以复现旧重复路径的较大既有文档；切页／切系统草稿保留；保存失败不假成功；自动测试提供revision次数和CAS证明 |
| WDC真实单写 | 准确身份检查后用改名等代表路径核对三次预算／临写／后态；同步监控以确认后态复用，恢复原参数 |
| WDC结构／MAX | 本轮迁移涉及全结构与MAX，做一次显式建池／层／MAX VD→GPT／MSR→NTFS64 KiB／盘符→明确删除清理闭环；并覆盖普通MAX路径。按可用单盘验证，不假称多成员；错误／Unknown分支用已有fixture，不人为制造危险真实故障 |
| 监控／生命周期 | 同一批次保持原采样设置（含20 Hz），跨一次断连／重连和真实编辑，核对序列／gap、持久化、终态与退出；普通归档使用现有小阈值fixture与工具路径验证，不做1 GiB耐久专项 |
| 数据 | 现有支持schema fixture、一个开发数据副本的加载／提交／导出／导入与数据根切换；旧不支持格式明确拒绝、不覆盖原件。W3回执迁移证明原commitId可查询、真实journal保留；不存在上个公开包前置 |
| 末态 | WDC按AGENTS恢复在线GPT、规范16 MiB MSR、余下可用NTFS64 KiB `WinPool_Test`／E:；两块Samsung限定结构无改动；恢复本批次临时偏好／模拟对象，未终结真实结果如实处理 |

实机操作仅通过WinPool受控路径，已有WDC授权无需再问；运行／证据依赖、身份或其它目标边界有冲突时停止对应写入并询问。当前磁盘状态必须读取，不能把历史末态当现场。

性能交付只需记录同环境代表路径的实际调用数和墙钟，原始计时沿现有`operation-timing`记录；没有匹配旧样本就报告当前值及调用删减，不编造提速比例。不同Windows／SSD／SCM／多成员／冗余／多层硬件、全DPI／高对比度矩阵、8–24小时耐久留给外部测试或后续专项，发布说明标未测即可。

**候选完成条件：**确定工作包完成；相关回归和集中场景有结果；不存在已知普通启动不可用、错误目标、重复写入、提交丢失或主要编辑链不可用的问题；zip／校验值／说明齐全。准备好候选即停止本轮，不自行上传或发送。首稿不因安装包／签名／Store／全面矩阵延期。

## 6. 状态与交接

| 包 | 当前状态 | 关闭时只补什么 |
| --- | --- | --- |
| W1 采集 | 未开始 | 实现提交、相关回归／普通刷新结果 |
| W2 真实执行 | 未开始 | 三次预算、结构／MAX／恢复结果与代表实机证据 |
| W3 模拟/UI | 未开始 | 唯一预演／reducer、CAS／幂等结果 |
| W4 IPC/监控/启动 | 未开始 | 轻量消息、批次／重连、按需加载与退出结果 |
| W5 退役删并 | 未开始 | 已撤链及确有消费者的保留项，一段即可 |
| W6 验证反馈 | 未开始 | fixture修复、定向与最终一次门结果 |
| W7 文档/工作方式 | 已验证（仅文档） | 当前本地链接／章节、16份原件归档完整性及Git文档范围已核对；未运行产品测试 |
| W8 V0.61候选 | 未开始 | 实际版本、集中验收、包路径／校验值／未覆盖项 |

代码执行批准尚未收到。下一位执行者先核对本节授权状态和HEAD差异，再从§3顺序0开始；不要把文档激活、历史WDC授权或W7完成当成本轮代码已获批准。

批准后用同表记录未开始／实施中／已实现待验证／已验证／受阻，最多附提交和已有输出链接。最终交付五项即可：基线／版本、完成及退役行为、验证证据、真实未解决／未覆盖项、候选包与说明。没有额外全量审查、性能平台或多份交接报告。
