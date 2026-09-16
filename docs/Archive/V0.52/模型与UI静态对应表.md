# 模型与 UI 静态对应表（人工检查用）

日期：2026-09-15。记录时本地代码基线：2216f26。

本文件是一份独立文字快照，只供人工检查。无数据库连接、脚本、查询、代码链接或自动更新机制，不被软件读取，不作为产品规范或开发计划。字段名称只是文字说明，不包含本机设备数据。代码后续变化不会自动改变本表。

范围：统一对象结构、存储业务投影、管理页详情，以及硬件页和编辑页的对应原则。不是所有页面全部控件的穷举清单。UI 列使用中文含义，括号中保留属性键以便辨认；具体译文可能不同。“未列出”仅指该管理详情区，没有断言其他页面也不显示。

## 1. 先区分三个层次

| 层次 | 保存／生成什么 | 与 UI 的关系 |
| --- | --- | --- |
| 来源事实 | 对象、属性值、读取状态、来源、时间、关系 | 保留观察依据，不是直接铺到所有控件 |
| 统一对象 WinPoolSystem / WinPoolObject | 组合已关联来源，提供选值与派生属性 | 硬件查看、来源详情等使用 |
| 存储业务投影 StorageSnapshot | PhysicalDiskInfo、PartitionInfo、VolumeInfo 等强类型对象 | 管理、拓扑、存储编辑使用，再进行格式化或汇总 |

因此“统一模型字段”并非全部声明在 WinPoolVolume 等类上；部分通过 Field(属性名) 读取，部分在存储投影中有固定属性。

## 2. 统一对象本身

| 对象／字段 | 含义 | UI 对应 |
| --- | --- | --- |
| WinPoolSystem.SystemId | 系统内部身份 | 不作为普通显示项 |
| Revision | 当前事实修订 | 提交与缓存依据，不是普通显示项 |
| Objects | 统一对象集合 | 分类、设备对象的基础 |
| Sources / Collections | 来源及采集任务状态 | 硬件页来源状态、采集时间、失败原因 |
| DisplayGroups | 派生显示分组 | 热备、退役等；不是实际存储层 |
| ProcessorNames | 处理器名称组合 | 可供系统信息使用，不保证每页都有独立行 |
| TotalMemoryBytes | 内存条容量求和 | 可供总内存显示使用，缺失／溢出返回未知 |
| WinPoolObject.Id / ObjectType | 对象身份／类型 | 定位、分类；身份通常不直接显示 |
| DisplayName | FriendlyName、Name 或类型名 | 对象名称／标题，具体页面可能另组装标题 |
| Primary / Sources | 主来源及补充来源 | 来源详情；不等于两个 UI 对象 |
| Field(name) | 按规则读取一个属性 | 不是一个名为 Field 的 UI 控件 |
| WinPoolDisk.IsVirtual | 是否虚拟磁盘 | 区分物理盘与虚拟盘 |
| WinPoolPartition / WinPoolVolume | 继承通用属性；自身没有逐项声明容量、文件系统等固定属性 | 具体业务字段见后面的存储投影 |

## 3. 分区、卷、逻辑磁盘（重点）

| 来源事实／关系 | 统一对象或投影 | 管理页详情对应 | 人工检查重点 |
| --- | --- | --- | --- |
| MSFT_Partition 独立观察 | WinPoolPartition / PartitionInfo | 分区详情 | 分区身份不与卷合并 |
| MSFT_Volume 独立观察 | WinPoolVolume / VolumeInfo | 卷详情 | 卷容量不使用分区容量代填 |
| Win32_LogicalDisk ＋ same-volume | 作为 WinPoolVolume 的补充来源 | 来源详情 | 当前通过本次唯一盘符匹配关联 |
| 未关联的 LogicalDisk | 独立统一对象 | 硬件分类／来源观察 | 不强行并入某个卷 |
| partition-volume | VolumeInfo.PartitionStableId | 卷的类型行会读取关联分区 | 是关系，不是同一身份 |
| 分区 GptType / MbrType / Type | PartitionInfo.Type、PartitionTypeId 等 | 类型（Type） | 已知 GPT 类型转换为友好分类 |
| 分区 Size | PartitionInfo.Size | 分区容量（Capacity） | 表示分区长度 |
| 分区 Offset / DiskNumber / PartitionNumber | 同名投影属性 | 未列为分区详情独立行 | 几何位置／编号，不是容量 |
| 分区 Guid / GptType / MbrType | 同名投影属性 | 标准字段详情 | 分区自身 Guid 与类型 GUID 不同 |
| 分区投影 FileSystem | PartitionInfo.FileSystem | 分区文件系统（FileSystem） | 详情读取此投影字段；不能假定已自动从关联卷补齐 |
| 分区投影 AllocationUnitSize | PartitionInfo.AllocationUnitSize | 分区分配单元（AllocationUnit） | 缺失可能显示 Unknown 或 — |
| 分区投影 SizeRemaining | PartitionInfo.SizeRemaining | 分区可用空间（Available） | 不能仅因字段在分区投影里，就认为它是分区原生几何属性 |
| 分区投影 HealthStatus ＋ OperationalStatus | 两个属性组合 | 分区健康状态（Health） | 不是单字段直接显示 |
| 分区投影 Path | PartitionInfo.Path | 分区路径（Path） | 空值占位 |
| 卷 FileSystem | VolumeInfo.FileSystem | 卷文件系统（FileSystem） | 来自卷主来源 |
| 卷 FileSystemLabel | VolumeInfo.FileSystemLabel | 可参与卷标题；不是详情独立行 | 卷标不同于盘符 |
| 卷 Size | VolumeInfo.Size | 卷容量（Capacity） | 字节格式化 |
| 卷 SizeRemaining | VolumeInfo.SizeRemaining | 卷可用空间（Available） | 不取分区 Size |
| 卷 AllocationUnitSize | VolumeInfo.AllocationUnitSize | 卷分配单元（AllocationUnit） | 字节格式化；未知值占位 |
| 卷 HealthStatus ＋ OperationalStatus | 两个属性组合 | 卷健康状态（Health） | 合并摘要 |
| 卷 AccessPaths；必要时关联分区 AccessPaths；再备用卷 DriveLetter | VolumeInfo.AccessPaths | 卷路径（Path） | 多路径拼接显示；有明确备用顺序 |
| 卷投影 AccessPaths | VolumeInfo.DriveLetter | 标题／盘符相关显示 | 从路径识别盘符，非独立来源身份 |
| 卷关联分区 Type | 读取 PartitionInfo.Type | 卷类型（Type） | 这一行不是卷 FileSystem，也不是卷自身类型字段 |
| LogicalDisk.Size / FreeSpace 等补充观察 | 保留在来源对象中 | 来源详情 | 当前不通用地补写 Volume.Size / SizeRemaining |

## 4. 物理盘与 OS 磁盘

| 来源／业务字段 | 存储投影 | 管理页详情对应 | 处理 |
| --- | --- | --- | --- |
| Model | PhysicalDiskInfo.Model | 型号（Model） | 直接展示 |
| SerialNumber | SerialNumber | 序列号（Serial） | 当前基线保持采集原值；本表不收录实际值 |
| BusType / MediaType | 同名属性 | 总线（Bus）／介质（Media） | 友好显示 |
| Size | Size | 容量（Capacity） | 字节格式化 |
| HealthStatus ＋ OperationalStatus | 同名属性 | 健康状态（Health） | 合并摘要 |
| CanPool | CanPool | 可入池（CanPool） | Yes／No 本地化；缺失问题另作占位处理 |
| CannotPoolReason | CannotPoolReason | 不可入池原因 | 不可入池且原因非空时显示 |
| Usage | Usage → IsHotSpare / IsRetired | 分组、用途操作 | 不列在此物理盘详情常驻行中 |
| IsBoot / IsSystem / IsPageFile / IsCrashDump | 同名安全标志 | 保护规则、相关状态 | 主来源优先；明确关联 OS Disk 的字段可参与备用／冲突检查 |
| LogicalSectorSize / PhysicalSectorSize | 同名属性 | 未列为此详情独立行 | 来源详情／业务规则按需使用 |
| FirmwareVersion / ProvisioningType 等 | 对应属性 | 未列为此详情独立行 | 模型字段不等于常驻控件 |
| MSFT_Disk.PartitionStyle | OsDiskInfo.PartitionStyle | OS 磁盘类型（Type） | 分区表类型 |
| MSFT_Disk.Size | OsDiskInfo.Size | OS 磁盘容量（Capacity） | 字节格式化 |
| Number / IsOffline | OsDiskInfo 同名属性 | 编号、脱机相关交互；非此详情独立行 | OS 磁盘视图，不能与物理盘字段混为一谈 |

## 5. 池、层、虚拟磁盘

| 对象 | 投影字段／计算 | 管理页详情对应 |
| --- | --- | --- |
| 存储池 | IsPrimordial | 类型：原始池／存储池 |
| 存储池 | HealthStatus ＋ OperationalStatus | 健康状态 |
| 存储池 | Size / AllocatedSize | 容量／已分配 |
| 存储池 | MemberPhysicalDiskIds.Count | 成员数；不是保存的独立计数 |
| 存储层 | MediaType | 介质 |
| 存储层 | ResiliencySettingName | 布局／角色（Role） |
| 存储层 | Size | 容量 |
| 存储层 | MemberPhysicalDiskIds.Count | 成员数 |
| 存储层 | FootprintOnPool、NumberOfColumns、Interleave、副本／冗余参数 | 模型保留，此详情未全部列出 |
| 虚拟磁盘 | HealthStatus ＋ OperationalStatus | 健康状态 |
| 虚拟磁盘 | ResiliencySettingName | 布局／角色（Role） |
| 虚拟磁盘 | Size | 容量 |
| 虚拟磁盘 | NumberOfColumns | 列数；空值 — |
| 虚拟磁盘 | Interleave | 交错大小；字节格式化 |
| 虚拟磁盘 | FootprintOnPool、AllocatedSize、ProvisioningType、副本／冗余参数 | 模型保留，此详情未全部列出 |

## 6. 汇总、网络与显示分组

| 对象／内容 | 来源或计算 | UI 对应 |
| --- | --- | --- |
| 系统 Windows 信息 | 产品名＋版本＋Build | Windows 摘要 |
| 系统对象数量 | 相应对象集合 Count | 物理盘、池、层、虚拟盘、网络盘、分区数量 |
| 未划层／层归属未知组 | 当前池直接成员，排除真实层成员、热备和退役盘 | 类型、容量合计、成员数、健康状态摘要 |
| 网络盘 | FileSystem、Size、SizeRemaining、ProviderPath | 文件系统、容量、可用空间、路径 |
| 网络组 | 网络盘计数及容量／剩余空间求和 | 数量、容量、可用空间 |
| 其他磁盘组 | 其他 OS 磁盘及其分区计数、容量汇总 | 磁盘数、分区数、容量、可用空间 |
| LastScan | StorageSnapshot.ScannedAt | 最后扫描时间，转换为本地时间；不等于每个来源的独立采集时间 |

## 7. 硬件页、编辑页和状态

| 页面／内容 | 对应方式 | 检查提醒 |
| --- | --- | --- |
| 硬件属性 | 来源对象字段，配合友好名称及格式化 | 字段数量取决于实际来源，不是固定一套 SQL 列 |
| 来源详情 | 标准字段名、值、类型、单位、状态、来源时间 | 可追溯；组合对象内部仍保留不同来源 |
| 来源状态列表 | Sources／Collections | 查询成功但零对象与查询失败区分 |
| 来源读取状态 | Returned / NotCollected / Unavailable / Failed | 成功返回 null、false、0 与未读取不同 |
| 缺失／冲突 | FieldIssues、来源选值结果 | 普通值显示可能变为 —；操作规则可拒绝或返回信息不足 |
| 编辑表单 | 当前投影＋用户输入／草稿意图 | 输入值不一定已经提交到当前系统 |
| MAX | 编辑意图及容量规则 | 不是直接对应某个来源属性 |
| 待应用动作／命令预览 | 类型化操作计划 | 不是硬件来源字段 |
| 撤销／重做／放弃 | 编辑会话与历史 | 不属于统一设备属性 |

## 8. 人工核对记录

以下留给使用者填写，不表示已经进行本轮原生验收。

| 检查项 | 结果／备注 |
| --- | --- |
| 分区容量与卷容量是否分清 | |
| 卷的“类型”来自分区是否符合预期 | |
| 分区详情的文件系统／可用空间缺失时是否清晰 | |
| 卷与逻辑磁盘的原始观察是否能分别查看 | |
| 容量单位与原始字节值是否一致 | |
| 合并健康状态是否容易理解 | |
| 来源失败、空集合、未知值是否分清 | |
| 哪些模型字段希望增加到 UI | |
| 哪些 UI 项目希望拆分或改名 | |
