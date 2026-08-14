# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

> 本文件仅为中文阅读副本；项目事实和控制规则以无 `.zh-CN` 后缀的
> [README.md](README.md) 为准。

WinPool 是面向 Windows 存储系统的第三方 WinUI 3 桌面应用，用于理解存储拓扑、执行测试与监控，
并以可审阅、可验证的方式规划操作。

## 当前版本

V0.41 是当前产品版本，在已完成的 V0.3 基础上进入视觉打磨和基础交互完善阶段。IPC 协议
保持为 3，Agent 持有的 SQLite 合同为 schema 13。

V0.41 是当前本地实施版本。当前可复现自动基线为 549 项 Release 测试通过、0 failed、
0 skipped、零警告 Release 构建，并且没有已知易受攻击依赖。原生 UI、托盘、UAC、设备、
外部工具和数据位置用例继续保持 `unverified`，不得将其写为通过。V0.39 保留为已 tag 和
发布的 V0.3 历史记录。

当前只实现无打包 Windows x64 便携式交付。MSIX 和 Microsoft Store 路线直接记录在
[产品方向](docs/Product.zh-CN.md)，不再单独建立安装文档。

当前 [V0.41 计划](docs/Archive/V0.41/Plan.md) 已经确认并实施完成；该授权不等于发布、部署、提交或推送。

进入 V0.4 前的 V0.39 架构治理已清除已确认的无用代码，并拆分过度集中的 Agent 和页面职责；
未新增产品功能、未改变 IPC/schema 合同，也未降低安全边界。该阶段现已冻结在
[V0.39 架构治理归档](docs/Archive/V0.39-architecture-hardening/README.zh-CN.md)。治理后的完整
自动门为 530 passed、0 failed、0 skipped；目标原生导航结果与仍为 `unverified` 的设备和副作用
用例分别记录。

## 安全边界

当前 V0.4 产品线未实现也未授权真实存储结构修改。在此产品线中，不得创建、初始化、格式化、调整、
修复或删除真实磁盘、分区、卷、存储池、存储层或虚拟磁盘。

V0.5 是首个可增加受控真实存储操作的计划阶段。开发期间，Agent 每次执行准确操作前必须立即获得开发者批准。产品中，用户在当前会话显式选择“本机真实修改”选项，即授权执行受控真实操作；仅有提权或 Real 模式不足够。模拟仍是默认路径。文件测试只能操作明确登记目录内、由本次运行登记的文件。DiskSpd、fio、Dite、RoboCopy 和 RAMMap 始终是单独安装的外部工具。

## 构建

WinPool 需要 Windows、PowerShell、`global.json` 固定的 SDK，以及由 .NET 恢复的 Windows App
SDK 依赖。

```powershell
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1
```

可复现的自包含 staging 命令见[开发文档中文阅读副本](docs/Development.zh-CN.md)。

## 文档

- [产品方向](docs/Product.zh-CN.md)：长期定位、产品边界和路线图。
- [开发文档](docs/Development.zh-CN.md)：架构、环境、构建、staging、版本和文档流程。
- [质量规则](docs/Quality.zh-CN.md)：自动门、原生集成门和人工验收门。
- [V0.41 计划](docs/Archive/V0.41/Plan.md)：已经实施完成的启动、欢迎、监控、持久化、托盘和基础交互计划。
- [V0.39 最终修正归档](docs/Archive/V0.39-final-correction/README.zh-CN.md)：
  已实施的 V0.3 最终最小修正及其证据。
- [V0.39 架构治理归档](docs/Archive/V0.39-architecture-hardening/README.zh-CN.md)：
  已完成的 V0.4 前清理与边界硬化阶段。
- [变更记录](docs/CHANGELOG.zh-CN.md)：已经实际发生的结果。
- [历史归档](docs/Archive/README.zh-CN.md)：冻结的已完成或已失效历史，包括已实施的
  V0.39 Plan。
- [参考资料](docs/Reference/AI-Agent-Harness-项目管理架构参考.md)：非权威项目管理参考。
- [V0.8–V0.9 技术债务参考](docs/Reference/V0.8-V0.9-技术债务参考.md)：
  已延期且不构成当前要求的技术观察。
- [Agent 规则](AGENTS.zh-CN.md)：操作、安全、授权和 Git 规则。

## 研究背景

在已经完成的 Windows 10 22H2 Storage Spaces 测试范围内，当前测试建议为：

```text
64K interleave + 64K NTFS 分配单元
```

Windows 11 尚未完成等价测试。

## 权利

本仓库未授予任何许可证，保留所有权利。
