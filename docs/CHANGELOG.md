# WinPool 重要变化

只记录可交付变化；过程由 Git 保存，验收由[Quality](Quality.md)引用。完整旧记录已原样移至[2026-10-17前历史](Archive/20261017-v061-document-baseline/CHANGELOG-before.md)，不追溯改写。

## V0.61：实现简化与测试候选

- 完整／Storage／scope 采集统一为一份内嵌模板，普通读取关闭 discovery，发现失败与物理枚举分开；真实临写复用类型化上下文，已验证后态供 inventory／monitor 按覆盖范围复用，退出旧比较采集链。
- 真实结构 Apply 由 Agent 持久化 job 编排，保留 MAX／GPT／MSR 阶段及准确输出映射；App 断流不重发，Agent 重启仅只读恢复，剩余目标另行确认。Query／List 只读，显式重查一次，撤 App 真实状态与恢复轮询。
- 模拟预览／提交共享无 IO 规则，创建 ID／时间冻结、有效 revision 复用预演；正常编辑改 typed edits＋CAS／commitId 轻量回执。核心 schema 20 保留旧回执查询和真实 journal，目录 metadata 按需加载正文，导航页只由 LastActivePage 恢复。
- IPC 15 采用 inventory 单正文事件、真实状态 revision 事件、按需 evidence 与监控批次；样本先编号再进有界队列，250 ms／128 点传输与落库独立。图表和告警共用 App cache，去重、gap／重连窗口同步，缺失区段断开显示；生产 MainWindow 要求 Agent，连接超时归一处理。
- 新增唯一 `build/Package-WinPool.ps1`，复用已验证 runtime trees 与现有 merge，拒绝开发数据混入，保留资源和第三方许可，输出新批次 zip／SHA-256／中英测试说明；不启动程序、重跑构建或自行发布。
- 实机反馈修正结构目标对可用空间波动的误判、持久化 null／字符串转义引起的指纹差异；调用前失败不伪装 Unknown。修复普通 VD 命名草稿容量、数据根重启交接、空闲控制连接超时、监控 WAL 测长竞态和 Agent 退出后的无效保存请求。
- 修复局部复验的三个问题：原始结构草稿摘要绑定动作、参数和目标组件，准备时拒绝外部新增对象扩大删除范围；工作区接续恢复操作的终态事件，核验事实后解除屏障；监控快照与state共用Agent单调revision，迟到状态不再清空重连窗口。保持事件驱动，无恢复轮询；App／Agent须使用配套IPC 15。

V0.61 便携候选已生成并通过解压后的普通权限启动、Storage刷新、监控恢复和托盘退出；尚未公开发布。[完成计划](Archive/20261018-v061-completed/Plan.md)已归档，当前无活动计划。自动回归、实机验证、性能计量与当前候选路径见[Quality](Quality.md)。

## 2026-10-17：V0.61 文档目标与接手计划

激活唯一 V0.61 Plan，核对原 V0.6x 方案与源码，补清真实单写计量、结构阶段迁移、模拟幂等回执、监控批次与候选包步骤。精简当前 README、产品、开发、质量和工作规则，归档已完成计划、原提案和重复历史。首要目标为减负，其次性能；首稿供外部测试，全部当前工作仍在开发者环境。

非核心资料按用途收拢：Review 区分本轮三份审查的基线、方法与用途；Reference 汇集统一对象、专项调查和源码入口；旧阶段报告及通知表原样归档，docs 根只留五份核心文档，不再维护独立 DesignTables 目录。

**该次仅文档，当时代码为 V0.60。** 未修改版本字段、代码、测试或构建脚本，未编译、运行或发布；后续代码工作属于同一 V0.61。

## 最近已实现基线

- V0.60：拓扑与编辑交互、紧凑动作、统一编辑栏、新建自动盘符；后续项名宽度按现有共享常量调整。[各次验证范围](Archive/20261017-v061-document-baseline/Quality-before.md)。
- V0.59：结构草稿、整数 GiB MAX、真实新池参数和已有 WDC 操作闭环；多成员硬件覆盖仍有限。[阶段归档](Archive/20261014-v059-completed-plan/README.md)。
- V0.58：本机单盘真实修改阶段。[历史记录](Archive/20261007-real-edit-stage1/README.md)。

这些是历史结果，不自动成为 V0.61 改造的验证结果。
