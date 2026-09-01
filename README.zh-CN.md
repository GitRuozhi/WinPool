# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

> 本文件仅为中文阅读副本；项目事实和控制规则以无 `.zh-CN` 后缀的
> [README.md](README.md) 为准。

WinPool 是面向 Windows 存储系统的第三方 WinUI 3 桌面应用，用于理解存储拓扑、监控和管理存储，
并以可审阅、可验证的方式规划操作。

当前产品版本为 **V0.43**。版本源是 `Directory.Build.props`。最终结果见
[变更记录](docs/CHANGELOG.zh-CN.md)。

## 能力

WinPool 提供存储拓扑、聚焦的操作工作区、模拟编辑和监控。当前交付为无打包、自包含的
Windows x64 便携式应用。

真实存储结构修改尚未开放。WinPool 1.x 的“测试”和“开发”标签页只保留简单路线说明；完整工作区
计划在 WinPool 2.0 推出。磁盘测试、外部工具和开发/AI 诊断子系统已从 1.x 运行时移除，推迟到
1.x/2.0。产品边界见[产品方向](docs/Product.zh-CN.md)。

## 构建

WinPool 需要 Windows、PowerShell、`global.json` 固定的 SDK，以及由 .NET 恢复的 Windows App
SDK 依赖。

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\build\Rebuild-WinPool.ps1
```

该命令会清理可再生成的本地输出、重新编译，并生成 `WinPool.lnk` 快捷方式。本地运行目录是 `artifacts\$(Configuration)\`。Staging 与进程布局见[开发文档](docs/Development.zh-CN.md)。

## 文档

- [产品方向](docs/Product.zh-CN.md)：长期定位、产品边界和路线图。
- [开发文档](docs/Development.zh-CN.md)：架构、环境、构建、staging 和版本规则。
- [质量规则](docs/Quality.zh-CN.md)：测试与验收规则。
- [变更记录](docs/CHANGELOG.zh-CN.md)：重要最终结果。
- [历史归档](docs/Archive/README.zh-CN.md)：冻结的历史计划和状态。
- [Agent 规则](AGENTS.zh-CN.md)：操作、安全、Git 和发布规则。

`docs/Reference` 下的参考资料不是当前项目要求。

## 研究背景

在已经完成的 Windows 10 22H2 Storage Spaces 测试范围内，当前测试建议为：

```text
64K interleave + 64K NTFS 分配单元
```

Windows 11 尚未完成等价测试。

## 权利

本仓库未授予任何许可证，保留所有权利。
