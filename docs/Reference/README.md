# 专项参考

按问题查阅，不作为日常必读清单。当前产品语义归 [Product](../Product.md)，本轮实现与验证范围归 [Plan](../Plan.md)。历史调查保留原文；其中的建议、现场状态和当时授权不自动成为当前要求。

| 材料 | 性质与适用范围 |
| --- | --- |
| [统一对象与派生属性](UnifiedModel.md) | 现行产品专项说明：来源事实、显示对象、联合体与统计口径；只有这些语义变化时维护 |
| [单 HDD 容量调查／2026-10-09](Single-Hdd-Tier-Capacity-20261009.md) | 历史技术依据：单 HDD 实际层、微软原生 MAX 接口行为和明确容量验证；现行容量算法看 [Product MAX](../Product.md#max)，不把实验扣减值恢复成产品规则 |
| [池／VD 操作计时／2026-10-16](Pool-VD-Timing-20261016.md) | 基线 `8f939d6` 的只读账本／源码调查；支撑 V0.61 减少重复采集，未重放真实操作，不是改造后的性能结果 |
| [开发踩坑记录](开发踩坑记录.md) | 已发生问题及定位经验；按当前代码判断是否适用，不新增例行检查或验收门 |

精确实现直接读拥有该行为的源码，不再同步控件、样式、通知参数副本：

| 需要 | 入口 |
| --- | --- |
| 编辑栏尺寸 | [EditorFormMetrics](../../src/WinPool.App/Controls/EditorFormMetrics.cs) |
| 控件样式、颜色与图标 | [App.xaml](../../src/WinPool.App/App.xaml)及所改页面／控件的实际使用处 |
| 模拟编辑规则与预览 | [StorageEditRules](../../src/WinPool.Application/StorageEditRules.cs)、[EditWorkspace](../../src/WinPool.Application/EditWorkspace.cs) |
| 消息生命周期与呈现 | [GlobalNotificationService](../../src/WinPool.Application/GlobalNotificationService.cs)、[ApplicationNotificationPresenter](../../src/WinPool.App/Services/ApplicationNotificationPresenter.cs) |
| 真实操作结果反馈 | [RealOperationFeedback](../../src/WinPool.App/Services/RealOperationFeedback.cs)及调用方 |

旧 DesignTables 入口与通知触发表已[原样归档](../Archive/20261017-noncore-docs/README.md)；其中已过期的“查看操作结果”入口不再适用，尺寸和时长直接读当前源码。归档不改变产品行为，也不要求为文档迁移运行代码测试。
