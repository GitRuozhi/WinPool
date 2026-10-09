# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool 是 Windows 存储系统桌面应用，用于查看存储拓扑、监控设备、编辑模拟系统和执行已支持的本机存储操作。

当前产品版本仍为 **V0.58，迭代 8**，[活动计划](docs/Plan.md)尚未完成或归档；此前 H01–H11 结果保留在[历史归档](docs/Archive/20261007-real-edit-stage1/Plan.md)。前轮普通 VD 原生 MAX 已通过原界面创建：冻结命令为 `SizeBytes=0`／`UseMaximumSize=true`，Windows 实际返回 VD 与 OS 磁盘容量均为 3,999,688,294,400 bytes，随后完成 GPT、规范 16 MiB MSR、NTFS／64 KiB 数据分区，卷标 `WinPool_V059_Data`、盘符 `W:`。见[原生布局证据](artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Ordinary/native-maximum-layout-readonly-proof.json)、[断言](artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Ordinary/native-maximum-layout-assertion.json)及[原托盘正常退出](artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/ordinary-native-max-observed-exit-result.json)。

单 HDD 分层 32 GiB 创建与重建的原有成功证据仍有效，不外推至 MAX。分层原生 MAX 两条候选均未满足目标：准确 `StorageTiers` + `UseMaximumSize` 被本机以参数互斥消息拒绝（与官方48010定义一致）；`MediaType HDD` + `UseMaximumSize` 实际创建了没有 actual tier 的普通 VD。后者经严格只读对账收口为 Failed／ObservedUnexpectedEffect，再由原界面单独确认并清理残留。新分层 MAX 请求已在任何解散或创建计划前阻止，Prepare／Preflight、适配器与固定脚本另设调用前拒绝门。分层原生 MAX 的技术调查继续按活动 Plan 推进；临时防护不定义永久 Windows 限制。先前 `StorageTierSizes` 显式 bytes 失败与本次原生结果分别保留。

前轮原生 MAX 运行的[末态原生／full对账](artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-protected-comparison-review.json)已通过：WDC物理盘在线GPT、规范16 MiB MSR、最大整MiB NTFS／64 KiB数据分区，`WinPool_Test`／`E:`，无concrete pool或VD；Samsung限定结构范围0变化／0证据缺口，4项共享Primordial调整单列保留。[原UI解散局部刷新复验](artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/scoped-release-original-ui-review.json)确认WDC准确fresh Primordial成员关系，未扩查Samsung兄弟盘。[普通重启与托盘退出](artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/normal-restart-exit-exit-result.json)通过，两进程均exit 0；[只读审计](artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/after-normal-restart-readonly-audit.json)确认偏好与B0一致、监控关闭且保留20 Hz设置、写调用边界计数不变、无未终结操作。多盘冗余、混合介质分层、真实 MBR→GPT 转换与既有 VD／层 MAX 扩展仍禁用。

三块磁盘均有实际 20 Hz 监控样本的六次原界面改名已通过。同机三组匹配样本从接受确认至刷新就绪的中位数由 18.472 秒降至 15.437 秒，减少 16.43%；排除准备和人工等待，不作为普遍性能保证。2026-10-08 后续仅调整告警呈现的构建未再重复六次计时，该次改动未改变采样与执行路径；这些测量早于原生 MAX 与局部成员修正，未在其构建重新计时；旧基线设备映射及界面就绪条件的限制见 [Development](docs/Development.md)。

[前轮原生 MAX Release 工程门](artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-frozen-gate-20261009-095540/20261009-095540/summary.json)覆盖12项目：1866 Total／1863 Passed／0 Failed／3 NotExecuted，restore／build成功且零警告／错误，源码哈希前后不变。[集中定向门](artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/consolidated-direct-20261009-095138/commands.json) Infrastructure 440/440、App 166/166、Application 6/6通过，覆盖新增拒绝门与局部成员修复。自动验证不代替仍缺失的分层 MAX 正例；前轮物理末态、保护比较、偏好与退出另有上述原生证据。

最新[单 HDD 容量调查](docs/Review/Single-Hdd-Tier-Capacity-20261009.md)已通过独立 pwsh 与修正后的原界面，同值创建明确容量 3,997,809,246,208 bytes（3723.25 GiB）的实际 HDD 层；该值不是 MAX。分层容量只用准确模板范围，不套普通池网格。[最新 Release 工程门](artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/Engineering/final-gate/20261009-155245/summary.json)覆盖12项目，1900 Total／1897 Passed／0 Failed／3 NotExecuted，构建零警告／错误、源码哈希稳定。分层原生 MAX 仍属技术未解决问题；本次调查已恢复约定WDC E:布局，Samsung限定结构比较0变化／0缺口，普通重启和退出通过，当前证据见调查报告。

设置中的开发者模式默认关闭；启用后显示硬件、测试、开发三页，硬件排在管理之前。只读硬件页按十个大类呈现基本硬件报告，支持设备属性详情、本机完整刷新和完整系统 JSON 导出。当前格式为文档 3 / 核心 SQLite 18 / 监控 SQLite 2 / IPC 13，保存来源事实；核心库从 schema 17 升级时执行受检迁移，旧文档格式仍明确拒绝，不删除旧数据。

启动时优先用完整性校验后的只读本地状态显示上次页面和系统，再由 Agent 复核；缓存不可用时显示加载状态，避免先呈现默认系统。管理页和硬件页先显示上次本机数据。Agent 启动后自动先采集存储系统、再采集完整硬件，分别回报更新；两页刷新按钮仍可手动触发对应采集。自动采集和手动刷新均在窗口内显示采集中、成功或失败提示；采集失败保留上次数据。读取历史不作为一次采集成功提示。

## 当前可用功能

- 通过只读采集查看本机存储，检查池、层、磁盘、分区及相关信息。
- 查看 Computer、System、Mainboard、CPU、Memory、VirtualMemory、Storage、GPU、Monitor、Network 十段只读硬件报告及原始来源详情。
- 在存储结构编辑和磁盘分区编辑两页修改模拟系统。
- 两个编辑页保留授权单盘受控真实操作入口，使用 Agent 冻结计划确认和 OperationId 查询；普通启动时真实编辑关闭。结构页通过原表单、拓扑草稿和净差异 Apply 提交，分区页每项动作独立提交；已有名称按 Enter 单独改名。
- “存储结构”新建默认普通布局／`MAX`，也可选择单 HDD 分层并输入明确 GiB。普通 MAX 将 `UseMaximumSize` 保留到确认计划，由 Windows 决定容量，不把 provider 范围最大值转成预测 bytes。明确普通 GiB 须满足准确池的 provider 范围；明确单 HDD 分层 GiB 须满足准确且尚未用于 VD 的 HDD 模板范围，不套普通 VD 的池步长。后续自动分区布局须等待 fresh 身份、规格和实际容量核验；分层目标还必须有唯一 actual tier。本阶段分层 MAX 尚无已核验创建路径，已在删除或创建前拒绝；单 HDD 分层当前须输入明确容量。读查询不预留容量，也不保证创建成功。AutoVD 与 AutoPart 是独立的已保存偏好；切换偏好不会改动已有对象或草稿。重建必须在草稿中明确提出删除并重新创建，破坏性步骤分别列入确认计划。
- 在“磁盘分区”页，只有准确来源事实返回 `IsOffline` 时，联机/脱机按钮才可操作。RAW 可初始化为 GPT，GPT 显示已初始化；MBR→GPT 仅在模拟模式可转换，真实模式明确拒绝。独立的“清空至 RAW”在模拟模式按原规则支持 GPT/MBR；真实清空当前仅支持联机 GPT 普通数据/MSR 分区，或本次完整来源事实证明分区数为零的 GPT，真实 MBR 清空禁用。该操作不整盘写零、不初始化、不格式化也不建池。自动 GPT 布局沿用已保存的 MSR 偏好：AutoPart 开启且 MSR 开启时，在 1 MiB 处创建唯一 16 MiB MSR，BasicData 从 17 MiB 开始；MSR 关闭时不创建 MSR，BasicData 从 1 MiB 开始。单独初始化不会创建 BasicData、建池或格式化卷。
- 在 1 MiB 网格上创建模拟 GPT 分区；创建容量使用整数 MiB，默认填入对齐后的最大容量，并提供 MAX 按钮。右侧同一个操作按钮随选择切换“新建分区／格式化分区”。
- 以 JSON 导出存储系统。分区页的快速格式化与完整格式化互斥；模拟目标不写真实磁盘，真实写入只开放当前支持的单盘路径，并须通过受控计划确认。阶段最终验收仍需分层原生 MAX 的实际层成功证据。
- 监控受支持设备，持续显示会话运行时长；异常统一进入消息卡片和本次运行消息记录。
- 新监控记录使用独立数据库，达到 1 GiB 轮换并以随附 7-Zip 归档；设置支持指定自定义 7Z 路径。
- 使用中英文界面、主题与键盘导航。

监控归档不自动删除。软件不提供监控历史查看，CSV 仅导出当前活动监控库中可用的记录。轮换期间落盘短暂停顿，以有界内存承接采样；崩溃或缓冲耗尽仍可能产生需要提示的记录缺口。

重启后，历史缺口不会阻止已由新鲜事实唯一绑定的受支持设备开始实际采样，恢复仍须真实样本确认；操作结果未知继续阻止采样恢复。监控关闭时，历史端点未知的待核验缺口保留在诊断中，不作为当前监控异常提醒。

模拟结果不能证明 Windows 能够执行该配置，详见[当前限制与变更记录](docs/CHANGELOG.md)。

真实调用报错或后置条件无法核实时保留结果未知屏障。严格只读对账可将证据完整的无变化结果收口为 Failed／ObservedNoEffect，或将准确识别的不符目标残留收口为 Failed／ObservedUnexpectedEffect；后者仍保留残留，须另行确认清理，两者都不是创建成功。软件不会静默降低 MAX、改布局、试写探测或重放未知调用。

普通模拟数据分区可按目标总容量扩展或压缩，目标需按 1 MiB 对齐；系统分区仍禁止删除和格式化。真实 RAW 与 NTFS 数据分区按 Agent 实时 provider 范围扩缩，NTFS 可在线操作。扩展公式为 `A + B = C`，压缩为 `A − B = C`：当前容量、变化量和目标总容量；唯一权威目标是精确 bytes 值，Agent 在准备操作前重核实时范围。真实数据分区格式化默认使用 NTFS、64 KiB 簇和快速格式化；其他 NTFS/exFAT 与格式化方式组合仅在当前 provider 能力和准确计划都允许时可用。ReFS 仅在适用的 Workstations/SKU 与 provider 探针通过时支持 64 KiB 快速格式化和扩容；H11 已通过指定测试卷的内容哈希核对。ReFS 收缩与完整格式化、exFAT 扩缩以及系统/启动卷扩缩未开放。

“测试”页保留路线说明；开发页左上“消息列表”以单行条目显示最近 200 条本次运行消息，双击条目在页面中央打开可复制的详情，点击对话框外关闭。三个区域之间可拖拽调整，右上和下方暂留空，下方提示人工智能入口正在开发。该页仅在开发者模式下可见。消息只保存在内存，退出 App 后清空；软件不查看故障日志文件内容，不记录完整操作历史。完整测试与开发工作区计划在 2.0 推出。

即时消息统一使用右下角固定宽度、高度随内容变化的简洁卡片，退出时向右移动，独立重复消息不合并。普通消息点击消失，错误消息点击打开消息对话框；两者都会自动消失，错误保留时间较长。详细记录在开发页查看。

界面和模拟系统的具体行为见[设计核对表](docs/DesignTables/README.md)。

## 系统要求与运行

最低受支持系统为 **Windows 10 22H2 x64**，主要平台为 Windows 11 24H2、25H2 x64。具体存储功能取决于 Windows 版本/版本类型及存储提供程序。

当前交付为无打包、自包含的 x64 便携目录。保留完整目录并运行 `WinPool.App.exe`，配套 Agent 在用户托盘中运行。数据默认存放于 `%LocalAppData%\WinPool`，也支持显式选择程序旁可写的 `Data` 目录。替换程序文件前退出 WinPool。

目前没有已发布的 MSIX 安装包或 Microsoft Store 页面。

## 从源码构建

在 Windows、PowerShell 和 `global.json` 指定的 SDK 环境中运行：

```powershell
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1
.\artifacts\Release\WinPool.App.exe
```

构建前确认已有 WinPool 进程未占用输出目录。开发入口为 [AGENTS](AGENTS.md)，内部开发文档统一使用中文；构建和数据所有权详见[开发约定](docs/Development.md)。

## 研究背景

```text
64K interleave + 64K NTFS cluster = current tested recommendation.
Windows 11 has not yet received equivalent testing because current storage hardware prices and the author's practical budget do not allow a second full test platform.
```

即：64K 交错配合 64K NTFS 簇是当前测试建议。受现有存储硬件价格及作者实际预算限制，无法配置第二套完整测试平台，Windows 11 尚未获得等价测试。这些研究结果不代表全部 Windows 配置都受支持或具有同等可靠性证据。

## 权利

本仓库未对 WinPool 自有代码授予任何许可证，保留所有权利。第三方组件遵循各自许可证；随附 7-Zip 组件的[许可证](assets/ThirdParty/7zip/26.03/License.txt)及[来源说明](assets/ThirdParty/7zip/26.03/NOTICE.txt)单独提供。
