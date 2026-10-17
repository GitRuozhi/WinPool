# WinPool 验证与验收

2026-10-18：**V0.61 候选验收完成。** 当前有效自动结果为 **2489 passed／4 explicit skip／0 个未解决失败**，包含一次完整门及其后必要的受影响项目／定向修复回归，不是最后一次整套全绿。最终构建、本机单写、两种MAX、GUI／数据／监控、硬件末态及实际候选解压验收均已有结果，见下文及[Plan](Plan.md)。

## 最小充分验证

| 改动 | 验证范围 |
| --- | --- |
| 纯文档 | 当前内容／链接、归档完整性、Git 范围；不运行产品或测试 |
| 低影响文案、布局、设置呈现 | 通常一次编译；仅确需判断视觉时检查所改场景，不写镜像测试 |
| 规则、提交、异常流程 | 对应行为回归，覆盖会复发的成功／拒绝／冲突或未知分支 |
| 采集、真实执行、持久化 | 相关 fixture，加本轮改变动作的必要设备闭环；不全套重演历史 |
| V0.61 收口 | 稳定代码一次完整自动门、一个集中 GUI／设备批次、一次实际候选包启动 |

一个执行者跑验证，其他 Agent 审阅结果。已有通过检查未受后续改动影响就复用；失败只补相关路径。编译成功、模拟通过、单设备实测分别证明不同事情，不能互相替代。skip、未运行和缺环境均不算 passed。

不再以文档固定句子、文件数量、XAML 子串或无语义 wrapper 的存在作为产品验收。保留真实依赖方向、单写入方、准确目标、固定命令、事务、Unknown 不重放等行为检查。修 fixture 不能放松生产校验；删除旧实现时将有价值行为用例迁到实际所有者，其余同退役。

## 当前证据与缺口

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

### V0.61 自动检查证据（2026-10-18）

原始输出位于 [v061](../artifacts/test-results/v061/)。[完整门原始 TRX](../artifacts/test-results/v061/final/) 合计 **2452 passed／12 failed／4 skip**；其中 Infrastructure 因监控导出 fixture 挂起被 blame 中止，该轮不完整。原始失败与中止记录保留。随后 Infrastructure、Persistence、Agent.Client 完整项目回归通过，其余改动补充直接回归；未受影响的完整门结果继续有效。

| 项目 | 当前有效通过／skip | 有效证据及口径 |
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
| **合计** | **2489／4** | 按当前用例去重、以修复后的完整项目替代旧结果；各次定向运行的数量不另行累加 |

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
- 采集与真实：scope／代次／准确身份、缺失值、目标角色、真实写前能力、CallIssued 耐久、Unknown 不重放。正常单写三次预算的适用单位和例外严格按 Plan；MAX 按 attempt 记录，容量拒绝证明所需观察保留。
- UI：同 revision 复用预演，相同进度不重建拓扑；选择改变只更新选择呈现，基线改变仍触发真实冲突处理。
- IPC／监控：实际 wire 大小、批次序列去重、上游丢样可见、溢出／重连同步、终态耐久。不能靠降低20 Hz采样率掩盖传输成本。
- 性能：先证明重复调用／传输／计算消失，再比较同设备、同文档、同监控设置的墙钟。只计底层实际 invocation，嵌套计时不相加；用户准备、写调用、后态和界面完成分清。不预先承诺秒数或倍数。
- 归档路径确有改动时：持续样本按身份／值核对、恢复数据与根路径一致、失败保留有效原件、缓冲缺口如实报告；一段普通轮换不能冒充1 GiB长期压力验收。未改的专项不强制重做。

原生操作通过适用 WinUI 工具观察，浏览器 DOM 不替代原生桌面证据。真实修改沿用 [AGENTS](../AGENTS.md) 的 WDC 授权和末态，两块 Samsung 禁止写；自动测试不触碰真实存储结构。

最终只需要一份短结果：版本／基线、完成及退役的路径、实际验证与输出链接、未解决／未覆盖项、候选包与说明。放在本文件当前状态或 Plan 收口段，不再新建全面审查报告。
