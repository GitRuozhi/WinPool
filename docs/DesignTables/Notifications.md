# 消息卡与本次运行日志触发表

状态：2026-09-23。来源是 `GlobalNotificationService`、`ApplicationNotificationPresenter` 和各页面的发布点；“日志”指开发页读取的本进程内存 `History`，不读取 Diagnostics 文件。每次独立完成事件分配独立 ID；相同文案不会合并。

| 场景 | 实际触发条件 | 右下角卡 | 开发页日志 | 不触发／结束条件 | 代码入口 |
| --- | --- | --- | --- | --- | --- |
| 普通完成／提示 | 调用 `Publish`，默认 `ShowNotification=true`、`RecordInHistory=true`，且标题、正文或详情非空 | 是，通常 8 秒 | 是，最多保留最近 200 条 | 关闭卡片不清除日志；到期只移除卡片 | `GlobalNotificationService.Publish` |
| 警告 | 状态变化、操作拒绝、结果未知等发布 Warning | 是，通常 8 秒 | 是 | 持续异常的重复轮询不重新发布 | `PublishWarning`／各来源 |
| 错误 | 采集、连接、模拟操作、导入导出等失败发布 Error | 是，通常 20 秒；点击打开可读错误正文 | 是 | 点击或到期不删除历史 | `PublishError`／各来源 |
| 进行中 | 明确设置 `IsProgress=true` 并带进度键 | 是，同一键更新，不计自动到期 | 否，服务强制排除 | 完成、失败、取消或结果未知时按键移除，再发布结果 | `GlobalNotificationService.UpdateProgress` |
| 仅记录 | 发布者显式设置 `ShowNotification=false`、`RecordInHistory=true` | 否 | 是 | 用于不应重复打扰但需留记录的结果 | `EditorPageBase`、`MonitorPage`、`AgentInventorySynchronizer` |
| 自动本机采集开始 | Agent 报 Started，展示进度 | 是，进度卡 | 否 | 仅启动显示；晚连接补读与重复事件不当作新开始 | `AgentInventorySynchronizer` |
| 自动本机采集完成 | 新清单通过验证并应用后发布结果 | 是 | 是 | 读取既有历史不报新的成功 | `AgentInventorySynchronizer` |
| 自动本机采集失败／连接中断 | Agent 报失败或进度被连接故障打断 | 是，警告或错误 | 是 | 旧清单继续保留；恢复另按状态变化发布 | `AgentInventorySynchronizer` |
| 手动刷新本机／硬件 | 请求开始显示进度；请求有成功、失败、取消或未知终态时发布结果 | 进度及结果按阶段显示 | 仅终态 | 页面进入、对象选择、鼠标移动不发布 | `WorkspaceViewModel`、`HardwarePage` |
| 模拟编辑提交 | 规则与事务提交产生确定成功后发布结果 | 是 | 是 | 表单输入、暂存选择、确认对话框取消不报成功 | `EditorPageBase`、分区/结构页 |
| 模拟编辑拒绝／失败 | 规则拒绝、提交失败或异常被处理为结果 | 按来源决定显示错误/警告；可能仅记录 | 是 | 不把失败说成成功，不留下部分模拟提交 | `EditorPageBase` |
| 模拟提交结果未知 | 传输中断而结果无法确认 | 警告 | 是 | 等待按 CommitId 对账，不按失败自动重试 | `EditorPageBase` |
| 格式化数据损失确认 | 已用数据的模拟分区准备格式化 | 否，使用确认对话框 | 否 | 用户取消不提交、不报成功 | `DiskPartitionPage` |
| ReFS 证据提示 | 模拟格式化选择 ReFS 后进入提交流程 | 警告 | 是 | 仅选择控件不提交时不把操作报成功 | `DiskPartitionPage.PublishRefsNotice` |
| 导入／导出系统 | 文件读写与验证实际完成，或产生明确失败 | 是，成功/失败对应级别 | 是 | 只打开或取消文件选择器不报成功 | `MainPage`、`WorkspaceViewModel` |
| 模拟系统删除 | 用户确认且目录删除完成 | 是 | 是 | 取消确认不发布成功 | `MainPage.DeleteSimulationAsync` |
| 监控异常 | 故障、采样缺口或归档失败首次进入新状态 | 是，警告或错误 | 是 | 同一异常的正常轮询不重复弹出 | `MonitorPage` |
| 监控恢复 | 已记录异常状态实际恢复 | 视来源显示提示或仅记录 | 是 | 关闭故障卡片不代表恢复 | `MonitorPage` |
| 设置操作 | 数据根迁移、重置或工具配置产生确定结果/失败 | 按结果显示 | 是 | 仅打开设置页或选择器取消不报成功 | `SettingsPage` |

| 生命周期 | 当前值或规则 | 效果 |
| --- | --- | --- |
| 卡片尺寸 | 宽 384 DIP；高度随内容，72–200 DIP | 长短文字不强制同高；窗口高度不足时减少同时显示数 |
| 堆叠 | 服务最多 3 张活动卡；显示层按可用高度收窄 | 被挤出的卡退出后可从日志读到已完成详情 |
| 退出 | 向右平移 420 DIP、240 ms；动画关闭时立即完成 | 呈现动作不清除日志，不改变故障状态 |
| 日志寿命 | 本次 App 运行最多 200 条 | 退出应用即清空；进度不会进入日志 |
| 文本长度 | 标题 256、正文与详情各 2048 字符，来源 128 | 卡片可显示更短的摘录；详情使用保留的完整字段 |
