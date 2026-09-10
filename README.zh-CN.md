# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool 是 Windows 存储系统桌面应用，用于查看存储拓扑、监控设备和编辑模拟存储系统。

当前实现版本为 **V0.47**。V0.48 是已规划的模型和模拟系统重构，相关修复尚未实施。真实存储结构修改尚未开放。

## 当前可用功能

- 通过只读采集查看本机存储，检查池、层、磁盘、分区及相关信息。
- 在存储结构编辑和磁盘分区编辑两页修改模拟系统。
- 监控受支持设备，设置软件前台与后台偏好。
- 使用中英文界面、主题与键盘导航。

当前编辑器仍有提交、容量估算和对象关联方面的已知问题。模拟结果不能证明 Windows 能够执行该配置，详见[当前限制与变更记录](docs/CHANGELOG.md)。

整个 1.x 的“测试”“开发”标签只显示路线说明；完整工作区计划在 2.0 推出。

## 系统要求与运行

最低受支持系统为 **Windows 10 22H2 x64**，主要平台为 Windows 11 24H2、25H2 x64。具体存储功能取决于 Windows 版本/版本类型及存储提供程序。

当前交付为无打包、自包含的 x64 便携目录。保留完整目录并运行 `WinPool.App.exe`，配套 Agent 在用户托盘中运行。数据默认存放于 `%LocalAppData%\WinPool`，也支持显式选择程序旁可写的 `Data` 目录。替换程序文件前退出 WinPool。

目前没有已发布的 MSIX 安装包或 Microsoft Store 页面。

## 从源码构建

在 Windows、PowerShell 和 `global.json` 指定的 SDK 环境中运行：

```powershell
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1
.\artifacts\Release\WinPool.App.exe
```

构建前确认已有 WinPool 进程未占用输出目录。开发入口为 [AGENTS](AGENTS.md)，内部开发文档统一使用中文；构建和数据所有权详见[开发约定](docs/Development.md)。

## 研究背景

```text
64K interleave + 64K NTFS cluster = current tested recommendation.
Windows 11 has not yet received equivalent testing because current storage hardware prices and the author's practical budget do not allow a second full test platform.
```

即：64K 交错配合 64K NTFS 簇是当前测试建议。受现有存储硬件价格及作者实际预算限制，无法配置第二套完整测试平台，Windows 11 尚未获得等价测试。这些研究结果不代表全部 Windows 配置都受支持或具有同等可靠性证据。

## 权利

本仓库未授予任何许可证，保留所有权利。
