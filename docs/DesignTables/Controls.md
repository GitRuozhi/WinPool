# 交互控件说明表

状态：2026-10-08 按当前 XAML 与动态控件代码核对。相同用途的重复行按控件家族归纳；条件只列代码中已实现的规则。悬停帮助指由 `ContextHelp` 提供的帮助或禁用原因；没有特别注明时，不代表所有控件都有自定义悬停文本。按钮图形见[按钮与图标表](Buttons.html)。

## 窗口与导航

界面顶部标题栏行高 48 DIP，系统首选高度为 Tall。图标与 WinPool 标题区、交互控件之间的空白属于可拖拽区域；导航列表、活动系统选择器、真实编辑控件及系统窗口按钮接收各自输入，因此整条 48 DIP 不能任意位置拖动。交互区加载或尺寸变化后刷新 Passthrough 命中矩形，实际剩余可拖宽度随窗口和语言变化。

| 控件 | 用途 | 帮助／禁用条件 | 源码 |
| --- | --- | --- | --- |
| 标题栏导航列表 `ShellNavigationList` | 在硬件、管理、结构、分区、测试、监控、开发、设置页面间导航；选择项提供页面本地化名称和图标 | 硬件、测试、开发仅开发者模式可见；Agent 工作区恢复期间禁用，完成后启用 | `MainWindow.xaml`、`MainWindow.xaml.cs` |
| 标题栏活动系统选择器 `ActiveSystemSelector` | 以 260 DIP 宽度显示当前选中存储系统名称，并切换本机、导入或模拟系统；下拉项上限 280 DIP | 有页面级帮助。启动时偏好读取之前隐藏；若有效的只读缓存预览先完成，则显示系统名但选择器及页面交互仍禁用；无有效预览时保持隐藏。Agent 成功完成目录/状态恢复后，存在所选系统时显示并启用；恢复失败时保持禁用，若此前显示过预览则仍可见，否则隐藏 | `MainWindow.xaml`、`MainWindow.xaml.cs` |
| 系统标题栏关闭与程序化关闭 | 通过统一关闭准备保存 App 状态，再关闭主窗口；关闭准备期间页面输入禁用。单独关闭标题栏窗口保留后台 Agent；Agent 托盘 Exit 会结束 App 与 Agent | 数据位置替换等程序化关闭使用同一准备入口；H12 原生记录已核实标题栏 Close 保留 Agent、托盘 Exit 结束两进程 | `MainWindow.xaml.cs`、`App.xaml.cs`、`SettingsPage.xaml.cs` |
| 工作区启动遮罩 | 在首屏偏好读取和 Agent 工作区恢复期间显示状态消息，防止过早操作半初始化页面 | 有效只读缓存预览完成时可显示页面内容，但页面仍不可命中输入，直到 Agent 恢复结束 | `MainWindow.xaml.cs` |
| 本机真实编辑 `LocalRealOperationsSwitch` | 切换本机存储页面的真实编辑模式 | 管理员能力影响帮助文本；开关本身保持启用，真实操作仍受执行规则约束；工作区恢复期间页面交互被阻断 | `MainWindow.xaml`、`MainWindow.xaml.cs` |
| 全局通知卡 `NotificationCard` | 点击普通通知可关闭；点击错误通知打开可读错误消息；Enter/Space 同样触发 | 单层 InfoBar 本体使用不透明主题背景；卡片退出期间不可交互，卡片无独立按钮 | `MainWindow.xaml`、`NotificationCard.xaml(.cs)` |
| 通知列表 `GlobalNotificationStack` | 承载当前可见的动态通知卡 | 每次发布的可显示通知生成一张卡；卡片数量与自动退出由通知服务控制 | `MainWindow.xaml`、`MainWindow.xaml.cs` |

## 管理页

| 控件 | 用途 | 帮助／禁用条件 | 源码 |
| --- | --- | --- | --- |
| 拓扑树节点 `TopologyNodeControl` | 浏览池、层、磁盘、分区等层级；节点行可选择，右键打开当前对象命令菜单；符合条件的节点可拖放调整结构顺序 | 展开按钮仅在有子项时显示并启用；悬停说明展开／折叠，无子项时说明原因。拖放资格由节点角色决定 | `MainPage.xaml`、`TopologyNodeControl.xaml(.cs)`、`TopologyNodeViewModel.cs` |
| 子节点呈现 `FlowChildren`／`WeightedChildren`／`StackChildren` | 按对应拓扑布局显示子节点列表 | 子项仍使用可选择、展开和拖放的 `TopologyNodeControl`；这些 ItemsControl 本身只承载布局与数据 | `TopologyNodeControl.xaml(.cs)` |
| 工作区 `GridSplitter` | 调整 Manage 拓扑区与属性/命令工作区的上下比例 | 原生可拖动分隔条；自动化名称为“Resize workspaces” | `MainPage.xaml` |
| 展开／折叠按钮 `ExpandButton` | 展开或收起当前节点 | 无子项时禁用；图标随状态变化 | `TopologyNodeControl.xaml(.cs)` |
| `TopologyScrollViewer` 与 `TopologySystemsControl` | 滚动查看拓扑；承载按系统生成的根节点 | 无额外禁用状态；内部节点分别响应选择、展开、拖放及右键 | `MainPage.xaml` |
| `VerticalCategoryList` | 选择系统、池、层、磁盘、分区属性类别 | 单选；类别按当前系统填充 | `MainPage.xaml`、`WorkspaceViewModel.cs` |
| Manage 区域 `GridSplitter` | 调整属性表与命令区域的列宽 | 原生可拖动分隔条，自动化名称为“Resize table and command areas” | `MainPage.xaml` |
| `TableOuterScrollViewer` | 滚动属性表区域的纵向内容 | 外层只负责纵向滚动，内表格另有水平滚动 | `MainPage.xaml` |
| `TableScrollViewer` | 水平滚动比较表各对象列 | 分列表格宽于可视区域时可滚动 | `MainPage.xaml` |
| `ComparisonTableGrid` 单元格 | 点击选择对应对象/属性组；悬停高亮；右键可复制当前值、复制组数据或查看原始字段 | 复制菜单只在当前选中列对象的属性组打开 | `MainPage.xaml`、`MainPage.xaml.cs`、`PropertyTableContextMenu.cs` |
| `CommandButtonsPanel` 下区按钮 | 按当前对象类型显示精简操作；拓扑节点右键菜单保持原有完整命令集 | 池、层和分区编辑入口常亮；磁盘编辑需有可定位的目标页。目标页限制真实/模拟编辑；Windows 专用操作仍按对象条件禁用并说明原因 | `MainPage.xaml`、`MainPage.xaml.cs`、`ManageCommandProjector.cs` |
| System 管理命令 | 本机刷新、转换为模拟、导入、导出、删除模拟系统 | 刷新/转换只对本机系统启用；删除只对模拟系统启用；导入/导出由投影器持续提供 | `MainPage.xaml.cs`、`ManageCommandProjector.cs` |
| Pool/Tier 下区按钮 | 池仅“编辑存储池”，层仅“编辑存储层” | 两个按钮始终可点击并定位到结构编辑页；当前系统的只读/可编辑边界由目标页执行 | `MainPage.xaml.cs` |
| Disk 下区按钮 | 仅“编辑磁盘”“系统属性对话框” | 对应 OS 磁盘在分区编辑页拓扑中可见时定位到分区页；入池物理盘等在该页隐藏的对象若有可解析存储池，定位到结构页；两者都没有时禁用并说明原因。系统属性按当前对象与本机条件决定可用性 | `MainPage.xaml.cs`、`ManageCommandProjector.cs` |
| Partition/Volume 下区按钮 | 仅“打开资源管理器”“编辑分区”“优化驱动器”“系统属性对话框” | 编辑分区始终可点击并定位到分区编辑页；其它 Windows 专用按钮按本机对象及系统条件决定可用性 | `MainPage.xaml.cs`、`ManageCommandProjector.cs` |
| 拓扑节点右键菜单 | 保留投影器提供的创建、重命名、初始化、格式化、删除等上下文命令 | 与精简的下区按钮不同；原有模拟/本机身份和对象角色禁用规则继续适用 | `MainPage.xaml.cs`、`ManageCommandProjector.cs` |
| Manage 分类未投影命令 | `MainPage` 保留层重命名、创建及池优化命令映射 | 当前 `ManageCommandProjector` 未返回这些命令，因此当前界面不生成对应按钮；不要把已有映射误当作可见功能 | `MainPage.xaml.cs`、`ManageCommandProjector.cs` |
| 属性表上下文菜单 | 复制当前单元格值、复制属性组、查看原始字段 | 仅在已选列属性组的右键菜单中出现；原始字段对话框提供只读可选文本和关闭按钮 | `PropertyTableContextMenu.cs` |

## 硬件页

| 控件 | 用途 | 帮助／禁用条件 | 源码 |
| --- | --- | --- | --- |
| 刷新硬件 `refresh` | 刷新本机只读硬件信息；运行中变为取消 | 本机非扫描期间可刷新；刷新中可取消；模拟系统或本机扫描中禁用，并提供原因帮助 | `HardwarePage.cs` |
| 导出报告 `export` | 导出当前本机或模拟系统的硬件报告 | 刷新期间禁用；有自定义帮助及禁用原因 | `HardwarePage.cs` |
| 硬件报告分组表格 | 点击组标题选择并居中对应列；悬停高亮；右键复制值/组/原始字段 | 每个报告分组及对象列动态生成；原始字段不存在时展示不可用说明 | `HardwarePage.cs`、`PropertyTableContextMenu.cs` |

## 结构页

| 控件 | 用途 | 帮助／禁用条件 | 源码 |
| --- | --- | --- | --- |
| `TopologyScrollViewer`／结构拓扑 `TopologyControl` | 滚动、选择池/层/磁盘等结构对象；节点支持展开、拖放和右键交互；结构表单可编辑模拟草稿，也可通过原入口准备本阶段支持的真实草稿 | 真实草稿仍限于页面与 Agent 支持的准确目标和布局；拖放只改变草稿关系，Apply 前不写盘 | `StorageStructurePage.xaml`、`TopologyNodeControl.xaml(.cs)` |
| 结构页内容根 `Grid` | 为拓扑、操作区与属性区整体提供页面外边距 | 四周 24 DIP；内卡片 Padding 不代替页面外边距 | `StorageStructurePage.xaml` |
| `UndoButton` | 撤销最近一项尚未应用的模拟或真实草稿修改 | 撤销栈为空或真实草稿正在执行/等待核对时禁用；已 Verified 的真实步骤不进入 Undo 历史 | `StorageStructurePage.xaml(.cs)` |
| `RedoButton` | 恢复最近撤销的模拟或真实草稿修改 | 重做栈为空或真实草稿正在执行/等待核对时禁用 | `StorageStructurePage.xaml(.cs)` |
| `DiscardAllButton` | 放弃当前所有未应用的模拟或真实草稿修改 | 无未应用修改，或真实 Apply、改名、操作忙时禁用；不会撤销已确认并执行的真实步骤 | `StorageStructurePage.xaml(.cs)` |
| `ApplyAllButton` | 提交模拟待处理计划，或将真实草稿交给准确计划准备、冻结确认与 Agent 执行 | 需要非空可应用草稿、无阻止步骤/构建错误/非法容量及未知结果；真实目标还受真实模式、fresh 来源和执行门禁约束；禁用时显示原因 | `StorageStructurePage.xaml(.cs)`、`EditorPageBase.cs` |
| `CreatePoolButton` | 在同一编辑草稿中创建新池；真实新池默认使用 Ordinary/MAX 意图 | 当前已有池草稿时禁用；自动创建虚拟磁盘由独立 `AutoCreateVirtualDiskSwitch` 偏好控制 | `StorageStructurePage.xaml(.cs)` |
| `DissolveButton` | 将所选模拟池或受支持的真实池解散意图加入草稿；可在同一草稿中明确创建替代池并重新分配成员 | 真实池要求准确单物理成员、最多一个虚拟磁盘及受支持的 HDD 布局；Apply 按准确冻结计划执行，不提供独立重建向导 | `StorageStructurePage.xaml(.cs)` |
| `RetireButton` | 将所选模拟池成员盘标记为已退役 | 当前仅支持模拟池中的在线、非启动/系统成员盘；真实模式禁用并说明原因；已退役时禁用 | `StorageStructurePage.xaml(.cs)` |
| `HotSpareButton` | 将所选模拟池成员盘标记为热备 | 当前仅支持模拟池中的在线、非启动/系统成员盘；真实模式禁用并说明原因；已为热备时禁用 | `StorageStructurePage.xaml(.cs)` |
| `CreateVdiskButton` | 为所选池加入虚拟磁盘创建草稿；独立自动分区偏好开启时按钮显示“创建虚拟磁盘和分区”，关闭时显示“创建虚拟磁盘” | 需符合当前模拟或真实布局条件且没有现有虚拟磁盘；真实模式允许受支持的准确空池创建首个虚拟磁盘。按钮生成草稿，仍由 Apply 执行 | `StorageStructurePage.xaml(.cs)` |
| `DeleteVdiskButton` | 将所选虚拟磁盘及其准确关联后代加入删除草稿 | 需可编辑池并有可删除的目标；真实模式只接受页面支持的准确虚拟磁盘，不会因此解散池或自动重建 | `StorageStructurePage.xaml(.cs)` |
| `ShowHotSpareSwitch` | 显示或隐藏模拟热备磁盘 | 当前仅模拟系统可用；真实模式禁用 | `StorageStructurePage.xaml(.cs)` |
| `ShowRetiredSwitch` | 显示或隐藏模拟退役磁盘 | 当前仅模拟系统可用；真实模式禁用 | `StorageStructurePage.xaml(.cs)` |
| `PoolFormGrid` 动态属性表单 | 编辑选中对象的草稿字段；真实池使用 Ordinary/单 HDD、名称、明确 GiB 或 MAX、自动 VD/分区、MSR、文件系统、簇、卷标及盘符等原表单意图 | 文本框、组合框、数值框按对象生成；自动 VD 与自动分区是独立已保存偏好，切换偏好不会改写现存草稿。真实不支持的原地布局/多成员变更会被阻止；真实 MAX 按本次 Agent provider 创建范围冻结成精确 bytes | `StorageStructurePage.xaml(.cs)` |
| 结构页左右 `GridSplitter` | 调整拓扑／操作区与右侧属性栏宽度 | 右栏初始 320 DIP；可向两侧拖动，属性表单弹性列与换行标签适应栏宽；左侧操作区允许横向滚动 | `StorageStructurePage.xaml` |
| `SavePoolPropertiesButton` | 将所选池当前表单改动保存到模拟或真实草稿 | 需可编辑的非始祖池且表单有改动；真实字段受对象支持范围、离线/冲突及 busy 门禁限制；此按钮不直接写盘 | `StorageStructurePage.xaml(.cs)` |
| SSD/HDD/SCM 层配置输入框 | 设置容量、精简/固定配置、复原能力、数据副本、容错数、磁盘数、列数及交错 | 动态使用 `ComboBox`、`TextBox`、`NumberBox`；数值按各字段边界归一化；无自定义帮助时字段标签说明用途 | `StorageStructurePage.xaml.cs` |
| `MaximumButton` 层容量最大值按钮 | 将 SSD/HDD/SCM 模拟层容量填为规划的对齐上限 | 每个容量输入组生成一个；帮助说明该规划值不保证 Windows 实际可用容量；它不同于真实新建虚拟磁盘的 provider MAX | `StorageStructurePage.xaml.cs` |
| 动态属性行重置按钮 | 将已改动字段恢复推荐值 | 仅字段不同于已提交值时显示；每项提供推荐值帮助 | `StorageStructurePage.xaml.cs` |
| `AutoCreateVirtualDiskSwitch`／`AutoCreatePartitionSwitch` 与虚拟磁盘布局选项 | 分别保存自动建 VD、自动建分区偏好；新建 VD 意图可带分区样式、文件系统/簇大小、容量、MSR、卷标和盘符 | 两项偏好独立；更改偏好不会插入/删除已有草稿对象。真实新建容量的 MAX 由该次 Agent provider 精确范围解析；不开放现有 VD/层 MAX 扩展或任意原地布局改写 | `StorageStructurePage.xaml(.cs)` |
| `PendingActionsPanel` 与步骤 `Expander` | 列出当前待处理计划；展开查看步骤详情及只读命令预览 | 空计划显示空状态；步骤命令预览为空时复制命令按钮禁用并说明未通过预检查 | `StorageStructurePage.xaml(.cs)` |
| 动态“复制命令预览”按钮 | 复制当前步骤预览文本 | 命令列表为空时禁用；复制不会执行命令 | `StorageStructurePage.xaml.cs` |
| `RealTopologyOverlay`／`RealTopologyProgress` | 真实结构写入及写后刷新阶段遮罩图形区域并显示当前阶段；共享 busy 状态会刷新按钮禁用状态 | 准备阶段可显示 footer 进度；遮罩依 Workspace 图形遮挡状态显示，刷新尾段结束后释放 | `StorageStructurePage.xaml(.cs)`、`EditorPageBase.cs` |

## 分区页

| 控件 | 用途 | 帮助／禁用条件 | 源码 |
| --- | --- | --- | --- |
| 分区页内容根 `Grid` | 为拓扑、操作区与属性区整体提供页面外边距 | 四周 24 DIP；内卡片 Padding 不代替页面外边距 | `DiskPartitionPage.xaml` |
| 磁盘与分区拓扑 `TopologyControl` | 选择磁盘、分区或可见的未分配空间；展开节点浏览结构 | 未分配间隙小于设置的隐藏阈值时不投影到页面；可见但对齐后不足 1 MiB 的间隙会使操作按钮显示禁用原因 | `DiskPartitionPage.xaml(.cs)`、`TopologyNodeControl.xaml(.cs)` |
| `TopologyScrollViewer` | 滚动浏览磁盘与分区拓扑 | 承载左侧结构树；节点选择更新右侧表单和操作状态 | `DiskPartitionPage.xaml` |
| 分区页左右 `GridSplitter` | 调整磁盘拓扑／操作区与右侧属性栏宽度 | 右栏初始 320 DIP；可向两侧拖动，属性表单受栏宽约束；起点和终点各以 MiB 主值及自适应单位副值分行显示 | `DiskPartitionPage.xaml` |
| `PartitionFormGrid` | 显示磁盘/分区身份字段和右侧编辑表单 | 当前选择决定哪些字段和按钮启用；表单放在纵向可滚动区域 | `DiskPartitionPage.xaml(.cs)` |
| `StartOffsetValue`／`EndOffsetValue` 与自适应值 | 起点、终点各自保留一个准确偏移；每个字段分两行显示千分位 MiB 主值和自适应单位副值 | 两行均由原始偏移 bytes 生成，不因显示取整改写几何；数值右对齐；无有效值时显示未知标记 | `DiskPartitionPage.xaml(.cs)` |
| `DiskOnlineStateButton` 联机/脱机状态按钮 | 单一按钮按当前准确状态显示“联机”“脱机”或“状态未知”并执行相应状态变更 | 需选中磁盘本身、目标非启动/系统/分页文件/崩溃转储盘且操作不忙；真实模式必须从该磁盘准确 `SourceFacts` 读到 Returned 的 `IsOffline` 布尔值，缺失或未知时禁用 | `DiskPartitionPage.xaml(.cs)` |
| `DiskPartitionStyleButton` 初始化/GPT 状态按钮 | RAW 显示初始化；MBR 显示“转换为 GPT”；GPT 显示已初始化 | 需选中在线准确磁盘；模拟 MBR 可按模拟规则转换，真实 MBR 转换 C03 显示但禁用并拒绝；真实 RAW 初始化使用 MSR 偏好并提交相应准确配置段 | `DiskPartitionPage.xaml(.cs)` |
| `ClearDiskButton` 清空至 RAW | 将所选磁盘及关联分区/卷清至 RAW，不初始化或格式化 | 模拟 GPT/MBR 按原规则可用；真实仅支持 GPT 普通数据/MSR 分区，或本次完整来源事实证明零分区的 GPT，真实 MBR 禁用并说明原因。还要求准确在线且非受保护目标；真实操作通过 Agent 冻结计划确认，模拟操作仅改模拟事实 | `DiskPartitionPage.xaml(.cs)`、`EditorPageBase.cs` |
| `DeletePartitionButton` | 删除选中的分区 | 模拟系统按模拟规则；真实仅允许准确、安全且可支持的分区目标；启动/系统分区及操作忙时禁用 | `DiskPartitionPage.xaml(.cs)` |
| `ExtendButton` | 输入分区扩展量和目标总容量并提交扩展 | 模拟按连续空间及规则校验；真实限普通 GPT NTFS、ReFS 扩展或 RAW 数据分区，由 Agent 点击时读取实时支持范围；启动/系统、脱机、未知状态或操作忙时禁用 | `DiskPartitionPage.xaml(.cs)` |
| `ShrinkButton` | 输入分区压缩量和目标总容量并提交压缩 | 模拟按文件系统、使用量和几何规则校验；真实限受支持的普通 GPT NTFS/RAW 数据分区，ReFS 压缩禁用，范围由 Agent 实时读取 | `DiskPartitionPage.xaml(.cs)` |
| `OpenExplorerButton` | 打开选中本机卷 | 需本机分区、磁盘在线、盘符可用且对应目录存在；模拟系统禁用 | `DiskPartitionPage.xaml(.cs)` |
| `PartitionTypeBox` | 为选中的 GPT 未分配空间选择分区类型 | 仅可创建且几何有效的 GPT 空隙中启用；真实写入仍需 fresh Agent 准备和冻结确认 | `DiskPartitionPage.xaml(.cs)` |
| `DriveLetterBox` | 设置已有卷或新分区的盘符 | 仅适用的数据卷/新建分区可用；EFI/MSR/恢复分区不能分配；真实更改经 Agent 准备与确认 | `DiskPartitionPage.xaml(.cs)` |
| `VolumeLabelBox` | 设置现有卷或新分区的卷标 | 可改写的数据卷/新建分区可用；MSR 不设卷标；现有卷输入后按 Enter 提交，真实变更经 Agent 准备与确认 | `DiskPartitionPage.xaml(.cs)` |
| `ResetSizeButton` | 将新建容量恢复为当前分区类型的推荐值 | 容量偏离推荐值时显示；帮助指出恢复推荐 MiB 容量 | `DiskPartitionPage.xaml(.cs)` |
| `SizeBox` 与固定 `MiB` 后缀 | 输入新分区 MiB 容量；已有分区时显示当前 MiB 数值 | 创建时仅接受正 MiB 整数并受所选空隙的 1 MiB 对齐上限限制；已有分区时只读，扩展/压缩使用单独对话框 | `DiskPartitionPage.xaml`、`DiskPartitionPage.xaml.cs` |
| `SizeAdaptiveValue` | 显示 `SizeBox` 当前容量对应的自适应容量单位 | 只读第二行；无有效整数或尚无可计算容量时显示说明或破折号 | `DiskPartitionPage.xaml(.cs)` |
| `MaximumSizeButton` | 将创建容量填为当前所选 GPT 空隙所能容纳的最大对齐值（MAX） | 几何有效时启用；真实值仅是当前页面候选，最终以 Agent fresh 计划为准；使用 U+E74E 图标 | `DiskPartitionPage.xaml(.cs)` |
| `ResetFileSystemButton` | 恢复该分区类型推荐的文件系统 | 文件系统值偏离推荐项时显示 | `DiskPartitionPage.xaml(.cs)` |
| `FileSystemBox` | 选择可格式化的数据分区或新分区文件系统 | 仅数据分区/新建分区可用；MSR、启动/系统分区受限。真实默认 NTFS、64 KiB 簇、快速格式化；其它组合仅在当前 provider 能力和准确计划允许时可用 | `DiskPartitionPage.xaml(.cs)` |
| `ResetClusterButton` | 恢复推荐的分配单元大小 | 分配单元值偏离推荐项时显示 | `DiskPartitionPage.xaml(.cs)` |
| `ClusterBox` | 选择格式化时的分配单元大小 | 仅格式化选项可用时启用；真实选择随冻结格式化计划提交；帮助说明当前 64 KiB NTFS 已测建议的边界 | `DiskPartitionPage.xaml(.cs)` |
| `ResetQuickFormatButton` | 恢复快速格式化选项的推荐值 | 快速格式化开关偏离推荐状态时显示 | `DiskPartitionPage.xaml(.cs)` |
| `QuickFormatSwitch` | 选择格式化时的快速格式化选项 | 仅适用格式化选项可用时启用；真实角色/文件系统限制仍由计划校验 | `DiskPartitionPage.xaml(.cs)` |
| `FullFormatSwitch` | 选择格式化时的完整格式化选项 | 仅适用格式化选项可用时启用；部分真实文件系统和角色组合禁用；真实计划需 Agent 冻结确认 | `DiskPartitionPage.xaml(.cs)` |
| `PartitionActionButton` | 右侧底部单一操作按钮；选 GPT 未分配空间时新建分区，选现有可格式化分区时格式化 | 文字、图标、自动化名称、帮助和禁用原因随选择切换；模拟操作沿用模拟规则；真实操作仅接受支持的准确目标并经 Agent 冻结确认 | `DiskPartitionPage.xaml(.cs)`、`EditorPageBase.cs` |
| 扩展／压缩公式对话框 | 两行展示 `A ± B = C`；第一行使用 MiB 整数，第二行显示自适应单位 | 第一行 A 当前容量只读，B 变化量与 C 目标总量可编辑并双向联动；第二行全部只读。bytes 是唯一权威值，显示值不反向取整；真实操作点击后 fresh 查询 Agent 支持范围 | `DiskPartitionPage.xaml.cs`、`RealPartitionResizeUiRange` |
| `RealTopologyOverlay`／`RealTopologyProgress` | 真实写入及写后刷新时遮罩分区图形区并显示当前阶段 | busy 门禁覆盖准备到刷新尾段；遮罩依共享 Workspace 状态出现/释放 | `DiskPartitionPage.xaml(.cs)`、`EditorPageBase.cs` |
| `QueryRealOperationButton`／`StopRealOperationButton` | 在结构页或分区页按持久 OperationId 查询真实操作，或停止该操作的后续步骤 | 需 Agent 连接；操作身份与阶段按当前真实操作状态核对，不以关闭进度卡代替对账 | `StorageStructurePage.xaml(.cs)`、`DiskPartitionPage.xaml(.cs)`、`EditorPageBase.cs` |

## 监控页

| 控件 | 用途 | 帮助／禁用条件 | 源码 |
| --- | --- | --- | --- |
| `ActivityGraph` | 显示磁盘读写活动曲线和性能刻度 | 图表为可视化显示控件；没有采样值时由监控状态区提供状态说明 | `MonitorPage.xaml`、`DiskActivityGraphControl.xaml(.cs)` |
| `TableScroll`、`TableRowsScroll` 与 `DiskRows` | 滚动查看逐磁盘颜色、名称、池、卷、介质、容量、活动及读写速率 | 每个磁盘行动态生成；表头和行同处横向滚动区，行列表保留纵向滚动 | `MonitorPage.xaml` |
| 监控行 `ShowInGraph` 复选框 | 切换对应磁盘曲线是否绘制 | 每个磁盘行一个复选框；按绑定状态切换 | `MonitorPage.xaml` |
| 监控行颜色色块按钮 | 打开颜色选择器；点色块或输入颜色以修改对应曲线色彩 | 每行重复；颜色弹层色块按调色板动态生成 | `MonitorPage.xaml(.cs)` |
| 曲线颜色 `input` 文本框 | 手动输入十六进制 `#RRGGBB` 或 RGB 分量 | Enter 或失焦时校验并应用有效颜色；无效输入不更新曲线颜色，颜色弹层关闭 | `MonitorPage.xaml.cs` |
| `ContinuousMonitoringSwitch` | 开始或停止后台持续采样 | 有悬停说明；停止采样不代表已有记录缺口已恢复 | `MonitorPage.xaml(.cs)` |
| `RateOptions` | 选择图表采样频率 | 有悬停说明；选项来自 `MonitoringService.RateOptions` | `MonitorPage.xaml(.cs)` |
| `AutoColorsButton` | 为监控曲线重新分配可区分颜色 | 有悬停说明；无动态禁用条件 | `MonitorPage.xaml(.cs)` |
| `EventsButton` | 查看已采集的存储健康事件 | 有悬停说明；无动态禁用条件 | `MonitorPage.xaml(.cs)` |
| `ExportButton` | 导出当前活动数据库的监控数据 | 有导出范围帮助；无动态禁用条件 | `MonitorPage.xaml(.cs)` |
| 图表／表格 `GridSplitter` | 调整图表与磁盘表格的垂直空间 | 拖动调整布局；自动化名称为“Resize monitor areas” | `MonitorPage.xaml` |
| 表格／工具栏 `GridSplitter` | 调整磁盘表格与底部工具栏的垂直空间 | 8 DIP 拖动槽；窄窗工具栏可拆行，按钮仍可触达 | `MonitorPage.xaml` |

## 设置页与欢迎页

| 控件 | 用途 | 帮助／禁用条件 | 源码 |
| --- | --- | --- | --- |
| `ThemeOptions` | 选择系统、浅色或深色主题 | 有悬停说明；保存失败时恢复当前值 | `SettingsPage.xaml(.cs)` |
| `AccentOptions` | 选择应用强调色 | 有悬停说明；保存失败时恢复当前值 | `SettingsPage.xaml(.cs)` |
| `LanguageOptions` | 选择系统、中文或英文界面语言 | 有悬停说明；选择后立即切换界面语言 | `SettingsPage.xaml(.cs)` |
| `WelcomeButton` | 打开欢迎窗口 | 有悬停帮助；无动态禁用条件 | `SettingsPage.xaml(.cs)` |
| `StartupAgentSwitch` | 控制是否在用户登录时启动 WinPool Agent | 有悬停说明 | `SettingsPage.xaml(.cs)` |
| `DeveloperModeSwitch` | 显示硬件、测试、开发页面入口 | 有悬停说明；切换后刷新导航项 | `SettingsPage.xaml(.cs)` |
| `SettingsExecutionModeSwitch` | 设置全局真实编辑模式 | 有说明；开关保持启用，权限不足时说明需要管理员，真实操作仍受执行规则约束 | `SettingsPage.xaml(.cs)` |
| `MsrSwitch` | 控制初始化及自动布局是否创建 Microsoft 保留分区 | 有悬停说明；作为软件偏好用于模拟流程及受支持的真实 RAW 初始化/自动布局 | `SettingsPage.xaml(.cs)` |
| `PartitionGapBox` | 用 MiB 输入隐藏小分区缝隙的阈值；Enter 保存 | 有悬停说明；最大输入长度 4，只接受数值输入 | `SettingsPage.xaml(.cs)` |
| `DataLocationOptions` | 迁移标准/便携数据位置 | 迁移期间下拉框禁用并说明正在等待确认；切换需确认，失败时恢复 | `SettingsPage.xaml(.cs)` |
| `OpenDataLocationButton` | 在资源管理器中打开当前 WinPool 数据目录 | 32×32 DIP 行内单图标按钮，具有自动化名称和悬停帮助；路径不存在或打开失败时显示错误通知 | `SettingsPage.xaml(.cs)` |
| `ResetAllButton` | 恢复 WinPool 设置默认值 | 有悬停帮助；执行前显示确认对话框 | `SettingsPage.xaml(.cs)` |
| `SevenZipOptions` | 选择内置或自定义 7-Zip 程序 | 选择/保存及文件选择器处理期间暂时禁用，并说明原因 | `SettingsPage.xaml(.cs)` |
| `OpenSevenZipLocationButton` | 打开当前 7-Zip 程序所在目录 | 32×32 DIP 行内单图标按钮，具有自动化名称和悬停帮助；路径不可用时显示错误通知 | `SettingsPage.xaml(.cs)` |
| `WebsiteButton` | 在浏览器打开官网 | 有悬停说明；无动态禁用条件 | `SettingsPage.xaml(.cs)` |
| `UpdateButton` | 在浏览器查看更新 | 有悬停说明；无动态禁用条件 | `SettingsPage.xaml(.cs)` |
| `FeedbackButton` | 在浏览器提交反馈 | 有悬停说明；无动态禁用条件 | `SettingsPage.xaml(.cs)` |
| `AboutCommunityButton` | 在浏览器打开 WinPool QQ 交流群 | 有悬停说明；无动态禁用条件 | `SettingsPage.xaml(.cs)` |
| `CloseButton` | 关闭欢迎窗口 | 图标按钮，有上下文帮助 | `WelcomeWindow.xaml(.cs)` |
| `CycleButton` | 显示下一个欢迎内容；按钮文字随内容改变 | 图标文字普通按钮；无动态禁用条件 | `WelcomeWindow.xaml(.cs)` |
| `ConfirmButton` | 关闭欢迎窗口 | 图标文字普通按钮；无动态禁用条件 | `WelcomeWindow.xaml(.cs)` |

## 开发页与测试页

| 控件 | 用途 | 帮助／禁用条件 | 源码 |
| --- | --- | --- | --- |
| 消息列表 `MessageList` | 显示本进程通知历史；无表头，每条消息依次为时间、级别、来源、信息标题，标题列只取通知标题；双击条目打开详情 | 单行紧凑排列且文字垂直居中；悬停有底色、选中有强调色底色和左侧标记；空列表显示提示；与 `Diagnostics` 故障日志分离 | `DevelopmentPage.xaml(.cs)` |
| 开发页横向／纵向分隔条 `TopAreaColumnSplitter`／`TopBottomAreaSplitter` | 拖拽调整左上和右上宽度、上下高度 | 窄窗隐藏右上区域和横向分隔条；上下分隔条仍可拖拽 | `DevelopmentPage.xaml(.cs)` |
| 消息详情 `MessageDetailOverlay`／`MessageDetailText` | 在页面中央查看完整消息并用键盘复制 | 背景变暗；单个只读、可选文本框由不透明实色底板承托并按内容调整高度，长文本可滚动；点击文本框外关闭 | `DevelopmentPage.xaml(.cs)` |
| 测试页 | 说明完整测试工作区属于 WinPool 2.0 规划 | 当前为静态说明页，没有交互控件 | `TestPage.xaml` |

## 临时对话框与弹层

| 控件 | 用途 | 帮助／禁用条件 | 源码 |
| --- | --- | --- | --- |
| `EditorPageBase.ConfirmAsync` 确定/取消 | 显示编辑操作的风险或详情确认 | 默认焦点为取消；关闭不会确认操作 | `EditorPageBase.cs` |
| `EditorPageBase.ShowMessageAsync` 关闭 | 显示编辑结果或说明文本 | 只有关闭按钮 | `EditorPageBase.cs` |
| 模拟系统删除确认 | 确认删除当前模拟系统 | 删除按钮为主要操作，默认焦点是取消；取消不删除 | `MainPage.xaml.cs` |
| 模拟磁盘重命名输入框及确定/取消 | 输入新的模拟磁盘名称 | 名称为空或未改变时不提交；取消不保存 | `MainPage.xaml.cs` |
| 真实编辑模式风险确认 | 确认切换真实编辑；权限不足时主要按钮改为以管理员身份重启 | 取消保持原模式；启动期间窗口输入仍受工作区遮罩控制 | `MainWindow.xaml.cs` |
| 数据位置迁移确认及提交 | 首次说明迁移影响；计算计划后再次确认源、目标、文件数、大小和清单哈希；最终按钮继续或迁移并重启 | 任一取消都不提交迁移；计划确认取消后恢复 Agent | `SettingsPage.xaml.cs` |
| 恢复默认设置确认 | 确认恢复所有 WinPool 设置默认值 | 主要按钮执行，取消不重置；运行中的重复触发被忽略 | `SettingsPage.xaml.cs` |
| 设置消息关闭按钮 | 关闭普通设置操作结果对话框 | 只有关闭按钮 | `SettingsPage.xaml.cs` |
| 监控事件详情关闭按钮 | 关闭已采集事件列表 | 事件列表只读并在可滚动区域中；只有关闭按钮 | `MonitorPage.xaml.cs` |
| 属性组／原始字段详情文本与关闭按钮 | 查看原始字段文本 | 只读、可选择的 `TextBlock`；无原始字段时显示不可用说明；关闭为弹层文本按钮 | `PropertyTableContextMenu.cs` |
| 通知错误详情关闭按钮 | 查看通知中的可读错误正文 | 由错误通知点击打开；只有关闭按钮 | `MainWindow.xaml.cs` |
| 通知错误详情 `message` 文本框 | 查看完整通知错误正文 | 只读、多行、可选择文本并换行；与关闭按钮同属错误详情对话框 | `MainWindow.xaml.cs` |
| 分区扩展／压缩容量输入框与主要操作/取消 | 输入分区扩展后或压缩后的 MiB 整数目标总容量 | 实时校验目标方向、对齐、几何、可用空间和模拟文件系统；无有效结果时主要操作禁用；取消不提交 | `DiskPartitionPage.xaml.cs` |

## 当前审阅边界

- 表格核对 `src/WinPool.App` 页面 XAML、动态控件和 `ManageCommandProjector`；重复生成的对象行、命令和字段按家族列出，具体数量由当前数据决定。
- 临时对话框按触发流程列出其中的输入、确认、取消或关闭控件；系统文件选择器由 WinUI 原生对话框提供，相关触发控件列在设置页。
- 拓扑节点内的类型状态图标是信息标记，不是可操作按钮，故不列入控件表。
