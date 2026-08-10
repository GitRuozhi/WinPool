# WinPool

[English](README.md) | [简体中文](README_CN.md)

WinPool 是面向 Windows 存储系统的第三方 WinUI 3 桌面应用，用于理解存储拓扑、执行测试与监控，
并以可审阅、可验证的方式规划操作。

## 当前检查点

V0.31 是 V0.3 架构线当前的源码集成检查点，包含 App、Agent、TestWorker、Broker 四进程架构、
Agent 独占 SQLite、类型化命名管道 IPC、模拟编辑、本机只读发现、登记目录文件测试、监控和可复现
四进程 staging。

V0.31 不是二进制发布或 GitHub Release。只有完成原生 UI、托盘、UAC、设备、外部工具、生命周期
和数据位置验收矩阵，并由用户明确确认后，才进入 V0.32。

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

可复现的自包含 staging 命令见[开发文档](docs/Development.md)。

## 文档

- [产品方向](docs/Product.md)：长期定位、产品边界和路线图。
- [开发文档](docs/Development.md)：架构、环境、构建、staging、版本和文档流程。
- [质量规则](docs/Quality.md)：自动门、原生集成门和人工验收门。
- [当前计划](docs/Plan.md)：唯一活动的 V0.3 阶段及其证据。
- [变更记录](docs/CHANGELOG.md)：已经实际发生的结果。
- [历史归档](docs/Archive/README.md)：冻结的已完成或已失效历史。
- [参考资料](docs/Reference/AI-Agent-Harness-项目管理架构参考.md)：非权威项目管理参考。
- [Agent 规则](AGENTS.md)：操作、安全、授权和 Git 规则。

## 研究背景

在已经完成的 Windows 10 22H2 Storage Spaces 测试范围内，当前测试建议为：

```text
64K interleave + 64K NTFS 分配单元
```

Windows 11 尚未完成等价测试。

## 权利

本仓库未授予任何许可证，保留所有权利。
