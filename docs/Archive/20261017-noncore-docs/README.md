# 非核心文档整理原件

2026-10-17，整理基线 `ad90b3c`，代码仍为 V0.60，活动目标为 V0.61。本目录保存已结束阶段的调查／复审和被替代的界面参考；五份原件移动前后 SHA-256 一致，未改写结论、历史验证或未测边界。此次只整理文档，未运行程序、构建、测试或存储操作。

| 原路径 | 归档原件 | 日期／基线与原始角色 | 归档原因及后续入口 |
| --- | --- | --- | --- |
| `docs/WinPool-Operation-Latency-20261008.md` | [真实操作延迟调查](WinPool-Operation-Latency-20261008.md) | 2026-10-08；V0.58、`6f51bc7`；原调查及 V0.59 追加测量 | 保留旧采集轮次、测量修订和限制；当前优化由 [V0.61 Plan](../../Plan.md) 接替，最近计时见 [2026-10-16 调查](../../Reference/Pool-VD-Timing-20261016.md) |
| `docs/Review/review.zh-CN.md` | [静态复审 2026-10-12](Static-Review-20261012.md) | 2026-10-12；`1cb9cb0` 对比 `de9641c`；GitHub 源码静态审阅，无编译／测试／本机操作 | 当阶段修复已记录于 [Plan §10.10](../20261012-editor-ui-baseline/Plan.md#1010-第二轮静态复审修复2026-10-12)；改用日期与用途命名，原文不变，不据此新增当前任务 |
| `docs/Review/Pool01-Topology-20261014.md` | [Pool01 拓扑诊断](Pool01-Topology-20261014.md) | 2026-10-14；V0.59、`b39ac42`；Windows／来源／账本只读核查 | 管理与编辑页的模板／actual 层选择分叉已有[阶段修复记录](../20261016-v060-topology-completed/Plan.md)；保留当时对象及参数，不能当成现在的磁盘状态 |
| `docs/DesignTables/README.md` | [旧界面参考入口](DesignTables/README.md) | 2026-10-17；`ad90b3c`；源码与旧表索引 | 有用入口合并到 [Reference](../../Reference/README.md)，不再保留单独 DesignTables 目录 |
| `docs/DesignTables/Notifications.md` | [旧通知触发表](DesignTables/Notifications.md) | 原表标记 2026-10-10；实现／参数对应表 | “查看操作结果”等描述已落后于当前反馈入口；产品语义归 [Product](../../Product.md)，精确触发与参数直接读源码，不维持重复表格 |

前三份报告未明确署名作者／模型；这里只记录能够从原文确认的审查方式，不推断身份。归档不表示原报告所有建议均被采纳，也不补造新的验收结果。

原文相对链接仍以表中**原路径**为基准解释。`artifacts/` 证据留在 WinPool 原工作区，不移动、不公开、不补造；部分附件未纳入 Git。旧 `Quality.md` 的对应记录见[整理前原件](../20261017-v061-document-baseline/Quality-before.md)，旧 `Plan.md` 须按报告日期选阶段归档，不跳到当前计划套用旧章节。静态复审提到的附件若当前不存在，不在本轮恢复。

另有三份仍需按需查阅的文件迁入 `docs/Reference`：原 `docs/UnifiedModel.md`、原 `docs/Review/Single-Hdd-Tier-Capacity-20261009.md`、原 `docs/Review/Pool-VD-Timing-20261016.md`。两份调查正文保持逐字节不变；统一对象说明调整定位、历史验证链接、旧“本轮”措辞，并明确只在相关语义修改时选择相应验证。三份 2026-10-17 审查仍在 [Review](../../Review/README.md)，由索引区分基线、方法和用途。
