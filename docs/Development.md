# WinPool 开发约定

本文件维护技术所有权、数据含义和开发方式。产品范围归 [Product](Product.md)，V0.57 基线见 [收口归档](Archive/20260927-v057-closeout/README.md)，测试要求归 [Quality](Quality.md)。当前版本源仍为 V0.58、迭代 8；本机单盘真实修改第一阶段 H01–H11 及其工程门已完成，历史结果见 [CHANGELOG](CHANGELOG.md) 和 [Quality](Quality.md)。真实编辑接回原界面、范围采集及监控联动按活动 [V0.59 Plan](Plan.md) 实施，阶段未完整验收、未升版或归档。前轮普通 VD 原生 `UseMaximumSize` 已通过创建及实际容量后的自动布局；单 HDD 分层明确 32 GiB 的历史成功不外推 MAX。准确模板加原生开关被本机拒绝，WindowsAutomaticHdd 则实际创建普通 VD 而无 actual tier；后者经严格只读 UnexpectedEffect 对账终结 Failed，并由原界面另行清理。新分层 MAX 已在任何删除或创建计划前拒绝，准备、预检及调用入口保持零写入防护；分层原生 MAX 的技术调查继续按活动 Plan 推进，不能将这些结果泛化为永久 Windows 限制。前轮原生 MAX 运行的物理末态、保护比较、解散后的准确成员返回及普通重启／退出已独立核验；旧末态及工程门仍只证明各自历史快照。

原生 MAX 修正前的源码通过[统一 Release 工程门](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-223310/summary.md)：12 个测试项目共 1775 条，1772 Passed、0 Failed、3 NotExecuted，restore／build 成功且 0 warnings／errors，393 个源码文件 hash 前后一致、运行树 merge 无冲突。该历史结果仍有效，未执行项不计为通过，不代替本轮修正的工程门或实机核验。该构建已通过普通启动 RealOff／MonitorOff、无历史监控误报、真实 E: 与两块 Samsung 显示及原 Agent 托盘 Exit 两进程 exit 0；[退出结果](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-exit-result.json)与[退出后偏好／计数](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-after.json)保留准确现场。标题栏关闭 App exit 0、Agent 继续运行及后续正常重启也有[独立原生证据](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/close-repaired-titlebar-native-result.json)；这不是原生分层 MAX 创建通过证明。

[前轮原生 MAX 完整 Release 门](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-frozen-gate-20261009-095540/20261009-095540/summary.json)覆盖12项目，1866 Total／1863 Passed／0 Failed／3 NotExecuted，restore／build成功且0 warnings／errors、395个源码哈希项前后不变、标准运行树合并无冲突。[集中定向门](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/consolidated-direct-20261009-095138/commands.json) Infrastructure 440/440、App 166/166、Application 6/6通过，包含分层 MAX 零调用防护与准确 Primordial 成员修复。未执行项不计为通过。

前轮原生 MAX 运行的[独立原生／full对账](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-protected-comparison-review.json)为13项末态断言通过（14次只读查询）、15项review检查通过，采集均晚于全部写入完成：16个Storage来源Returned、非merged／非scoped、codec typedEqual，native与full身份及几何一致。WDC物理盘在线GPT，MSR offset1 MiB／size16 MiB，BasicData offset17 MiB／size4,000,767,279,104 bytes，NTFS／64 KiB／`WinPool_Test`／`E:`，无concrete pool或VD。Samsung限定结构范围0变化／0证据缺口，4项共享Primordial调整另存。原App11672／Agent24656托盘退出均0；[普通重启后的托盘退出](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/normal-restart-exit-exit-result.json)App16372／Agent4296均0，[重启后只读审计](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/after-normal-restart-readonly-audit.json)确认Accepted 214／CallIssued 271不变、无未终结操作、395源码哈希无变化、B0偏好全部一致，普通启动RealOff／MonitorOff并保留20 Hz设置。分层 MAX 的API冲突仍未收口。

最新[单 HDD 容量调查](Review/Single-Hdd-Tier-Capacity-20261009.md)经独立 pwsh 和修正后原界面同值创建 3,997,809,246,208 bytes（3723.25 GiB）实际 HDD 层；生产 Verified／fresh scope 与[后态断言](../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/after-fix-ui-same-capacity-assertions.json)一致，该值不称 MAX。[最新完整 Release 门](../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/Engineering/final-gate/20261009-155245/summary.json)覆盖12项目，1900 Total／1897 Passed／0 Failed／3 NotExecuted，restore／build零警告／错误、源码哈希稳定。大层采集与未知删除恢复的修正见下文；本次调查的WDC末态、Samsung限定结构0变化／0缺口、偏好及普通重启／退出已核验，证据见调查报告；分层原生 MAX 技术上仍未解决，不提前升版或归档。

## 环境与模块

C#、WinUI 3、.NET 10、Windows App SDK 2.4；SDK 以 `global.json` 为准。Windows 项目当前 TFM 为 `net10.0-windows10.0.26100.0`，SDK BuildTools 为 28000 系列。最低操作系统以 Product 为准。

| 模块 | 所有权 |
| --- | --- |
| Domain | 稳定标识、单位和无副作用的存储规则、容量计算 |
| Application | 存储事实模型、用例契约、编辑意图、操作规划和表现投影 |
| Execution | 类型化计划/步骤、执行策略、风险和前置条件、结果与回放；真实执行仅接受 Agent 冻结且通过权威门的封闭计划 |
| Inventory / Monitoring | 采集与监控契约、适配接口及各自数据模型 |
| Infrastructure.Windows | 固定只读 Windows 采集、真实操作实时规划／安全预检／封闭步骤适配、现有模拟协调与系统仓储适配 |
| Infrastructure.Sqlite | 事务、仓储、真实计划及逐步事件持久化、数据格式实现 |
| Ipc / Agent.Client | IPC 13 封闭的 App–Agent 请求、连接和结果传播 |
| App | WinUI 页面、输入、呈现和交互；不自行实现存储规则或写 SQLite |
| Agent | 每用户可见托盘进程、采集/监控协调、真实操作会话门与后台编排、SQLite 写租约和进程生命周期 |

依赖保持表现与适配层 → Application → Domain 的现有方向，Execution 与 Inventory 等边界按现有项目引用验证。优先在现有项目内拆分职责，不新增通用引擎项目、DSL、插件体系或公开 SDK。

真实操作请求在 App 中只是类型化提案。Agent 先核实调用进程仍存活、PID、启动时间、映像路径和会话，再从 Windows 重采并冻结计划、准确目标指纹、完整物理成员关系、能力证据及哈希。接受计划须持有绑定该计划、目标和会话的短时一次性 token；Agent 在接受前和每步写调用前重新核对机器、管理员状态、目标身份、磁盘与池角色、BitLocker、运行依赖和适用能力。准备以 `PreparationId` 与会话／意图摘要幂等，步骤结果写入核心 SQLite，长操作在后台执行，App 按 `OperationId` 查询；断流或退出不强杀已接受作业。缺服务、能力、唯一关联或安全事实时拒绝真实操作。

真实计划只包含封闭的类型化命令。RAW 磁盘初始化 GPT 后必须重新采集，识别 provider 可能自动生成的 MSR；若需规范化布局，使用独立计划准确删除/创建 MSR，再创建 BasicData，不能在同一阶段暗中假设零分区。MSR 安全属性由原生 GPT 布局读取补证；卷的文件系统经只读 VDS 查询，BitLocker 状态按精确 Volume GUID 查询。虚拟磁盘、OS 磁盘和分区通过完整父子关联核对，不只依赖名称或 DiskNumber。缺失、冲突或不完整证据一律拒绝。

结构页真实创建和重建使用原表单、拓扑拖放及同一草稿，不另开参数向导或独立重建入口。`RealStructureDraftPlanner` 从基线与目标的净差异生成封闭动作，`RealStructureEditorFlow` 将其拆成独立 Prepare／冻结确认／Accept 计划。已知布局能力先只读查询，拒绝后不提交删除；显式解散旧池后按实时物理盘和直接 OS 磁盘关联复核，如仍有 GPT/MSR，须单独清至零分区 RAW 并复采，再创建池、可选 HDD 模板和 VD。GPT 初始化与自动布局分别确认。取消、失败或结果未知停止后续计划，不自动接受、不重放不确定调用；普通更新失败不能暗中转为删除重建。

`EditorPageBase` 在接受后最多轮询 1200 次，每次先等待 250 ms，再查询同一个 `OperationId` 的持久状态；等待间隔合计约 300 秒，另有查询与调度耗时。这不是操作完成时间保证。查询不再次发送 Accept，也不自动接受后续计划；超过窗口保留准确操作身份，后续只读查询和对账，待核实终态后才可重新准备必要计划。

`SequentialPartial` 和 `OutcomeUnknown` 都会停止后续 Windows 写调用。Agent 只做只读采集和持久状态对账，不重放可能已成功的调用；需要修复或继续时，先确认现场状态，再生成新的准确计划。单成员池创建要求正面证明当前池角色、完整成员集合及 OS/运行依赖关系安全；只凭单盘名称或候选身份不足以放行。真实计划每步重核目标、系统角色、BitLocker 和运行依赖；写入仍需产品内当次确认。当前单盘支持范围与 C03、C05、D 类、既有虚拟磁盘/层 MAX、多盘冗余、混合介质分层及新建 MBR 等限制见 [Product](Product.md)，本轮新增路径的验证状态以 [Plan](Plan.md) 和 [Quality](Quality.md) 为准。

单 HDD 分层创建的同步 provider 错误可经封闭的 `ReconcileFailedTieredCreationAsync` 只读确认未产生新对象：冻结单步计划／哈希、准确池／模板／物理盘定位、调用前 target evidence 和能力证据必须一致；provider 须确实返回创建错误，不能带创建身份、分区／磁盘定位或作业 ID。调用前与两轮 fresh 完整闭包指纹须一致，池仍只有该 unused 模板且没有 VD／OS 磁盘／分区／卷；两轮 storage-job 枚举须完整、新鲜、身份唯一且全部为终态，历史失败作业如实保留。`WindowsObservedNoEffectStepEvidence.WindowsCallIssued=true` 明确区别于写前拒绝的 `NoWindowsCall=true`。对账结果是 Failed，不是 Succeeded；任一事实不完整、变化或作业未终结均保持 Unknown，不重放调用。历史冻结计划只读恢复保持其原能力契约，不倒套新准备格式。[首次严格恢复证据](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/hddmax-provider-uncertain/audit-observed-recovery-positive.json)与[后续 provider 限制调查](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/hddmax-joint-range-provider-limit/review.md)分别记录恢复和容量限制。

Agent 的 live `RunStepsAsync` 与启动／Query 恢复共用 reconciled-step 持久化：先核对准确步骤集合、合法状态迁移与非空终态 evidence，CAS 写入步骤，再读取 durable steps 计算 operation 终态。已持久终态不能被相反观察覆盖；步骤 CAS 失败或 evidence 冲突保持／转为 OutcomeUnknown，terminal CAS false 不再当作成功或永久留在 Running。并发 Query 只启动一个正在进行的只读 reconciler，不重复调用 provider。服务与实际 SQLite repository 的 typed fake 回归[23/23 通过](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/agent-runtime-reconciled-step-persistence/review.md)，包括 live provider.error→observed NoEffect→step Failed→operation Failed、已 Verified 前缀的 Partial、CAS 拒绝与后续 Query 恢复；这是定向证据，不能代替最终工程门或实机创建成功。

真实 backend 的安全检查、执行前后和对账均重新采集。`operationScopes` 只保存准确 selector，不缓存事实；尚无安全 locator 时以 `initial-safe-locator` 采集 full，生产 provider 支持 scoped 后，每次后续检查各取新的 scoped 事实。失败、不完整或合并后的事实不能成为安全 oracle，不静默退回 full 或复用上次结果；重启对账从 durable 的准确物理 selector 恢复范围。明确不支持 scoped 的旧只读适配/测试源仍逐次 fresh full，并记录 `source-scoped-unavailable`。

卷后态按自身 UniqueId／ObjectId 和独立父分区链匹配，不要求 Volume 具有父分区的 Guid 字段。单步 RenameVolume 的只读恢复须同时核对冻结计划哈希、调用前准确目标、成功 Provider 证据、当前身份／父链／卷标和安全事实；不能重放调用或续执行其它步骤。2026-10-06 用户已执行的 WDC E: 改名经此路径完成日志核对，证据见 [Quality](Quality.md)。当次异常恢复的受控停止另获用户批准；已完成阶段的退出与替换规则见[原阶段计划](Archive/20261007-real-edit-stage1/Plan-history.md) 第 7.3 节，现有 WDC 持续授权见 [AGENTS](../AGENTS.md)。

进程实例核验除映像路径和启动时间外，必须确认进程仍在运行。在同一具有 SYNCHRONIZE 权限的句柄上，以零超时 `WaitForSingleObject` 在身份读取前后检查存活；只有 WAIT_TIMEOUT 可接受，已退出或等待失败均拒绝。退出进程的内核对象可因其它程序保留句柄而继续存在，仅能读取路径／时间不证明存活。Windows 语义见 [进程终止](https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process)与 [WaitForSingleObject](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject)。

Agent 的退出信号覆盖全部 App 启动目标，包括独立 `--page Welcome`。主窗口的标题栏关闭、Agent 退出信号和提权交接进入同一幂等 `RequestClose`：`AppWindow.Closing` 首次取消原生关闭，在窗口仍存活时停止 UI 事件／通知计时器、取消启动工作、关闭所属欢迎窗并等待监控观察器停止，再保存工作区、断开监控客户端，最后放行原生关闭。`Closed` 仅记录已关闭，不在销毁后等待并继续操作 XAML。普通关窗不停止 Agent 后台业务；Agent 原托盘 Exit 仍走后台有序关闭；提权交接沿用已保存状态及旧 Agent 的关闭职责。此前 WER 异常退出没有托管栈，不能将某条推测调用链当作已证实崩溃栈；修复后的普通关窗与托盘退出分别按原生进程 exit code 核验。

本机库存与真实预检通过 Agent 的同一持久身份解析器绑定 SystemId。真实事实仍逐次从 Windows 新采，只重绑定文档及来源事实的系统 ID，不合并缓存观察；外机、模拟或原始文档／来源 ID 不一致时拒绝，提案的系统 ID 校验保持严格。界面在准备期间占用提交状态，绑定冻结计划后按准确操作 ID／哈希保留禁用；接受回复不明、查询失败、切页或切模式都不解除。取消最终确认会请求取消该准备计划，只有 Agent 返回匹配且无需对账的终态才解除；Agent 在重启恢复发现未终结真实操作时拒绝重新武装。

`TopologyLayoutEngine` 的布局决策归整数单位计划，像素/DPI 只负责最后映射；不把容量业务规则放入布局算法。修改布局算法时按需读[踩坑记录中的布局案例](Reference/开发踩坑记录.md#布局重构)。硬件页按来源对象展示，不以旧 13 类、154 项为数量契约。旧报告工厂和原始快照解释器已退出构建，有效 CIM/WMI 与原生补充读取保留。

## 存储事实、草稿和操作

共用一套存储对象含义，区分采集事实、编辑草稿和操作结果；不复制三套完整模型。

| 含义 | 约定 |
| --- | --- |
| 身份 | 内部稳定 ID、系统 ID 和提供程序定位信息分工明确；盘符、名称、列表顺序、DiskNumber 不能单独作为持久身份。名称变化不得改变身份、关系或目标定位；缺少可靠 ID 时不以名称猜测跨采集关联。保留稳定性/未知标记 |
| 实体 | PhysicalDisk、StoragePool、StorageTier、VirtualDisk、OS Disk、Partition、Volume 各有明确身份；分区描述几何和分区类型，卷描述文件系统与挂载。无卷的分区也是合法事实 |
| 关系 | 来源事实中的对象关联是唯一事实源；通用关系图、导航和显示是其派生投影，不各自维护另一套可修改关系 |
| 显示对象 | 模拟层、模拟池是统一层派生对象，沿用真实层/池的适用属性结构，但没有来源字段和容量；不得进入来源事实、持久化或真实命令目标。详细产品语义见 [UnifiedModel](UnifiedModel.md) |
| 未知 | 未采集、读取失败、不支持、否、零、空集合分别按含义表达；不得把缺失状态静默补成健康、可写或无系统角色 |
| 草稿 | 记录用户意图和基线修订。临时输入不完整不等于允许生成非法模拟文档 |
| 操作 | 预览、校验和提交共用类型化意图/计划；模拟整批原子提交，真实按 fresh 事实分段冻结确认；命令文本不反推执行 |
| 结果 | 区分成功、明确失败、结果未知和修订冲突；传输失败不能证明存储未改变。以 Agent 的持久化记录对账 |

`SimulationEditingSession` 是结构页模拟与真实草稿的共同会话，保存 SystemId、Baseline/Revision、Working、`PoolEditIntent`、MAX 意图、undo/redo 和冲突/未知状态。模拟仍生成同一批类型化步骤并原子提交；真实路径只比较最终关系和参数，不执行模拟命令，也不将成员拖放翻译为 `MovePhysicalDisk`。已有成员只有在同一草稿显式解散旧池后，才可分配给新 replacement 草稿池。无成员的真实空池只保留输入，不生成创建计划；不支持的既有对象改动必须列明阻止原因，不能静默忽略。

拓扑草稿拖动统一使用同一 `XamlRoot` 内的 pointer capture，不依赖管理员进程受限的原生 OLE 拖放。左键移动达到 8 DIP 后高亮命中的编辑目标；释放只调用既有草稿关系入口，仍检查当前编辑状态和成员指派规则。Escape、capture lost 或控件卸载取消拖动；非目标释放不改变草稿，拖后抑制误触选择。2026-10-08 的[管理员实机回归证据](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B3/internal-drag-success.jsonl)确认 WDC 从 Primordial 拖入普通新池草稿，单次释放只回调一次、成员仅包含 WDC、Apply 可用。B3 原 Apply 已完成[仅建池](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B3/pool-only-retry/2fe1658c967b417e876ce51e45745dcb.json)及[已有空池首块普通 VD、32 GiB](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B3/first-ordinary-vd32/17ca0eea11d9468e85b05f279cb217e0.json)，后者[实测为 RAW、零分区](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B3/first-vd32-raw-proof.json)；旧普通 MAX 意图已通过[原界面末态核验](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B3/ordinary-max-native-assertion.json)，单 HDD 分层 32 GiB 已通过[创建、自动布局与重建末态核验](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B4/hdd32-final-native-assertion.json)。旧分层 MAX 意图被转换为显式 `StorageTierSizes` 后遭 provider 拒绝，不能证明原生 `UseMaximumSize` 不受支持；本轮普通原生调用已通过，分层两条原生候选未满足实际层目标，新分层 MAX 请求已阻止。修正前 B5 物理盘已按选定目标恢复，[原生只读末态核验](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/final-native-assertions.json)确认联机 GPT、规范 MSR、NTFS／64 KiB 整 MiB 最大数据分区、`WinPool_Test`／`E:` 且无 concrete pool 或关联 VD。该次完整来源对账、重启及正常退出已核验；本轮普通原生 MAX 和新物理末态的独立结果见下文，仅分层 MAX 处理方式仍未收口，不据此宣布 B4 全部通过或升版。

`PoolEditIntent` 集中承载 Ordinary/单 HDD 分层、名称、明确 bytes/MAX、独立自动 VD/分区偏好及适用的 MSR、文件系统、簇、卷标、盘符等创建参数；默认普通/MAX。当前真实单盘创建固定受支持的 Simple、Fixed、1 列、65536-byte interleave，不能因表单存在其它选项就放行。真实 VD 草稿占位不填估算容量作为授权；普通池的页面投影隐藏模拟 tier 脚手架。已有对象名称 Enter 独立走准确改名计划，失焦不提交；`AcceptRename` 同步当前草稿及 undo/redo，改名不随结构放弃回退。待创建名称仍属创建参数。

真实 single-HDD tiered VD 的顶层 Provisioning／Resiliency／Columns／Interleave 可能为来源明确 Returned 的 null。封闭 supported 判定保留这一原值，仅在同次 fresh SourceFacts 准确关联到唯一实际 tier、该 tier 与 pool／VD 父链完整非 retained、唯一 HDD 成员及 Fixed／Simple／1 列／65536-byte interleave／copies1／redundancy0 和 size／allocated／footprint 一致时支持解散与 `PendingAutomaticLayout` 续行。unused template 与 actual tier 分开核对；缺失／读取失败字段不得借 Returned-null 补默认，多 tier 或不完整关系仍拒绝。[真实 SourceFacts 纯规划与 codec 证据](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/native-tier-draft-gates/review.md)避免用合成非 null 顶层规格掩盖现场形态。

分区工作区是显示投影，不能以共享 Primordial 的旧成员列表决定是否隐藏已释放的准确物理 OS 视图。先排除任何 concrete pool 成员或非 Primordial／未知 pool 引用，再保留原 Primordial 入口；列表尚未刷新时，仅对现有可靠物理对象与唯一 direct OS 映射补充显示，不按 ID 前缀、名称或盘号推断。孤儿、不可靠／重复身份及重复 OS 映射不补；既有 VD 入口保留。它不修改 SourceFacts、池成员或 CanPool，不以关系缺失授予真实操作，fresh 安全门保持不变。解散后的必要形态与两块 Samsung 保留等[42 项投影回归](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/post-dissolve-physical-display/review.md)已纳入上述统一门；固定 scoped 查询不扩张共享容器或兄弟盘。

预览用 `ForkPreview` 保留原生 SourceFacts、基线身份和修订，只替换候选草稿；不能用模拟来源重种本机基线而丢失 MSFT 类型/分区角色。外部刷新冲突保留草稿和历史并阻止写入；页面不能盲目 Bind。内部已 Verified 段经 fresh 采集后用 `RebaseVerified` 推进同系统基线，保留剩余目标、清除不可回滚的旧结构历史，不清 Unknown 屏障。`BindingGeneration` 隔离 Bind/Discard 后的新目标与旧写入的迟到映射。Apply 仅在全部目标已 Verified 后 Bind 清草稿。

创建身份只能由本次 Verified 输出与 fresh 可靠来源共同映射，不能按名称或盘号猜测。会话内 `VerifiedVirtualDiskId` / `PendingAutomaticLayout` 与 `RealAutomaticLayoutProgress` 保留原始参数、准确创建输出和已 Verified 前缀；再次 Apply 只准备第一个尚未核实的必要布局步骤。fresh 分区须匹配准确 ID、OS 父链、几何及原始 Role 的 GPT 类型字段；替换、额外分区、外部改动或未知结果均阻止续写。provider MSR 删除/规范化与单步创建 MSR 仍使用 `InitializeDisk` intent 及适用的准确 OS/MSR targets，保留严格初始化门禁，不能拆成普通删除绕过核验。

写入已 Verified 而结果刷新失败时，保存本会话的 pending refresh；下一次 Apply 先只读刷新并 map/rebase，不重写已完成段。目标全部满足且无阻止、Unknown 或冲突时才结束；若草稿代次已变化，只核对旧结果并标记冲突，不将旧映射套到新目标。上述草稿、布局进度和 pending refresh 为内存会话状态，不承诺跨重启恢复；持久 OperationId 对账与结果未知屏障独立有效。

相关刷新、提交和草稿 Apply/recovery 对 `InvalidDataException` 设置明确的受控边界；该异常不属于 `IOException`，不能只靠后者的过滤处理不完整采集或无效文档。scoped 刷新遇到此异常保留 `ScanError`、发布通知并返回失败，不将 incomplete 视为成功，不让异常逃逸到页面的 async-void 事件。已观察的 Unknown 仍保持提交屏障与剩余目标，不能因此重放写入；其它未约定编程异常不被通用 catch 吞掉。[局部异常边界证据](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/scoped-refresh-invalid-data-boundary/review.md)保留当时 helper／提交流程回归与原界面待验的边界；后续单 HDD 32 GiB 原界面核验属于独立设备证据。

读取遇到超出编辑范围的 Windows 结构时保留原貌及只读原因，不自动“修复”。新增或更改结构必须通过本次操作适用的规则；对无关未知对象不得仅因其存在而阻断其他独立对象的只读展示。

## 规则与容量

规则统一入口，内部按对象/操作分组。合法性规则输出允许、拒绝或信息不足，并携带稳定原因、受影响对象和依据/适用条件。App 用同一规则和模拟操作服务完成表单反馈、动作预检查及内存候选文档；UI 禁用不是校验边界。模拟提交到 Agent 时系统校验明确标记为 `SkippedForSimulation`，Agent 不重跑整套业务规则，只执行封闭请求、格式/哈希、修订、CommitId 对账、事务和单写入方等提交保护。真实执行由 Agent 重新检查实际 Windows 状态，不能信任模拟规则或 App 提案作为实时证据。

规则依赖必要的 OS/SKU、提供程序、布局、介质、用途、扇区及能力信息；只定义当前操作需要的字段。不把本机能力偷偷套用到导入系统。未知组合不默认放行，不为通过旧样例而放宽规则。

容量至少明确原始物理容量、逻辑容量、已分配/物理占用、可用范围和估算来源，不能用同一个值替代。计算使用整数 bytes 和溢出检查；单位转换只在输入/显示边界进行。

真实新建容量与布局支持是两种只读查询。`ReadStructureCreationSupportAsync` 核对准确物理目标及普通/分层所需的已知 provider 能力，供破坏旧结构前拒绝已知不支持布局；它不预测未来池的容量。创建池/模板并 Verified 后，明确 GiB 才通过 `ReadVirtualDiskCreationRangeAsync` 针对当前准确对象读取范围：普通 VD 使用准确池 `GetSupportedSize(Simple)`；single-HDD tiered VD 只读取准确 unused HDD template 的 `GetSupportedSize(Simple)`，要求模板 UID／ObjectId 唯一匹配，按该层自身范围验证明确 bytes，不依赖普通 VD 池范围或其 1 GiB 步长。`RealVirtualDiskCreationRange` 以 provider 离散精确 bytes 集合，或 minimum/maximum/origin/divisor 连续约束判定明确 bytes 是否受支持。普通 MAX 独立保留 `UseMaximumSize=true`，不调用 `ResolveMaximum` 生成预测 bytes；固定脚本调用 Windows 原生开关，实际容量创建后再核验。布局能力和安全证据仍来自准确目标的 fresh 读取，不能以显示用合并缓存替代。查询结果不是冻结计划授权，Agent Prepare/Preflight 仍重新核实当前身份、角色和适用能力，明确 bytes 还须重核 provider 范围；新建 MAX 不解除既有 VD/层扩展限制。

层明确容量的 Prepare、每步 Preflight 和固定 adapter 写前核验均重读当前准确模板的范围；连续范围按 minimum + k × divisor 检查，不将普通 VD 池网格强加给 `StorageTierSizes`。缺字段、调用失败、非法数值或层范围不接纳冻结 bytes 时拒绝；有效范围也不是容量预留或创建必成功的 dry-run。历史 `exact-pool-new-size:`／`PoolCreationSize` 字段仍可读取，不再作为该分层路径的容量门；旧计划、hash 及失败结果不重写。旧[交集实现与定向回归](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/hddmax-capacity-intersection/review.md)仅保留当时实现记录，不能作为现行分层容量约束。普通容量规则及分层原生 MAX 拒绝门不变。

旧实现曾把本机 pool 与 HDD template 交集最大值 3999688294400 bytes 作为 `StorageTierSizes` 显式容量调用，并被 Windows 拒绝；这与本轮 Windows 自行返回相同数值是不同调用合同，数值相同不能证明预测上限正确。明确分层 32 GiB 的成功也不外推 MAX。原生准确 `StorageTiers` + `UseMaximumSize` 操作 `a11ca8d034414b4aa275bb2d62625981` 被本机以参数互斥消息拒绝（与官方48010定义一致，原导出未保留数值字段），经严格只读对账终结 Failed／ObservedNoEffect，未重放。WindowsAutomaticHdd 候选只传 `MediaType HDD` 与 `UseMaximumSize`，不附 `StorageTiers`、`Size` 或 `StorageTierSizes`；模板仅为冻结的身份／规格约束来源，不是 provider 克隆输入。本机操作 `b0ca4d950b2c43dcb6c936b33ca40730` 实际返回普通 VD，没有 actual tier，不能依据通用文档宣称该机制保证实际分层。旧计划缺省 ExactTemplate、旧 hash／明确容量语义保持；机制参与新 hash、摘要与 provider input receipt。新分层 MAX 已在草稿、Prepare／Preflight、adapter 和固定脚本调用前拒绝；固定脚本仅保留明确容量的 ExactTemplate／StorageTierSizes 创建路径。旧 DTO／枚举／JSON／hash 与只读恢复保持兼容，普通原生 MAX 不受阻断。分层原生 MAX 的技术调查继续按活动 Plan 推进，既不自动 fallback，也不宣布所有现代 provider 永久不支持。旧[只读方法与参数对照](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/hddmax-joint-range-provider-limit/review.md)保留当时调查边界。

大层的 `GetPhysicalExtent` 本机单次约42.6秒、返回14,893条记录，曾使安全采集超过60秒。现仅在 fresh 唯一实际 tier→VD→concrete pool、sole pool member 与 `Get-PhysicalDisk -VirtualDisk … -HasAllocations $true` 的准确 UID／ObjectId 一致，tier／VD 六个 Size／AllocatedSize／FootprintOnPool 均为正 UInt64 且相等、缓存0及支持规格完整时使用严格成员关联快路径。VD 聚合字段必须存在，Returned-null 保持原值并以实际层完整规格核对；缺证、冲突、复杂布局或查询异常回到原 extent 路径，不猜成员、不用缓存。新代码生产只读采集约10.53秒，包含 Storage 与 Full 的另一测试约12.96秒；这是本机采集测量，不是编辑端到端性能比例，详见[调查报告](Review/Single-Hdd-Tier-Capacity-20261009.md)。

旧未知 DeleteVD 只在准确目标仍在、两轮完整 fresh 闭包及冻结指纹一致、安全 RAW 零分区、实际层／池／成员关系完整且两轮 jobs 全终态时，可只读落盘 Failed／ObservedUnchangedDelete／NotVerified；终态 jobs 不证明 provider 返回或 NoEffect，原 CallIssued 与 `adapter_exception` 保留。已有 worker 未结束时不另起 reconciler。缺证仍 Unknown；后续删除须另建确认计划。[本轮生产 Query 收据](../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/Engineering/closed-delete-query-recovery-host/execution-receipt.json)证实 adapter0、无旧请求重放；原界面随后另建删除计划。未来适配器调用前的 fresh 捕获失败在该调用边界保存 NoCall 证据，不能倒推旧未知调用未发生。

前轮普通原生 MAX 操作 [b085439f8cd645bb9f65512503f23359](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Ordinary/create-native-maximum/b085439f8cd645bb9f65512503f23359.json)冻结 `SizeBytes=0`／`UseMaximumSize=true`，由 Windows 决定容量，未以 provider range 最大值或估算 bytes 调用。实际 VD／唯一 OS 磁盘容量均为 3999688294400 bytes；VD 为 Simple／Fixed／1 列／65536-byte interleave，没有模板或 actual tier；随后按实际 OS 容量确认 GPT、规范 16 MiB MSR、BasicData／NTFS 64 KiB／`WinPool_V059_Data`／`W:`。见[只读布局证明](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Ordinary/native-maximum-layout-readonly-proof.json)与[断言](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Ordinary/native-maximum-layout-assertion.json)。[原 Agent 托盘 Exit](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/ordinary-native-max-observed-exit-result.json)确认 App4860／Agent4936 均 exit 0。这验证本机普通原生 MAX；该轮物理盘收口另有上文独立证据，不能由普通成功推定分层 MAX 通过，也不代替本次容量调查的最终恢复核验。

开启 AutoPart 时，已知的显式 bytes 在草稿 preview 阶段即由 `RealOperationProposalFactory.ValidateAutomaticLayoutCapacity` 验证，先于解散旧池或其他写入；非整 MiB、或不足以容纳所选 MSR 与 BasicData 几何的目标被阻止。blocked 草稿保留输入和说明，但 `RemovedPools`、`RemovedVirtualDisks`、`RemovedTiers`、`Creations` 等类型化执行集合为空。原生 MAX 不存在预先解析的准确 bytes；本轮修正要求创建 Verified 后，以唯一 fresh 实际 OS 磁盘容量准备后续 GPT／MSR／BasicData 步骤，不预冻结虚构容量的分区几何，不为满足布局改小 VD。后续容量或几何不受支持时保留已核验结果与剩余目标并停止续写。`ConfigureInitializedDisk` 与明确容量前置校验共用几何 helper，并调用 `EditWorkspace.GetRealPartitionCreateGeometry`；关闭 AutoPart 时不附加这些分区布局条件。

原生 MAX 的类型化命令区分 `UseMaximumSize=true` 与明确 bytes；兼容占位 `SizeBytes=0` 只允许与原生开关同时出现，不能作为实际容量或旧计划的隐式 MAX。模式参与计划 hash、确认摘要和 provider input receipt，旧明确容量计划与恢复证据不重新解释。普通路径要求本次可靠 VD 输出与唯一 fresh OS 磁盘容量一致；分层路径还要求唯一 actual tier、完整规格和非 retained 的 pool／member 父链。缺 actual tier 必须停止后续布局，不能将普通残留改称分层成功。

b0 的严格只读对账以两轮完整 fresh 相同残留指纹、原物理成员指纹、准确 provider-returned VD／OS 身份、RAW 安全标志及终态 storage jobs，收口为 Failed／ObservedUnexpectedEffect，明确保存 WindowsCallIssued、NotVerified、ResidualObjectsPresent；不是 NoEffect、Verified 或更改目标。标准运行树的 Unknown build guard 未被绕过：临时封闭 host 复用已有生产 Agent 查询服务和 CAS repository，注入所有 Execute 硬拒的 adapter，RealOff、adapter attempts0、reconcile1，原 Accepted／CallIssued 边界事件各1保持，见[独立恢复审核](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/closed-query-recovery-host/root-verification-independent.md)。原 App 查询准确终态后，另行确认的[残留清理计划](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Tiered/cleanup-observed-residual/99f2ccc16d9a42adb362b0624c9c6389.json)已完成删除 VD／模板／池。该 host 是本次恢复证据，不是永久写入旁路；没有 SQL 手改状态、重放创建或自动降级。该恢复、后续完整工程门和前轮原生 MAX 运行的物理末态已有各自证据；活动 Plan 仍因分层 MAX 的实际 API 冲突未收口。

真实 GPT 创建统一使用 `EditWorkspace.GetRealPartitionCreateGeometry`：选定空隙与磁盘尾部 1 MiB 预留区取交集，再按 1 MiB 对齐。手动 MAX、自动布局和 Agent 校验共用此计算，保留相邻分区重叠检查；模拟几何继续使用自身规则。准备阶段的能力拒绝返回 `agent.real_operation.prepare_failed`，没有准备计划或写调用，不能误记为执行结果未知。

分区页联机状态只读准确 SourceFacts 的 `IsOffline`，要求 Returned 的有效布尔值，不用默认值代替未知证据。RAW 初始化 GPT 后复采，再按 MSR 偏好规范化；不自动创建 BasicData，已 GPT 不另给“完成布局”，真实 MBR→GPT 仍按 C03 拒绝。真实 clear 需要完整当前分区事实，成功空集合才可证明零分区；真实清空清除所选盘分区并置 RAW，不等于全盘写零、格式化或初始化。模拟清空使用独立 `SimulationEditKind.ClearDisk`，只移除选定模拟盘的分区/关联卷并置 RAW。

- 保留适用的采集值及来源；改名等无容量影响的操作不重新估算。
- 成员、布局或分配相关参数改变后，使受影响的容量/能力证据失效，重新估算并标记，保留未受影响事实。
- 模拟 Simple、Mirror、Parity 分别按布局、数据副本、列数和校验列折算理想逻辑上界，排除热备与退役盘。MAX 与默认创建值向下对齐到 4 GiB，不再扣旧 1% 余量，也不以 Interleave 代替容量粒度；这不是 Windows 通用保证。混合介质逐层计算，物理占用与逻辑容量分别保存。
- 理论估算、保守规划值和真实采集值区分。模拟应用后仍是估算；真实创建完成后才由重新采集结果更新。
- 200G 成员合计、UseMax 实得约 199.86G、规划预留至约 198G 是用户提供的示例，不是 Windows 的固定扣减公式。当前估算策略见本节，行为验证按 Quality 执行；调整策略时在对应任务或阶段计划中明确范围。
- 容量余量不能替代合法性，也不保证真实操作成功。无法建立保守估算的组合返回尚不支持，不制造精确数字。

新增字段按一条链路完成：采集来源 → 原始数据 → 规范化模型 → 规则/模拟 → 持久化与 IPC → 界面/导出 → 往返和缺失值测试。字段的来源、单位、缺失含义和失效条件跟定义就近维护，不再分别维护大型字段副本。

修复缺陷时，将失败场景补成回归测试，检查实际入口的最终结果（按需覆盖连续操作、重载和失败恢复）。后续修改相关功能时重跑；预期以当前需求为准，不把旧样例固化为产品边界。

## 统一事实与采集链路

采集通知使用同一套状态文案。Agent 的 Started、Updated、Failed 事件带自动来源标记；自动阶段由 `LocalInventoryObserver` 转交通知，成功仅在文档验证并应用后呈现。手动请求仍由请求/响应路径通知，包含连接或传输失败；事件不再重复通知手动采集。自动存储、自动完整硬件及手动刷新使用独立进度键；失败、断流或重连清理未完成的自动进度。采集事件与监控诊断共用严格版本握手；当前 IPC 为 13，App 与 Agent 须成套更新和重启。

`WinPoolFacts` 保存带来源、时间、类型、读取状态的原始对象及关联；`WinPoolSystem` 与 `StorageSnapshot` 是只读派生结果。跨来源选值由 `WinPoolSourceDetails` 维护，只有明确等价的字段参与备用或冲突判断。缺少安全字段、关联冲突和数值超范围不能变成允许；所有采集字段保持原值，不设置脱敏状态或按隐私开关裁剪。

显示用假池、假层由 `SyntheticStorageProjection` 从当前快照派生，使用独立的 `SyntheticStorageObject`，不为它们构造 `WinPoolSourceObject`。`WinPoolSystem.SyntheticStorageObjects` 与有来源的 `Objects` 分开；管理列表、拓扑、属性及导航共享同一派生规则。快照经 `with` 修改成员或用途后重新派生，不能携带旧的缓存分组。假对象使用 `LogicalGroup` 操作身份，并在命令和模拟编辑入口拒绝作为存储修改目标；原始字段区域单独显示“无原始来源”。既有编辑器用途操作仍针对成员磁盘，不针对假层。

真实层容量呈现统一使用 `TierCapacityText`，根据来源字段读取状态区分缺失与零，不以成员合计回填。只有新建草稿的规划默认值可以按既有规则计算；该默认值不属于统一显示用模拟层的容量。

Storage、Hardware 与 scoped Storage 使用独立刷新用途，由 Agent 同一串行入口协调。范围契约携带 SystemId、OperationId、StepId、类型化 StorageObjectId 目标、准确 before locator、before closure 和 generation；原始 UniqueId/ObjectId/Guid 从 Agent 权威事实解析，调用方不能用任意 before ID 扩大删除覆盖。结果保留范围、起止时间及按来源的覆盖/完整性；只有完整覆盖域中的 absence 可移除对象。失败、不完整的范围保留旧事实、旧关联和观察时间，范围外对象/关系保留。full 与各 scope 分别记录状态，按 generation 和观察时间拒绝旧结果；迟到 full 不覆盖更新的 local 事实。层成员没有可靠关联时显示归属未知，不按介质相同猜测。

`WindowsHardwareInventoryProvider` 的 scoped 路径在 Windows 来源查询层按准确 provider 身份采集目标和关联闭包，不先 full 再过滤；系统角色、分页/转储和挂载依赖仍按安全需要只读采集。固定脚本继续内嵌、经标准输入执行。删除对象的 before locator 和 Agent 持有的历史闭包只用于查询与 absence 覆盖，历史关系不提供执行安全或采样绑定的新鲜证据。`WinPoolFactRefresh` 的合并事实标记 `IsMerged`，fresh safety reader 拒绝合并事实及不完整 scoped 结果；显示/选择恢复可以使用保留事实，真实安全检查必须另取 fresh 事实。

池级 unused tier template 与 VD 实际 tier 的 CIM 父链不同：模板可直接关联 `MSFT_StoragePool`，实际 tier 可只关联其 `MSFT_VirtualDisk`，再由该 VD 的准确 CIM 关联取得 concrete pool。来源闭包将已核对的 VD→pool 父对象加入同次范围，要求直接/间接证据最终指向唯一一致的池；缺失、多重或冲突父链均标记 incomplete，不按名称猜池、不退回 full 或借用保留关系。实际 CIM 父链与固定 Closure 的 13 项直接回归见[局部证据](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/scoped-tier-parent-fix/review.md)；该来源修复与实际 tier 的 fresh supported 判定共同支持了后续 32 GiB 原界面核验；不据此宣告单 HDD MAX 或 B4 全部通过。

局部采集保留准确物理盘父查询实际返回的 Primordial 对象及该盘的 `pool-member` 关系，但不将共享 Primordial 加入遍历队列，不查询其余成员。仅在本次事实没有 concrete pool 父级时记录该准确关联，不依据 `CanPool`、名称或盘号合成归属。合并保留范围外 Samsung 旧事实与关系；历史删除覆盖与新鲜返回关系分别处理，不用 merge 作为写入安全证据。直接回归及[原界面解散复验](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/scoped-release-original-ui-review.json)均通过：操作ae5a3bd3f5e8465cadcb1c116be27fba的generation145完整批次包含准确WDC与provider返回的fresh Primordial成员关系，Samsung来源及成员关系保留旧观察时间、未纳入当前范围扩查；删除的concrete pool／VD消失，原结构页已显示WDC。20项审阅检查全部通过，显示merge仍不作为写入安全证据。

局部采集与真实执行阶段在 `Diagnostics/operation-timing.jsonl` 记录 phase、operation/step、scope、captureCount、elapsedMs 和进程。provider、reader、step 和 UI refresh 存在嵌套计时；采集次数只统计实际来源调用，不把同一 capture 在多层日志重复计数。诊断时长不是安全核验凭据，也不保证跨机器或所有操作的性能。

三盘实际采样恢复后的六次原界面改名均已核验；同一入站卷标的第 2／4／6 次样本，接受确认至界面就绪的中位数从 18.4719994 秒降至 15.4372798 秒，减少 3.0347196 秒／16.43%，排除准备输入与人工等待，见[有效最终比较](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/monitor-restored-performance/verified-performance-comparison.json)。测量使用 20261008-221503 工程门的标准二进制（App PID 17736／Agent PID 15500），已包含采样恢复及退出修复，先于最终历史告警呈现修复；后者未改采样／执行路径，但未在 223310 最终构建重复六次计时。旧时窗有监控开启 20 Hz 证据，准确已选设备映射缺失；新驱动除输入值及 fresh DB 外，还等待 E: 卷标的拓扑 Group 可见，旧界面就绪判据较弱。因此这是单机三组配对诊断，不能外推普遍提速或完全同条件。旧 15.76% 测量的 WDC 实际样本为零，不再作为本轮最终负载比较；新目录的 `performance-final*` 是保留的无效准备产物，不可引用。

独立[诊断阶段审阅](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/monitor-restored-performance/diagnostic-phases-review.md)按六个准确 OperationId 与上述 App／Agent PID 双重过滤并逐行复核 126 条原记录：每次 5 条 scoped 来源采集，合计 30 次，另有 6 条 full 包装层标记。Prepare 等 full 底层记录没有 operationId，实际 full 总数仍为 missing；不按相近时间归属、不将包装层与来源重复计数，也不把 Execute 与其内部 provider／采集耗时相加。六次的 Prepare／Accept preflight／step preflight／provider／Execute／UI scoped refresh 中位数分别为 2703.192／2718.971／2197.511／1103.562／5546.675／2309.955 ms，嵌套阶段不相加为总耗时。

App 启动经 `ReadOnlyLocalInventoryReader` 以只读 SQLite 连接读取已提交的本机事实，不初始化数据库、不取得写租约；校验现有 schema、文档格式和哈希后先呈现历史。Agent 就绪后自动顺序执行 Storage、Hardware 两次采集，沿用同一串行采集/合并/持久化入口，成功落库后发送类型化库存事件，失败发送失败事件且仍尝试下一阶段。App 的 `LocalInventoryObserver` 在工作区初始化前订阅，初始化后接收阶段结果；晚连接与重连补读 Agent 缓存。UI 线程统一应用新结果，拒绝旧/重复回报，保留模拟选择；两页手动刷新仍触发各自用途的采集。退出、提权交接与切换数据根前取消并等待启动采集，避免继续写旧数据根。IPC 为 13，核心 SQLite schema 为 18。

scoped 请求回包与 observer 事件可按任意顺序交付同一已提交批次。catalog 仍拒绝旧/重复应用；真实编辑刷新另由 `RealScopedInventoryRefreshPolicy` 核对当前 active 文档是否已是这次完整回包：System/OperationId/StepId/targets、generation、起止与采集时间、UpdatedAt、快照身份及完整 SourceFacts 必须一致。报告已被 observer 先应用时，同批报告也可算本次刷新成功，不再要求本次调用改变 snapshot 引用；更晚不同 scope、同 operation 不同批次、retained-cache 或来源事实差异均不能授权续行。这只是显示结果的交付判定，不能成为执行安全 oracle。[竞态局部证据](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/scoped-refresh-observer-race/review.md)保留实机日志中未直接记录事件/回包应用先后的限制。

窗口恢复上次系统另走 `ReadOnlyWorkspaceStartupReader`：在同一个只读 SQLite 快照内核对 schema、工作区选择及其指向的唯一活动文档；模拟文档核对元数据与 SHA-256，本机文档核对缓存格式。首个异步偏好等待前隐藏系统内容，页面偏好加载后只对验证成功的文档做临时预显，标题栏显示系统名但暂不接受操作；损坏、缺失或旧 schema 则显示居中加载态。预显不写数据库，也不放开工作区状态持久化。Agent 连接后仍由现有仓储完整恢复目录与选择；只有确认后的编辑文档签名变化才重建已显示编辑页，最后放开页面和系统选择器。读取本次运行消息历史不阻塞首屏。此路径只优化可见启动顺序，不把预显当成真实存储状态或 Agent 的提交权威。

完整硬件刷新在既有 CIM/WMI 事实后追加 `WindowsGraphicsFactCollector` 和 `WindowsNetworkFactCollector`：前者以 DXGI LUID 保存适配器和输出，并用 D3D12 读取功能级别；同一 LUID 通过 D3DKMT 保存适配器类型标志、显示侧描述和渲染侧描述。`IndirectDisplayDevice` 为真时统一对象使用显示侧名称，保留 DXGI 原始描述，并禁止按相同 `VEN/DEV` 借用物理 GPU 的驱动和 PCI 位置。`Win32_VideoController`、`Win32_DesktopMonitor` 与 `WmiMonitorID` 在统一事实中属于字段补充，不形成第二组 GPU 或 Monitor 设备，驱动、型号和厂商仍可按可靠硬件标识补入 DXGI 对象。软件或间接显示 DXGI 适配器均不按标志或名称过滤。网络保持 `WinPool.NetworkAdapter` 统一来源键不变，内部以 `MSFT_NetAdapter` 的 `ConnectorPresent -or InterfaceType -ne 0` 作为对象集合边界，按接口索引关联全部 IP 地址与默认路由，并替换同次脚本采集产生的原始 `MSFT_NetAdapter` 观察。同一来源的成功刷新直接整组替换旧网络对象，不引入跨来源迁移规则。`WinPoolSystem` 是不持久化的运行时投影；入库的是来源事实，启动从来源事实重新生成统一模型，完整硬件在 Agent 启动第二阶段自动刷新，也可手动刷新。Monitor 不在报告投影中筛除，存储摘要不增加硬件页专用条件。

`HardwareReportProjector` 按统一对象类型完整投影 CPU、内存、页面文件、GPU、Monitor 和 Network；报告可以选择字段行，但不能按来源类名选择或丢弃某个对象。字段备用来源只用于补值，不改变对象列集合。`ManageSystemSummaryProjector` 为管理页与硬件页提供同一存储摘要。App 通过 `PropertyTableVisuals` 小范围复用管理页与硬件页的项名上限、项值上限、列间距、行高和单元格样式：两页项名列使用 `Auto` 宽度及 220 DIP 上限，项值列使用 `Auto` 宽度及 250 DIP 上限；硬件项名网格位于分段横向 `ScrollViewer` 外，标签行高跟随值行。外层纵向 `ScrollViewer` 包含左对齐操作、即时反馈和整份报告，不呈现采集完成时间。设备列选择、悬停、选中后居中和所选列文本复制沿用管理页语义，硬件页不再打开字段详情对话框。标题栏的 `ActiveSystemSelector` 复用现有系统切换入口；下拉选择先暂存，待 `DropDownClosed` 后再切换工作区和重建列表，避免在 WinUI 弹出层仍打开时使控件集合失效。不建立第二套事实模型或通用表格框架。所有字段保持原值；内部格式为核心 SQLite 18 / 监控 SQLite 2 / IPC 13 / StorageSystemDocument 3 / 来源事实 1。

`MainWindow` 的自绘标题栏行高 48 DIP，先启用 `ExtendsContentIntoTitleBar`，再设置 `AppWindow.TitleBar.PreferredHeightOption=Tall`。导航列表、真实编辑控件和可见的系统选择器容器是 Passthrough 交互区，剩余标题行由系统处理拖动；这些交互区加载或尺寸变化后排队重算物理像素矩形，避免语言和窗口布局变化后命中区域仍是旧坐标。系统选择器宽 260 DIP，容器及下拉项上限 280 DIP。App 自建按钮按角色使用两套尺寸：图标文字普通按钮以 `WinPoolButtonBaseStyle` 统一 4 DIP 圆角、32 DIP 最小高和内边距；行内单图标按钮以 `WinPoolInlineIconButtonStyle` 统一 32×32 DIP。按钮处在管理命令区或编辑操作区不改变其角色，也不派生第三套按钮高度。输入和下拉最小高 32 DIP，属性字段行采用 `Auto` 高度及 40 DIP 下限，文字换行时可增高；布局槽高度不是按钮高度。

内置模拟继续以 `StorageSnapshot` 作为编辑模型，但持久化前由 `WinPoolSimulationFacts` 生成 Windows 形态的来源事实。来源仍明确标记为 `FactOrigin.Simulation`，命名空间、类名、字段名、CIM 数字枚举、数组类型和 bytes 单位分别对齐 `Win32_ComputerSystem`、`Win32_OperatingSystem`、`Registry.CurrentVersion`、`MSFT_*`、`Win32_LogicalDisk`、`Win32_DiskDrive` 与磁盘角色补充来源。Partition、Volume 和 LogicalDisk 按真实来源拆分并用关系组合；模拟模型无法提供的 Windows 属性不伪造。系统版本号与 DisplayVersion 分开，系统卷使用 4096 bytes 分配单元，存储空间数据卷继续使用当前测试布局的 65536 bytes。

编辑状态由 `SimulationEditingSession` 集中管理。模拟结构、即时分区和改名共用 `SimulationEditRequest`、规则与类型化步骤；真实路径使用准确目标的真实提案及 Agent 冻结步骤。目标分组、用途、分区表类型分别使用 `DestinationGroupId`、`DiskUsage`、`PartitionStyle`，不得塞入 `Name`。模拟命令只解释步骤，未绑定 CIM 目标和无命令操作均明确说明，没有文本执行入口。

模拟格式化方式由 `SimulationEditRequest.QuickFormat` 携带，默认沿用快速方式；分区页两个互斥开关只决定此意图。计划与解释性命令预览保留完整方式的差异，当前模拟卷结果仍按现有 `StorageSnapshot` 字段生成，不增加介质扫描或真实 Windows 命令执行。系统导出保持现有 `StorageSystemDocument` JSON 结构，以 `.json` 扩展名写出；旧 `.winpool` 文件只作为导入兼容入口。当前编辑资格与几何边界见[模拟规则表](DesignTables/SimulationRules.md)；旧叙述稿已[归档](Archive/20260923-design-details/README.md)。

模拟新建分区以 `EditWorkspace.GetPartitionCreateGeometry` 计算可用范围：把起点向上吸附到 1 MiB 网格，可用长度向下取整到整 MiB；不足一个整 MiB 返回明确不可创建原因。分区页与结构页自动创建读取该几何，草稿规划先按同一粒度整理容量，规则校验及模拟提交再以共享几何核对，不能由界面独自夹紧超界容量。空盘勾选 MSR 时创建于 1–17 MiB；已有导入结构保持原始偏移及容量，不自动重排。

存储结构与磁盘分区编辑页右侧属性栏均从 320 DIP 宽开始，使用 8 DIP 可拖拽分隔槽调整左右比例；属性栏紧贴分隔槽，不再叠加 8 DIP 左边距。两页属性表单使用受栏宽约束的弹性标签／值列，不再强制 412 DIP 内容宽或横向滚动。分区属性行最小高 40 DIP、行距 4 DIP；容量输入与换算组成的复合行按内容增高。左下操作区的按钮槽最小高 44 DIP，实际高度随同行内容增长；窄窗可双向滚动且最高 220 DIP，结构页待处理列表内容最高 96 DIP，以保留拓扑视口。起点与终点分别显示两行：第一行准确 MiB，第二行有限小数的自适应单位；终点为 `start + size` 的排他结束偏移，不可得时显示横线。两页根外边距为 24 DIP，图形遮罩局限拓扑区域；视觉与窄窗结果另按 Quality 验证。容量输入下方的只读自动单位值使用普通正文文字样式。管理、监控和开发页的实际区域分隔槽同样为 8 DIP 且可拖；普通卡片之间的间距不创建分隔槽。

模拟分区扩缩的容量能力与提交共用 `StorageEditRules` 的建模计划。目标是总容量并按 1 MiB 对齐，依据保存的磁盘范围、下一分区边界和卷已用容量检查；分区与关联卷以同一增量更新，保留合法容量差，不将文件系统容量强制等同分区范围。该建模范围不是 Windows `Get-PartitionSupportedSize` 实测，不授权真实写入。

分区页扩缩入口使用同窗口串行对话框：扩展 `A MiB + B MiB = C MiB`，压缩 `A MiB − B MiB = C MiB`；A 只读，整数 MiB 的 B/C 双向联动，第二行只读显示自适应单位。单一 nullable long bytes 目标为权威值，避免递归事件和显示舍入后二次提交；当前 bytes 不是整 MiB 时拒绝，不把四舍五入值当起点。真实 UI 查询到的 provider range 仅作候选限制，Prepare/Preflight 仍 fresh 重读并拒绝过期范围；模拟沿用自身建模规则。已有分区的页面容量框仅显示当前值，不隐式改变目标。支持范围和验证结果见 [Product](Product.md)、[Quality](Quality.md)。

模拟分区删除资格只由所选分区自身的 Boot／System 标记决定，页面、管理页投影和服务端共用 `StorageEditRules.CanDeleteSimulatedPartition`：同盘的系统身份、分区类型（含 EFI、MSR、恢复）和所属系统盘都不再单独禁止删除，但仍不得删除标记为 Boot 或 System 的分区。格式化保持更窄的范围，只允许普通 Primary／BasicData 且非 Boot／System，不随删除资格一起放宽。

系统 JSON 导入在转换及保存前验证来源投影：分别拒绝已知负偏移、非正容量，以及可证明的加法溢出、超出所属磁盘范围和重叠；缺失或冲突的字段不作为已知值参与几何运算。按显式磁盘身份分组，不按磁盘编号猜测关联，也不把编辑器的新建限制套在合法导入结构上。容量估算溢出统一转为参数拒绝，保留原始来源事实。

内置模拟与导入模拟均可删除。`BuiltInSimulationCatalogPolicy` 只在首次成功初始化或显式恢复默认时补齐样例；`UserPreferences.BuiltInSimulationCatalogSeeded` 在持久化成功后设置，正常重载不重建已删除样例。目录可以没有模拟系统，此时使用本机选择。单项导入、转换及删除先完成仓储操作再更新内存目录；批量恢复默认逐项同步成功结果，避免失败重试使用旧修订。

自动创建虚拟磁盘、分区保存在 App 的 `UserPreferences`，结构页开关保留原位置；选择对象不修改偏好，偏好变更不反向增删已有结构。新建操作读取偏好，相关保存串行取最新偏好以避免快速连切丢失字段。前台文件选择器采用带窗口身份的 `Microsoft.Windows.Storage.Pickers`，不再用提升模式不支持的旧 picker；不因此新增管理员写入存储的权限。

## 通知与上下文帮助

V0.55 沿用 Application 的通知契约与 GlobalNotificationService，Presenter 负责本地化，App 负责布局和交互。人工反馈修复采用最多三张即时卡片，超出时移出最旧卡但保留会话 History，不提供合并或溢出入口；独立重复消息分开记录。普通卡 8 秒自动消失，错误卡 20 秒，普通卡点击消失，错误卡点击显示消息对话框。清空历史不删除活动消息。消息只保存有界文本与标识，不持有控件、异常对象或完整采集文档，不写数据库或新日志文件。进度显式不进入历史，持续异常按真实状态变化发出，不因轮询重复发布；消息消失只改变呈现。验证范围见 [Quality](Quality.md)。

真实编辑由 `EditorPageBase` 和 Workspace 的统一 activity 驱动准备、冻结确认、执行、核验及结果刷新阶段；同一生命周期键更新 progress，不自动到期、不进入 History。两页通过 `OnRealOperationActivityChanged` 同步 action gates、阶段文字及图形区域黑色半透明遮罩/ProgressRing。busy 覆盖写后刷新尾段，多段结构 Apply 中不因单段终态提前放开编辑；成功、取消、失败、异常或断连均结束相应呈现。消息与遮罩清理不解除未终结 OperationId/哈希的写屏障，结果未知仍须匹配身份的只读对账，不能以 spinner 消失替代安全结论。写后先刷新，不用额外成功模态框阻塞刷新。

即时卡的宽度由 `NotificationCard` 固定，高度随有界内容变化；退出动画在 App 的展示层向右移动，动画结束后才释放可见项；服务的历史与生命周期不依赖动画。主窗口以展示项实例身份处理完成回调，避免旧卡回调移走同 ID 的新项。触发时机、图标、悬停和样式分别见[设计核对表](DesignTables/README.md)。

开发页“消息列表”只消费现有消息服务，不显示 Diagnostics 路径或读取故障日志文件。消息缓存不依赖开发页生命周期或 DeveloperMode；退出 App 自然清空，不补采后台历史。开发者模式关闭仍隐藏开发页，错误卡固定提示到开发页消息列表查看详情，不另加开启模式判断。现有 CommandLog 不接入此页，不复制成第二套历史。原生 ToolTip 与开发页详情分别承担短说明和可复制长信息；禁用控件仍应支持鼠标悬停帮助。其它需要确认的同窗口 ContentDialog 继续使用小型串行协调，不增加通用任务平台。

开发页左上消息列表直接投影 History 的时间、级别、来源和 Title，不读取 Diagnostics 文件；第四列不拼接 Message 正文。列表不显示表头，单行条目按四列紧凑排列并垂直居中。条目双击后在页面中央显示背景变暗的详情层；唯一可操作内容是只读可复制文本框，背后由 `WinPoolOpaqueSurfaceBrush` 实色底板承托，点外关闭。右下通知的单层 InfoBar 本体也使用该不透明主题画刷，避免背景透出。三个子区域可拖拽调整；右上和下方保留空区域，下方只给小号 AI 入口开发中提示；窄窗时消息列表横跨上方。此页不显示区域标题、说明、路径和操作按钮；“消息列表”仅用于控件自动化名称及文档称呼。

## 数据与生命周期

标准数据根是 `%LocalAppData%/WinPool`，便携模式使用程序旁可写 `Data`；启动指针为根目录之外的 `%LocalAppData%/WinPool.storage-location.json`，避免替换标准数据根时把活动指针一起移走。兼容读取标准根内旧指针；首次切换在目录改名前先固化当前指向。切换前验证目标，现有租约、单实例和生命周期机制继续保留。迁移核对表结构、主键与完整行摘要，核心库 checkpoint 在写入方静止后执行；若暂存副本缺少已提交 WAL 内容，拒绝切换并保留源，提示重启后重试。未确认的回滚树保留，不按名称自动清除。

| 持久化来源 | 唯一写入者与用途 |
| --- | --- |
| `app-settings.json` | App；语言、主题、当前页面等前台偏好，Agent 只读所需项 |
| `agent-settings.json` | Agent；持续监控、采样率、自启及可选的 7z 绝对路径覆盖；App 经类型化请求修改 |
| `winpool.db` | Agent；系统和采集快照、模拟文档、工作区、存储健康事件、执行与 Agent 会话记录；旧监控表和记录保留 |
| `monitoring.db` | Agent；新监控会话、设备、原始样本和编辑采样缺口，独立格式版本，不迁移旧核心库监控记录 |
| `MonitoringArchives` | Agent；封存库、临时压缩包、完成归档及恢复记录；不是历史查询数据库 |

偏好按变化原子保存；已存在文件不可读时禁止用默认值覆盖。App 的读取、局部变更和整体替换共用串行门，局部变更在取得门后读取最新偏好，保存成功再发布状态，避免旧快照覆盖其它设置。Agent 偏好的 `SavedAtUtc` 只比较是否变化，不按大小排序；通知、重连和文件观察汇入串行重载。Agent 自己维护指向自身可执行文件的 HKCU Run 项。执行模式和真实操作同意不持久化。

监控拆库与归档的历史阶段见[归档](Archive/20260921-monitoring-rotation/README.md)。核心库现为 schema 18，旧监控结构仍保留；监控库独立 schema 2。精确有效的 schema 1 经旧结构校验，在事务内新增 `monitor_edit_gaps` 并升级至 2，不改写旧样本；损坏或未知结构拒绝，不将“旧文档拒绝”误用为监控库一律不迁移。固定活动路径为 `monitoring.db`，主文件与 WAL 达到 1 GiB 时触发轮换：采样继续进入有界内存，旧写入排空、TRUNCATE checkpoint 成功并关闭连接后，仅将自包含主库改名封存，再创建固定名称新库。CSV 读租约与切换互斥。切换允许短暂推迟落盘，不承诺进程崩溃时内存不丢失。

真实编辑通过 `IRealStorageEditObserver` 接收 Agent 已持久化的 accepted、CallIssued、Verified、终态及只读恢复查询状态，App 意图不代替 durable 事件。重复 OperationId/StepId 按步骤状态优先级处理，迟到旧状态不能将已核验目标退回 Editing；observer 异常只记诊断，不改变已 Verified 写入结果。当前采样影响集合为联机状态、清盘、GPT 初始化、池/VD 创建删除；改名等其它命令不因此暂停采样。接受计划仅提示预期变化，可能开始 Windows 调用后才暂停受影响目标的采样，未受影响目标继续。Verified 对活动会话可等待一次 fresh 身份解析后动态重绑，不重启整场监控。

PhysicalDisk/VirtualDisk 以稳定 StorageObjectId 绑定；吞吐计数器使用 OS disk number 前须有新鲜唯一 same-device 关联。VD 状态计数器优先沿同次 fresh、非 retained 的唯一 VD→OS disk 关联生成 `vd-disk-number:N`，保留 VD 与 OS disk 各自的 UniqueId/ObjectId；PDH 只接受唯一实例以规范的 ` - Disk N` 后缀完整结束，大小写不敏感，不按 FriendlyName 或数字子串匹配。重复 OS number 归属、多个匹配实例均拒绝；后续重绑重新读关联，不将旧盘号当身份。既有准确 provider GUID selector 保持兼容，不能为32个十六进制字符的 UniqueId 猜字节序或从其它对象借 GUID。只采请求的 metric 家族，缺失/无效不转为健康零值。绑定成功不等于实际采样恢复；无法唯一绑定时保持待核验或 NeedsSelection 及原因。已核验删除的旧对象保持 RemovedByEdit，新对象用自己的身份/序列，不反向恢复或改写旧样本。[状态绑定局部证据](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/monitor-virtual-state-binding-fix/review.md)记录来源／PDH 观察与定向回归；B4 原生 session `bd1ace5317aa4a1f8112bb55d1df8312` 的[独立只读复核](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/native-vd-state-bd1-readonly-review.json)确认六项 state 各有 5912 条非 NULL 样本：ActiveBytes 恒为 32 GiB，其余五项为有效零；两块 Samsung 各 5912 条、最大间隔 64 ms、gap 0，会话 Stopped／dropped 0。该证据对应 B4 当时二进制，不冒充最终构建再次创建 VD 后的采样复测。

实际 availability 回调经有界队列异步保存 `MonitorEditGap`，记录原 session、target、counter、operation/step 和观察到的开始时间。只有同类计数器实际恢复，或删除终态已核验，才记录适用的结束时间；状态计数器恢复不关闭吞吐缺口，停止或重启不伪造未知终点，不回填缺失样本。轮换将未结束缺口及其原关联带入新活动库，封存段保留封存时已知状态。availability/gap 持久化是异步路径，不意味着 Verified 的 fresh rebind 完全不等待采集；本轮端到端验证状态见 Quality。

重启恢复按历史 gap 的真实开始时间倒序、稳定 ID 排序并以 TryAdd 载入，不能覆盖当前 Unknown／live 状态。`StartAsync` 仅对 PendingVerification／NeedsSelection 且 reason 为 `recovered_endpoint_unknown`、`verified_without_active_sampler`、`binding_verified_waiting_sample`、`unchanged_waiting_sample` 的目标允许重新采样；实际代码保留共同的 `monitor.edit.` 前缀。仍须 full、非 merged、非模拟的 fresh 来源，state／target／facts 的 SystemId 一致，准确 provider／counter 非空且非通配符，无 unresolved 目标，每个对象／计数器家族唯一绑定。生产 resolver 对实际使用的 provider／OS source 要求 Returned、准确 UID／ObjectId 和唯一非 retained same-device 关系；不要求无关来源类全部成功。物理盘与 VD 均支持，VD 吞吐和 state 分别核对；缺失或歧义保持暂停，Unknown／CallIssued 理由不适用。Start 不设 Restored，只有所有请求家族的真实样本恢复才更新状态；只关闭当前 session 的新 gap，旧 session 未知端点继续保留。[恢复直接证据](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/monitor-recovered-gap-resumption-fix/review.md)保留 54 项定向回归及原失败现场。

恢复后的原生 session `810015f675bf45c5a5cf7c2c3bd2aca1` 已经[最终只读核验](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/audit-monitor-repaired-performance/session-810015f675bf45c5a5cf7c2c3bd2aca1/final-readonly-receipt.json)：WDC 与两块 Samsung 各 4252 条实际样本、约 20.006 Hz、最大间隔 65 ms，18 个 durable 操作窗及 18 个性能窗均 gap 0，Stopped／dropped 0。后续 full 来源为非 merged／非 scoped，16 个 Returned 来源、36 个对象且 codec 深比较一致；WDC 末态 8 项核对均通过。Samsung 限定结构范围比较 0 变化／0 缺口，4 项共享 Primordial 调整单独保留，不外推为所有硬件字段或后台 I/O 均未改变。六次改名新增 plan／accepted／call 边界各 6，最终为 245／193／244，最后普通重启及退出不增加调用。

告警呈现与恢复状态分开：仅 `!MonitoringService.IsRunning`、`PendingVerification` 且 reason 精确等于 `monitor.edit.recovered_endpoint_unknown` 的历史目标不进入当前 issue 列表。其 gap、diagnostic 与 target 不删除，不标 Restored，也不补历史端点；其他 Pending／NeedsSelection、活动监控 pending 及 Unknown／live call 继续告警。[9 项纯呈现回归](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/monitor-inactive-history-alert-fix/summary.json)与最终构建的[MonitorOff 启动现场](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-main-later.txt)分别核验边界及无历史误报；这项呈现修复不改变采样、绑定、执行或持久化。

同一 operation 中后续 Pending step 不能覆盖目标已进入 CallIssued 或更后阶段的前一步归因，避免 availability 回调把 VD 的实际调用缺口记到尚未调用的 delete-pool。未进入调用的其它目标仍可显示 Pending 意图；后续步骤真正进入调用边界后才更新其受影响目标归因，不改写历史 gap、开始时间或采样范围。[归因局部回归](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/monitor-pending-step-attribution-fix/review.md)核对实际 availability 持久化路径；旧二进制已经产生的错误 step 关联保留为历史事实，不据新源码宣称已被修复。

CSV 仅导出当前活动监控库中的可用记录，不跨归档补齐会话。持久化诊断区分正常待写数量、最老待写年龄和确知未保存数量；正常 250 ms 攒批不是丢样，最老待写达到 2 秒时报告延迟。故障写入器的未提交数量按写入器身份只累计一次，恢复后的写入器失败另计；队列拒绝与已接受但未保存的样本分别计数，无法确认的异常结束缺口不编造条数。会话时长使用单调计时，归档诊断在停止采样后仍可刷新；通信成功不代替采样或落库成功。

停止监控及后续空快照保留 App 本次运行观察到的最近会话 ID，允许导出活动库中该会话的已保存记录；新会话取代旧会话。零行返回无数据且不覆盖已有目标文件。CSV 接受用户选择的普通绝对路径（含 UNC），拒绝设备命名空间、备用数据流、保留设备名及异常路径组件。该限制不改变自选导出目录。

归档在 `MonitoringArchives/sealed` 与 `MonitoringArchives/packages` 管理，恢复记录 `archive-ledger.json` 持久化归档根内相对路径，迁移数据根后按新根解析，不访问旧根。已完成归档不自动淘汰，不提供历史读取或解压缓存。后台串行压缩采用临时包，完整性、流式数据库 SHA-256 和清单内容核验通过后发布，才可释放本功能封存的原库；失败保留有效数据。故障恢复、缓冲计数与阶段验证状态见上述归档。

`ControlledProcessRunner` 与 `SevenZipArchiveAdapter` 是现有 SQLite 基础设施内的两个小型职责，不恢复旧工具管理项目。7z 默认相对运行目录解析为 `Tools/7zip/7za.exe`，随附资源来自 `assets/ThirdParty/7zip/26.03`，许可证和来源说明一并打包。自定义覆盖只检查绝对路径和文件存在，失败不回退，不执行能力或版本预检；压缩及校验固定使用本次任务开始时取得的路径。产品不提供工具安装、更新或搜索。

当前实施代码为核心 SQLite schema 18、监控 SQLite schema 2、IPC 13、StorageSystemDocument 3、来源事实 1、StorageSnapshot 3；均是内部格式编号，不是产品版本。核心库从 schema 17 经受检迁移进入 18，真实计划、步骤和事件记录必须保留恢复屏障；监控库的受检 1→2 迁移独立处理。旧文档格式仍明确拒绝，不提供文档迁移或兼容回退。新文档只持久化来源事实和应用状态，Snapshot 是无 setter 的只读重建投影，旧硬件报告模型及独立报告生产路径已退出。缓存仍校验哈希。监控样本逐项保存全部 `MonitorMetricKind`，未提供的指标写为 NULL，真实零保持为零；CSV 使用空单元格表达缺失。模拟文档 IPC 先分页读取有界元数据，再按 ID 单独读取正文，不扩大 4 MiB 帧上限。模拟提交的 CommitId 同时绑定文档、前后哈希、修订、OperationId 和 PlanHash，查询返回提交时的不可变文档回执。控制管道握手有独立 5 秒期限，连接级异常记录稳定代码并释放连接，监听任务终止会进入 Failed 并由托盘呈现。实际产品版本以 Directory.Build.props 为准，V0.52 验证状态见[归档](Archive/V0.52/README.md)及[实施核对](Archive/V0.52/实施核对.md)。

控制管道在握手、事件连接和请求执行前核对实际客户端令牌完整性；较低完整性或无法核实的客户端不能控制 Agent。握手后每次请求读取与响应写出分别有 30 秒传输期限，不把此期限用于业务操作。连接超时释放监听器，后续合法客户端可重连。模拟提交分别保留输入拒绝、修订冲突、持久化失败和取消的状态及诊断代码。

App 使用更短的 25 秒控制连接复用期限，在发送任何请求字节前主动更新空闲连接；计时从上次传输开始，保持保守。已发出的请求不因断连自动重试，继续返回结果未知并走原有对账流程。

V0.53 在 `UserPreferences` 中保存默认关闭的 `DeveloperMode`，旧格式缺少字段时按关闭处理，不升级偏好格式。主窗口从偏好重建可用导航；Hardware、Test、Development 同受该门控制，隐藏状态下启动目标、快捷键和记忆页面均回到 Manage。开发者导航顺序以 Hardware 在 Manage 之前开始。

数据重建只能针对明确的 WinPool 开发数据，不静默擦除未知根。首次打开旧格式应明确提示版本不支持/需重建；自动测试按夹具使用临时数据，普通开发与原生界面核对直接使用已核实的 WinPool 开发数据。必要的旧开发数据处置遵守 AGENTS 的移动规则。允许丢弃开发数据不取消单写入方、事务、冲突检测和故障恢复要求。

普通启动采用 Windows App SDK 单实例机制；重复启动激活已有窗口。提权交接是整套 App + Agent 重启：新管理员 bootstrap 以 SID 绑定的 ready/continuation 事件进入等待，期间不初始化 WinUI、不取得实例键、也不连接或复用旧 Agent。旧 App 保存工作区并获得旧 Agent 的后台有序关闭确认后才允许 bootstrap 继续；后者必须按 PID、启动时间和路径核验旧 App、旧 Agent 均已退出，才进入普通启动并创建新的管理员 Agent。取消、事件失败、身份不符或超时均不得接管实例、复用旧 endpoint 或强杀旧进程。等待失败诊断写入数据根 `Diagnostics/elevation-handoff.jsonl`；IPC 正常断开不等同于 Agent 故障。SQLite 不是实时 Windows 状态的权威，真实操作执行前必须重新核对对象及前置条件。

## 构建与运行树

在仓库根执行常规构建：

```powershell
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1
```

生成文件集中在 `artifacts`：`trees/<Configuration>/App` 与 `Agent` 是独立树，`<Configuration>` 是并集运行树，`obj` 和 `build` 是中间文件与类库/测试输出。`src` 和 `tests` 只存源码。

`build/Merge-RuntimeTrees.ps1` 按相对路径和 SHA-256 合并：同路径同内容存一份，内容不同则失败。`WinPool.App.exe` 与 `WinPool.Agent.exe` 位于同一根目录，共享自包含 runtime；保持 `PublishTrimmed=false`。

```powershell
.\artifacts\Release\WinPool.App.exe
```

开发阶段默认关闭占用标准运行树的 WinPool App / Agent，然后直接构建到 `artifacts/Release`；可按开发需要修改或重建已核实的 WinPool 开发数据，无需逐次请示。不要在进程仍占用目录时部分覆盖。仅在用户明确要求并行保留版本，或标准目录确实不可用时，才考虑其他输出位置。正式分发保持完整目录；不包含脚本、PDB、源图、数据库、日志、测试结果或重复子程序。构建输出可保留 PDB，产物不提交。

旧的自定义隔离输出路径有生成目标传播缺陷：App/Agent 的 `.deps.json`、`.runtimeconfig.json` 及 App `.pri` 可能留在默认 `artifacts/trees/Release`，导致隔离运行树缺文件。这不是开发阶段的默认构建路线；不要为规避关闭进程或重建开发数据而重新尝试隔离构建。若未来确有明确的并行产物需求，应先修复并核对运行树文件完整性。

`build/Merge-RuntimeTrees.ps1` 先在相邻暂存树完成碰撞检查，并复制现有便携 `Data`；确认进程未占用后，将旧运行树移至项目根 `Rubbish/YYYYMMDD_winpool-build`，再发布新树。发布失败尝试恢复旧树，失败暂存树也保留。路径必须位于当前 checkout 内且不能含重解析点。

`build/Rebuild-WinPool.ps1` 先仅移走中间输出，保留旧运行树直到新构建合并成功，然后写入快捷方式；运行前须关闭占用标准树的 WinPool 进程。`build/Clean-WinPool.ps1 -WhatIf` 可预览，默认只移动明确生成物，保留运行树的 `Data`、测试证据及其它未知 artifacts。两者均不直接删除旧文件，也不按进程名结束未知路径的进程。它们不是纯文档任务或普通检查的默认入口。

运行树替换前，`build/Assert-RealOperationIdle.ps1` 以只读方式检查活动核心库，继续阻断 `risk >= 4` 且状态非终态的操作。拒绝信息列出最多十条明确的 `OperationId` 与状态名，并提示启动保留的现有运行树，在编辑页按 ID 查询和只读对账。只有权威日志确认终态、解除写屏障且相关进程已停止后，才能重新尝试替换；查询一次不保证解除未知状态。

未来正式 staging 使用仓库外未占用的新路径，复现同一并集和碰撞检查；不顺便部署、签名或发布。测试命令归 Quality。

## 文档与版本

内部开发文档只维护中文无语言后缀版本。根目录 `README.md`（英文）和 `README.zh-CN.md`（中文）保持用户信息一致；其他目录中的索引 README 不因此需要双语。历史双语原件不追溯翻译。代码/API 标识和微软原名保持英文。

Product 管产品，[UnifiedModel](UnifiedModel.md) 是其统一对象与派生属性专项设计；Development 管技术、Quality 管验证、Plan 管当前阶段（若有）、CHANGELOG 管重要结果；AGENTS 管操作规则。Design 保存未排期方案，Reference 保存方法，Archive 保存被替代/结束的历史。一个事实一个维护位置，其余用短摘要和链接。

设计只需状态、基线/条件、未决问题三个说明，不引入复杂审批体系。讨论中的设计不等于当前规范；方向已认可也不代表细节冻结。纳入版本时重新核对代码，明确采纳部分并写入唯一活动 Plan，长期决定归各自所有者。无须为每份设计新建一个 Plan。

有活动阶段时，Plan 记录范围、固定决策、任务依赖和验收；执行时及时更新实际状态。阶段被替代时如实归档，不写成验收完成；阶段结束时记重要结果、归档 Plan，没有新阶段就不保留活动 Plan。CHANGELOG 按重要结果记录，长历史可按明确时间点归档，Git 保留过程。

用户明确要求留待以后执行的计划可以保留在 `docs/Plan.md` 中，标记“未激活”并与当前任务分节；不提前归档，也不视为执行授权。此前界面与人工反馈阶段的 Plan 已[归档](Archive/20260924-before-real-edit/README.md)；2026-09-27 开始的本机真实修改第一阶段已完成并归档，H01–H11、正常重启保护及工程门为该阶段结果。当前 [V0.59 Plan](Plan.md) 已激活，源码版本仍为 V0.58，本轮最终状态以其验收与版本收口为准。2026-10-07 用户重申 WDC 全部真实操作已获批准、E: 无有效数据，执行时沿用 [AGENTS](../AGENTS.md) 的持续授权，范围内不再逐操作聊天索批；该授权不扩大产品能力或其它磁盘范围。未激活 Design 不因此获得实施授权。

唯一产品版本源为 `Directory.Build.props`：`Va.b` 表示产品线，`Va.bc` 的 `c` 为 1–9 的迭代；迭代为 0 时显示补零，因此产品线 0.5 显示为 V0.50，框架数字版本为 0.5.0。框架必需数字版本由该文件机械生成。

项目不能通过相对路径、复制运行文件、子模块或运行时导入依赖其他仓库。软件资源使用受版本控制的 `assets`，不让代码依赖忽略目录。

## 分区联合体投影

`WinPoolSystem` 将唯一的 partition-volume / same-volume 关联组合为 `WinPoolPartition`，`Resolve` 接受所有成员来源 ID。`StorageSnapshot.PartitionUnions` 是管理与业务导出的单层表面，`PartitionSourceIds` 负责旧来源选择恢复。PartitionInfo / VolumeInfo / NetworkDiskInfo 仍作为采集、持久化和现有编辑命令的内部适配记录；不得再次据此建立独立卷属性分类。文件系统属性由同一个 `ManagePartitionProjector` 输出。

磁盘补充事实通过 disk-supplement 关联匹配唯一 Disk.Number。OS 磁盘的 Number、PartitionStyle 等仍读取自己的来源，不能被物理磁盘主来源覆盖。单例 Computer/OperatingSystem 身份按系统和类名确定。按来源刷新只替换本次成功且完整覆盖的对象域；保留仍被事实字段引用的历史来源及每类 full/各 scoped 范围的最新状态，避免重复失败状态增长，不将局部成功称为整类刷新。模拟提交保留仍有效的补充关系，删除联合体时同时去掉其附属逻辑来源，真正孤立来源继续保留。

固定采集脚本始终内嵌，经标准输入执行，不输出独立 inventory 脚本。分区联合体修复的历史验证见 [CHANGELOG](CHANGELOG.md) 中 2026-09-15 对应记录。
