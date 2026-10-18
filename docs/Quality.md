# WinPool 验证与验收

2026-10-19：**源码已推进V0.62／0.6.2.0，已批准计划进入W6本机验收。** 一次完整自动门已执行，原始结果为2517项：2464通过、53失败、0跳过。53项均在Infrastructure，修复后按影响范围补验；不能把补验次数累加成完整门用例数。实盘批次、8小时监控及实际候选验收仍未完成。原始构建、TRX与性能证据集中于[本次目录](../artifacts/test-results/v062/)，局部通过不等于V0.62完成。

实施前最新两份独立全量验收记录均为2499 passed／1 failed／4 skip。方法与边界见[Review索引](Review/README.md)，本地[汇总A](../artifacts/acceptance-20261018/automated-summary.json)／[汇总B](../artifacts/test-results/acceptance-20261018/acceptance-test-summary.json)可读。唯一失败是旧私有字段源码断言；其历史结论不代表本次新二进制已验证。

V0.61的R1–R3修复及当次候选开发机验收继续作为历史有效证据：[修复验收](#v061-fixes)为706项受影响回归，累计2500是沿用旧结果的汇总，不替代上述后来完整门；[阶段计划](Archive/20261018-v061-completed/Plan.md)已归档。[V0.61候选](#v061-candidate)未公开发布，不作为V0.62候选。

## 最小充分验证

| 改动 | 验证范围 |
| --- | --- |
| 纯文档 | 当前内容／链接、归档完整性、Git 范围；不运行产品或测试 |
| 低影响文案、布局、设置呈现 | 通常一次编译；仅确需判断视觉时检查所改场景，不写镜像测试 |
| 规则、提交、异常流程 | 对应行为回归，覆盖会复发的成功／拒绝／冲突或未知分支 |
| 采集、真实执行、持久化 | 相关 fixture，加本轮改变动作的必要设备闭环；不全套重演历史 |
| 版本收口 | 稳定代码一次完整自动门、一个集中 GUI／设备批次、一次实际候选包启动；范围以活动Plan为准 |

一个执行者跑验证，其他 Agent 审阅结果。已有通过检查未受后续改动影响就复用；失败只补相关路径。编译成功、模拟通过、单设备实测分别证明不同事情，不能互相替代。skip、未运行和缺环境均不算 passed。

2026-10-18用户确认V0.62采用上述“一次完整检查，失败按影响补验”规则，并明确开发机以外的测试由用户另行处理和提供结果。执行方完成已授权本机范围即可交付；外部结果待回报时，保持V0.62整体验收未完成。具体范围与证据复用口径见[Plan的D6／D7](Plan.md#31-三份审查不能逐字同时满足)。

不再以文档固定句子、文件数量、XAML 子串或无语义 wrapper 的存在作为产品验收。保留真实依赖方向、单写入方、准确目标、固定命令、事务、Unknown 不重放等行为检查。修 fixture 不能放松生产校验；删除旧实现时将有价值行为用例迁到实际所有者，其余同退役。

## 当前证据与缺口

### V0.62 本机验收进行中

[原始完整门清单](../artifacts/test-results/v062/final/complete-gate-runs.json)覆盖11个保留测试项目；Infrastructure按宿主与其它用例互斥拆成两次进程运行，以免共享全局宿主互相干扰。App、Agent、连接层、架构、Application、Domain、Execution、IPC、Monitoring及Persistence均通过；Infrastructure原始为966通过、53失败。修复包括窄事实profile对应的测试证据、PowerShell测试外层的进度流处理，以及生产目标比较中排除独立校验的HostCall传输标识；真实存储身份与参数仍完整比较。[相关补验](../artifacts/test-results/v062/final/Infrastructure-repairs.trx)为624通过、0失败、1门控跳过，含带真实宿主调用标识的正向执行、只读恢复及目标篡改拒绝。该跳过用例在原始完整门已实际通过。

[Release构建](../artifacts/test-results/v062/final/Release-build-02.log)及[修复构建](../artifacts/test-results/v062/final/repair-build.log)均为0警告、0错误。修复构建身份含未提交受控修改，不能称为clean `4229118`；最终候选将另绑定稳定源码与文件hash。

只读SQLite检查将数据库绝对路径编码成file URI，避免`#`或`%`被误作片段／转义而打开错误文件。[路径及迁移补验62项](../artifacts/test-results/v062/final/Persistence-uri-migration-repairs.trx)全部通过，包括真实路径、解码诱饵库、写入拒绝及源文件集合／hash不变。

Persistence原始[197项完整结果](../artifacts/test-results/v062/final/WinPool.Persistence.Tests.trx)通过。随包7za更新至26.04后，[归档相关69项补验](../artifacts/test-results/v062/final/Persistence-7zip2604-repairs.trx)全部通过、无跳过，包含重新开启执行的三项实际测量；下表均为26.04结果：

| 测量 | 输入／样本 | 归档大小 | 实测耗时 |
| --- | ---: | ---: | ---: |
| 1 GiB直接归档 | 1,102,270,464 B／2,810,000 | 56,735,681 B | 压缩149.637 s |
| 生产轮转生命周期 | 封存1,102,274,560 B／2,810,001 | 56,554,050 B | 生命周期171.813 s |
| 7z参数比较 | 129,351,680 B／330,000 | 64m／2线程7,285,107 B；128m／4线程7,291,055 B | 15.750／16.007 s |

原始完整门中的26.03数字保留在原TRX，不作为26.04证明。生产参数为`-mx=9 -m0=lzma2 -md=64m -ms=on -mmt=2`，本次参数比较峰值工作集718,917,632 B，对照1,304,518,656 B，少44.89%，保留当前参数。前两项校验解压哈希，第三项执行`7z t`；TRX未记录哈希字面值。这些测量不替代8小时20 Hz实际监控。

依赖核查中发现[7-Zip官方26.04说明](https://www.7-zip.org/history.txt)包含安全修复，随包x64独立7za已更新至26.04；[官方asset摘要与下载校验](../artifacts/test-results/v062/final/7zip-2604-input.json)一致，旧26.03源文件已按规则保留到项目根Old。[本次NuGet公告查询](../artifacts/test-results/v062/final/dependency-advisories.json)未列出已知易受攻击包，只代表该数据源此次查询范围。

IPC中间JSON层重新转义导致正文SHA失配的问题已修复，保留原始JSON字节而不重算或跳过hash。相关补验为[Agent 180项](../artifacts/test-results/v062/final/WinPool.Agent.Tests-wire-repairs.trx)、[Agent.Client 26项](../artifacts/test-results/v062/final/WinPool.Agent.Client.Tests-wire-repairs.trx)、[IPC 19项](../artifacts/test-results/v062/final/WinPool.Ipc.Tests-wire-repairs.trx)，全部通过。修复后的实际Medium界面已正常加载本机清单。

实际提权中，一次UAC返回Win32取消1223后，原App与Agent保持存活、closing=false，界面恢复，随后经托盘正常退出；用户未看到该次提示，不能称为人工点击“否”验证。另一次用户明确点击“是”，旧Medium App／Agent正常退出，新High App／Agent接续成功，见[进程与权限证据](../artifacts/test-results/v062/uac-accepted-processes.json)。

[构建保留fixture](../artifacts/test-results/v062/final/runtime-preservation-repairs.log)和[实际无变更构建](../artifacts/test-results/v062/final/no-change-build-02.json)通过：MergeSkipped=true，stamp／identity内容及时间戳不变，耗时13.496 s。[Release分析器](../artifacts/test-results/v062/final/analyzers-release.log)无警告／错误，0文件变更；命令临时设Configuration=Release，避免默认Debug缺少引用导致工作区不完整。补验产物与后续候选的依赖适用映射尚待收口，原完整门hash清单不能自动代表后续修复。

[接近64 MiB的独立跨进程验证](../artifacts/test-results/v062/ipc-near-limit-114-05/summary.json)已通过，输入完整envelope为66,623,922 B，包含16,421对象；保存、加载、typed改名、CAS／重复提交、导出、再导入后对象／字段／关系语义均核对。连续轻控制最大22.061 ms；发送取消反馈27.131 ms，接收取消返回OutcomeUnknown的反馈0.066 ms、暂存释放663.295 ms，两端最终均无受控暂存文件。客户端／服务端峰值private分别约4.20／3.01 GB，不能把2 MiB传输buffer预算当作对象图内存上限；强制GC后的值也不声称为自动回收。该fixture不启动完整产品Agent，不替代GUI取消、生命周期或实际开发数据验收。

首次实际HDD MAX在创建child或发出写入前，被full基线与窄profile计划的指纹不匹配拦截；[原始拒绝证据](../artifacts/test-results/v062/hdd-max-first-rejected.json)保留。已改为用覆盖两种profile的raw事实先验证旧指纹，再转换授权锚点；拒绝merged／覆盖不足基线，保留身份、成员、角色及参数检查。[App 462项](../artifacts/test-results/v062/final/App-structure-profile.trx)、[事实profile／成员51项](../artifacts/test-results/v062/final/Infrastructure-structure-profile-02.trx)、[Agent 180项](../artifacts/test-results/v062/final/Agent-structure-profile-02.trx)通过，实际WDC重试尚待执行。首轮补验的4个失败均为测试样本未模拟实际变更或旧profile合同，原日志保留。29.5分钟20 Hz监控的[部分记录](../artifacts/test-results/v062/monitor-before-restart.json)为三个有效目标各35,382样本、drop=0、正常结束；因修复需重启Agent，将重新计连续8小时，不能拼接为一次通过。

独立可见WinUI Graph同负载比较保留[第一次](../artifacts/test-results/v062/final/graph-benchmark-01.log)／[第二次](../artifacts/test-results/v062/final/graph-benchmark-02.log)两边同10 Hz的控制实验，单次分配减少42.33%。这不是整条绘图路径每秒减半的判定口径；源码中20 Hz采样时旧页最小50 ms、新页最小100 ms。按相同3条×1200点、同20 Hz输入和同60秒窗，[实际页面频率比较](../artifacts/test-results/v062/final/graph-benchmark-03.log)旧／新实际调用954／594次，托管分配14,971,080／5,387,601 B/s，减少64.01%；新Graph最大6.456 ms。延迟跳过的呈现槽据实记录，未将名义频率当实际次数。该结果仅归属同步SetSeries，不包括异步布局、原生／GPU内存及Page其它工作。

完整产品的[MonitorPage实测](../artifacts/test-results/v062/monitor-page-measurement.json)使用20 Hz、两个勾选的有效序列：60秒测量窗中可见260次Poll，均值2.306 ms、最大10.440 ms，0次超过16 ms；158次同revision时图窗复制／重建均为0。隐藏277次Poll，图窗复制、渲染、图元增删均为0；统计到2,136 B其它托管分配，未称整个隐藏回调零分配。[App正常关闭重开](../artifacts/test-results/v062/monitor-page-measure-finished.json)保持Agent 25380及同一session，[持久检查点](../artifacts/test-results/v062/monitor-after-app-reopen.json)三个目标继续采样、drop=0。WDC清空造成的3.836秒编辑gap已正常结束，未冒充无中断采样；连续8小时尚未完成。

后续真实HDD MAX已完成并验证Clear RAW，但创建池前被Windows物理provider返回的CanPool=false拦截；[部分完成记录](../artifacts/test-results/v062/hdd-max-repaired-partial.json)仅一个child、无Pending，未创建池／VD。完整刷新仍显示OS磁盘RAW零分区、物理provider几乎全部容量已分配且CannotPoolReason=7；正补受控provider-cache刷新，不放宽可入池或授权校验。WDC当前临时RAW，任务收口前仍须恢复约定末态。

旧MAX历史144.454／102.418 s实际为Prepared job创建至终态，包含确认停留且不包含此前Prepare；缺少原生写调用开始／返回，不能据此精确计算本版70%非写开销／50%端到端降幅。对应历史读取／阶段原始数据仍保留，新的同口径性能比较待完成。

<a id="v061-fixes"></a>

### R1–R3 修复验收（2026-10-18）

代码基线 `d210a23b194b88108fb8e93757a36dd99df9e9c0`，仍为V0.61／0.6.1.0。以下修复关闭W2／W4及W8；原探针和旧包保留，不能再将旧包当作当前交付。

- **R1已修复：** App从原draft计算typed意图摘要，绑定动作、创建参数和相关组件；Agent以fresh重新计算并比较，外部在未编辑池或目标池新增子对象时，在持久化准备和provider调用前拒绝。App核对返回摘要及草稿revision后才展示／接受确认。正常、重复jobId、剩余目标恢复及可用空间变化回归通过；普通VD布局参数纳入摘要，不代表扩大既有布局执行能力。
- **R2已修复：** Workspace持有恢复操作的事件订阅，按operationId／planHash／耐久revision接续；终态走既有事实更新和不确定标记路径后解除屏障。Running／Unknown到终态、旧／重复／不同操作事件、UTC回退、取消释放订阅均有直接回归，不引入轮询或自动重放。
- **R3已修复：** Agent在同一捕获边界为monitor snapshot／state分配单调revision，session和窗口一致读取；App拒绝旧状态和旧实例事件。1–20快照后迟到state再来21仍保留1–21；真实session切换只补一次窗口，序列缺口仍可见。IPC升15，App／Agent须配套；核心schema20及monitoring schema2不变。

| 本轮项目 | 有效通过／skip | 证据 |
| --- | --- | --- |
| Agent | 156／0 | [首轮TRX](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/WinPool.Agent.Tests.trx)155 passed／1 failed；修正只改变SizeRemaining的fixture后，[定向5项](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/WinPool.Agent.Tests-targeted-rerun.trx)替代该失败，不重复加总 |
| App | 461／0 | [完整受影响项目](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/WinPool.App.Tests.trx)；首轮fixture的ID构造编译错误记录保留 |
| Monitoring | 53／0 | [TRX](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/WinPool.Monitoring.Tests.trx) |
| IPC | 12／0 | [TRX](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/WinPool.Ipc.Tests.trx) |
| Agent.Client | 24／0 | [TRX](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/WinPool.Agent.Client.Tests.trx) |
| **本轮合计** | **706／0** | [汇总、命令及原始失败路径](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/summary.json)；按testId去重，Agent净增5、App净增6；累计2500 passed／4 explicit skip，非新一次全套结果 |

[完整Release构建](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/Release-build.log)0 warning／0 error，39.10 s，包含真实Workspace接线的App项目。R2终态竞态和R3迟到事件的确定性结论来自上述自动回归；GUI未人为制造磁盘故障或强制Unknown。

真实GUI已按当前采集值核实WDC唯一身份及序列号，拖入新池草稿，经Agent准备后显示[完整确认框](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/structure-prepare-final.png)及[文字](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/confirmation-text.txt)，随后取消并放弃草稿。耐久job为Cancelled、child为0，无provider写入；未重复实盘MAX／清理。当前候选验收见下方。

<a id="v061-recheck"></a>

### V0.61 局部复验发现（2026-10-18，修复前）

基线 `c65f09086e649d4a5e16dbb2f6b4f4325b4fae8e`。按用户要求只抽部分：Agent 13、Persistence 5、Infrastructure 18、App 2、Application 3、Agent.Client 5，合计 **46 passed／0 failed／0 skip**。覆盖结构作业耐久与单写、Unknown不重放、采集复用、模拟预览／提交一致性、回执丢失／原子性、监控批次与连接超时；逐项命令、TRX及日志见[运行清单](../artifacts/test-results/v061-recheck-20261018-113417/run.json)，总体结果见[summary.json](../artifacts/test-results/v061-recheck-20261018-113417/summary.json)。这些数量不累加到首次验收用例总数。

补充探针使用现有合成fixture及生产服务，三个场景均确认问题，不能计作产品行为通过。保留[探针源码](../artifacts/test-results/v061-recheck-20261018-113417/probe/Program.cs)、[项目](../artifacts/test-results/v061-recheck-20261018-113417/probe/WinPool.RecheckProbe.csproj)及[结果](../artifacts/test-results/v061-recheck-20261018-113417/probe-results.json)；运行命令在汇总中。本次不修改产品／正式测试源码，未启动WinUI／Agent进程、未执行实盘操作。

1. **R1／P1：结构准备会把用户草稿之外的新对象加入删除范围。** 用户基于旧快照仅删除池A；准备前，外部工具在既有、未编辑的池B新增一个受支持的未使用HDD层模板。请求没有原始草稿基线，Agent在[PrepareStructureJobAsync](../src/WinPool.Agent/AgentRealOperationService.StructureJobs.cs)读取fresh基线后以Working目标重新Bind／Build，把Working中不存在的新层解释为删除；App在[RealStructureEditorFlow](../src/WinPool.App/RealStructureEditorFlow.cs)仍从原draft生成确认内容。探针中App待删层为空，Agent待删层为`tier:b`、物理授权包含`physical:b`，Prepare成功且确认内容没有该层。执行器随后遍历`draft.RemovedTiers`生成删除阶段。本次只验证准备及授权范围，provider写入调用为0，**未实际删除任何对象**。后续必须绑定原始草稿意图，拒绝未获确认的新增删除目标，且界面确认与Agent冻结动作一致；不能仅增加一个基于fresh目标的内部安全检查。
2. **R2／P2：重开／重连恢复的真实操作不会持续接收终态。** [WorkspaceRealOperations](../src/WinPool.App/ViewModels/WorkspaceRealOperations.cs)只执行一次`RealOperationRecovery.RefreshAsync`；唯一消费`AgentRealOperationStatusEvent`的[RealOperationStatusCache](../src/WinPool.App/Services/RealOperationStatusCache.cs)由发起操作的编辑页等待流程持有，恢复路径没有订阅。合成服务读到Running后，Agent完成并推送Succeeded，提交屏障仍保持；事件订阅数为0，显式再Refresh才解除。结论来自生产恢复服务复现及Workspace调用链核对，未运行完整WinUI场景。后续应由工作区接续恢复操作的事件，终态刷新相关事实后解除屏障，保留Unknown保护，不恢复周期轮询。
3. **R3／P2：重连后迟到的监控状态会静默清空刚恢复的图表窗口。** [NamedPipeAgentConnection](../src/WinPool.Agent.Client/NamedPipeAgentConnection.cs)先订阅事件、发布当前snapshot，再读已排队事件；[MonitoringService](../src/WinPool.App/Services/MonitoringService.cs)没有区分旧state与新snapshot的先后。顺序“当前session的1–20样本快照→较早无session状态→当前session状态→第21样本”使窗口被清空；探针最终仅1个样本，补快照请求0、gap计数0。已证明的是实时缓存窗口缺失，未证明持久样本丢失。后续需让snapshot与state共享可比较的先后边界，拒绝较旧状态；真正换session时正确重置／补窗并报告缺口，不增加全快照轮询。

当时对旧候选的独立只读复核通过：[586个ZIP文件逐一内容校验、SHA-256、App／Agent的V0.61／0.6.1.0及资源／排除项](../artifacts/test-results/v061-recheck-20261018-113417/package-verification.json)均与首次包记录一致。该次没有重打包或重做启动验收，旧包包含上述问题，随后已被本页当前候选替代；W2／W4及W8的修复回归见上方。

### 既有审查与历史证据

| 证据 | 可以证明什么 |
| --- | --- |
| [2026-10-17 仓库审查 §§3、11–12](Review/WinPool-Repository-Audit-20261017.md) | 首轮 2418 passed／34 failed／3 skip；两个守卫在停止进程后通过，修正 admission 口径后的汇总 2420／32／3 不是重跑全套；当时列出的30个恢复 fixture 和2个文本断言待修；本轮结果见下文 |
| [同报告 §12](Review/WinPool-Repository-Audit-20261017.md) | 当时报告 Medium 默认 Storage 55.204 s／7条失败、无 discovery 参数探针 3.389 s／0失败；这是当时的候选诊断，不代表当前V0.61结果，也不外推通用倍数 |
| [全量审查](Review/WinPool-Full-Review-20261017.md)、[接手审查](Review/WinPool-Takeover-Review-20261017.md) | 不同运行条件下的发现；后者额外超时、归档和轮转问题须按实际故障确认，不能把所有失败加成同一产品 bug 数 |
| [2026-10-16 VD耗时](Reference/Pool-VD-Timing-20261016.md) | 删除和 MAX 创建／自动布局中的重复采集成本；不是 V0.61 的性能结果 |
| [历史完整 Quality](Archive/20261017-v061-document-baseline/Quality-before.md) | V0.58–V0.60 各日期、动作、GUI、设备、工程门的原始结论与未测边界 |
| [单HDD容量调查](Reference/Single-Hdd-Tier-Capacity-20261009.md)、[整数GiB算法验收](Archive/20261012-editor-ui-baseline/Plan.md#104-用户指定的内置整数-gib-max-算法已验证) | 已有本机 WDC 的明确容量及普通／单HDD MAX 证据，不证明新重构或多成员硬件已验证 |

<a id="editor-320"></a>
<a id="v060-compact-actions"></a>
2026-10-16 表单、紧凑动作和自动盘符历史结果见[原记录](Archive/20261017-v061-document-baseline/Quality-before.md#editor-320)。随后共享项名改为100 DIP，只做相关编译／GUI；当前源码尺寸归 `EditorFormMetrics`，不将前一轮80 DIP结果当成当前值。

### V0.61 首次实现自动检查证据（2026-10-18）

原始输出位于 [v061](../artifacts/test-results/v061/)。[完整门原始 TRX](../artifacts/test-results/v061/final/) 合计 **2452 passed／12 failed／4 skip**；其中 Infrastructure 因监控导出 fixture 挂起被 blame 中止，该轮不完整。原始失败与中止记录保留。随后 Infrastructure、Persistence、Agent.Client 完整项目回归通过，其余改动补充直接回归；未受影响的完整门结果继续有效。

| 项目 | 首次收口有效通过／skip | 有效证据及口径 |
| --- | --- | --- |
| Agent.Client | 24／0 | [完整项目回归](../artifacts/test-results/v061/final-fixes/v061-idle-control-pipe.trx)；合法空闲连接用例替换旧 `byteCount: 0` 超时预期，不重复计数 |
| Agent | 151／0 | [完整门147项](../artifacts/test-results/v061/final/Admin_DESKTOP-BLE6H18_2026-10-18_01_18_39_net10.0.trx)、[scope回归14项](../artifacts/test-results/v061/final-fixes/v061-agent-final-fixes.trx)及[结构／provider字符串回归51项](../artifacts/test-results/v061/v061-provider-string-structure-agent.trx)；分别净增1项及3项，原结构作业单例扩为4个场景，不重复计数 |
| App | 455／0 | [完整门446项](../artifacts/test-results/v061/final/Admin_DESKTOP-BLE6H18_2026-10-18_01_18_54_net10.0.trx)、[监控呈现2项](../artifacts/test-results/v061/final-fixes/v061-app-monitor-final.trx)、[新增数据根交接用例](../artifacts/test-results/v061/final-fixes/v061-data-handoff.trx)及[结构目标175项回归](../artifacts/test-results/v061/v061-structure-goal-free-space-fixed.trx)，后者含8个新增用例 |
| Application | 441／0 | [完整门440项](../artifacts/test-results/v061/final/Admin_DESKTOP-BLE6H18_2026-10-18_01_18_56_net10.0.trx)＋[普通 VD 草稿14项回归](../artifacts/test-results/v061/final-fixes/v061-untiered-draft.trx)，后者含1个新增命名／撤销／重做用例 |
| Architecture | 44／0 | [完整门](../artifacts/test-results/v061/final/Admin_DESKTOP-BLE6H18_2026-10-18_01_18_58_net10.0.trx) |
| Domain | 23／0 | [完整门](../artifacts/test-results/v061/final/Admin_DESKTOP-BLE6H18_2026-10-18_01_19_01_net10.0.trx) |
| Execution | 113／0 | [完整门](../artifacts/test-results/v061/final/Admin_DESKTOP-BLE6H18_2026-10-18_01_19_03_net10.0.trx) |
| Infrastructure | 983／1 | [修复后完整项目978项](../artifacts/test-results/v061/final-fixes/v061-infrastructure-final.trx)替代中止轮；[provider字符串安全回归656项](../artifacts/test-results/v061/v061-provider-string-fingerprint-safety.trx)为655 passed／1 skip，Returned null与字符串用例合计净增6项，host只读 skip不重复计数 |
| IPC | 12／0 | [协议修复后完整项目](../artifacts/test-results/v061/final-fixes/v061-ipc-final.trx) |
| Monitoring | 53／0 | [完整门](../artifacts/test-results/v061/final/Admin_DESKTOP-BLE6H18_2026-10-18_01_24_15_net10.0.trx) |
| Persistence | 190／3 | [修复后完整项目193项](../artifacts/test-results/v061/final-fixes/v061-persistence-final.trx)；覆盖 schema20 构建守卫与只读读取。两个“schema20应拒绝”的旧参数已换成不支持的21，新参数通过；旧参数失败不列为未解问题 |
| **合计** | **2489／4** | 首次收口时按用例去重、以修复后的完整项目替代旧结果；各次定向运行不另行累加；其后R1–R3新增11项见上方 |

结构目标新增8项先复现2失败（[修复前8项](../artifacts/test-results/v061/v061-structure-goal-free-space-before.trx)：6 passed／2 failed），修复后175项全部通过。修复仅将 partition／volume 的 `SizeRemaining` 视为运行观察值，保留独立改标签、盘符、几何或移除磁盘／分区／卷的拒绝边界；不把可用空间波动误判为编辑。

Returned null 跨 JSON 往返后的指纹差异已修复；`NotCollected`／`Unavailable`／`Failed` 与 Returned null 仍严格区分（[652 passed／1 skip](../artifacts/test-results/v061/v061-returned-null-fingerprint-safety.trx)）。结构作业[51项回归](../artifacts/test-results/v061/v061-structure-null-fingerprint-final.trx)全部通过：child／`CallIssued` 前失败记为 Failed，历史无 child 的假 Unknown 在重启后只读关闭并保留 remaining goal；真实拓扑差异仍拒绝且执行次数为0。

真实 frozen baseline 的9个 `ObjectId` 在持久化 JSON 中使用 `\u0022`、fresh provider 使用 `\"`，字符串值相同却曾产生不同指纹（[canonical差异](../artifacts/test-results/v061/authorization-canonical.diff.txt)）。现按字符串值规范化，保留身份／角色／拓扑及读取状态校验；[真实基线、fresh与planner只读重放](../artifacts/test-results/v061/authorization-actual-comparison.json)的 target／physical指纹一致、plan hash及授权校验通过。[Agent 51项](../artifacts/test-results/v061/v061-provider-string-structure-agent.trx)、[Infrastructure 655 passed／1 skip](../artifacts/test-results/v061/v061-provider-string-fingerprint-safety.trx)及[持久化定向回归6项](../artifacts/test-results/v061/v061-provider-string-persistence.trx)均通过；3个新增字符串用例属于同一Infrastructure集合，只计一次。

监控WAL文件在轮转中消失的局部竞态已修复，[两项既有WAL／轮转fixture](../artifacts/test-results/v061/final-fixes/v061-wal-footprint-final.trx)回归为2 passed／0 skip，不增加总用例数。Agent退出已避免必失败的workspace RPC并保留本地页；最终候选托盘退出的实际日志核对也已通过。

4 个 skip 均有明确门控原因，不计入 passed：`RealAdmissionReadOnlyTests.CaptureCurrentHostAdmissionFactsOnlyWhenExplicitlySelected` 未设置 `WINPOOL_READ_ONLY_ADMISSION=1`；`MonitoringArchiveMeasurementTests` 中两项1 GiB归档测量未设置 `WINPOOL_RUN_1GIB_ARCHIVE_MEASUREMENT=1`，一项压缩参数比较未设置 `WINPOOL_RUN_7Z_PARAMETER_COMPARISON=1`。这些缺口不由合成／单设备结果替代。

[首次最终构建](../artifacts/test-results/v061/final-build.log) 的局部变量冲突及连带 XAML 错误已修复；[修复后完整构建](../artifacts/test-results/v061/final-build-fixed.log)、[GUI修复后构建](../artifacts/test-results/v061/gui-fixes-build.log)、[数据交接修复后构建](../artifacts/test-results/v061/data-handoff-build.log)及[结构指纹修复后完整构建](../artifacts/test-results/v061/structure-fingerprint-build.log)均为0 warning／0 error。[provider字符串修复后完整构建](../artifacts/test-results/v061/provider-string-build.log)为0 warning／0 error，42.74 s；[最终Release构建](../artifacts/test-results/v061/final-release-build.log)为0 warning／0 error，30.37 s。`final-results.json` 是较早生成的中间汇总，仍含已被替换的旧参数失败，当前统计以上述 TRX 和用例替换为准。

### 本机单写与持续监控（2026-10-18）

WDC 单写操作 `40757c3c-289f-45d6-a15e-8f35bc16075c` 将池名 `Q2` 改为 `V061_Single`，验证成功后恢复为 `Q2`。[阶段计时](../artifacts/test-results/v061/real-single-timing.json)恰有3次底层 `inventory.provider.collect`，分别为3.414／3.277／3.409 s；prepare 3.536 s、preflight 3.324 s、execute 5.682 s，合计约12.54 s。后态发布／界面使用已验证事实，没有第4次采集；嵌套计时不累加为额外采集，不与条件不同的历史样本计算百分比。

20 Hz 监控由 Agent 5064 持续持有，会话 `1a0bd841a52f49d89c677ef29f374fd5` 跨上述编辑及 App 22752正常关闭、17524重开继续采样。[重连前](../artifacts/test-results/v061/monitor-before-app-reconnect.json)与[重连后](../artifacts/test-results/v061/monitor-after-app-reconnect.json)为同一会话，样本继续增长、drop=0、无 gap；随后通过界面停监控、托盘菜单正常退出 App／Agent，现场核对该会话终态为4、drop=0。本条不替代后续 MAX 或更长时压力验证。

HDD MAX 结构作业 `145626005d1b42dd811c7d06e8245186` 通过界面确认后完成10阶段：清至RAW、建池、建HDD模板、MAX VD、GPT、调整提供程序MSR、创建16 MiB MSR、数据分区、格式化、盘符（[作业结果](../artifacts/test-results/v061/hdd-max-structure-result.json)）。[容量journal](../artifacts/test-results/v061/hdd-max-capacity-attempts.json)记录3725 GiB容量拒绝，经两次未变化观察和job核验确认边界，随后3724 GiB创建成功（3,998,614,552,576 bytes）。[原生读回](../artifacts/test-results/v061/hdd-max-native-after.json)核对唯一WDC成员、HDD层1列／64 KiB interleave、GPT、16 MiB MSR、其余数据分区NTFS／64 KiB、卷标 `WinPool_Test`、`E:`。

随后受控解散 `V061_HDD` 的[清理作业](../artifacts/test-results/v061/hdd-max-cleanup-result.json)成功，结果为 `agent.structure.completed`、无pending child。本条证明本机单HDD MAX及其自动布局／清理，不将本次创建末态当作任务结束末态。

普通MAX沿现有界面路线完成：[先建HotSpare空池](../artifacts/test-results/v061/ordinary-empty-hotspare-result.json)、[同池恢复AutoSelect](../artifacts/test-results/v061/ordinary-role-result.json)，再提交作业 `16cc7452fbed4df9b4515a13520047e0`，VD及自动布局7阶段全部成功（[作业结果](../artifacts/test-results/v061/ordinary-max-structure-result.json)）。从活动数据库只读提取该作业child关联的[容量journal](../artifacts/test-results/v061/ordinary-max-capacity-attempts.json)：3724 GiB创建、3725 GiB增长均核验成功，3726 GiB经两次未变化观察与job核验确认为容量边界，最终3725 GiB（3,999,688,294,400 bytes）。[原生读回](../artifacts/test-results/v061/ordinary-max-native-after.json)确认普通VD无tiers、唯一WDC成员、GPT、16 MiB MSR及NTFS／64 KiB、`WinPool_Test`、`E:`。随后[清理作业](../artifacts/test-results/v061/ordinary-max-cleanup-result.json) `6663bc3b4f6346ae8f3180bf9dbd25d4` 成功，`agent.structure.completed`、无pending child。

最终硬件[139项核验](../artifacts/test-results/v061/final-hardware-verification.json)全部通过（`Passed=true`、`FailedCount=0`）：WDC已脱离具体池，在线、可写、GPT，16 MiB MSR及一个NTFS／64 KiB数据分区，`WinPool_Test`、`E:`，几何达到界面整数MiB MAX边界；两块Samsung的身份、分区、卷属性及关联与既有基线一致。核验脚本曾因SerialNumber空格产生误报，按原值正确比较后重核通过，未更改硬件身份。

硬件批次的20 Hz[监控记录](../artifacts/test-results/v061/monitor-final-hardware.json)为同一会话 `4975452655104f8eaa6da71baea24e01`、drop=0；16条明确的编辑gap均已有结束时间，其中10条为实际采样恢复（Restored）、6条为编辑删除（RemovedByEdit），没有开放gap。这些已记录的编辑暂停不当作丢样；[正常退出终态](../artifacts/test-results/v061/monitor-final-normal-exit.json)为state4、drop=0。结束前已通过界面恢复10 Hz、持续监控开启及本机Monitor目标。

### 开发数据与普通权限 GUI（2026-10-18）

普通权限 App／Agent 已核对为 Medium；标准运行树手动刷新 Storage 的底层 provider 为3.030 s（[计时](../artifacts/test-results/v061/medium-refresh.json)）。实际候选包的独立解压验收见下节。

较大既有模拟文档“超多磁盘服务器”（约530 KB）完成名称草稿、切页／切系统、撤销／重做后放弃；另通过界面导出、导入独立测试文档，修改名称并提交到 revision2，只有1条提交回执，再次导出。新建普通 VD 名称编辑导致容量归零的 GUI 问题已修复并重放通过。测试副本 `V061_SimMAX` 另经界面完成MAX提交至[revision2且仅1条回执](../artifacts/test-results/v061/simulation-max-result.json)，回执revision／after hash与文档一致。测试模拟对象已通过界面移除；[最终完整性](../artifacts/test-results/v061/final-simulation-integrity.json)核对13→13，`OriginalsUnchanged=true`。

标准→便携→标准两次数据根迁移均完成，返回迁移已验证旧进程退出、App／Agent自动重启并回到设置页（[返回清单](../artifacts/test-results/v061/data-return-manifest.txt)、[界面](../artifacts/test-results/v061/data-return-success.png)）。数据 schema19→20后，原545条真实计划／状态／正文保持一致，原91个commitId仍保留（[对比](../artifacts/test-results/v061/data-migration-integrity.json)）。曾阻止返回的旧内部位置指针已按文件规则归入根Rubbish；便携开发目录继承的拒绝删除子项ACL已保存原值并仅在该Data内修正，未变更产品权限逻辑。

旧SchemaVersion2模拟文件通过界面导入得到明确“不支持”错误（[详情](../artifacts/test-results/v061/unsupported-import-result.txt)），导入前后原文件SHA-256相同、文档数不增加。原始开发数据备份保留于[developer-data-before](../artifacts/test-results/v061/developer-data-before/)，未作为候选内容打包。

跨 Windows、SSD／SCM、多成员、Mirror／Parity、多层 MAX、长时运行和显示环境矩阵仍交首稿外部测试；现有开发机不足以证明全部覆盖。普通权限转提权的UAC安全桌面确认未自动化；High真实模式及获准WDC写入已另行通过，不能相互替代。没有“上个公开测试包”的升级前置。

<a id="v061-candidate"></a>

### V0.61 便携候选交付（2026-10-18）

**当前批次：R1–R3修复后。** 基线 `d210a23b194b88108fb8e93757a36dd99df9e9c0`，V0.61／0.6.1.0。[打包结果](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/package-result.json)：585个运行文件＋TESTING.md，ZIP 104,095,807 bytes；输入与标准Release树一致，App／Agent版本、资源、7-Zip许可及排除项由现有脚本检查。打包时仅尚未提交文档及既有Ref使源码状态为dirty，未混入包内。

- [当前候选ZIP](../artifacts/packages/V0.61/20261018T041447677Z-900efa87aaf041d2ab0d311dda6f0ba4/WinPool-V0.61-win-x64.zip)
- [SHA-256](../artifacts/packages/V0.61/20261018T041447677Z-900efa87aaf041d2ab0d311dda6f0ba4/WinPool-V0.61-win-x64.zip.sha256)：`453ad2cacbb59bcdc94bcb7a09c1936331aba8f1f06e495b5f74ea09ff8ea45c`
- [包内测试说明](../artifacts/packages/V0.61/20261018T041447677Z-900efa87aaf041d2ab0d311dda6f0ba4/WinPool-V0.61-win-x64/TESTING.md)

[解压核对](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/candidate-extraction.json)586文件逐一哈希一致；[App／Agent均为Medium](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/candidate-medium-start.json)。冷启动和Storage手动刷新成功，本次provider 2.769 s、merge 5.6 ms、persist 28.6 ms（[计时](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/candidate-refresh-timing.json)）；只报告当前值，不据此声明提速比例。关闭主窗口保留Agent，再打开可恢复既有监控曲线（[截图](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/candidate-monitor-after-reopen.png)），10 Hz／持续监控设置保持。真实托盘正常退出后App／Agent均消失，同一monitor session持久1767样本、终态4／drop0，五类错误日志均无新增；无未终结结构job，原13个模拟文档不变，末页Monitor，见[最终验收](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/candidate-verification-final.json)。

本次前后读取分别为High和Medium，21个共同可见存储对象的身份／几何／卷参数及共同关联不变；Medium未返回一个Samsung EFI卷，其分区GUID／几何／EFI类型仍一致。[最初跨权限全量比较](../artifacts/test-results/v061-r1-r3-fixes-20261018-115854/candidate-verification.json)的false记录保留，最终验收明确限定共同可见范围，不能将其当作EFI被删除或原139项硬件检查的重跑。此次真实准备在确认处取消，child为0，未执行存储写入。候选未上传、推送、发布或发送。

#### 首次候选（历史，包含R1–R3）

源代码基线 `12e0f979d65b273da95f108c8dc001c5fb4f1622`，版本V0.61／0.6.1.0。[实际打包结果](../artifacts/test-results/v061/package-result.json)为585个运行文件及一份中英TESTING.md，ZIP 104,084,499 bytes；App／Agent配套版本、PRI／XBF／Assets和7-Zip许可均核对，未含Data、数据库、日志、诊断、PDB或脚本。打包时有未提交文档及既有 `Ref/`，包内源码状态如实为dirty，运行树哈希与已验证标准输出一致。首轮打包曾误将 `Microsoft.UI.Xaml` 目录按源码后缀拒绝，修正为仅检查文件后重新生成，未放宽私有数据检查。

- [候选ZIP](../artifacts/packages/V0.61/20261017T190704575Z-4b46ef80d83f4449aad1410d2d216c85/WinPool-V0.61-win-x64.zip)
- [SHA-256文件](../artifacts/packages/V0.61/20261017T190704575Z-4b46ef80d83f4449aad1410d2d216c85/WinPool-V0.61-win-x64.zip.sha256)：`d8c0ef5ad5048b9dd788e75037994cc61d3631c02558d0eefc5cad0aca475402`
- [包内测试说明](../artifacts/packages/V0.61/20261017T190704575Z-4b46ef80d83f4449aad1410d2d216c85/WinPool-V0.61-win-x64/TESTING.md)

[解压核对](../artifacts/test-results/v061/candidate-extraction.json)确认586个文件逐一哈希一致。实际从解压目录以Medium启动App／Agent（[进程路径与完整性](../artifacts/test-results/v061/candidate-medium-start.json)），工作区及10 Hz持续监控恢复；手动Storage刷新成功，底层provider 2.526 s、merge 13.2 ms、persist 27.0 ms（[计时](../artifacts/test-results/v061/candidate-refresh-timing.json)）。通过真实托盘菜单正常退出后，两进程均消失，监控1389条持久样本、终态4／drop0，LastActivePage保持Monitor，崩溃／workspace／control／monitor／refresh错误日志均无新增，未留未终结结构job（[候选验收](../artifacts/test-results/v061/candidate-verification.json)）。此处为开发机短时验收，不代替外部矩阵和耐久测量；候选未上传、推送、发布或发送。

## 后续可用命令

以下为可复用命令，本轮已执行的验证以上方 TRX 和构建日志为准。先停止占用标准运行树的已核实 App／Agent，后按需要 restore；日常只运行受影响用例：

```powershell
dotnet restore WinPool.slnx
dotnet test tests/WinPool.Persistence.Tests/WinPool.Persistence.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SimulationDocumentRepositoryTests" -m:1
```

版本收口使用全套自动门并生成最终运行树；失败后只补受影响范围，不反复重跑已有效的检查：

```powershell
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
```

依赖有变化或已有结果不适用时再执行 `dotnet list WinPool.slnx package --vulnerable --include-transitive`，不作为每个工作包的前置。记录实际命令、基线和结果即可，不另建工程审计平台。

现有 `ManualPerformance` 三项门控测量保留显式 skip。当前不存在全仓 `Synthetic`／`HostReadOnly` 分类；不得写不存在的 filter 充当验收命令。`RealAdmissionReadOnlyTests` 已将未启用环境开关改为显式 skip；合成恢复 fixture 已注入能力结果，不能偷偷读取主机 WMI。测试诊断使用临时目录或可注入 sink，不写用户 Diagnostics。

## 行为与性能口径

- 模拟：规则允许／拒绝／信息不足；合法预览与提交一致；CAS 原子性；重复 commitId 与丢回复对账；失败保留草稿。只增加此次重复路径的少量计数断言。
- 采集与真实：scope／代次／准确身份、缺失值、目标角色、真实写前能力、CallIssued 耐久、Unknown 不重放。正常单写三次预算的适用单位和例外见[Development](Development.md#事实草稿与边界)；MAX 按 attempt 记录，容量拒绝证明所需观察保留。
- UI：同 revision 复用预演，相同进度不重建拓扑；选择改变只更新选择呈现，基线改变仍触发真实冲突处理。
- IPC／监控：实际 wire 大小、批次序列去重、上游丢样可见、溢出／重连同步、终态耐久。不能靠降低20 Hz采样率掩盖传输成本。
- 性能：先证明重复调用／传输／计算消失，再比较同设备、同文档、同监控设置的墙钟。只计底层实际 invocation，嵌套计时不相加；用户准备、写调用、后态和界面完成分清。不预先承诺秒数或倍数。
- 归档路径确有改动时：持续样本按身份／值核对、恢复数据与根路径一致、失败保留有效原件、缓冲缺口如实报告；一段普通轮换不能冒充1 GiB长期压力验收。未改的专项不强制重做。

原生操作通过适用 WinUI 工具观察，浏览器 DOM 不替代原生桌面证据。真实修改沿用 [AGENTS](../AGENTS.md) 的 WDC 授权和末态，两块 Samsung 禁止写；自动测试不触碰真实存储结构。

最终只需要一份短结果：版本／基线、完成及退役的路径、实际验证与输出链接、未解决／未覆盖项、候选包与说明。放在本文件当前状态或 Plan 收口段，不再新建全面审查报告。
