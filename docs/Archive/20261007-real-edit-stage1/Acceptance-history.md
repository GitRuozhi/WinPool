# WinPool 验证与验收

本文件规定验证选择与结果含义。有活动阶段时，范围和进度记入 `docs/Plan.md`；已完成阶段见[归档](Archive/README.md)。技术约定见 [Development](Development.md)，真实操作边界见 [Product](Product.md)。

## 2026-10-07 当前真实验收进度

当前证据根为 `artifacts/test-results/20261007-real-edit-stage1-9b7a6ce20531437aafe897e143628d43/`。管理员 App→Agent 最终确认取消未产生新增写调用；普通 App 重启并复用旧 Agent 后真实编辑开关仍关闭。H01 清盘计划 `17b31c05-1c69-4f4e-9eea-27d0e8b82fc1` Completed、步骤 Verified，操作后只读采集证明 WDC 为 RAW、零分区（`H01/verified-final/`）。

P5 当前原生 UAC 成功交接已通过：两次启动助手失败均无 child 且原样保留；随后用 medium Explorer Run 打开标准 App。旧 App/Agent（PID 21344/15464）以 integrity 8192、未提权运行，用户接受 UAC 后均以 exit 0 完整退出、保留 handles、未强杀；新 App/Agent（PID 2656/22228）均为 integrity 12288、已提权，App UIA 显示 Real editing On，Agent 正面进入本机真实模式。交接保持 `ContinuousMonitoringEnabled=true`、20 Hz；只读会话/事件核对中 `accepted` 为 60→60、`call_issued` 为 85→85，delta 均为 0。完整检查点见 `P5/uac-current-native-success-checkpoint.md`。这闭合本轮 UAC/监控交接子项；P5 仍只待当前 App/Agent 崩溃汇总和稳定源码最终工程门/T01–T16 映射。

H02 首次初始化计划 `797f9f3e-5946-4315-b3bb-d567cc24507c` 仍为 PartiallyCompleted：GPT 初始化 Verified，MSR 步骤 Skipped；Windows 在初始化时自动创建的 MSR 位于 offset 17,408、大小 16,759,808 bytes。后续计划 `86b7b785-4429-4bea-aa20-984885cd0750` 已 Verified 删除该分区（原生属性为 0），但 MSR 重建在适配器预检被拒、未调用 Windows；单独的新计划 `ecc915f1-e531-4455-bd69-fdf7d1d078b9` 随后 Completed、步骤 Verified，并在 WDC 上重建了 offset 1 MiB、大小 16 MiB 的 MSR（`H02/msr-config-completed/`、`H02/msr-retry-result-2/`、`H02/canonical-msr-fresh-inventory/`）。这些独立计划的结果不改写前述部分完成计划。

主 NTFS 数据分区计划 `9d0ba4f8-b7d0-49e3-9a43-df174a9a22a5` Completed、两个步骤均 Verified：BasicData 分区从 offset 17,825,792 bytes 开始、大小 8 GiB，已快速格式化为 NTFS、64 KiB 簇，卷标 `WinPool_Stage1_Data`（`H02/main-ntfs-operation-check-2/`、`H02/main-ntfs-fresh-inventory-2/`）。RAW BasicData 分区 `partition:uid:d9eb6ce62c136ad35db5283c` 从 1 GiB 扩展到 2 GiB，再压缩到 1.5 GiB；两个 ResizePartition 计划均 Completed、步骤 Verified（`H02/raw-extend-2048/`、`H02/raw-extend-fresh/`、`H02/raw-shrink-1536/`、`H02/raw-shrink-fresh/`）。随后删除计划 `7935500b-3323-4367-9c82-981bc9b89698` Completed、步骤 Verified。冻结计划证据与紧邻调用前的安全证据都指向同一分区／卷，BitLocker 枚举完整、状态为 `NotApplicableUnformatted`，VDS 文件系统类型为 1（RAW）、分配单元 512 bytes（`H02/raw-delete-fixed-frozen/`、`H02/raw-delete-fixed-result/`）。删除后的持久化 inventory 在 `H02/raw-delete-fixed-fresh/` 记录 Storage 来源全部返回，目标分区和关联卷均已消失；MSR #1 与主分区 #2 的稳定 ID、offset 与当时几何保持一致。16 MiB 邻接文件在删除后仍与基线 SHA-256 相同（`H03/hash-after-adjacent-raw-delete.json`）。该导出读取匹配的持久化 snapshot，但 `freshness_asserted=false`，不自行证明 ScanAsync；也不排除 Windows 后台 I/O。较早因无法唯一映射安全状态而被拒的 Prepare 是历史尝试，记录为 `NoWindowsCall` 且没有历史截图（`H02/raw-delete-prepare-rejected-observation.json`）；它已被上述独立、成功的删除操作取代。本段 H02 GPT/RAW 目标操作序列已验证，但不改写仍为部分完成的原初始化计划。更完整的操作清单见 `runRoot/H02-operation-evidence-ledger-20261007-after-delete.md`。

H03 的盘符／卷标步骤均已按各自计划 Verified：无盘符→W (`ad6262d8-cf74-44fc-b8ad-e695ee0be158`)、按 Enter 将卷标改为 `WinPool_Stage1_Rename` (`542bc965-fa40-43f8-a783-fc40451123fb`)、W→X (`c37f6b5c-046c-40d3-a1db-ecfe7079ba8f`)、X→无盘符 (`02a71ab4-e508-4ebf-8259-6b2f7e754b27`)，再恢复 W (`e30d950e-73b1-4555-a6de-4f968e71d488`)；这些计划均 Completed（`H03/assign-W/`、`H03/rename-enter/`、`H03/letter-W-to-X/`、`H03/letter-clear-X/`、`H03/restore-W/`）。主 NTFS 分区 #2 由 8 GiB 扩到 12 GiB (`c5d74216-f9f8-437f-b26a-228190699289`)，再缩到 10 GiB (`51801c69-1913-416a-b414-e901edfa764c`)，两个计划均 Completed、步骤 Verified。缩后持久化 inventory 记录 WDC 上该分区 offset 17,825,792 bytes、大小 10 GiB、NTFS 64 KiB 簇、卷标 `WinPool_Stage1_Rename`；记录的 Storage 来源全部返回，但 helper 的 `freshness_asserted=false`（`H03/ntfs-extend-12288/`、`H03/ntfs-extend-fresh/`、`H03/ntfs-shrink-10240/`、`H03/ntfs-shrink-fresh/`）。16 MiB 文件 `WinPool_Stage1_Hash.bin` 的基线 SHA-256 为 `CB92A512A2840194DADF278A913A3C0E9635551772281F59A9C58272345D3775`，在重命名、盘符变化、扩容及缩容后均匹配基线（`H03/hash-baseline.json`、`H03/hash-after-rename.json`、`H03/hash-after-W-to-X.json`、`H03/hash-after-letter-clear.json`、`H03/hash-after-restore-W.json`、`H03/hash-after-ntfs-extend.json`、`H03/hash-after-ntfs-shrink.json`）。

离线计划 `e6b48fbc-4842-445f-a8e4-d7317d5598dc` 首次调用后曾记录 `OutcomeUnknown`，因为严格核对遇到 `MSFT_Partition.IsHidden=null`；这一中间状态仍保留在 `runRoot/H03-operation-evidence-ledger-20261007-offline-unknown.md`。只读恢复 Agent PID 15816 未重放操作，最终 `H03/offline-recovery/result-check-2/` 将同一计划核对为 Completed、步骤 Verified，且恢复过程没有新增写适配器调用。该结果保存了精确 WDC 身份及离线属性：MSR GUID `2a812f6d-1d91-43ce-84fe-b576bd9c0285` 位于 1 MiB、大小 16 MiB、Attributes `9223372036854775808`；BasicData 主分区 GUID `d7233fc8-bf94-4c39-93cb-58c54ca570d2` 位于 offset 17,825,792 bytes、大小 10 GiB、Attributes 0；两者均记录 `DiskIsOffline=true`。随后独立联机计划 `6616d429-4fca-4308-b998-1788de6a84e6` Completed、步骤 Verified（`H03/online-result-check-2/`、`H03/online-success.txt`）。联机后的 `hash-after-online.json` 中 16 MiB 文件 SHA-256 仍为基线 `CB92A512A2840194DADF278A913A3C0E9635551772281F59A9C58272345D3775`；持久化 inventory 时间为 `2026-10-07T15:25:51.928273+08:00`，记录 WDC 已联机、主分区仍为同一 GUID／offset／10 GiB，NTFS 64 KiB 簇、卷标 `WinPool_Stage1_Rename`、W:。该 inventory 的 `freshness_asserted=false`，不能单独证明 ScanAsync 因果。H00 基线到此 inventory 的 Samsung 结构比较为 unchanged、0 changes、0 evidence gaps；两块盘均按 Stable ID、UniqueId、ObjectId 与 serial 四项匹配（`H03/protected-comparison-after-online/`）。比较不能排除 Windows 后台 I/O。最终只读恢复 Agent 经托盘正常退出；恢复后标准构建为 0 警告、0 错误（`H03/offline-recovery/standard-build.log`）。H03 命名操作已完成；早期 OutcomeUnknown 记录保留为历史检查点。

H04 NTFS 与 exFAT 真实操作已有明确结果：计划 `48b6f49c-e58a-4734-97da-52d8fad2ffae` 在 WDC 上创建 1 GiB BasicData 分区，并以 64 KiB 簇、卷标 `WP_S1_NTFS` 完整格式化 NTFS；删除计划 `c3b952c2-6c8e-453d-b807-5321d1de5e40` Verified 删除目标分区及卷。计划 `4fdf7cf9-2c07-40ff-92ed-3d5d3e496cb0` 创建同几何分区并快速格式化为 exFAT、64 KiB 簇、卷标 `WP_S1_EX`；随后完整格式化计划 `868ad9fc-b892-4d5f-869e-8b14b830c960` 和删除计划 `085e9842-5043-41e4-a5b3-35ed139af4a4` 均 Completed、步骤 Verified。完整预览、结果及操作导出位于 `H04/`。
EFI 最初的计划 37caca80-9ee6-4ee8-8468-fcb8c5e49bac 仍保留为 PartiallyCompleted：EFI 创建步骤 Verified，格式化在 Windows 调用前被 preflight 拒绝，未出现该步骤的 call_issued。主代理在该初始拒绝后只读核对主数据文件 SHA-256 仍与基线 CB92A512A2840194DADF278A913A3C0E9635551772281F59A9C58272345D3775 相同。该尝试留下的原始 EFI 分区 GUID 7389d26f-9da4-403c-87e7-0ac707428dd5 后由独立删除计划 e6da184c-5259-4d23-88fb-75af6b774905 Completed、步骤 Verified 删除。

后续计划 c2696572-12a4-4500-afdd-2d190ece5782 在 WDC WD40EZAZ-00SF3B0、serial WD-WX22D610FP80、OS Disk 0 上创建并格式化新 EFI 分区，两个步骤均 Verified。分区 GUID 为 d03ef22a-e3ec-4130-8eea-3c9433951a4b，EFI 类型 GUID c12a7328-f81f-11d2-ba4b-00a0c93ec93b，offset 10,755,244,032 bytes，大小 272,629,760 bytes（260 MiB）；卷格式为 FAT32、簇大小 4,096 bytes、卷标 WP_S1_EFI。格式步骤保存了 ProviderCode provider.returned、MSFT_Partition 与 MSFT_Volume 原生来源，以及 BitLockerEnumerationComplete=true、BitLockerApplicability=NotApplicableNativeEfiSystemPartition；VDS filesystem 与 allocation-unit 字段均为空。之后删除计划 b3645486-94b1-4d5c-9495-0f82735d1d3c Completed、步骤 Verified 删除该 EFI 分区及关联卷。

efi-fixed-delete-fresh 持久化 inventory 中已无目标分区和卷，WDC 报告两块剩余分区。raw-efi-delete-fresh 与 efi-fixed-delete-fresh 的选定结构字段逐项相同：WDC 身份、MSR 与 BasicData 分区 ID／GUID／类型／几何／角色／访问路径，以及 W: 主卷的 NTFS、64 KiB 簇和 WinPool_Stage1_Rename 标签均未变化。MSR 为 GUID 2a812f6d-1d91-43ce-84fe-b576bd9c0285，offset 1 MiB、大小 16 MiB；主分区为 GUID d7233fc8-bf94-4c39-93cb-58c54ca570d2，offset 17,825,792 bytes、大小 10 GiB。三份 inventory manifest 都标记 freshness_asserted=false：helper 读取匹配的持久化 snapshot，不执行 ScanAsync；capture 时间与存储来源状态照实留在新的检查点中。

Recovery 初始计划 6e0ccf9e-5664-453f-9d91-4c7823dc86c0 的创建步骤 Verified，但格式步骤因 “The exact VDS volume is absent or not unique.” 在 call_issued 前被拒，计划保持 PartiallyCompleted。对原 Recovery 卷 Volume{7a6b23d0-e551-4cc1-8880-724b892caa73} 的只读 Win32_EncryptableVolume 精确对象核验，前后 DeviceID 均与请求值相同，Get 成功，EncryptionMethod、ConversionStatus、LockStatus 均为 0，三项返回值均为 0。WMI 安全读取随后通过精确 volume GUID 得到 FullyDecrypted；旧计划的独立只读 preflight 因 MSR 删除后 topology 已变化而拒绝，未重放（H04/recovery-exact-bitlocker-object-bound-readonly.json、recovery-wmi-safety-readonly-fixed.log）。

规范 MSR 删除计划 910211f2-3975-412a-b3d7-02b5567fd55f Completed、步骤 Verified，删除 offset 1 MiB、大小 16 MiB 的 MSR；随后独立创建 MSR 的准备在原始 Recovery topology 校验处被拒，未调用 Windows（H04/canonical-msr-delete-result/、msr-create-while-recovery-raw-rejected.txt）。原 Recovery 分区由 1d5794e3-73f5-4213-b9de-69f003f4b898 Verified 删除。新的 Recovery 计划 acee8c6a-936b-49b7-b711-ff74f696dc99 创建并快速格式化 1 GiB 分区，两个步骤均 Verified：GUID d7d820c3-1b5e-48c9-b717-9e3ff7f1a82e、类型 GUID de94bba4-06d1-4d40-a16a-bfd50179d6ac、offset 10,755,244,032 bytes、NTFS 4,096-byte clusters、卷标 WP_S1_REC。该格式步骤的 BitLocker evidence 绑定同一新卷 GUID，GetEncryptionMethod／GetConversionStatus／GetLockStatus 均有精确对象与零返回值记录，状态 FullyDecrypted，VDS filesystem／allocation-unit 字段为空。随后删除计划 6283278a-2217-4ba4-984b-5a7ca73c96c6 Completed、步骤 Verified 删除该 Recovery 分区及关联卷。

recovery-fixed-delete-fresh 核对目标 Recovery 分区和卷均已消失；在该中间检查点，WDC inventory 只剩原主 BasicData 分区（GUID d7233fc8-bf94-4c39-93cb-58c54ca570d2、10 GiB、W:、NTFS 64 KiB、WinPool_Stage1_Rename），当时尚无 MSR。Recovery 的创建／格式化／删除周期已验证；稍后的独立 MSR 创建和删除计划完成了最后一项 H04 角色操作，见下文新检查点。WMI 修复后 Infrastructure 回归 458/458 通过、标准构建 0 警告／0 错误（H04/regression/recovery-wmi-infrastructure.trx、recovery-wmi-standard-build.log）。该 inventory helper 的 freshness_asserted=false，来源均 Returned，但 helper 不自行证明 ScanAsync；操作、WMI 与 inventory 证据见 H04-recovery-wmi-checkpoint-20261007.md。

关联查询/CreateTier 定点回归 49/49、Agent preflight 诊断回归 86/86、EFI 修复后的 Infrastructure 回归 441/441 通过；EFI 修复后标准构建为 0 警告、0 错误（H04/regression/resize-tier-regression.trx、preflight-diagnostic.trx、efi-infrastructure-final.trx、efi-standard-build.log）。修复后操作导出及有效 capture-screen 画面保存在 H04/efi-fixed-result/、efi-fixed-fresh/、efi-fixed-success-screen.png 与后续删除目录；before-efi-fix-clean-exit.json 早于修复，不作为修复运行时证据。更早的黑色 4 KiB PNG 不作为视觉通过依据。完整 EFI 检查点见 runRoot/H04-efi-closure-checkpoint-20261007.md；原 partial 检查点仍保留在 runRoot/H04-operation-evidence-ledger-20261007-efi-preflight-partial.md。EFI 与 Recovery 分区循环已闭环；独立 MSR 创建／删除随后完成，H04 命名序列现已完成。完整最终门仍未通过，产品仍为 V0.57。

H04 最后创建计划 `23fa5229-7895-4af2-91de-ba75384dfe25` 与删除计划 `92cc0efe-c4e2-4ce3-b4e8-103fc52c73bc` 均为 Completed、步骤 Verified。创建后的持久化 inventory 在 WDC OS Disk 0 记录 MSR GUID `60378aef-61ce-416e-ac2b-2e10464c71d8`、类型 GUID `e3c9e316-0b5c-4db8-817d-f92df00215ae`、offset 1 MiB、大小 16 MiB；删除的冻结目标与此身份和几何相同，原生 MSR 安全事实记录磁盘在线。删除后 inventory 中该 MSR、EFI 与 Recovery 测试分区均已消失，只剩原 10 GiB BasicData 主分区：NTFS、64 KiB 簇、W:、卷标 `WinPool_Stage1_Rename`。`hash-after-H04-complete.json` 的 16 MiB 文件 SHA-256 与基线相同。H00 持久化基线至 H04 删除后 inventory 的 Samsung 比较为 unchanged、0 changes、0 evidence gaps；两块 Samsung 均按 StableId、UniqueId、ObjectId、serial 四项匹配。比较保留基线 manifest 的 `recorded_storage_sources_returned=false` 原值；关联 Storage collection 和逐项 MSFT storage class 读取均为 Returned。比较 `freshness_verified=false` 且不能排除 Windows 后台 I/O。Inventory helper 使用关闭页面、约 4 秒后 ScanAsync 的操作路径，但 helper 只读取本地持久文档及对应 snapshot，本身不证明 ScanAsync 因果，manifest 仍标 `freshness_asserted=false`。详细最终账本见 `runRoot/H04-final-closeout-checkpoint-20261007.md`；早期 partial、preflight rejection 和 WMI checkpoint 均保留。H04 命名序列完成；P6 全部验收与最终工程门仍待完成，不升版本，当前产品为 V0.57。

H05 ReFS 候选分区创建计划 `1ce58c83-05b8-464c-9d60-48d6e551514d`、C01 格式计划 `1340470d-9799-4823-9eeb-884eeba8bcff` 和 C02 扩容计划 `771a51d9-0de6-4678-8bb9-9bd6650b3151` 均记录为 Completed、步骤 Verified。WDC OS Disk 0 上创建的 BasicData 分区稳定 ID `partition:uid:e4a5eb421657905cd660f150`、GUID `b530dc34-7379-4c9a-bc9d-4c295f56689b`、offset 10,755,244,032 bytes、1 GiB；C01 以快速格式化建立 ReFS、65,536-byte clusters、卷标 `WinPool_Stage1_Refs`，无盘符。Provider live capability 返回 ReFS 及 4,096／65,536-byte 簇支持；格式证据绑定同一分区／卷，BitLocker 枚举完整、Applicable，精确卷 DeviceId 匹配且状态 FullyDecrypted。C02 将该分区扩至 2 GiB，保持 GUID 和 offset；provider 范围及几何交集记录在 `H05/refs-extend-provider-range.txt`，目标在所报范围内，因此容量与几何已验证。ReFS 卷内文件内容保留尚未验证：现有 16 MiB 主文件哈希属于相邻 W: NTFS 卷，不能证明 ReFS 文件哈希在扩容前后保持。此前记录的 Shrink 控件禁用来自操作员观察；H11 将补充 C02 同一 ReFS 卷前后哈希和禁用控件的 UIA 证据，再删除该候选分区。故尽管 C02 扩容操作本身 Completed/Verified，C02 内容保留验收及 H05 整体闭合仍待补证。H05 inventory 显示 ReFS 2 GiB 测试分区与 W: 主 NTFS 分区均在线。H00→H05 Samsung 比较为 unchanged、0 changes、0 evidence gaps，两盘四项身份均匹配；比较不能证明采集由 ScanAsync 触发，也不能排除 Windows 后台 I/O。三个 inventory helper manifest 均为 `freshness_asserted=false`，只读取本地持久文档和所链接 snapshot。C03 因 WDC 起始为 GPT 而 `not_required`；C05 保持禁用，分区 C02 范围不代替既有 VD／层扩展能力证据。详见 `runRoot/H05-final-closeout-checkpoint-20261007.md`；其 H05 关闭措辞由此段收窄，原始操作结果仍保留。

H06 已闭合。清盘计划 `2254a455-88f8-453c-a3fc-23c7e00a23cc` Completed、步骤 Verified，WDC 为 RAW、零分区。此前 CanPool 快照曾为 false/reason 7；缓存发现修复的定点回归 8/8 通过、标准构建 0 警告／0 错误，修复后 startup inventory 显示 CanPool=true（`H06/provider-cache-facts-tests.log`、`H06/provider-cache-standard-build.log`、`H06/provider-cache-fixed-canpool.json`、`H06/provider-cache-fixed-startup-fresh/`）。建池计划 `17024aab-0b0f-4cc3-853a-946b35ad719e` 的 Provider 已返回新池，UniqueId `{a21facae-61c0-4f1e-8e03-24354735a630}`；初次后态核验失败，留下 `OutcomeUnknown`、`real.postcondition_unverified` 和 `real.reconciliation_capture_failed` 历史事件。之后的只读恢复将同一计划更新为 Completed、步骤 Verified。恢复 evidence 中 `PoolMemberRoleEvidence` 精确绑定 WDC `physical:uid:6ceea032f3fb375a61cd27bd` 与池 `pool:uid:9fff1b6c6f5416e2a8f9cf98`，`AssociatedOsDiskIds=[]` 且 IsBoot／IsSystem／IsPageFile／IsCrashDump 均为 false，方法为 `CompleteCurrentPoolAndOsDiskAssociations`。前后 `call_issued` 数均为 1；只新增 `real.reconciliation_verified_observed_pool_creation` 和 `real.reconciliation_verified` 两项事件，没有重放 Windows 写调用（`H06/pool-readonly-recovered/`、`H06/pool-recovery-no-replay-comparison.json`）。旧 OutcomeUnknown 检查点及失败事件仍保留。恢复后的 PID 15384 通过 UI 正常退出、退出码 0；恢复 Agent 与标准构建均为 0 警告／0 错误（`H06/pool-recovery-agent-normal-exit.json`、`H06/pool-role-recovery-tree-agent-build.log`、`H06/pool-role-standard-build.log`）。Infrastructure 定点回归各次结果分别保留：首次 510/520 通过、10 失败；第二次 512/520 通过、8 失败；最终 530/530 通过（`H06/regression/pool-role-recovery-infrastructure.trx`、`pool-role-recovery-infrastructure-2.trx`、`pool-role-recovery-infrastructure-3.trx`）。17:47 持久化 inventory 显示具体池为非 primordial、成员只有 WDC、尚无 VD；Storage collection 与 8 个 MSFT Storage 类来源均 Returned，但该 capture manifest 的 `recorded_storage_sources_returned=false` 且 `freshness_asserted=false`，helper 不证明 ScanAsync 因果。建池后的旧全范围比较为 `changed`、8 changes／0 gaps（`H06/protected-comparison-after-pool-unknown/`），差异为共享 primordial `AllocatedSize` 0→4,000,769,376,256 bytes 与 WDC PoolMember 边移除，不是 Samsung 自有 OS Disk、分区或卷字段变化。独立 scoped 比较 `H06/protected-comparison-after-pool-scoped-v2-2/` 为 `unchanged`、0 changes／0 gaps，两盘 Stable ID、UniqueId、ObjectId、serial 四项身份均匹配；它只比较 Samsung 关联结构，并基于快照及 SourceFacts `IsPrimordial=true`／Returned 的正面证明排除共享 primordial `AllocatedSize` 全机汇总和非目标成员边，保留 Samsung 自身 PoolMember 边、池身份与其它结构。比较不表示全机拓扑无变化、不证明 ScanAsync 因果，也不能排除 Windows 后台 I/O。H06 最终检查点见 `runRoot/H06-final-closeout-checkpoint-20261007.md`；较早 unknown checkpoint 保留。

H07 完整命名序列已完成。初始 RAW VD 创建计划 `23fcdd92-ea4b-4164-941c-fcfefa3ebf92` Completed、1/1 Verified，随后删除计划 `2bf2f3b7-0148-4cb7-b01c-313b27a02e40` 也 Completed、1/1 Verified。GPT/MSR 关闭计划 `936085d1-23d5-4605-8713-b5a1da2812b1` Completed、1/1 Verified；Windows 创建的偏移 17,408 bytes、大小 16,759,808 bytes 的未格式化 MSR 由独立删除计划 `68b103e3-92fd-4d57-9617-9b1f6ace6dd0` Verified 移除。AutoVD=true 分支创建计划 `aa95511a-a6c1-470a-88ca-671ebd578108` 两步均 Verified，建立 16 GiB Simple/Fixed、1 列、65,536-byte interleave 的 `WinPool_Stage1_VD`。布局计划 `aee397f1-0cca-458f-af66-10b3f2bfea01` 的五步（移除自动 MSR、创建 MSR、创建 BasicData、格式化 NTFS、分配 W:）均 Verified；状态查询明确为 `Succeeded`、`Requires reconciliation: False`。旧的 phase-2 Running 导出保留；`auto-vd-layout-phase2-result-2/` 为最终 Completed 证据。

最终持久化 inventory `H07/auto-layout-final-fresh/`（2026-10-07 18:01:36.2502407 +08:00）将 WDC WD40EZAZ-00SF3B0、serial `WD-WX22D610FP80`、Physical Stable ID `physical:uid:6ceea032f3fb375a61cd27bd`、UniqueId `50014E2E14568901` 映射到在线 GPT OS Disk 3。池 `pool:uid:9fff1b6c6f5416e2a8f9cf98` 中的虚拟盘为 16 GiB、Simple/Fixed、1 列、65,536-byte interleave。最终 MSR GUID `{1510604c-56dd-4980-bf6d-58bf3b1a946d}` 位于 1 MiB、大小 16 MiB；BasicData GUID `{08aac3f4-5aab-4e0a-8caa-8b40b3994fa5}` 位于 offset 17,825,792 bytes，大小 17,160,994,816 bytes（默认 MAX），NTFS、65,536-byte 簇、卷标 `WinPool_Stage1_Data`、W:。Storage collection 与 16 个 Storage-purpose 来源均 Returned。Capture 的 `freshness_asserted=false`：helper 读取匹配的本地持久文档/snapshot，不证明 ScanAsync 因果。

H00→H07 Samsung scoped 比较 `H07/protected-auto-layout-final/` 为 unchanged、0 changes、0 evidence gaps、4 项 scope adjustment；两块盘各自四项身份均匹配。比较仅覆盖记录的 Samsung 关联结构，不声称全机拓扑不变，也不能排除 Windows 后台 I/O。最新 Agent 回归 89/89 通过、0 失败、0 跳过（`H07/regression/layout-and-pool-recovery-agent-3.trx`）；早期测试编译失败和 87/89、2 失败结果保留在原始日志/TRX。

H08 的三项改名及相邻 16 MiB 文件哈希核对已完成。池改名计划 `531e5792-0f5a-49cd-bb76-100be1088bec`、VD 改名计划 `4593e90a-5ebf-4dd6-94c4-e198b1cbf23b`、卷标改名计划 `064a32fc-9479-44c1-a322-855b9cf84e76` 均为 Completed、1/1 Verified，且每项各有 1 次 `call_issued`。新名称分别为 `WinPool_Stage1_Pool_Rename`、`WinPool_Stage1_VD_Rename`、`WinPool_Stage1_Data_Rename`；StableId、UniqueId、partition GUID 及 W: 保持。

最终 inventory `H08/rename-volume-final-fresh/` 链接的本机 snapshot 时间为 `2026-10-07T18:20:20+08:00`；Capture manifest 于约 4 秒后读取，Storage collection 和 16 个 Storage-purpose 来源均 Returned，`freshness_asserted=false`。它记录 WDC `WD40EZAZ-00SF3B0`、serial `WD-WX22D610FP80`、Physical Stable ID `physical:uid:6ceea032f3fb375a61cd27bd`、UniqueId `50014E2E14568901`，在线 GPT OS Disk 3。池 `pool:uid:9fff1b6c6f5416e2a8f9cf98` 和 Simple/Fixed、16 GiB、1 列、65,536-byte interleave VD 均保持原身份。MSR 保持 1 MiB/16 MiB；BasicData GUID `{08aac3f4-5aab-4e0a-8caa-8b40b3994fa5}` 保持 offset 17,825,792 bytes、size 17,160,994,816 bytes、NTFS 65,536-byte 簇、W:，标签改为 `WinPool_Stage1_Data_Rename`。

`H08/rename-final-hash.json` 与 `H08/hash-baseline.json` 的 16 MiB 文件 SHA-256 完全一致：`F5DABF9A2A45DD60D162EB2EBD7F8C19A49D7781118162A348468ABC3528A37F`；池/VD UniqueId、分区 GUID、W: 和簇大小也相同。H00→H08 Samsung scoped 比较 `H08/protected-rename-final/` 为 unchanged、0 changes、0 gaps、4 scope adjustments；两盘四项身份均匹配。范围仅为受保护盘关联结构，不证明 ScanAsync 因果、全机无变化或排除 Windows 后台 I/O。

C05 现有 VD 扩展仍因当前 supported-size/bounds 证据不足而禁用；`H08/c05-existing-vd-disabled-uia.txt` 记录当前布局相关控件禁用。最新 Agent 回归 `H07/regression/layout-and-pool-recovery-agent-3.trx` 为 89/89，通过现有对象 Resize 拒绝（tier false/true）及不得删除重建的测试；未执行 C05 写入，不据此宣称 provider 支持扩展。完整 H08 检查点见 `runRoot/H08-final-closeout-checkpoint-20261007.md`。

H09 已完成。首次重建计划 fdc2108d-c34f-4275-9a06-8a81db9b0fc4 在删除旧 VD/池后因残留自动 MSR被 CreatePool preflight 拒绝，NoWindowsCall；部分结果保持不变。早期 ClearDisk/AutoCreatePool/layout recovery 结果也全部留存。修复版 R5 则将旧对象删除、精确成员清盘、CreatePool/32 GiB VD/GPT、独立自动 MAX 布局拆成四个单独冻结与确认的计划：45453963-f718-4f60-8a9e-74de3f65e5ef 为 Completed 2/2；9b01e8b3-17a4-4e0e-8a96-73242bbfedd2 为 Completed 1/1；5d5ea9d7-ac22-494b-8040-fb4a0b5577eb 为 Completed 3/3；dc32cbba-6e4c-49d7-8612-791deb637303 为 Completed 5/5。四段每一步均 Verified；final layout 的首轮 Running capture 有事件 654–675，result-2 terminal capture 保留同一事件前缀并追加事件 676 `real.reconciliation_verified`；两个 capture 的 5 次写调用数一致。H09/regression/rebuild-stages-app.trx 为 52/52 通过，标准构建 H09/rebuild-fix-standard-build.log 为 0 警告/0 错误。H09/rebuild-fixed-final-fresh/ 于 18:48:50 链接的 persisted inventory 显示唯一 WDC 物理 ID/UniqueId/Serial，新的单成员池和 32 GiB Simple/Fixed 单列 VD；OS Disk 3 为 GPT，MSR 为 offset 1 MiB/size 16 MiB，BasicData 为 MAX、NTFS/65,536-byte clusters、W:。关联卷 `volume:uid:4af4466a800119cab017d384` 的 UniqueId 含 BasicData GUID，来源返回 NTFS、65,536-byte allocation unit、W: 与 `WinPool_Stage1_Data` 标签。H09/protected-rebuild-fixed/ 为 passed/unchanged、0 changes/0 gaps、4 scope adjustments，两块 Samsung 的四项身份均匹配。inventory freshness_asserted=false，比较 freshness_verified=false；比较只覆盖 Samsung 关联结构，不声称全机无变化，也不能排除 Windows 后台 I/O。完整检查点见 `runRoot/H09-final-closeout-checkpoint-20261007.md`；首次失败与旧 Running 记录保留。

H10 的分层 VD 与 HDD tier template 序列已完成。首次创建计划 `f5d37ac8-1df0-4ade-b2f4-48ebb3b83a54` 的后态曾因布局字段为空进入 OutcomeUnknown；Agent 启动时只读恢复为 Completed/Verified，`call_issued` 与 accepted 计数均保持 1，未重放。原 tier rename 计划 `beb40cde-0b00-4cd5-9fcb-de3f67958f47` 因 `tier-not-unique` 明确 NoWindowsCall；修复后 `0ef6a1ab-2bea-4b1f-b1a3-aa777fd91d88` 将准确 instance 改名为 `WinPool_Stage1_HDD_Rename`，Completed/Verified。旧非分层 VD、旧分层 VD 和 unused template 删除计划 `227a6c49-272f-4411-a334-aed08a7e6533`、`d1b286cd-0fd1-4ea7-ad42-7c82e07bb03f`、`2872b83c-54d1-4935-9726-b9f65768edc2` 均为 Completed/Verified；新 template `3bfb41ee-8d3f-4447-9bf1-c21ee941524b` 与新 tiered VD `f4ab4c88-b031-4c5c-a8c7-e85b1359ba96` 也各 1/1 Verified。最终 VD 为 16 GiB Simple/Fixed，tier instance 一列、65,536-byte interleave、1 copy/0 redundancy；H10 最终 inventory 的 Storage collection 和八个 MSFT Storage 类均 Returned。Samsung scoped comparison unchanged、0 changes/0 gaps、4 scope adjustments，不表示全机无变化，不能排除 Windows 后台 I/O；inventory helper 的 freshness_asserted=false，也不证明 ScanAsync 因果。C05 仍禁用：UIA 显示 Use maximum size disabled，Agent 回归继续拒绝 existing-object tier resize，未做写入。H10 相关最终回归为 251/251 layout、89/89 Agent、275/275 Backend/Adapter、312/312 Application、557/557 Infrastructure；早期 250/251 与 555/557 失败运行保留。相关标准构建日志均为 0 警告／0 错误。完整检查点见 `runRoot/H10-final-closeout-checkpoint-20261007.md`；H11 与 P6 最终门仍待完成，产品保持 V0.57。

自动回归结果按各次运行分别保留：首次综合回归 1,111 项中 1,107 通过、1 失败、3 跳过；唯一失败是旧 Agent 测试夹具，修正夹具后的 Agent 回归 84/84 通过（`H02/integrated-regression-summary.json`、`H02/agent-regression-corrected/`）。Infrastructure 首轮定点回归 313/313 通过；扩展回归 340 项为 339 通过、1 失败（严格 CLR 无符号数组类型检查），后续定点回归 62/62 通过（`H02/empty-gpt-regression/`、`H02/adapter-tier-regression.log`、`H02/tier-exact-type-regression/tier-exact-type-regression.trx`）。Infrastructure VDS 在线安全回归 366/366 通过，构建 0 警告、0 错误；离线修复 Backend 回归 137/137 通过，SafetyInspector 首轮 82 项中 66 通过、16 失败，中间 Infrastructure 回归 412/413，最终相关回归 413/413 通过（`H02/safety-vds-tests/infrastructure-vds-online-verified.trx`、`H02/vds-safety-build.log`、`H03/offline-fix-tests/`、`H03/offline-infrastructure-final.log`、`H03/offline-fix-tests/offline-infrastructure-final.trx`）。这些历史失败运行均保留。只读恢复 Agent 构建及恢复后的标准构建均为 0 警告、0 错误（`H03/offline-recovery/agent-build.log`、`H03/offline-recovery/standard-build.log`）。真实离线计划已由后续只读对账闭合，联机后哈希和 inventory 也已核对；H03 命名操作完成。H04 命名序列完成；H05 C01 已验证，C02 扩容容量/几何已核对但 ReFS 内容保留未验证，H11 待补同一 ReFS 卷扩容前后哈希及 Shrink 禁用 UIA。H06 清盘与建池均已核验；建池计划 `17024aab-0b0f-4cc3-853a-946b35ad719e` 从历史 `OutcomeUnknown` 通过只读恢复闭合，前后调用计数相同、不重放；相关 Infra 最终回归 530/530 通过，先前 10 失败和 8 失败运行保留。H07 完整命名序列已 Completed/Verified；H08 三项改名已 Completed/Verified，16 MiB 哈希匹配；C05 现有 VD 扩展仍禁用。H09 修复版 R5 分段重建已完成：四个独立冻结计划全部 Completed/Verified（删除旧对象、RAW 清盘、建池/VD/GPT、自动 MAX 布局）；最终 WDC 布局为 MSR 16 MiB + MAX NTFS/64 KiB BasicData/W:，Samsung scoped comparison 0/0。App 回归 52/52、标准构建 0/0。首次 preflight 失败和中间 Running 结果保留。H10 命名与 tier-template 生命周期已完成；首次 Unknown 由只读恢复闭合、未重放。完整证据见 `runRoot/H10-final-closeout-checkpoint-20261007.md`。H11 尚未闭合。当前 P6 与最终工程门尚未通过，产品仍为 V0.57。详细操作账本见 `runRoot/H03-operation-evidence-ledger-20261007-complete.md`、`runRoot/H05-final-closeout-checkpoint-20261007.md`、`runRoot/H06-final-closeout-checkpoint-20261007.md`、`runRoot/H07-final-closeout-checkpoint-20261007.md` 和 `runRoot/H09-final-closeout-checkpoint-20261007.md`；较早 H06 unknown checkpoint 和各次失败/Running 运行均保留。

### 当前收口状态

- **H05 C02：** 扩容容量/几何已核对；同一 ReFS 卷内容保留哈希和 Shrink 禁用 UIA 仍待 H11 补齐并清理候选。
- **H09：** 已完成。修复版流程以四个独立计划完成删除、精确 RAW 清盘、32 GiB Simple/Fixed VD/GPT 创建和自动 MAX NTFS/64 KiB/W: 布局；final inventory 确认新 ID，Samsung scoped comparison 0/0。原失败与 Running 记录保留，freshness/scope 限制见 H09 最终检查点。
- **H10：** tier rename、旧 VD/template 删除、新 template 与 tiered VD 创建均已 Completed/Verified；首次 Unknown 已经只读恢复闭合，C05 仍禁用。检查点见 `runRoot/H10-final-closeout-checkpoint-20261007.md`。
- **H11：** 仍在进行；C02 ReFS 内容保留哈希、Shrink 禁用 UIA、最终 WDC 末态、保护盘核对、普通 App 重启重读和 P6 最终工程门仍待完成。
- **阶段与版本：** P6 仍在执行，现有证据不能支持全阶段通过或升级版本；当前产品为 V0.57。
## 2026-10-06 卷标核对、进程存活与退出／重建修复

用户在标准管理员 WinPool 手动将 WDC E: 卷标改为 `WP_ADMIN_1006`，操作 `fe551baa-75d2-4640-a6d1-9ee711ea6630`。Provider 成功证据已持久化，但 Volume 匹配错误要求卷自身具有父分区 GUID，导致后态未验证、OutcomeUnknown 屏障持续，完整退出与 rebuild 被拒绝。修复区分卷自身身份与父分区链，新增单步 RenameVolume 的严格只读恢复；其它命令、多步骤及证据不足仍保留屏障。

直接 Release 回归 **51 passed、0 failed、0 skipped**，覆盖真实 Volume 不含 Guid 的后态、父链／几何／身份拒绝、只读恢复成功与不完整证据拒绝；恢复用例断言写适配器调用数为 0。独立修复版 Agent 构建 0 警告／0 错误。构建 guard 保持原阻断条件与只读数据库访问，增加明确 OperationId／状态诊断。

用户批准本次受控切换后，修复版 Agent 通过既有事务将原操作更新为 Completed、步骤 Verified。冻结计划哈希保持 `b189d3bf77ffaa9ed4649fb9b955bf6077fbd65f64bde762b6d45111161e32d4`，未新增 accepted／call_issued 事件，只新增结果核对事件。当前后态、物理成员及安全事实已核对；E: NTFS 簇仍为 4 KiB，卷标保持用户测试值，没有重新改名或格式化。修复版 Agent 通过托盘正常完整退出，标准运行树的空闲 guard 通过。原核心库已一致性备份，完整前后记录、TRX 和日志保存在 `artifacts/test-results/20261006-rename-verification-repair-7dacef301b064bb98e070540c8b1483c/`。完整 H00–H11 设备验收仍未完成，产品仍为 V0.57。

原生退出复测发现两处额外缺陷：进程核验没有检查存活，保留已退出进程句柄时仍可误接受原映像／启动时间；主窗口正常关闭遗漏所属欢迎窗，独立 `--page Welcome` 入口也未启动 Agent 退出监听。前者以退出码 0／259 的真实 Windows 子进程回归证明并修复；后者将欢迎窗关闭放在主窗口清理的任何 await 之前，并在全部启动目标分支前注册既有退出监听。

最终标准运行树 rebuild 为 `standard-rebuild-r3.log`，restore 成功、零警告／零错误。原生 `Normal`（主窗与欢迎窗同时打开）和 `StandaloneWelcome` 两项均由一次托盘退出完成，验收程序刻意保留 App／Agent 进程句柄直到检查结束，两个进程均退出码 0、无强制终止。证据为 `native-exit-r3-Normal.json`、`native-exit-r3-StandaloneWelcome.json` 和对应 windows 清单；管理员令牌核对见 `native-welcome-r3-elevation.json`。完整退出的最终结果以这两项为准，不将中间残留状态算作通过。

最终 R3 全解回归为 **970 passed、0 failed、3 skipped**，覆盖 12 个测试项目，guard 8/8、保留进程句柄回归 2/2；3 项跳过仍为既有大型监控归档测量。日志与 TRX 为 `final-tests-r3.log`、`final-regression-r3/`，汇总为 `automated-summary-r3.json`。`native-and-storage-final-summary.json` 确认两个原生 Agent 会话均 clean、无未终结真实操作、没有新增真实计划或写调用。文档本地链接与差异检查通过。

中间构建、回归和失败退出证据保留。首次 guard 定点回归 6 passed／2 failed：一项坏 ID 夹具受外键阻止，已修正专用临时连接；另一项 pristine 数据根夹具因当时 WinPool 活实例而正确被拒绝。生产阻断条件未放宽；本次恢复仅通过 Agent 既有事务更新准确操作记录，没有手工 SQL 清除屏障。

## 2026-10-06 早前 P5 续执行基线

以下结果限定于修复提交 `747b71e` 对应的早前验证时点；后续用户卷标写入、管理员 CLI 及恢复结果以上一节为准。

修复提交 `747b71e` 的稳定代码通过标准 Release 工程门：restore 成功，构建 0 警告／0 错误，12 个测试项目 **929 passed、0 failed、3 skipped**；跳过项仍是既有大型监控归档测量。23 项目直接／传递依赖审计未列出已知漏洞，`git diff --check` 通过。最终证据位于 `artifacts/test-results/20261006-real-edit-stage1/prewrite-gate-95f61ad60f3046c0ada0430c3900d7f8/`，以 `build-stable.log`、`tests.log`、`automated-summary-final.json`、`dependencies.json` 为准。前两份构建日志保留为实现中间态。直接回归另覆盖持久本机身份与新鲜事实、换盘／外机／缺事实拒绝、执行及未知状态禁用、准确计划取消、旧请求释放竞争、Agent 重启恢复屏障；使用替身与临时 SQLite，不调用物理写适配器。

标准 App 普通权限启动后，真实编辑开关为 Off；选中 WDC 的 E: 时格式化、删除、扩展、压缩仍禁用，查询／停止后续步骤可见。UIA 证据为 `native-normal-start-uia.txt`。本次启动时间段 Application 日志完成查询，4 条事件中没有相关崩溃来源或 WinPool／历史签名匹配；证据为 `native-normal-crash-summary.json`，不据此宣称历史 E_POINTER 根因已修复。App 正常关闭，空闲 Agent 经准确路径核对后退出。生产核心库仍只有 40 个既有模拟记录，真实准备及接受均为 0，见 `native-no-real-plans.json`。

该验证时点工具进程不是管理员，管理员 App→Agent 准备／取消尚未验证。2026-09-28 的管理员固定只读安全探针曾通过，但写前必须重采；raw provider 身份的独立 planner 探针不等于实际 App→Agent 冻结计划。当时 P0/P5 与 H00–H11 未闭合，尚无真实磁盘写入，产品保持 V0.57，V0.58 仍为目标。

## 2026-09-27 单盘真实修改第一阶段：写入前进度

当前仍在 [Plan](Plan.md) 的 P0 写入前准入及后续代码集成期，产品版本为 V0.57，V0.58 只是目标。已提交的 IPC 12、schema 18、Agent 持久状态与身份门、Windows 封闭适配及两页单盘入口已通过本阶段最终自动工程门：Release restore 成功，标准 Release 全解构建 0 警告、0 错误；全解测试 896 passed、0 failed，另有 3 项既有大型监控归档测量按门控 skipped；直接和传递依赖审计覆盖 23 项目，未报告已知漏洞。App.Tests 15/15、Agent.Tests 67/67、Agent.Client.Tests 22/22、IPC.Tests 9/9 均包含在 896 项内。TRX、构建日志及审计结果位于 `artifacts/test-results/20260927-real-edit-stage1/final-gate-localized/`。这些自动结果只证明测试夹具覆盖的计划、IPC／状态、适配器和 UI 提案行为；P5 无写原生准入及 P6 实机用例尚未完成，不能沿用上一轮 V0.57 的 780 项回归充当本阶段验收。

P0 的固定只读拓扑已采集，但非提权探针的 BitLocker WMI 查询被拒绝，A 类逐命令支持性及 C 类现场能力仍缺完整证据。未执行真实磁盘写入，也未验证单盘创建、删除、重建或后置数据完整性。C01–C05 只有取得适用能力证据的分支才可进入实测；C04/C05 当前保持禁用，D01–D05 真实入口保持禁用。任何实际写入仍需重新采集并对准确操作与目标逐项授权，自动测试和只读采集都不能代替该证据。

有限无写原生复核在 480 DIP 英文存储结构页属性视图确认两个自动创建开关均显示 `On`，真实模式开关处于关闭状态；截图为 `artifacts/test-results/20260927-real-edit-stage1/final-gate-localized/native-480-en-storage-properties-real-off.png`。这只覆盖该窄宽文案与可见性，不代表 P5 完整原生准入。

2026-09-27 P5 崩溃证据只读核验：本机 Application 日志查询范围为当日 00:00 至 23:45（查询截止时），共 348 条事件；其中 `Application Error`、`.NET Runtime`、`Windows Error Reporting` 三个相关来源共 26 条。`WinPool.App`、`WinPool.Agent`、`combase.dll`、`E_POINTER` 均未匹配到事件。扫描汇总保存在 `artifacts/test-results/20260927-real-edit-stage1/final-gate-localized/p5-application-eventlog-summary.json`。这只说明本轮没有对应的新崩溃记录；历史 `combase.dll / E_POINTER` 根因仍未确认，P5 无写原生准入仍未完成。

## 2026-09-27 V0.57 缺陷收口

基线 `3739691` 加本轮修复；最终 Release 全量回归 780 passed、0 failed、3 skipped，标准构建 0 警告/0 错误，直接及间接依赖漏洞审计覆盖 22 项目且无已知漏洞报告。3 项跳过为原有手工大规模归档测量，不记为通过。完整 TRX、构建日志及汇总位于 `artifacts/test-results/20260927-closeout-final-r3`；依赖审计位于 `20260927-closeout-final/dependency-audit.json`。

有限原生检查覆盖中英文 480 DIP 导航、900/1100 DIP 标题栏、模式/语言切换及导航名称恢复、欢迎内容、设置名称、只读刷新和停止后导出；CSV 为 18,936 行。两轮启动/退出检查未发现新 WinPool 崩溃事件，历史 `combase.dll / E_POINTER` 根因未确认。故障夹具、脚本保全、早期失败记录和未验证范围见[收口归档](Archive/20260927-v057-closeout/README.md)。真实存储写入未运行。

## 2026-09-23 界面分隔与消息表面：定点验证

关闭已核实位于标准 `artifacts/Release` 的 WinPool App / Agent 后，执行 `dotnet build WinPool.slnx -c Release --no-restore -m:1`，最终结果 0 警告、0 错误，产物直接重建于标准运行目录。未另建隔离运行树。本轮为可逆界面调整，未扩成全套测试。

最终标准 App 原生启动并截图核对：`settings.png` 中标签列收紧、主题下拉不拉满、路径／7Z 下拉与图标按钮紧邻，标题栏系统选择器变窄；`storage-structure.png` 与 `disk-partition.png` 中右栏紧邻单个 8 DIP 分隔槽；`development-detail.png` 中消息列表无表头、详情文本背景不透底；硬件只读刷新时的 `notification.png` 中右下 InfoBar 背景为实色。图片均在 `artifacts/test-results/20260923-ui-feedback-final/`。UIA 读到设置页两个路径下拉宽约 188 DIP、相邻图标按钮各 32 DIP；开发页消息条目仍为 28 DIP。只读刷新没有提交存储编辑。

当前 `TestPage.xaml` 只有静态规划说明，没有子块或分隔槽；用户所说“测试页双倍分隔”无法对应当前源码，已请求位置确认，本轮未擅自改动其他页面。未做主题／DPI／最窄窗口全矩阵，也未把这些项目记为通过。

## 2026-09-23 控件角色尺寸与分隔槽：验证结果

标准 Release App 原生启动成功，UIA 读到存储结构编辑页右栏宽 319 DIP（布局取整后约为设计值 320 DIP）、分隔槽宽 8 DIP，拓扑区及操作区滚动容器可见。桌面前台保护多次拒绝拖动；单次工具报告成功后页面和尺寸观测不一致，故**拖拽效果不计为已验证**。480 DIP 窄窗、监控／管理／开发页及分区编辑页本轮未完成原生核对。截图文件生成但画面全黑，不能作为视觉证据；证据目录为 `artifacts/test-results/control-size-final-20260923/evidence/`。测试后恢复原 StorageStructure 页与 1440×900 窗口，只读确认设置未变；App 正常关闭、Agent 随后退出，最终无 WinPool App/Agent 进程，未执行存储操作。

按用户最新要求执行项目标准 `dotnet build WinPool.slnx -c Release --no-restore -m:1`，直接重建 `artifacts/Release`：exit 0、0 warning、0 error，App/Agent 的 `.deps.json`、`.runtimeconfig.json` 与 App `.pri` 五个启动文件均在运行树。Architecture 项目全量单次回归 47/47 passed、0 failed、0 skipped；TRX 位于 `artifacts/test-results/control-size-final-20260923/architecture-control-size-final.trx`。定点编辑页布局检查也曾 1/1 通过；完整回归额外发现标题栏交互容器的旧静态断言，已改为与当前 `ActiveSystemSelectorHost` 实现一致，复跑通过。`git diff --check` exit 0，目标 XAML 均通过静态 XML 解析。

此前两次隔离编译虽然各自 0 错误，其合并运行树均缺上述五个文件，因此不能作为原生运行证据；第一次直接启动在进入界面前崩溃。缺失文件实际被生成到默认 `artifacts/trees/Release`，与覆盖运行树路径的结果分离；失败日志保留在 `artifacts/verification-control-size-20260923-195807-10b39158/`，完整隔离树的定点补齐记录在 `artifacts/verification-control-size-r2-20260923-202038-7c75851b/`。本轮最终以用户指定的标准 Release 重建结果为准，不把隔离尝试算作有效界面验证。

## 2026-09-23 七项界面反馈：验证结果

最终隔离构建位于 `artifacts/verification-ui-feedback-20260923-responsive-8238ee77/`，App/Agent Release 编译 exit 0、0 警告、0 错误，运行树合并 288 shared／294 App-only／10 Agent-only／0 collisions；直接相关的 Architecture 两项 2/2 passed、0 failed、0 skipped，TRX、命令和日志保留在该目录。已有 `artifacts/Release` 未作为构建目标。第一次隔离构建在 `artifacts/verification-ui-feedback-20260923-a2225ec1/` 通过编译和开发页定点测试，但原生检查发现分区属性栏 320 DIP 时长 MiB 数字使右侧单位被裁，原始画面在该目录的 `evidence/partition-before-width-fix.png`，UIA 读到两个单位均 offscreen。第二次隔离构建 `artifacts/verification-ui-feedback-20260923-final-1b28b26b/` 改为 420 DIP 后，同一对象的完整单位可见；随后独立审查发现固定 420 DIP 会挤占最小窗口的拓扑视口，因此最终改为随窗口宽度在 320–420 DIP 之间调整，并为窄窗右栏增加横向滚动。前两次结果均未冒充最终通过。

原生界面：开发页四列表头依次为时间、级别、来源、信息标题，标题列实际显示“采集成功”，没有拼接正文；消息列表行 UIA 为 28 DIP，文字纵向位于中间。双击详情后，唯一只读 TextBox 位于页面中央，实色主题背景与暗色遮罩可从 `evidence/development-detail.png` 区分；截图中框内左右空白采样同为 RGB(28,28,28)，框外为 RGB(21,21,21)，未出现底层左右区域透出。分区页复验本机 C: 时，起终点的 MiB 数字分别处于同一列，括号内数字另处一列；`952,664.54199219MiB (930.34 GiB)` 的两个单位都在宽窗 420 DIP 栏内，截图 `evidence/partition-aligned-complete.png`。选择模拟空隙后，起点 `593,937MiB (580.02 GiB)`、终点 `1,907,348MiB (1.82 TiB)` 分列，第一行容量 `1313411` MiB、第二行 `1.25 TiB` 用普通字号显示，截图 `evidence/partition-gap-capacity.png`。窗口缩至 900 DIP 时右栏仍为 420 DIP，两个单位可见，见 `evidence/partition-900.png`；缩至最小 480 DIP 时右栏为 320 DIP、左侧拓扑视口约 142 DIP，与旧布局相同，横向滚到右端后两个单位可见，见 `evidence/partition-480.png`、`evidence/partition-480-scrolled-end.png`。本轮仅选择对象，未提交创建或格式化。

UIA 读到系统自绘标题栏和界面标题行均高 48 DIP。左侧标题区域从 (1080,294) 到 (1140,294)、以及从 (1080,310) 到 (1140,310) 的两次真实鼠标拖动，各使 1440×900 窗口水平移动 60 像素；每次都拖回初始 (1000,270)。导航、可见的系统选择器容器、真实编辑控件和系统窗口按钮是 Passthrough／系统按钮区域，不能作为拖动区。App 全局与结构／分区页显式 Button 样式都设置 4 DIP 圆角，管理和分区页截图已看；未逐一遍历所有页面、主题与 DPI。实际屏幕取证使用 `winapp ui screenshot --capture-screen --focus`，本次返回可辨认画面。测试后恢复原来的本机系统和管理页，并退出经路径核实的隔离 App/Agent；没有修改真实存储。

## 2026-09-23 通知卡透明余高修正

基线为 `40d8a9b` 加本轮修改。移除 `NotificationCard.xaml` 外层 `MinHeight=72`，让 384 DIP 宽的卡片高度跟随单层 InfoBar 内容；200 DIP 最大高度、堆叠间距和向右离场规则未修改。更新旧的架构断言后，`NotificationShellKeepsThreeSimpleCardsAndDeveloperOnlyDetails` 定点测试 1/1 passed；隔离 App/Agent Release 构建 exit 0、0 warnings、0 errors，合并 288 shared／294 App-only／10 Agent-only／0 collisions。有效命令、日志、TRX 和运行树位于 `artifacts/verification-notification-card-height-20260923-r2/`；既有 `artifacts/Release` 的 592 个文件哈希及时间戳未变。首次 r1 命令因工作目录错误在编译前退出，记录已保留；r2 编译的项目库和测试中间输出仍使用共享 `artifacts/build/<项目>/Release`，没有将其当作完全隔离。

当时用户运行的 `artifacts/Release` App/Agent 是旧版本，未为了该次小幅布局修正中断。因此新卡片的原生视觉高度与离场后占位未验证；该轮通过范围为源码、定点架构测试和隔离编译。此前 UIA 读到的短卡 InfoBar 高度约 53，但当时外层仍有 72 DIP 下限，不能作为修复后的原生证据。

## 2026-09-23 上一轮 13 项界面反馈：验证结果

基线为 `5424902` 加本轮修改。最终 Architecture 47/47 passed、0 failed、0 skipped；`WinPool.slnx` 隔离 Release 构建 exit 0、0 warnings、0 errors，App/Agent 合并为 288 shared／294 App-only／10 Agent-only／0 collisions。TRX、实际构建命令与日志位于 `artifacts/verification-20260923-final-integration-5c02b9a7/`；合并产物位于其 `Release/`，共 592 文件。保护目录 `artifacts/Release` 的 App EXE 哈希和时间在构建前后相同。自动测试不代替原生界面检查。

原生 App 与 Agent 从该隔离 Release 目录运行，使用现有标准数据根，不执行模拟创建、格式化或真实存储写入。开发页横向、纵向分隔条均实际拖动，消息区 UIA 边界由 746×525 变为 860×461；两条消息以单行列出，列表及区域自动化名称均为“消息列表”，页面没有可见标题。悬停第一条后点击，UIA 读到 `IsSelected=True`；双击后唯一只读 `MessageDetailText` 边界为 640×171，中心与内容区中心一致，点击框外后控件消失。实际 Ctrl+C、长文本滚动和悬停/选中颜色视觉未核对。

分区边界模拟系统选择可见间隙时，UIA 读到“起点” `593,937MiB (580.02 GiB)`、“终点” `1,907,348MiB (1.82 TiB)`、默认容量 1313411 MiB，第二行 `1.25 TiB`；第一行输入、MiB、MAX 和第二行换算值的边界分行清楚。该系统只出现一段大未分配空间；恢复的小间隙阈值源码核对通过。选间隙时唯一 `PartitionActionButton` 为“新建分区”，选已有 C: 时切为“格式化分区”。管理页下区 UIA 见池仅 1 个编辑按钮、磁盘仅编辑及系统属性 2 个、分区指定 4 个；本机磁盘编辑实际进入分区页。层分类在本次样例中无条目，按钮未原生核对；入池物理盘路由按分区拓扑可见投影修正，未另造样例进行原生触发。

自动本机只读刷新期间，UIA 见一张宽 384 的通知 Group，只有一个 `NotificationInfoBar` 子控件；XAML 已移除外层 Border。截图工具的窗口捕获返回全黑 4 KB PNG，屏幕区域捕获包含其它应用而非 WinPool 窗口，均保存在同一验证目录 `evidence/`，不能作为卡片单层外观、暗色遮罩或选中颜色的视觉证据。通知右退场、不同文本高度和减少动态效果本轮仍 `unverified`。测试后恢复原系统 `[模拟] 其它与网络` 和硬件页，并退出隔离 App/Agent；未清理数据。

## 2026-09-23 上一轮 21 项要求：历史验证结果

以下以 `1d94d2e` 加上一轮源码为基线的回归、构建和原生验收，只适用于上一轮 21 项要求，不覆盖本轮改动。Release 自动回归：Application 291/291、Infrastructure 93/93、Architecture 47/47；Persistence 130 passed、3 个既有大型监控测量按门控 skipped，均 0 failed。新增启动只读读取器的子代理定点 Debug 回归 7/7 passed。TRX 位于 `artifacts/verification-20260923-final-r1/test-results`。Architecture 首轮 46/47：旧断言要求先呈现默认系统，已改为检查预显顺序，R2 47/47；Infrastructure 首轮 90/93：三个格式化测试夹具用非整数 MiB 创建分区，与新规则冲突，改为有效的 500 MiB 后 R2 93/93。首轮失败 TRX 均保留。新增分区几何回归还覆盖大小写不同的磁盘关联与删除中间分区后编号唯一。隔离 App/Agent Release 构建见同一验证目录的 `release-build.log`，exit 0、0 警告、0 错误，运行树在 `artifacts/verification-20260923-final-r1/Release`，合并 288 shared／294 App-only／10 Agent-only／0 collisions；未替换 `artifacts/Release`。

有限原生核对在上述隔离 Release 运行树和现有标准数据根进行，没有清理数据。关闭 Agent 后冷启动：计时脚本约 663 ms 找到主窗口、约 1883 ms 读到 `[模拟] 分区边界`，当时标题栏选择器可见但禁用，说明 Agent 完成前已呈现上次系统；随后选择器启用。该计时包含 WinApp CLI 调用开销，不能作为准确绘制时延或逐帧“零闪烁”证明。重新启动后 UIA 与 `evidence/restart-partition-simulation.png` 核对上次分区页和模拟系统。测试结束将原选择恢复为本机系统／开发页，下一次启动再核对读回；App 和隔离 Agent 均已退出。

开发页 `evidence/startup-restored-development.png` 显示左上单行日志、右上空区及下方提示；第二次双击日志后 `evidence/development-log-detail-r2.png` 显示仅有文本框的详情浮层，UIA 读到 `IsReadOnly=True` 和完整内容，点击下方空区后该编辑控件消失。未实际改动剪贴板，Ctrl+C 复制动作本轮 `unverified`。分区页 `evidence/partition-gap-mib-max.png` 显示独立的右侧“新建／格式化”、固定 MiB 输入后缀、第二行自动单位及图标式最大值；选择 MSR 后空隙时 UIA 读到起点 `17MiB (17 MiB)`、终点 `953674MiB (931.32 GiB)`、默认容量 `953657` MiB，改为 100 MiB 后按最大值恢复 `953657` MiB。选择磁盘头部 1 MiB 空隙时“新建”禁用，UIA HelpText 为“起点按 1 MiB 对齐后，剩余空间不足 1 MiB。”；未提交模拟新建或格式化。成功通知卡曾在 UIA 中显示 384×72 DIP；长短内容的高度变化及向右退场画面本轮 `unverified`。设计表及归档做了内容、链接和范围检查；图标及样式 HTML 在本机 Edge 呈现后的截图为 `evidence/buttons-table.png` 和 `evidence/styles-table.png`，图形与色块可见。真实存储写入未执行。

下面“最近已记录的验证基线”及其后各段只说明此前对应提交/基线的范围，不自动覆盖本轮变化。

## 最近已记录的验证基线

2026-09-23 系统 JSON、格式化方式、通知卡和开发页本轮：基线 `87c4f6e` 后的本轮改动。定点 Release 回归为 Application `V049PartitionSemanticsTests` 15/15、Infrastructure 格式协调与命令预览 28/28、Architecture 47/47，均 exit 0、0 failed/0 skipped；TRX 和日志在 `artifacts/verification-20260923-112235`。导入 JSON 嵌套形状补充检查的 Application 定点 Debug 回归另为 3/3 passed、0 failed（终端结果，未另存 TRX）；它只验证该校验器，不替代系统导入原生往返。首次 Infrastructure 因新增计划参数 bool 与文本辅助方法类型不匹配而编译失败；首次 Architecture 44/47，三项旧断言仍要求已移除的开发页路线标题和固定取三卡写法。修正类型并把断言更新为当前导航门、内存日志、三区域和卡片边界后，Infrastructure 与 Architecture 分别在 R2 得到上述通过结果。没有把首轮失败覆盖或记为通过。

第一次隔离 Release 构建因格式开关 UIA 命名遗漏 `Microsoft.UI.Xaml.Automation` 引用而失败，同轮还报告 XAML 类型解析错误；失败日志保留于上述验证目录。补齐引用后在全新 `artifacts/verification-build-r2-20260923-113438/Release` 完整构建 exit 0、0 警告、0 错误，表明该轮已无上述 XAML 诊断；App/Agent 合并为 288 shared／294 App-only／10 Agent-only／0 collisions。导入形状校验器接入 App 后又进行一次隔离 WinUI App Release 编译，`artifacts/verification-20260923-import-json-app-release-r1/logs/app-release-build.log` 记录 exit 0、0 警告、0 错误，合并报告仍为 288／294／10／0；目标执行前确认不存在。所有构建均未替换 `artifacts/Release`。本轮未运行全解决方案测试、真实存储写入或完整设备/DPI/高对比度矩阵；有限原生交互结果如下。

有限原生核对使用上述隔离运行树及现有标准数据根，未清理或迁移数据。主代理查看 `evidence/development-page.png`：无大标题，左上日志/右上空区/下方提示卡三块位置与边界正确；双击现有本次运行条目弹同窗只读详情，内容和复制结果由 UIA/剪贴板核对，测试后恢复原剪贴板。主代理查看 `format-full-on.png` 与 `format-quick-restored.png`：完整开启时快速关闭、恢复快速时完整关闭，两个开关的中文 UIA Name 和模拟说明可读；未点击格式化。保存选择器截图显示 `JSON (*.json)`；实际导出 `evidence/WinPool-SystemExport.json` 能由标准 JSON 解析，扩展名 `.json`、`Product=WinPool`、外层/内层 schema 均为 3、系统类型 Simulation。仅在本地保留原始系统数据，不公开。测试后 LastActivePage 恢复 StorageStructure，DeveloperMode、主题及语言保持原值，App/Agent 均正常退出。

通知卡固定尺寸与向右退场已有源码、WinUI 元数据及编译核对，原生画面为 `unverified`。一次 15 秒 WinPool 单窗口 gdigrab 录屏正常封装 438 帧，但提取帧为全黑，不能作为卡片出现或动画证据；原视频、日志和黑帧留在同一 evidence 目录，不重录也不把黑帧报成通过。录制期间再次导出的模拟系统 JSON 可解析，未提交格式化或真实存储修改。完整中英文长消息、减少动态效果、480×300 窄高窗口卡片堆叠、点击／超时／挤出动画、完整 DPI/高对比度矩阵保持 `unverified`。

2026-09-22 系统盘删除与模拟扩缩修正：用户回报的“同盘分区全部不能删除”和“扩展／压缩无效”经只读审计确认为实现缺陷。修正后 Application 278/278、Infrastructure 90/90、Architecture 47/47 全通过，TRX 位于 `artifacts/v055-delete-resize-fix-20260922-r1/test-results`，主代理已核对；完整隔离 Release 构建写入 `artifacts/v055-delete-resize-fix-20260922-r1/Release`，0 警告、0 错误，并集合并 288 shared／294 App-only／10 Agent-only／0 collisions。首轮隔离命令漏写路径尾部分隔符导致合并目标落错位置，该轮构建失败未被当作通过；修正后重新构建取得明确成功，误建的两个中间树已按文件处置规则移入项目根 Rubbish。修正后的运行树已替换为用户原先使用的 `artifacts/Release`（592 文件），替换前旧树完整保留在 `Rubbish/20260922_release-before-delete-resize-fix/Program/WinPool/artifacts/Release`。本轮原生扩缩与内置模拟删除仍为 `deferred_by_user`，未做真实存储修改。

2026-09-22 更正：下述反馈修复只是已取得的局部证据，上轮据此归档过早，不能代表用户 15 项全部完成。当前已重开 [Plan](Plan.md)，逐项补查实现遗漏及原生验证缺口；既有通过记录仍保留，未验证项不自动转为完成。

本轮代码补齐后：Application 272/272、Infrastructure 88/88、Architecture 47/47 全通过，TRX 位于 `artifacts/v055-feedback-completion-20260922-r1/test-results`，主代理已核对。最终完整隔离 Release 构建为 `artifacts/v055-feedback-completion-20260922-r2/Release`，exit 0、0 警告、0 错误；r1 构建会话没有最终退出状态，保留 unverified，之后在新 r2 目录构建取得明确结果，未重复测试。原运行目录未覆盖。模拟扩缩的几何、方向文件系统、容量差值、只读门控及持久化偏好回归已覆盖；扩缩容与容量重置的原生操作按用户决定为 `deferred_by_user`，不计为通过。

补验已确认：当时用户运行的 `artifacts/Release` 普通实例上，普通成功卡出现后 422 ms 真鼠标点击，同一调用内 UIA 与截图证实移除；动态“删除模拟系统”和静态“格式化”禁用按钮真实悬停均显示具体原因。主代理实际查看前后图和 tooltip popup，证据保存于 `artifacts/v055-feedback-completion-20260922/evidence`。该实例不是已消失的 r4 临时树，不能混写构建身份；本轮四处用途提示新增代码和模拟扩缩容恢复已通过上述统一构建，但不因此冒充原生验证。

管理员补验：同一当前 Release 的提升实例 PID 11748 实际打开模拟导入“打开”和导出“另存为”窗口；主代理查看 `admin-open-picker-open-20260922-120027936.png`、`admin-save-picker-open-20260922-120027936.png`，并核对 cleanup JSON 的两个 `Cancelled=true`。两窗口均已取消，未导入数据或保存文件；此证据只覆盖管理员窗口打开/取消，不代表管理员文件往返。此前辅助脚本的中文编码、导航匹配和误判同进程文件窗口造成数次自动化失败，不记为产品失败或通过。原“单盘池／磁盘分区编辑”已恢复，主代理核对读回 JSON 和 `admin-restored-singlepool-20260922-120224378.png`，App/Agent 保持运行。

2026-09-22 V0.55 人工反馈修复完成实现及有限验证：Application 首轮 265 项中 264 passed、1 failed（新增用例误用 `Single()`，不是产品实现失败）；修正测试夹具后，受影响用例及通知测试 9/9 passed。Infrastructure 86/86 passed。Architecture 首轮 46/47 passed，磁盘改名入口绕过投影的新增代码已修正，失败守卫定点复测 1/1 passed，未放宽原断言。主代理已核对 TRX 与相关代码，不重复执行已通过测试；证据位于本地 `artifacts/v055-feedback-repair-20260922-r1/test-results`，详细范围见[反馈修复归档](Archive/20260922-feedback-repair/README.md)。

最终完整隔离 Release 构建为 `artifacts/v055-feedback-repair-20260922-r4/Release`，exit 0、0 警告/0 错误。首轮发现 WinUI Border 不可继承，已通过项目 WinMD 元数据核验改用可继承的 Grid；第二轮开发页遗漏命名空间引用已修复；r3 未取得最终退出状态，保持 unverified，不能作为最终构建证据。各轮输出原位保留，未替换原运行树。

本轮有限原生通过：普通进程模拟导出→导入新副本→仅删除该副本、简洁错误卡与真实鼠标点击消息对话框、可用动态按钮用途悬停、两个自动创建偏好跨模拟系统保持并恢复。主代理实际查看有效截图。普通卡前后截图相隔 23 秒，不能区分点击和自然超时；禁用按钮悬停被前台限制及窗口遮挡影响；两项保持 unverified。管理员选择器、重启偏好、删除内置后重启及完整主题/DPI矩阵未原生验证。原有模拟系统未删除，真实存储未修改。

2026-09-22 V0.55 通知与轻量消息阶段收口：基线 `d304942` 加本轮改动，限定 Application 回归 60 passed、0 failed、0 skipped；Architecture 47/47 passed；完整隔离 Release 构建 0 警告/0 错误。Application 测试编译曾有一条 xUnit2031 风格警告，不影响测试结果。主代理已审阅代码、TRX 和截图；未重复执行子代理已通过的检查，不代表全套回归或完整设备验收。命令与本地证据见[阶段归档](Archive/20260922-information-system/README.md)。

有限原生验证通过：实际 Diagnostics 路径复制、选中消息详情复制、清空、开发者导航门开关与恢复、中英文切换与恢复、深色系统主题、上下文帮助和 900×900 窄窗。测试实例正常退出，偏好恢复为 System 主题/SystemDefault 语言/DeveloperMode=true/Settings 页面；隔离 Agent 经路径核实后停止。未迁移或清理数据，未修改真实存储。超量通知、重要错误和悬停/焦点计时未原生注入验证；服务行为测试及源码检查不能替代这些原生交互证据。完整 DPI/高对比度矩阵、重启后历史清空、数据根切换保持 `unverified`。开发页首屏的“复制全部/清空消息”需滚动至上方内容区域下部，作为已知布局限制保留。

本机 WinApp CLI 0.6.1 默认 WGC 路径返回全零帧，且第五次重试后仍报告成功；默认截图不可作为通过证据。同窗口 `--capture-screen` 截图正常，已实际查看。后续在此前提下使用屏幕区域截图并确认前台无遮挡；先核对本机 `--help`，不假设较新技能中的 `ui yield`、`find-api` 已可用。

2026-09-21 V0.54 设置页小幅调整：7Z 同宽对齐、删除浏览按钮并显示路径、选择自定义调起选择器、MSR 简称及版本更新。按用户要求不重跑自动测试或原生验收，只执行一次隔离编译，产物位于 `artifacts/settings-tools-v054/Release`，未替换现有运行树。构建执行未返回最终退出码/汇总，完成状态为 `unverified`，不以产物存在代替通过；未为取得结果重复编译。此前设置页验证仅覆盖下述初稿，不视为 V0.54 最终界面验证。

2026-09-21 设置页工具布局初稿：基线 `600dc25` 加当时改动，隔离 App/Agent Release 构建 0 警告/0 错误；`WinPool.Application.Tests` 中 `AgentPreferencesReloadCoordinatorTests` 3/3 passed（`dotnet test -c Release --no-restore --filter FullyQualifiedName~AgentPreferencesReloadCoordinatorTests --maxcpucount:1 -m:1`）。结果保留于执行终端，未另存 TRX；不是全套回归。主代理核对初稿 XAML 中 9 个 Button 均包含图标，这是源码检查，不代替视觉验收。

有限 WinApp CLI / UIA 验证覆盖卡片顺序及控件存在、7Z 默认/自定义选项、调起 EXE 选择器、取消后保持默认且 Agent 偏好仍为空。测试使用隔离程序树、现有标准数据根，未迁移或清理数据，App/Agent 正常退出。自定义成功保存、两处 Explorer 实际打开目标、中英文窄窗及完整视觉为 `unverified`；`artifacts/settings-tools-check/evidence/` 中截图全黑或被遮挡，不能作为视觉通过证据。

首轮隔离构建遗漏最终合并命令覆盖，部分清理了旧运行目录的 17 个资源。旧目录已保留于项目根 `Rubbish/20260921_settings-build-recovery/Program/WinPool/artifacts/Release`，完整运行树恢复后与隔离树逐文件 SHA-256 比较一致（591 文件）；最终更新在 App/Agent 退出后执行。标准 AppData 数据不在该清理范围。构建隔离注意事项已记入 Development。

2026-09-21 监控轮换阶段：基线 `92047b9` 加本轮实现，最终完整 Release 测试 655 passed、0 failed、3 个门控测量 NotExecuted；完整 Release 构建 0 警告/0 错误，22 项目依赖审计无已知漏洞包。最终日志与 11 份 TRX 位于本地 `artifacts/test-results/20260921-full-gate-runtime-closeout-final/`。三个跳过的测量已分别执行，包括真实 1 GiB 阈值轮换及压缩期间新活动库继续写入、原始参数测量和两组压缩参数比较；不是把跳过当作通过。

有限原生 WinApp CLI 验证覆盖正常运行/停止、页面重入同会话时长、归档失败关闭后新失败重现、恢复默认 7Z 后成功隐藏、中英文窄窗单行异常、7Z 设置及托盘正常退出。双库/归档往返使用生产迁移器和真实 SQLite/7z，quiesce 为接口替身；不冒充原生设置迁移验证。实际 DPI/主题全矩阵、跨 Windows 会话、强制断联未知状态注入和本轮 UAC 全流程未重跑，保持 `unverified`；未做真实存储结构修改。详见[阶段记录](Archive/20260921-monitoring-rotation/README.md)。

2026-09-16 统一层模拟池/模拟层阶段记录：Release 613/613 测试通过，完整构建 0 警告/0 错误，依赖审计无已知漏洞包；实现提交 `9e15f54`，文档归档断言同步提交 `98f69b7`。有限原生验证覆盖中英文、选择、空容量及无来源弹窗；完整主题/DPI/拖放和生命周期退出未获完整验证。详见[阶段归档](Archive/20260916-unified-synthetic/README.md)及[整理前验证记录](Archive/20260916-docs-review/Quality-before.md)。

以上是已有证据，不代表后续改动自动通过。测试数量不是固定验收门槛；每次验证须记录实际基线、范围和结果。历史阶段的设备数量、DPI 和界面行为只适用于当时条件，不能合并成当前版本的全量验收结论。

## 选择验证范围

2026-09-16 编辑页整页重建修复：主代理执行 `TitleBarProvidesStorageSystemSelector` 单项架构检查，1/1 通过；这是系统身份导航守卫的源码回归检查，不是原生交互测试。隔离编译 `WinPool.App.csproj -c Release --no-restore` 及其 Agent 目标通过，0 警告/0 错误，输出在 `artifacts/editor-refresh-check/trees/Release`。用户要求不跑完整测试，本轮未重跑全套；保留正在运行的 `artifacts/Release`，原生操作后不闪烁的目视复测为 `unverified`。监控数据库轮换计划没有因此开始实施。

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

模拟规则验证不执行真实存储写操作。未来允许真实写入的阶段，仍须按 Product/AGENTS 取得准确授权并记录目标与结果；自动测试和 CI 不触碰真实结构。缺少设备或目标平台时明确未验证，不制造通过记录。

自动门证明工程行为，不能批准视觉意图或物理设备行为。用户接受阶段也不能把未执行用例改写为 passed。例外记录原因、范围、批准者和风险，保持简短可追溯。

## 历史验证入口

- [2026-09-16 文档整理归档](Archive/20260916-docs-review/README.md)：保留整理前 Quality 全文，包括 613/603/598 项回归与各次原生范围。
- [V0.52 实施核对](Archive/V0.52/实施核对.md)：统一事实与模拟编辑的原始验证。
- [硬件报告实施核对](Archive/20260915-hardware-report/实施核对.md)：硬件报告、来源与有限原生验证。
