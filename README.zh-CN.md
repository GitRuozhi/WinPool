# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

> 本文件仅为中文阅读副本；项目事实和控制规则以无 `.zh-CN` 后缀的
> [README.md](README.md) 为准。

WinPool 是面向 Windows 存储系统的第三方 WinUI 3 桌面应用，用于理解存储拓扑、执行测试与监控，
并以可审阅、可验证的方式规划操作。

## 当前版本

V0.37 是当前本地实施版本。它保留 App、Agent、TestWorker、Broker 四进程架构，并收口 UI 事件重入、
未观察任务异常、关键 async void 生命周期、无效分区输入、未确认的模拟池创建、事件流恢复、
单实例重定向挂起和 RoboCopy 输出解析。IPC 协议保持为 3，Agent 持有的 SQLite 合同保持为 schema 12。

V0.37 的 Release 自动门、依赖审计和构建结果记录在变更记录中。原生 UI、托盘、UAC、
设备、外部工具和数据位置用例继续保持 `unverified`，不得将其写为通过。用户本次只授权本地 Git
提交，不授权推送、tag、二进制上传、GitHub Release 或部署。

## 安全边界

当前 V0.3 产品线未实现也未授权真实存储结构修改。在此产品线中，不得创建、初始化、格式化、调整、
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
- [变更记录](docs/CHANGELOG.zh-CN.md)：已经实际发生的结果。
- [历史归档](docs/Archive/README.zh-CN.md)：冻结的已完成或已失效历史，包括已实施的
  V0.37 Plan。
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
