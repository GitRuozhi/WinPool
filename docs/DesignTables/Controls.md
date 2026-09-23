# 交互控件说明表

状态：2026-09-23 按当前 XAML 与动态控件代码核对。相同用途的重复行按控件家族归纳；条件只列代码中已实现的规则。悬停帮助指由 `ContextHelp` 提供的帮助或禁用原因；没有特别注明时，不代表所有控件都有自定义悬停文本。按钮图形见[按钮与图标表](Buttons.html)。

## 窗口与导航

界面顶部标题栏行高 48 DIP，系统首选高度为 Tall。图标与 WinPool 标题区、交互控件之间的空白属于可拖拽区域；导航列表、活动系统选择器、真实编辑控件及系统窗口按钮接收各自输入，因此整条 48 DIP 不能任意位置拖动。交互区加载或尺寸变化后刷新 Passthrough 命中矩形，实际剩余可拖宽度随窗口和语言变化。

| 控件 | 用途 | 帮助／禁用条件 | 源码 |
| --- | --- | --- | --- |
| 标题栏导航列表 `ShellNavigationList` | 在硬件、管理、结构、分区、测试、监控、开发、设置页面间导航；选择项提供页面本地化名称和图标 | 硬件、测试、开发仅开发者模式可见；Agent 工作区恢复期间禁用，完成后启用 | `MainWindow.xaml`、`MainWindow.xaml.cs` |
| 标题栏活动系统选择器 `ActiveSystemSelector` | 显示当前选中存储系统名称，并切换本机、导入或模拟系统 | 有页面级帮助。启动时偏好读取之前隐藏；若有效的只读缓存预览先完成，则显示系统名但选择器及页面交互仍禁用；无有效预览时保持隐藏。Agent 成功完成目录/状态恢复后，存在所选系统时显示并启用；恢复失败时保持禁用，若此前显示过预览则仍可见，否则隐藏 | `MainWindow.xaml`、`MainWindow.xaml.cs` |
| 工作区启动遮罩 | 在首屏偏好读取和 Agent 工作区恢复期间显示状态消息，防止过早操作半初始化页面 | 有效只读缓存预览完成时可显示页面内容，但页面仍不可命中输入，直到 Agent 恢复结束 | `MainWindow.xaml.cs` |
| 本机真实编辑 `LocalRealOperationsSwitch` | 切换本机存储页面的真实编辑模式 | 管理员能力影响帮助文本；开关本身保持启用，真实操作仍受执行规则约束；工作区恢复期间页面交互被阻断 | `MainWindow.xaml`、`MainWindow.xaml.cs` |
| 全局通知卡 `NotificationCard` | 点击普通通知可关闭；点击错误通知打开可读错误消息；Enter/Space 同样触发 | 卡片退出期间不可交互；卡片无独立按钮 | `MainWindow.xaml`、`NotificationCard.xaml.cs` |
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
| 对比表列标题按钮 | 选择该列代表的对象并显示其属性和可用命令 | 每个比较对象重复生成；使用普通图标文字尺寸并有悬停帮助 | `MainPage.xaml.cs` |
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
| `TopologyScrollViewer`／结构拓扑 `TopologyControl` | 滚动、选择池/层/磁盘等结构对象；拓扑节点支持展开、拖放和右键交互 | 编辑表单只面向模拟系统；本机结构只读 | `StorageStructurePage.xaml`、`TopologyNodeControl.xaml(.cs)` |
| `UndoButton` | 撤销最近一项未应用修改 | 撤销栈为空时禁用并说明原因 | `StorageStructurePage.xaml(.cs)` |
| `RedoButton` | 恢复最近撤销的修改 | 重做栈为空时禁用并说明原因 | `StorageStructurePage.xaml(.cs)` |
| `DiscardAllButton` | 放弃所有未应用模拟修改 | 没有未应用修改时禁用并说明原因 | `StorageStructurePage.xaml(.cs)` |
| `ApplyAllButton` | 提交通过预检查的待处理模拟修改 | 需要模拟系统、非空可应用计划、无被阻止步骤、无构建错误、无非法容量且上次结果已知；禁用时显示对应原因 | `StorageStructurePage.xaml(.cs)` |
| `CreatePoolButton` | 创建模拟池草稿 | 仅模拟系统可用；已有池草稿时禁用 | `StorageStructurePage.xaml(.cs)` |
| `DissolveButton` | 解散所选普通模拟池 | 仅模拟系统中可编辑的非始祖、非离线池可用 | `StorageStructurePage.xaml(.cs)` |
| `RetireButton` | 将所选池成员盘标记为已退役 | 需普通模拟池中的在线、非启动/系统成员盘；已退役时禁用 | `StorageStructurePage.xaml(.cs)` |
| `HotSpareButton` | 将所选池成员盘标记为热备 | 需普通模拟池中的在线、非启动/系统成员盘；已为热备时禁用 | `StorageStructurePage.xaml(.cs)` |
| `CreateVdiskButton` | 为所选池创建一个虚拟磁盘和分区 | 需可编辑池、至少一块数据盘，且池中没有现有虚拟磁盘 | `StorageStructurePage.xaml(.cs)` |
| `DeleteVdiskButton` | 删除所选池现有虚拟磁盘和分区 | 需可编辑池、有可删除虚拟磁盘且其在线 | `StorageStructurePage.xaml(.cs)` |
| `ShowHotSpareSwitch` | 显示或隐藏热备磁盘 | 模拟系统可用；本机存储只读并禁用 | `StorageStructurePage.xaml(.cs)` |
| `ShowRetiredSwitch` | 显示或隐藏退役磁盘 | 模拟系统可用；本机存储只读并禁用 | `StorageStructurePage.xaml(.cs)` |
| `PoolFormGrid` 动态属性表单 | 编辑选中池、层、虚拟磁盘和分区属性 | 文本框、组合框、数值框按所选对象生成；要求可编辑模拟池，离线盘、超出支持对象数或未知提交结果会限制编辑 | `StorageStructurePage.xaml(.cs)` |
| 结构页左右 `GridSplitter` | 调整拓扑／操作区与右侧属性栏宽度 | 右栏初始 320 DIP；可向两侧拖动，窄栏属性和左侧操作各自允许横向滚动 | `StorageStructurePage.xaml` |
| `SavePoolPropertiesButton` | 保存所选池当前属性草稿到待处理修改 | 需模拟系统、普通非草稿池、表单有改动且池在线；禁用时显示原因 | `StorageStructurePage.xaml(.cs)` |
| SSD/HDD/SCM 层配置输入框 | 设置容量、精简/固定配置、复原能力、数据副本、容错数、磁盘数、列数及交错 | 动态使用 `ComboBox`、`TextBox`、`NumberBox`；数值按各字段边界归一化；无自定义帮助时字段标签说明用途 | `StorageStructurePage.xaml.cs` |
| `MaximumButton` 层容量最大值按钮 | 将 SSD/HDD/SCM 容量填为当前规划的对齐上限 | 每个容量输入组生成一个；只在容量输入启用时可用；帮助说明该上限不保证 Windows 实际可用容量 | `StorageStructurePage.xaml.cs` |
| 动态属性行重置按钮 | 将已改动字段恢复推荐值 | 仅字段不同于已提交值时显示；每项提供推荐值帮助 | `StorageStructurePage.xaml.cs` |
| 虚拟磁盘／分区选项 | 自动创建虚拟磁盘或分区、选择分区样式/文件系统/簇大小并填写容量 | 动态表单按池/虚拟磁盘情况生成；不可编辑状态、非法容量、已超出支持数量时禁用关联字段 | `StorageStructurePage.xaml.cs` |
| `PendingActionsPanel` 与步骤 `Expander` | 列出当前待处理计划；展开查看步骤详情及只读命令预览 | 空计划显示空状态；步骤命令预览为空时复制命令按钮禁用并说明未通过预检查 | `StorageStructurePage.xaml(.cs)` |
| 动态“复制命令预览”按钮 | 复制当前步骤预览文本 | 命令列表为空时禁用；复制不会执行命令 | `StorageStructurePage.xaml.cs` |

## 分区页

| 控件 | 用途 | 帮助／禁用条件 | 源码 |
| --- | --- | --- | --- |
| 磁盘与分区拓扑 `TopologyControl` | 选择磁盘、分区或可见的未分配空间；展开节点浏览结构 | 未分配间隙小于设置的隐藏阈值时不投影到页面；可见但对齐后不足 1 MiB 的间隙会使操作按钮显示禁用原因 | `DiskPartitionPage.xaml(.cs)`、`TopologyNodeControl.xaml(.cs)` |
| `TopologyScrollViewer` | 滚动浏览磁盘与分区拓扑 | 承载左侧结构树；节点选择更新右侧表单和操作状态 | `DiskPartitionPage.xaml` |
| 分区页左右 `GridSplitter` | 调整磁盘拓扑／操作区与右侧属性栏宽度 | 右栏初始 320 DIP；可向两侧拖动，长 MiB 数值保留栏内横向滚动 | `DiskPartitionPage.xaml` |
| `PartitionFormGrid` | 显示磁盘/分区身份字段和右侧编辑表单 | 当前选择决定哪些字段和按钮启用；表单放在纵向可滚动区域 | `DiskPartitionPage.xaml(.cs)` |
| `OnlineButton` | 联机选中的模拟磁盘 | 仅选中模拟磁盘本身且磁盘当前脱机时启用；帮助说明只作用于模拟磁盘 | `DiskPartitionPage.xaml(.cs)` |
| `OfflineButton` | 脱机选中的模拟磁盘 | 仅选中在线、非启动且非系统模拟磁盘时启用 | `DiskPartitionPage.xaml(.cs)` |
| `InitializeButton` | 初始化选中的模拟磁盘 | 仅在线 RAW 且非启动/系统的模拟磁盘本身可用；帮助说明不会修改本机磁盘 | `DiskPartitionPage.xaml(.cs)` |
| `ConvertGptButton` | 将选中的模拟 MBR 磁盘转换为 GPT | 仅在线、非启动/系统 MBR 模拟磁盘本身可用 | `DiskPartitionPage.xaml(.cs)` |
| `DeletePartitionButton` | 删除选中的模拟分区 | 仅在线模拟系统中满足删除规则的分区可用；启动/系统分区受保护 | `DiskPartitionPage.xaml(.cs)` |
| `ExtendButton` | 输入并应用模拟分区扩展后的目标总容量 | 需选中可扩展的普通模拟数据分区，并有合适的右侧连续空间；禁用帮助给出规则原因 | `DiskPartitionPage.xaml(.cs)` |
| `ShrinkButton` | 输入并应用模拟分区压缩后的目标总容量 | 需选中支持压缩的普通模拟数据分区；仅 NTFS 或未格式化 RAW 支持，受几何/已用空间限制；禁用帮助给出原因 | `DiskPartitionPage.xaml(.cs)` |
| `OpenExplorerButton` | 打开选中本机卷 | 需本机分区、磁盘在线、盘符可用且对应目录存在；模拟系统禁用 | `DiskPartitionPage.xaml(.cs)` |
| `PartitionTypeBox` | 为所选 GPT 未分配空间选择固定分区类型 | 只在可编辑的模拟 GPT 未分配空间中启用 | `DiskPartitionPage.xaml(.cs)` |
| `DriveLetterBox` | 设置模拟卷盘符 | 仅适用的模拟卷或可分配盘符的新建分区可用；EFI/MSR/恢复分区不可分配 | `DiskPartitionPage.xaml(.cs)` |
| `VolumeLabelBox` | 设置模拟卷标 | 仅有可改写卷标的模拟卷或新建分区可用；MSR 不设卷标；输入后按 Enter 提交 | `DiskPartitionPage.xaml(.cs)` |
| `ResetSizeButton` | 将新建容量恢复为当前分区类型的推荐值 | 容量偏离推荐值时显示；帮助指出恢复推荐 MiB 容量 | `DiskPartitionPage.xaml(.cs)` |
| `SizeBox` 与固定 `MiB` 后缀 | 输入新分区 MiB 容量；已有分区时显示当前 MiB 数值 | 创建时仅接受正 MiB 整数并受所选空隙的 1 MiB 对齐上限限制；已有分区时只读，扩展/压缩使用单独对话框 | `DiskPartitionPage.xaml`、`DiskPartitionPage.xaml.cs` |
| `SizeAdaptiveValue` | 显示 `SizeBox` 当前容量对应的自适应容量单位 | 只读第二行；无有效整数或尚无可计算容量时显示说明或破折号 | `DiskPartitionPage.xaml(.cs)` |
| `MaximumSizeButton` | 将创建容量填为当前空隙所能容纳的最大对齐值（MAX） | 只有模拟 GPT 可见间隙的几何可创建时启用；对齐后余量不足 1 MiB 时给出原因；使用 U+E74E 图标 | `DiskPartitionPage.xaml(.cs)` |
| `ResetFileSystemButton` | 恢复该分区类型推荐的文件系统 | 文件系统值偏离推荐项时显示 | `DiskPartitionPage.xaml(.cs)` |
| `FileSystemBox` | 选择模拟格式化文件系统 | 仅可格式化的普通模拟数据分区或新建分区可用；受 MSR、启动/系统分区保护 | `DiskPartitionPage.xaml(.cs)` |
| `ResetClusterButton` | 恢复推荐的分配单元大小 | 分配单元值偏离推荐项时显示 | `DiskPartitionPage.xaml(.cs)` |
| `ClusterBox` | 选择模拟分配单元大小 | 仅格式化选项可用时启用；帮助说明当前 64 KiB NTFS 已测建议的边界 | `DiskPartitionPage.xaml(.cs)` |
| `ResetQuickFormatButton` | 恢复快速格式化选项的推荐值 | 快速格式化开关偏离推荐状态时显示 | `DiskPartitionPage.xaml(.cs)` |
| `QuickFormatSwitch` | 选择模拟快速格式化选项 | 仅格式化选项可用时启用；悬停帮助说明模拟行为 | `DiskPartitionPage.xaml(.cs)` |
| `FullFormatSwitch` | 选择模拟完整格式化选项 | 仅格式化选项可用时启用；悬停帮助明确不会扫描真实介质 | `DiskPartitionPage.xaml(.cs)` |
| `PartitionActionButton` | 右侧底部唯一操作按钮；选未分配间隙时新建分区，选现有分区时格式化分区 | 文字、图标、自动化名称、用途帮助和禁用原因随选择切换；新建要求有效对齐几何及整数 MiB 容量，格式化要求可格式化的普通模拟数据分区；提交前按流程确认 | `DiskPartitionPage.xaml(.cs)` |
| 扩展／压缩目标对话框输入框 | 输入目标总容量的 MiB 整数 | 扩展目标需大于当前大小；压缩目标需较小且满足建模几何、空闲空间与文件系统条件；不通过时主操作被阻止 | `DiskPartitionPage.xaml.cs` |

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
| `MsrSwitch` | 控制模拟磁盘初始化是否创建 Microsoft 保留分区 | 有悬停说明；仅影响模拟初始化 | `SettingsPage.xaml(.cs)` |
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
| 消息列表 `MessageList` | 显示本进程通知历史；表头和每条消息依次为时间、级别、来源、信息标题，标题列只取通知标题；双击条目打开详情 | 单行紧凑排列且文字垂直居中；悬停有底色、选中有强调色底色和左侧标记；空列表显示提示；与 `Diagnostics` 故障日志分离 | `DevelopmentPage.xaml(.cs)` |
| 开发页横向／纵向分隔条 `TopAreaColumnSplitter`／`TopBottomAreaSplitter` | 拖拽调整左上和右上宽度、上下高度 | 窄窗隐藏右上区域和横向分隔条；上下分隔条仍可拖拽 | `DevelopmentPage.xaml(.cs)` |
| 消息详情 `MessageDetailOverlay`／`MessageDetailText` | 在页面中央查看完整消息并用键盘复制 | 背景变暗；单个只读、可选文本框使用不透明主题背景并按内容调整高度，长文本可滚动；点击文本框外关闭 | `DevelopmentPage.xaml(.cs)` |
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
