# V0.55 通知、上下文帮助与轻量事件记录

状态：已实施并收口，2026-09-22 归档。激活日期：2026-09-21。版本由 V0.54 增加至 V0.55；不推送。实际验证与限制见[阶段记录](README.md)。

基线：`d304942`，激活前工作树干净，无其他活动 Plan。完整范围采用[信息系统方案](../../Design/WinPool-Information-System-Plan.md)，其中历史的“仅编制”授权由本次执行授权替代。已完成的统一对象与监控轮换阶段不重开。

## 固定范围

- 开发页展示可复制的实际 Diagnostics 路径，不读取日志内容；本次运行消息最多 200 条，仅内存，提供清空、复制全部和选中详情。
- 统一现有通知的生命周期、去重、限量与恢复语义；重要错误保持可见，短暂消息悬停/焦点期间暂停计时；关闭开发者模式时用户必要反馈仍完整可用。
- 保留自动/手动采集独立进度、监控现有异常状态及真实缺口；不新增事件数据库、历史监控读取或真实存储操作。
- 补齐主要页面按钮、参数和禁用原因，协调同窗口对话框串行显示；不改变业务合法性或容量规则。
- 中英文和开发者导航门继续适用，唯一版本源为 Directory.Build.props。

## 文件所有权与工作包

主代理亲自负责计划、版本、产品/开发/质量文档、最终代码审查与归档提交。三个 Terra-Max 子代理不得派生代理、提交或推送，不修改文档或版本。

| 工作包 | 独占文件范围 | 状态 |
| --- | --- | --- |
| A 通知核心与采集接线 | Application 通知契约/服务/自动通知；App 的 ApplicationNotificationPresenter、AgentInventorySynchronizer、WorkspaceViewModel；新增核心测试 | 已实现，相关行为回归通过 |
| B 主窗口与开发页、统一验证 | MainWindow.xaml/.cs、DevelopmentPage.xaml/.cs、NotificationSeverityConverter；架构断言；新增通知展示控件 | 已实现，构建与有限原生验证通过 |
| C 页面反馈与帮助 | MainPage、HardwarePage、StorageStructurePage、DiskPartitionPage、EditorPageBase、MonitorPage、SettingsPage、PropertyTableContextMenu；MonitoringService 仅增加跨页异常跟踪；新增 ContextHelp、DialogCoordinator 等小型帮助类 | 已实现，行为测试与构建通过，原生范围见阶段记录 |
| 主代理统筹与验收 | 文档、版本、集成审查、归档与本地提交 | 已审阅并归档 |

共享契约由 A 尽早发送 B/C；保留现有 Publish 方法源码兼容，通过可选参数增加策略。B/C 不修改核心契约。各自文案可用既有 LocalizationService.IsChinese 就近选择，避免多人编辑同一字典。跨所有权改动先协调。

## 验证责任

B 是唯一构建、测试和原生桌面验证执行方；A/C 完成代码与直接回归用例后由 B 统一运行。API/样例查询与原生验证优先 WinApp CLI。共享构建输出串行使用，隔离必须覆盖输出与合并参数，遵守 Development。

运行相关 Application/Architecture 回归与完整 Release 构建；仅在新失败或实际影响需要时扩展。覆盖第 201 条消息、长文本有界、去重、关闭/清空/恢复分离、进度不污染历史、重要错误不超时、交互暂停计时、超量通知可见入口，以及开发模式、日志路径、复制、主要帮助和模拟/本机区分。原生无法验证的场景明确 unverified。主代理审阅代码、输出及截图，不重复运行未受后续修改影响的检查。

## 收口

限定 Application 回归 60 passed、0 failed、0 skipped，Architecture 47/47 passed，完整 Release 构建 0 警告/0 错误；有限原生关键路径已执行并恢复偏好、关闭测试实例。主代理完成代码与证据审阅。全部证据、开发页滚动布局限制及原生未验证项见阶段记录，不将服务测试当作完整 UI 验收。本 Plan 已退出活动入口，只本地提交本任务文件。
