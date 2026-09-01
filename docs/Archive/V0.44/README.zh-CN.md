# V0.44

[English](README.md) | [简体中文（仅供阅读）](README.zh-CN.md)

> 本文件仅为中文阅读副本；归档说明以无 `.zh-CN` 后缀的
> [README.md](README.md) 为准。

实施于 2026-09-01。

这是冻结的 V0.44 平台升级与发行瘦身计划。Windows App SDK 为 2.4.0。面向
Windows 的工程统一为 TFM `net10.0-windows10.0.26100.0`；钉死的 .NET SDK 以
NETSDK1140 拒绝 `net10.0-windows10.0.28000.0`。BuildTools 为
`10.0.28000.2705`。未使用的 Windows App SDK AI/ML/Search/Widgets 载荷已从
publish 排除。因 App 与 Agent 有 5 个同名桌面程序集内容不同，保留 nested
`Agent\WinPool.Agent.exe` 布局。正式 staging 不含 PDB。

Release portable staging 为 779 个文件、338.40 MiB，相对 V0.43 基线 853 个文件、
380.44 MiB。完整 Release 门通过 352 项测试（0 failed、0 skipped）、零警告
Release 构建、无已知易受攻击包。App 与 Agent 能从 staging 树启动。

继承的设备、UAC、DPI、Win10 22H2、Win11 24H2/25H2 与长期用例仍为
`unverified`。未创建 push、tag、Release、二进制上传或部署。

后加注释，2026-09-01：临时 App/Agent 运行时冲突分析稿已从 `docs/` 移入此处。来源
提交 `23ed240`。产品版本仍为 V0.44。该稿为历史输入，不构成当前要求。
