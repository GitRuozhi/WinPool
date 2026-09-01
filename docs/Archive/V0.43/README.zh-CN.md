# V0.43

实施于 2026-09-01。

这是冻结的 V0.43 产品瘦身计划。完整 Release 门通过 350 项测试（0 failed、
0 skipped）、零警告 Release 构建、无已知易受攻击包，并验证两进程 staging 树
（`WinPool.App.exe` 与 `Agent\WinPool.Agent.exe`，含预期的 Windows App SDK 和
.NET self-contained 运行时组件）。IPC 协议 4 与 SQLite schema 14 在全新数据根上
得到确认：通过 `build/Reset-WinPoolLocalData.ps1` 重置本地数据库后，从 staging
树启动 App，欢迎页与管理页正常渲染真实本机存储清单，`schema_info` 记录版本 14，
且无任何退役表。

原生欢迎、管理与清单渲染已验证。继承的设备、UAC、DPI 与长期用例仍为
`unverified`。未创建 push、tag、Release、二进制上传或部署。
