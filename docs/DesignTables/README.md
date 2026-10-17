# 界面与规则参考

2026-10-17：取消“每改控件就同步多份尺寸／图标／规则表”的工作步骤。当前产品行为归[Product](../Product.md)，精确控件、图标和尺寸直接读源码；只有独立的产品决定变化才更新其所属文档。

| 需要 | 直接入口 |
| --- | --- |
| 编辑栏尺寸 | [EditorFormMetrics](../../src/WinPool.App/Controls/EditorFormMetrics.cs)；两编辑页共用，不另维护中英文尺寸副本 |
| 样式、颜色、控件与图标 | [App.xaml](../../src/WinPool.App/App.xaml)与所改页面XAML／控件生成代码；查看实际使用位置，不按历史表猜当前控件名 |
| 模拟允许／拒绝／信息不足 | Application的StorageEditRules与EditWorkspace；产品边界见Product，执行变更见Plan |
| 通知触发和历史记录范围 | [Notifications](Notifications.md)，保留有独立用途的触发对应关系，不扩为完整操作审计 |

旧[控件／规则表](../Archive/20261017-v061-document-baseline/DesignTables/README.md)、[图标预览](../Archive/20261017-v061-document-baseline/DesignTables/Buttons.html)、[样式预览](../Archive/20261017-v061-document-baseline/DesignTables/Styles.html)原样保留为历史参考，包含旧MAX、偏好和尺寸，不是当前规范。无需继续同步这些历史表，也不将旧表数量和原句作为架构门。
