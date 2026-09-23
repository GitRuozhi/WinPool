# WinPool 设计核对表

状态：2026-09-23 当前实现的逐项核对入口。表格按源码中的可见控件和实际规则维护；无图标或未实现的行为明确写“无”或“未实现”。界面和模拟系统的变更先改代码与相关验证，再同步对应行。历史段落式设计稿见[归档](../Archive/20260923-design-details/README.md)，不定义当前行为。

| 表格 | 用途 |
| --- | --- |
| [按钮与图标](Buttons.html) | 按页面列出按钮文字、实际图形、WinUI 字形码位和源码控件；可直接看图挑选替换 |
| [控件说明](Controls.md) | 按页面列出输入、选择、开关、列表和动态控件的用途、悬停及禁用条件 |
| [消息与日志触发](Notifications.md) | 列出何时出现右下角卡片、何时保留本次运行日志，以及不触发条件 |
| [模拟操作规则](SimulationRules.md) | 列出模拟系统中允许、阻止、禁用及证据不足的判定和反馈 |
| [前端样式](Styles.html) | 主题资源、颜色、边框、间距、字体、布局及组件状态的可视化核对表 |

图标表优先用系统字体 `Segoe Fluent Icons` 在 HTML 中绘制实际字形，缺失时回退 `Segoe MDL2 Assets`，保留码位与控件名。以 [Microsoft 官方字形目录](https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font)核对候选图形；该目录建议优先使用 Segoe Fluent Icons，并说明 Windows 10 可能需要另行提供字体。修改字形前还要核对按钮语义、悬停文本和自动化名称。界面弹出详情采用 WinUI 原生可轻触外部关闭的浮层，行为参考 [Microsoft 的 Dialogs and flyouts 指南](https://learn.microsoft.com/en-us/windows/apps/design/controls/dialogs-and-flyouts/)。
