# Pool01管理与编辑拓扑不一致调查

2026-10-14，V0.59基线 `b39ac42`。用户明确指出的是管理页与存储结构编辑页的拓扑不一致。本次只读取Windows、WinPool来源和执行账本；未重建池、未更改磁盘。

## 判断

**存在WinPool构图问题，不是本次Pool01的Windows故障或创建参数错误。** 管理页将同介质的未使用模板与其已分配实例画成两个业务层，编辑页只显示实际层。两页各自的构图选择规则分叉，且旧测试曾明确要求保留这种差异。修复由共享介质层选择规则统一：已有实际层时画实际层；没有实际层时画模板。多个实际层仍逐个保留，不按介质错误合并。原始Windows对象、完整来源事实和详情列表仍保留模板与实例的各自身份。

## 三方事实

| 对象 | Windows事实与创建账本 |
| --- | --- |
| Pool01 | UID `{896cdd26-939b-4078-bb57-accb727c3ee4}`，Healthy／OK，仅WDC `50014E2E14568901`，成员Usage Auto-Select |
| 池默认值 | `ResiliencySettingNameDefault=Mirror`，`ProvisioningTypeDefault=Fixed`；创建池命令未指定默认复原类型，因此沿用Windows默认。这不是实际层的复原类型。 |
| 池级模板 Pool01_HDD | UID `{0547c961-bbc9-4f2d-a15b-cbb0fa5f2513}`；HDD／Simple／1列／16 KiB／1副本／0冗余，Size与AllocatedSize均0；它提供创建规格，未与实际VD建立`virtual-disk-tier`关系。 |
| 实际层 Pool01-Pool01_HDD | UID `{a0d4ad14-0366-466f-af77-87a4f908d857}`；HDD／Simple／Fixed／1列／16 KiB／1副本／0冗余，Size与AllocatedSize均2,068,026,753,024 bytes，即1926 GiB。 |
| 虚拟磁盘Pool01 | UID `AE390F4A7867CE4DA670D9B6ECEB9DB8`，Healthy／OK，容量与实际层一致；汇总布局字段由Windows明确返回null，准确布局存在于actual tier，不能把pool默认Mirror补成VD布局。 |
| 创建参数 | 09:20创建池、09:21创建Simple／1列／16 KiB模板与明确1926 GiB分层VD；执行账本各步骤Verified。实际层与参数一致。1926 GiB是明确容量，不是本轮发现的MAX搜索上限。 |
| 分区／卷 | VD关联OS disk在线GPT；E:为ReFS、64 KiB簇，容量小于VD是分区／文件系统自身布局，不是同一容量指标。 |

池AllocatedSize为2,068,295,188,480 bytes，比VD／actual tier多256 MiB；这是provider报告的不同记账层次。本次没有证据将此差额判定为损坏或将其强改为相等。

微软[Get-StorageTier说明](https://learn.microsoft.com/en-us/powershell/module/storage/get-storagetier?view=windowsserver2025-ps)分别支持从池与VD取得层对象；[New-VirtualDisk说明](https://learn.microsoft.com/en-us/powershell/module/storage/new-virtualdisk?view=windowsserver2025-ps)说明未指定参数使用池默认值，分层创建接受模板对象和对应层容量。这些说明结合本机准确关联用于判断，不能只凭友好名称推导对象相同。

## 根因与修复

- 管理路径：`ManageSystemProjector.Project → TopologyProjector.Project → CreatePoolNode`，原来直接遍历池下全部`StorageTiers`。
- 编辑路径：`EditWorkspace.ProjectPoolWorkspace → CreateEditPoolNode`，已按介质优先选择分配实例、无实例才选择模板。
- 修复：两条路径复用同一个业务层选择器；来源与详情不删对象。管理页的分区等额外查看内容仍按其产品职责显示，不能为了截图完全相同而删掉。
- 同轮定位到的hover／selected状态组和SVG加载问题是另外的界面缺陷，与Pool01创建正确性无关。

## 验证状态

代码及GUI验证结果在本阶段[Quality](../Quality.md)统一记录。原始只读取证、两页修复前截图和账本摘录保存在[证据目录](../../artifacts/test-results/20261014-v060-editor/)，包括`native-before.json`、`native-actual-tiers-before.json`、`local-document-before.json`、`ledger-before.json`、`manage-before.png`和`structure-before-visible.png`。
