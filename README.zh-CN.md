# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool 是一款用于查看 Windows Storage Spaces 及其相关存储拓扑的原生 Windows 桌面程序。`V0.1` 是使用 C#、WinUI 3、.NET 9 和 Windows App SDK 构建的公开只读测试预览版。

## 当前功能

- 通过固定的只读 PowerShell 脚本扫描 Windows 存储清单。
- 关联存储子系统、存储池、存储层、物理磁盘、虚拟磁盘、分区和映射网络磁盘。
- 在上部操作工作区显示对象详情，在下部逻辑工作区显示完整的嵌套存储拓扑。
- 提供固定模拟系统，使没有复杂 Storage Spaces 配置的计算机也能查看完整界面。
- 支持中文、English、亮色/暗色/跟随系统主题、Windows 或预设主题色以及单实例运行。
- 在数据进入界面、剪贴板或默认导出前对磁盘序列号进行脱敏。

目前已经实现“管理”和“设置”标签页。“创建”“测试”“监控”和“开发”是后续里程碑的占位页。

## 安全边界

`V0.1` 不包含创建存储池、初始化磁盘、格式化、移除、扩容、修复或其他修改存储状态的操作。

每次普通启动固定进入“模拟”。“真实”开关目前只演示权限处理和界面状态，不会启用存储修改。库存提供程序只调用仓库内固定的只读脚本，不接受用户提供的 PowerShell 参数。

## 运行要求

- Windows 10 版本 1809 或更高版本，或 Windows 11
- x64 处理器和操作系统
- 普通只读使用不要求管理员权限

便携测试版为自包含发布，不需要另行安装 .NET 或 Windows App SDK。

## 便携测试版

从 [GitHub Releases](https://github.com/GitRuozhi/WinPool/releases) 下载 `WinPool_V0.1_Test_x64.7z`，解压其中的 `WinPool` 文件夹，然后运行 `WinPool.App.exe`。

用户设置保存在：

```text
%LOCALAPPDATA%\WinPool\settings.json
```

WinPool 不提供安装器或应用内更新。设置页只会使用系统浏览器打开 GitHub Releases 页面。

## 从源码构建

本仓库使用未打包、自包含的 x64 发布模型。

```powershell
dotnet test WinPool.slnx -c Release
dotnet build src\WinPool.App\WinPool.App.csproj -c Release -p:Platform=x64
dotnet publish src\WinPool.App\WinPool.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true
```

`global.json` 固定了 SDK。进行 XAML 开发时，建议使用安装了 Windows App SDK C# 工作负载的 Visual Studio。

## 仓库结构

```text
Docs/                               当前产品和工程文档
src/WinPool.App/                    WinUI 外壳、页面、控件和视图模型
src/WinPool.Core/                   领域模型、拓扑、选择和布局规则
src/WinPool.Infrastructure.Windows/ Windows 只读库存和本地服务
tests/                              核心与 Windows 基础设施测试
```

## 已知限制

- 这是测试预览版，不是生产级存储管理工具。
- 当前版本只支持 x64 便携发布。
- 网络磁盘扫描只反映当前 Windows 用户会话可见的映射。
- 硬件和 Storage Spaces 关联质量取决于 Windows 提供的信息。
- 尚未实现真实存储操作、Dite 集成、报告和监控。

## 研究背景

WinPool 来源于一项以实验证据为基础的 Storage Spaces 研究。当前经过测试的建议是 64K Interleave 配合 64K NTFS 分配单元。

阅读 [WinPool 分层存储研究与 V10 指南](https://github.com/GitRuozhi/WinPool-Tiered-Storage)。

## 反馈

请通过 [GitHub Issues](https://github.com/GitRuozhi/WinPool/issues) 提交可复现的问题和界面反馈。请勿公开未经脱敏的磁盘序列号或私有诊断资料。

## 权利声明

本仓库目前未授予任何许可，保留全部权利。
