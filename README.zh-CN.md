# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool 是 Windows 存储系统桌面应用，用于查看存储拓扑、监控设备、编辑模拟系统和执行已支持的本机存储操作。

当前产品版本仍为 **V0.58，迭代 8**，[活动计划](docs/Plan.md)尚未完成或归档；此前 H01–H11 结果保留在[历史归档](docs/Archive/20261007-real-edit-stage1/Plan.md)。

`WINPOOL-MAX-GIB-1` 使用 `G = 1,073,741,824` bytes、`R = 4,000,000` bytes，计算 `C = floor((A - R - 1) / G)`。真实路径的 A 是 fresh provider 最大范围与物理容量预算中的较小值；模拟路径的 A 是应用布局折算并扣除已知物理占用后的逻辑余量。C 是起始候选而非保证的物理最大值：模拟每层直接采用其 C，真实 MAX 按 1 GiB 步长搜索，不传递 Windows `UseMaximumSize`。真实分层仅限受控 typed backend 支持的 Simple／Fixed、单列、64 KiB 且各层介质不同布局；每层从精确 `0.5C` bytes seed 开始并独立搜索到整数 GiB 结果，最终 VD 容量为层容量之和。seed 仅为初始结构，不是最终 MAX。没有多盘 UI 或真实多层 MAX 实机结果。历史原生 `UseMaximumSize` 结果见[Quality](docs/Quality.md)。

在本次 WDC／provider 上，普通搜索从 3,724 GiB 开始，3,724 与 3,725 GiB 均成功，3,726 GiB 收到结构化错误 40000；观测边界为 **3,725 GiB／3,999,688,294,400 bytes**。单 HDD 分层搜索在 3,725 GiB 收到 provider code 1，3,724 GiB 成功，**actual HDD tier、VD 与 OS disk 容量一致为 3,998,614,552,576 bytes**，自动布局完成。断言分别为 18/18 和 17/17：[普通 MAX](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Ordinary-Retry/native-assertions.json)、[HDD 分层 MAX](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Hdd/native-assertions.json)。独立复核分别通过普通路线 22/22 项、HDD 路线 23/23 项且无 evidence gaps：[普通复核](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/ordinary-native-independent-review/review.json)、[HDD 复核](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/hdd-native-independent-review/review.json)。这些是该磁盘与 provider 的实测结果，不保证其它系统具有相同上限。

MAX 操作后，WDC 已恢复为在线 GPT、规范 16 MiB MSR，以及 `E:` 上的 `WinPool_Test` NTFS／64 KiB 分区。[最终 native assertions](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/final-native-verification.json)为 11/11，[独立末态复核](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/final-native-independent-review.json)为 14/14；Samsung 限定结构比较为 0 changes／0 evidence gaps。普通重启后仍为 Real Off／Monitor Off 20 Hz，E: 可见，B0 偏好一致、412 个源码文件哈希与最终门相同且无未终结操作；原托盘退出时 App/Agent 均为 0。见[最终操作审计](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/final-operation-verification-v2.json)、[重启只读审计](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/after-final-restart-readonly-audit.json)、[重启界面](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Final/normal-restart-ui.txt)及[托盘退出结果](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Final/final-normal-restart-exit-result.json)。多盘冗余 UI、任意混合介质布局创建、真实 MBR→GPT 转换与扩展现有 VD／层至 MAX 仍不可用。

三块磁盘均有实际 20 Hz 监控样本的六次原界面改名已通过。同机三组匹配样本从接受确认至刷新就绪的中位数由 18.472 秒降至 15.437 秒，减少 16.43%；排除准备和人工等待，不作为普遍性能保证。2026-10-08 后续仅调整告警呈现的构建未再重复六次计时，该次改动未改变采样与执行路径；这些测量早于原生 MAX 与局部成员修正，未在其构建重新计时；旧基线设备映射及界面就绪条件的限制见 [Development](docs/Development.md)。

当前冻结源码的[完整工程门](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/20261009-203701/summary.json)覆盖 12 个项目：**1,999 Total／1,996 Passed／0 Failed／3 NotExecuted**；源码哈希未变，restore 与 Release build 成功、0 warnings／0 errors，复用了之前 23 项目的依赖审计。前一轮 20:30 门的三个恢复失败及修复记录见[历史 summary](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/20261009-203059/summary.json)；新增的 footprint recovery 回归为 12/12：[TRX](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/multi-recovery-footprint-final/results.trx)。三个 NotExecuted 是既有 archive/performance measurements，不计为通过。

此前的[单 HDD 容量调查](docs/Review/Single-Hdd-Tier-Capacity-20261009.md)通过独立 pwsh 与原界面，以明确容量 3,997,809,246,208 bytes（3723.25 GiB）创建实际 HDD 层；该明确容量结果不是 MAX，且早于 `WINPOOL-MAX-GIB-1`。

设置中的开发者模式默认关闭；启用后显示硬件、测试、开发三页，硬件排在管理之前。只读硬件页按十个大类呈现基本硬件报告，支持设备属性详情、本机完整刷新和完整系统 JSON 导出。当前格式为文档 3 / 核心 SQLite 19 / 监控 SQLite 2 / IPC 13，保存来源事实；核心库对 schema 17 和 18 执行受检迁移，旧文档格式仍明确拒绝，不删除旧数据。

启动时优先用完整性校验后的只读本地状态显示上次页面和系统，再由 Agent 复核；缓存不可用时显示加载状态，避免先呈现默认系统。管理页和硬件页先显示上次本机数据。Agent 启动后自动先采集存储系统、再采集完整硬件，分别回报更新；两页刷新按钮仍可手动触发对应采集。自动采集和手动刷新均在窗口内显示采集中、成功或失败提示；采集失败保留上次数据。读取历史不作为一次采集成功提示。

## 当前可用功能

- 通过只读采集查看本机存储，检查池、层、磁盘、分区及相关信息。
- 查看 Computer、System、Mainboard、CPU、Memory、VirtualMemory、Storage、GPU、Monitor、Network 十段只读硬件报告及原始来源详情。
- 在存储结构编辑和磁盘分区编辑两页修改模拟系统。
- 两个编辑页保留授权单盘受控真实操作入口，使用 Agent 冻结计划确认和 OperationId 查询；普通启动时真实编辑关闭。结构页通过原表单、拓扑草稿和净差异 Apply 提交，分区页每项动作独立提交；已有名称按 Enter 单独改名。
- “存储结构”新建默认普通布局／`MAX`，也可选择单 HDD 分层并输入明确 GiB。MAX 使用统一的 `WINPOOL-MAX-GIB-1`：以 `G = 1,073,741,824`、`R = 4,000,000` 计算 `C = floor((A - R - 1) / G)`；模拟每层直接采用其 C，真实创建按 1 GiB 步长搜索，不传 Windows `UseMaximumSize`。本次 WDC/provider 实测普通 MAX 为 3,725 GiB，单 HDD 分层 MAX 为 3,724 GiB，均有相邻候选的实际结果；这不是其它设备的容量保证。真实分层在现有受控 backend 内仅支持 Simple／Fixed、单列、64 KiB 且各层介质不同布局；每层从精确 `0.5C` bytes 开始并独立搜索至整数 GiB，seed 不是最终 MAX，VD 容量是各层结果之和。没有多盘 UI 或真实多层 MAX 结果。明确普通 GiB 须满足准确池的 provider 范围；明确单 HDD 分层 GiB 须满足准确且尚未用于 VD 的 HDD 模板范围，不套普通 VD 的池步长。后续自动分区布局须等待 fresh 身份、规格和实际容量核验；分层目标还必须有唯一 actual tier。读查询不预留容量，也不保证创建成功。AutoVD 与 AutoPart 是独立的已保存偏好；切换偏好不会改动已有对象或草稿。重建必须在草稿中明确提出删除并重新创建，破坏性步骤分别列入确认计划。
- 在“磁盘分区”页，只有准确来源事实返回 `IsOffline` 时，联机/脱机按钮才可操作。RAW 可初始化为 GPT，GPT 显示已初始化；MBR→GPT 仅在模拟模式可转换，真实模式明确拒绝。独立的“清空至 RAW”在模拟模式按原规则支持 GPT/MBR；真实清空当前仅支持联机 GPT 普通数据/MSR 分区，或本次完整来源事实证明分区数为零的 GPT，真实 MBR 清空禁用。该操作不整盘写零、不初始化、不格式化也不建池。自动 GPT 布局沿用已保存的 MSR 偏好：AutoPart 开启且 MSR 开启时，在 1 MiB 处创建唯一 16 MiB MSR，BasicData 从 17 MiB 开始；MSR 关闭时不创建 MSR，BasicData 从 1 MiB 开始。单独初始化不会创建 BasicData、建池或格式化卷。
- 在 1 MiB 网格上创建模拟 GPT 分区；创建容量使用整数 MiB，默认填入对齐后的最大容量，并提供 MAX 按钮。右侧同一个操作按钮随选择切换“新建分区／格式化分区”。
- 以 JSON 导出存储系统。分区页的快速格式化与完整格式化互斥；模拟目标不写真实磁盘，真实写入只开放当前支持的单盘路径，并须通过受控计划确认。V0.59 阶段仍按活动 Plan 收口；本轮 MAX 结果仅覆盖记录中的 WDC/provider 与受支持布局。
- 监控受支持设备，持续显示会话运行时长；异常统一进入消息卡片和本次运行消息记录。
- 新监控记录使用独立数据库，达到 1 GiB 轮换并以随附 7-Zip 归档；设置支持指定自定义 7Z 路径。
- 使用中英文界面、主题与键盘导航。

监控归档不自动删除。软件不提供监控历史查看，CSV 仅导出当前活动监控库中可用的记录。轮换期间落盘短暂停顿，以有界内存承接采样；崩溃或缓冲耗尽仍可能产生需要提示的记录缺口。

重启后，历史缺口不会阻止已由新鲜事实唯一绑定的受支持设备开始实际采样，恢复仍须真实样本确认；操作结果未知继续阻止采样恢复。监控关闭时，历史端点未知的待核验缺口保留在诊断中，不作为当前监控异常提醒。

模拟结果不能证明 Windows 能够执行该配置，详见[当前限制与变更记录](docs/CHANGELOG.md)。

真实调用报错或后置条件无法核实时保留结果未知屏障。严格只读对账可将证据完整的无变化结果收口为 Failed／ObservedNoEffect，或将准确识别的不符目标残留收口为 Failed／ObservedUnexpectedEffect；后者仍保留残留，须另行确认清理，两者都不是创建成功。当前 MAX 流程会按 1 GiB 步长显式搜索容量候选；不会改换布局或重放结果未知的调用。

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
