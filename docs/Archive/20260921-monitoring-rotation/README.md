# 监控轮换、7z 归档与状态提示阶段

状态：已完成实施与本阶段有限验收；2026-09-21 归档。产品版本保持 V0.53，执行基线 `92047b9`，实现提交 `fe2faf4`。用户确认的范围与场景保存在 [Plan](Plan.md)，现行规则归 [Product](../../Product.md)、[Development](../../Development.md) 和 [Quality](../../Quality.md)。本归档不激活任何 Design 储备方案。

## 实现结果

- 新监控数据使用独立 `monitoring.db`，schema 1；核心 schema 17 及旧监控记录保留。IPC 11 要求 App、Agent 配套运行。
- 主库和 WAL 达到 1 GiB 后，排空、checkpoint、关闭旧库并封存，再创建固定名称新库；有界内存承接切换期间采样。已确认漏记与未知异常结束缺口分别报告。
- 后台串行压缩，完整性、数据库与清单哈希验证后才释放原库；故障保留数据并可恢复。历史不自动删除，不提供历史读取；CSV 只覆盖当前活动库。
- 随附独立 x64 7-Zip Extra 26.03，采用最小调用器和适配器。设置只有一行 7Z 工具配置，支持自定义路径与恢复默认，移除容量上限；不安装、更新、搜索或预检工具能力。
- 正常仅显示运行时长或已停止，异常逐项单行、可关闭，新失败重新显示；修复非就绪快照误报停止、同速率页面重入重启会话和启动异常端点清理。

## 验证证据

以下路径相对于 WinPool 仓库根，均为本地证据，不提交数据库、运行树、截图或测试输出。

| 范围 | 结果与位置 |
| --- | --- |
| 最终自动门 | `artifacts/test-results/20260921-full-gate-runtime-closeout-final/`：11 份 TRX，655 passed、0 failed、3 个门控测量 NotExecuted；Release 构建 0 警告/0 错误，22 项目依赖审计无已知漏洞包。恢复、测试、构建及依赖审计分别保存日志 |
| 生产 1 GiB 全链路 | `artifacts/test-results/20260921-production-1gib-rotation/production-1gib-rotation-concurrent-active.*`：封存 2,810,001 条、1,102,262,272 B，归档 56,429,191 B；压缩释放前新固定库已经落入另一标记，跨库逐值核对，最后释放原库 |
| 压缩参数 | `artifacts/test-results/20260921-7z-parameter-comparison/7z-parameter-comparison-after-checkpoint.*`：同一约 123 MiB 合成库比较两组参数；选择级别 9、LZMA2、64 MiB 字典、2 线程，压缩效果近似而峰值内存更低。生产大库链路测得峰值约 686 MiB，不将字典大小当作进程总内存上限 |
| 故障与生命周期 | `artifacts/test-results/20260921-closeout-persistence/` 最终持久化 123 passed、3 门控测量跳过，监控 26 passed；包含生产迁移器的双库及归档往返、恢复诊断、取消和文件句柄释放、25 小时单调时长。先前写入、轮换、WAL、归档恢复专项位置见 Plan |
| 原生运行与退出 | `artifacts/native-validation/monitoring-ui-20260921-runtime-closeout-r2/`：停止/运行、页面往返同 session，时长 00:00:06→00:01:26；托盘正常退出后 App/Agent 均结束、会话关闭、`shutdown_clean=1`、端点无残留 |
| 原生失败恢复与设置 | `artifacts/native-validation/monitoring-ui-20260921-runtime-closeout-r3-archive/`：失败保留 raw、提示关闭、新失败再现、恢复默认 7Z 后 `SourceReleased` 且提示隐藏；外部工具卡只有 7Z 一行 |
| 中英文窄窗 | `artifacts/native-validation/monitoring-ui-20260921-runtime-closeout-r4-narrow-issues/`：主代理逐图核对两种语言异常独立单行、截断和关闭按钮 |

主代理核对 TRX、关键断言、真实 ledger 与有效截图。全黑截图及只截到标题栏的图片不作为视觉通过证据。早期原生失败记录仍保留，不用后续成功重写历史；此前“启动死锁就是现场根因”的推断已撤回。

## 验证边界

双库迁移采用真实迁移器、SQLite 与归档协调器，但 quiesce 为接口级替身；原生托盘正常退出有独立证据，不能把两者合称原生数据位置切换。完整主题/DPI/平台矩阵、强制断联未知状态注入、本轮 UAC 全流程、同 SID 多 Windows 会话均未补跑，保持 `unverified`。不执行真实存储结构写操作，不发布或推送。

三个测量测试在常规全量门中显式跳过，已有独立实测，不计入 655 个通过测试，也不以跳过替代生产阈值验证。普通内存排队不是数据丢失；异常结束的持久化 evidence sentinel 不作为 UI 精确丢失数量。
