# WinPool V0.48：模拟语义、存储模型与提交链路重构

## 0. 状态与交接

- 状态：范围与下述实施约定已按 2026-09-10 用户讨论确认，计划已编制；**代码未开始修改，交给后续 Agent 执行**。
- 代码基线：`39f0d4eb3c141b169632976e598754b299a5a997`，`main`，已推送 `origin/main`。
- 文档基线：本次文档重构提交；执行者先检查 Git，记录实际起点，不把代码基线误当成唯一所需提交。
- 当前实现版本：V0.47；目标：V0.48。只编写文档时不改 `Directory.Build.props`，收口时按 R7 升版。
- 本阶段取代原 V0.47 控件 Plan。旧计划已有对应实现及已知缺陷，不能再按其“未开始”状态重做，也没有据此宣告人工验收完成。原件见 [文档重构归档](Archive/20260910-documentation-reset/README.md)。
- 当前已有的 `temp/磁盘分区编辑页-控件设计.md` 是用户未跟踪文件，保留，不纳入提交或默认作为需求。

读 [AGENTS](../AGENTS.md)、[Product](Product.md)、[Development](Development.md)、[Quality](Quality.md)及本计划即可进入阶段；按任务读取代码。不批量读 Archive/Reference/Design。收到“执行 V0.48”后按 R0→R7 连续完成本计划规定的实现和自动验证，无须每项再请求确认。

## 1. 目标与禁止扩张

交付一个只能提交已知合法 Windows 存储配置、容量语义清楚、关系一致、可通过 Agent 原子持久化并在回复丢失后对账的模拟编辑闭环。补全相应真实只读采集，为 V0.5x 提供可复用基础。

固定边界：

1. **不加入、不调用真实存储写操作。** 不创建 V0.5x 的实际写入适配器或完整后台作业执行框架；自由形式命令仍禁止。允许固定只读采集与能力查询。
2. **不保留旧开发数据，不做兼容。** 直接采用新数据库/文档/IPC 格式，拒绝旧格式，不编写迁移、双读双写、旧模型适配或通用未知字段回传框架。
3. 保留两进程、SQLite 单写入方、现有依赖结构、布局引擎、硬件报告和软件中英界面。只调整当前功能需要的模型、规则、操作规划、适配与显示。
4. 不加入完整硬件页、StorageFast/HardwareFull 总体拆分、多发行通道、MSIX/Store、基准测试、AI、S2D/集群、VHD 或新的功能家族。[Design](Design/README.md)不是任务来源。
5. 不整体重设计页面或重写拓扑排版。允许为表达估算、未知、尚不支持、冲突和结果未知而调整控件与说明。
6. 不以“Windows 规则不全”为由把所有操作统一禁用来通过验收，也不以保留旧样例为由放行非法配置。

## 2. 必须固定的模型与操作约定

以下约定决定实现方向；类的私有拆分和命名可调整，含义不得自行变更。

### 2.1 单一事实模型

- 继续以 Application 的类型化存储快照作为存储用例事实模型，Domain 承载纯规则/数值类型。`InventorySnapshot/StorageObjectView` 和拓扑卡片作为投影，不再各自定义不同的存储事实。
- 新增独立 Volume 实体及稳定关联。Partition 只负责分区身份、类型、起点、长度和磁盘归属；Volume 负责文件系统、标签、空间、卷身份及访问路径集合。无盘符、有文件夹挂载、多个访问路径、无卷分区必须可表示。创建/格式化/删除同时维护关联，不长期保留两份可写卷属性。
- 保留原始分区类型标识、磁盘/池只读与原因、用途及未知状态、层副本数和冗余数、虚拟磁盘分配/逻辑/占用信息；精确字段按实际消费者定义，不复制整个 CIM schema。
- PhysicalDisk 的 Windows Usage 为一个规范状态，保留未知/未覆盖值和来源；HotSpare/Retired 由用途投影，不维护可能同时为真的两个独立事实。两者不算数据成员，不构造假的 StorageTier。
- 内部 ID 必须连同系统和稳定性使用；DiskNumber、盘符、名称只能作为属性。提供程序定位信息与显示 ID 分开；敏感原值只在必要的 Agent 采集内存中使用，持久化、导出、日志仍脱敏。
- 类型化父子/成员关联作为唯一来源。通用 Relationships、通用 Inventory 和拓扑由统一投影生成；验证身份唯一、端点存在、归属一致，不靠多个调用点手工补边。
- 保存原始观察和编辑意图的区别，但不复制三套完整对象模型。输入未完成可留在页面；只有合法完整的结果可以提交为有效模拟文档。

### 2.2 规则管理

在现有 Domain/Application 项目建立一个统一校验入口，规则按对象与操作拆分。结果包含允许/拒绝/信息不足、稳定原因码、对象引用及必要诊断。规则依据就近记录官方来源、适用系统/提供程序和已知限制；不用可执行文档、DSL 或插件注册框架。

| 规则组 | 必须覆盖的输入和决定 |
| --- | --- |
| 目标能力 | OS/版本/SKU、提供程序和本次操作所需能力；导入系统使用其来源条件，缺失返回信息不足，不偷用本机能力 |
| 成员与用途 | CanPool/不能入池原因、归属、在线/只读、系统/启动/页面文件/崩溃转储角色、数据/备用/退役；未知不视为安全 |
| 布局与冗余 | Simple/Mirror/Parity、有效数据盘数、copies、columns、冗余、介质、扇区、interleave；非法组合被拒绝，未知组合不能应用 |
| 虚拟磁盘/层 | Fixed 和本阶段已验证的布局、容量边界、层与虚拟磁盘关联；读取多虚拟磁盘不受创建一块的限制 |
| 分区与卷 | GPT 创建范围、分区区间不交叠、盘内边界、访问路径冲突、文件系统/SKU 能力、格式化和扩缩容限制 |
| 编辑语义 | 有数据时的重建风险、实际允许的转换、成员移除/退役后冗余和已分配空间；不能用改标记模拟已经完成的数据疏散 |

规则区分 Windows 合法性、产品支持范围、研究风险提示。经验风险警告不等于 Windows 不支持；Windows 支持也不代表 WinPool 已支持该编辑。

旧“单盘 Mirror 警告即可继续”等行为取消。相应旧测试改成合法性测试，不能为了维持旧断言放宽规则。Retired/HotSpare 只在目标条件有依据时允许；不是凭 UI 切换即合法。

必须为现有操作种类逐一记录结果：支持并具备正反例，或因明确能力/规则限制而不支持。该矩阵写在本计划 R2 执行记录中，禁止隐藏降级：

- Rename、ChangeDriveLetter、FormatPartition、DeletePartition、SetDiskOffline；
- InitializeDisk、ConvertDisk（只保留已确认的 GPT 路径，不新增 MBR）、CreatePartition、ExtendPartition、ShrinkPartition；
- CreateStoragePool、CreateTieredPool、CreateVirtualDisk、DeleteVirtualDisk、UpdateStoragePool、DissolveStoragePool；
- MovePhysicalDisk、EvictPhysicalDiskFromTiers、SetDiskUsage；
- OptimizePool、OptimizeDrive。

本阶段至少完成：合法配置的建池/分层/虚拟磁盘闭环、GPT 分区与 NTFS 卷创建/改名/盘符/格式化/删除、完整草稿应用、已验证前置条件下的成员与属性修改。ReFS、复杂容量形态、涉及无法模拟的数据搬迁等按条件明确不支持；不能把这一条当成任意砍掉基础闭环的理由。扩缩容没有适用的 supported-size/文件系统限制依据时，保留入口并说明尚不支持，不仅凭已用空间或盘尾距离放行。

### 2.3 容量与证据

- 统一使用整数 bytes，区分 RawPhysical、Logical、Allocated/Footprint、Available/SupportedRange；名字可适配现有类型，语义必须保留。
- 容量携带来源：采集值、提供程序支持范围或模拟估算；来源、采集上下文/时间和失效条件可追踪。不是给每个字段加通用元数据库，只覆盖会影响规则和容量的事实。
- 改名不失效容量；改变成员、用途、布局、copies/columns、分配相关参数时，使受影响容量与能力查询失效。不得保留原实测标签套用到新结构。
- 数据盘容量先按受支持布局折算冗余，排除备用/退役；不得令 Mirror logical=raw。成员大小不同、不完整列组和已有占用不能靠简单比例掩盖。
- 扣减与比较保持同一容量语义：物理占用从物理预算扣，逻辑使用量从逻辑预算扣，再按布局转换；不能直接用 physical Allocated 从 logical Size 相减。复用同一余量策略时避免层与虚拟磁盘再次重复扣减。
- **V0.48 的有界简化策略：** 对规则已经支持、可建立容量上界的 Fixed 布局，先扣已知占用/开销，得到可用逻辑上界；再保留至少 1% 的规划余量，按已知分配粒度向下对齐。1% 是本项目的初始保守估算政策，不是 Windows 规定或成功保证；已知开销要求更大余量时采用更保守值。参数集中定义并记录依据，不散落在页面。
- 不把 1% 推广为所有布局、分区扩缩容或所有 Windows 的公式。无可靠上界、粒度或能力时返回信息不足，不猜测。不能为了让测试通过调小余量；如正例无法在此政策下成立，记录具体证据和改动理由。
- 同一未变化配置有适用 Windows supported-size 结果时，按其 Min/Max/Divisor 或离散合法值约束；不能套用其他配置的历史 Max。保守值低于最低值时拒绝，不向上扩大。
- 相同单位下，上界 200G 且对齐无额外损失时，1% 余量示例为 198G；用户提供的 UseMax 约 199.86G 仅是案例背景，不作为通用实测常量。已有实测 199.86G 的对象不因改名被压成 198G。
- 用户显式请求超出可用/支持范围的大小时返回可理解的校验错误，不静默夹成另一个值后报告原请求成功；Max/自动大小是显式的估算选择。
- 纯模拟提交后仍保留估算来源，不伪造采集。未来真实执行后刷新实测值的含义在模型中留清楚，实际写入与跟踪留给 V0.5x。

### 2.4 规划、提交与未知结果

- 从草稿和基线生成一份不可变的类型化操作序列；预览、校验和应用共用。UI 不再包含另一套按快照差异临时执行的编排。
- 同一模拟计划中新对象的 ID 在规划时一次分配，执行复用；不能创建后再拿旧草稿 ID 比较并误删。真实提供程序 ID 映射不在本阶段实现。
- 删除只来自明确的删除意图；分区更改独立参与计划，不能挂在“池属性有变化”的条件下。名称、成员、层参数、虚拟磁盘和分区在同一草稿语义内。
- 保留现有属性确认按钮时，只确认草稿输入/生成同一计划，不另做绕过统一校验和事务的提交；统一应用是持久化入口。
- App 检查用于反馈，Agent 在 expected revision/hash 下重验，顺序应用到候选文档；全部成功后以一个事务提交文档、计划、事件、commit 记录。任一步拒绝不产生部分模拟持久化。
- `OutcomeUnknown` 贯穿 IPC、仓储、协调器和 UI。提交已发送而回复丢失时，用稳定提交标识查询 Agent 持久化记录/修订对账；确认已提交则重载，确认未提交才允许安全重试，无法判断则继续显示未知。不能自动重发非幂等计划，不能提示“没有改变”。
- 复用现有事务、提交 ID、事件和修订机制；不另建事件溯源平台。模拟任务结果明确带模拟来源；Optimize 等可保留明确说明的模拟无操作结果，但不得声称测得重排/性能改善，不能假造真实后台任务执行记录。

### 2.5 格式与数据重建

- 保留 SQLite 和现有仓储所有权。根据新事实模型收敛文档/对象/关系存储，不为每个展示属性新增 SQL 表。
- 本阶段格式一次切换：SQLite schema 14→15、StorageSystemDocument 1→2、StorageSnapshot 2→3、IPC 4→5。更新唯一常量、序列化、握手、内置样例和测试；执行记录如发现基线已被外部改动则先报告实际差异，不能覆盖更高编号。
- 旧数据库、导入文件和旧进程协议明确拒绝，不提供兼容入口。测试和调试使用隔离新根；旧开发数据可按已授权范围移入项目根 Rubbish 后重建，不静默擦除未知目录，也不要求保留业务数据。
- 格式切换不改变 app-settings/agent-settings 单写入方、读失败保护、数据根定位和租约；不顺便重写设置、监控或便携部署系统。只有消费新模型所需修改才进入范围。

## 3. 任务与依赖

所有任务当前均为**未开始**。执行时逐行更新状态和短证据，不创建并行版本计划。

| ID | 工作与主要位置 | 必须产出/通过的检查 | 状态 |
| --- | --- | --- | --- |
| R0 | 文档断言适配与失败基线；`tests/WinPool.Architecture.Tests/ArchitectureBoundaryTests.cs`、相关 Application/AgentClient/Sqlite 测试 | 仅根 README 配对；新中文权威、Design 状态和链接；不删除有效架构边界。固定 §4 的失败行为为回归，不依赖忽略目录中的临时 harness | 未开始 |
| R1 | 类型化事实模型；`StorageModels.cs`、`InventoryContracts.cs`、`RawSnapshot.cs`、`EmbeddedStorageInventoryScript.cs`、`EmbeddedPowerShellInventoryProvider.cs`、原生 collector 与持久化模型 | Volume/用途/身份/未知/关联定义贯通；真实只读、内置样例、导入和 generic Inventory 投影语义一致 | 未开始 |
| R2 | 集中规则和容量；Domain 的 `StorageMath.cs`、Application 的 `EditWorkspace.cs` / `StorageSystems.cs` 中相关计算 | §2.2 操作矩阵、规则依据和能力条件；§2.3 保守估算；不再有 UI/服务两套算法；新增字段全链路往返 | 未开始 |
| R3 | 操作规划与模拟服务；Application、现有 Execution 契约、`StorageStructurePage.xaml.cs`、磁盘分区页及 ViewModel | 同一计划用于预览/应用；一次分配模拟 ID；B1/B2 修复；创建/删除/修改后统一重建关系投影并校验 | 未开始 |
| R4 | 原子提交与格式切换；`SimulationEditCoordinator.cs`、`StorageSystemRepository.cs`、Sqlite 仓储、Ipc/AgentClient | schema/文档/协议统一切换；冲突不覆盖、失败无部分提交、提交重试幂等；B3 对账闭环；新数据根可启动 | 未开始 |
| R5 | 当前页面和引擎接入；Manage、两编辑页、Topology 投影、Localization、现有任务呈现 | 显示估算与未知、规则拒绝原因；无未实现功能伪成功；软件双语/主题保留；布局算法不无故改动；旧消费路径退出 | 未开始 |
| R6 | 完整回归与验证；受影响测试项目及整个 solution | §4 回归与 §5 自动门；报告实际覆盖和未验证项，禁止用静态字符串检查替代编辑行为 | 未开始 |
| R7 | 升版和文档收口；`Directory.Build.props`、README 双语、Development、CHANGELOG、本 Plan | 产品版本 V0.48，实际内部格式号一致；所有任务状态真实，限制可见；无旧模型/旧算法并行和未提交任务改动 | 未开始 |

R1 模型变化与最低限度序列化/测试修正可在同一可编译提交内完成，不为机械任务边界制造中间兼容层；R4 负责最终事务/版本闭环。保持每批可构建、可审阅，避免整个重构结束才首次运行。

## 4. 已确认缺陷和必需回归

基线审查定位如下，行号会随重构变化，以方法和行为定位。以下是待修复问题，不是已完成结果。

| ID | 基线问题 | 必需回归 |
| --- | --- | --- |
| B1 | `StorageStructurePage.ApplyPendingSequenceAsync` 创建新池后，使用旧草稿 ID 对比新生成 ID，接着删除刚创建的虚拟磁盘 | 一个有效建池草稿自动创建一块虚拟磁盘并应用后仍有一块；计划无意外 DeleteVirtualDisk；关闭自动创建、保留 RAW、显式删除另测 |
| B2 | 同方法把分区应用放在池属性变化判断之后；仅分区改动被跳过 | 只改分区/卷字段也实际提交；预览和最终结果一致；池不变不能阻断分区；取消/撤销不持久化 |
| B3 | `AgentBackedStorageSystemRepository.SendAsync` 与 `SimulationEditCoordinator` 丢失 OutcomeUnknown，误报失败且未改变 | 故障注入在 Agent 已提交后丢回复：库修订已增加，App 显示未知并对账重载；重复提交不重复创建。另测提交前失败与修订冲突 |
| B4 | `BuildTier/CreateVirtualDisk/NormalizeTierCapacities` 简单相加成员容量并令 logical=footprint | 两副本逻辑上界不超过有效原始容量一半；1% 余量/对齐/已有占用分别测试；备用/退役不贡献数据容量 |
| B5 | CreateTieredPool 创建的 tier 无 VD 归属、VD 无 tier IDs，Relationships 未维护 | 新建后实体关联与统一图投影一致；删除、改成员和重载后无悬空边；空池/RAW/无卷分区与非实体显示组分开 |
| B6 | 模型新增 IsRetired/IsHotSpare、copies/redundancy 未贯穿真实 raw/投影；分区携带的卷身份在操作模型丢失 | 带用途、层参数、卷 ID 和多访问路径的脱敏输入经过采集转换与持久化往返后保留语义；缺失不默认为 false/0 |

2026-09-10 的本地审查在旧代码上通过了 449 项自动测试，但仍复现 B1–B5；测试通过不证明 Windows 语义完整。临时证据位于忽略目录 `artifacts/inspection-20260910/`，可能在重建后不存在，不作为执行依赖。

可重建的故障用例：B1 输出创建后随即删除 VD；B2 分区 adapter 调用数为 0；B3 持久化修订 2、App 修订 1，却返回 Failed 且声称未改变。B4/B5 的小容量纯模拟探针以总量 6,000,000,000 bytes 返回同等 VD logical、两层 copies=2、双方关联缺失；这是算法/关联缺陷证据，不代表该小容量 fixture 已在 Windows 创建验证。新规则下用符合支持范围的样例测试整个工作流，容量纯函数仍可用缩小数字验证数学性质。

还必须覆盖：

- 合法 Simple、两副本 Mirror、受支持 Parity 的正例与盘数/列数/副本非法反例；单盘 Mirror 不再仅警告放行。能力缺失、只读、未知角色单独反例。
- 不同容量成员、零/负/溢出输入、分配粒度、支持范围最小值/最大值、非数据成员和显式超额申请；不支持的估算场景说明原因。
- 已实测容量只改名称仍保留；成员/布局变化后估算来源更新；模拟成功不变成实测。
- 一个物理盘对应的多种 provider 视图、一致稳定 ID、变化的 DiskNumber、没有盘符/文件夹挂载、多访问路径、无卷分区、多虚拟磁盘只读导入。
- 分区扩展与下一个分区冲突、缩小限制未知、ReFS 能力不足、RAW 到 GPT/卷的合法状态序列。
- 一次草稿含成员+参数+分区；撤销/重做/放弃；全部应用；失败时整份模拟文档不变；属性确认不产生独立持久化路径。
- Agent 重连、重复提交标识、回复丢失、冲突和保存后重新加载；未授权本地真实系统的所有结构修改请求仍被拒绝。

## 5. 验证与完成

每批运行直接相关测试。R6 运行 Quality 的 Release restore/test/build/依赖审计自动门及运行树合并检查，属于本计划授权的开发验证。使用隔离输出和新数据根，不替换正在运行的旧程序，不触碰真实结构；无需重新申请普通自动验证。

受影响 WinUI 交互必须给出验证状态：两编辑页打开、建池/分区完整应用、规则拒绝、估算显示、撤销重做、软件双语、Manage 重载；可以进行隔离的纯模拟原生检查。完整跨平台/真实设备/长期人工验收不在本阶段自动启动。没有相应环境或人工证据时写 unverified，不能称整个产品已经验收通过。

完成条件：

1. R0–R7 对应实现和必需自动回归完成，已知 B1–B6 有具体修复及行为证据。
2. 支持矩阵有实际正例；拒绝/信息不足/近似边界明确，无未标记的静默成功。
3. 数据、字段和规则只有明确维护位置；预览与应用一套计划；旧语义和兼容路径退出。
4. 新数据根可创建、保存、重载；新 App/Agent/格式配套，旧格式被明确拒绝；真实结构修改依然拒绝。
5. 当前版本、内部格式、文档和实际状态一致；完整人工验收与自动完成分开报告。

本 Plan 允许执行者调整私有实现、函数拆分、直接相关测试和字段命名。不能自行改变：Windows 合法性原则、未知默认拒绝、容量来源、产品创建范围、数据不兼容决定、两进程/SQLite 所有权、V0.48 无真实写入及上述验收。

遇到需要新增产品能力、改变这些约定或缺少 Windows 依据的关键选择，记录具体冲突及有限选项并请求用户决定；其他独立任务继续。常规实现细节不重复提问。阶段完成默认本地提交；本计划不授权新推送、tag、Release、二进制上传或部署。

## 6. Windows 规则核对入口

仅作为对应任务的官方资料入口，不要求全量读取或照搬全部字段。OS 本机 CIM 类元数据可只读核对；它证明字段/方法存在，不等于某 SKU 或配置实际支持创建。

- [MSFT_PhysicalDisk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-physicaldisk)：Usage、SupportedUsages、成员资格、状态。
- [MSFT_StorageTier.GetSupportedSize](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-storagetier-getsupportedsize)：适用布局的容量范围与粒度。
- [MSFT_Partition](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-partition)、[MSFT_Volume](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-volume)：对象身份、访问路径、分区边界与卷属性。
- [MSFT_StorageJob](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-storagejob)：未来真实后台操作语义；V0.48 不实施真实作业调度器。
