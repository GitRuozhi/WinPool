# WinPool 真实磁盘操作响应慢问题

日期：2026-10-08。下文保留原始调查，源码基线为 V0.58（`6f51bc7`）；“尚未优化”描述的是编制时状态。随后已在 V0.59 执行阶段实施相关范围采集、持续反馈与刷新调整；最终测量和限制将在本报告追加，不改写原调查数据。

## 问题

用户观察到 WinPool 完成真实磁盘操作明显慢于系统磁盘管理器和命令行，而且操作后还要等待采集、刷新图形。现有执行链确实增加了大量重复工作。

## 已确认的额外开销

1. **重复完整存储采集。** 准备一次、接受前一次；每步预检、调用前、调用后分别采集；最后对账和界面刷新又各采一次。正常成功且每步首轮核验通过时，单步操作最少 **7 次**，五步操作最少 **19 次**。采集范围是完整存储拓扑，尚非目标盘局部采集；不是完整硬件采集。
2. **反复启动 PowerShell。** 每次存储采集、每步写操作都重新创建进程，重复承担启动、模块加载和查询成本。
3. **完成消息框阻塞刷新。** 当前先等待用户关闭“真实操作完成”消息框，再调用 `ScanAsync` 刷新视图。
4. **轮询增加反馈等待。** App 每两秒查询操作状态；后态尚未核验通过时，后端也每两秒重试。后端的 60 秒是核验窗口，不是每步固定等待 60 秒。

主要代码入口：`WindowsRealOperationPlanner.cs:73`、`AgentRealOperationService.cs:399/900/1069`、`WindowsRealStorageBackend.cs:89/192/292/400`、`EditorPageBase.cs:354/373/380`。

## 既有实机日志

以下只统计“任务已接受 → Agent 确认完成”，不含准备、接受前检查、用户停留在消息框和最终界面刷新。

| 操作 | 耗时 | 日志 |
| --- | ---: | --- |
| 修改池名 | 11.24 秒 | [执行事件](../artifacts/test-results/20261007-real-edit-stage1-9b7a6ce20531437aafe897e143628d43/H08/rename-pool-enter-result/execution-events.json) |
| 创建普通虚拟磁盘 | 13.09 秒 | [执行事件](../artifacts/test-results/20261007-real-edit-stage1-9b7a6ce20531437aafe897e143628d43/H07/create-raw-vd-result-2/execution-events.json) |
| 五步分区布局 | 48.01 秒 | [执行事件](../artifacts/test-results/20261007-real-edit-stage1-9b7a6ce20531437aafe897e143628d43/H07/auto-vd-layout-phase2-result-2/execution-events.json) |

## 待测量与改进方向

现有日志没有分别记录 Windows 命令、进程启动、每次采集和界面重绘的准确耗时，因此尚不能给出各项占比，也不能声称已经完成与系统工具的公平对比。

下一步按阶段计时，优先减少重复完整采集、改为相关范围刷新、消除消息框对刷新的阻塞并缩短反馈等待。实时目标、安全角色和操作后态仍须核验，不能靠省略必要检查提速。具体实施与验收归 [V0.59 Plan](Plan.md)。

## V0.59 实施后的首轮末态实测：2026-10-08

**后续审计发现条件差异：** 这组六次操作虽开启 20 Hz 监控，但新会话 WDC 被旧建池操作的恢复屏障阻止，实际只有两个 Samsung 采样，旧基线则有三个物理设备持续采样。因此 15.76% 只保留为该次真实操作链结果，不能称严格同负载优化对照或最终验收。监控恢复修复和三盘真实采样后的重测结果另记，不改写这组原始数据。

原结构页已接回草稿、属性、净差异和 Apply；真实执行改为相关对象采集与刷新，完成反馈前先取得对应操作的新鲜结果，执行进度更新复用同一消息。实时身份、系统角色、成员、安全条件与后态核验仍保留。上述历史静态次数描述的是改动前代码，不是当前运行次数。

该次标准运行树为V0.58 iteration8，基于 `Engineering/final-full-tests/20261008-214608` 通过的源码；目标版本尚待分层MAX验收边界收口。App14884/Agent18124在同一WDC最终NTFS/E:完成六次往返，每次均Completed/Verified/匹配完整scoped结果。旧基线三设备持续采样可证，但准确provider映射缺失；本次虽开启20Hz监控，WDC实际无样本，两者负载不同，不能作最终同条件结论。

| 同方向卷标改回 `WinPool_Test` | 旧基线 | 最终测量 |
| --- | ---: | ---: |
| 样本数 | 3 | 3（六次往返中的 2/4/6） |
| 接受执行→界面取得新结果，中位数 | 18.472 秒 | 15.561 秒 |
| 中位数减少 | — | 2.911 秒，15.76% |

计时从点击准确确认开始，到界面取得操作相关的新结果结束；不含输入、准备和人在确认框停留的时间。旧流程包含终态反馈及随后刷新，新流程先刷新后反馈，因此这是实际操作链改善，不是单独 Windows 命令的加速。中途23.24%及本次15.76%均保留历史，最终结论使用下面三盘实际采样后的新测量。本次单盘六次测量不能外推其它硬件，也没有完成与系统磁盘管理工具的公平对照。

六个 OperationId 与实际运行 PID 双重筛选得到 126 条诊断记录。每次准确记录到 5 次 scoped provider 底层采集（执行/核验 4 次、结果刷新 1 次），六次合计 30 次；另有每次 1 个 full wrapper marker，不能与底层采集重复相加。Prepare/full 的底层日志没有 OperationId，完整 full 底层调用总数仍缺失，不按邻近时间强行归属。

| 独立阶段 | 六样本阶段耗时中位数 |
| --- | ---: |
| 准备计划 | 2.713 秒（位于上述主计时窗口之外） |
| 接受时预检 | 2.697 秒 |
| 每步执行前预检 | 2.186 秒 |
| Windows provider 调用 | 1.118 秒 |
| 整步执行 | 5.625 秒 |
| 界面相关结果刷新 | 2.305 秒 |

这些阶段存在包含关系：整步执行已包含 provider 与内部采集，不计算各行总和或占比。30 次 scoped 底层采集的中位数为 2.176 秒，剩余等待主要仍涉及真实查询和核验，不能通过缓存或省略安全门消除。

完整时间戳、操作ID、范围完整性和原诊断记录见[首轮末态历史比较](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/performance-final-comparison.json)、[首轮分阶段诊断](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-diagnostic-phases.json)及[旧基线监控条件核对](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/audit-performance-monitor-condition/baseline-monitor-condition.json)。随后退出生命周期修复与这组测量分别留证；不能把退出检查发现的崩溃写成性能测试失败或已正常退出。

## 最终三盘真实采样后的测量：2026-10-08

标准 V0.58 iteration8，App17736/Agent15500，源码工程门为221503（1763 passed/0 failed/3 NotExecuted）。历史恢复屏障修复后，以相同原界面完成新六次卷标往返；每次均 Completed/Verified、对应范围新鲜且完整，driver额外等待可见 `E: <label>` 拓扑组。新810015…会话准确映射WDC及两块Samsung，三盘各4252完整非NULL样本，20.006Hz、最大间隔65ms，Stopped/dropped0；18个Accepted窗及18个点击确认→视图就绪窗均gap0。旧基线同窗三个设备有持续采样，但准确provider UID映射仍缺，不能声称全部选择细节一致。

| 同方向改回 `WinPool_Test`，样本2/4/6 | 旧基线 | 最终新测量 |
| --- | ---: | ---: |
| 有效样本 | 3 | 3 |
| 中位数 | 18.472秒 | 15.437秒 |
| 范围 | 18.288–20.544秒 | 15.394–16.278秒 |
| 中位数减少 | — | 3.035秒，16.43% |

计时仍为acceptClick→viewReady，不含准备/输入/人类确认停留。这是单盘真实操作链的减少，不是Windows调用本身提速，也不是与系统磁盘管理器的公平对比。

| 阶段 | 新六样本中位数 |
| --- | ---: |
| 准备计划（主窗口外） | 2.703秒 |
| 接受时预检 | 2.719秒 |
| 步执行前预检 | 2.198秒 |
| Windows provider调用 | 1.104秒 |
| 整步执行 | 5.547秒 |
| 界面相关结果刷新wrapper | 2.310秒 |

按准确六OperationId和实际App/Agent PID双重过滤的126条原诊断均可重解析核对。每次记录5次scoped底层采集、共30次，中位数2.170秒；每次1个full wrapper不与底层重复相加，Prepare/full底层缺少操作归属，完整full底层总数保持缺失。各阶段有包含关系，不求和或编造占比；driver视图就绪还包含可见拓扑等待，与刷新wrapper定义不同。剩余耗时仍主要涉及实时查询和安全核验。

权威证据是[新比较](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/monitor-restored-performance/verified-performance-comparison.json)、[新分阶段诊断](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/monitor-restored-performance/diagnostic-phases.json)及[三盘窗口与独立full审计](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/audit-monitor-repaired-performance/session-810015f675bf45c5a5cf7c2c3bd2aca1/final-readonly-receipt.json)。同一新目录中准备时误读旧样本生成的 `performance-final-comparison.json`/review已标INVALID并保留错误记录，不能混作新结果。其后仅历史监控告警呈现发生修复，不影响采样、执行或计时路径；本组六次性能不重复运行，也不冒称在该呈现修复后的源码上重测。
