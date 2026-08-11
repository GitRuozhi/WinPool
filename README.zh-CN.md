# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

> 本文件仅为中文阅读副本；项目事实和控制规则以无 `.zh-CN` 后缀的
> [README.md](README.md) 为准。

WinPool 是面向 Windows 存储系统的第三方 WinUI 3 桌面应用，用于理解存储拓扑、执行测试与监控，
并以可审阅、可验证的方式规划操作。

## 当前版本

V0.34 是用户确认的当前版本。它保留 App、Agent、TestWorker、Broker 四进程架构，并完成 V0.33
之后发现的 Local identity、存储边界、进程生命周期、事件恢复和工具流缺陷收口。IPC 协议为 3，
Agent 持有的 SQLite 合同为 schema 12。

用户于 2026-08-11 在 494 项 Release 自动测试、零警告 Release 构建、依赖审计和可复现四进程
staging 通过后确认 V0.34。原生 UI、托盘、UAC、设备、外部工具、数据位置和 V0.34 M01--M07
用例继续保持 `unverified`；确认版本不代表伪造这些结果。该确认授权对应的本地 checkpoint、
`main` 推送和本机 portable 部署，但不授权 tag、二进制上传或 GitHub Release。

## 安全边界

WinPool 未实现也未授权真实存储结构修改。不得创建、初始化、格式化、调整、修复或删除真实磁盘、
分区、卷、存储池、存储层或虚拟磁盘。

存储结构编辑只支持模拟系统。文件测试只能操作明确登记目录内、由本次运行登记的文件。DiskSpd、
fio、Dite、RoboCopy 和 RAMMap 始终是单独安装的外部工具。

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
- [变更记录](docs/CHANGELOG.zh-CN.md)：已经实际发生的结果。
- [历史归档](docs/Archive/README.zh-CN.md)：冻结的已完成或已失效历史，包括已验收的
  V0.34 Plan。
- [参考资料](docs/Reference/AI-Agent-Harness-项目管理架构参考.md)：非权威项目管理参考。
- [Agent 规则](AGENTS.zh-CN.md)：操作、安全、授权和 Git 规则。

## 研究背景

在已经完成的 Windows 10 22H2 Storage Spaces 测试范围内，当前测试建议为：

```text
64K interleave + 64K NTFS 分配单元
```

Windows 11 尚未完成等价测试。

## 权利

本仓库未授予任何许可证，保留所有权利。
