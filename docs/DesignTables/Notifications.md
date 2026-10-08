# 消息卡与消息列表触发表

状态：2026-10-08。来源是 `GlobalNotificationService`、`ApplicationNotificationPresenter` 和各页面的发布点；开发页“消息列表”读取本进程内存 `History`，与写入 `Diagnostics/*.jsonl` 的故障日志不同。每次独立完成事件分配独立 ID；相同文案不会合并。

| 场景 | 实际触发条件 | 右下角卡 | 开发页消息列表 | 不触发／结束条件 | 代码入口 |
| --- | --- | --- | --- | --- | --- |
| 普通完成／提示 | 调用 `Publish`，默认 `ShowNotification=true`、`RecordInHistory=true`，且标题、正文或详情非空 | 是，通常 8 秒 | 是，最多保留最近 200 条 | 关闭卡片不清除历史；到期只移除卡片 | `GlobalNotificationService.Publish` |
| 警告 | 状态变化、操作拒绝、结果未知等发布 Warning | 是，通常 8 秒 | 是 | 持续异常的重复轮询不重新发布 | `PublishWarning`／各来源 |
| 错误 | 采集、连接、模拟操作、导入导出等失败发布 Error | 是，通常 20 秒；点击打开可读错误正文 | 是 | 点击或到期不删除历史 | `PublishError`／各来源 |
| 进行中 | 明确设置 `IsProgress=true` 并带进度键 | 是，同一键更新，不计自动到期 | 否，服务强制排除 | 完成、失败、取消或结果未知时按键移除，再发布结果 | `GlobalNotificationService.UpdateProgress` |
| 仅记录 | 发布者显式设置 `ShowNotification=false`、`RecordInHistory=true` | 否 | 是 | 用于不应重复打扰但需留记录的结果 | `EditorPageBase`、`MonitorPage`、`AgentInventorySynchronizer` |
| 自动本机采集开始 | Agent 报 Started，展示进度 | 是，进度卡 | 否 | 仅启动显示；晚连接补读与重复事件不当作新开始 | `AgentInventorySynchronizer` |
| 自动本机采集完成 | 新清单通过验证并应用后发布结果 | 是 | 是 | 读取既有历史不报新的成功 | `AgentInventorySynchronizer` |
| 自动本机采集失败／连接中断 | Agent 报失败或进度被连接故障打断 | 是，警告或错误 | 是 | 旧清单继续保留；恢复另按状态变化发布 | `AgentInventorySynchronizer` |
| 手动刷新本机／硬件 | 请求开始显示进度；请求有成功、失败、取消或未知终态时发布结果 | 进度及结果按阶段显示 | 仅终态 | 页面进入、对象选择、鼠标移动不发布 | `WorkspaceViewModel`、`HardwarePage` |
| 真实结构／分区操作准备与执行 | `SubmitRealAsync` 成功预留后，以同一 progress key 更新“准备准确计划、等待冻结计划确认、接受并执行、执行/核对、刷新相关对象”各阶段 | 一张同键进度卡持续更新，不自动到期、不计入消息历史；真实写入后图形区域显示遮罩与阶段，busy 门禁一直保持到写后刷新结束 | 否，进度被服务强制排除 | 正常终态或退出准备时移除该键；阶段卡消失不清除持久 OperationId 或 Unknown 屏障 | `EditorPageBase.SubmitRealAsync`、`WorkspaceViewModel`、结构/分区页 |
| 真实计划确认／取消 | Agent 返回冻结准确计划后，由所属流程显示确认对话框；用户确认才接受该计划 | 同键进度卡继续显示阶段；确认走对话框，不另发确认成功通知。真实清空至 RAW 直接使用该冻结计划确认，不额外先弹重复确认 | 否 | 取消不报成功；准备取消仍按准确 OperationId 核对，无法确认时保留操作安全屏障 | `EditorPageBase.SubmitRealAsync`、`DiskPartitionPage` |
| 真实操作终态 | Agent 状态达到确定完成、拒绝、失败或需核对时，发布结果通知；写入后执行相关对象刷新 | 终态按结果显示通知；同键进度卡随流程结束 | 是，仅记录终态 | 进度卡的结束、失败或 Unknown 都不能单独解除未核实写入屏障；需按持久 OperationId 对账 | `EditorPageBase.SubmitRealAsync`、`WorkspaceViewModel` |
| 模拟编辑提交 | 规则与事务提交产生确定成功后发布结果 | 是 | 是 | 表单输入、暂存选择、确认对话框取消不报成功 | `EditorPageBase`、分区/结构页 |
| 模拟编辑拒绝／失败 | 规则拒绝、提交失败或异常被处理为结果 | 按来源决定显示错误/警告；可能仅记录 | 是 | 不把失败说成成功，不留下部分模拟提交 | `EditorPageBase` |
| 模拟提交结果未知 | 传输中断而结果无法确认 | 警告 | 是 | 等待按 CommitId 对账，不按失败自动重试 | `EditorPageBase` |
| 格式化数据损失确认 | 已用数据分区准备格式化 | 模拟操作使用流程确认；真实操作以 Agent 返回的冻结准确计划确认 | 否 | 用户取消不提交、不报成功；真实准备取消按 OperationId 核对 | `DiskPartitionPage`、`EditorPageBase` |
| ReFS 证据提示 | 模拟格式化选择 ReFS 后进入提交流程 | 警告 | 是 | 仅选择控件不提交时不把操作报成功 | `DiskPartitionPage.PublishRefsNotice` |
| 导入／导出系统 | 文件读写与验证实际完成，或产生明确失败 | 是，成功/失败对应级别 | 是 | 只打开或取消文件选择器不报成功 | `MainPage`、`WorkspaceViewModel` |
| 模拟系统删除 | 用户确认且目录删除完成 | 是 | 是 | 取消确认不发布成功 | `MainPage.DeleteSimulationAsync` |
| 监控异常 | 全局观察器在 App 运行期间发现当前故障、采样缺口或归档失败首次进入新状态，无需打开监控页 | 是，警告或错误 | 是 | 同一异常的正常轮询不重复弹出；Monitor Off 时单独的历史 `PendingVerification` / `recovered_endpoint_unknown` 状态不再冒充当前异常卡片，历史 gap/endpoints 保留不变；其他活动和待核实告警仍按状态显示 | `MonitorAlertObserver`、`MonitorEditAlertPresentation`、`MonitorIssueStateTracker`、`MonitoringService` |
| 存储健康事件 | 全局观察器收到新的警告、错误或严重存储健康事件，无需打开监控页 | 是，警告或错误 | 是 | 同一事件标识只报一次 | `MonitorAlertObserver`、`MonitoringService` |
| 监控恢复 | 已记录异常状态实际恢复 | 视来源显示提示或仅记录 | 是 | 关闭故障卡片不代表恢复 | `MonitorPage` |
| 设置操作 | 数据根迁移、重置或工具配置产生确定结果/失败 | 按结果显示 | 是 | 仅打开设置页或选择器取消不报成功 | `SettingsPage` |

| 生命周期 | 当前值或规则 | 效果 |
| --- | --- | --- |
| 卡片尺寸 | 宽 384 DIP；无额外最小高度，内容最大高 200 DIP | 外层不保留空白高度；长短文字不强制同高，窗口高度不足时减少同时显示数 |
| 堆叠 | 服务最多 3 张活动卡；显示层按可用高度收窄 | 被挤出的卡退出后可从消息列表读到已完成详情 |
| 退出 | 向右平移 420 DIP、240 ms；动画关闭时立即完成 | 呈现动作不清除消息历史，不改变故障状态 |
| 消息寿命 | 本次 App 运行最多 200 条；消息历史在 App 进程内存中 | App 退出即清空；后台 Agent 仍运行不保留或恢复 App 内存消息；进度不会进入消息列表，真实操作状态仍按持久 OperationId 对账 |
| 文本长度 | 标题 256、正文与详情各 2048 字符，来源 128 | 卡片可显示更短的摘录；详情使用保留的完整字段 |
