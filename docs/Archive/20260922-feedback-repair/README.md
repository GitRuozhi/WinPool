# V0.55 人工反馈修复

执行基线 `b64382b`，产品版本保持 V0.55。实现及有限验证收口，[阶段 Plan](Plan.md)保留分工及验收边界；不表示全量原生或设备验收。

## 实现

通知恢复简洁卡片：普通 8 秒、错误 20 秒自动消失，独立重复消息分别记录；普通卡点击消失，错误卡点击打开消息对话框，完整详情留开发页。移除业务横幅，同一持续监控异常只在状态变化时通知。保持三张活动卡和 200 条内存历史，不新增日志查看器或审计数据库。

灰色按钮由透明悬停容器承接禁用原因，常用可用按钮增加用途说明。自动创建虚拟磁盘／分区保留结构页原位置，保存全局偏好，不随对象选择变化，不反向增删结构。模拟导入导出采用现代文件选择器；允许删除内置模拟，首次播种标记避免启动时恢复用户已删除样例；先持久化再更新内存。系统分区删除和格式化保护保留，允许的非破坏性编辑不再整体禁用。

三个子代理按文件分工，主代理负责计划、文档、跨模块协调与代码验收；一个子代理集中执行构建、测试和原生操作。未修改真实存储、未替换原运行树、未推送。

## 问题来源

- 导入导出原先采用 `Windows.Storage.Pickers`，提升模式不受支持；改用已有项目版本支持的 `Microsoft.Windows.Storage.Pickers`。本机旧日志没有该导入异常记录，不能声称从日志取得 picker 错误码。
- `d6076a9`（2026-09-11）引入过宽的系统/启动分区编辑限制；初版 V0.55 的外部灰字不是全部限制的起点。
- 内置模拟原先在投影禁止删除，启动还会补回缺失样例；删除行为与播种策略一并修复。

## 自动证据

本地证据不提交 Git，位于 `artifacts/v055-feedback-repair-20260922-r1/test-results`：

| 范围 | 首轮 | 修复复测 |
| --- | --- | --- |
| Application | 265 项，264 passed、1 failed | 新测试夹具误用 `Single()`，修正精确目标后，受影响测试及通知测试 9/9 passed |
| Infrastructure | 86/86 passed | 无须重跑 |
| Architecture | 47 项，46 passed、1 failed | MainPage 改名恢复读取应用投影，原架构守卫 1/1 passed，未放宽断言 |

主代理读取 TRX 并核对代码，不重复运行已通过测试。没有运行全套解决方案测试。xUnit2031 断言风格警告已修正。

完整隔离 Release 构建最终为 r4：exit 0、0 警告、0 错误，App/Agent 合并 288 个共享文件、294 个 App 独有文件、10 个 Agent 独有文件、0 冲突。运行树为 `artifacts/v055-feedback-repair-20260922-r4/Release`。r1 的密封 Border 继承错误、r2 的开发页缺失命名空间引用均已修复；r3 未得到最终退出状态，保持 unverified。各轮产物原位保留。

执行命令为 `dotnet build WinPool.slnx -c Release --no-restore -m:1`，通过 MSBuild 属性将 `WinPoolLocalTreeRoot` 指向 r4 的 `trees/Release/`，并覆盖完整 `WinPoolMergeRuntimeCommand`：调用仓库 `build/Merge-RuntimeTrees.ps1`，AppDir、AgentDir 分别为该目录的 App、Agent，Destination 为 r4 的 Release，使用 `-ReplaceDestination`；执行前确认该最终目标不存在。默认逐项目 BaseOutputPath、BaseIntermediateOutputPath 不覆盖。执行方取得最终退出码 0 和汇总，耗时 32.82 秒；不是仅凭产物存在判断通过。

## 原生证据与限制

使用 WinApp CLI 0.6.1 和真实鼠标输入，截图采用已验证的 `--capture-screen`；默认 WGC 黑帧不作为证据。主代理实际查看画面，不根据截图文件名判断通过。证据位于 r4 的 `evidence` 目录。

已核对普通进程导入选择器、错误卡外观及详情提示、错误卡真实点击消息对话框、已有模拟只读导出→导入新副本→仅删除新副本闭环。导出的 `native-data/winpool-native-export.winpool` 保留，可重新导入恢复测试副本；没有删除既有模拟系统。

全局开关原生链：`[模拟] DESKTOP-PL96UKD` 两项开 → 关闭两项 → 切换到 `[模拟] 空池待建虚拟磁盘` 仍为关 → 恢复两项开。主代理查看 `structure-global-switches-initial.png`、`structure-global-switches-off-after-system-change.png` 和 `structure-global-switches-restored.png`，核对了系统名称和状态；没有应用结构变更。

早期 `error-card-message-dialog*.png` 没有实际对话框，弃作该项证据；有效图为 `error-card-after-native-click-rapid.png`。普通卡前后图相隔 23 秒，超过 8 秒寿命，不能据此认定点击关闭，该原生交互保持 unverified，不能用服务测试代替。

动态可用按钮“修改卷标”真实悬停用途可见：`tooltip-dynamic-enabled-rename-volume.png`，主代理已查看。该图同时显示模拟系统分区的“修改卷标”“编辑分区”可用，而格式化和删除禁用。禁用按钮悬停因 `foreground_not_target` 和窗口遮挡未取得有效证据；`disabled-structure-undo-tooltip.png` 不算通过。不反复抢焦点或重试，保持 unverified。

管理员模式选择器、普通卡点击消失、禁用按钮悬停、重启持久偏好及删除内置后重启、完整主题/DPI矩阵均没有本轮原生通过证据；相关源码与自动测试不替代这些实测。真实设备写操作未执行。

## 退出与恢复

最终持久偏好已核对为 Theme=System、Accent=System、Language=SystemDefault、DeveloperMode=true、AutoCreateVirtualDisk=true、AutoCreatePartition=true、LastActivePage=Development，活动系统为 `[模拟] 空池待建虚拟磁盘`。App 正常关闭；残留 Agent 核实为 r4/Release/WinPool.Agent.exe 后停止，主代理另查 WinPool 进程为空。没有迁移或清理用户数据根，也没有覆盖常规 `artifacts/Release`。
