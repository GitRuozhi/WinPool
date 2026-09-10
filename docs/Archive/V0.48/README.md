# V0.48 模拟语义、存储模型与提交链路

2026-09-10 用户确认带成员的建池/改池可用，并要求结束本阶段。活动 Plan 已从 `docs/Plan.md` 移入本目录冻结。其后活动阶段见仓库根 [docs/Plan.md](../../Plan.md)（V0.49，待批准）。

| 文件 | 角色 |
| --- | --- |
| [Plan.md](Plan.md) | 冻结的阶段计划、完成条件与验收记录 |

实现提交 `507fd42`。产品版本 V0.48。内部格式：SQLite schema 15、IPC 5、StorageSystemDocument 2、StorageSnapshot 3。真实存储结构修改继续拒绝；旧开发数据不兼容。

验证：

- Release 自动测试 460 通过；依赖审计无已知漏洞包。
- 用户确认带成员的建池与改池。
- WinUI 自动化覆盖两编辑页打开、草稿撤销/重做、扩缩容拒绝、双语、Manage 重载。
- 完整设备、DPI、高对比度、托盘与长期运行 **unverified**，未改写成 passed。
- 空池无成员直接应用会拒绝但无原因对话框且草稿被丢；分区页撤销/应用按钮空闲时仍亮。记入 CHANGELOG 已知限制，不另起 Plan。

本归档不授权 push、tag、Release、二进制上传或部署。
