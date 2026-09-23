# 模拟系统操作规则表

状态：2026-09-23。规则入口为 `StorageEditRules`，分区创建几何入口为 `EditWorkspace.GetPartitionCreateGeometry`。表中“拒绝”表示规则不允许提交，“禁用”表示页面据同一原因禁用按钮或显示用途提示，“信息不足”表示来源事实不足以安全判断。界面禁用只是预检查，模拟提交仍会重新校验规则、修订和事务。

## 通用门槛

| 条件 | 结果 | 界面反馈／依据 |
| --- | --- | --- |
| 当前是本机真实系统 | 结构修改入口禁用；真实存储写入默认拒绝 | 当前产品只允许浏览真实结构，模拟编辑不授权真实操作 |
| 目标对象不存在、系统修订已变化或提交冲突 | 拒绝／要求刷新 | 不按旧对象位置、名称或旧修订覆盖新事实 |
| 本操作依赖的来源字段缺失或互相冲突 | 信息不足 | 显示缺失字段及对象；不把未知当作允许 |
| 相关来源数值超出编辑可表示范围 | 拒绝 | 保留原始值供查看，不裁剪后继续编辑 |
| 只是选择、输入或取消确认对话框 | 不提交 | 不产生成功消息或部分模拟文档 |
| 传输中断且提交结果不能判定 | 结果未知 | 用 `CommitId` 对账；不能按失败自动重试 |

## 磁盘与分区

| 操作 | 允许条件 | 阻止、禁用或信息不足的规则 | 具体反馈 |
| --- | --- | --- | --- |
| 改名 | 目标存在且可编辑 | 不把相关容量缺失当作改名障碍 | 保存原身份与关系，仅改显示名 |
| 设置盘符 | 分区有关联卷；目标盘符为未占用的单个 A–Z 字母，或清空盘符 | 无卷、非法或已被本机卷／网络盘占用时拒绝 | 指出无卷或冲突盘符 |
| 磁盘离线／上线 | 有目标 OS 磁盘 | Boot、System、PageFile、CrashDump 磁盘不得设为离线 | 指出系统角色；上线不套用离线限制 |
| 初始化 RAW 磁盘 | 非 Boot／System、布局为 RAW，仅 GPT | MBR、新建非 GPT、已初始化盘及系统盘拒绝；勾选 MSR 时磁盘小于 17 MiB 拒绝 | MSR 放在 1 MiB 起点，长 16 MiB，结束于 17 MiB |
| MBR 转 GPT | 非 Boot／System 的 MBR 磁盘 | 非 MBR、转 MBR、Boot／System 磁盘拒绝 | 仅当前模拟转换路径有效 |
| 新建分区 | 在线 GPT 盘的未分配范围至少容纳一个对齐的 1 MiB；新分区长度为正整数 MiB | 起点上取整到 1 MiB 网格；末端以下界取整，余量不足 1 MiB 明确拒绝；超出对齐上限、负数、非整 MiB 拒绝 | 输入为整数 MiB，默认及“最大值”为选中范围的对齐上限；保留磁盘开头 1 MiB |
| 新分区类型和文件系统 | BasicData 可不格式化或用 NTFS／ReFS／exFAT；EFI 只 FAT32；Recovery 只 NTFS；MSR 不格式化 | 不匹配类型的文件系统拒绝 | 创建计划、预览和提交共用同一意图 |
| 格式化现有分区 | 普通 Primary／BasicData、非 Boot／System；NTFS／ReFS／exFAT | EFI、MSR、Recovery、Boot、System 及其它文件系统拒绝 | 快速／完整方式互斥；已有数据先确认损失；两种方式均只模拟结果，不执行真实介质扫描 |
| 删除分区 | 所选分区本身不是 Boot／System | 所选分区有 Boot 或 System 标记时拒绝；同盘其它分区的系统身份和所选 EFI／MSR／Recovery 类型本身不额外阻止删除 | 页面、管理页与提交都使用 `CanDeleteSimulatedPartition` |
| 扩展／压缩 | 在线盘的 Primary／BasicData 分区，几何与卷信息可靠；目标**总容量**为正整数 MiB | EFI／MSR／Recovery、重叠或越盘、多个关联卷、无可靠容量、目标方向错误、目标低于已用数据或超出下一分区边界时拒绝 | 扩展允许 NTFS／ReFS／RAW；压缩允许 NTFS／RAW。能力来自模拟几何，不等于 Windows 实际支持容量 |

分区页仍按设置中的“隐藏小分区缝隙”阈值过滤拓扑节点；这是显示规则，不能放宽模拟提交时的几何检查。右侧底部只有一个随当前选择切换“新建分区／格式化分区”的按钮。右侧字段固定称“起点”“终点”“容量”：起终点以带千位分隔符的 `xxMiB (xx自适应单位)` 显示，数值列右对齐；容量第一行是整数 MiB 输入、固定 MiB 后缀和 MAX，第二行是自动单位的只读换算。“终点”是独占边界：起点 1 MiB、长度 16 MiB 的 MSR 终点为 17 MiB。1 MiB 网格是 WinPool 当前模拟创建规则；已有导入结构不会为了匹配网格而改写。Windows 的 `create partition primary` 和 `New-Partition` 提供 size、offset／alignment 参数，不能据此宣称所有 Windows 磁盘都有相同默认对齐行为：[diskpart 文档](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/create-partition-primary)、[New-Partition 文档](https://learn.microsoft.com/en-us/powershell/module/storage/new-partition?view=windowsserver2025-ps)。

## 池、层和物理盘

| 操作 | 允许条件 | 阻止、禁用或信息不足的规则 |
| --- | --- | --- |
| 新建空池 | 可不选择成员盘 | 空池不可同时创建虚拟磁盘 |
| 新建池／层并加入成员 | 成员存在、`CanPool`、位于 primordial 池，且非 Boot／System／PageFile／CrashDump | 不能入池、来源用途未知或不是 primordial 成员时拒绝／信息不足 |
| Simple／Mirror／Parity 布局 | Simple 至少 1 个数据盘；Mirror 副本数至少 2 且数据盘数不少于副本数；Parity 列数覆盖数据列及冗余且不超过数据盘数 | 单盘 Mirror、盘数／列数不足拒绝；未知布局返回信息不足 |
| 新建容量 | 正数且不超过按当前成员、介质和布局计算的模拟上限 | 无法得出安全上限返回信息不足；小于 4 GiB 可建逻辑容量或超过上限拒绝；MAX 按 4 GiB 粒度规划 |
| 新建虚拟磁盘 | 非 primordial 池且此池尚无虚拟磁盘，布局合法 | 编辑器每池最多新建 1 个；导入系统已有额外虚拟磁盘保持只读 |
| 修改已有池／层 | 非 primordial 池、每池最多 1 个虚拟磁盘，布局和容量合法 | 含数据池只允许现有层使用“最大值”扩展；规格变更、缩容、越过已有分区边界拒绝；上限未知返回信息不足 |
| 删除虚拟磁盘／解散池 | 目标存在；解散目标须非 primordial | primordial 池不可解散；目标缺失拒绝 |
| 删除空池 | 非 primordial，且无成员盘、无虚拟磁盘 | 非空池拒绝 |
| 移动物理盘成员身份 | 盘存在、不是 Boot／System／PageFile／CrashDump | 被保护角色拒绝；已有池中用途未知返回信息不足 |
| 退役／热备／恢复数据用途 | 已入池、非 Boot／System，且设置 Retired／HotSpare 后仍有至少一个数据成员 | 未入池、系统盘、把最后一个数据盘转退役／热备或未知用途值拒绝 |
| 优化池／优化驱动器 | 模拟规则接受 | 当前是显式无操作预览，不声称实际 Windows 优化完成 |

上述是当前模拟编辑边界。来源 Windows 对象与操作参考链接由 `StorageEditRules` 常量维护；模拟规划值不当作物理设备实测。格式化和分区修改没有真实存储执行入口。
