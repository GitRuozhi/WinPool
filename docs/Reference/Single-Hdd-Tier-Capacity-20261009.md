# 单 HDD 分层容量调查

日期：2026-10-09。范围：仓库原始研究、网络原作者资料、微软公开接口、本机固定 PowerShell 脚本和 WinPool 原界面。未修改或发布 V10 文章。

## 已证实的区别

单 HDD 可以创建真实 HDD 容量层。本轮 WinPool 原界面再次创建了 32 GiB 实际层；独立 pwsh 调用还创建了接近全盘容量的实际层，不能把接近上限失败解释为单 HDD 不支持分层。

2026-10-09 用户明确要求记录归因：**本机分层原生 MAX 失效是微软 Windows Storage 接口／提供程序侧问题，不是 WinPool 容量算法问题。** 该归因与本报告查出的 WinPool 校验和采集缺陷分开；它不等于微软已公开确认具体缺陷，也不将本机两条失败路径外推为所有 Windows 的永久限制。

目前须分开三个问题：

1. `StorageTiers + UseMaximumSize` 的本机错误消息与官方 48010 的互斥输入定义一致，和容量不足不是同一错误。导出没有保留数值错误字段，48010 数字由当时操作者报告，不能冒充原始数值回执，详见[准确模板调用审阅](../../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/exact-template-native-max-readonly-review.json)。`MediaType HDD + UseMaximumSize` 的前轮 provider 返回了普通 VD；后续只读观察没有找到 VD 关联的 actual tier，严格对账定为 Failed／ObservedUnexpectedEffect，不能当作分层成功。
2. 层查询上界／普通 VD 查询上界不能保证明确分层容量创建成功。本轮相同请求在 pwsh 7 与 Windows PowerShell 5.1 中均失败，显式缓存 0 未解决；尚不能从通用资源错误推出 metadata、heatmap 或固定预留公式。
3. 查出并修复 WinPool 自身的明确校验错误：将普通 VD 的池范围和 1 GiB 步长强加给 `StorageTierSizes`。真实成功值只符合准确 HDD template 的 256 MiB 步长，反证池步长不是该层创建路径的必要约束。修正后的原界面同值回归已通过，准确结果见下文。

## 原始研究与官方文档

仓库 [V10](../../../../Research/WinPool-Tiered-Storage/articles/storage-spaces-tiered-storage-guide-V10.md) 的配置是 2 SSD Mirror + 5 HDD Parity，先以 `StorageTierSizes 16GB,16GB` 创建，再分别扩展实际层。HDD `Resize-StorageTier -Size 14892GB` 的输出为 15,989,727,355,904 bytes；没有保留该数值的上界查询输出、更大失败值或最大容量推导。V10 的 `UseMaximumSize` 用于之后的 `New-Partition`，不是分层 VD。原始 Tests 01 中的 `New-VirtualDisk -UseMaximumSize` 记录是普通非分层 Simple／Parity，不能代替单 HDD 实际层的 MAX 证据。详见本轮[仓库调查](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/repository-research.md)。

| 官方来源 | 说明及本次解释边界 |
| --- | --- |
| [Get-StorageTierSupportedSize](https://learn.microsoft.com/en-us/powershell/module/storage/get-storagetiersupportedsize?view=windowsserver2025-ps) | 示例把结果称为尺寸估计；查询没有最终创建时的缓存、列数、interleave 等全部输入。 |
| [MSFT_StorageTier.GetSupportedSize](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-storagetier-getsupportedsize) | 定义离散集合或 min + k × divisor 的序列。层范围须绑定准确层对象，不能混用普通 VD 的网格。 |
| [New-VirtualDisk](https://learn.microsoft.com/en-us/powershell/module/storage/new-virtualdisk?view=windowsserver2025-ps) | 原生 MAX 表达最大容量意图；显式 `StorageTierSizes` 提供每层容量。语法同时列出参数不代表 provider 接受所有组合。未传 WriteCacheSize 时采用池默认，Auto 结果取决于实际配置。 |
| [MSFT_StoragePool.CreateVirtualDisk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/createvirtualdisk-msft-storagepool) | 48010 区分 Size／UseMaximumSize 与 StorageTiers／StorageTierSizes 两类输入。公开页面有旧系统最低版本；本机失败不能泛化为所有现代实现永久不支持。 |
| [DPM 官方配置步骤](https://learn.microsoft.com/en-us/system-center/dpm/add-storage?view=sc-dpm-2025#configure-dpm-storage) | 明确建议显式层容量略小于实际容量；没有公开适用于本机的固定扣减算法。该示例是 SSD+HDD／DPM，不是单 HDD 原生 MAX 正例。 |

网络原作者的可核验实验包括 [José Barreto 2013 分层步骤](https://barreto.home.blog/2013/08/28/step-by-step-for-storage-spaces-tiering-in-windows-server-2012-r2/)、[freemansoft Windows 10 原始脚本](https://github.com/freemansoft/win10-storage-spaces/blob/main/new-storage-space.ps1) 和 [MicrosoftDocs issue 4122](https://github.com/MicrosoftDocs/windows-powershell-docs/issues/4122)。前者是旧 Server Preview 多盘实验；第二个采用显式上界乘 0.99，不能当通用算法；第三个是作者报告 SSD 上界 6725 GiB 却能扩到 12937.5 GiB，目前未获微软确认。详见[网络调查](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/network-research.md)。未找到单 HDD actual tier 原生 MAX 的可核验网络成功例，不等于证明它不存在。

## 本机对照

Windows 10 专业工作站版 22H2，build 19045；Storage module 2.0。WinPool 使用固定路径的 Windows PowerShell 5.1.19041.7725；另以 pwsh 7.6.6 对照。仅授权 WDC 承载测试池，Samsung 不是修改目标。

先通过 WinPool 原界面创建池、HDD template 和 32 GiB 实际层，随后原界面只删除 VD，保留同一池和 template。固定诊断脚本的 Create 模式核对机器、准确物理 UID／serial／ObjectId、准确池／template、单 WDC 成员、系统盘保护、零既有 VD及终态 jobs；要求 App／Agent 停止并持有互斥。每次调用前、原始返回后和独立后态分别落盘，不自动降容量重放。用户本次明确要求的脚本诊断是限定实验，不是软件开放自由命令入口。

查询返回：tier max 3,999,956,729,856 bytes、divisor 268,435,456；普通 VD pool max 3,999,688,294,400 bytes、divisor 1,073,741,824。两者不是同一个创建布局的联合 dry-run。

| 路径／唯一或主要变化 | 明确层容量 bytes | 结果 |
| --- | ---: | --- |
| 本轮 WinPool 原界面，默认缓存，32 GiB | 34,359,738,368 | 真实独立 HDD actual tier、VD、OS 同容量 |
| pwsh，pool 查询值作为显式 tier size，Cache0 | 3,999,688,294,400 | Code1，eligible resources；无新 VD |
| Windows PowerShell 5.1，同值同参数 Cache0 | 3,999,688,294,400 | 同样失败；排除换宿主能解决该请求 |
| pwsh，默认缓存，单独省略顶层 columns 覆盖 | 3,999,688,294,400 | 同样失败；相对历史默认缓存完整参数控制，不是相对 Cache0 的单变量实验 |
| pwsh，默认缓存，单独省略顶层 interleave 覆盖 | 3,999,688,294,400 | 同样失败；同上 |
| pwsh，Cache0，tier max−2个层单位 | 3,999,419,858,944 | 同样失败，无新 VD |
| pwsh，Cache0，tier max−8个层单位 | 3,997,809,246,208 | 成功；实际层、VD、唯一 RAW OS 同容量 |

单次原始证据：[32 GiB WinPool 后态](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/winpool-control32.json)、[大容量显式层成功](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/pwsh-tiermax-less-eight-units.json)、[pwsh 上界请求失败](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/pwsh-poolmax-cachezero.json)、[Windows PowerShell 同请求失败](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/winps-poolmax-cachezero.json)。前轮 MediaType 原生 MAX 残留的最终 Failed／ObservedUnexpectedEffect 见[生产查询收据](../../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/closed-query-recovery-host/execution-receipt.json)，初始 postcondition_unverified 不是最终恢复状态。

成功的大容量实际层为 HDD／Simple／Fixed／1列／65536-byte interleave，copies1／redundancy0，实际 AllocationUnitSize=256 MiB，VD WriteCacheSize=0。池 AllocatedSize 等于 VD 容量加 256 MiB。该值除以普通池 divisor 余 256 MiB，却符合层网格，直接反证错误的 pool-grid 限制。unused template 的 AllocationUnitSize 为 Auto 哨兵，不能当成实际层分配单位。

这些减单位请求是明确容量诊断点，没有用作产品 MAX 算法或静默 fallback。一次成功不证明最大值；池与实际层 footprint 的差额也不能解释创建时所有临时资源要求。保存的 StorageManagement 错误事件只重复通用资源不足，没有提供 metadata／heatmap 子因。Driver Diagnostic 抓取每日志最多 300 条且确有截断，不能宣称已穷尽系统日志。

## 当前软件路径与实验算法的区别

当前生产真实分层没有自动 MAX 绕过算法。草稿规划和固定写入脚本均拒绝分层 `UseMaximumSize`；可执行路径接收明确 GiB，将其转换为准确 bytes，依据准确 unused HDD template 的范围校验，再以 `StorageTiers + StorageTierSizes` 创建一次。连续范围要求 `min ≤ size ≤ max` 且 `(size - origin) % divisor = 0`；离散范围要求该值属于 provider 返回的集合。范围校验不承诺 provider 最终创建成功，也不在失败后自动减容量或试写搜索。

本机实验使用的成功诊断值是 `3,999,956,729,856 - 8 × 268,435,456 = 3,997,809,246,208 bytes`，即层查询上界扣2 GiB，得到3723.25 GiB。该公式仅记录这次明确请求如何选值；没有写入生产MAX算法。普通真实VD的MAX直接使用`UseMaximumSize`，模拟MAX估算仍是另外的路径。

## 软件修正与收口

分层明确容量已改为使用准确 template 的层范围；普通 VD 的 pool 范围保持。修正覆盖 App 查询依赖的 Planner 范围、Prepare／Preflight、固定 PowerShell 调用前及后态能力核验；历史计划字段、hash 和失败结果保留兼容。原生分层 MAX 防错门不因明确容量修复而解除，也不再称为待用户作设计选择。

大容量实际层暴露了另一处问题。当前 provider 的 `GetPhysicalExtent` 为该层返回 14,893 条记录，[单次方法调用](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/extent-method-timing-pwsh.json)约 42.6 秒；WinPool 全量及 scoped 采集都查询它，多次安全采集接近 60 秒超时。独立 fresh `Get-PhysicalDisk -VirtualDisk … -HasAllocations $true` [查询](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/vd-allocated-member-timing-pwsh.json)约 0.144 秒，准确返回该 VD 的唯一已分配 WDC 成员；[官方参数说明](https://learn.microsoft.com/en-us/powershell/module/storage/get-physicaldisk?view=windowsserver2025-ps#-hasallocations)明确其意义是承载 VD extent 的物理盘。这两个数字是方法级测量，不是端到端延迟改善比例。

软件快路径仅适用准确单 HDD 池、唯一实际层与 VD、唯一已分配成员关联，以及 tier／VD 的 Size、AllocatedSize、FootprintOnPool 全部正数相等、缓存 0 和支持布局等事实完整的结构；复杂或证据不足时保留原 extent 核验。没有按介质类型猜造实际层成员，也不使用旧缓存跳过安全核验。

旧清理操作 `08f82c7e-a562-44a9-b7db-6384cb316487` 因超时留下 `adapter_exception` 与 CallIssued，不能事后改称 NoWindowsCall 或 NoEffect。修正使用专用只读对账：旧 worker 已停止，准确目标仍在，两轮新鲜完整闭包与冻结指纹一致，RAW 零分区且 jobs 全终态，才可落盘 Failed／NotVerified；缺证仍保持未知屏障。未来适配器调用前的采集失败则在真实边界保存明确 NoCall 证据。对账不重放旧请求。

修正后的 WinPool 原界面操作 `03d3e149` 已创建同值 3723.25 GiB 的实际 HDD 层，准确请求为 3,997,809,246,208 bytes，默认缓存实际为0。独立[原生后态](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/after-fix-winpool-same-capacity.json)、[已分配成员](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/after-fix-ui-allocated-members.json)和[断言](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/after-fix-ui-same-capacity-assertions.json)确认 actual tier／VD／唯一 RAW OS 同容量、Size／AllocatedSize／FootprintOnPool相等、完整单HDD规格和准确成员；生产步骤Verified且fresh scoped view完整。不是把模板对象或普通VD当作实际层，也不是原生MAX正例。

完整[工程门](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/Engineering/final-gate/20261009-155245/summary.json)为12项目1900项，1897通过、0失败、3既有测量未执行，源码哈希稳定、Release构建零警告／错误。新代码下现存大层的生产只读采集测试 `RealReadOnlyScanReturnsUsableInventory` 用时约10.53秒；另一包含Storage和Full采集的测试 `StorageRefreshOmitsHardwareAndFullRefreshReturnsTypedProcessorFacts` 用时约12.96秒，见[直接门v2 TRX](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/Engineering/direct-gate-v2/Infrastructure/results.trx)。此前同实机结构对应测试均60秒超时，首轮失败TRX保留；这不是重测全部编辑操作的端到端百分比。未知删除由[严格生产查询](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/Engineering/closed-delete-query-recovery-host/execution-receipt.json)落盘Failed／NotVerified，约8.30秒、adapter0、273 plans／220 Accepted／277 CallIssued不变；原UI随后查询终态，并另建 `afd9b476` 删除准确残留，Verified且未重放旧请求。

同值创建后的原UI解散 `33d18dd9` 三步Verified，再经清空 `9c8865b7`、GPT `4e688ad7`、规范MSR `a82082a2`、Data／Format／Letter `6a7bb339` 恢复物理WDC。新[原生快照](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/final-native-snapshot.json)及[末态比较](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/final-native-verification.json)11项全部通过：在线GPT，MSR offset1 MiB／size16 MiB，BasicData offset17 MiB／size4,000,767,279,104 bytes，NTFS／64 KiB／`WinPool_Test`／`E:`，无concrete pool、VD或tier。两块Samsung的物理／OS／分区／卷限定结构为0变化、0证据缺口；排除可用空间、顺序采集时间及共享Primordial聚合，不代表全机无变化或无后台IO。

[操作终审](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/final-operation-verification.json)29项通过：恢复后七个新UI计划共12步骤均Verified、准确WDC成员、每步CallIssued一次，旧08仍保留Failed／NotVerified与原Accepted／CallIssued各一次，无未终结真实操作。偏好恢复后，App26424／Agent16044[原托盘退出](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/final-native-normal-exit-result.json)均exit0；随后普通启动RealOff／MonitorOff、E卷可见、无未知结果或监控异常提示，App16396／Agent6968再次[正常退出](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/final-normal-restart-exit-result.json)均exit0。第二次托盘helper首次在菜单出现前查找失败，随后以fresh窗口定位原菜单完成退出；没有终止进程代替正常退出。[最终只读审计](../../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/after-final-exit-readonly-audit.json)确认B0偏好一致、MonitorOff／20Hz、重启和退出期间Accepted227／CallIssued289不变、396个源码哈希与最终门一致。

本轮已经区分并修复软件校验和采集问题，接近上界的资源拒绝在独立宿主仍复现；后者只能定位到本机Storage provider，不能据此判为已确认的微软系统缺陷。本次调查不会将完整 V0.59 或分层原生 MAX 记为已完成，不提前升版。后续仍须找到有官方合同或可复现实机正例支持的保型原生MAX路径；不以反复试写、成功点、上界扣减或普通VD代替该证据。
