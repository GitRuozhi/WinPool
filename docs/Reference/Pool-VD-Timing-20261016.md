# 最近两次虚拟磁盘操作耗时调查

日期：2026-10-16。调查基线`8f939d6`。只读检查现有账本、分段计时及源码，没有重新执行真实存储操作，也没有修改执行链。

## 结论

主要瓶颈是WinPool把一个用户操作拆成多个阶段，每阶段反复启动PowerShell采集存储状态。两次操作的60轮采集累计223.53秒，约占278.20秒阶段总耗时的80%。这不表示这些检查可以直接删除；当前实现的重复流程和进程启动开销需要优化。

两次操作均Completed，当前Q池和Q虚拟磁盘Healthy／OK。没有发现结果未知、执行失败或排队积压；采集队列等待几乎为零。MAX仅3725 GiB失败、3724 GiB成功两次，不是长时间逐GiB搜索。

## 实际时间线

时间为本机UTC+8；计时从首个`real.prepare`开始至最后`real.ui_scoped_refresh`结束，不冒充鼠标点击到结束的完整计时。

| 用户操作／内部阶段 | 耗时 | 采集次数 | 采集耗时 |
| --- | ---: | ---: | ---: |
| 删除VD，18:50:29.736–18:51:00.671 | 30.94秒 | 7 | 25.37秒 |
| 新建MAX分层VD | 47.74秒 | 11 | 35.51秒 |
| 初始化GPT | 30.33秒 | 7 | 26.58秒 |
| 删除provider自动MSR | 31.60秒 | 7 | 26.83秒 |
| 创建规范MSR | 30.77秒 | 7 | 25.97秒 |
| 创建数据分区 | 30.43秒 | 7 | 27.17秒 |
| ReFS／4 KiB快速格式化 | 45.00秒 | 7 | 28.29秒 |
| 分配E盘符 | 31.39秒 | 7 | 27.81秒 |

后七阶段属于一次创建VD及自动布局：18:51:42.882–18:55:50.569，共247.69秒，约4分8秒，阶段间间隔约0.42秒。创建Prepare前另有两次未关联OperationId的Storage采集，共7.04秒，未计入表内。表内及表外共62次采集，不能将嵌套的`real.capture`、`real.step.execute`与`inventory.provider.collect`再次相加。

## 代码中的放大路径

普通单步阶段依次经过Prepare全量采集、Accept首步全量预检、worker首步预检、Execute写前采集、写进程、写后采集、终态对账、UI结果采集，共七次读取。正常成功也会终态对账，并非只有异常恢复才调用。

- [采集入口](../../src/WinPool.Infrastructure.Windows/InventoryPipeline.cs)每次启动独立PowerShell进程。
- [Agent执行与终态对账](../../src/WinPool.Agent/AgentRealOperationService.cs)和[Windows后端](../../src/WinPool.Infrastructure.Windows/WindowsRealStorageBackend.cs)串行执行上述各层采集。
- [结构编辑编排](../../src/WinPool.App/RealStructureEditorFlow.cs)把自动布局拆成各自Prepare／Accept／对账／UI刷新阶段。

例如创建数据分区的Execute计时9.995秒，包含写前3.55秒及写后4.08秒采集，余约2.37秒才是写进程和其它执行开销。格式化Execute为23.50秒，扣除前后采集余约15.14秒，仍包含PowerShell启动、安全及原生调用，现有日志不能进一步分解，不能全部归因于Windows格式化。

此次MAX走多目标实现；拒绝无效果与最终边界证明包含双次观察、稳定等待和作业核验。它们有具体证明作用，不能作为重复刷新直接删掉。

## 优化方向（尚未实施）

1. 保留GPT初始化后的provider事实边界，评估把后续MSR规范化、数据分区、格式化、盘符接入已有完整多步布局proposal。保留每步身份、安全、写前／写后验证及持久前缀，收拢重复Prepare、Accept、终态对账和UI刷新。需要先验证新分区引用、部分完成及结果未知不重放；不能直接把多个命令串在一起。
2. 评估只读采集进程／runspace复用，每次仍重新读取准确范围，只减少进程和模块加载。先补启动、脚本及原生查询分段计时；当前不能承诺具体提速倍数。
3. 补MAX provider及安全／VDS耗时细分，避免把包含采集的Execute误记成纯Windows写耗时。

原始时序、账本摘录和计算结果保留在[本机证据](../../artifacts/test-results/20261016-label100-operation-timing/summary.json)。本轮只是完成调查；性能执行链尚未修复。
