# WinPool V0.62：减负、性能与公开测试版本完整实施计划

**状态：用户已于2026-10-18批准完整执行；W1–W5主要实现已落地，W6本机验收中。** 普通技术问题由执行Agent自主判断，不逐工作包请求批准；外部验收与公开发布仍按D6及第7节处理。实现不等于验证通过，当前结果归[Quality](Quality.md)。

编制／本地复核日期：2026-10-18。实施前源码基准：`2fb84ddc3e7c6929a6feb2cf7c73058fe90f9368`；W6已将产品版本推进 **V0.62／0.6.2.0**，完整验收与候选尚待完成。

计划编制阶段仅核对源码、文档与已有证据，未修改代码或运行验证；用户现已批准执行W0–W6。后续状态与结果依实际实施更新，发布仍按明确发布指令。用户提供的[原提案](Archive/20261018-v062-proposal/WinPool-V0.62-Plan.md)按原字节归档；历史审查不改写。本文件为唯一工作清单。

## 0. 可行性判定与本次修订

**技术路线可实施，原稿已按源码和用户决定修订。** 常驻外部PowerShell 5.1、同批事实、后台模拟保存、有界IPC复用、分块与旧代码退役都有现有入口可接，不需要推翻App／Agent所有权。绝对性能门槛仍待同条件测量；用户已明确外部环境验收由其另行处理，本执行方仅负责现有开发机及已授权设备。激活计划不等于所有性能数字已经达标。

| 原稿问题 | 核对结论与执行修正 |
| --- | --- |
| 结构 job 使用 UTC，时钟回退会漏终态 | 持久更新已用 `MAX(updated_utc_ms+1,$at)` 保证同 job 单调；保留并验证，撤销为该误判新增 revision 列的任务，见 W2-C／[实际持久实现][S46] |
| 一个串行宿主同时完成所有查询及长写 | 短控制留在 .NET；provider 查询仍受单宿主串行限制。补发送槽、CallIssued／化身绑定、父进程死亡和退出准入，见 W1 |
| “每个Agent一个宿主”不足以约束崩溃遗留；长期无输出的含义不明确 | 补本机全局最多一个产品宿主、原子创建准入，以及存活／退出／不可核实三态；无输出不能证明写已失败，见§4.1.1–4.1.3 |
| 2 s 必须发起保存，同时每文档只有一个在途 | 2 s 是无在途时的最大调度等待；已有在途则记录到期，确认后立即接下一批。排队时间单列，不能称已保存，见 W3-A |
| 64 MiB 文档、原子 intent、导出及事件未共用完整口径 | 以完整可导入 envelope 的规范编码计量；导出同编码；补全部正文入口、轻终态身份和有界分块，见 W4-B |
| 退表、协议变化与构建守卫未一起落地 | 本版 IPC 15→16、核心 schema 20→21 各集中一次；同步旧 schema 验证／迁移、数据根切换和构建守卫，不增加 job revision 列，见 §3.3 |
| Unknown 一律阻止运行树替换；既有测试已覆盖完整提权退出 | 无活动写且相关进程已停的 Unknown 不阻止安装修复；实际 handoff 迟到回调需直接补验，见 W5 |
| 性能百分比与发布前全设备实测均视作已有可达条件 | W0补结构／MAX基线与原生耗时下界；D6已明确本机由执行方验证，外部由用户组织并回报。保留V0.62全部范围，分别记录完成状态 |

## 1. 开发者只需先看这一节

### 1.1 本版目标与边界

**V0.62 交付完整、可靠、明显更快且更易维护的公开测试版本。当前已知问题全部在本版处理；V0.63–V0.70 仅用于以后检查新发现的问题，不承担本计划任何已知工作。**

完成必须同时满足四件事：

1. **减负真实发生：**已确认的旧生产链、无消费者存储副本和维护旧实现的测试实际退出；日常只做修改相关验证、更新唯一事实位置、提交，不再要求每个小改动全面审查、全量测试或多份完成报告。
2. **性能结果达标：**读写共用常驻 Windows PowerShell 5.1 宿主；同批事实共享；正常写后不循环整机采集；模拟输入、界面选择、监控绘制和短控制请求不再承担无关大工作。按第 6 节实际秒数、响应时间、分配与调用形状共同验收。
3. **已知可靠性缺口关闭：**临时事实生命周期、大小限制不一致、终态依赖大证据、保存失败与交接退出等都有实现和直接验证；现有job单调版本保持，不按已纠正的误判重复改造。
4. **公开交付闭环：**一个稳定源码构建、对应的测试结果、实际解压运行的同一个 ZIP、使用说明和可下载的 GitHub prerelease。仅有本机 candidate 不算公开完成。

本计划不允许用“风险不高”“已有替身测试”“在TESTING写未测”“没有新P1”“旧计划允许延期”替代完成条件。**用户2026-10-18决定：本执行方负责全部本版开发及本机授权范围验证；开发机以外的测试由用户另行处理并告知结果，仍属于V0.62。** 本机交付完成后如实记外部结果待用户提供即可结束本执行方任务，不要求等待外部测试或自行提供设备；V0.62整体发布验收仍需外部结果，不能把它移入下一版本或假记通过。

“所有已知问题”包括三份原审查、三份新验收以及本次源码核查发现的缺口。当前 Product 明确没有提供的功能，如动态磁盘、VHD/VHDX、自由形式命令，并不因此自动变成新增功能；这些原有产品边界要如实保留。对已经声称支持而缺少验证的能力，则按第 7 节在本版完成验证或由开发者明确改变范围。

### 1.2 实施顺序

| 工作包 | 直接结果 | 依赖 |
| --- | --- | --- |
| W0 基准与合同 | 固定当前事实、方案与本机性能基线；按已明确分工登记外部验收 | 本计划批准；D6／D7已确认 |
| W1 常驻读写宿主 | 正常读取、写入、后验不再逐次启动 powershell.exe | W0 |
| W2 事实与执行闭环 | 同批合并、能力复用、窄后验、状态恢复和临时对象释放 | 与 W1 共用边界，先做一条真实单步纵向链 |
| W3 模拟与界面 | 本地编辑及时呈现、合并保存、一次预演和局部 UI 更新 | 保存语义明确；与 W4 共用现有 IPC |
| W4 IPC、监控与 SQLite | 短控制不被长任务堵住、大小契约一致、监控少复制、减少无用写入 | W1/W2 接口稳定；可与 W3 分文件并行 |
| W5 删除与开发减负 | 旧链和无价值测试退出，文档一处维护，构建工具定点修复 | 随 W1–W4 一起删除，不等最后集中扫尾 |
| W6 验收与公开交付 | 执行方完成本机验证与V0.62候选；用户提供外部验收，整体通过后明确发布 | 本机交付依赖W1–W5；整体发布另依赖外部结果与发布指令 |

这些是同一个版本内的工作包，不是小版本。先贯通“刷新→Prepare／确认→单步写→后验→UI／monitor”验证主要性能目标，再扩展其余路径，不保留旧启动器。本执行方完成本机范围即可交付，外部验收由用户单独安排；不采用“半天”“1–2天”等未验证估时。

## 2. 真实基准：源码、历史与运行证据分开

### 2.1 已独立核实的源码与历史

原提案编制者记录：对固定 Git tree 的309个生产文本文件、151个测试文件及28个相关配置／文档，共488个文件核对Git blob，并计算以下同口径规模。本次在本地Windows仓库复核实际调用者、持久更新、入口与生命周期；以下规模保留为原提案统计，不冒充本次重新下载／逐文件计数的结果。

| 项目 | 原审查基准 `74a40af` | 当前 `2fb84dd` | 同口径变化 |
| --- | ---: | ---: | ---: |
| 生产 C# 文件 | 277 | 282 | +5 |
| 生产 C# 行数 | 91,733 | 91,481 | −252 |
| 生产 XAML 文件／行数 | 14／2,818 | 14／2,818 | 不变 |
| 生产 C#＋XAML 行数 | 94,551 | 94,299 | **−252，约 −0.27%** |
| 生产 C#＋XAML 字节 | 4,412,191 | 4,461,044 | +48,853 |
| 当前生产／测试项目 | — | 10／11 | 原 Inventory 两个项目确已退出 |
| 当前测试 C# | — | 140 文件、51,417 行 | 静态规模，不是已执行用例数 |

计数只含受控 `src/**/*.cs`、`.xaml`；UTF-8-sig 解码后按 `splitlines()`，不混入生成文件或 Old。路径迁移不会被当作纯删除收益。旧报告的 92,010 可由“仅 C#，每文件把末尾空行项多计一次”精确复现；用同法统计当前为 91,763，也只净减 247。**86,735 和“净减 5,275”不能在这两个提交间复现，不再用作减负完成证据。**这不否定已经删除的重复路径，但总量和具体负担必须分别说明。[H1]、[H2]、[H3]

历史链：

- `74a40af`：三份原审查的主要基线；其后有权限/文档/实施改动。
- `24b5e96451e5d381c994472be7ba761dddbba8d5`：本轮主要简化，确实退出旧模拟执行包装、重复采集和轮询；同时引入的部分交接/缓存生命周期尚未完整收口。
- `d210a23b194b88108fb8e93757a36dd99df9e9c0`：草稿绑定、恢复事件及 monitor revision 的后续修复。
- `d210a23 → 2fb84dd`：4 个提交、18 个变更文件均为文档/README/AGENTS；**当前运行代码没有在这段期间继续修复。**[H4]

### 2.2 当前运行结果能说到哪里

| 证据类别 | 当前事实 | 本计划的使用方式 |
| --- | --- | --- |
| 源码确定 | 读、写都逐次 `new Process`；启动同代合并接线不足；普通操作后态未释放等 | 直接安排修复 |
| 两份验收报告一致 | 全量 2,504：2,499 passed／1 failed／4 skip；失败为旧私有字段字符串断言 | 作为“报告的最近运行结果”，不能冒充本次重跑 |
| 旧现场性能 | 普通单写约 12.54 s、三次采集；单次手动刷新 provider 约 2.77–2.89 s | W0 同机重建可复现基线，W6 作同条件比较 |
| 旧 candidate | 报告称 ZIP 完整且有实际启动；其 10 个 WinPool DLL 与另一批测试 Release 不同 | 不把两个 build 的证据自动拼接 |
| 原提案环境 | 编制者使用Linux，未取得被Git忽略的TRX／ZIP／实盘原始日志 | 这是来源限制，不是当前执行机条件 |
| 本次本地复核 | Windows／PowerShell；读取源码及两份已有自动汇总，均为2499 passed／1 failed／4 skip | [汇总A](../artifacts/acceptance-20261018/automated-summary.json)、[汇总B](../artifacts/test-results/acceptance-20261018/acceptance-test-summary.json)；本次未重新构建、测试或验包，历史实机结果仍按各报告范围使用 |

报告关于“一次手动刷新有 1 次还是 3 次采集”存在归属冲突；同 PID 不足以证明属于同一次操作。W0 使用 correlation/operation/generation 与触发时间区分启动、手动刷新和 monitor，不能简单相加。嵌套计时不能重复相加当墙钟。[FA]、[RA]、[TA]

### 2.3 已完成而应保留的成果

以下已有源码依据，实施时保护受影响行为，不重新建设同一套：

- Accept 不再采集；typed preflight context 供 Execute；普通全部 Verified 不再走成功恢复链。
- Agent 持有整个结构作业；MAX single/multi 已共用 attempt；算法仍为既定整数 GiB 策略。
- App 不再 250 ms 查询真实状态；稳态 monitor 不再拉完整快照；inventory 正文不在事件和响应双传。
- 模拟已改 typed intent＋轻回执，Agent 共用 reducer＋CAS；一份有效草稿 revision 外围只做一次预演。
- 目录 metadata 与正文惰性加载、SystemId 缓存、页面偏好唯一来源、普通权限 discovery 失败域分离已接通。
- 旧 Inventory 项目、legacy provider/compare、点名的多数旧 RPC/factory/alias 已消失；不再把不存在的项安排删除。
- 恢复只读、不重放已发写；SID/进程化身/准确目标/保护盘/CallIssued/Unknown 语义保留。[S1]、[S2]、[S3]、[S4]、[S5]

## 3. W0：固定方案与验收分工

### 3.1 三份审查不能逐字同时满足

R 要求保留准确预览与临写观察，T 要求固定“两读”；F 曾建议 Unknown 重试用尽释放屏障，T/R 又要求 Unknown 不重放且保留屏障。T 按 planner/Read 名称计数，R 按真实规则所有者判断。它们不是一个已经一致的验收合同。

本计划把每项映射到动作和证据。D1–D5按下表具体方案提交批准，不需要执行者再次猜选；批准本计划即采用相应方案。**D6外部验收分工、D7测试规则已由用户于2026-10-18确认；这两项答复不是整个代码计划的批准。** 不因此取得新增磁盘授权；改变方案时先同步对应验收，不在结果里自行把冲突标成“满足”。

| 决策 | 本计划采用的具体提案 | 必须明确的差异 |
| --- | --- | --- |
| D1 审查冲突 | 以唯一行为所有者和可测结果闭环；共用纯草稿规则、统一有类型的存储读取，保留 Agent 权威与临写 fresh 核验；首次后验不满足时允许有界窄探针；Unknown 不因次数耗尽解锁 | 不承诺固定总接口数、所有 planner 只剩一个类、整个 MAX 只读两次、取消所有后验时间窗口。逐项差异列在附录，需开发者裁决后形成验收合同 |
| D2 模拟保存 | 保留Enter／Apply完成输入的边界；合法本地结果立即呈现，300 ms空闲合并保存，无在途时最迟2 s调度；已有在途时待其结果明确后接续。失败留内容，显示真实保存状态；撤销仍只针对Apply前草稿 | 这是保存体验调整，当前并非每次按键RPC；不增加跨Apply全局撤销、自动冲突合并或App写库；真实确认不变 |
| D3 文档容量 | 完整可导入envelope的规范UTF-8编码≤64 MiB，导出使用相同编码；文档与原子intent分别限64 MiB。IPC每帧仍4 MiB，正文／证据／超帧intent走同一小范围分块 | 文件输入也先限64 MiB；解析规范化后再核保存口径。不静默缩成3 MiB，不只抬帧上限；临时事实的宿主结果另有独立边界 |
| D4 性能预算 | 采用第 6 节建议的明确门槛，在同机完整负载下达标；不足继续修 V0.62 | 不是已测结果；不得用“比原来快一点”替代。目标需调整时明确请求开发者决定 |
| D5 平台与交付 | 当前便携 x64 自包含 ZIP＋GitHub V0.62 prerelease；维持当前 Windows 10 22H2 和 Windows 11 24H2/25H2 产品目标并分别实测、准确标注上游支持 | 不自动新增 MSIX/Store/新功能；不自动删除 Win10 承诺；签名按实际条件说明，若要求签名须有证书与环境 |
| D6 验证分工（已确认） | 本执行方仅处理现有开发机及已授权WDC范围验证；开发机以外的OS／设备／人员由用户另行安排并提供结果，全部仍属V0.62 | 外部结果不阻塞本机任务交付，但阻止声称V0.62整体验收完成；不自行建VM、采购设备或扩展磁盘授权 |
| D7 测试规则（已确认） | 日常相关回归；稳定后完整检查一次，失败按影响范围补验并记录实际来源 | 不因每次修复机械重跑全量；无法确认旧证据适用时扩大范围，不把补跑汇总冒充最后一次整套全绿 |

Windows PowerShell 5.1 使用 .NET Framework；当前 Agent 使用 .NET 10。W1 选择 **Agent 独占管理的一个 5.1 子进程，进程内一个长期会话/runspace**，避免把 5.1 SDK 直接当作 .NET 10 内嵌运行时。常驻是复用进程/模块，不是缓存可变磁盘事实。PowerShell 7 不作为本版提速方案，也不创建双宿主 fallback：本次已定位的是反复冷启动与重复工作，没有证据表明换 7 是必要修复。这是本版明确技术选择，未给未来版本预留迁移任务，也未宣称永远不用 7。[M1]

.NET 10 官方支持表截至本次核对未列 Windows 10 Pro 22H2，列有特定 Enterprise/IoT 版本；“本项目实测兼容”与“上游正式支持”必须区分。保留 Win10 兼容目标的提案需要实际测试，不能用本机能运行反推上游支持。[M2]

### 3.2 W0 的执行产物

在现有`StorageOperationTiming`增补缺失字段即可：buildId、correlation／operation／job、generation、host incarnation、scope／purpose、队列等待、宿主启动、provider／写／后验、UI应用与wire bytes。exact-member和MAX adapter补同套`real.provider`阶段。Stopwatch统计耗时，未经单调处理的UTC只用于显示时间；现有job的持久单调更新时间按W2-C保留。

读取预算从生产采集入口到所需raw事实可用计，排队、冷宿主启动、解码都包含；子阶段用于归因，不把嵌套计时相加或把开销移出预算。操作端到端从Prepare到UI应用，只扣除明确人工停留并另存原始总时长。

固定一组低成本基线：当前同机一次冷启动、10 次热刷新、10 次模拟改名、代表单步真实改名、原约 530 KB 文档、20 Hz 监控可见/隐藏场景。真实改名与必要还原分开记；不要为凑样本重跑大量破坏性操作。旧文件能直接提供原始证据时可复用；否则按当前源码复现一次，不要求人类找齐所有历史日志。

在替换W1之前，再固定一条现有WDC结构／MAX代表负载的原始输入、最终拓扑、候选数、provider读取、原生写入、必要观察间隔和端到端墙钟。先查现有raw是否足够；不足才补一次受控基线及还原，不拿不同版本的247.69 s直接计算降幅。同时冻结530 KB／3 MiB／接近64 MiB的合法复杂文档及其hash，记录机器RAM、完整Hardware原始输出和规范文档字节；64 MiB样本不是空白填充文件。

第6节数值保留为目标。先计算结构／MAX不可省的原生写入和证明等待下界：若该下界已超过旧总时长的一半，则“总体降低50%”不能由应用优化达到；记录实际分项并在继续追该门槛前请用户调整，不无限增加架构或破坏MAX算法。W0本身不是降低目标的授权。

这里只保留一个临时证据目录和 Quality 的一份汇总。不是新建遥测平台、基准站或第二份审查报告。

### 3.3 跨包契约与接线顺序

1. 先在Application／Ipc集中确定宿主调用证据、typed存储查询、轻状态冻结身份、分块描述与保存批次；App／Agent同包将IPC **15→16**，删除旧生产codec／handler，不保留双协议。业务版本字段仍在W6改为V0.62。
2. W4-D的退表统一用核心SQLite **20→21**；保持17→18→19→20→21升级、旧schema20结构检查与未知journal。同步`WinPoolSqliteStore`、`StorageLocationManager`、`build/Assert-RealOperationIdle.ps1`及现有迁移／guard用例中的版本列表。监控schema2与文档格式不因产品升版机械增加；没有独立job revision迁移。
3. 先接通单个真实改名的“原草稿摘要→prepare→确认→新鲜临写→同宿主write→窄后验→轻终态→界面／monitor”，再迁其余typed命令和MAX；逐类撤旧入口。W3后台保存先接一个文档，验证在途前缀／CAS／退出协商，再接两页和多文档，不保留两种保存语义。
4. W1宿主、W4控制复用、W3后台保存共用现有Agent生命周期。协议/迁移由一个执行者集中修改；本机构建、测试、GUI、设备各时刻只有一个执行方。独立文件可以委派，完成后由主代理核对实际生产接线。
5. 工作包状态初始均为**未开始**；采用“实施中／已实现待验证／已验证／具体条件未满足”。本执行方按D6完成开发与本机验证，外部项记“用户负责／待结果”，不执行新增环境／设备操作。本机完成可交付，只有实测证据才关闭对应性能／设备项。

## 4. W1–W2：一次解决读写成本与执行可靠性

### 4.1 W1：唯一常驻 PowerShell 5.1 宿主

**当前位置：**`Infrastructure.Windows/InventoryPipeline.cs`、`WindowsRealStorageCommandAdapter.cs`、`WindowsRealStoragePowerShellScript.cs`、Agent `Program.cs`。[S1]、[S2]、[S6]

实施：

1. 本机由WinPool产品管理的PowerShell宿主在正常运行、崩溃恢复、提权与根切换期间合计最多一个；同一时刻只执行一条宿主命令。按§4.1.1取得准入后延迟启动或在启动读准备时启动；固定使用系统64位Windows PowerShell 5.1路径。Storage模块、固定只读／写入模板加载一次。所有生产读取、scope查询、存储写命令及后验探针走同一宿主；不保留每次启动的旧生产路径，不建池、多版本切换或任意脚本接口。
2. 请求是封闭的命令种类＋typed参数＋requestId；帧有实际字节上界。stdout只承载协议，错误／诊断单独处理；每个请求有独立变量作用域。调度到执行时发一次`started`帧，返回时发唯一`result`帧，均匹配宿主化身和requestId；`started`只证明宿主已接纳执行，不证明Windows写已成功或正在推进。只复用模块与固定模板，每次观察仍真正读取Windows所需事实。
3. **必须同时改造脚本入口。**现有写脚本使用 `Console.In.ReadToEnd()`、每次 `Import-Module Storage`、自行 `Console.Out.WriteLine`，不能原样塞进长循环。改为接收已解码参数并返回 typed 结果，由唯一宿主帧层读写；不得用 EOF 判断一条请求结束或吞掉下一帧。初始化成功应有一次 ready/version 握手。
4. .NET 控制状态、停止后续步骤、事件通知不排在 PowerShell 长命令队列后。宿主只串行执行需要 Windows provider 的工作；真实写 gate 与普通控制响应区分。
5. 删除两套旧启动/压缩/编码/EOF 交互实现及只为它们存在的测试。嵌入模板的来源和允许命令由一个边界拥有，不每层全文扫描同一可信模板。请求参数的形状/范围仍在输入和真实执行边界验证。
6. 宿主生命周期接入既有Agent退出、提权、数据根切换和崩溃恢复。当前宿主没有可能仍在执行的写时才可安全回收；不为内存或超时建立定时重启任务。

接线范围须包括现有System.Management存储能力、partition／VD范围与StorageJob读取，将其实现迁到宿主的固定query；保留typed reader接口及纯规划。Registry机器绑定、进程化身和必要原生安全探测不伪称已由PowerShell执行，使用现有原生边界并明确剩余deadline／有限在途。单宿主正在执行长写时，新的provider读取排队或按预算返回忙；轻状态、停止后续步骤和取消等待由.NET直接响应，不承诺同一个runspace同时执行实时job查询。

宿主协议固定为长度前缀＋UTF-8 typed JSON，握手返回protocol／incarnation／ready；每次结果匹配incarnation与requestId。新限额为请求4 MiB、原始结果64 MiB、待执行队列8项，W0核完整Hardware实际字节；它们与App文档上限是不同合同，超限明确失败，不截掉事实假装完整。stdout由唯一reader读帧，stderr持续排空且仅保留16 KiB诊断尾部；原runner没有这些收集限额，不能只移动事后截断函数。超限或坏帧发生于已发写时保留Unknown，不因回包损坏重发。

读deadline取调用方剩余预算与60 s的较小值，覆盖排队、执行和解码。队列满立即返回busy，不能在队列外无限积压`WaitAsync`；排队读取消只移除该项。正在执行的只读超时且当前宿主没有可能仍在执行的已发写时，才按§4.1.2回收；写入不加强杀计时器。

写入协议必须保留的语义：

- **CallIssued 在可能发送写帧之前耐久记录。**仅进入队列、确定尚未发送的工作可取消；发送开始后不能凭“没收到响应”重发。
- 先占住宿主发送槽、取得ready化身，再在既有步骤`target_json`及CallIssued事件的同一事务存`HostCall`（incarnation、PID、进程启动时间、requestId）。复用`ExecutionRepositories.TransitionStepAsync`；MAX在现有attempt的目标证据中存同字段。计划／步骤ID沿原行键，不另建宿主表。记录后不得换宿主发送；记录后故障按Unknown保守处理。恢复核验复用`ProcessIncarnationVerifier`，不只认PID。
- 写帧部分发送、宿主崩溃、丢输出等不确定边界进入 OutcomeUnknown，保留原 operation/job 身份和 journal，只读核对。
- UI 断开、只读超时、取消等待、App 关闭不得杀死可能仍在执行写命令的宿主。停止请求阻止下一步，不能伪造当前写已取消。
- 只有确定正在执行只读请求、当前宿主没有可能仍在执行的已发写时，读超时才允许终止／重建宿主；失败明确显示，不隐式切回旧启动器。
- 正常退出保留现有准入语义：有活跃写时本次退出返回忙，写结束后才走关闭；不得把写宿主放进15 s退出步骤后强杀。父Agent异常死亡时，宿主在当前请求结束后遇EOF退出，不中途杀写。新Agent按§4.1.1核验全局占用及CallIssued／HostCall；旧宿主仍存活或无法核实占用时，不创建任何新宿主，连只读替身也禁止，提权／根切换不能例外。旧无HostCall的记录继续按既有Unknown处理，不能把缺字段当作已无写入。旧宿主确认退出后，新唯一宿主可以只读解释结果；写屏障仍按既有恢复规则处理，不续发未执行阶段。

#### 4.1.1 全局数量与创建准入

约束对象是WinPool创建的产品采集／存储命令宿主，不包括开发者终端、测试脚本进程和Windows自身provider服务。对WinPool产品宿主，本机全局上限为1，不按Agent、用户登录会话、版本、程序目录、数据根或权限级别分开计算。进程退出后可以按需重建；不创建第二个宿主接替仍活着的旧进程。

采用一个固定命名的Win32 Job对象`Global\WinPool.PowerShellHost`实施数量约束；它是进程容器，与存储业务的`MSFT_StorageJob`不同，不引入服务、守护进程或新数据库表：

1. 新建Job时设置`JOB_OBJECT_LIMIT_ACTIVE_PROCESS`、`ActiveProcessLimit=1`；不设置`KILL_ON_JOB_CLOSE`、进程／Job时间上限或breakaway。打开既有Job时先查询并核对这些约束，不能修改活动Job来绕过占用。宿主固定脚本不另起PowerShell、`Start-Job`或其他子进程，不新增`-AsJob`式脱离当前请求的调用。[M4]、[M7]
2. 创建前查询Job成员：已有成员时只核对身份、显示等待旧宿主退出，不启动第二个进程；访问被拒、成员查询失败或约束不符时明确报宿主不可用，不换名称、会话或程序路径绕过。Job权限沿当前用户／系统／管理员的必要权限处理，不为恢复向Everyone开放控制。
3. 唯一启动入口使用`CreateProcess`扩展启动属性`PROC_THREAD_ATTRIBUTE_JOB_LIST`，创建时即加入该Job；继承句柄清单只有三根标准管道及一个仅有`JOB_OBJECT_QUERY`权限的Job保活句柄。子宿主持有后者直到进程退出，确保父Agent在创建后、journal登记前死亡时，固定Job名称仍可重开；不能只依赖内核关联对象的寿命推断名称仍在。禁止先`Process.Start`再`AssignProcessToJobObject`，也不把“子进程启动后自己取Mutex”当成严格数量保证。并发创建由Job上限拒绝，失败明确报告。[M5]
4. Job不设置关闭即杀写；新Agent重开同名对象并等待，准确核实旧成员已退后才创建唯一新宿主。Windows PowerShell在本机需要Windows自动创建的`conhost.exe`；它不是第二个产品宿主。正常退出准入仅识别本准确PowerShell及最多一个经系统路径、原生父PID和创建时间核实的关联conhost，随后等待两者退出；创建新宿主前任何活Job成员仍阻止准入，查询失败不能按已退出处理。权限／会话约束使原子加入失败时保持不可用，不建立另一条启动路径。正常提权／根切换先完成旧宿主退出，随后才启动新宿主。[M4]
5. 首次由V0.61切换时，沿既有退出准入停止旧App／Agent并核实其遗留产品调用；旧版本进程不能靠新Job追溯纳管。身份或旧写状态无法核实时保留屏障，不按进程名批量终止，也不启动新宿主补足功能。

#### 4.1.2 长期无输出的判断与处理

Agent持有启动取得的精确进程句柄并监听退出；进程死亡以该进程句柄的退出信号为准。`WaitForExitAsync`的等待取消／异常不是退出证明。跨Agent恢复复用`ProcessIncarnationVerifier`的PID、完整路径和启动时间核验，但需提供**存活／已退出／不可核实**的明确结果；现有`TryRead()==null`同时包含无权访问和读取失败，不能据此清占用或写屏障。[S47]、[M6]

| 可观察情况 | 可以确定的事实 | 固定处理 |
| --- | --- | --- |
| 空闲时无输出 | 没有待返回请求；不构成故障 | 保持宿主，不发送定时保活请求或定时重启 |
| 有请求且进程存活，尚无result | 进程存在；无法仅凭此区分provider正常等待、阻塞或死锁 | 显示读取／已提交等待系统返回、已等待时长及最后已知阶段，不虚构进度百分比 |
| ready初始化或只读请求超过调用方剩余预算与60 s的较小值 | 请求超预算，不等于已经证明进程死亡 | 原请求到期立即返回失败；该宿主没有可能仍在执行的已发写时，才在后台串行终止准确旧宿主，最多用5 s确认退出，不让原调用额外等待。退出未确认则保持占用与不可用；确认后仅在下一次显式读取需求到来时重建，不重放原请求或循环重启 |
| 写帧已开始发送，60 s仍未返回 | 长时间没有完成回执，不能据超时认定写已失败 | 显示“等待系统返回，结果尚未确认”；不强杀、不重发、不续下一步，不创建只读替身。正常进度状态保留；通信丢失／回包不可解释时按原operation进入Unknown |
| 通道断开或坏帧，但进程仍活着 | 通信失效，不能推定宿主已退或写已停止 | 未发写按只读回收规则处理；可能已发写则保留Unknown和写屏障，等待旧进程退出，不创建新宿主 |
| 精确宿主进程已退出 | PowerShell宿主已死；不证明Windows后台写也已结束 | 未发写请求失败；可能已发写转Unknown。全局占用归零后可按需启动唯一新宿主做只读核对，禁止重放原命令；仍按既有恢复证据决定能否解除写屏障 |
| 旧身份、进程退出或Job占用不可核实 | 当前证据不足 | 保持宿主不可用与适用写屏障，给出具体错误，不把未知当已退出 |

CPU为0、没有stdout、心跳缺失都不能证明写已失败；即使另设心跳，也只能证明心跳线程／链路活着，不能证明cmdlet正在推进。本版不增加独立心跳线程、第二runspace或旁路WMI轮询来伪造这一判断。等待时长使用Agent本地单调计时和现有状态呈现，不为每秒刷新增加数据库写入或重复消息。

回收条件针对当前宿主有无可能仍在执行的写。旧宿主已经退出后留下的durable Unknown，不会让新的只读宿主永远不可回收；该新宿主仍按只读超时规则处理，原业务Unknown和写屏障不因此清除。

单宿主长写期间，模拟编辑／保存、已有监控会话、轻控制和缓存浏览继续按各自边界工作，实时存储刷新可以超时或返回忙；不把缓存标成fresh。已发写若永久不返回，则无法同时承诺自动恢复时限和不打断未知写入：保持保护状态并报告，由用户另行决定是否进行系统恢复，不自动强杀或重启Windows。人工终止／系统重启也不等于取消成功；恢复后先只读核对。宿主退出、一次没有StorageJob、一次未见目标均不能单独证明后台写已终止或允许重放。

#### 4.1.3 实施顺序与最小验证

按以下顺序接线，不留给执行者另选恢复架构：

1. 在唯一宿主实现中加入Job安全句柄、原子创建和精确进程退出观察；Agent `Program`注入单一实例。先验证进程创建数量，不改业务写入语义。
2. 接通ready／started／result帧和请求阶段，保留单reader；把三态身份核验接入恢复，禁止将读取失败转换成已退出。
3. 接入现有CallIssued／HostCall耐久边界，然后迁移真实写；宿主进程退出与写结果核验分别处理，不能把Job成员归零当作业务写屏障已解除。
4. 接通上述只读超时、通道故障、父Agent死亡、退出／提权／根切换分支，再删除旧runner。真实provider写不用于故意制造挂起；故障分支用现有可控替身和临时进程验证。

最小验证复用现有runner／backend／Agent测试：连续请求同一PID、读→写→读、模块一次、错误输出不破帧、局部变量不串请求；补只读超时重建前旧进程已退出、写静默不强杀、写后丢响应不重放。数量边界集中验证三组：①父Agent在原子创建后、登记前死亡，旧宿主仍活时新Agent不能启动第二个；②长写／通道丢失／权限不可读时只读替身也不能启动；③旧宿主退出后正常、提权或根切换只建立一个新宿主，原Unknown仍需核对。原始Job成员数／进程化身与结果一起核对，不故意打断真实存储写。**验收观察实际生产入口和生命周期，不以源码含Runspace、Mutex或new Process字样代替。**

### 4.2 W2-A：一个事实批次，按用途投影

**当前确定残项：**

- Collector 的 Local/Hardware/Scoped/Fresh 入口各自 BeginGeneration；共享 helper 没有让生产启动调用者共用一代。
- Storage→Hardware 启动读取、结构支持逐成员调用、范围前后观察仍可能多次整机枚举。
- 精确成员能力 reader 使用 System.Management/WMI，不是全部由 PowerShell 执行；只改宿主不会自动消除它的重复调用。
- SystemId 已缓存，但机器 binding 的 Registry/WMI 仍按调用重读。
- `RealScopedInventoryRefreshPolicy` 仍把两份 SourceFacts 完整序列化后比较。[S3]、[S7]、[S8]、[S9]

实施：

1. 由现有启动/读取用例拥有 generation 和完整覆盖范围；Storage、Hardware、monitor 身份在同一轮只读需求上共享结果、分别投影。公开调用者必须传入/加入同一批次，测试调用 helper 手工给同 generation 不算完成。不同真实写边界和明确 fresh 刷新仍建立新观察，禁止用固定 TTL 充当临写证据。
2. 把 live inventory、scoped inventory、partition/VD range、structure support 收进一个有类型的存储读取用例，固定 `scope/purpose/query` 和响应类型；缓存正文读取、目录 metadata、内存 GetSnapshot 的职责仍明确。按 D1 裁决删除旧消息/client/handler，不留下多条执行同一读取的入口；不得用无类型任意 Read/command 字符串代替。
3. 一次结构Prepare批量取得所需能力。子系统通用支持字段按`generation + subsystem identity`复用；partition／VD／tier范围和格式化支持另绑定准确目标、成员集及layout／filesystem，不能按同一子系统误共用目标范围。删除逐成员前后Capture放大；范围只供UI，不授予写权限。
4. 每批建立一次关系索引，保留现有固定模板/scoped 参数方案。provider discovery 只在所需失效边界执行；其失败不能抹掉成功物理枚举，缺物理事实不能进入无效 extent 回退。Medium/High 都复验旧故障分支。
5. Agent 生命周期持有稳定机器 binding；明确 rebind、根切换、Agent incarnation 变化时失效。目标盘、保护角色和关联仍 fresh；不能把不变机器身份与易变存储事实混为一类。
6. 同次进程证据读取复用可信 witness。现行 TryReadWithIntegrity 已合并句柄读取，按真实重复 OS 调用进一步收口；保留 PID 复用、路径、启动时间、SID、权限语义，不以三个类名相似为由删除。
7. 内部复用以批次 owner 的 identity/revision/hash 和覆盖范围判定，删除两次 SourceFacts 全文 JSON 比较；不能只比较缺少源证据字段的 projection fingerprint。JSON 仅在真正存储/进程边界编码。

完成条件：生产冷启动一轮覆盖完整的事实可供三类消费者使用；无外部变化的连续使用不重复采同一代。单个结构 Prepare 不随成员数重复整机枚举；同批同子系统能力查询一次。显式刷新与每次临写仍能看见外部真实变化。

### 4.3 W2-B：写前 fresh、写后窄核验，去掉重复层

保留当前 typed preflight→Execute、成功后态复用和 Agent-owned 结构作业。进一步按命令实际需要收窄：

现状已经优先scoped采集；主要残余是按物理／池连通闭包取得过宽字段及重复遍历，缺scope定位／部分恢复才回到full。实施时从`WindowsRealStorageTopology.RequireSource`等检查提取每类命令所需证据，不能只缩脚本查询而保留“八类源全部Returned”的旧要求。明确区分本次未查询、查询成功但为空、读取失败；coverage只覆盖实际检查过的对象／删除域。临写与后置条件消费raw窄事实，UI合并另做；job／monitor仅在覆盖满足需求时复用，显示缓存不冒充新鲜事实。MAX的两份无变化证明仍覆盖实际比较的完整受影响组件。

- Prepare 使用明确来源的当前批次生成冻结的用户意图和确认范围；已有事实足够时不为每次按钮重建整机快照。若需要新事实才能给出可信确认，合并成一次必要读取，并在端到端计时中如实计入。
- 用户确认后的执行门进行**一次新的目标身份、成员闭包、角色与命令所需字段核对**，形成 typed StepContext，执行直接消费。App 确认时的旧 facts 不替代临写 fresh；变化导致确认范围、目标或参数不同，则拒绝并要求重新确认，不能静默扩大。
- 保留 Agent 对真实命令的权威规划：新建对象身份在 Windows 执行后才产生，后续计划必须依据已验证后态映射。App 不持有任意已授权命令序列；共用草稿差分不等于相信 App 发来的执行结果。
- 把“所有字段/所有 MSFT 状态必须 Returned”改为命令所需字段集合。对改名、联机/脱机、分区扩缩/格式化、池/层/VD 建删、MAX 分别明确所需身份、关联、保护/加密状态、能力和结果字段。不相关遥测缺失不阻塞改名；关键字段 Unknown 不能当默认值通过。
- 正常写后先读准确受影响对象和必要关系，判断命令后置条件。首次即满足，立即发布同一 verified 后态供 job、inventory、monitor、App 使用；不得再成功 Reconcile、monitor 重采或 UI 兜底采集。
- 首次尚不可见时只做有截止时间的窄探针/相关 StorageJob 查询，退避与取消按剩余预算传播。必要升级完整事实最多一次且标明原因；不在每个 2 秒 tick 重采整机。正常改名等同步场景不固定等待 2/60 秒；异步 Windows 工作如实显示仍执行，不以本地超时冒充已完成。
- StorageJob只有provider实际提供可核验job identity时才缩到准确关联；当前写脚本尚不提供有效ProviderJobId，MAX使用的全局job终态证据不能凭名称筛选后削弱。缺准确关联时保留原证明范围并单列成本。
- 时间预算作用于整个只读观察：provider、同步 WMI、job/safety 查询及 delay 都接收剩余 deadline。仅在调用前检查 token 或把阻塞 WMI 包进 Task.Run 不算实现中途超时。无法中断的只读底层调用必须有受控超时/隔离和有限未完成数量；不能每次重试遗留一个后台阻塞任务。
- 删除各层重复计算相同计划合法性、hash、重复日志/异常包装。保留输入反序列化、耐久记录、临写事实与写后不确定性这几处有不同意义的边界。现有 RequireFrozenStep 已不再全量重算，不为修改它再引入新校验。

**计数合同：**正常单个写边界是一份 fresh 前态＋一条冻结 typed 写＋首次窄后验。必要 Prepare 观察、明确异步后验和 MAX 候选要单列；不可把它们隐藏在统计外，也不可用整个操作固定“两次读取”删掉必须的新鲜事实。端到端时间仍必须达第 6 节。

### 4.4 W2-C：MAX、恢复、内存与状态版本

1. **MAX 策略完整保留。**使用现有唯一 attempt；`G=1,073,741,824`、`R=4,000,000`、`C=floor((A−R−1)/G)`；真实明确拒绝且证实无变化后按 1 GiB 搜索，多层按既定半 C seed 和冻结层顺序。不得改二分、固定扣 2/4 GiB、UseMaximumSize，也不以性能为由缩减容量合同。每候选复用必要前/后态，容量拒绝仍需两份无变化证明及相关 jobs；Unknown 立即停止搜索，不改候选重发。
2. Query/List 继续只读；启动恢复和用户明确重查只解释已发生结果。收到 Unknown 后停止自动调度，不按计时器无限对账、不产生新 OperationId。**重查用尽不能自行解除写屏障。**新确认仅对明确剩余目标，绑定原草稿范围/identity/hash。
3. 将普通操作`verifiedPostStates`改为执行返回的局部交接值，结构阶段仅短暂持有并消费一次；普通成功、失败、Unknown、取消结束时释放。释放backend的`operationScopes`及单步stop记录；结构job的stop／child已经在finally清理，保留并验证其子operation覆盖。不新增LRU或定时清理，不能因内存清理丢失耐久journal。
4. **保留现有结构job单调版本。**虽然`StructureStatus`取UpdatedAt毫秒，持久`TransitionStructureJobAsync`已原子使用`MAX(updated_utc_ms+1,$at)`；原稿“时钟回退必使版本倒退”的判断不成立。沿现有实现验证回退、同毫秒更新与重开读回，不增加独立revision列或专用迁移；App按jobId＋goalHash＋持久版本拒旧，不新增polling。[S10]、[S11]、[S46]
5. 普通 operation 与 monitor 已有 revision 的修复继续保护；同值/重复事件不刷新，旧实例事件不覆盖新实例。轻终态到达即可结束等待，不依赖大 evidence 全部下载（接 W4）。

完成条件：多次不同 ID 普通操作后不再保留全部历史 facts/contexts；结构阶段仍消费正确后态；UTC 回退、同毫秒多变更、重开/reseed/重复迟到事件都能到达正确终态；任何丢写结果分支不重放写入。真实设备验证与故障替身分别承担其能证明的范围。


## 5. W3–W5：交互性能、数据链与开发减负

### 5.1 W3-A：模拟在 App 内完成编辑，合并耐久保存

**当前事实：**名称 TextChanged 只更新本地状态，Enter/按钮或结构 Apply 才进入提交；提交仍等待 Agent CAS 后再发布“已保存”文档。不要以“每次敲键都走管道”作为优化依据。当前实际入口见 `DiskPartitionPage`、`StorageStructurePage`、`WorkspaceViewModel` 与 `SimulationEditCoordinator`。[S12]、[S13]、[S14]、[S15]

按 D2 提案实现一个具体闭环：

1. 保留输入完成手势与结构Apply，不把半个名字／非法结构提交。App共享reducer得到当前revision的合法结果后立即应用本地版本，排入300 ms空闲保存；未验证候选只显示预演中。无在途保存时，自最早未保存合法编辑起最迟2 s发起；有前批在途则到期标记，前批结果明确后立即接续，排队时间独立记录。达到字节阈值提前flush；故障时暂停而非重试风暴。保存不经过旧模拟authority／executor。
2. App 预览与 Agent 耐久提交调用**同一套纯编辑规则**。共用结构/分区所需的差分、几何、角色和容量规则；清除两页之间及 `SimulationDraftPlanner/RealStructureDraftPlanner` 中实际重复的纯差分，不凭类名把真实 capability/执行规划并进模拟。真实规则与模拟估算的不同含义用明确输入/结果表达，不增加通用 planner 框架。
3. 每文档最多一个保存请求在途。`Document.Revision`按耐久batch加一，`SourceFacts.Revision`保留按逻辑reducer步骤累计；本地content revision独立。组批不重冻创建ID／时间；新工作模型最终生成一次facts但结果hash须与同序列旧规则等价。回执只确认对应前缀，不用旧全文覆盖后续本地内容。普通批1 MiB提前flush，单个原子intent及全部待保存编码各限64 MiB；超帧原子Apply走W4一次CAS，不分成部分成功提交。无法无损合并且触限时暂停新Apply并保留现有内容。
4. Agent 继续作为 SQLite 唯一持久化 owner；commitId＋base revision＋intent/hash 在同一事务完成 CAS 和回执。丢回执先查同 commitId，不创建新 ID 猜测成功；冲突保留本地内容并显示选择，不静默最后写入者覆盖。
5. 保存失败不回滚人类正在编辑的内容，也不显示“已保存”。提供原处重试/保留草稿信息；明确关闭前可重试、导出当前草稿副本或确认丢弃后退出。新方案不承诺崩溃后恢复尚未耐久保存的内容，不另建 App 写库/草稿日志系统。
6. 切系统先触发该文档 flush，但不冻结其它文档操作；失败保留该文档内存草稿与失败标记。导出先 flush；失败时明确让用户导出当前本地草稿，不能悄悄导出旧 Agent 版本。删除/重置先停止该文档接收新动作，再取消尚未发出的保存；已发出或提交结果未知的批次必须按原 commitId 确认结果，再对明确 durable revision 执行删除/重置。取消等待不等于取消已提交写入。删除/重置确认完成后废弃旧文档 generation；迟到回执与删除标记按文档代次处理，不能使已删除文档复活。
7. App 关闭、Agent 托盘退出、提权、根切换复用一个 lifecycle：停止接新编辑，flush 已完成合法编辑，处理失败选择，然后执行原交接。Agent 从托盘发起退出时，必须先让 App 完成 flush/失败选择，再封禁保存请求、关闭 pipe 或进入不可保存的 Closing 状态。真实写执行中仍遵守已有继续/停止后续步骤语义；不为模拟退出杀真实宿主。
8. 保存等待与完整预演在后台完成，结果绑定 documentId＋binding generation＋content revision；过期结果不能覆盖新草稿。一次有效 revision 在 App 只跑一次完整预演 reducer；Agent 对一个 durable batch 执行一次同一规则的 reducer 是独立保存边界，另计而不删除。最终 Apply 必须消费当前 revision 的结果，不能用防抖尚未完成的旧“可应用”。

具体落点：`SimulationEditCoordinator`／`WorkspaceViewModel.CommitSimulationDocumentAsync`拆本地应用与耐久完成反馈；两页“已写入模拟系统”只在对应版本耐久后显示。沿用Apply前草稿Undo／Redo，合法Apply后清草稿历史，不新增跨Apply全局撤销。CAS冲突停止该文档自动保存，保留合法本地内容和未完成表单，可重试通信／导出副本／明确丢弃并载入Agent版；不自动rebase，结果未知先查原commitId。`StorageSystemRepository`全局gate改为每文档保存owner＋必要短metadata锁，A的预演／通信／回执等待不占B的owner，各文档本地编辑不等待落盘；SQLite的短写事务仍允许串行，不承诺两个数据库写事务同时执行。

退出复用现有生命周期做两阶段协商：`AgentSessionCoordinator`在进入Closing前发送绑定shutdownAttemptId和App进程化身的prepare；App停止新编辑、flush并处理失败选择，明确完成或用户丢弃后ack。确认后才进入旧shutdown／拆订阅／发AppExitSignal；取消或超时保持可保存，不执行`MainWindow`原finally无条件关窗。补`DesktopAgentRuntime`当前空通知与直接退出路径，提权及根切换调用同一入口；`DataLocationSwitchRuntime`现有no-op Quiesce不算已接通。仍有已发真实写或无法确认退出的旧宿主时按W1拒绝关闭，不借模拟退出杀写。

完成证据：可快速预演样本的十个合法动作按50 ms间隔到达，空闲后合并保存一次；持续输入且无在途时按2 s调度，慢前批时仅一在途、确认后接下一批。记录调度／排队／耐久三段，不把2 s称为落盘保证。覆盖两个文档、冻结ID／时间与批次hash、旧receipt、CAS失回执／冲突、Undo、导出／切换／删除／退出失败取消；无全文plan/events正常往返、无App写库，保存标志只对应已耐久版本。

### 5.2 W3-B：一次工作模型与局部 UI 更新

1. `StorageSystems.Reduce` 当前仍逐 step Rebuild/Validate/SourceFacts↔Snapshot 投影。改为一份计划内工作模型：必要关系随步骤更新，最终边界统一生成文档/源事实/图验证；保留每步真正依赖的规则，不能为计数删除约束。相同基线、相同编辑的结果/拒绝行为等价。
2. 合并高频输入的完整预演，后台计算；UI 线程只做局部输入反馈和应用最新结果。小修改不全量生成按钮/说明/比较列。现有缓存消除不变 revision 的重复工作后，仍须降低**变化 revision**的实际 CPU 成本。
3. `WorkspaceViewModel.RebuildTopology` 不再清空全部已加载拓扑根；RebuildObjects 及相关 setter 只更新其实际受影响范围。使用现有 documentId/revision 与 VM 实例，只替换变更文档的根；切换已加载未变文档直接复用，选择只更新高亮和该对象详情。
4. 比较列不因每次选择全生成；一次 commit 统一发布一次变更，删除 setter 与显式调用叠加的 details/comparison 刷新。仅语言真的改变才重建本地化文案。
5. 保护已有同进度短路：100 次同值真实进度额外 topology/reducer 为零。拆页面时按纯规则、输入状态、呈现责任删除重复，再利用现有 partial；不以每文件 ≤600 行为完成条件，也不新增 ViewModel 框架。

完成证据：约 530 KB 和 3 MiB 合法样本文档的输入、撤销/重做、切换两系统、选择和单文档提交达到第 6 节；无关根/比较列重建为零。更大文档功能正确并保持 UI 可响应；不把小空白 fixture 的结果外推为大文档性能。

### 5.3 W4-A：控制管道解除队头阻塞

当前 `NamedPipeAgentConnection.sendGate` 覆盖完整请求等待，服务端同连接等待 handler 完成才读下一条。去掉 monitor 轮询并没有解决长 Prepare/range/provider/export 阻塞短控制。[S16]、[S17]

本版采用**现有 correlationId 上的有界请求复用**，保持一个协议实现：

- 客户端只有一个 control 响应读取循环，按 correlationId 分发；发送锁仅保护一个完整帧的写入，不跨越等待响应。禁止多个调用者同时直接 Read 同一 stream。
- 服务端一个解析循环、响应帧写锁；长只读 handler 在有界在途集合执行。轻状态/停止后续步骤/生命周期请求使用独立于长 handler 的轻处理额度，不占长任务在途名额；长任务额度耗尽也能响应这些请求。并发只读上限及队列有明确常量，超额返回 busy 或可取消等待，不能无限累积。
- 真实 mutationGate 仍只有一个权威写用例；并发请求不能获得第二写权限。读写共享宿主的限制由 W1 管，不把 pipe 并发等同磁盘写并发。
- 取消/超时解除本次等待与 pending 字典；迟到响应按已失效 correlation 丢弃或由现有 operation/commit 身份补取。丢写响应仍按原 ID 查状态，不自动重复 Accept/write。
- 断连、重连、版本不匹配、App 关闭都释放 pending/subscription；初连取得一次当前状态是必要同步，之后靠事件。事件缺口/reseed 只做一次相关补取，不恢复稳态 polling。
- 对旧源码字段/调用顺序的字符串测试随 W5 删除；用现有实际命名管道替身验证“长 provider 阻塞时短控制仍按预算完成”和关闭后的 pending 清零。

具体改动：同时替换`SendAsync`／`SendConnectedAsync`的sendGate和requestGate完整往返锁；初连snapshot也纳入唯一reader／pending，连接代次与idle lease不得让旧流响应匹配新请求。单请求取消仅取消自身，不能沿用`DisposeStreamAsync`关闭其他在途请求；整连接故障才统一结束本代pending。服务端复用`AgentSessionCoordinator`的stateLock／shutdownGate做接纳与在途计数：退出prepare阶段仍接受flush／commit lookup，ack后拒新普通请求、等待已接纳任务，再关闭连接。

初始调度限额固定：长只读handler同时2个、普通等待16个、轻控制同时8个、单连接pending最多32个；轻控制不等待长任务名额，饱和明确busy。真实写gate仍为1，宿主队列另按W1限制。按同一correlation只完成一次；批量传输分块之间让出发送权，不能长时间连续霸占控制流。数值只有实际预算不能满足时再按测量调整，不建通用调度框架。

模拟commitId幂等检查必须随并发一起修改：当前runtime与repository的lookup在CAS事务外；在同文档owner／同提交事务内再确认绑定，两个相同commitId并发返回同回执，异意图拒绝，不能第二次误报CAS冲突。新增一项实际并发重复提交回归，不靠旧串行用例推断成立。

这是处理一个已知控制通道问题，不新增通用调度服务、持久 read job 系统或第二套连接 fallback。

### 5.4 W4-B：统一文档和证据的字节合同

**现状：**导入文件 64 MiB、raw document 3 MiB、IPC frame 4 MiB；JSON 转义还会放大 frame。Evidence 按 step 数分页不能约束单条字节，轻终态当前又依赖先取完整 evidence。[S18]、[S19]、[S20]

按 D3 提案实施：

1. 单一文档合同：输入文件UTF-8字节≤64 MiB，解析后完整可导入envelope的规范UTF-8编码也≤64 MiB，导出使用同一无缩进规范编码。保存／加载不能仅校验内部body而忽略导出外壳；字节超限在昂贵解析前拒绝，规范化超限在提交前明确拒绝，不留空目录项／半文档。大型原子simulation intent独立限64 MiB，不能依靠分块接受无限大的一个提交。
2. 保持 4 MiB 的完整 IPC frame 限制。为文档正文、单条 evidence，以及确实超过单帧的原子 simulation intent 增加小范围分块，使用有界字节块（例如每块 512 KiB，按实际编码后的 frame 再校验），避免把原 JSON 再当字符串转义形成无上限膨胀。
3. 传输绑定 document/operation identity、revision/hash、总长度、块序号；大型 simulation intent 另绑定 commitId/base revision，完整接收后一次 CAS，不能分块提交或部分应用；文档 64 MiB 是编码长度上限，不是解析对象图的内存上限。大传输最多同时 2 个，传输缓冲总预算 2 MiB、受控临时文件总预算 128 MiB；超额等待可取消。证据每次按需读取窗口最多 64 MiB，声明原始总长度和剩余范围，不能截断后称完整。解析/投影另按最多一个大型任务执行并实测峰值，不能同时解析多个 64 MiB 文档。完成且 hash/结构校验成功后才原子提交。取消、超限、断线、换 root 释放临时资源；不增加通用文件同步、续传数据库或多套 codec。
4. 模拟正常编辑仍只发送合并的 intent＋receipt，不能因有分块就恢复每个动作发全文。导入/首次正文/明确补取才传正文，metadata 仍轻量。
5. 轻状态补齐SystemId、operation／jobId、PlanHash／GoalHash、revision和必要结果身份，让`RealOperationStatusCache`、`RealOperationRecovery`、`WorkspaceRealOperations`、`RealOperationSubmissionState`直接消费；不得伪造空Plan。状态到达即可结束等待并正确呈现Succeeded／Failed／Unknown，大evidence失败不把它退回Running。Unknown仍阻止新写；已核验终态沿现有相关事实／草稿更新路径解除屏障，该更新不依赖下载完整历史。大证据按wire预算分页，单条超大返回准确摘要与按需块，不伪称已取全。
6. inventory事件统一只通知documentId／revision／hash及正文引用，正文经唯一control加载／分块路径传一次；同revision合并读取，缺事件重连补一次最新缓存。引用已被更新替代时返回最新身份，不能用新正文冒充旧hash；在途传输绑定一份不可变编码结果。正常saved response只返回metadata／receipt，不再回全文。相同投影但源证据不同不能误判同一批次。

所有入口同时迁移：`DesktopExportService`导入／导出、`SimulationDocumentCodec`与`LocalInventoryDocumentCodec`、`SimulationDocumentRepository`／`LocalInventoryDocumentRepository`、simulation save/load request与response、inventory cache加载／更新事件及`LocalInventoryObserver`、大intent和LoadEvidence。通过同一编码／分块实现，不为每入口另建codec；当前导出缩进导致回读超限的问题随规范envelope口径修复。

覆盖正常、边界附近、超限、引号／转义密集、单条大evidence、断线半传输和终态恢复；至少验证3 MiB旧边界、接近64 MiB合法复杂文档的导入→保存→导出→再导入及超限拒绝。用W0固定样本记录UI取消反馈（≤500 ms）、后台停止／暂存释放、解析峰值和完成后回收；出现OOM或残留增长即失败，不能以2 MiB传输buffer冒充整个对象图的内存上限。大输入耗时单列，不以空白填充当复杂文档证明。

### 5.5 W4-C：监控只处理需要显示的新数据

1. 保留 Agent 唯一采样/持久化、批次序号/gap、App 唯一缓存。采样仍支持 0.2–20 Hz，不能靠减少实际采样或隐瞒 drop 提速。
2. 分开采样、传输与渲染频率。建议图表最多 10 Hz 刷新且只在可见序列 revision 改变时工作；后台/隐藏图不复制 60 秒全窗、不创建 shape。一批可包含多个真实样本，20 Hz 持久化不变。
3. `GetWindows` 不再每 tick 对所有 series ToArray；只取显示目标的有界窗口。减少中间数组，复用可复用几何/视图数据；`DiskActivityGraphControl.SetSeries` 在同 revision 直接返回，不 Clear 全 Canvas 后重建一切。
4. 删除 500 ms 偏好轮询，消费已有设置变化通知；关闭/切页释放订阅。告警和图用同一份样本/角色信息。
5. 删除 App 的 null-agent sampler/CSV/local loop；测试注入 fake connection。CSV 从 Agent 实际持久样本导出，不能生成第二份旁路数据。
6. 没有独立 OS counter 的物理池成员仍显示 Unsupported，不借用 VD counter；身份失效、Agent instance 变化、重连缺口都真实显示。

渲染失效键包含Agent／session、样本revision、gap／reset、显示目标集合以及颜色／主题／尺寸；同sample revision改变勾选或resize仍要更新。偏好直接订阅现有`WorkspaceViewModel.CurrentAgentPreferences`变化，不能只删timer却漏掉设置更新。

完成证据：20 Hz 持久样本/CSV/gap 正确；可见图与隐藏图的采样结果一致；隐藏时 60 秒窗口复制/图元重建为零，同 revision 渲染为零；按第 6 节记录 UI 时间、CPU 和分配，不只检查 GetSnapshot 调用为零。

### 5.6 W4-D：SQLite 只写有消费者且确需耐久的数据

1. **确定删除写入：**`storage_objects` 和 `storage_relationships` 在生产仅由 InventoryRepositories 写入、无 SELECT 消费者；`inventory_comparisons` 仅存在 schema。删除对应重复插入/死 API，并用精确迁移处理旧表。生产 `InventorySnapshotRepository.Get/List` 没有调用者，随无用途历史读取 API 退役。[S21]、[S22]
2. 明确选择`local_inventory_document`为最新本机正文唯一持久owner，退无生产历史读取消费者的`inventory_snapshots`及projection／repository调用。20→21迁移重建local表，保留当前正文、hash和snapshot_id作为capture identity，解除其旧snapshot外键；先退对象／关系／comparison外键再退snapshot表。把旧`InventorySnapshotRepository.SaveAsync`中的systems upsert并入当前文档保存事务，`AgentInventoryCoordinator`改为一次该事务。真实journal／Unknown证据、模拟文档和monitor归档全部保留；若实施前发现本文未覆盖的真实消费者，先报告具体调用者再调整删除范围，不留下双写。
3. WinPool/Monitoring store 不再每逻辑操作关闭连接后重付全部初始化成本。沿现有 store 实现受控连接生命周期/池复用；journal_mode 在数据库初始化设置，foreign_keys/synchronous/busy_timeout 等连接属性必须在每个实际物理连接保证，不能把四条 PRAGMA 一起移走导致失效。
4. 原子 commit/CAS、唯一 writer lease 与 busy/事务行为保留。root 切换、轮转、归档前关闭相关实际连接/池并释放文件句柄，不能为了 pooling 破坏 Windows 文件移动。
5. Agent 每个只读控制请求不再写 SQLite heartbeat：活性在内存及时更新，耐久注册/退出和必要间隔合并写；仍满足崩溃/租约判定的既定语义。关机 flush，不新建 heartbeat 数据管线。
6. 删除同事务内重复全文编码、无消费者对象副本；序列化一次供实际需要的存储/传输边界，不新增通用 ORM 或 repository 层。

验证聚焦现有 SQLite/迁移/根移动/归档测试：升级前后文档与仍需证据一致，busy/读写并发行为正确，无残留锁阻止移动，控制只读高频请求不同比增加 DB 写入。最终人工检查便携/标准根往返、归档轮转和恢复。

### 5.7 W5-A：具体退役清单

| 当前确定仍在的负担 | 本版处置 | 保留的真实职责 |
| --- | --- | --- |
| OperationPlanning 中 DefaultOperationPlanner、FixedOperationInventoryVersionSource 及旧接口；OperationExecution 中 ExecutorGate/AuthorizedOperation/LocalStorageMutationExecutor；SafeOperationExecutors 中 ReadOnlyWindowsExecutor/ReplayExecutor/IReplayEventSource | 删除已无生产构造、仅内部互引与测试维持的通用执行族；连同转发/协议残壳和无价值测试 | Agent 仍在用的 InMemoryOperationAuthority、OperationPolicyEvaluator、真实冻结授权/执行 gate 不能整项目删除 |
| TryParseGigabytes 字符串 MAX | 删除 parser 与仅为它存在的用例 | FormatGigabytes、typed MAX、ResolveAsync |
| App null-agent 监控/CSV/本地采样 | 删除并强制实际 Agent owner | fake connection 测试，Agent 采样/导出 |
| 两套一次性 PowerShell 运行器/每请求模板编码 | W1 替换后彻底删除 | 一个常驻固定命令宿主 |
| 无读消费者的对象/关系/比较表和 API | W4 精确迁移退役 | 文档、恢复、实际历史用途 |
| 重复的校验/hash/异常记录、空转发接口、页面重复投影 | 修改相关 owner 时直接消除；完成前一次源码调用者确认 | 输入、进程、持久化、临写和写后边界 |
| 已在 V0.61 删除的旧 Inventory/provider/RPC/factory/alias | 保持退出，不再另建重复任务 | 现行路径 |

F 所称“17 个全仓零实现接口”不是可靠删除依据：preferences、shutdown actions、monitor diagnostics/database access 等存在真实实现及消费者。每个被点名接口在本次实施范围中要么删除无用途转发，要么说明一项具体保留职责；不能用“只有一个实现”作为必须删的理由，也不能为保留接口虚构第二实现。

文件真正退役时遵守现有仓库 Old/Archive 约定和 Git 历史，不能仅搬进另一个生产目录或继续编译旧壳。不得清理用户 Ref 或为了行数销毁原审查/测试证据。

### 5.8 W5-B：测试减负、真实 skip 与单次完整门

**当前可确认的退役量约 60 个执行 case，不能承诺砍到几百。**当前 2,504 为报告的展开用例数；静态 Fact/Theory 数不等于执行 case。新增必需回归后净数量可能不同，不以总数验收。

| 测试范围 | 当前执行 case | 本版动作 |
| --- | ---: | --- |
| ArchitectureBoundaryTests | 44 | 删除约 39：2 文档、3 旧项目黑名单、2 SDK/TFM 字符串快照、32 UI/私有字段/调用顺序源码快照；保留约 5 个稳定依赖方向/纯层/资产边界 |
| SafeOperationExecutorTests | 8 | 随无生产消费者执行族整组退役 |
| ExecutionSecurityTests 中 DefaultPlanner/ExecutorGate/LocalStorageMutationExecutor | 3 | 只删旧族用例，其它真实授权/目标/一次令牌测试保留 |
| RealVirtualDiskCreationCapacityResolverTests 中旧字符串 parser | 10 | 随 parser 删除；如极值格式化仍有意义并入现有格式化用例，不维持旧 parser |
| 合计 | 约 60 | 架构源码快照约减 89%，不一对一新建替代镜像测试 |

当前私有字段`_closingForElevationHandoff`红灯随源码快照退出，不恢复旧变量。已有App.Tests不编译MainWindow／Workspace，现有observer取消与启动参数用例不能证明完整handoff行为；在W3生命周期实际拥有者补“关闭后迟到Ready／刷新不生效、失败取消仍可保存”的直接验证，并在W6实际UAC接受／取消路径复验，不一对一新造字段拼写测试。

必须同时修的已知失真：

- `InfrastructureTests` 无参 LocalUserPreferencesService 仍写真实 app-settings，改为现有构造参数注入临时根；finally 恢复不算隔离。
- `BuildOperationGuardTests` 六处非 Windows 裸 return，共 11 个展开 case，改显式 platform skip；缺 pwsh 也说明实际依赖。这里 pwsh 是**开发脚本测试**，与产品运行所需 Windows PowerShell 5.1 区分。
- 现存 WMI 磁盘查询、真实 sampler、full Hardware、Storage+Hardware 四个默认主机触点改明确 opt-in；合成测试不能落回真实 WMI。现有 fixture 能力注入和 TestDiagnostics sink 保留。
- guard 继续测试实际 PowerShell 脚本，以临时根和准确运行树隔离环境；修全局进程名耦合，不另写一份 C# 假 guard 取代真实入口。
- 归档 RoundTrip 当前已有 WaitForIdleAsync 与 LastError 条件，不是简单睡 30 秒。只针对仍能复现的记账完成竞态修已有完成条件；不得继续增加固定 sleep 或放宽超时来隐藏问题。
- 现有三项 GiB/7z ManualPerformance 及 read-only admission 在 W6 Windows 集中批次启用，补齐本版已知未测项；日常仍明确 skip，不记 pass。
- 已有 FullCalls==3 和一次 revision 构建预算，不再宣称“全仓无预算”。把修改后的预算接到生产入口和真实 owner，不新增相同 helper 的镜像测试。

按实际风险保留准确身份/成员保护、写前耐久、Unknown 不重放、MAX 拒绝证明、CAS/失回执、迁移/归档原件保留、monitor gap/重连等。历史命名的 V048/V049 测试和 SimulationEditCoordinatorTests 仍覆盖当前行为，不能按版本号整删。

**日常验证规则：**

- 文案/当前文档修改：只核对变更链接/事实；可逆布局修改通常编译即可，不额外编写控件拼写测试。
- 行为修改：只跑对应现有测试类/必要集成；新增测试只针对实际错误可能性，优先扩展已有测试。
- 共享链稳定后，由一名执行者收一次完整 Release 门；期间不让三个审查者分别构建和争用设备。
- 失败后只重跑受影响范围，源码／依赖／运行输入未变的结果可复用；按测试项目记录实际build及被测依赖hash。跨项目公共行为改变时扩大到所有受影响项目；无法证明旧结果仍适用才扩大到全套。不累加重复passed，不把最终有效汇总称最后一次整套全绿。**用户已确认此规则**，不再引入“每次二进制修复重跑全套”的附加门槛。
- 不新增全仓测试分类平台、PR 审批台账、逐函数接口测试或长期指标服务。一次部署所需的证据集中归档即可。

### 5.9 W5-C：文档整治及维护方式

| 唯一事实位置 | 保留内容 | 本版删除/修正 |
| --- | --- | --- |
| README/README.zh-CN | 定位、下载、启动、基本能力、帮助入口 | 重复 DIP 尺寸、内部管线、过时 AutoVD/AutoPart 可选承诺；两语言语义一致 |
| Product | 当前产品语义、保存/退出、支持范围、MAX 和真实边界 | “V0.61 已闭环”与旧 0.62–0.69 承担剩余工作的路线；改为本计划全部已知工作在 0.62 |
| Development | 各用例唯一 owner、必要构建/运行约束 | 冗长历史、重复性能数字、按每个小改动更新多文档的要求；按实现更新常驻宿主/读写/保存/IPC |
| Quality | 最近有效 build/测试/实机结果、未通过门与证据链接、最小命令 | 累计 2500 误作本次全量、UI 选择“已全局完成”等过宽表述；不抹掉旧运行事实 |
| CHANGELOG | 重要用户变化与版本结论 | 重复整套实验日志/逐文件工作过程 |
| AGENTS | 安全/目录/授权和简短工作规则 | 旧阶段状态、强制重复报告/验证的冲突；遵从本次用户目标 |
| docs/Plan.md | 本文范围、阶段状态、最终覆盖索引 | 不再有第二份活动计划；按工作包更新，不逐次写新审查 |
| Review/Archive/Reference | 原审查、历史数字、可追溯证据 | 修链接/加明确基准说明，不改写历史数字或删除用户旧证据 |

归档V0.61 Plan的移动后相对链接在本轮计划激活时修复，只改路径、保留历史正文与原提交／原hash说明；按复核后的实际数量记，不伪造全仓链接灾难。继续采用现有docs/Archive，让默认阅读只指向当前入口，不为改目录再维护一轮全仓引用。

F 提议的 DesignTables 与 R 的“一事实一处”按用途协调：纯实现尺寸/动画由实际 UI 代码拥有，不复制另一张表；真正独立的产品设计规则放现有设计文档并链接。2026-10-16 的 30.94 s/247.69 s 不改；旧 latency 文档的数字不改，必要时加“历史基准，非当前完成指标”及当前 Quality 链接，避免继承误导结论。

本计划虽完整，但只在本轮结束做一次覆盖核对。开发过程中按 **修改代码并删旧 → 相关验证 → 更新真正变化的事实位置 → 本地提交** 工作；不要求同步改八份文档。结束时只有一个 Quality 当前结论、一个实际发布包和必要原始证据，不能再靠新增三份“验收已完成”文档推动状态。

### 5.10 W5-D：构建、guard 与标签修复脚本

1. `Repair-WinPoolIntegrityLabel.ps1` 支持实际仓库根以及既有研究目录布局，不只硬编码 Program/WinPool；目标来自明确 repo/runtime 路径。icacls 非零、读取返回 error:* 必须失败并报告实际目标，不能被当作“不是 Low，所以成功”。
2. `Assert-RealOperationIdle.ps1`按准确运行树／数据根关联实例，避免其它checkout误阻塞。活跃写或W1旧宿主仍可能写时拒绝替换；所有相关进程已停的durable Unknown保留产品写屏障，但不阻止安装修复／构建。同步§3.3 schema21及旧版本列表，不因改守卫解除产品Unknown。
3. 既有 Low 标签问题已报告修复，不为每次编译递归重置研究根 ACL；不把 unsigned/未知发布者与强制 Low 完整性混成同一问题。
4. 在现有 build/merge 命令记录一次实际无变更构建与单项目变更成本，消除重复全树 copy/hash/merge 与无关 App 重建。利用 MSBuild 增量输入/输出和现有分层运行树；改后标准树依赖仍正确，不能以跳过资源/Agent 合并伪装加速。
5. 保留单一 Package-WinPool 入口；沿用其 PRI/XBF/Assets/完整树校验。增加最小 build identity：**构建时**源 SHA/受控输入状态、版本、运行文件 hash；不能只记打包时 HEAD。
6. 文档提交且运行输入未变不强制重新构建；实际代码/协议/资源变化须产生新的候选并验证。无需建立逐字节可复现构建工程或为此拆大批程序集。

完成证据：独立 clone 与研究布局能定位正确目标；命令失败不假绿；同一运行树不被活动写覆盖；无变更构建不重复执行昂贵全树处理，改 App/Agent/资源后最终合并仍完整。


## 6. 性能与减负的完成标准

以下是 **D4 提交开发者审阅的 V0.62 目标**，不是本次已经实现/测得的成绩。采用 W0 固定开发机、相同权限、相同完整文档/硬件覆盖、相同监控状态；不运行其它全量测试争抢资源。记录实际每次结果，小样本只给中位数/最大值，不制造高置信度统计。

### 6.1 必须同时通过结构和结果两类门

| 路径 | 必须消除或保持已退出的工作 | 建议结果门 |
| --- | --- | --- |
| 冷/热 Storage 读取 | 同一 Agent 连续调用逐次启动 PS；Storage/Hardware/monitor 同轮各采一遍 | 冷宿主完整 Storage provider 中位数 ≤3 s、最大 ≤4 s；热宿主完整 Storage provider 中位数 <1 s、最大 ≤1.5 s；热手动刷新到 UI 应用中位数 ≤2 s、最大 ≤3 s |
| 完整 Hardware 读取 | 重复 discovery/逐对象相同关系枚举、重复启动 | 热完整 Hardware 中位数 ≤2.5 s、最大 ≤4 s，字段覆盖不缩水 |
| 普通真实池/卷改名 | 同一阶段重复采集、正常成功后恢复/重采、固定后验等待 | 从 Prepare 开始到 UI 应用、扣除明确人工停留的总时长中位数 ≤5 s、最大 ≤7 s；原始总时长另存。Windows 写命令时间单列 |
| 真实结构与 MAX | App 接力、每候选重复前态、成功后的额外全采、循环整机后验 | 同一 V0.61 基线工作负载与最终结果下，应用侧非写开销至少降 70%；总体墙钟至少降 50%。候选数/原生写时间同时列出；不能改算法或少做步骤换分数 |
| 模拟合法动作 | 每个动作等待 Agent 才显示；一次 revision 多次完整 reducer | 约 530 KB 文档输入/接收/预演中反馈 ≤100 ms，成功本地候选发布 ≤300 ms。3 MiB 样本反馈 ≤150 ms、合法候选发布 ≤1 s；保存中仍能输入。仅 App 完整预演按 revision 计一次，Agent 保存按 batch 另计。连续动作形成有界合并保存 |
| 选择/切系统/同值状态 | 无关文档/比较列全重建，语言未变仍全本地化 | 已加载约 530 KB 文档选择/切换 ≤100 ms，3 MiB ≤200 ms；100 次同值通知额外 topology/reducer=0；未变系统 root/columns 重建=0 |
| 长读取期间短控制 | sendGate/同步 handler 挡住状态、停止、设置、生命周期 | 阻塞 provider 替身时，轻状态/停止后续步骤请求 ≤250 ms 返回；真实候选同类请求 ≤500 ms，有界 pending，无第二写 owner |
| 20 Hz 监控 | 每个采样 tick 复制所有 series 60 秒窗、同 revision 清空重画、偏好轮询 | 图最多 10 Hz；可见更新主线程处理 ≤16 ms/次；隐藏/未变图复制与图元重建=0。绘图相关分配在同负载下至少减半，已有近零路径保持零 |
| Agent 长生命周期 | facts/scopes/stops 随已完成 operationId 永久增长 | 普通终结后临时交接项=0，结构交接消费一次；cache/队列均受窗口和上限约束，长时不随已完成操作线性增长 |
| 构建与日常验证 | 无变更仍完整 merge/copy/hash；每小修改全仓/全机门 | 无变更构建不执行昂贵全树合并；相关修改只运行对应验证。最终集中一次完整门，其时间按新真实测试集登记 |
| 维护耦合 | 每个 UI/字段/文档合法修改触发源码字符串失败，多处重复事实 | 约39个浅源码/文档 case 退出；约60个已确认旧 case 退役；没有一对一补回镜像测试。修改一种事实只更新其 owner |

三次冷采样、十次热采样、十次模拟连续动作足够识别这里的数秒/重复工作问题；真实改名采用少量可还原往返，不为统计精度增加破坏性测试。预算观察接到生产调用入口；必要故障用现有替身。

MAX 总时长不是“每步 1.5 s”的乘法。若同机实测表明 Windows 原生必要耗时本身已超过建议总时长门，保留完整分段结果并请开发者调整**该门槛**；不得隐瞒结果、自行宣布达标或移动到后续版本。输入规模不同、provider 覆盖不同的样本分别列示。

### 6.2 不能用来替代结果的指标

“只有一处 new Process”“读次数为 3”“测试 99.9% 通过”“接口剩 15 个”“文件小于 600 行”“文档移走 46%”都不能独立证明目标实现。也不要求无目的减到某个总代码行数：必须删除明确重复、减少维护点，并在真实操作上变快。

不新增长期性能平台。沿用现有 timing/进程/内存与测试输出，在 Quality 记录一张基线→本版结果表；必要 raw 数据留同一 artifacts 目录。最终结果未达时继续修本版对应路径，重新验证该风险范围即可。

## 7. W6：本版完整验证与实际公开交付

### 7.1 已知未测范围在本版处理

以下仍是V0.62的完整代表性验收范围，不做环境全排列。**本执行方只验证现有开发机可完成且已授权的项目；开发机以外的系统／设备测试由用户另行处理，结果由用户提供。** 不把外部项变成本执行方完成本机交付的前置，也不假记通过；发布前按对应结果核对。共享逻辑／故障分支使用现有行为测试。

| 范围 | 本版必须执行的代表路径 | 资源/权限 |
| --- | --- | --- |
| 开发机真实已授权链 | 原审查六条：池名往返、卷名往返、VD OS disk 脱机/联机；再覆盖本次改过的建/删池、层、VD、初始化/自动布局、格式化、分区扩缩和 single MAX、结构 MAX | 原 WDC 持续授权；全部通过产品控制链，记录独立只读前/后态 |
| Windows 10 22H2 x64 | 干净无开发SDK启动／刷新／模拟保存／导入导出／监控／退出；普通权限与提权；记录SKU与上游支持关系 | 外部环境由用户负责；开发机结果不替代干净环境 |
| Windows 11 24H2、25H2 x64 | 各完成干净运行、配套App／Agent、普通读取、UAC接受／取消、重开／退出；一套适合实盘的Win11环境 | 开发机以外由用户负责；本执行方不安装其它OS或VM |
| SSD/SCM、多成员、冗余、混合层 | SSD与SCM分别验证，多成员池、支持SKU上的Simple／Mirror／Parity、一个多层MAX／容量拒绝／还原闭环 | 用户负责外部设备及授权；本执行方不使用受保护Samsung补足范围，VM结果不冒充真实SCM |
| 模拟和数据 | 原四条模拟创建/卷名/缩分区/删除；较大文档高频编辑/撤销/重做；D2 保存失败/冲突/交接；64 MiB 合同和大证据 | 现有 13 份开发模拟文档先保留；在副本完成修改/迁移 |
| 显示/交互 | 中英、明暗、100%/150%/200% DPI、高对比；管理/结构/分区/Hardware/Monitor/Settings 主要操作可达、无关键裁切 | 至少一台支持缩放的 Windows GUI；不为每像素添加测试 |
| 长时监控与归档 | 连续至少 8 小时 20 Hz、多个有效目标；可见/隐藏切换、App 重开、Agent 重连、采样 gap/CSV/轮转/关闭；三项现有 GiB/7z 手动测量实际执行 | 一次集中运行，可利用夜间；足够临时空间，记录实际样本/丢失/内存/归档结果 |
| 存储迁移与升级 | 17／18／19经20升21的退表／当前文档迁移；job单调版本沿用；标准↔便携根往返、归档／CSV、文件句柄、原文档／必要journal不丢 | 副本数据库和临时根；同步guard与根切换版本列表，不对原件做破坏性故障实验 |
| 异常/恢复 | 已发写丢回执/宿主退出、Unknown 停止搜索、UTC 回退、reseed/迟到事件、满帧/半传输、CAS 失回执、归档失败 | 优先现有可控 fixture；真盘不故意制造危险中断来替代故障测试 |
| 软件功能边界 | 读取已有多 VD 原貌；不支持的成员迁移/MBR/动态盘等准确拒绝；受保护对象按钮/拒绝原因与当前语义一致；Explorer/属性/反馈链接按现行入口 | 只验证已声明能力和拒绝行为，不新增占位页后台系统 |

WDC 授权依据：`DESKTOP-BLE6H18` 的 `WDC WD40EZAZ-00SF3B0`，`physical:uid:6ceea032f3fb375a61cd27bd`；历史 Disk 0/E: 不是授权定位依据。两块 Samsung 及关联对象禁止成为写目标；单 WDC 授权不扩展到新 SSD/SCM/其它磁盘。任务结束恢复约定 WDC 在线 GPT、16 MiB MSR、其余一个 NTFS/64 KiB、WinPool_Test、E:，并核对保护盘稳定字段。末态要求不是未读取现场时的现状声明。[S23]

本执行方收口时在Quality的同一张结果表分别写本机结果与“外部：用户负责，待提供结果”。交付候选hash、TESTING和未测范围后即可结束本机任务，不等待外部排期。用户后续结果须记录所测ZIP／源码身份；若外部反馈导致本版修复，生成新候选并按D7补相关验证，原结果不冒充新二进制的证明。

### 7.2 一个构建、完整自动门、同一个 ZIP

1. **冻结 V0.62 产品输入。**沿版本规则把 iteration 改为 2，显示 V0.62、数值 Version 0.6.2/FileVersion 0.6.2.0；不误写成另一套 0.62.0 版本。共享 AssemblyVersion 按既有规则处理。提交本版受控源码/测试/资源/构建修改，用户 Ref 等无关文件不因“clean”被清理。
2. **稳定批次构建一次。**正常仓库根执行 restore、Release build，记录 SDK 与依赖解析；当前已配置 analyzers 无未处置 warning/error。使用标准运行树；不临时改输出目录、目录标签或 props 测试开关。测试/GUI 期间只一名 Windows 执行者。
3. **完整自动门一次。**由当前 solution 的全部保留测试项目生成独立 TRX；明确 pass/fail/skip。启用本版要求的 read-only admission、GiB archive/rotation/7z 测量。默认开发集可以 skip，但最终相应 Windows 批次要有实际执行记录；非适用平台/已明确不支持能力的 skip 解释清楚，不标成通过。
4. **处理红灯。**过时测试删除错误合同，生产故障修行为；不能为清零跳过失败、恢复旧字段或删关键约束。变更二进制后重建并补受影响回归，按§5.8核对最终全部保留用例均有适用证据；未变依赖hash的结果可复用，不能证明适用时扩大验证。纯文档不导致重复构建。最终报告区分完整门原始结果与修复后有效汇总。
5. **源/产物绑定。**构建时生成最小身份记录，位于现有构建／证据输出；Package读取构建SHA、受控输入状态、运行文件hash，不把打包时HEAD冒充构建SHA。有效测试及必要PDB核对必须对应最终运行依赖；不能把相同产品版本号的不同DLL直接拼为同一build。记录复用和补跑依据，说明写入现有TESTING／证据，不为manifest放宽所有JSON入包规则。
6. **不再构建地打包。**现有 Package-WinPool 生成 `WinPool-V0.62-win-x64.zip`，包含配套 App/Agent、.NET/WindowsDesktop 与 Windows App SDK 自包含运行资产、PRI/XBF/Assets/7za 和必要许可说明。检验完整文件集/hash/ZIP 可解压，无 Data/db/log/原始硬件记录/PDB/源码/脚本混入。
7. **解压实际 ZIP 独占运行。**先确认没有另一候选路径的 App/Agent 抢占同一实例/数据根，再从独立解压目录完成第 7.3 节。记录实际进程路径及 ZIP hash；不能拿标准 Release 的 GUI 结果替代要发送的 ZIP。
8. **本机交付。**Quality只登记此批源码身份、版本、有效自动结果、性能、本机／已授权设备、包hash、本机未通过项及外部待用户结果。按附录核本执行方范围一次，不凭文档存在划勾。本机无未解决问题时可写“本机实施与验收完成”；外部未回报时不得写“V0.62整体验收完成”。
9. **外部验收与公开发布。**准备GitHub V0.62 prerelease说明、ZIP、SHA-256和反馈链接交用户。用户另行完成外部验收并告知结果，符合本版条件后由用户发布，或给明确指令再由Agent执行。当前计划不代替push／tag／发布授权；本执行方不为等待外部结果继续占用本机。实际公开后核对下载ZIP／hash，整体发布仍属于V0.62。

可沿用的最小命令骨架如下；执行者按最终保留项目枚举测试，不建立新的调度平台：

```powershell
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet format analyzers WinPool.slnx --no-restore --verify-no-changes --severity warn
# 对当前 solution 中各测试项目运行一次 Release --no-build --no-restore，并写独立 TRX。
# 在本版 Windows 验收批次启用现有手动测量环境开关，恢复测试环境后再跑 GUI。
./build/Package-WinPool.ps1 -TestedScope "填写同一 build 的实际验证范围"
```

现有开关包括 `WINPOOL_READ_ONLY_ADMISSION`（必要时准确指定 `WINPOOL_ADMISSION_PHYSICAL_ID`）、`WINPOOL_RUN_1GIB_ARCHIVE_MEASUREMENT`、`WINPOOL_RUN_7Z_PARAMETER_COMPARISON`。它们只在测试进程环境中设置，不能写进项目默认 props；结束恢复环境。

### 7.3 实际公开包的用户路径

按同一配套解压包依次完成：普通权限冷启动/目录恢复 → Storage 与 Hardware 刷新 → 模拟创建、名称、撤销/重做和自动保存 → 导入/导出 → 监控/曲线/CSV → 关闭 App 留 Agent → 重开接原 Agent → 设置保存 → UAC 接受与取消/提权交接 → 本版改过的受控真实操作 → 数据根迁移/升级 → 托盘正常退出。

对控制连接失败验证当前 5 秒连接失败语义只呈现一次，不能迟到 Ready；对 UI 已关闭、模式切换、旧 Agent 事件、未保存草稿均无迟到覆盖。High 写成功与 Medium 启动成功不替代实际 UAC handoff。无需机械截图每个按钮，保存足以证明关键结果的 UI/日志/独立后态。

原生只读核查最终目标、保护盘和实际数据；样本、CSV、原件/归档、关机 clean 状态对应同一运行批次。任何发现的产品问题在当前 V0.62 修好并补相关验证，不当作预留版本的“新问题”逃离本次已知闭环。

### 7.4 TESTING、许可与外部入口

只完善同一份 TESTING：

- 全目录解压与配套 App/Agent；实际 Windows/SKU/权限范围和 .NET 上游支持区别；版本/包 hash。
- PowerShell 5.1 为产品系统宿主；不要求用户另装 PowerShell 7 或开发 SDK。
- 普通权限、真实确认、Unknown 只读重查/剩余结构目标重新确认、模拟保存失败/退出/未保存草稿语义。
- 本版实际性能结果与容量合同、明确不提供的功能；不能把仍未修的本计划项包装成 beta 已接受限制。
- 升级前托盘退出、替换目录/数据根说明；签名状态按最终 EXE 实际读取。如未签名直说，不声称所有机器不会提示，不引导用户关闭系统防护。
- 按**实际随包 payload**收集需要的第三方 license/notice：.NET/WindowsDesktop、Windows App SDK、SQLite/SQLitePCLRaw、实际随包库、7za 等；不把 build-only/ExcludeAssets 依赖机械全部附上。保留项目自有权利声明，不要求改变开源许可。
- GitHub Issues 反馈直链和最小模板：版本/hash、Windows、操作步骤、预期/实际、错误码与用户选择提供的日志。公开包不附开发者硬件日志，不建设新遥测/反馈系统。
- 依赖最终解析有变更时重新核已知公告，实际非 NuGet 二进制也按版本核查；不把旧 NuGet 无命中当全包证明。只更新本版需要/解决已知问题的依赖，不顺便大规模升级。

### 7.5 只有以下条件全部成立，才能写“V0.62 闭环”

- [x] D1–D5随本计划获批；D6外部验收由用户负责、D7一次完整检查后按影响补验已确认，没有扩展新增磁盘授权。
- [ ] W1所有生产读写共用常驻宿主，旧逐次启动链退出；本机全局最多一个宿主，崩溃遗留／提权／根切换不例外；无输出、超时、存活不可核实及写后丢回执按§4.1处理，不强杀未知写或重放。
- [ ] W2 同批事实／能力／范围、窄后验、MAX、临时对象释放落地；现有耐久job单调版本保持并验证。
- [ ] W3 本地编辑/保存失败/flush、一份工作模型与局部 UI 更新完成。
- [ ] W4 控制 HOL、统一大小与轻终态、监控复制/绘图、SQLite 死副本/连接/heartbeat 全部完成。
- [ ] W5 具体旧链与低价值测试退役，主机/偏好隔离、真 skip、文档断链/状态、build/guard/标签脚本缺口关闭。
- [ ] 第 6 节性能与维护结果达标；未达门槛没有被改写成“已改善所以通过”。
- [ ] 第7.1节本机授权范围已验证；外部能力由用户提供对应包的实测结果。外部未回报不阻塞本机交付，但此整体验收项保持未完成。
- [ ] 最终产物的全部保留测试有适用通过证据，复用／补跑口径清楚；必需人工测量完成，实际ZIP独占GUI／实盘结果成立。
- [ ] 本附录所有原要求均有对应实现/已完成保留/开发者裁决及有效证据；没有未知归属、未安排或自行延期的行。
- [ ] TESTING/第三方说明/hash 与最终包一致；GitHub prerelease 公开下载已验证。

批准前状态为“待批准”；批准后区分代码实施、本机验收、用户负责的外部验收和公开发布。本机范围结束可写“本机实施与验收完成，外部待用户结果”并交付；其余仍属V0.62，不默认转交下一版本。上方清单用于V0.62整体完成，不作为要求本执行方等待／代办外部测试的指令。





## 8. 完整覆盖索引：用于本版结束时一次核对

来源：[F] 全量原审查、[R] 仓库原审查、[T] 接手原审查；[FA]、[RA]、[TA] 为各自新增验收。以固定源码和本计划第3节裁决为准，不能拿一份审查的旧范围代替另外两份要求。

表中“已实现”表示实施前基线已有该行为，需要在本版保持，不表示V0.62已经验收。未完成行全部落在W0–W6。差异列中的审查方法冲突现按已随本计划获批的D1合同执行；源码证实错误的数字／调用者判断直接纠正，不以错误前提新增无用代码。

编号说明：F.P/A 对应原P1–P6/A1–A5；F0.x/F2.x/F4.x/F5.x 对应原修改阶段的子项；F4.1-01…10、F4.2-01…06对应原第4节的两张表；R7.F01…16与R7.D01…12按原功能/删除表顺序；TA.01…35与原验收逐项一致。多ID同行表示共用同一修复，不是省略。

### 8.1 原全量审查 F 与对应要求

| 来源ID | 当前源码/证据结论 | 本版动作 | 差异与必要边界 |
| --- | --- | --- | --- |
| F.P1；F.阶段1；FA.B3；TA.5–6 | InventoryPipeline与write runner仍逐次起powershell.exe，warm优势只属于受控实验。 | W1本版实现唯一常驻Windows PowerShell5.1读写宿主，实测残余启动成本及整条用户路径。 | 不得把12.7×实验外推为保证；不在.NET10内直接托管5.1 CLR，不开放任意命令。 |
| F.P2；F.A1；F0.3–0.4；R6.1 | 普通单写Accept零采、preflight上下文复用及Verified后态复用已接通，代表操作仍约12.54秒。 | W2保留已消除的重复，合并生产事实/能力观察并将轻后验接入实际owner；W6量实际墙钟。 | Prepare用于确认且用户可停留，不能用旧Prepare事实代替临写核验。 |
| F.P3；F.A3；F0.1 | Query/List只读，显式Recheck与启动恢复分离；后态临时集合生命周期另有漏洞。 | W2保持无自动重放/无查询触发对账，关闭operationScopes/verifiedPostStates等临时持有。 | 拒绝N次失败自动释放Unknown写屏障；停止重试不等于证明无写入。 |
| F.P4；F0.2；F0.5 | App旧250ms×1200常态查询已撤，backend60秒/2秒后验窗口仍在。 | W2改首次准确轻后验及必要的有界观察；W4终态事件/一次重同步，撤固定冗余等待。 | 窗口存在不代表每次固定等60秒；provider延迟收敛时不可无条件宣称失败无效果。 |
| F.P5；R6.5 | 监控稳态全快照poll已撤，但控制请求仍在sendGate内等完整响应。 | W4本版解决长任务/HOL，明确请求相关性、有界分发和写串行所有权。 | 不能裸删锁导致同stream响应错配；不保留第二套永久连接实现。 |
| F.P6；F2.6；R6.6 | typed模拟intent/receipt与inventory单正文事件已接通，导入/正文/evidence仍预算不一。 | W3保留轻提交；W4统一真实wire预算、按需证据并将终态与证据解耦。 | 不把必要重连补读算双实现；越界风险已有代码依据但未实测终态卡死。 |
| F.A2；F.阶段3 | scope读取仍常解析整份事实，能力/范围另有采集；生产startup没有共用generation。 | W2分离准确目标轻读取与完整捕获，同代能力/角色/关系共用；W1宿主承载固定查询。 | 不得只按DiskNumber/名称窄查；2分钟新鲜度不能替代写前对象当前核对。 |
| F.A4；F4.2–4.3 | 输入、持久化、临写边界均仍有实际职责，内部仍有重复IsValid/hash/JSON计算。 | W2/W4每个边界一个负责者、内部传typed context；Returned仅约束本次决策所需字段。 | 不把未知值变健康默认，不删保护角色/BitLocker/唯一闭包。 |
| F.A5；F0.7；F5.4；FA.B1；RA.R6；TA.23–24 | Architecture44case全为源码/文档扫描，2个文档Fact仍在，当前私有字段字串有一红。 | W5退39个快照case，保留少数稳定依赖/资产约束；真实风险留已有行为测试。 | 不一对一造新强类型镜像测试；TA23不能以再堆one-planner字串证明生产接线。 |
| F4.1-01读本机清单；F2.2；F4.2-02/03 | Native旧provider已退出，固定内嵌模板与只读启动预览仍各有用途。 | W1唯一实际采集宿主/模板；W5清完旧壳，仅保留独立缓存预览。 | 读缓存与向Windows采集不是第二套相同实现。 |
| F4.1-02清单IPC；F2.3；F4.2-04 | 旧CaptureInventory/LoadManageInventory已被Manage和缓存读取接替。 | W4保留purpose/scope/缓存语义，W5旧codec/client/handler/只证存在的测试同退。 | 缓存读取不能机械改为触发采集。 |
| F4.1-03拓扑采集；F2.5 | 统一collector实例已接通，但公开入口各BeginGeneration，生产合并未落实。 | W2由startup/read owner持有同代批次与覆盖，临写新开观察边界。 | 不能仅凭helper同generation测试判生产去重完成。 |
| F4.1-04机器记录；F2.4部分；F4.2-05 | LocalMachineRecordService旧路径已退，Agent-backed记录仍支撑启动缓存。 | W5撤无消费者别名/包装；W2稳定SystemId/binding生命周期缓存。 | Ephemeral测试替身与生产多路不能混为一谈。 |
| F4.1-05PowerShell启动器 | 读取和写入仍为两套逐次启动传输。 | W1单宿主协议替换旧runner，写已发出后的取消/崩溃语义集中处理。 | 读可取消/重建不等于可杀已发出写入再重放。 |
| F4.1-06身份；F2.9 | 存活、进程化身、完整性是三个不同判断，底层系统读取有共享机会。 | W2/W4同PID同次共用必要查询，W5删无语义转发。 | 不按三个类名强删两类，不把握手当永久身份。 |
| F4.1-07真实状态 | Query单条与List恢复集合仍为不同查询，均不自动恢复。 | W4轻状态/订阅/初连重同步；W2显式只读重查。 | 不能只留列表导致准确operation状态或终态恢复丢失。 |
| F4.1-08快照；F2.8 | 正常monitor已有批次推送，snapshot用于初连/重连。 | W4保持一cache/批次源，修余迟到/重同步和HOL，不恢复稳态poll。 | 0Hz是稳态poll目标，不是禁止初次同步。 |
| F4.1-09清单正文 | inventory事件承载正文、响应回receipt已完成。 | W4保留单正文、同revision不重复应用，预算覆盖事件帧。 | 必要补读一次不算双传常态。 |
| F4.1-10监控样本；F2.7 | 旧空AgentMonitorSampleEvent已由实际batch链替代。 | W4保留唯一生产batch并删除空协议链残项。 | 原报告主张删事件、改靠快照与推送方向冲突，取已落实的批次事件。 |
| F4.2-01；F2.1 | WinPool.Inventory及Tests已从tree/solution退出。 | W5保留有价值比较逻辑在实际owner，不恢复空项目/占位测试。 | 不以仍须12测试项目当完整门要求。 |
| F4.2-06；F4.3；F4.1 | 旧17零实现/80接口统计含错误归类，当前Authority等有真实消费者。 | W5按实际组成删无消费接口、旧planner/executor/replay与测试，记录删除的路径。 | 不以15接口/55k行或catch数量硬指标验收。 |
| F0.6；R3.2；TA.28 | admission已explicit skip，但guard仍6处非Windows裸return。 | W5修11展开case的平台skip并显式主机触点；W6本版执行已知manual/host范围。 | 非Windows空执行不是pass；新要求不允许自行把已知测量延期。 |
| F0.8 | Backend fixture默认SyntheticPoolCapabilities已修，无host能力回落。 | W5保留行为回归与显式能力注入，旧30项不能为绿灯删除。 | 报告旧说未传参数不精确，当前已传且给默认synthetic。 |
| F0.9 | guard测真实pwsh脚本且用临时根，但脚本按全局进程名判断。 | W5准确实例/临时根隔离、明确无WinPool进程前置；保留实际脚本验证。 | 不重写一份C#模型替代脚本测试，不删进程保护。 |
| F0.10 | RoundTrip已有WaitForIdle及指定record LastError等待，非固定sleep。 | W5沿实际消费方诊断条件修剩余计账竞态，保留有界等待和归档原件。 | 不能无证据断言全部7za失败为Low，也不盲加超时。 |
| F0.11 | 构建成本是历史量，当前脚本可复用已验证树。 | W5修重复merge/copy/hash的增量构建成本；W6稳定后一build一包并记录实际耗时。 | 不因历史App编译51秒自动拆程序集。 |
| F4.4 | 大文件仍存在，机械partial拆分不减少工作。 | W3先删重复刷新/旧逻辑，W5按实际owner必要整理。 | 拒绝每文件≤600行目标，不新拆ABCD框架。 |
| F4.5 | 重复catch/日志仍需按拥有者辨析，IPC/写/SQLite边界不能一起撤。 | W2–W5异常只在负责边界处理/呈现一次，删重复记录后再抛出。 | 不能按480数量批删或静默吞异常。 |
| F5.1–5.2 | 当前五类核心文档已缩短，但后续多份完成声明及归档链接仍漂移。 | W5只维护唯一当前事实与短结果，历史留原件；精确控件参数由源码拥有。 | 不再维持DesignTables副本或逐日长日志。 |
| F5.3 | Archive原文仍在，V0.61 Plan移动后链接失效。 | 本次激活修相对路径，保留历史正文和原hash说明；W5不重复整理。 | 不整棵搬Archive到Old，不清原始证据。 |
| F5.5；FA.B4 | Backend已有FullCalls==3/Scopes==3断言，无预算的验收结论不实。 | W2/W6验真正生产入口调用与墙钟；W5不补相同fixture镜像预算。 | 进程≤1、读取≤2须按新宿主与操作单位定义，不能硬套整MAX作业。 |
| F5.6 | 历史18.472→15.437秒记录有其原基线，不是当前完成标准。 | W5当前Quality改写为当前瓶颈/已完成结果，保留历史数字及原结论出处。 | 不追溯重写历史当时测量，也不以旧改善承认用户结果已达。 |
| F§8-01写前身份；F§8-02闭包；F§8-03角色；F§8-04加密 | 这些保护有独立防错盘意义。 | W2复用一次新鲜观察并保留精确身份、完整成员、保护角色及BitLocker必要检查。 | 减冗余不能削弱目标授权或把未知当无保护。 |
| F§8-05写后；F§8-06Unknown只读；F§8-07CallIssued | 写返回不能替代后态，Unknown不能重放，调用边界需耐久。 | W2轻后验/只读重查/调用前journal；W6现有fixture及代表实盘验证。 | 超时/重试耗尽不能自动释放未知写屏障。 |
| F§8-08固定内嵌；F§8-09单实例/SID/化身 | 内嵌固定命令和单实例/当前用户管道已存在。 | W1固定脚本/封闭参数；W4维持SID/进程化身与单实例。 | 持久宿主不得变任意PowerShell或跨完整性降级通道。 |
| F§5.1–5.3；F§6；F§7阶段预期 | 报告运行数字与代码体积是各自历史基线，非本次静态实测。 | W0固定源码/执行case/产物基准；W5删同类负担；W6独占Windows取实际结果。 | 预测秒数/比例/行数不作达标证据，App.Tests不等GUI。 |

### 8.2 原仓库审查 R：功能与删并清单

| 来源ID | 当前源码/证据结论 | 本版动作 | 差异与必要边界 |
| --- | --- | --- | --- |
| R§3 | 旧34→32是补跑汇总；新报告2499/1/4，本次本地读取两份汇总相符。 | W0分别登记来源／环境／口径；W5修失真，W6先完整门再按影响补验。 | 本次未重跑；不合并三报告红灯，不把累计2500称当前一次全绿。 |
| R§4 | 真写曾7–8采、模拟小场景亚秒；不同测量范围不能互推。 | W1–W4完成热路径改变，W6同条件记录采集/写/后验/UI墙钟与字节。 | 不把工具wait/xUnit耗时当用户方法时间或普遍p95。 |
| R§5 | 已有本机GUI/监控/末态证据，UAC/多环境等仍未覆盖。 | W6本机范围由执行方验证；外部环境由用户按D6另行安排并回报。 | High写成功与Medium启动不能替代UAC交接；混配Agent不算通过。 |
| R6.2 | Agent结构job已生产接通，R1原始草稿范围修复已加入。 | W2保持单作业所有权/准确摘要/新对象Verified映射，撤旧App阶段接力。 | 不能因App草稿合法而让Agent照办旧事实命令。 |
| R6.3–6.4 | 单revision预演已接通，Workspace全catalog重建和重复setter仍在。 | W3后台预览/保存、局部根/selection更新，零无关重建。 | helper缓存绿不证明完整Workspace路径已去重。 |
| R6.7 | Agent批次和App cache已接通，MonitoringService仍存null-agent本地loop/CSV。 | W4单owner/序列/gap/绘图分频；W5删除旧本地实现。 | 旧分支存在不证明UI正在双采样。 |
| R6.8 | 模板已统一；启动generation未共享、binding仍重复；MAX已共享attempt，DB重复表示仍有消费者差异。 | W1宿主，W2生产同代/能力/MAX与轻后验，W4 DB只留当前必要表示/设置。 | 不更改GiB算法，不混微软原生MAX限制与本软件采集；不加全局大锁。 |
| R7.F01 启动/激活/工作区 | 单实例和LastActivePage已接通 | W4唯一lifecycle/缓存metadata/超时与退出 | 无 |
| R7.F02 管理/Hardware/手动刷新 | 唯一collector但同代未共享 | W2同轮采集/视图投影，W1宿主 | 无 |
| R7.F03 模拟目录/导入导出/删除重置 | metadata惰性正文已接通，预算不一 | W3仓库与后台保存；W4统一入口/wire预算 | 无 |
| R7.F04 模拟属性/结构/分区 | 共享reducer已接通，无防抖后台保存 | W3草稿本地响应/后台CAS保存，删旧执行栈 | App不直接写SQLite |
| R7.F05 真实模式/冻结确认 | 真实会话和摘要为安全边界 | W2保留一份当前会话授权/冻结规则 | 真实/模拟并非可合成无类型executor |
| R7.F06 真实单动作 | Agent单执行器，临时后态仍泄漏 | W2生命周期/轻后验；W4轻状态 | 无 |
| R7.F07 结构Apply/重建/自动布局 | Agent job已接通 | W2同作业精确依赖/结果映射 | 无 |
| R7.F08 明确容量/MAX | attempt已共用，parser仅测试 | W2策略与typed MAX；W5撤parser | 整数GiB算法不变 |
| R7.F09 准入/范围查询 | 不同查询各会采集 | W2共用目标观察/能力，保留查询语义 | 查询不赋予写授权 |
| R7.F10 Unknown/启动恢复 | 只读重查与事件恢复已接通 | W2有界恢复/清临时值；W4终态独立证据 | 不自动重放 |
| R7.F11 监控/采样/告警/CSV | batch生产在Agent，App旧路径仍在 | W4单cache/流/实际库CSV；W5撤旧loop | 无 |
| R7.F12 App设置 | app-settings归App | W3/W4只更新相应UI/偏好 | 不触发库存采集 |
| R7.F13 Agent设置 | Agent偏好经请求保存 | W4合并事件/watcher reload | 不建第二设置模型 |
| R7.F14 数据位置/提权/退出 | 受控handoff存在 | W4一个生命周期；W6实际UAC/迁移/退出 | 不把UAC安全桌面当已自动化 |
| R7.F15 Explorer/原生属性/链接 | shell与权限动作不同 | W4普通shell留App，准确权限handler留Agent | 不无类型合并 |
| R7.F16 开发消息/测试占位 | 内存消息/路线占位 | W5保留现有呈现，删存在性快照测试 | 不建设V2工作区 |
| R7.D01 OpenMainWindowRequest | 当前 src 已无 OpenMainWindowRequest 定义/引用 | W5保持已退役；不重复安排删除，不恢复无用协议/别名/只测存在的测试 | 无 |
| R7.D02 CaptureAgentInventoryRequest/compare | 当前 src 已无 CaptureAgentInventoryRequest/旧 compare 定义/引用 | W5保持已退役；不重复安排删除，不恢复无用协议/别名/只测存在的测试 | 无 |
| R7.D03 IStorageInventoryProvider/WindowsStorageInventoryProvider | 当前 src 已无 IStorageInventoryProvider/WindowsStorageInventoryProvider 定义/引用 | W5保持已退役；不重复安排删除，不恢复无用协议/别名/只测存在的测试 | 无 |
| R7.D04 CreateHddTierTemplate | 当前 src 已无 CreateHddTierTemplate 定义/引用 | W5保持已退役；不重复安排删除，不恢复无用协议/别名/只测存在的测试 | 无 |
| R7.D05 RenameHddTier | 当前 src 已无 RenameHddTier 定义/引用 | W5保持已退役；不重复安排删除，不恢复无用协议/别名/只测存在的测试 | 无 |
| R7.D06 CreateMultiTieredMaximumVirtualDisk | 当前 src 已无 CreateMultiTieredMaximumVirtualDisk 定义/引用 | W5保持已退役；不重复安排删除，不恢复无用协议/别名/只测存在的测试 | 无 |
| R7.D07 CreateSingleMemberPool/RebuildSingleMemberPool | 当前 src 已无 CreateSingleMemberPool/RebuildSingleMemberPool 定义/引用 | W5保持已退役；不重复安排删除，不恢复无用协议/别名/只测存在的测试 | 无 |
| R7.D08 TryParseGigabytes(MAX) | 当前Application parser仍仅测试 | W5删parser三Theory10case，保留Format/Resolve | 无 |
| R7.D09 AutoVD/AutoPart setters | 当前 src 已无 SetAutoCreateVirtualDiskAsync/SetAutoCreatePartitionAsync 定义/引用 | W5保持已退役；不重复安排删除，不恢复无用协议/别名/只测存在的测试 | 无 |
| R7.D10 EffectiveActiveDocument/Snapshot | 当前 src 已无 EffectiveActiveDocument/EffectiveActiveSnapshot 定义/引用 | W5保持已退役；不重复安排删除，不恢复无用协议/别名/只测存在的测试 | 无 |
| R7.D11 SimulatedSnapshot | 当前 src 已无 SimulatedSnapshot 定义/引用 | W5保持已退役；不重复安排删除，不恢复无用协议/别名/只测存在的测试 | 无 |
| R7.D12 MainWindow null-agent | MainWindow强制Agent但MonitoringService还保留本地分支 | W5连本地sampler/CSV/loop退，测试fake connection | 无 |
| R§8 | 现行文档已缩短，但归档40链接和过宽UI完成措辞未闭环。 | W5一事实一位置/修链接/退快照；日常修改→相关验证→唯一事实→提交。 | 不把增加审查报告或长台账当减负。 |
| R§9 | A/B各条大量已实施，但同代/UI/字节/删旧未完成。 | 本计划W1–W6本版完成所有现存残项，稳定后集中验证。 | 本次用户不接受自动延期，不能沿旧Plan延期来关闭。 |
| R§10 | 交接索引含本地ignored原证据，远程不可重放。 | W0说明可读源码/报告/实物边界；W5维护唯一完成记录。 | 不能仅凭链接声称本代理已执行。 |
| R§11 | Medium冷启动/endpoint可写已在后补记录不复现，Low来源未被唯一证明。 | W5定点修Repair脚本布局/错误成功；W6最终包普通冷启动与实际签名说明。 | 不重开整树ACL调查，不一律管理员，不把未知发布者等同Low。 |
| R§12/12.1/12.2 | discovery耦合/49秒extent已修，5秒Connect timeout与60秒采集是不同问题。 | W1保持发现独立；W2无前提不fallback；W4连接边界一次呈现；W6实际Medium验证。 | 旧55.204→3.389是探针对照，不等当前产品或通用加速倍数。 |

### 8.3 新验收 FA/RA

| 来源ID | 当前源码/证据结论 | 本版动作 | 差异与必要边界 |
| --- | --- | --- | --- |
| FA.B2 | 已有独立完整报告2499/1/4，旧2500组合不能覆盖新红灯。 | W5去失真后，W6共享生产链稳定批次一次完整门，失败补对应范围。 | 不因每改小项重复全套；用例总数随退役变，不硬维2504。 |
| FA.B5 | 包记录dirty含文档/Ref，运行文件未混入这些内容；构建与测试包却不是一build。 | W6冻结受控源码/构建清单，绑定一个build/ZIP/hash/实际启动路径并公开。 | dirty标记不自动证明业务漂移；不要求清用户Ref或逐字节可复现。 |
| FA§2.4/§4/§7.2 | 417字串不是417case，raw/warm不同，旧包完整自包含为验收报告结果，本次未读取ZIP。 | W5退真实维护负担，W1/W6量端到端，W6完善实际说明/notice/反馈。 | 不能把“无预算测试”“必达1.5秒”“仅一个红灯就全部不可用”字面继承。 |
| RA.R1 | verifiedPostStates普通操作无清理，operationScopes也有生命周期残项。 | W2直接返回阶段后态或仅结构交接一次消费，所有终结/异常清理临时事实。 | 引用泄漏已证，MB/次与OOM阈值未测。 |
| RA.R2 | collector入口各自BeginGeneration，生产startup没有共享批次。 | W2实际startup/read owner共代/coverage；W6从生产入口计数。 | helper绿不等生产已合并。 |
| RA.R3 | Workspace切换/提交全已加载文档重投影，setter和显式刷新重复。 | W3保留VM按document/revision局部更新/selection仅详情，移后台重计算。 | 已有单遍预演不证明UI重建已消失。 |
| RA.R4 | MonitoringService旧本地loop/CSV和test-only容量parser仍在。 | W5实删本地分支/parser并退其无用途case；旧执行族另按5.8清理，保留生产Resolve/Format和真实Authority。 | 不等同当前双采样，也不整删有真实消费者项目。 |
| RA.R5 | 64MiB导入、3MiB raw文档、4MiB frame不对称；终态还强取evidence。 | W4真实wire/入口预算统一，轻终态独立，大单条证据摘要或窄分块。 | 历史3.6MB未证明已越界；不建通用传文件框架。 |
| RA.R6 | 当前Architecture私有关闭字段断言导致1红，既有用例不直接覆盖完整handoff。 | W5退浅快照；W3补关闭迟到回调直接行为，W6实际UAC验证。 | 不恢复旧变量，不把断言失败说成UAC产品必坏。 |
| RA.R7 | 归档Plan相对链接失效，Quality当前完成措辞过强。 | 本次激活修链接及证据口径；W5后续只维护变化事实。 | 保留历史运行结果，不因修路径重跑代码。 |
| RA.R8 | 逐次PS/binding、串行控制、DB副本/PRAGMA、UI同步预演/高频绘图仍有成本。 | W1–W4本版关闭具体已知重复及计时缺口；W0按已批准方案建立本机基线。 | 不再援引旧批准Plan延期；存在成本不等已证主要瓶颈。 |
| RA§7.2–7.3；FA§7.2；T安全边界 | 最终支持/签名/许可/反馈说明未全部实物核对，已知矩阵仍需外部实测。 | 按D5明确Windows10支持含义和发布目标；W6完成本机、ManualPerformance及notice，用户按D6提供外部结果，公开发布另需明确指令。 | 不自动延期到后续版本，不强求MSIX/Store/全笛卡尔矩阵或公开硬件原始日志；外部待结果不阻塞本机交付。 |

### 8.4 接手审查 T/TA：35 项与补充要求

| 来源ID | 当前源码/证据结论 | 本版动作 | 差异与必要边界 |
| --- | --- | --- | --- |
| TA.01 模拟通用executor撤除 | Coordinator已typed reducer，无new DefaultPlanner/Gate | W5再清Execution未消费通用栈及8+3case | 真实Authority仍在Agent用 |
| TA.02 模拟无整文plan/events | intent/receipt已接通 | W3保留并做后台合并保存 | 后台保存不能丢CAS/幂等 |
| TA.03 共用一个草稿计划器 | 模拟/真实planner服务不同职责 | W3统一同一模拟规则，W2Agent真实计划权威 | 类名不止一个不证明同功能重复 |
| TA.04 App内存差分/防抖 | 当前明确Apply/Enter等合法动作SaveEditAsync，无后台合并；并非每次按键RPC | W3本版实现App即时草稿/防抖持久化/失败保留 | 保留Agent单写/CAS，不能App直写库 |
| TA.05 长期5.1runspace | 当前每次新进程 | W1常驻5.1宿主 | out-of-proc可满足，不强求.NET10内Runspace字面 |
| TA.06 禁止逐次启动 | 两runner仍逐次启动 | W1读写共协议/宿主，取消与崩溃集中 | 写已发出不杀后重放 |
| TA.07 legacy比较退出 | CaptureComparison旧路径已退 | W5保留退役，清残包装 | 无 |
| TA.08 单Read(scope) | 缓存/采集/范围查询语义不同 | W1唯一Windows宿主/模板，W2共享事实，W4轻契约 | 不机械合成无类型万能RPC |
| TA.09 GetSnapshot不采集 | 内存snapshot已不触发inventory | W4保持初连/重连用途 | 无 |
| TA.10 真实读写读 | 代表单写三采含Prepare+临写+后验 | W2去同一步重复，W1降低每次成本，W6端到端 | 确认前事实不能永远充临写 |
| TA.11 删60秒/2秒后验 | 常量/循环仍在 | W2轻后验及必要有界观察 | 常量存在不代表正常路径等满60秒 |
| TA.12 合Prepare/Accept/step采集 | Accept已零采，Prepare与临写有时间边界 | W2以实际阶段共享，不无差别去新鲜观察 | 用户停留确认后仍要临写 |
| TA.13 撤1200×250ms | 已watch取代 | W4保持事件/耐久终态 | 无 |
| TA.14 仅推送丢失查询 | 初次Watch先Query，reseed补一次 | W4只初连/重连/明确缺口同步，不常态poll | 初始Query不是错误重复 |
| TA.15 撤WindowsRealPlanner职责 | Agent仍需fresh计划/能力权威 | W2一个Agent真实计划owner与共享规则 | 不得App给旧命令Agent照办 |
| TA.16 能力一次读取 | RequireExactMemberCapability仍逐成员查询 | W2同subsystem/同代能力读取一次，按目标闭包消费 | 不能删能力证明本身 |
| TA.17 TopologyReader同次事实 | 生产已注入collector，默认构造仍可建provider | W2显式生产依赖，撤无意义默认回退 | 默认构造存在不证明正常生产双采 |
| TA.18 范围/结构支持同读 | 各范围入口仍独立捕获 | W2合并同轮事实/能力，独立刷新有明确边界 | 不同查询语义保留 |
| TA.19 monitor身份不再全采 | coverage相符已复用Verified后态 | W2生产启动也共代，缺coverage才补 | 不能跨写入代复用过期事实 |
| TA.20 Development对应分工 | 现文未包含本版宿主/后台保存 | W5只更新最终真实owner说明 | 不照抄T的App写权威 |
| TA.21 Quality链接实施 | 当前移除旧句但完成措辞过宽 | W5唯一V0.62实际结果/证据入口 | 不再增加竞争完成报告 |
| TA.22 历史30.94/247.69不改 | 原记录保留 | W5留历史，另写当前实测 | 无 |
| TA.23 架构测试锁唯一链 | 源码assert不能证明生产唯一 | W5退快照；W1–W4实际组成/行为闭环 | 不新造one-planner字串测试 |
| TA.24 旧控件Border锁撤 | 点名两断言已撤，其他同类仍多 | W5退39浅快照而非只修当前红灯 | 无 |
| TA.25 不写用户timing | Agent/Infra module initializer已Trace sink | W5保持隔离并修另一默认prefs污染 | 日志隔离不等全部用户数据隔离 |
| TA.26 不留输出开关 | props无WINPOOL_AUDIT | W5保持标准输出/不留临时覆盖 | 无 |
| TA.27 Low标签去掉 | 后补验收记录Medium | W5仅修Repair工具明确bug，W6实际最终包 | 不例行重扫ACL或猜标签设置者 |
| TA.28 仓库目录完整门 | TA未新跑，另RA/FA已跑2499/1/4 | W6本版最终一完整门，记录实际新总数 | 不能因TA未跑否认其他报告，也不能说当前全绿 |
| TA.29 warm<1秒/cold2.4 | TA把3不同启动/刷新时段记录并列 | W1/W6同purpose同权限同生命周期测冷/暖与墙钟 | 三记录不是一次手动三采；目标量级非保证 |
| TA.30 模拟进程内完成 | 草稿可本地，持久提交仍Agent | W3即时本地预览/后台保存，AgentCAS | 不以不经任何IPC作为正确性要求 |
| TA.31 写前新鲜核对 | 已有角色/身份/能力边界 | W2保留一次准确临写 | 无 |
| TA.32 写后只读一次 | 循环可因不收敛补读 | W2首次轻后验、再必要有界，不全拓扑轮转 | 不把provider返回或一次失败当确定无效 |
| TA.33 Unknown不重发 | 已接通显式只读重查/无自动续写 | W2/W4保持并清临时数据/轻终态 | 无 |
| TA.34 授权盘/保护三星 | AGENTS有持续WDC授权 | W6现有WDC代表闭环，额外盘由W0确认授权 | 不把单WDC授权扩至其它设备 |
| TA.35 当前用户pipe/无Low降级 | SID ACL和完整性核验存在 | W4保留，无业务降级写路 | 不把High写通过当Low客户端已验证 |
| T.预期-补项；T.未验证-补项；T.落地顺序-补项 | TA35已覆盖多数动作，但未单列嵌入脚本遍历瘦身、三条代表墙钟、导航亚秒、7za进程完整性/原四报告保留等边界。 | W1删重复遍历并测固定脚本；W3/W6测本地编辑和切页；W5保留用户文档/修工具；W6核真实7za/Medium路径及代表操作。 | 不为计时先建平台，不还原用户删除报告；T当时未执行项不能写成产品当前缺陷。 |

### 8.5 本次源码新增/扩展发现

| 编号 | 不依赖审查结论的发现 | 本版归属与证据 |
| --- | --- | --- |
| N1 | 同口径净减252行；旧减5275声明不能复现 | W0 §2.1，固定两版Git blob和计数方法；W5用真实删除与维护耦合验收 |
| N2 | 原稿误判：同job更新已用MAX(旧值+1,时间)保证耐久单调 | W2-C保留并验证，撤独立版本列及专用迁移任务；[S10]、[S11]、[S46] |
| N3 | 三组库存关系/比较表无生产SELECT，仍有写入/死API | W4-D精确撤写与迁移；[S21]、[S22] |
| N4 | 旧通用planner/executor/replay失去生产消费者但仍编译与测试 | W5-A/B，物理退役+保留真实Authority；[S39]、[S40] |
| N5 | 测试默认用户偏好写入、六处非Windows裸return仍存在 | W5-B，临时根和显式skip；[S31]、[S32] |
| N6 | 常驻改造不能复用现有ReadToEnd/每次Import/自行stdout入口 | W1固定宿主帧与typed脚本函数；[S1]、[S2]、[S6] |
| N7 | 模拟后台保存新增的在途删除/退出、持续输入、超帧原子intent边界 | W3/W4在同一改动内完成，不把由本版方案引出的已知问题留到0.63 |
| N8 | 连接有界复用仍需轻控制独立额度；64MiB是编码而非对象内存上限 | W4-A/B明确调度、暂存、并发解析预算；不以表面改锁/抬上限验收 |
| N9 | 每Agent单实例不能约束孤儿宿主；TryRead返回null不等于进程已退出，静默也不能证明写已失败 | W1 §4.1.1–4.1.3用固定全局Job／原子创建约束数量，以精确退出信号和三态核验恢复，写结果仍由既有只读对账证明；[S5]、[S47] |

本表不是未来待办。闭环时每行只需链接到本版对应提交/已有测试或最终实机证据；不再为每个ID写独立报告。若发现同一根因的新表现，修入对应工作包。只有V0.62全部闭环之后的新检查发现，才使用预留版本号。


## 9. 依据与执行定位

源码基准固定为开头SHA；实施必然产生新提交，最终记录实际构建SHA及相对基准的改动，不能要求交付HEAD仍等于旧SHA。历史比较H1–H4保留固定链接，审查与源码S入口使用仓库相对路径便于执行；历史判断可用`git show <基准>:<路径>`核对。行数来源、旧运行与本次静态复核分别说明，不冒充新实测；HEAD有外来变动先核差异，已解决项不重复做。

### 9.1 三份原审查及三份验收

| 代号 | 固定来源 |
| --- | --- |
| F | [F]：全量检视，2026-10-17 |
| R | [R]：仓库接手审查，2026-10-17 |
| T | [T]：接手修改方案，2026-10-17 |
| FA | [FA]：对全量检视的验收，2026-10-18 |
| RA | [RA]：V0.61仓库审查验收与公开准备度，2026-10-18 |
| TA | [TA]：接手审查35项验收，2026-10-18 |

### 9.2 按工作包定位源码

| 工作包 | 当前源码/配置定位 |
| --- | --- |
| 历史 | [H1] 原基线；[H2] 当前受控树；[H3] 两版差异；[H4] 生产最后修改之后的纯文档差异 |
| W1 | [S1] 读取运行器；[S2] 写运行器；[S6] 写脚本EOF入口；[S5] 实际生产组成／当前按SID及会话单实例；[S47] 进程化身核验及null的歧义 |
| W2 | [S3] collector；[S7] member能力；[S42] range/support；[S8] binding；[S9] 全文比较；[S45] backend；[S4] operation生命周期；[S10] job版本；[S11] App等待 |
| W3 | [S12] 分区页；[S13] 结构页；[S14] Workspace更新；[S15] 模拟提交；[S25] 逐step工作模型 |
| W4 IPC | [S16] control等待锁；[S17] server/heartbeat；[S18] 导入64MiB；[S19] raw3MiB；[S24] frame4MiB；[S20] 终态/证据 |
| W4 monitor/DB | [S26] monitor复制/旧分支；[S27] 采样率绘制timer；[S28] graph重建；[S21] 死副本；[S22] SQLite连接；[S29] 监控连接 |
| W5 测试/死链 | [S30] Architecture44case；[S31] 偏好/主机访问；[S32] guard平台return；[S39] 旧executor测试；[S40] 旧planner/gate测试；[S41] parser测试；[S43] 旧规划族；[S44] 旧执行族 |
| W5/W6 | [S33] 标签修复脚本；[S34] 打包与build身份；[S35] 版本规则；[S23] 授权/目录边界；[S36] 产品现状；[S37] 开发现状；[S38] 当前质量口径 |
| 平台依据 | [M1] 微软PowerShell5.1与7运行时差别；[M2] .NET10支持系统表（本次核对日期2026-10-18，页面标2026-09-28更新）；[M3] Windows App SDK与.NET自包含分别配置 |
| 宿主恢复依据 | [M4] Job生命周期与不自动杀进程；[M5] 创建时Job列表；[M6] 进程退出异步观察；[M7] 活动进程数量限制 |

本计划把产品工作完整写在一个文件中；这些链接用于执行者定位和最后核对，不要求人类开发者重新逐篇阅读才能继续。

[H1]: https://github.com/GitRuozhi/WinPool/commit/74a40af73e213eaf63d7267f6065422a05fec3bd
[H2]: https://github.com/GitRuozhi/WinPool/tree/2fb84ddc3e7c6929a6feb2cf7c73058fe90f9368
[H3]: https://github.com/GitRuozhi/WinPool/compare/74a40af73e213eaf63d7267f6065422a05fec3bd...2fb84ddc3e7c6929a6feb2cf7c73058fe90f9368
[H4]: https://github.com/GitRuozhi/WinPool/compare/d210a23b194b88108fb8e93757a36dd99df9e9c0...2fb84ddc3e7c6929a6feb2cf7c73058fe90f9368
[M1]: https://learn.microsoft.com/en-us/powershell/scripting/whats-new/differences-from-windows-powershell
[M2]: https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md
[M3]: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps
[M4]: https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects
[M5]: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute
[M6]: https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.waitforexitasync?view=net-10.0
[M7]: https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information
[F]: Review/WinPool-Full-Review-20261017.md
[R]: Review/WinPool-Repository-Audit-20261017.md
[T]: Review/WinPool-Takeover-Review-20261017.md
[FA]: Review/WinPool-Acceptance-20261018.md
[RA]: Review/WinPool-V061-Review-Acceptance-20261018.md
[TA]: Review/WinPool-Takeover-Review-Acceptance-20261018.md
[S1]: ../src/WinPool.Infrastructure.Windows/InventoryPipeline.cs
[S2]: ../src/WinPool.Infrastructure.Windows/WindowsRealStorageCommandAdapter.cs
[S3]: ../src/WinPool.Agent/AgentInventoryCollector.cs
[S4]: ../src/WinPool.Agent/AgentRealOperationService.cs
[S5]: ../src/WinPool.Agent/Program.cs
[S6]: ../src/WinPool.Infrastructure.Windows/WindowsRealStoragePowerShellScript.cs
[S7]: ../src/WinPool.Infrastructure.Windows/WindowsRealOperationPlanner.MemberSets.cs
[S8]: ../src/WinPool.Infrastructure.Windows/WindowsRealMachineIdentityProvider.cs
[S9]: ../src/WinPool.App/Services/RealScopedInventoryRefreshPolicy.cs
[S10]: ../src/WinPool.Agent/AgentRealOperationService.StructureJobs.cs
[S11]: ../src/WinPool.App/RealStructureEditorFlow.cs
[S12]: ../src/WinPool.App/DiskPartitionPage.xaml.cs
[S13]: ../src/WinPool.App/StorageStructurePage.xaml.cs
[S14]: ../src/WinPool.App/ViewModels/WorkspaceViewModel.cs
[S15]: ../src/WinPool.Infrastructure.Windows/SimulationEditCoordinator.cs
[S16]: ../src/WinPool.Agent.Client/NamedPipeAgentConnection.cs
[S17]: ../src/WinPool.Agent/AgentControlServer.cs
[S18]: ../src/WinPool.App/Services/DesktopExportService.cs
[S19]: ../src/WinPool.Infrastructure.Windows/StorageSystemRepository.cs
[S20]: ../src/WinPool.App/Services/RealOperationStatusCache.cs
[S21]: ../src/WinPool.Infrastructure.Sqlite/InventoryRepositories.cs
[S22]: ../src/WinPool.Infrastructure.Sqlite/WinPoolSqliteStore.cs
[S23]: ../AGENTS.md
[S24]: ../src/WinPool.Ipc/IpcProtocol.cs
[S25]: ../src/WinPool.Application/StorageSystems.cs
[S26]: ../src/WinPool.App/Services/MonitoringService.cs
[S27]: ../src/WinPool.App/MonitorPage.xaml.cs
[S28]: ../src/WinPool.App/Controls/DiskActivityGraphControl.xaml.cs
[S29]: ../src/WinPool.Infrastructure.Sqlite/MonitoringSqliteStore.cs
[S30]: ../tests/WinPool.Architecture.Tests/ArchitectureBoundaryTests.cs
[S31]: ../tests/WinPool.Infrastructure.Tests/InfrastructureTests.cs
[S32]: ../tests/WinPool.Persistence.Tests/BuildOperationGuardTests.cs
[S33]: ../build/Repair-WinPoolIntegrityLabel.ps1
[S34]: ../build/Package-WinPool.ps1
[S35]: ../Directory.Build.props
[S36]: Product.md
[S37]: Development.md
[S38]: Quality.md
[S39]: https://github.com/GitRuozhi/WinPool/blob/2fb84ddc3e7c6929a6feb2cf7c73058fe90f9368/tests/WinPool.Execution.Tests/SafeOperationExecutorTests.cs
[S40]: ../tests/WinPool.Execution.Tests/ExecutionSecurityTests.cs
[S41]: ../tests/WinPool.App.Tests/RealVirtualDiskCreationCapacityResolverTests.cs
[S42]: ../src/WinPool.Infrastructure.Windows/WindowsRealOperationPlanner.cs
[S43]: https://github.com/GitRuozhi/WinPool/blob/2fb84ddc3e7c6929a6feb2cf7c73058fe90f9368/src/WinPool.Execution/OperationPlanning.cs
[S44]: https://github.com/GitRuozhi/WinPool/blob/2fb84ddc3e7c6929a6feb2cf7c73058fe90f9368/src/WinPool.Execution/OperationExecution.cs
[S45]: ../src/WinPool.Infrastructure.Windows/WindowsRealStorageBackend.cs
[S46]: ../src/WinPool.Infrastructure.Sqlite/OperationPlanRepository.StructureJobs.cs
[S47]: ../src/WinPool.Agent/ProcessIncarnationVerifier.cs
