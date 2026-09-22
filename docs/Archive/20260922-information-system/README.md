# V0.55 通知与轻量运行消息

2026-09-21 激活，2026-09-22 收口。基线 `d304942`，产品 V0.54 → V0.55。[阶段 Plan](Plan.md)保留执行分工，[采用方案](../../Design/WinPool-Information-System-Plan.md)保留设计来源；当前要求以 Product/Development/Quality 为准。

## 实现与审阅

复用通知服务，活动通知与本次运行 History 各有 200 条上限，前三张卡及非开发者超量入口；文本有界、重复合并、重要问题常驻、交互暂停、关闭/清空/恢复分离。采集进度独立且不进历史；事件缺口不冒充成功。开发页仅显示实际 Diagnostics 路径和内存消息，不读日志、不增加数据库。主要页面补充帮助、禁用原因、目标标识及同窗口对话框协调。

三个 Terra-Max 子代理按文件分工，主代理负责计划、文档、版本与代码验收；一个子代理统一执行构建/测试/桌面验证。主代理审查后修正了常驻警告容量保护、长键关闭恢复、焦点保留、目标误标、静默失败和虚假恢复等问题。未推送、未部署替换原运行目录、未修改真实存储。

## 验证证据

全部命令在 WinPool 根目录执行。恢复和构建保留默认逐项目 `artifacts/build`、`artifacts/obj`；隔离覆盖 `WinPoolLocalOutputRoot`、`WinPoolLocalTreeRoot`、`WinPoolMergeRuntimeScript` 和完整 `WinPoolMergeRuntimeCommand`，包括新建的合并目标。

- Application：`dotnet test tests/WinPool.Application.Tests/WinPool.Application.Tests.csproj -c Release --no-restore --maxcpucount:1 -m:1 --filter "FullyQualifiedName~GlobalNotificationServiceTests|FullyQualifiedName~AutomaticInventoryNotificationTests|FullyQualifiedName~ApplicationBehaviorTests|FullyQualifiedName~MonitorIssueStateTrackerTests"`；60 passed，0 failed，0 skipped。一条 xUnit2031 测试风格警告保留。
- Architecture：`dotnet test tests/WinPool.Architecture.Tests/WinPool.Architecture.Tests.csproj -c Release --no-restore --maxcpucount:1 -m:1`；47/47 passed。
- 完整构建：`dotnet build WinPool.slnx -c Release --no-restore -m:1`；退出码 0，0 警告/0 错误，App/Agent 合并 0 冲突。App/Agent 产品版本均为 V0.55。

本地证据（不提交生成物）：

- `artifacts/v055-notification-ui-application-20260921-r3/TestResults/ApplicationNotificationSubset.trx`
- `artifacts/v055-notification-ui-architecture-20260921-r2/TestResults/ArchitectureBoundary.trx`
- `artifacts/v055-notification-ui-release-20260921-r1/release-build.binlog`
- 同一 Release 根的 `Release/` 为隔离运行树，`UiEvidence/` 为截图。

早期失败分别为隔离参数中的项目名字面路径、模式变量遮蔽、两条过时架构断言，均已修正；失败证据保留，未算作通过。主代理读取 TRX、核对实际版本并查看截图，没有重复运行测试。

## 原生范围与限制

通过：Diagnostics 路径复制、所选消息完整复制、清空、开发者导航门切换、中英文及恢复、深色系统主题、上下文帮助、900×900 窄窗。使用标准数据根，无迁移清理；测试 App 正常退出，隔离 Agent 经路径核实后停止。偏好恢复为 Theme=System、Language=SystemDefault、DeveloperMode=true、LastActivePage=Settings。

有效截图采用 `--capture-screen`：`development-zh-screen.png`、`settings-zh-developer-on-screen.png`、`settings-context-help-theme-screen.png`、`settings-en-screen.png`、`settings-zh-narrow-screen.png`。WinApp CLI 0.6.1 默认 WGC 连续空帧，第五帧仍空却返回成功；`development-zh.png` 和 `diagnosis-window-verbose.png` 不作为视觉通过证据。同 HWND 的屏幕截图正常，窗口可见、未最小化且无防截图标记；未确定更底层 WGC 空帧原因，未修改驱动或工具。后续先核对本机 CLI 版本及 help，使用已验证截图方式。

保留限制：开发页“复制全部/清空消息”需滚动上方区域才能看到；超量/常驻错误/悬停与焦点计时未做原生故障注入，服务行为测试不能替代 UI 交互证据；完整 DPI/高对比度、重启后清空及切换数据根均为 `unverified`。未运行全套回归或真实设备写操作验收。
