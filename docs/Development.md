# WinPool 开发约定

本文件维护技术所有权、数据含义和开发方式。产品范围归 [Product](Product.md)，当前阶段结果见 [V0.52 归档](Archive/V0.52/README.md)和[硬件报告执行归档](Archive/20260915-hardware-report/README.md)，测试要求归 [Quality](Quality.md)。当前代码为 V0.53。统一数据、模拟编辑及十段硬件报告已完成；硬件采集与报告边界见[实施核对](Archive/20260915-hardware-report/实施核对.md)。已知限制见 [CHANGELOG](CHANGELOG.md)。

## 环境与模块

C#、WinUI 3、.NET 10、Windows App SDK 2.4；SDK 以 `global.json` 为准。Windows 项目当前 TFM 为 `net10.0-windows10.0.26100.0`，SDK BuildTools 为 28000 系列。最低操作系统以 Product 为准。

| 模块 | 所有权 |
| --- | --- |
| Domain | 稳定标识、单位和无副作用的存储规则、容量计算 |
| Application | 存储事实模型、用例契约、编辑意图、操作规划和表现投影 |
| Execution | 类型化计划/步骤、执行策略、风险和前置条件、结果与回放；真实修改默认拒绝 |
| Inventory / Monitoring | 采集与监控契约、适配接口及各自数据模型 |
| Infrastructure.Windows | 固定只读 Windows 采集、Windows 适配、现有模拟协调与系统仓储适配 |
| Infrastructure.Sqlite | 事务、仓储、数据格式实现 |
| Ipc / Agent.Client | 封闭的 App–Agent 类型化传输、连接和结果传播 |
| App | WinUI 页面、输入、呈现和交互；不自行实现存储规则或写 SQLite |
| Agent | 每用户可见托盘进程、采集/监控协调、SQLite 写租约和进程生命周期 |

依赖保持表现与适配层 → Application → Domain 的现有方向，Execution 与 Inventory 等边界按现有项目引用验证。优先在现有项目内拆分职责，不新增通用引擎项目、DSL、插件体系或公开 SDK。

`TopologyLayoutEngine` 的布局决策归整数单位计划，像素/DPI 只负责最后映射；不把容量业务规则放入布局算法。修改布局算法时按需读[踩坑记录中的布局案例](Reference/开发踩坑记录.md#布局重构)。硬件页按来源对象展示，不以旧 13 类、154 项为数量契约。旧报告工厂和原始快照解释器已退出构建，有效 CIM/WMI 与原生补充读取保留。

## 存储事实、草稿和操作

共用一套存储对象含义，区分采集事实、编辑草稿和操作结果；不复制三套完整模型。

| 含义 | 约定 |
| --- | --- |
| 身份 | 内部稳定 ID、系统 ID 和提供程序定位信息分工明确；盘符、名称、列表顺序、DiskNumber 不能单独作为持久身份。名称变化不得改变身份、关系或目标定位；缺少可靠 ID 时不以名称猜测跨采集关联。保留稳定性/未知标记 |
| 实体 | PhysicalDisk、StoragePool、StorageTier、VirtualDisk、OS Disk、Partition、Volume 各有明确身份；分区描述几何和分区类型，卷描述文件系统与挂载。无卷的分区也是合法事实 |
| 关系 | 来源事实中的对象关联是唯一事实源；通用关系图、导航和显示是其派生投影，不各自维护另一套可修改关系 |
| 显示分组 | 备用/退役/介质分组不冒充真实 StorageTier。推导的关联带来源，不能自动升级为可执行事实 |
| 未知 | 未采集、读取失败、不支持、否、零、空集合分别按含义表达；不得把缺失状态静默补成健康、可写或无系统角色 |
| 草稿 | 记录用户意图和基线修订。临时输入不完整不等于允许生成非法模拟文档 |
| 操作 | 一次生成类型化操作序列，预览、校验和提交共用它；模拟命令文本只作解释，不再由文本反推执行 |
| 结果 | 区分成功、明确失败、结果未知和修订冲突；传输失败不能证明存储未改变。以 Agent 的持久化记录对账 |

读取遇到超出编辑范围的 Windows 结构时保留原貌及只读原因，不自动“修复”。新增或更改结构必须通过本次操作适用的规则；对无关未知对象不得仅因其存在而阻断其他独立对象的只读展示。

## 规则与容量

规则统一入口，内部按对象/操作分组。合法性规则输出允许、拒绝或信息不足，并携带稳定原因、受影响对象和依据/适用条件。App 用同一规则和模拟操作服务完成表单反馈、动作预检查及内存候选文档；UI 禁用不是校验边界。模拟提交到 Agent 时系统校验明确标记为 `SkippedForSimulation`，Agent 不重跑整套业务规则，只执行封闭请求、格式/哈希、修订、CommitId 对账、事务和单写入方等提交保护。未来真实执行必须由 Agent 重新检查实际 Windows 状态。

规则依赖必要的 OS/SKU、提供程序、布局、介质、用途、扇区及能力信息；只定义当前操作需要的字段。不把本机能力偷偷套用到导入系统。未知组合不默认放行，不为通过旧样例而放宽规则。

容量至少明确原始物理容量、逻辑容量、已分配/物理占用、可用范围和估算来源，不能用同一个值替代。计算使用整数 bytes 和溢出检查；单位转换只在输入/显示边界进行。

- 保留适用的采集值及来源；改名等无容量影响的操作不重新估算。
- 成员、布局或分配相关参数改变后，使受影响的容量/能力证据失效，重新估算并标记，保留未受影响事实。
- 模拟 Simple、Mirror、Parity 分别按布局、数据副本、列数和校验列折算理想逻辑上界，排除热备与退役盘。MAX 与默认创建值向下对齐到 4 GiB，不再扣旧 1% 余量，也不以 Interleave 代替容量粒度；这不是 Windows 通用保证。混合介质逐层计算，物理占用与逻辑容量分别保存。
- 理论估算、保守规划值和真实采集值区分。模拟应用后仍是估算；未来真实创建结束后才由重新采集结果更新。
- 200G 成员合计、UseMax 实得约 199.86G、规划预留至约 198G 是用户提供的示例，不是 Windows 的固定扣减公式。具体受支持估算策略、参数和验证归当前 Plan。
- 容量余量不能替代合法性，也不保证真实操作成功。无法建立保守估算的组合返回尚不支持，不制造精确数字。

新增字段按一条链路完成：采集来源 → 原始数据 → 规范化模型 → 规则/模拟 → 持久化与 IPC → 界面/导出 → 往返和缺失值测试。字段的来源、单位、缺失含义和失效条件跟定义就近维护，不再分别维护大型字段副本。

修复缺陷时，将失败场景补成回归测试，检查实际入口的最终结果（按需覆盖连续操作、重载和失败恢复）。后续修改相关功能时重跑；预期以当前需求为准，不把旧样例固化为产品边界。

## V0.52 统一事实链路

`WinPoolFacts` 保存带来源、时间、类型、读取状态的原始对象及关联；`WinPoolSystem` 与 `StorageSnapshot` 是只读派生结果。跨来源选值由 `WinPoolSourceDetails` 维护，只有明确等价的字段参与备用或冲突判断。缺少安全字段、关联冲突和数值超范围不能变成允许；所有采集字段保持原值，不设置脱敏状态或按隐私开关裁剪。

存储与完整硬件使用独立刷新用途，Agent 串行协调；较旧结果忽略。来源失败保留上次事实及关联时间，成功空集合才移除对象。层成员没有可靠关联时显示归属未知，不按介质相同猜测。

完整硬件刷新在既有 CIM/WMI 事实后追加 `WindowsGraphicsFactCollector` 和 `WindowsNetworkFactCollector`：前者以 DXGI LUID 保存适配器和输出，并用 D3D12 读取功能级别；同一 LUID 通过 D3DKMT 保存适配器类型标志、显示侧描述和渲染侧描述。`IndirectDisplayDevice` 为真时统一对象使用显示侧名称，保留 DXGI 原始描述，并禁止按相同 `VEN/DEV` 借用物理 GPU 的驱动和 PCI 位置。`Win32_VideoController`、`Win32_DesktopMonitor` 与 `WmiMonitorID` 在统一事实中属于字段补充，不形成第二组 GPU 或 Monitor 设备，驱动、型号和厂商仍可按可靠硬件标识补入 DXGI 对象。软件或间接显示 DXGI 适配器均不按标志或名称过滤。网络保持 `WinPool.NetworkAdapter` 统一来源键不变，内部以 `MSFT_NetAdapter` 的 `ConnectorPresent -or InterfaceType -ne 0` 作为对象集合边界，按接口索引关联全部 IP 地址与默认路由，并替换同次脚本采集产生的原始 `MSFT_NetAdapter` 观察。同一来源的成功刷新直接整组替换旧网络对象，不引入跨来源迁移规则。`WinPoolSystem` 是不持久化的运行时投影；入库的是来源事实，启动从来源事实重新生成统一模型，完整硬件数据仍按需刷新。Monitor 不在报告投影中筛除，存储摘要不增加硬件页专用条件。

`HardwareReportProjector` 按统一对象类型完整投影 CPU、内存、页面文件、GPU、Monitor 和 Network；报告可以选择字段行，但不能按来源类名选择或丢弃某个对象。字段备用来源只用于补值，不改变对象列集合。`ManageSystemSummaryProjector` 为管理页与硬件页提供同一存储摘要。App 通过 `PropertyTableVisuals` 小范围复用管理页与硬件页的项名上限、项值上限、列间距、行高和单元格样式：两页项名列使用 `Auto` 宽度及 220 DIP 上限，项值列使用 `Auto` 宽度及 250 DIP 上限；硬件项名网格位于分段横向 `ScrollViewer` 外，标签行高跟随值行。外层纵向 `ScrollViewer` 包含左对齐操作、即时反馈和整份报告，不呈现采集完成时间。设备列选择、悬停、选中后居中和所选列文本复制沿用管理页语义，硬件页不再打开字段详情对话框。标题栏的 `ActiveSystemSelector` 复用现有系统切换入口；下拉选择先暂存，待 `DropDownClosed` 后再切换工作区和重建列表，避免在 WinUI 弹出层仍打开时使控件集合失效。不建立第二套事实模型或通用表格框架。所有字段保持原值；内部格式为 SQLite 17 / IPC 8 / StorageSystemDocument 3 / 来源事实 1。

内置模拟继续以 `StorageSnapshot` 作为编辑模型，但持久化前由 `WinPoolSimulationFacts` 生成 Windows 形态的来源事实。来源仍明确标记为 `FactOrigin.Simulation`，命名空间、类名、字段名、CIM 数字枚举、数组类型和 bytes 单位分别对齐 `Win32_ComputerSystem`、`Win32_OperatingSystem`、`Registry.CurrentVersion`、`MSFT_*`、`Win32_LogicalDisk`、`Win32_DiskDrive` 与磁盘角色补充来源。Partition、Volume 和 LogicalDisk 按真实来源拆分并用关系组合；模拟模型无法提供的 Windows 属性不伪造。系统版本号与 DisplayVersion 分开，系统卷使用 4096 bytes 分配单元，存储空间数据卷继续使用当前测试布局的 65536 bytes。

编辑状态由 `SimulationEditingSession` 集中管理。结构、即时分区和改名共用 `SimulationEditRequest`、规则与类型化步骤；目标分组、用途、分区表类型分别使用 `DestinationGroupId`、`DiskUsage`、`PartitionStyle`，不得塞入 `Name`。命令只解释步骤，未绑定 CIM 目标和无命令操作均明确说明，没有执行入口。

## 数据与生命周期

标准数据根是 `%LocalAppData%/WinPool`，便携模式使用程序旁可写 `Data`；`storage-location.json` 是定位活动根的启动指针。切换前验证目标，现有租约、单实例和生命周期机制继续保留。

| 持久化来源 | 唯一写入者与用途 |
| --- | --- |
| `app-settings.json` | App；语言、主题、当前页面等前台偏好，Agent 只读所需项 |
| `agent-settings.json` | Agent；持续监控、采样率、自启等关闭 App 后仍有效的偏好；App 经类型化请求修改 |
| `winpool.db` | Agent；系统和采集快照、模拟文档、工作区、监控、执行与会话记录 |

偏好按变化原子保存；已存在文件不可读时禁止用默认值覆盖。Agent 偏好的 `SavedAtUtc` 只比较是否变化，不按大小排序；通知、重连和文件观察汇入串行重载。Agent 自己维护指向自身可执行文件的 HKCU Run 项。执行模式和真实操作同意不持久化。

V0.53 当前实施代码为 SQLite schema 17、IPC 8、StorageSystemDocument 3、来源事实 1、StorageSnapshot 3；均是内部格式编号，不是产品版本。新文档只持久化来源事实和应用状态，Snapshot 是无 setter 的只读重建投影，旧硬件报告模型及独立报告生产路径已退出。缓存仍校验哈希，旧格式明确拒绝，不提供迁移或兼容回退。监控样本逐项保存全部 `MonitorMetricKind`，未提供的指标写为 NULL，真实零保持为零；CSV 使用空单元格表达缺失。模拟文档 IPC 先分页读取有界元数据，再按 ID 单独读取正文，不扩大 4 MiB 帧上限。模拟提交的 CommitId 同时绑定文档、前后哈希、修订、OperationId 和 PlanHash，查询返回提交时的不可变文档回执。控制管道握手有独立 5 秒期限，连接级异常记录稳定代码并释放连接，监听任务终止会进入 Failed 并由托盘呈现。实际产品版本以 Directory.Build.props 为准，V0.52 验证状态见[归档](Archive/V0.52/README.md)及[实施核对](Archive/V0.52/实施核对.md)。

V0.53 在 `UserPreferences` 中保存默认关闭的 `DeveloperMode`，旧格式缺少字段时按关闭处理，不升级偏好格式。主窗口从偏好重建可用导航；Hardware、Test、Development 同受该门控制，隐藏状态下启动目标、快捷键和记忆页面均回到 Manage。开发者导航顺序以 Hardware 在 Manage 之前开始。

数据重建只能针对明确的 WinPool 开发数据，不静默擦除未知根。首次打开旧格式应明确提示版本不支持/需重建；测试使用隔离新根。必要的旧开发数据处置遵守 AGENTS 的移动规则。允许丢弃开发数据不取消单写入方、事务、冲突检测和故障恢复要求。

普通启动采用 Windows App SDK 单实例机制；重复启动激活已有窗口。提权交接是整套 App + Agent 重启：新管理员 bootstrap 以 SID 绑定的 ready/continuation 事件进入等待，期间不初始化 WinUI、不取得实例键、也不连接或复用旧 Agent。旧 App 保存工作区并获得旧 Agent 的后台有序关闭确认后才允许 bootstrap 继续；后者必须按 PID、启动时间和路径核验旧 App、旧 Agent 均已退出，才进入普通启动并创建新的管理员 Agent。取消、事件失败、身份不符或超时均不得接管实例、复用旧 endpoint 或强杀旧进程。等待失败诊断写入数据根 `Diagnostics/elevation-handoff.jsonl`；IPC 正常断开不等同于 Agent 故障。SQLite 不是实时 Windows 状态的权威，未来真实操作执行前必须重新核对对象及前置条件。

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

不要部分覆盖正在运行的目录。构建前检查运行进程；若用户正在使用旧运行树，优先采用独立输出树，不擅自结束其会话。正式分发保持完整目录；不包含脚本、PDB、源图、数据库、日志、测试结果或重复子程序。构建输出可保留 PDB，产物不提交。

现有 `build/Rebuild-WinPool.ps1` 会停止 WinPool、调用清理脚本直接清除可再生输出、重建并写入快捷方式。只有任务明确需要该完整动作时使用；`build/Clean-WinPool.ps1 -WhatIf` 可预览。它不是纯文档任务或普通检查的默认入口。

未来正式 staging 使用仓库外未占用的新路径，复现同一并集和碰撞检查；不顺便部署、签名或发布。测试命令归 Quality。

## 文档与版本

内部开发文档只维护中文无语言后缀版本。根目录 `README.md`（英文）和 `README.zh-CN.md`（中文）保持用户信息一致；其他目录中的索引 README 不因此需要双语。历史双语原件不追溯翻译。代码/API 标识和微软原名保持英文。

Product 管产品、Development 管技术、Quality 管验证、Plan 管当前阶段（若有）、CHANGELOG 管重要结果；AGENTS 管操作规则。Design 保存未排期方案，Reference 保存方法，Archive 保存被替代/结束的历史。一个事实一个维护位置，其余用短摘要和链接。

设计只需状态、基线/条件、未决问题三个说明，不引入复杂审批体系。讨论中的设计不等于当前规范；方向已认可也不代表细节冻结。纳入版本时重新核对代码，明确采纳部分并写入唯一活动 Plan，长期决定归各自所有者。无须为每份设计新建一个 Plan。

有活动阶段时，Plan 记录范围、固定决策、任务依赖和验收；执行时及时更新实际状态。阶段被替代时如实归档，不写成验收完成；阶段结束时记重要结果、归档 Plan，没有新阶段就不保留活动 Plan。CHANGELOG 按重要结果记录，长历史可按明确时间点归档，Git 保留过程。

唯一产品版本源为 `Directory.Build.props`：`Va.b` 表示产品线，`Va.bc` 的 `c` 为 1–9 的迭代；迭代为 0 时显示补零，因此产品线 0.5 显示为 V0.50，框架数字版本为 0.5.0。框架必需数字版本由该文件机械生成。

项目不能通过相对路径、复制运行文件、子模块或运行时导入依赖其他仓库。软件资源使用受版本控制的 `assets`，不让代码依赖忽略目录。

## 分区联合体投影（2026-09-15）

`WinPoolSystem` 将唯一的 partition-volume / same-volume 关联组合为 `WinPoolPartition`，`Resolve` 接受所有成员来源 ID。`StorageSnapshot.PartitionUnions` 是管理与业务导出的单层表面，`PartitionSourceIds` 负责旧来源选择恢复。PartitionInfo / VolumeInfo / NetworkDiskInfo 仍作为采集、持久化和现有编辑命令的内部适配记录；不得再次据此建立独立卷属性分类。文件系统属性由同一个 `ManagePartitionProjector` 输出。

磁盘补充事实通过 disk-supplement 关联匹配唯一 Disk.Number。OS 磁盘的 Number、PartitionStyle 等仍读取自己的来源，不能被物理磁盘主来源覆盖。单例 Computer/OperatingSystem 身份按系统和类名确定。按来源刷新只替换本次成功的来源；保留仍被事实字段引用的历史来源及每类最新状态，避免重复失败状态增长。模拟提交保留仍有效的补充关系，删除联合体时同时去掉其附属逻辑来源，真正孤立来源继续保留。

此轮按用户要求只构建和人工检查，不运行自动测试。原生 UI 验收使用独立运行目录和数据库副本；固定采集脚本始终内嵌，经标准输入执行，不输出独立 inventory 脚本。
