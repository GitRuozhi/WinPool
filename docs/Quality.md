# WinPool 验证与验收

日期：2026-10-09。当前产品版本仍为 V0.58。V0.58 本机真实磁盘修改第一阶段已完成并归档；V0.59 真实编辑流程对齐计划已激活，自动/真实界面/设备验收尚未全部结束。逐阶段操作账本和历史结果保留在忽略提交的测试证据目录；详细第一阶段 H11 结果见 [H11 检查点](../artifacts/test-results/20261007-real-edit-stage1-9b7a6ce20531437aafe897e143628d43/runRoot/H11-final-closeout-checkpoint-20261007.md)。收口前 Quality 原文保存在 [Acceptance-history.md](Archive/20261007-real-edit-stage1/Acceptance-history.md)，不作为当前状态。

## V0.58 单盘真实修改第一阶段（历史，已归档）

- **现场序列：** H00 基线及 H01–H11 适用操作已完成。包括分区/GPT/MSR、NTFS/exFAT/ReFS、EFI/Recovery、RAW 扩缩与删除、盘符/卷标、脱机/联机、建池、VD 与 HDD tier template、自动布局和分阶段重建。C03 因目标盘原已为 GPT 而 `not_required`；C05 现有 VD 与实际层实例扩展仍按边界条件禁用；D 类能力保持拒绝。
- **C02 与末态：** ReFS 1 GiB/65,536-byte cluster 卷扩至 2 GiB 后，同一 16 MiB 测试文件 SHA-256 保持一致；Shrink 属性及 UIA 均为禁用，候选卷随后删除。最终 WDC 为在线 GPT，16 MiB MSR 与分区页按对齐上限创建的 NTFS/65,536-byte cluster BasicData，卷标 `WinPool_Test`、E:。此处 MAX 指分区空隙几何候选，不是虚拟磁盘 `UseMaximumSize`。
- **保护盘：** H00 基线到最终布局、普通重启的 Samsung scoped comparisons 均为 0 changes、0 evidence gaps。范围仅限两块 Samsung 的关联结构；它排除有正面 `IsPrimordial` 证据的共享 aggregate 字段和非目标成员边，不代表全机结构无变化，也不能排除 Windows 后台 I/O。
- **工程门：** 稳定 V0.57 源码门共 12 个项目、1,450 项：1,447 Passed、0 Failed、3 NotExecuted（既有手工 archive/performance measurements）。Release 构建 0 warnings、0 errors；23 项依赖审计未报告漏洞。T01–T16 交叉表中的所选方法均在最终 TRX 中找到并 Passed。版本源随后提升为 V0.58；App/Agent 标准重建 0 warnings、0 errors，两个程序集 metadata 与 About UIA 均显示 V0.58。完整测试门没有在单独的版本号提升后重跑。
- **P5 与正常重启：** 原生 UAC 提权交接和 20 Hz monitoring 通过，交接期间 accepted 与 call_issued 未增长；普通启动 Real Off，App/Agent 正常退出。Application 日志查询得到 36 条 1000/1001/1026 候选，精确 WinPool 消息匹配为 0；这不确定历史 E_POINTER 的根因。

## 证据边界

- 最新严格目标审计保留 3 条旧记录缺少 `PhysicalMemberObjectId` 的字段缺口；每条仍保存准确 WDC UniqueId 和 serial，历史行未回填。
- 另一条历史 format-volume 失败步骤的 `target_json` 缺失，且该步 `call_issued=0`。有 preflight-failed 事件，但没有显式持久化 `NoWindowsCall=true`；因此不能把它改标为身份完整或已证明的 NoWindowsCall。最终审计原报告未被放宽或改写。
- `call_issued` 记录的是应用层发起边界，不单独证明 Windows 存储变更确已发生；历史目标字段完整性按严格审计结果保留。
- Persisted-inventory helper 不证明具体一次 ScanAsync 的因果；Samsung comparison 也无法排除后台 I/O。
- 最终门之后的版本号提升只由标准构建、metadata 和 About UIA 核对，不等同于完整测试门重跑。

第一阶段最终门和 H11 目标审计位于[历史测试证据](../artifacts/test-results/20261007-real-edit-stage1-9b7a6ce20531437aafe897e143628d43/)。完整 H11 结构、操作 ID、几何、文件哈希与退出证据见 [H11 最终检查点](../artifacts/test-results/20261007-real-edit-stage1-9b7a6ce20531437aafe897e143628d43/runRoot/H11-final-closeout-checkpoint-20261007.md)。以下 V0.59 工作流对齐是独立的活动阶段，不改写这份历史结果。

## V0.59 工作流对齐的当前验收状态

截至 2026-10-09，产品版本仍为 V0.58，`docs/Plan.md` 中 V0.59 阶段处于实施与验收中。2026-10-08 的 223310 完整门是 MAX、恢复保护及 scoped-capture 后续改动之前的历史结果，不代表当前源码门。B5 本轮 WDC 末态、独立 native/full 复核、Samsung scoped comparison、偏好恢复与原托盘退出均已通过，且证据仅适用于各自记录的精确目标和采集范围。Monitor Off 全新启动无历史告警也已在最终标准 runtime 复核。分层 MAX 仍未解决，因此 V0.59 阶段未完成。

普通真实 VD 的 Windows 原生 MAX 已有成功正例：`UseMaximumSize=true` 创建了 3,999,688,294,400-byte VD/OS disk，随后核实 16 MiB canonical MSR、NTFS/64 KiB、W: 和 App/Agent exit 0。单 HDD 分层 MAX 仍无通过的实际层正例。旧 `c2a5`/`d92` 路线使用明确的 `StorageTiers + StorageTierSizes` 容量值，其失败不证明 Windows 原生 MAX 不支持；本轮 exact-template `StorageTiers + UseMaximumSize` 路线（不是 `StorageTierSizes`）收到 48010，改用 `MediaType HDD + UseMaximumSize` 的路线则创建出未关联实际 HDD tier 的普通 VD，随后由生产 Query/CAS 严格定为 Failed/UnexpectedEffect，未重放。当前临时 fail-closed guard 在解散旧结构或建池前阻止分层 MAX；明确容量和普通 MAX 路线保持可用。该 guard 是当前安全措施，不是永久产品边界或“Windows 不支持”的结论；最终策略仍未决定。详见下方 2026-10-09 证据段。

版本仍为 V0.58 iteration 8，活动 Plan 不归档。文案断言同步后的最新完整自动门已通过，详见下方 09:55:40 记录；自动门通过不等于分层 MAX 或整个 V0.59 阶段已验收。V0.59 阶段仍未完成。

### 自动门结果与证据范围

- 早期第一轮聚合为 **1540 项：1530 Passed、7 Failed、3 Skipped**，使用本轮新增源码/测试之前的已编译结果；第二轮为 **1583 项：1577 Passed、3 Failed、3 Skipped**，三条失败分别涉及自动布局输出证据、分区磁盘预检和 pristine-root guard。两轮 TRX 保留原状态，均不能代表后续新测试结果。
- 中间定向 `device-gate-tests` 五个 TRX 合计 **433 Total / 433 Passed / 0 Failed / 0 NotExecuted**：Agent.RealService 15、App 124、Application.Session 15、Infrastructure.RealSafetyBackend 271、Persistence.BuildOperationGuard 8。该定向门不是完整 solution 最终门；TRX 文件夹名含 `device` 不代表这些自动测试写过真实设备。
- 较早完整工程门 [20261008-195646 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-195646/summary.json) 为 **12 项目，1622 Total / 1619 Passed / 0 Failed / 3 NotExecuted**；后续门 [20261008-202506 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-202506/summary.json) 为 **1655 / 1652 / 0 / 3**；再后门 [20261008-203645 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-203645/summary.json) 为 **1677 / 1674 / 0 / 3**，后续门 [20261008-210146 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-210146/summary.json) 为 **1714 / 1711 / 0 / 3**；[20261008-214608 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-214608/summary.json) 为 **1734 / 1731 / 0 / 3**。此前 [20261008-221503 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-221503/summary.json) 为 **12 项目，1766 Total / 1763 Passed / 0 Failed / 3 NotExecuted**，392 个源码哈希项未变化。最后一轮旧源码完整门 [20261008-223310 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-223310/summary.json) 为 **12 项目，1775 Total / 1772 Passed / 0 Failed / 3 NotExecuted**，12 项目退出码均为 0，393 个源码哈希项前后相同，Restore/Release build 为 0 warnings/0 errors、标准 App/Agent 合并无冲突。本门早于 native MAX、后续恢复保护与 scoped-capture 变更，故仅保留为历史结果；三个 NotExecuted 是既有 archive/performance measurement，不计为通过。版本 V0.58 iteration 8；依赖审计复用已有 23 项目结果，未重跑。
- 新变更完整门的第一次运行 [095259 summary](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-frozen-gate-20261009-095259/20261009-095259/summary.json) 保留 **1866 Total / 1856 Passed / 7 Failed / 3 NotExecuted**：七项 App 测试仍断言文案调整前的字面帮助字符串。同步断言后的最终门 [095540 summary](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-frozen-gate-20261009-095540/20261009-095540/summary.json) 覆盖 12 项目，**1866 Total / 1863 Passed / 0 Failed / 3 NotExecuted**，`sourcesUnchanged: true`（395 个源码哈希项），Restore/Release build 退出码均为 0，build 0 warnings/0 errors，标准 App/Agent 合并无冲突。三项 NotExecuted 仍是既有 archive/performance measurement。23 项目依赖审计复用本轮 [`Engineering/dependency-audit.json`](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/dependency-audit.json)，没有重跑；不应按 summary 中的相对字段另找审计结果。
- 与完整门分开的定向回归：[backend-complete TRX](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/hddmax-provider-uncertain/backend-complete/results.trx) 为 259/259 Passed，[capacity-intersection TRX](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/hddmax-capacity-intersection/capacity-intersection.trx) 为 93/93 Passed，[no-effect focused TRX](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/hddmax-provider-uncertain/no-effect-tests-final/results.trx) 为 42/42 Passed；这些是定向门，不叠加到完整门总数中。容量交集回归用准确 pool 与 HDD template provider 范围计算可用共同网格；这一代码/夹具结果不能代替真实设备以 Windows `UseMaximumSize=true` 参数创建成功。
- 该定向门实际包含并通过 7 个 P6/P8 服务方法：每次做新鲜 scoped 安全采集、失败/不完整范围不回退或复用旧事实、重启对账使用持久物理选择器、格式错误的 durable selector 保持 Unknown 屏障、无 scoped 能力时仍做 fresh full 检查，以及 observer 故障不把已核验写入变成 Unknown。前六项使用合成事实源，不能证明生产 CIM 遍历耗时或真实写入；最后一项也不等于监控联动已端到端验收。
- 较早定向门所记录的 Release 构建为 0 warnings、0 errors；复用的 23 项目依赖审计未报告漏洞。这些结果单独记录，不等同于重新执行依赖审计。

完整 T01–T18 方法映射及逐项原生 H/B 限制见[自动覆盖交叉表](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/acceptance-map.md)。下表归纳覆盖种类；“自动覆盖”是方法/服务夹具的断言范围，不代表页面控件或真实设备已经通过。

| T | 自动覆盖类别 | 自动证据的边界 |
| --- | --- | --- |
| T01 | `SimulationEditingSessionTests`、`RealStructureDraftPlannerTests` | 草稿拖放语义、撤销/放弃、冲突及零计划调用；没有实际页面拖拽端到端测试 |
| T02 | `RealStructureDraftPlannerTests`、`RealOperationUiFlowTests` | 表单意图、名字、布局、精确容量/MAX 与偏好进入 helper/计划；普通 MAX 保留 typed Windows 意图，单 HDD 分层 MAX 在计划前阻止；页面绑定仍需真实界面设备验收 |
| T03 | `SimulationEditingSessionTests`、`RealOperationUiFlowTests` | Enter 改名会话语义和 proposal；没有真实页面 Enter/失焦事件测试 |
| T04 | `RealVirtualDiskCreationRangeTests`、`WindowsRealPlanSafetyTests`、Agent planner、`RealVirtualDiskCreationCapacityResolverTests` | 显式容量读取新鲜 provider 范围并校验；普通 MAX 以 `UseMaximumSize` typed 意图跳过容量范围查询、不推算 bytes；分层 MAX 在任何删除/创建计划前 fail closed；已有虚拟磁盘/层扩展负例拒绝。新增 App 覆盖不在 223310 历史门中；已由 095540 完整门验证通过 |
| T05 | `RealOperationUiFlowTests`、`RealAutomaticLayoutProgressTests`、`WindowsRealPlanSafetyTests` | 普通/单 HDD 布局与分段输出通过 helper、planner 和服务断言；不等于原表单实机完成 |
| T06 | `RealStructureDraftPlannerTests`、`WindowsRealPlanSafetyTests` | 显式重建、删除前的已知创建能力预检，以及已知不支持时不调用 adapter 的负例；最终门通过了分区目标在计划清空后创建结构的回归。设备侧仍须验证原界面完整顺序 |
| T07 | `AgentRealOperationServiceTests`、`RealAutomaticLayoutProgressTests`、`RealOperationUiFlowTests` | 取消、部分完成、Unknown 停止及不重放；原生逐段取消路线仍需设备证据 |
| T08 | `RealOperationUiFlowTests`、`AgentRealOperationServiceTests` | 准备互斥、重连查询与 durable identity；不代替完整原生重连验收 |
| T09 | `SimulationEditingSessionTests`、`SimulationClearDiskTests`、Execution/Windows 安全测试 | 模拟 ClearDisk 独立关系变化及真实安全拒绝；夹具不修改真实磁盘 |
| T10 | `DiskPartitionPage` 静态路径审阅及辅助状态规则 | 没有自动测试实例化/驱动联机状态或分区表按钮；字段和禁用原因由 H07 原生验证 |
| T11 | `RealOperationUiFlowTests`、`WindowsRealPlanSafetyTests`、`SimulationClearDiskTests` | 精确 clear 目标、空 GPT 完整事实正例和未知/危险角色负例；模拟规则与真实设备结果分开 |
| T12 | `RealOperationUiFlowTests`、`WindowsRealPlanSafetyTests` | 公式/helper 双向联动、整 MiB 约束、provider stale-range 拒绝；没有实际 textbox 输入事件测试 |
| T13 | `RealAutomaticLayoutProgressTests`、`WindowsRealStorageBackendTests`、MSR safety inspector tests | 精确 MSR/provider 后态、偏好和拒绝续段；实盘 MSR 开/关结果由 B1/B2 核实 |
| T14 | `ScopedInventoryProviderTests`、`ScopedFactRefreshTests`、`AgentInventoryCoordinatorTests`、`EditWorkspaceTests` | 实际范围、闭包、合并、失败保留、代次与并发语义；另有 12 个显示回归 cases 验证 scoped dissolve 后准确 OS disk 投影和拒绝不可靠 fallback；合成 provider 测试不代表全机或所有设备性能 |
| T15 | `GlobalNotificationServiceTests`、`RealOperationUiFlowTests` | 进度生命周期和工作流屏障；没有图形遮罩控件、导航或窗口生命周期自动化 |
| T16 | `MonitoringEditTests`、`MonitorTargetIdentityTests`、`MonitoringSqliteStoreTests`、Agent observer tests | 目标身份、编辑缺口、恢复与持久状态夹具；监控运行中的真实编辑路线仍需 H10/B3–B4 |
| T17 | `RealStructureDraftPlannerTests`、`RealOperationUiFlowTests`、`RealOperationContractTests` | 65536-byte 参数意图、contract 和准确目标形状；不是页面输入到实际层的实机证明 |
| T18 | `IpcProtocolTests`、Agent 恢复测试、scoped merge 与监控持久化/轮换测试 | 帧、恢复 identity、代次和持久缺口语义；App/Agent 真实重连和采样恢复仍需 H12/H10 设备证据 |

### P6 范围采集与 P8 监控缺口

- P6 的真实安全读路径每次按准确 selector 获取新的范围事实；scope 只携带 selector/scope，不缓存安全事实。范围失败或不完整时拒绝，不静默回退 full、不复用上次结果；merge 只在完整覆盖域中证明 absence，范围外对象、关系及失败范围的旧值/观察时间须保留。新鲜事实还须通过代次检查，不能用较旧 full 覆盖较新的 scoped 结果。相应 7 个直接服务方法在最新定向 TRX 中的精确名称见[P6/P8 交接](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/documentation-handoff-p6p8.md)。
- 生产 `ReadOnlyProbe` 已修正。最新 `scoped-subsystem-fixed/target-proof.json` 将 scoped 与 full 捕获中的同一目标逐字段核对：物理盘 UniqueId/ObjectId、所属 StorageSubsystem UniqueId/ObjectId 及 fingerprint 均匹配，fingerprint 为 `bac984e6d81942c8de504684853e1af97d80491f7500afdfae3142df5e1428f6`。本次配对耗时 full **3293.1505 ms**、scoped **1966.4554 ms**；仅这一组顺序捕获不能外推到其他设备、所有 scope、整条 UI 链或安全写入时长。较早 full **2757.2775 ms** / scoped **1848.3027 ms** 的捕获仍作为历史测量保留，不与本次数据合并成趋势。
- P8 将监控数据库升至 schema 2。精确且有效的 schema 1 在事务内迁移并保留旧样本；损坏或未知 schema 拒绝迁移。编辑缺口按真实 durable step、准确稳定对象身份和实际恢复样本记录，不伪造终点或补造样本；迁移不重写旧历史 TRX，也不迁移旧 core 库中的监控行。
- P8 的 `MonitoringEditTests` 缺口/删除恢复用例、`MonitoringSqliteStoreTests` schema 1 迁移/损坏拒绝用例及轮换 multiset 用例在 `integration-tests` TRX 中有 Passed 记录；它们不属于 433 项定向 device gate。生产采样目标重绑、未受影响目标持续采样、实测 gap 关闭与重启/轮换端态尚不能由这些 helper/持久化测试替代。三个 archive/performance measurement 用例仍为 NotExecuted；小阈值轮换不等于生产 1 GiB 与完整 7-Zip 性能测量。

### H00–H12 原界面与当前设备批次

H00–H12 是[计划 §7.2](Plan.md)中的原界面设备验收路线，不是新增向导，也不能由 helper、源码字符串或筛选 TRX 替代。当前批次状态如下：

| 批次 | 结果 | 已知范围 |
| --- | --- | --- |
| B0 基线 | `passed` | H00 写前基线及现有 NTFS 卷改名、联机/脱机、NTFS 扩缩和文件哈希路线；改名计时有专门范围，不能推广为所有操作的延迟保证 |
| B1 无 MSR/RAW | `passed` | MSR 关闭、GPT 零分区/BasicData 不格式化、RAW 分区扩缩、删除及零分区 GPT 独立清空；NTFS 扩缩由 B0 核验 |
| B2 有 MSR | `passed` | MSR 开启后核对 1 MiB/16 MiB 唯一规范 MSR，再独立清空至 RAW |
| B3 普通结构 | `passed` | 已记录的普通批次有效：仅建池 `2fe1658c967b417e876ce51e45745dcb`、首个普通 32 GiB VD `17ca0eea11d9468e85b05f279cb217e0`（RAW、零分区）、池改名往返 `ed926ae7f37e470cb4eedc31e7f4b1f3` / `68cce52422af46b580def639d9fb370b`，以及原界面选 MAX 后把容量解析成数值并传 `-Size` 的普通 VD 创建/重建 [ordinary-max-native-proof.json](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B3/ordinary-max-native-proof.json)。这里的“原生”指通过原界面执行，不是 Windows 原生 `UseMaximumSize=true` 参数。新的普通 `UseMaximumSize=true` 正例另见下方。重建两步解散 `b97b36a715614a1790b4d7101081e060` 均为 Verified；取消路线 `35a0109f703c4c42b8fb3b9ca777ab44` 的 `accepted_at_utc_ms` 为 null |
| B4 单 HDD 分层 | `in progress` | 32 GiB HDD tiered VD 设备正向证据见 [hdd32-final-native-proof.json](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B4/hdd32-final-native-proof.json)：`Interleave=65,536`、MSR false、GPT 数据区 NTFS/65,536-byte cluster。旧 `c2a5...` 与 `d92...` 路线使用明确 `StorageTiers + StorageTierSizes` 容量值，其 Failed/NoEffect 不能说明原生 MAX 不受支持；本轮 exact-template/`StorageTiers + UseMaximumSize` 路线 `a11...`（不含 `StorageTierSizes`）返回 Windows 48010，`MediaType HDD + UseMaximumSize` 路线 `b0...` 则观测到没有实际 HDD tier 的普通 VD，并经严格生产 Query/CAS 结为 Failed/UnexpectedEffect。两条新操作均未重放；b0 残留已由原 UI 清理。当前临时 guard 在解散或建池之前拒绝分层 MAX，明确容量和普通 MAX 不受影响。最终分层 MAX 路线仍未决定，B4 不关闭；这不是永久产品边界或 Windows 不支持结论 |
| B5 恢复末态 | `passed` | 本轮原 UI 写入的 WDC 在线 GPT、16 MiB canonical MSR、`WinPool_Test`/NTFS/64 KiB/E: 末态经 fresh native/full 对照复核；13 项末态断言通过（14 次只读查询），16 个 storage sources 的 typed facts 相等。Samsung scoped comparison 为 0 changes、0 evidence gaps，含 4 项共享 Primordial scope adjustments；41 个 staged 与 20 个 authority hashes 相符。偏好恢复、Real Off / Monitor Off 20 Hz 普通重启及原托盘退出也通过，细节与范围见下方本轮证据。旧 3 个 gap endpoints 仍为 Unknown，未被改写，也不再冒充当前监控告警 |

B0–B3 的既有批次记录仍为通过，不表示 H00–H12 整体验收完成，也不覆盖其他未执行的原界面路线。本轮 B5 末态和受保护对象 fresh comparison 已通过；H00–H12 原界面路线及批次对应关系如下，每个 H 点的通过状态须由对应现场记录判定，不能只凭方法名或批次名推定。

| H | 原生界面设备路线 | 对应批次 |
| --- | --- | --- |
| H00 | 写前基线与准入：准确 WDC/Samsung 关联、偏好、盘符占用、依赖和未终结操作 | B0 |
| H01 | 原结构页空草稿、拖入/移出、属性、撤销/放弃及 Apply 前零写入 | B3 |
| H02 | 普通单盘池/VD 创建及自动布局、明确容量与 MAX | B3；另有下文 ordinary native MAX 正例 |
| H03 | 单 HDD 分层池/VD、模板/实际层和自动布局 | B4；tiered MAX 未通过 |
| H04 | 已有对象按 Enter 改名、失焦不提交及属性/反馈 | B0、B3、B4 |
| H05 | 原流程明确的普通与单 HDD 分层删除重建、取消与删除前拒绝 | B3、B4 |
| H06 | 两个原编辑页的视觉、起终点字段、窄窗口、拖动和滚动 | B5 旧运行的分区页视觉检查通过：中英文 480×700 属性/目标页、纵向滚动、中文 1440×900 E: 卷截图，以及扩展无候选提示和压缩公式只读展示；本轮清理后的末态未复拍。其余结构页/拖动场景仍按各批次核验 |
| H07 | 联机/脱机、清空至 RAW、MSR 关闭/开启初始化及 GPT 按钮状态 | B0、B1、B2 |
| H08 | 原界面真实扩展/压缩公式、范围边界及写后容量/哈希 | B0、B1 |
| H09 | 操作进度、图形遮罩、准确范围刷新和终态反馈 | 横跨各批次 |
| H10 | 监控运行中编辑、目标重绑、未受影响采样与真实缺口 | B3、B4、B5 历史范围（恢复 session 通过；历史 3 个 endpoint 保留为 Unknown） |
| H11 | 排除用户等待的分段时长/采集次数及前后比较 | B0 起记录；B5 配对是历史数据，见下方；不得外推到其他设备或所有操作 |
| H12 | 最终复采、WDC 末态、Samsung 比较、偏好恢复和正常退出 | 本轮 WDC native/full 末态复核、Samsung scoped comparison、偏好恢复和原托盘退出均通过；重启后 Real Off / Monitor Off 与 Accepted/CallIssued 计数保持基线。历史 3 个监控 gap endpoints 仍是 Unknown |

H04 的六次卷标往返已在 B0 留证，但不单独关闭其全部属性/反馈路线；B0 的改名时长也只覆盖该操作链。B3 原界面普通 MAX 创建/重建把候选值解析成数值后传 `-Size`；新的 Windows `UseMaximumSize=true` 普通 MAX 成功是另一条独立证据。B4 的 32 GiB 单 HDD tiered VD 正向观察和断言通过；先前明确 `StorageTiers + StorageTierSizes` 容量路线及本轮两条不同的 native MAX 路线均未产生已验证的实际 tiered MAX 结果。分层 MAX 当前由临时 guard 在计划中拒绝；最终策略与设备正例仍未完成。B5 的 WDC 物理最终布局、独立 native/full 与 Samsung scoped comparison、偏好恢复和 App/Agent 原托盘退出均已通过，详见本轮证据。历史 3 个 gap endpoints 仍保留为 Unknown，且不再冒充当前异常告警。H03/H05/H09–H10 依赖的分层、重建或监控联动检查继续按原路线判定；V0.59 全阶段尚未完成。

较早 H10 只读监控快照记录新 session `a122c87fc2934ab2b584e1d9cd09ca24`：新建 VD 已有非 NULL activity/read/write 样本；当时 Samsung gap 为 0，WDC 入池后缺少准确的 OS-disk 绑定，仍有开放 gap。该快照只说明当时所列样本和缺口。后续 B5 session/性能结果是各自历史运行证据，见下方；历史 open endpoints 仍保持 Unknown。

本阶段对受保护 Samsung 的比较只说明记录中列出的准确关联对象与字段范围。正面 `IsPrimordial` aggregate 字段和范围外成员边按比较规则排除；`0 changes` / `0 evidence gaps` 不能推广为整机对象全部不变，也不能排除 Windows 后台 I/O。历史和本阶段 TRX 都是原始验收证据；禁止回写或修饰既有 TRX。修复后复跑必须生成新的 TRX，单独记录其结果，不能用来改写旧失败状态。

### 第二次 MAX 只读恢复与阶段工程门历史：2026-10-08 21:21:03

该时点 [完整门 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-212103/summary.json) 覆盖冻结后的 root/editor 源码：12 个项目，**1722 Total / 1719 Passed / 0 Failed / 3 NotExecuted**。392 个纳入哈希的源码文件前后完全一致（0 个哈希差异）；Restore 与 Release build 均退出码 0，构建 0 warnings/0 errors；未请求 CapacityPreflight。3 个 NotExecuted 仍是既有 archive/performance measurement，不计为通过。依赖审计复用已有 23 项目结果，没有重跑。本门不包含 App/Agent 启动、原生 UI 或真实存储操作；版本仍为 V0.58 iteration 8。最新工程门见下方独立记录。

- **d92 标准启动后只读恢复。** [恢复证据](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/second-max-running-recovery/reconciled/asserted-observed-no-effect.json) 记录 operation 与 step 均为 state 6（Failed）；Accepted 与 `operation.step.call_issued` 各仅一次，`WindowsCallIssued=true`。判据为 `real.reconciliation_observed_tiered_creation_no_effect`；冻结前 fingerprint 与两次独立 fresh fingerprint 相同。provider 再次报告 eligible resources 不足。该旧界面 MAX 操作使用明确的 `StorageTiers + StorageTierSizes` 容量值，并非 typed `UseMaximumSize=true` 参数测试。没有重放或成功创建证据；这证明本次已观察 NoEffect，不证明原操作没有调用 Windows，也不证明该参数不受支持。
- **当时的 B4 / B5 状态。** d92 已离开 Running/Unknown 屏障，但单 HDD tiered MAX 的 Windows `UseMaximumSize=true` 成功正例仍缺失；只读 NoEffect 恢复不等于 MAX 成功，也不能推出该参数不受 Windows 支持。该时点 B4 未关闭，B5 与 H06 尚待完成。后续的 32 GiB 正向证据、B5 布局证据和 H06 视觉验收见下方当前记录。

### 最终完整工程门、B4/B5 与 H06 更新：2026-10-08 21:48:21（当时状态；后续由下文更新）

- **完整工程门。** [summary.json](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-214608/summary.json) 为 12 项目 **1734 Total / 1731 Passed / 0 Failed / 3 NotExecuted**，12 项目退出码均为 0。392 个源码哈希项前后相同；Restore/Release build 均为 0，build 0 warnings/0 errors。未请求 CapacityPreflight，依赖审计复用 23 项已有结果。3 个 NotExecuted 是既有 archive/performance measurement，不计作通过。版本为 V0.58 iteration 8。自动门不验证 UI 或真实设备操作。
- **B4 32 GiB 正例。** 修复后只读 Windows provider 事实快照 [hdd32-final-native-proof.json](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B4/hdd32-final-native-proof.json) 的 [断言](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B4/hdd32-final-native-assertion.json) `passed: true`：WDC 上存在 32 GiB tiered VD、HDD template/interleave 65,536 bytes，MSR 偏好为 false，数据盘 GPT 且已格式化 NTFS/65,536-byte cluster。此正向证据不是 MAX 正例。旧 MAX 的 Failed/NoEffect 恢复记录仍保留；它们只描述当次数值 `-Size` 操作结果，不证明 `UseMaximumSize=true` 不受 Windows 支持。单 HDD `UseMaximumSize=true` 正例仍是必需验收，B4 尚未关闭。
- **B5 布局正例。** [final-native-proof.json](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/final-native-proof.json) 的 [13 项断言](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/final-native-assertions.json) 全为 true：目标物理盘在线 GPT，恰有规范 MSR 与一个最大整 MiB 数据分区，MSR 无卷、数据区唯一关联 NTFS/65,536-byte cluster `WinPool_Test` E:；没有具体池或关联 VD。此布局子验收已通过；B5 仍待受保护 Samsung 比较、性能统计、偏好恢复、正常退出/重启及最终末态核对。
- **H06 分区页视觉验收。** 当时现场以 WDC E: `WinPool_Test` 为准确选中对象。中文/英文 480×700 属性与目标页、纵向滚动底部及中文 1440×900 宽屏截图均已检查；两行起点/终点/容量、NTFS/64 KiB、底栏和窄屏布局可读。只读缩容预览显示 `3,815,429 MiB − 1 MiB = 3,815,428 MiB`；扩展入口因当时 provider 几何没有 1 MiB 目标而提示无可用目标。没有准备或提交扩缩操作。最终宽屏截图：[H06/partition-e-cn-1440x900-final-restored.png](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/H06/partition-e-cn-1440x900-final-restored.png)。其他 H12 性能、受保护对象、偏好和重启检查仍待。

### App 正常关闭生命周期修复与当时工程门：2026-10-08 22:17（历史快照；后续由下文更新）

- **原生退出异常与证据边界。** B5 原验证记录 [normal-exit-result.json](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/normal-exit-result.json)：App PID 14884 经已核实的 Agent 托盘 Exit 路径退出，Agent PID 18124 正常退出，但 App 退出码为 `-1073741189`。对应 [Windows 应用事件](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/normal-exit-application-events.json) 报告 CoreMessagingXP.dll `0xc000027b` 及 WER `combase.dll` `0x80004003`；没有相应的近期 managed crash 记录。源码审查发现旧 `Closed` 异步处理在 await 后仍访问 XAML/UI 状态的风险，因此实施了活窗口预关闭清理、同步 `Closed` 兜底、统一程序化关闭入口及关闭期间的输入/晚到回调防护。事件没有托管堆栈，故生命周期风险是修复依据，不能称为已确认的崩溃根因；没有吞掉或标记已处理 COM/Unhandled 异常。
- **最终完整工程门。** [20261008-221503 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-221503/summary.json)：12 项目 **1766 Total / 1763 Passed / 0 Failed / 3 NotExecuted**，Restore 与 Release build 均退出码 0、0 warnings/0 errors，392 个源码哈希项前后相同；未请求 CapacityPreflight。App.Tests 为 179/179，Architecture.Tests 为 48/48。三项 NotExecuted 仍为既有 archive/performance measurement；依赖审计复用已有 23 项目结果。工程门只证明冻结源码通过自动检查，不验证原生关闭行为。
- **保留的前置失败。** 较早的 [20261008-221056 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-221056/summary.json) 保留 **1766 Total / 1761 Passed / 2 Failed / 3 NotExecuted**，且 `sourcesUnchanged: false`：Architecture 的 Welcome 旧源码断言与 Persistence 归档临时目录 `archive-ledger.json` 文件占用失败。该 TRX 和错误记录未改写；修正后的冻结源码由 22:15 完整门单独验证，Persistence 文件占用未在该门复现。另一次构建尝试的 [build.log](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-221408/build.log) 也原样保留。前置错误不计入 22:15 门，也不因最终门成功而抹除。
- **仍待原生复验。** 本次工程门没有启动 UI 或执行存储操作。Agent 托盘正常退出后的 App 进程码、系统标题栏关闭时保留 Agent 后台行为、普通重启 Real Off/偏好及 Accepted/CallIssued 计数稳定性，仍需 root 按 H12 现场复核；在该证据落盘前，正常退出/重启为 `unverified`，B5 与 V0.59 阶段不标为完成。

### B5 监控恢复、性能与关闭最终核对：2026-10-08 22:38（历史运行证据）

- **最近完整工程门（本次 MAX 修正之前）。** [20261008-223310 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-223310/summary.json) 为 12 项目 **1775 Total / 1772 Passed / 0 Failed / 3 NotExecuted**，393 项源码哈希前后相同，Restore/Release build 退出码 0、0 warnings/0 errors，标准 App/Agent 合并 0 collisions。未请求 CapacityPreflight；依赖审计复用已有 23 项目结果。三个 NotExecuted 仍是既有 archive/performance measurement；版本保持 V0.58 iteration 8。该门不包含新增的 typed MAX App 解析、UI 确认文案与直接测试；它不能替代稍后的集中验证。新 Observer 告警呈现回归另有 [定向 9/9 记录](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/monitor-inactive-history-alert-fix/summary.json)。
- **当时 Monitor Off 的告警呈现。** 先前全新启动且 Monitor Off 时，`recovered_endpoint_unknown` / `PendingVerification` 历史项被错误呈现为当前监控异常；这不代表持久 gap 已恢复或重新发生。最小 Observer/predicate 修复只改变活动告警分类，不改采样、身份判定和历史 endpoints；Infrastructure 的 9 项回归验证仅排除此特定 inactive 历史状态，同时保留其他 pending/active 告警。该次标准 runtime 原生记录显示 App 3916 / Agent 14740 启动时 Real Off、Monitor Off，WDC E: `WinPool_Test` 与 Samsung 结构可见，Monitor Off 下无历史“监控异常”提示，见 [main-initial](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-main-initial.txt)、[monitor-off](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-monitor-off.txt)、[disk UI](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-disk-ui.txt) 和 [main-later](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-main-later.txt)。
- **监控 session 与操作计数。** [只读 receipt](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/audit-monitor-repaired-performance/session-810015f675bf45c5a5cf7c2c3bd2aca1/final-readonly-receipt.json) 记录 session `810015f675bf45c5a5cf7c2c3bd2aca1` 停止、`dropped_samples=0`、当前 session gap 为 0；三块准确目标各有 4,252 条完整有限值样本，均值 20.006 Hz、中位间隔 47 ms、最大间隔 65 ms。18 个 durable Accepted→View 窗口与另 18 个 AcceptClick→ViewReady 性能窗均通过；六次 rename 使 plan、Accepted、Accepted event、CallIssued boundary 分别增加 6，计数到 245 plans / 193 Accepted / 193 accepted events / 244 CallIssued。三个历史 open endpoints 仍为 Unknown，没有被此轮修复或关闭。
- **六次改名性能。** [验证后比较](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/monitor-restored-performance/verified-performance-comparison.json) 匹配 incoming label `WinPool_Test` 的 B0/B5 样本 2/4/6；中位数由 **18.472 秒**降至 **15.437 秒**，减少 **16.43%**。计时范围是 accept click 到 view ready，排除 Prepare/input 与人工确认等待；六次样本均已 Verified 且具完整 matching scope。它只覆盖一块 WDC，不能外推至其他设备，B5 采集器逐阶段 `captureCount` 未记录。更早的 **15.76%** 统计来自 WDC 监控流为 0 的首轮，不是同负载对照，保留为历史而不作当前结论。监控/性能/受保护比较证据来自 221503 标准 runtime；223310 的源码仅改历史 inactive 告警呈现，没有重跑六次测量，也未更改采样语义。
- **受保护对象与 WDC 末态。** [final2 独立执行 receipt](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/audit-final-production-full/final2-after-monitor-repair-six-renames-and-manage-refresh/independent-final-execution-receipt.json) 给出 Samsung 限定比较 `passed=true`、0 changes、0 evidence gaps，并有 4 项共享 Primordial scope adjustments；此范围不等于全机不变，不能排除 Windows 后台 I/O。[WDC 原生 crosscheck](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/audit-final-production-full/final2-after-monitor-repair-six-renames-and-manage-refresh/wdc-layout-native-crosscheck.json) 的 8 项检查全部 true。证据为 fresh read-only provider/UI refresh；未做真实结构写入。
- **最终关闭和偏好。** 正常标题栏 Close 以进程句柄复核为 App exit code 0，同时 Agent 仍运行；之后 Agent 托盘 Exit 以原入口关闭 App 与 Agent，两者 exit code 0，见 [标题栏 Close](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/close-repaired-titlebar-native-result.json) 与 [最终 tray Exit](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-exit-result.json)。普通重启与第二次全新启动都保持 Real Off / Monitor Off、原偏好基线以及 245/193/244 计数；最终核对见 [final-after](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-after.json)。第一次标题栏探针 App 17736 的 ExitCode 缺失是仪器缺口、不计通过，详见[原始探针](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/close-repaired-titlebar-result.json)；修复前 App 14884 的 `0xc000027b` 事件仍作为历史证据保留。旧崩溃之后的原生复验通过，不证明其根因。独立[只读最终复核报告](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-readonly-review.md)与[结构化结果](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-readonly-review.json)覆盖四份 Monitor Off 界面快照、偏好/计数及限定时间段的事件与进程查询；它确认原生 Agent 托盘 Exit 两进程为 0，并明确不延伸为全历史或未来保证。
- **当时的剩余计划范围。** 截至该记录时间，B5/H06/H10/H11/H12 所列范围已通过，唯一未关闭的真实界面目标被记为 B4 单 HDD tiered MAX 正例。后续普通 native MAX 与分层 MAX 尝试见下文；本段不再作为当前状态。旧测量和 Samsung 比较继续限于其记录的单机/scope。

### 2026-10-09 原生 MAX 运行与当前未决项

- **普通 VD MAX 通过。** 原界面普通池路线 [b085 operation](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Ordinary/create-native-maximum/b085439f8cd645bb9f65512503f23359.json) 使用 `UseMaximumSize=true`；[布局断言](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Ordinary/native-maximum-layout-assertion.json) 与[只读 provider 证明](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Ordinary/native-maximum-layout-readonly-proof.json)核实普通（非 tiered）VD/OS disk 均为 **3,999,688,294,400 bytes**、canonical MSR 16 MiB、NTFS/64 KiB 与 `WinPool_V059_Data` W:。随后原 Agent 菜单 Exit 的 [App/Agent 退出码](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/ordinary-native-max-observed-exit-result.json) 均为 0。该结果证明普通 native MAX 路线，不证明 tiered MAX。
- **Exact-template tiered MAX 失败。** 操作 `a11ca8d034414b4aa275bb2d62625981` 使用 exact HDD template 和 `StorageTiers + UseMaximumSize`（不含 `StorageTierSizes`）；Windows 返回 48010。其[只读复核](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/exact-template-native-max-readonly-review.md)核对 operation/step 为 Failed，调用后无创建对象，冻结 fingerprint 前后稳定、storage jobs 终态，未重放。它只否定这条组合路线，不能推出其他原生分层 MAX 路线受 Windows 限制。
- **MediaType HDD tiered MAX 未通过。** 操作 [b0](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Tiered/create-wire-corrected-native-maximum/b0ca4d950b2c43dcb6c936b33ca40730.json) 改用 `MediaType HDD + UseMaximumSize`，但 fresh provider 返回的是没有实际 HDD tier 的普通 VD。原 UI 查询及 CAS 严格收口为 `Failed/UnexpectedEffect`；[只读独立复核](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/closed-query-recovery-host/root-verification-independent.json) 确认记录未改写、Accepted/CallIssued 各一次、对象残留和两次 fresh closure 相符。没有重放，也没有把这次结果算作 tiered 成功。
- **恢复清理与临时 guard。** 随后原 UI 操作 `99f2ccc16d9a42adb362b0624c9c6389` 的[清理记录](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Tiered/cleanup-observed-residual/99f2ccc16d9a42adb362b0624c9c6389.json)核实了 residual VD、template、pool 的删除步骤均 Verified；未重放。由于目前没有通过的原生 HDD-tiered MAX 创建路线，UI/planner 现于任何解散或建池计划之前临时 fail closed 并要求明确容量；ordinary native MAX 和显式 tiered 容量路线不因此改变。这是针对已观察风险的临时防护，并不定义永久产品支持边界。新的 tiered MAX 方案和正向实机证明仍待完成。
- **WDC 当前末态、偏好与退出。** 普通池清理之后，原 UI 完成清空 [d24ec…](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/physical-clear/d24ec284142c41a9b497edcea6d714d1.json)、GPT 初始化 [a1d958…](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/physical-gpt/a1d95850e68d4be78a6eee8ec20e54d1.json)、canonical MSR normalization [32b551…](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/physical-msr-normalization/32b551c985184ccca41da19a22f84767.json) 和数据分区 [f06077…](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/physical-data/f06077e044394dd085d4caaa6e85404a.json)。`f060...` 的 create/format/assign-letter 三步均为 Verified；[全部写入完成记录](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/all-writes-completed.json)时间为 2026-10-09 02:04:29.4876334Z。原生界面快照显示 WDC 在线 GPT、canonical 16 MiB MSR、E: `WinPool_Test`、NTFS/64 KiB，见[最终布局页](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/physical-final-layout-ui.txt)。[B0 偏好对照](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/preferences-baseline-comparison.json)各项均相同，[Agent 设置](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/agent-settings-final.json)为 Monitor Off/20 Hz；该状态下原 Agent 托盘 Exit 的 [App/Agent 退出码](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/physical-final-observed-normal-exit-result.json)均为 0。
- **独立比较与重启复核。** 写入后 fresh native capture 于 02:06:19.499–02:06:20.840 UTC、full capture 于 02:06:32.692–02:06:35.578 UTC 完成：13 项末态断言通过（14 次只读查询），16 个 storage sources 的 typed facts 相等；Samsung scoped comparison 为 0 changes、0 evidence gaps，含 4 项共享 Primordial scope adjustments，WDC 与 full capture 逐字段一致，41 个 staged 与 20 个 authority hashes 相符；复核报告 15/15 检查通过。详见[最终保护比较复核](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-protected-comparison-review.md)及[结构化结果](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-protected-comparison-review.json)。普通重启后 App 16372 / Agent 4296 为 Real Off、Monitor Off/20 Hz，E: `WinPool_Test` 可见；Accepted 214 / CallIssued 271、0 unfinished、395 个源码哈希和 B0 偏好均保持不变，见[只读审计](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/after-normal-restart-readonly-audit.json)、[重启 UI 快照](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/normal-restart-full-ready-real-off-ui.txt)和[Monitor Off 快照](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/normal-restart-monitor-off-ui.txt)。原托盘 Exit 的 App/Agent 退出码均为 0，见[最终退出回执](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/normal-restart-exit-exit-result.json)。普通 UI 解散旧结构的 scoped 复核 20/20 通过（准确操作 `ae5a3bd3f5e8465cadcb1c116be27fba`、generation 145；fresh provider Primordial 边界与 Samsung 保留均核验通过），见[scoped release review](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/scoped-release-original-ui-review.json)。这些比较限于记录的精确 selector、对象与采集窗口，不证明全机或后台 I/O 无变化；三个历史 open gap endpoints 仍为 Unknown。
- **自动验证状态。** [新一轮定向门命令记录](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/consolidated-direct-20261009-095138/commands.json)及 Infrastructure/App/Application 三份 TRX 分别记录 440/440、166/166、6/6 Passed；这不是完整门。随后完整门 [095259 summary](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-frozen-gate-20261009-095259/20261009-095259/summary.json) 为 **1866 Total / 1856 Passed / 7 Failed / 3 NotExecuted**，Restore/Release build 成功，源码哈希未变。七项 App failure 均是 UI 帮助文案收紧后旧断言仍期待旧字串；失败 TRX 保留原状。同步断言后的 [095540 最终完整门](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-frozen-gate-20261009-095540/20261009-095540/summary.json) 为 12 项目 **1866 Total / 1863 Passed / 0 Failed / 3 NotExecuted**，`sourcesUnchanged: true`；Restore/Release build 成功、0 warnings/0 errors。三个 NotExecuted 是既有 archive/performance measurements，不计为通过。

## 选择验证范围

- 纯文档任务：检查内容一致性、当前链接、归档完整性及 Git 范围；不运行代码、原生、设备或视觉测试。
- 普通修复/小功能：运行能验证风险的直接相关检查，不自动扩成全套验收。低影响、可逆修改不为凑数量添加测试。
- 执行已确认 Plan：其中明确要求的回归和自动门属于任务本身，无须重复申请；阶段收口运行 Plan 指定范围。
- 完整人工、平台、设备和发布验收：仅在用户明确要求或已确认计划明确包含时进行。完成代码不自动表示完整人工验收开始或结束。
- 发现失败先判断是实现缺陷还是已失效的旧约定；不能删除有效安全断言来让测试通过，也不能让旧断言恢复用户已经否定的行为。

结果只能使用 `passed`、`failed`、`unverified`、`not_required`、`deferred_by_user`。跳过、缺少环境和未运行均不能报 passed；源码推断与实测证据要分开。

## 文档与架构检查

- 当前内部文档为中文单一权威，仅根 README 成对维护；历史副本原样保留，不要求归档一律配对或翻译。
- 有活动阶段时只有一个 `docs/Plan.md`。用户要求保留的待执行计划可继续留在该文件中，但须明确标记“未激活”，不得当作当前实施授权。具体激活状态以 Plan 为准。Design 不被枚举为待执行计划；标题或旧正文中的命令式措辞不改变其状态。
- 检查当前文档链接和路径、当前版本与目标版本的区分、已实现与待验证的区分。
- 历史文档的原相对链接按归档说明追溯，不为修历史链接重写当前规则。
- 保留真正的依赖、单写入方、类型化命令与默认拒绝边界测试。文档检查验证当前约定，不依赖整段固定句子或把所有旧文件数量当成永久产品要求。
- Git 排除生成物、数据库、日志和源图，保留软件实际消费的资源。

内部文档为中文单一权威，仅根 README 成对维护。该约定由架构测试 `CurrentInternalDocumentsAreChineseAuthoritativeAndOnlyRootReadmeIsPaired` 固定。

## 存储语义与提交检查

优先使用固定输入、纯函数规则测试和实际应用服务集成测试；不以扫描源代码字符串代替行为验证。

- 已知合法、已知非法、信息不足都覆盖；未知不能被默认值转换为允许。
- 原始容量、逻辑容量、占用和可用范围分别检查；覆盖不同冗余、边界、对齐、溢出和估算来源。测试值不是 Windows 实测证据。
- 采集 → 转换 → 保存 → 加载保持身份、用途、层参数、卷和挂载点；缺失值与采集失败仍可辨认。
- 对象关联和关系投影一致；创建、删除、成员变更后无悬空引用、重复身份和错误归属。
- 预览与提交使用同一操作序列；拒绝时不产生部分模拟提交；修订冲突不覆盖其他修改。
- 覆盖“提交成功但回复丢失”，验证结果未知和持久化对账，不能仅断言抛出了异常。
- 能查看 Windows 结构不代表已验证相应修改操作。只读采集样本不能冒充真实写操作验证。

## 监控持久化与归档验证口径

监控轮换阶段的具体场景归[阶段计划归档](Archive/20260921-monitoring-rotation/Plan.md)，以下规定后续回归的证据口径，不自动赋予未运行的场景通过结论：

- 连续性测试必须在切换期间持续产生样本，并覆盖多次轮换；按样本身份或完整值的多重集核对，不只比较总条数。多个设备共享时间戳、同库多个会话、NULL 与零都须可区分。
- 故障测试验证最终数据和文件状态，不只断言抛出异常；覆盖旧库改名前后、新库初始化及归档发布/原库释放边界。重启不能以新建空库掩盖唯一有效旧库失联。
- 数据根迁移需证明恢复记录只引用目标根内的文件，未在旧根继续压缩、写入或释放源库；归档恢复以完整性和内容校验为证据，不以文件存在代替。
- 验证有界缓冲和后台故障时，同时检查实时采样、实际持久化、已知未保存数量与用户诊断的一致性。正常窗口淘汰不能计入持久化丢样。
- 实际 7z 测量记录输入数据、固定参数、原始/归档大小、耗时及峰值资源；小阈值故障测试不能代替生产 1 GiB 阈值与完整归档验证。构建成功不能代替这些行为测试。

## 自动门

正式阶段如 Plan 要求，在 WinPool 仓库根运行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

自动测试使用各自夹具规定的数据，不修改真实存储结构。开发阶段可直接关闭 WinPool 进程、修改或重建已核实的 WinPool 开发数据，构建输出默认写入标准 `artifacts/Release`；不为保留旧运行实例额外搭建隔离产物或重复测试。记录实际命令、提交基线和结果；警告逐项说明，未解决失败不能作为完成。

App 和 Agent 独立产出、SHA-256 并集合并与碰撞失败机制继续验证；运行时查找与目录布局必须一致。命名管道身份/ACL、SQLite 所有权、原子提交及只读边界仍受直接回归保护。

## 人工与设备证据

WinPool 是原生多进程应用。浏览器 DOM 测试不替代 WinUI、托盘、原生选择器和设备检查。

需要实际证据的项目包括页面与拖拽、软件中英切换、主题/DPI/高对比度、键盘操作、托盘生命周期、文件夹选择器、监控启停和数据位置往返。1.x 的测试页验证路线占位及可访问性；当前开发页显示有界内存消息，验证其导航门、详情复制和退出后清空，不据此开发日志文件查看器或完整工作区。

模拟规则验证不执行真实存储写操作。真实写入须沿用 Product/AGENTS 已确认的准确授权，并记录当次目标与结果；自动测试和 CI 不触碰真实结构。缺少设备或目标平台时明确未验证，不制造通过记录。

自动门证明工程行为，不能批准视觉意图或物理设备行为。用户接受阶段也不能把未执行用例改写为 passed。例外记录原因、范围、批准者和风险，保持简短可追溯。
